using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.Projects;
using CodeyBox.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.Plugins;

/// <summary>
/// UAT coverage for <c>Auditor plugin SDK - Allows external auditors to join project audit composition</c>.
/// Plan anchor: docs/uat/00-plan.md#plugins
/// </summary>
[Collection("Pipeline integration")]
public sealed class AuditorPluginSdkUatTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-plugin-auditors-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task ProjectSelectedPluginAuditor_PersistsFindingLikeBuiltInAuditor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var plugin = new FindingPluginAuditor();
        using var pipeline = PluginsUatHelpers.BuildPluginAuditPipeline(
            _workspace,
            seed,
            plugin,
            new ProjectAudit
            {
                MaxIterations = 1,
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "uat.finding-auditor",
                    },
                ],
            },
            reports);
        pipeline.Agent.WorkPlan.Enqueue(new FileWrite("PluginTarget.cs", "public sealed class PluginTarget {}\n"));
        var item = PluginsUatHelpers.NewItem("feature/plugin-auditor-finding");
        await pipeline.Store.CreateAsync(item);

        await pipeline.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await pipeline.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        var report = Assert.Single(reports.Reports);
        Assert.Equal("uat:plugin-finding", report.AuditorName);
        Assert.Equal("tool", report.AuditorKind);
        Assert.Equal("Error", report.WorstSeverity);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("Plugin auditor finding", finding.Title);
        Assert.Equal(["PluginTarget.cs"], finding.Files);
        Assert.Equal([7], finding.LineHints);
    }

    [Fact]
    public void ProjectSelection_IncludesPluginAuditorWithDeclaredCapabilities()
    {
        var plugin = new CredentialedPluginAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [plugin],
            NullLogger<ProjectAuditorComposer>.Instance);
        var project = ProjectWithCustomPlugin("uat.credentialed-auditor");

        var auditors = composer.Compose(project, new FakeAgent());

        var auditor = Assert.Single(auditors);
        Assert.Same(plugin, auditor);
        Assert.True(auditor.Required.HasFlag(AuditCapabilities.AgentCredentials));
        Assert.True(auditor.Required.HasFlag(AuditCapabilities.Network));
    }

    [Fact]
    public void MissingPluginReference_LogsWarningAndLeavesOtherAuditorsSelectable()
    {
        var logger = new CapturingLogger<ProjectAuditorComposer>();
        var present = new PassingPluginAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [present],
            logger);
        var project = new Project
        {
            Id = new ProjectId("plugin-uat-project"),
            DisplayName = "Plugin UAT Project",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "missing.plugin",
                    },
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "uat.passing-auditor",
                    },
                ],
            },
        };

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Single(auditors);
        Assert.Same(present, auditors[0]);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("missing.plugin", StringComparison.Ordinal));
    }

    private static Project ProjectWithCustomPlugin(string pluginId) => new()
    {
        Id = new ProjectId("plugin-uat-project"),
        DisplayName = "Plugin UAT Project",
        RepositoryUrl = "https://example.invalid/repo.git",
        Audit = new ProjectAudit
        {
            Custom =
            [
                new CustomAuditorDescriptor
                {
                    Kind = "plugin",
                    PluginId = pluginId,
                },
            ],
        },
    };

    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }
}

[CodeyBoxPlugin("uat.finding-auditor", "UAT Finding Auditor")]
internal sealed class FindingPluginAuditor : IAuditor
{
    public string Name => "uat:plugin-finding";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(
            false,
            [
                new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Error,
                    Title: "Plugin auditor finding",
                    Description: "Finding emitted by an allowlisted plugin auditor.",
                    Location: "PluginTarget.cs:7"),
            ]));
}

[CodeyBoxPlugin("uat.credentialed-auditor", "UAT Credentialed Auditor")]
internal sealed class CredentialedPluginAuditor : IAuditor
{
    public string Name => "uat:credentialed";
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials | AuditCapabilities.Network;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}

[CodeyBoxPlugin("uat.passing-auditor", "UAT Passing Auditor")]
internal sealed class PassingPluginAuditor : IAuditor
{
    public string Name => "uat:passing";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
        => Task.FromResult(new AuditResult(true, []));
}
