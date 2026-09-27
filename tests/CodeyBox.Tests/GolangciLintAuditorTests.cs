using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.GolangciLintAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the golangci-lint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming golangci-lint (never a pass or finding).
/// - Exit codes 0 and 1 with a golangci-lint JSON report are verdicts; other exits are infrastructure.
/// - Exit 1 without JSON is infrastructure; exit 7 (no-go-files) with an empty-issues JSON document is
///   infrastructure via the exit code, not a clean pass.
/// - golangci-lint JSON maps to findings with linter names, locations, and mapped severity.
/// - Raw tool severities go through the declared mapping; empty severity (the common case) maps to Warning.
/// - Default exclusions (vendor/ + third_party/) and scoped options (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_golangci-lint", "true")].
/// </summary>
public sealed class GolangciLintAuditorTests
{
    private static readonly string? InstalledGolangciLintVersion = ProbeInstalledGolangciLintVersion();

    // Shape captured from golangci-lint 2.14.0 `run --output.json.path stdout --show-stats=false`
    // against a fixture with an errcheck issue (no severity assigned) and a govet issue carrying
    // an explicit error severity: one advisory finding in main.go, one blocking finding in pkg/server.go.
    private const string JsonWithErrcheckAndGovet = """
        {
          "Issues": [
            {
              "FromLinter": "errcheck",
              "Text": "Error return value of `f.Close` is not checked",
              "Severity": "",
              "SourceLines": ["\t\tf.Close()"],
              "Pos": {"Filename": "main.go", "Offset": 110, "Line": 10, "Column": 10},
              "ExpectNoLint": false,
              "ExpectedNoLintLinter": ""
            },
            {
              "FromLinter": "govet",
              "Text": "printf: non-constant format string in call to fmt.Sprintf",
              "Severity": "error",
              "SourceLines": ["\tfmt.Sprintf(msg)"],
              "Pos": {"Filename": "pkg/server.go", "Offset": 42, "Line": 4, "Column": 3},
              "ExpectNoLint": false,
              "ExpectedNoLintLinter": ""
            }
          ],
          "Report": {"Linters": [{"Name": "errcheck", "Enabled": true}, {"Name": "govet", "Enabled": true}]}
        }
        """;

    private const string JsonClean = """
        {
          "Issues": [],
          "Report": {"Linters": [{"Name": "errcheck", "Enabled": true}]}
        }
        """;

