using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.OxlintAuditorPlugin;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the oxlint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming oxlint (never a pass or finding).
/// - Exit codes 0 and 1 with an oxlint JSON report are verdicts; other exits are infrastructure.
/// - Exit 1 without JSON (oxlint's usage-failure convention) fails closed as infrastructure.
/// - oxlint JSON maps to findings with rule codes, locations, and mapped severity.
/// - Raw tool severities (error/warning/advice and unknown levels) go through the declared mapping.
/// - Default exclusions (vendored + generated trees) and scoped options (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_oxlint", "true")].
/// </summary>
public sealed class OxlintAuditorTests
{
    private static readonly string? InstalledOxlintVersion = ProbeInstalledOxlintVersion();

    // Shape captured from oxlint 1.85.0 `--format json` against a fixture
    // with a JavaScript and a TypeScript issue: one error-level diagnostic
    // in src/bad.js, one warning-level diagnostic in src/bad.ts.
    private const string JsonWithJsErrorAndTsWarning = """
        {
          "diagnostics": [
            {
              "message": "`debugger` statement is not allowed",
              "code": "eslint(no-debugger)",
              "severity": "error",
              "url": "https://oxc.rs/docs/guide/usage/linter/rules/eslint/no-debugger.html",
              "help": "Remove the debugger statement",
              "filename": "src/bad.js",
              "labels": [{"span": {"offset": 0, "length": 9, "line": 1, "column": 1}}]
            },
            {
              "message": "Variable 'x' is declared but never used. Unused variables should start with a '_'.",
              "code": "eslint(no-unused-vars)",
              "severity": "warning",
              "url": "https://oxc.rs/docs/guide/usage/linter/rules/eslint/no-unused-vars.html",
              "help": "Consider removing this declaration.",
              "filename": "src/bad.ts",
              "labels": [{"label": "'x' is declared here", "span": {"offset": 14, "length": 1, "line": 2, "column": 5}}]
            }
          ],
          "number_of_files": 2,
          "number_of_rules": 96,
          "threads_count": 2,
          "start_time": 0.009
        }
        """;

    private const string JsonClean = """
        {
          "diagnostics": [],
          "number_of_files": 1,
          "number_of_rules": 96,
          "threads_count": 2,
          "start_time": 0.009
        }
        """;

