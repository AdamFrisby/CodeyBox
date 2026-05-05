using System.Reflection;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the full path from project config → composer → plugin auditor invocation.
/// A project that declares <c>Custom: [{Kind: "plugin", PluginId: "..."}]</c> must
/// include the plugin auditor in its composed run; findings produced by the plugin
/// must flow through normally.
/// </summary>
public sealed class AuditorPluginInProjectAuditTests
{
    // ── Fake helpers ──────────────────────────────────────────────────────────

    private sealed class FakeAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(ISandbox _, string __, string ___, AgentCredential? ____, string? _____ = null, string? ______ = null, CancellationToken _______ = default, Action<string>? ________ = null)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }

    // In-process plugin: decorates with [CodeyBoxPlugin] so the composer index
    // can find it by ID, just as it would with a real loaded plugin assembly.
    [CodeyBoxPlugin(
        id: "test.inline-plugin",
        displayName: "Inline Test Plugin",
        minHostApiVersion: "1.0")]
    private sealed class InlinePluginAuditor : IAuditor
    {
        public string Name => "test:inline-plugin";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(Passed: true, Findings: []));
    }

    [CodeyBoxPlugin(
        id: "test.finding-plugin",
        displayName: "Inline Finding Plugin",
        minHostApiVersion: "1.0")]
    private sealed class FindingPluginAuditor : IAuditor
    {
        public string Name => "test:finding-plugin";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(
                Passed: false,
                Findings: [new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Error,
                    Title: "Test finding",
                    Description: "A finding from the inline plugin")]));
    }

    private static Project MakeProject(params CustomAuditorDescriptor[] custom) => new()
    {
        Id = new ProjectId("test-plugin-project"),
        DisplayName = "Test Plugin Project",
        RepositoryUrl = "https://example.com/test.git",
        Audit = new ProjectAudit { Custom = custom },
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PluginAuditor_IsIncluded_WhenProjectConfiguresIt()
    {
        var plugin = new InlinePluginAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [plugin],
            NullLogger<ProjectAuditorComposer>.Instance);

        var project = MakeProject(
            new CustomAuditorDescriptor { Kind = "plugin", PluginId = "test.inline-plugin" });

        var auditors = composer.Compose(project, new FakeAgent());

        Assert.Single(auditors);
        Assert.Same(plugin, auditors[0]);
    }

    [Fact]
    public async Task PluginAuditorFindings_FlowThrough_Normally()
    {
        var plugin = new FindingPluginAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [plugin],
            NullLogger<ProjectAuditorComposer>.Instance);

        var project = MakeProject(
            new CustomAuditorDescriptor { Kind = "plugin", PluginId = "test.finding-plugin" });

        var auditors = composer.Compose(project, new FakeAgent());
        Assert.Single(auditors);

        var ctx = new AuditContext(
            WorkItemId: WorkItemId.New(),
            WorkBranch: "feat/test",
            BaseBranch: "main",
            Iteration: 1,
            OriginalPrompt: "test");

        var result = await auditors[0].RunAsync(sandbox: null!, workingDirectory: "/tmp", context: ctx);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal("test:finding-plugin", result.Findings[0].AuditorName);
        Assert.Equal(AuditSeverity.Error, result.Findings[0].Severity);
    }

    [Fact]
    public void PluginAuditor_IsIncluded_AlongsidePresetAndCustomAuditors()
    {
        var plugin = new InlinePluginAuditor();
        var composer = new ProjectAuditorComposer(
            new PresetCatalog(),
            [plugin],
            NullLogger<ProjectAuditorComposer>.Instance);

        // Project has a preset + a shell custom + a plugin custom
        var project = new Project
        {
            Id = new ProjectId("mixed"),
            DisplayName = "Mixed",
            RepositoryUrl = "https://example.com/r.git",
            Audit = new ProjectAudit
            {
                Languages = ["csharp"],
                Custom =
                [
                    new CustomAuditorDescriptor
                    {
                        Kind = "shell",
                        Name = "my-check",
                        Argv = ["echo", "ok"],
                    },
                    new CustomAuditorDescriptor
                    {
                        Kind = "plugin",
                        PluginId = "test.inline-plugin",
                    },
                ],
            },
        };

        var auditors = composer.Compose(project, new FakeAgent());

        // csharp preset (2 auditors) + shell custom + plugin
        Assert.True(auditors.Count >= 3, $"Expected at least 3 auditors, got {auditors.Count}");
        Assert.Contains(auditors, a => a.Name == "test:inline-plugin");
    }
}
