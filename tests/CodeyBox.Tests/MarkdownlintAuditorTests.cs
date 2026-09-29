using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.MarkdownlintAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the markdownlint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming markdownlint (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exits 2, 3, 4 and others are infrastructure.
/// - Exit 1 without a JSON report on stderr fails closed as an infrastructure failure.
/// - The --json report is read from stderr (usage text on stdout is never parsed).
/// - A clean run (exit 0, no report) passes; a warnings-only run (exit 0 with a report)
///   reports advisory findings but still passes — the hybrid gate.
/// - markdownlint JSON issues map to findings with rule ids, locations, and mapped severity.
/// - Raw tool severities go through the declared mapping (error/warning plus the fail-closed default).
/// - Default exclusions, the --ignore-path /dev/null suppression posture, and scoped options
///   (ExpectedVersion, ConfigPath, Inputs, TrustRepositorySuppression).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_markdownlint", "true")].
/// </summary>
public sealed class MarkdownlintAuditorTests
{
    private static readonly string? InstalledMarkdownlintVersion = ProbeInstalledMarkdownlintVersion();

    // Shape mirrors real `markdownlint --json` (0.49.1): a JSON array on
    // stderr with fileName/lineNumber/ruleNames (MD code first)/severity.
    private const string JsonWithFindings = """
        [
          {
            "fileName": "docs/guide.md",
            "lineNumber": 3,
            "ruleNames": ["MD009", "no-trailing-spaces"],
            "ruleDescription": "Trailing spaces",
            "ruleInformation": "https://github.com/DavidAnson/markdownlint/blob/v0.41.1/doc/md009.md",
            "errorDetail": "Expected: 0 or 2; Actual: 3",
            "errorContext": null,
            "errorRange": [31, 3],
            "fixInfo": {"editColumn": 31, "deleteCount": 3},
            "severity": "error"
          },
          {
            "fileName": "docs/guide.md",
            "lineNumber": 7,
            "ruleNames": ["MD013", "line-length"],
            "ruleDescription": "Line length",
            "ruleInformation": "https://github.com/DavidAnson/markdownlint/blob/v0.41.1/doc/md013.md",
            "errorDetail": "Expected: 80; Actual: 133",
            "errorContext": null,
            "errorRange": [81, 53],
            "fixInfo": null,
            "severity": "warning"
          }
        ]
        """;

    private const string JsonWarningsOnly = """
        [
          {
            "fileName": "docs/guide.md",
            "lineNumber": 7,
            "ruleNames": ["MD013", "line-length"],
            "ruleDescription": "Line length",
            "ruleInformation": "https://github.com/DavidAnson/markdownlint/blob/v0.41.1/doc/md013.md",
            "errorDetail": "Expected: 80; Actual: 133",
            "errorContext": null,
            "errorRange": [81, 53],
            "fixInfo": null,
            "severity": "warning"
          }
        ]
        """;

    // Relative paths exercising the finding-level ExcludePaths mechanism.
    private const string JsonWithVendoredPaths = """
        [
          {
            "fileName": "docs/guide.md",
            "lineNumber": 3,
            "ruleNames": ["MD009", "no-trailing-spaces"],
            "ruleDescription": "Trailing spaces",
            "ruleInformation": "https://example.invalid/md009.md",
            "errorDetail": "Expected: 0 or 2; Actual: 3",
            "errorContext": null,
            "errorRange": [31, 3],
            "fixInfo": null,
            "severity": "error"
          },
          {
            "fileName": "vendor/upstream.md",
            "lineNumber": 1,
            "ruleNames": ["MD041", "first-line-heading"],
            "ruleDescription": "First line in a file should be a top-level heading",
            "ruleInformation": "https://example.invalid/md041.md",
            "errorDetail": null,
            "errorContext": "no heading here",
            "errorRange": null,
            "fixInfo": null,
            "severity": "error"
          }
        ]
        """;

    private const string JsonWithLevels = """
        [
          {
            "fileName": "a.md",
            "lineNumber": 1,
            "ruleNames": ["MD009", "no-trailing-spaces"],
            "ruleDescription": "Error-level diagnostic.",
            "ruleInformation": "https://example.invalid/md009.md",
            "errorDetail": null,
            "errorContext": null,
            "errorRange": null,
            "fixInfo": null,
            "severity": "error"
          },
          {
            "fileName": "a.md",
            "lineNumber": 2,
            "ruleNames": ["MD013", "line-length"],
            "ruleDescription": "Warning-level diagnostic.",
            "ruleInformation": "https://example.invalid/md013.md",
            "errorDetail": null,
            "errorContext": null,
            "errorRange": null,
            "fixInfo": null,
            "severity": "warning"
          },
          {
            "fileName": "a.md",
            "lineNumber": 3,
            "ruleNames": ["MD999", "unknown-rule"],
            "ruleDescription": "Unrecognised level.",
            "ruleInformation": "https://example.invalid/md999.md",
            "errorDetail": null,
            "errorContext": null,
            "errorRange": null,
            "fixInfo": null,
            "severity": "fatal"
          },
          {
            "fileName": "a.md",
            "lineNumber": 4,
            "ruleNames": ["MD041", "first-line-heading"],
            "ruleDescription": "No severity field.",
            "ruleInformation": "https://example.invalid/md041.md",
            "errorDetail": null,
            "errorContext": null,
            "errorRange": null,
            "fixInfo": null
          }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingMarkdownlint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "markdownlint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("markdownlint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingMarkdownlint()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("markdownlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "0.10.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("markdownlint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", JsonWithFindings));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var error = Assert.Single(result.Findings, f => f.Title.Contains("MD009", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("docs/guide.md:3", error.Location);

        var warning = Assert.Single(result.Findings, f => f.Title.Contains("MD013", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("docs/guide.md:7", warning.Location);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithWarnings_StillReportsFindings_ButPasses()
    {
        // A warnings-only run exits 0 yet still emits the JSON report:
        // advisory findings are reported without failing the gate.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", JsonWarningsOnly));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("MD013", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task Report_OnStdout_IsNotParsed_FailsClosedAsInfrastructure()
    {
        // The --json report lives on stderr; a JSON document on stdout
        // (usage text, a redirected stream) with an empty stderr is not a
        // report the parser trusts.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", JsonWithVendoredPaths));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        // The vendor/ finding is dropped by the default ExcludePaths; the
        // docs/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("docs/guide.md:3", finding.Location);
    }

    [Fact]
    public async Task ExitCode1_WithoutReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "some usage text, not a report"));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode4_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                4, "", "Cannot read or parse config file 'bad.json': Unexpected token"));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("markdownlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "markdownlint: command not found"));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("markdownlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", JsonWithLevels));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(4, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("MD009", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("MD013", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("MD999", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("MD041", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent severity stays advisory
    }

    [Fact]
    public async Task DefaultArguments_RunJsonDot_IgnorePathPin_AndExcludeIgnores()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("markdownlint", argv[0]);
        Assert.Contains("--json", argv);
        Assert.Contains("--dot", argv);
        var ignorePathIndex = argv.ToList().IndexOf("--ignore-path");
        Assert.True(ignorePathIndex >= 0 && ignorePathIndex + 1 < argv.Count);
        Assert.Equal("/dev/null", argv[ignorePathIndex + 1]);
        Assert.Contains("--ignore", argv);
        Assert.Contains("node_modules/", argv);
        Assert.Equal(".", argv[^1]);
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolAbsentFromBaselineProvisioning()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        Assert.Empty(loader.DiscoverPlugins());
        var status = Assert.Single(
            loader.GetDiscoveryStatuses(),
            s => s.PluginId == MarkdownlintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("markdownlint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresMarkdownlintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [MarkdownlintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == MarkdownlintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("markdownlint", tool.Binary);
        // Verify-only by design: markdownlint-cli ships via npm; no distro
        // apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("markdownlint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "0.10.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new MarkdownlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.10.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new MarkdownlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/markdownlint.operator.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/markdownlint.operator.json", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_Inputs_ReplaceDefaultTreeInput()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new MarkdownlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Inputs"] = "docs,README.md",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain(".", argv);
        Assert.Contains("docs", argv);
        Assert.Contains("README.md", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", JsonWithVendoredPaths));
        });

        IAuditor auditor = new MarkdownlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ finding is dropped by the default ExcludePaths; the
        // docs/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("docs/guide.md:3", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", JsonWithFindings));
        });

        var auditor = new MarkdownlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "MD013",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("MD013", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_RemovesIgnorePathPin()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new MarkdownlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--ignore-path", scanExec!.Argv);
    }

    [Fact]
    [Trait("requires_markdownlint", "true")]
    public async Task RealMarkdownlint_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledMarkdownlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedMarkdownlintFixtureRepoAsync(clean: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new MarkdownlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);

            var trailing = Assert.Single(result.Findings, f => f.Title.Contains("MD009", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, trailing.Severity);
            Assert.EndsWith("bad.md:3", trailing.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_markdownlint", "true")]
    public async Task RealMarkdownlint_CleanFixture_Passes()
    {
        var installed = InstalledMarkdownlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedMarkdownlintFixtureRepoAsync(clean: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new MarkdownlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_markdownlint", "true")]
    public async Task RealMarkdownlint_IgnoreFile_IsInert_ByDefault()
    {
        // The default scan pins --ignore-path /dev/null: a violation the
        // fixture hides via .markdownlintignore still surfaces as a finding.
        var installed = InstalledMarkdownlintVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(
            Path.GetTempPath(), "codeybox-markdownlint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixtureDir, "hidden.md"), "# Title\n\nTrailing spaces here   \n");
            await File.WriteAllTextAsync(Path.Combine(fixtureDir, ".markdownlintignore"), "hidden.md\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new MarkdownlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings, f => f.Title.Contains("MD009", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.MarkdownlintAuditorPlugin.dll");
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: MarkdownlintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Markdownlint Markdown Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, MarkdownlintAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("markdownlint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "markdownlint" && exec.Argv[1] == "--version";

    private static async Task<string> SeedMarkdownlintFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-markdownlint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.md"), "# Title\n\nSome text.\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.md"), "# Title\n\nTrailing spaces here   \n");
        }

        return dir;
    }

    private static string? ProbeInstalledMarkdownlintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "markdownlint",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var match = Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return process.ExitCode == 0 && match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture cleanup */ }
    }

    private static AuditContext FakeContext() =>
        new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class TestPluginHost(IConfigurationSection scoped) : IPluginHost
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; } = scoped;
    }

    private sealed class FakeSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec) : ISandbox
    {
        public string Id => "fake";

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return await onExec(exec, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