    private const string JsonWithSeveritiesAndExcludedPaths = """
        {
          "Issues": [
            {
              "FromLinter": "errcheck",
              "Text": "Error return value of `f.Close` is not checked",
              "Severity": "error",
              "Pos": {"Filename": "main.go", "Line": 10, "Column": 10}
            },
            {
              "FromLinter": "govet",
              "Text": "printf: non-constant format string in call to fmt.Sprintf",
              "Severity": "warning",
              "Pos": {"Filename": "pkg/server.go", "Line": 4, "Column": 3}
            },
            {
              "FromLinter": "staticcheck",
              "Text": "this value of err is never used",
              "Severity": "",
              "Pos": {"Filename": "pkg/server.go", "Line": 8, "Column": 2}
            },
            {
              "FromLinter": "custom",
              "Text": "A diagnostic from a future severity vocabulary.",
              "Severity": "not-a-golangci-level",
              "Pos": {"Filename": "pkg/server.go", "Line": 9, "Column": 1}
            },
            {
              "FromLinter": "revive",
              "Text": "exported function Foo should have comment or be unexported",
              "Severity": "info",
              "Pos": {"Filename": "pkg/server.go", "Line": 12, "Column": 1}
            },
            {
              "FromLinter": "typecheck",
              "Text": "undefined: undefined_symbol_here",
              "Severity": "",
              "Pos": {"Filename": "pkg/broken.go", "Line": 3, "Column": 2}
            },
            {
              "FromLinter": "errcheck",
              "Text": "Error return value is not checked",
              "Severity": "error",
              "Pos": {"Filename": "vendor/upstream/lib.go", "Line": 1, "Column": 1}
            },
            {
              "FromLinter": "errcheck",
              "Text": "Error return value is not checked",
              "Severity": "error",
              "Pos": {"Filename": "third_party/fork/tool.go", "Line": 2, "Column": 1}
            }
          ],
          "Report": {}
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGolangciLint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "golangci-lint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingGolangciLint()
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

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "golangci-lint has version 0.1.0 built with go1.0 from abc on never\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.1.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GolangciLintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new GolangciLintAuditor();
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
    public async Task Fixture_WithErrcheckAndGovet_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithErrcheckAndGovet, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The govet issue carries an explicit error severity, so the audit fails.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var errcheckFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("errcheck", StringComparison.Ordinal));
        Assert.Equal("codeybox:golangci-lint", errcheckFinding.AuditorName);
        Assert.Equal(AuditSeverity.Warning, errcheckFinding.Severity);
        Assert.Equal("main.go:10", errcheckFinding.Location);
        Assert.Contains("errcheck", errcheckFinding.Description, StringComparison.Ordinal);
        Assert.Contains("f.Close", errcheckFinding.Description, StringComparison.Ordinal);

        var govetFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("govet", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, govetFinding.Severity);
        Assert.Equal("pkg/server.go:4", govetFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("golangci-lint", scanExec!.Argv[0]);
        Assert.Contains("run", scanExec.Argv);
        Assert.Contains("--output.json.path", scanExec.Argv);
        Assert.Contains("stdout", scanExec.Argv);
        Assert.Contains("--show-stats=false", scanExec.Argv);
        Assert.Equal("./...", scanExec.Argv[^1]);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("golangci-lint ", StringComparison.Ordinal));
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

        IAuditor auditor = new GolangciLintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJson_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithErrcheckAndGovet, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task AdvisoryOnly_ExitCode1_ReportsWarningFindings_AndPasses()
    {
        const string advisoryOnly = """
            {
              "Issues": [
                {
                  "FromLinter": "revive",
                  "Text": "exported function Foo should have comment or be unexported",
                  "Severity": "",
                  "Pos": {"Filename": "pkg/foo.go", "Line": 3, "Column": 1}
                }
              ],
              "Report": {}
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, advisoryOnly, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Unclassified issues are advisory: reported, but the audit passes.
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("pkg/foo.go:3", finding.Location);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // A crash before the report is written: text, not a report.
            return Task.FromResult(new SandboxExecResult(1, "", "panic: runtime error\n"));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, JsonWithErrcheckAndGovet, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode7_NoGoFiles_WithEmptyIssuesJson_IsInfrastructureFailure()
    {
        // A directory with no Go files exits 7 carrying an (empty) JSON
        // document on stdout and the typechecking error on stderr: the exit
        // code is the verdict, so this is infrastructure, never a pass.
        const string emptyIssues = """
            {
              "Issues": [],
              "Report": {"Linters": []}
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                7,
                emptyIssues,
                "level=error msg=\"[linters_context] typechecking error: pattern ./...: directory prefix . does not contain main module or its selected dependencies\"\n"));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "golangci-lint: command not found"));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("golangci-lint", ex.Message, StringComparison.Ordinal);
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

        IAuditor auditor = new GolangciLintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/ and third_party/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("errcheck", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("main.go:10", error.Location);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(findings, f => f.Title.Contains("govet", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Contains("Severity (tool): warning", warning.Description, StringComparison.Ordinal);

        // The common case: no severity assigned by configuration.
        var unclassified = Assert.Single(findings, f => f.Title.Contains("staticcheck", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unclassified.Severity); // declared fallback default
        Assert.Contains("Severity (tool): (none)", unclassified.Description, StringComparison.Ordinal);

        var unknown = Assert.Single(findings, f => f.Title.Contains("custom", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var info = Assert.Single(findings, f => f.Title.Contains("revive", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Contains("Severity (tool): info", info.Description, StringComparison.Ordinal);

        var typecheck = Assert.Single(findings, f => f.Title.Contains("typecheck", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, typecheck.Severity);
        Assert.Equal("pkg/broken.go:3", typecheck.Location);

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
            s => s.PluginId == GolangciLintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("golangci-lint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGolangciLintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GolangciLintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GolangciLintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("golangci-lint", tool.Binary);
        // Verify-only by design: golangci-lint ships via its install script, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("golangci-lint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "golangci-lint has version 9.9.9 built with go1.0 from abc on never\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new GolangciLintAuditor();
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

        var auditor = new GolangciLintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/golangci.operator.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/golangci.operator.yml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new GolangciLintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("third_party/", StringComparison.Ordinal)));
        Assert.Contains(result.Findings, f => f.Location is not null
            && f.Location.StartsWith("main.go", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithErrcheckAndGovet, ""));
        });

        var auditor = new GolangciLintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "govet",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("govet", finding.Title, StringComparison.Ordinal);
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

        var auditor = new GolangciLintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--disable,errcheck,--max-issues-per-linter,0",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("golangci-lint", scanExec!.Argv[0]);
        Assert.Contains("--disable", scanExec.Argv);
        Assert.Contains("errcheck", scanExec.Argv);
        Assert.Contains("--max-issues-per-linter", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(" --disable", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("requires_golangci-lint", "true")]
    public async Task RealGolangciLint_FixtureWithIssue_YieldsFindings_WithRuleIdAndLocation()
    {
        var installed = InstalledGolangciLintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedGolangciLintFixtureRepoAsync(clean: false);

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

            var auditor = new GolangciLintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // Unclassified issues are advisory: findings are reported but the
            // audit passes through the real path.
            Assert.True(result.Passed);
            Assert.NotEmpty(result.Findings);

            var errcheckFinding = Assert.Single(result.Findings, f => f.Location == "main.go:8");
            Assert.Contains("errcheck", errcheckFinding.Title, StringComparison.Ordinal);
            Assert.Equal(AuditSeverity.Warning, errcheckFinding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_golangci-lint", "true")]
    public async Task RealGolangciLint_CleanFixture_Passes()
    {
        var installed = InstalledGolangciLintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedGolangciLintFixtureRepoAsync(clean: true);

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

            var auditor = new GolangciLintAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GolangciLintAuditorPlugin.dll");
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
            PluginId: GolangciLintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: golangci-lint Go Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "golangci-lint has version " + GolangciLintAuditor.DefaultExpectedVersion + " built with go1.0 from abc on never\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("golangci-lint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "golangci-lint" && exec.Argv[1] == "version";

    private static async Task<string> SeedGolangciLintFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-golangci-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "go.mod"),
            "module example.com/codeyboxfixture\n\ngo 1.24\n");

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "main.go"),
                "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"hi\")\n}\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "main.go"),
                "package main\n\nimport \"os\"\n\nfunc main() {\n\tf, _ := os.Open(\"nope\")\n\tif f != nil {\n\t\tf.Close()\n\t}\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledGolangciLintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "golangci-lint",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
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
        catch { /* best-effort fixture teardown */ }
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