    private const string JsonWithSeveritiesAndExcludedPaths = """
        {
          "diagnostics": [
            {
              "message": "`debugger` statement is not allowed",
              "code": "eslint(no-debugger)",
              "severity": "error",
              "filename": "src/app.ts",
              "labels": [{"span": {"offset": 0, "length": 9, "line": 4, "column": 1}}]
            },
            {
              "message": "Variable 'x' is declared but never used. Unused variables should start with a '_'.",
              "code": "eslint(no-unused-vars)",
              "severity": "warning",
              "filename": "src/app.ts",
              "labels": [{"label": "'x' is declared here", "span": {"offset": 14, "length": 1, "line": 1, "column": 5}}]
            },
            {
              "message": "This diagnostic is advisory only.",
              "code": "oxc(only-used-in-recursion)",
              "severity": "advice",
              "filename": "src/app.ts",
              "labels": [{"span": {"offset": 0, "length": 4, "line": 2, "column": 1}}]
            },
            {
              "message": "Expected `}` but found `EOF`",
              "severity": "error",
              "filename": "src/app.ts",
              "labels": [{"label": "`}` expected", "span": {"offset": 26, "length": 0, "line": 3, "column": 1}}]
            },
            {
              "message": "A diagnostic from a future reporter shape.",
              "code": "eslint(no-future)",
              "severity": "not-an-oxlint-level",
              "filename": "src/app.ts",
              "labels": [{"span": {"offset": 0, "length": 3, "line": 5, "column": 1}}]
            },
            {
              "message": "A diagnostic carrying no severity at all.",
              "code": "eslint(no-severity)",
              "filename": "src/app.ts",
              "labels": [{"span": {"offset": 0, "length": 3, "line": 6, "column": 1}}]
            },
            {
              "message": "`debugger` statement is not allowed",
              "code": "eslint(no-debugger)",
              "severity": "error",
              "filename": "vendor/lib.js",
              "labels": [{"span": {"offset": 0, "length": 9, "line": 1, "column": 1}}]
            },
            {
              "message": "`debugger` statement is not allowed",
              "code": "eslint(no-debugger)",
              "severity": "error",
              "filename": "node_modules/pkg/index.js",
              "labels": [{"span": {"offset": 0, "length": 9, "line": 2, "column": 1}}]
            },
            {
              "message": "`debugger` statement is not allowed",
              "code": "eslint(no-debugger)",
              "severity": "error",
              "filename": "dist/bundle.js",
              "labels": [{"span": {"offset": 0, "length": 9, "line": 1, "column": 1}}]
            }
          ],
          "number_of_files": 5,
          "number_of_rules": 96,
          "threads_count": 2,
          "start_time": 0.009
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingOxlint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "oxlint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oxlint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingOxlint()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oxlint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Version: 0.1.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oxlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.1.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(OxlintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new OxlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithJsErrorAndTsWarning_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsErrorAndTsWarning, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var jsFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("no-debugger", StringComparison.Ordinal));
        Assert.Equal("codeybox:oxlint", jsFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, jsFinding.Severity);
        Assert.Equal("src/bad.js:1", jsFinding.Location);
        Assert.Contains("no-debugger", jsFinding.Description, StringComparison.Ordinal);
        Assert.Contains("debugger", jsFinding.Description, StringComparison.Ordinal);

        var tsFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("no-unused-vars", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, tsFinding.Severity);
        Assert.Equal("src/bad.ts:2", tsFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("oxlint", scanExec!.Argv[0]);
        Assert.Contains("--format", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--no-error-on-unmatched-pattern", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("oxlint ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsErrorAndTsWarning, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task WarningsOnly_ExitCode0_ReportsAdvisoryFindings_AndPasses()
    {
        const string warningsOnly = """
            {
              "diagnostics": [
                {
                  "message": "Variable 'x' is declared but never used. Unused variables should start with a '_'.",
                  "code": "eslint(no-unused-vars)",
                  "severity": "warning",
                  "filename": "src/w.ts",
                  "labels": [{"label": "'x' is declared here", "span": {"offset": 14, "length": 1, "line": 1, "column": 5}}]
                }
              ],
              "number_of_files": 1,
              "number_of_rules": 96,
              "threads_count": 2,
              "start_time": 0.009
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, warningsOnly, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("src/w.ts:1", finding.Location);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // oxlint's usage-failure convention: text, not a report.
            return Task.FromResult(new SandboxExecResult(1, "Error: `--bogus-flag` is not expected in this context\n", ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oxlint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithJsErrorAndTsWarning, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oxlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "oxlint: command not found"));
        });

        IAuditor auditor = new OxlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oxlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("no-debugger", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("src/app.ts:4", error.Location);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(findings, f => f.Title.Contains("no-unused-vars", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Contains("Severity (tool): warning", warning.Description, StringComparison.Ordinal);

        var advice = Assert.Single(findings, f => f.Title.Contains("only-used-in-recursion", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, advice.Severity);
        Assert.Contains("Severity (tool): advice", advice.Description, StringComparison.Ordinal);

        // Parse errors carry no rule code: reported with the message as the
        // title rather than an invented rule id.
        var parseError = Assert.Single(findings, f => f.Title.Contains("Expected `}`", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, parseError.Severity);
        Assert.Equal("src/app.ts:3", parseError.Location);

        var unknown = Assert.Single(findings, f => f.Title.Contains("no-future", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("no-severity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // declared fallback default

        Assert.All(findings, f => Assert.IsType<AuditSeverity>(f.Severity));
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
            s => s.PluginId == OxlintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("oxlint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresOxlintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [OxlintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == OxlintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("oxlint", tool.Binary);
        // Verify-only by design: oxlint ships via npm/standalone, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("oxlint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Version: 9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new OxlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "9.9.9",
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
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new OxlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/oxlint.operator.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/oxlint.operator.json", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new OxlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("node_modules/", StringComparison.Ordinal)
                || f.Location.StartsWith("dist/", StringComparison.Ordinal)));
        Assert.Contains(result.Findings, f => f.Location is not null
            && f.Location.StartsWith("src/app.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsErrorAndTsWarning, ""));
        });

        var auditor = new OxlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "eslint(no-unused-vars)",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("no-unused-vars", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArguments_AreStructuredArgv_NotAShellString()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new OxlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-D,no-debugger,--deny-warnings",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("oxlint", scanExec!.Argv[0]);
        Assert.Contains("-D", scanExec.Argv);
        Assert.Contains("no-debugger", scanExec.Argv);
        Assert.Contains("--deny-warnings", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(" --deny-warnings", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("requires_oxlint", "true")]
    public async Task RealOxlint_JsAndTsFixture_YieldsFindings_WithRuleIdAndLocation()
    {
        var installed = InstalledOxlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedOxlintFixtureRepoAsync(clean: false);

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

            var auditor = new OxlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    // no-debugger is warning by default; deny it so the
                    // fixture fails the audit through the real path.
                    ["Scoped:ExtraArguments"] = "-D,no-debugger",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);

            var jsFinding = Assert.Single(result.Findings, f => f.Location == "src/index.js:1");
            Assert.Contains("no-debugger", jsFinding.Title, StringComparison.Ordinal);
            Assert.Equal(AuditSeverity.Error, jsFinding.Severity);

            var tsFinding = Assert.Single(result.Findings, f => f.Location == "src/math.ts:1");
            Assert.Contains("no-debugger", tsFinding.Title, StringComparison.Ordinal);
            Assert.Equal(AuditSeverity.Error, tsFinding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_oxlint", "true")]
    public async Task RealOxlint_CleanFixture_Passes()
    {
        var installed = InstalledOxlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedOxlintFixtureRepoAsync(clean: true);

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

            var auditor = new OxlintAuditor();
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

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.OxlintAuditorPlugin.dll");
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
            PluginId: OxlintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Oxlint JavaScript/TypeScript Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Version: " + OxlintAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("oxlint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "oxlint" && exec.Argv[1] == "--version";

    private static async Task<string> SeedOxlintFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-oxlint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "good.js"),
                "export function add(a, b) {\n  return a + b;\n}\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "index.js"),
                "debugger;\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "math.ts"),
                "debugger;\n");
        }

        return dir;
    }

    private static string? ProbeInstalledOxlintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "oxlint",
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
