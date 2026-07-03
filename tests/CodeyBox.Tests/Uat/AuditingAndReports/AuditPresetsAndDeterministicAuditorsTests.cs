using CodeyBox.Audit.Presets;
using CodeyBox.Audit.Shell;
using CodeyBox.Core;
using CodeyBox.Projects;
using CodeyBox.Tests;

namespace CodeyBox.Tests.Uat.AuditingAndReports;

/// <summary>
/// UAT coverage for audit preset expansion and built-in deterministic auditors.
/// Plan anchor: docs/uat/00-plan.md#auditing-and-reports
/// </summary>
public sealed class AuditPresetsAndDeterministicAuditorsTests
{
    [Fact]
    public void UatProfile_ExpandsToExpectedConcreteAuditors()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("audit-uat"),
            DisplayName = "Audit UAT",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                Profile = AuditProfilePresets.Uat,
                Profiles = AuditProfilePresets.CreateBuiltIns(),
            },
        };

        var auditors = composer.Compose(project, new CapturingAgent());

        Assert.Equal(
            [
                "csharp:format-check",
                "csharp:build-WaE",
                "csharp:test-pass",
                "security:gitleaks",
                "security:semgrep",
                "security:llm-review",
                "cheating:deterministic-patterns",
            ],
            auditors.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void MultiLanguageProject_ComposesEveryDeclaredLanguageAndAuditType()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("audit-uat"),
            DisplayName = "Audit UAT",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                Languages = ["csharp", "python", "node"],
                AuditTypes = ["security"],
            },
        };

        var names = composer.Compose(project, new CapturingAgent()).Select(a => a.Name).ToArray();

        Assert.Contains("csharp:build-WaE", names);
        Assert.Contains("python:test-pass", names);
        Assert.Contains("node:test-pass", names);
        Assert.Contains("security:gitleaks", names);
        Assert.Contains("security:semgrep", names);
        Assert.Contains("security:llm-review", names);
    }

    [Fact]
    public void ProjectLanguageOverride_ReplacesBundledDefaultWithoutMutatingCatalogDefaults()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("audit-uat"),
            DisplayName = "Audit UAT",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit
            {
                Languages = ["csharp"],
                LanguageOverrides = new Dictionary<string, ProjectLanguagePresetOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["csharp"] = new()
                    {
                        Replace = true,
                        Auditors =
                        [
                            new ProjectConfiguredAuditor
                            {
                                Name = "csharp:operator-build",
                                Argv = ["dotnet", "build", "-warnaserror"],
                            },
                        ],
                    },
                },
            },
        };

        var overridden = composer.Compose(project, new CapturingAgent()).Single();
        var defaultNames = new PresetCatalog()
            .ResolveLanguage("csharp", new PresetContext(new CapturingAgent()))
            .Select(a => a.Name)
            .ToArray();

        Assert.Equal("csharp:operator-build", overridden.Name);
        Assert.Equal(["csharp:format-check", "csharp:build-WaE", "csharp:test-pass"], defaultNames);
    }

    [Fact]
    public void UnknownAuditType_ReportsCloseMatch()
    {
        var composer = new ProjectAuditorComposer(new PresetCatalog());
        var project = new Project
        {
            Id = new ProjectId("audit-uat"),
            DisplayName = "Audit UAT",
            RepositoryUrl = "https://example.invalid/repo.git",
            Audit = new ProjectAudit { AuditTypes = ["securty"] },
        };

        var ex = Assert.Throws<PresetConfigurationException>(() => composer.Compose(project, new CapturingAgent()));

        Assert.Contains("unknown audit type id 'securty'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Did you mean 'security'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellAuditor_NonZeroExitCapturesRawOutputAndBlockingFinding()
    {
        var auditor = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = "csharp:format-check",
            Argv = ["dotnet", "format", "--verify-no-changes"],
        });
        var sandbox = new RecordingSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(0, "/usr/bin/dotnet\n", "")
                : new SandboxExecResult(2, "format stdout", "format stderr"));

        var result = await auditor.RunAsync(sandbox, "/repo", AuditingAndReportsHelpers.Context());

        Assert.False(result.Passed);
        Assert.Equal("format stdout\nformat stderr", result.RawOutput);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("command exited 2", finding.Title, StringComparison.Ordinal);
        Assert.Equal("format stderr", finding.Description);
        Assert.Equal(AuditCapabilities.None, auditor.Required);
    }

    [Fact]
    public void SecurityPreset_UsesDocumentedGitleaksAndSemgrepArguments()
    {
        var auditors = new PresetCatalog()
            .ResolveAuditType("security", new PresetContext(new CapturingAgent()))
            .ToDictionary(a => a.Name, StringComparer.Ordinal);

        Assert.Equal(
            ["gitleaks", "detect", "--source", ".", "--no-banner", "--no-color"],
            Assert.IsAssignableFrom<IShellAuditorArgvProvider>(auditors["security:gitleaks"]).Argv);
        Assert.Equal(
            ["semgrep", "--config", "auto", "--error", "--quiet"],
            Assert.IsAssignableFrom<IShellAuditorArgvProvider>(auditors["security:semgrep"]).Argv);
        Assert.Equal(AuditCapabilities.None, auditors["security:gitleaks"].Required);
        Assert.Equal(AuditCapabilities.Network, auditors["security:semgrep"].Required);
    }

    [Fact]
    public async Task SecurityPreset_ToolAuditorsExecuteWhenToolsArePresent()
    {
        var auditors = new PresetCatalog()
            .ResolveAuditType("security", new PresetContext(new CapturingAgent()))
            .Where(a => a.Name is "security:gitleaks" or "security:semgrep")
            .ToArray();
        var executedCommands = new List<IReadOnlyList<string>>();
        var sandbox = new RecordingSandbox(exec =>
        {
            if (IsToolProbe(exec))
                return new SandboxExecResult(0, $"/usr/local/bin/{exec.Argv[4]}\n", "");

            executedCommands.Add(exec.Argv);
            return new SandboxExecResult(0, "clean", "");
        });

        var results = new List<AuditResult>();
        foreach (var auditor in auditors)
            results.Add(await auditor.RunAsync(sandbox, "/repo", AuditingAndReportsHelpers.Context()));

        Assert.All(results, result =>
        {
            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        });
        Assert.Contains(executedCommands, argv => argv.SequenceEqual(
            ["gitleaks", "detect", "--source", ".", "--no-banner", "--no-color"]));
        Assert.Contains(executedCommands, argv => argv.SequenceEqual(
            ["semgrep", "--config", "auto", "--error", "--quiet"]));
    }

    [Theory]
    [InlineData("security:gitleaks", "gitleaks")]
    [InlineData("security:semgrep", "semgrep")]
    public async Task SecurityPreset_MissingToolSurfacesAsWarning(string auditorName, string toolName)
    {
        var auditor = new PresetCatalog()
            .ResolveAuditType("security", new PresetContext(new CapturingAgent()))
            .Single(a => a.Name == auditorName);
        var sandbox = new RecordingSandbox(exec =>
            IsToolProbe(exec)
                ? new SandboxExecResult(1, "", "")
                : new SandboxExecResult(0, "unexpected command execution", ""));

        var result = await auditor.RunAsync(sandbox, "/repo", AuditingAndReportsHelpers.Context());

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains($"tool not installed in sandbox: {toolName}", finding.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.Executions, exec => exec.Argv.Count > 0 && exec.Argv[0] == toolName);
    }

    [Theory]
    [InlineData("info", AuditSeverity.Info)]
    [InlineData("warn", AuditSeverity.Warning)]
    [InlineData("unexpected-severity", AuditSeverity.Error)]
    public void SeverityParser_DefaultsUnknownValuesToBlockingError(string value, AuditSeverity expected)
        => Assert.Equal(expected, AuditSeverityParser.Parse(value));

    private static bool IsToolProbe(SandboxExec exec) =>
        exec.Argv.Count >= 3 &&
        exec.Argv[0] == "sh" &&
        exec.Argv[1] == "-c" &&
        exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private sealed class CapturingAgent : IAgentRunner
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
