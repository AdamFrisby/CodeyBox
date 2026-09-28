using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.ShellcheckAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the shellcheck auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming shellcheck (never a pass or finding).
/// - Exits 0 and 1 are verdicts (findings-producing); exits 2 and 4 are infrastructure.
///   Exit 3 is a verdict only for the tool's own zero-file-arguments diagnostic
///   ("No files specified." — a clean pass); any other exit-3 run fails closed as infrastructure.
/// - shellcheck json1 output maps to findings with SC rule ids and file:line locations.
/// - Raw tool levels go through the declared severity mapping (error→Error, warning→Warning,
///   info/style→Info; never passed through); only error-severity findings fail the audit.
/// - Shell script discovery contributes sorted positional targets after a "--" separator;
///   probe failures and overflows fail closed.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled; when
///   enabled it declares the shellcheck requirement with AptPackage for baseline install.
/// - Real binary execution tests under [Trait("requires_shellcheck", "true")].
/// </summary>
public sealed class ShellcheckAuditorTests
{
    private static readonly string? InstalledShellcheckVersion = ProbeInstalledShellcheckVersion();

    // Shape mirrors real `shellcheck -f json1` (0.9.0): a "comments" array
    // with file/line/level/numeric-code/message entries.
    private const string JsonWithMixedFindings = """
        {
          "comments": [
            {
              "file": "./scripts/deploy.sh",
              "line": 2,
              "endLine": 2,
              "column": 10,
              "endColumn": 21,
              "level": "error",
              "code": 2045,
              "message": "Iterating over ls output is fragile. Use globs.",
              "fix": null
            },
            {
              "file": "./scripts/deploy.sh",
              "line": 3,
              "endLine": 3,
              "column": 6,
              "endColumn": 8,
              "level": "warning",
              "code": 2006,
              "message": "Use $(...) notation instead of legacy backticks.",
              "fix": null
            },
            {
              "file": "./scripts/deploy.sh",
              "line": 4,
              "endLine": 4,
              "column": 6,
              "endColumn": 8,
              "level": "style",
              "code": 2086,
              "message": "Double quote to prevent globbing and word splitting.",
              "fix": null
            }
          ]
        }
        """;

    private const string JsonClean = """
        {
          "comments": []
        }
        """;

    private const string JsonWarningsOnly = """
        {
          "comments": [
            {
              "file": "./scripts/deploy.sh",
              "line": 3,
              "endLine": 3,
              "column": 6,
              "endColumn": 8,
              "level": "warning",
              "code": 2006,
              "message": "Use $(...) notation instead of legacy backticks.",
              "fix": null
            },
            {
              "file": "./scripts/helpers.sh",
              "line": 4,
              "endLine": 4,
              "column": 6,
              "endColumn": 8,
              "level": "info",
              "code": 2086,
              "message": "Double quote to prevent globbing and word splitting.",
              "fix": null
            }
          ]
        }
        """;

    private const string JsonWithUnknownLevel = """
        {
          "comments": [
            {
              "file": "./scripts/deploy.sh",
              "line": 1,
              "endLine": 1,
              "column": 1,
              "endColumn": 5,
              "level": "error",
              "code": 2045,
              "message": "Iterating over ls output is fragile. Use globs.",
              "fix": null
            },
            {
              "file": "./scripts/deploy.sh",
              "line": 3,
              "endLine": 3,
              "column": 1,
              "endColumn": 5,
              "level": "fatal",
              "message": "hypothetical future severity",
              "code": 9999,
              "fix": null
            }
          ]
        }
        """;

    private const string JsonWithFilteredPaths = """
        {
          "comments": [
            {
              "file": "./scripts/deploy.sh",
              "line": 1,
              "endLine": 1,
              "column": 10,
              "endColumn": 21,
              "level": "error",
              "code": 2045,
              "message": "root script violation",
              "fix": null
            },
            {
              "file": "./vendor/charts/helpers.sh",
              "line": 1,
              "endLine": 1,
              "column": 10,
              "endColumn": 21,
              "level": "error",
              "code": 2045,
              "message": "vendored script violation",
              "fix": null
            },
            {
              "file": "./node_modules/pkg/install.sh",
              "line": 1,
              "endLine": 1,
              "column": 10,
              "endColumn": 21,
              "level": "error",
              "code": 2045,
              "message": "dependency script violation",
              "fix": null
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingShellcheck_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "shellcheck: command not found"));
            if (IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0,
                    "ShellCheck - shell script analysis tool\nversion: 0.8.0\nlicense: GNU General Public License, version 3\n",
                    ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.8.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ShellcheckAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ShellcheckAuditor();
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
    public async Task Fixture_WithMixedFindings_YieldsRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithMixedFindings, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The SC2045 error-severity finding fails the audit.
        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var error = Assert.Single(
            result.Findings, f => f.Title.Contains("SC2045", StringComparison.Ordinal));
        Assert.Equal("codeybox:shellcheck", error.AuditorName);
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("scripts/deploy.sh:2", error.Location);
        Assert.Contains("Iterating over ls output", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(
            result.Findings, f => f.Title.Contains("SC2006", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("scripts/deploy.sh:3", warning.Location);

        var style = Assert.Single(
            result.Findings, f => f.Title.Contains("SC2086", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, style.Severity);
        Assert.Equal("scripts/deploy.sh:4", style.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("shellcheck", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("-f", argv);
        var formatIndex = argv.ToList().IndexOf("-f");
        Assert.Equal("json1", argv[formatIndex + 1]);
        // A "--" separator ends option parsing, and discovery contributed
        // the positional targets with a ./ prefix so dash-leading names
        // stay positional.
        Assert.Contains("--", argv);
        Assert.Contains("./scripts/deploy.sh", argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WarningsOnlyFixture_ReportsFindings_ButPassesAdvisory()
    {
        // Gate behaviour: only error-severity findings fail the audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(1, JsonWarningsOnly, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        var warning = Assert.Single(
            result.Findings, f => f.Title.Contains("SC2006", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("scripts/deploy.sh:3", warning.Location);
        var info = Assert.Single(
            result.Findings, f => f.Title.Contains("SC2086", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Equal("scripts/helpers.sh:4", info.Location);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJsonReport_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(1, JsonWithMixedFindings, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_FileError_IsInfrastructureFailure()
    {
        // shellcheck exits 2 for unreadable inputs — "could not run" even
        // though stdout still carries an empty json1 report, so the exit
        // code (not the report) is the discriminator.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(
                2, JsonClean, "./scripts/deploy.sh: openBinaryFile: does not exist (No such file or directory)"));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode3_WithoutSentinel_IsInfrastructureFailure()
    {
        // shellcheck exits 3 for flag/usage errors — "could not run" writes
        // no JSON report, so the parser fails closed.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(
                3, "", "unrecognized option `--bogus-flag'\n\nUsage: shellcheck [OPTIONS...] FILES..."));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode3_NoScriptsSentinel_IsCleanPass()
    {
        // Zero file arguments: discovery found no shell scripts and no
        // Targets were configured — nothing checkable is a clean pass, not
        // a failure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            return Task.FromResult(new SandboxExecResult(
                3, "", "No files specified.\n\nUsage: shellcheck [OPTIONS...] FILES..."));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode4_UnsupportedFormat_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(4, "", "Unknown format sarif"));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(127, "", "shellcheck: command not found"));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevels_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(1, JsonWithUnknownLevel, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        var error = Assert.Single(result.Findings, f => f.Title.Contains("SC2045", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        // The raw tool level is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("error", error.Description, StringComparison.OrdinalIgnoreCase);

        // An unrecognized future level maps through the declared default,
        // not the tool's raw string.
        var unknown = Assert.Single(result.Findings, f => f.Title.Contains("SC9999", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);
        Assert.DoesNotContain("fatal", unknown.Severity.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discovery_ContributesSortedPositionalTargets_AfterSeparator()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "./scripts/zulu.sh\n./deploy.sh\n./scripts/alpha.bash\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("shellcheck", argv[0]);
        // No shell: the discovery probe itself is argv, and its results are
        // passed as positional args after a "--" separator with a ./ prefix
        // (so a dash-leading name is never option-parsed), sorted by
        // repository-relative path.
        var separatorIndex = argv.ToList().IndexOf("--");
        Assert.True(separatorIndex >= 0);
        var targets = argv.Skip(separatorIndex + 1).ToList();
        Assert.Equal(["./deploy.sh", "./scripts/alpha.bash", "./scripts/zulu.sh"], targets);
    }

    [Fact]
    public async Task Discovery_Failure_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "find: permission denied"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("discovery", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Discovery_Overflow_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
            {
                var many = string.Join("\n", Enumerable.Range(0, ShellcheckAuditor.MaxDiscoveredTargets + 1)
                    .Select(i => $"./service-{i}/run.sh"));
                return Task.FromResult(new SandboxExecResult(0, many + "\n", ""));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("shellcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ShellcheckAuditor.TargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Discovery_DashLeadingFilename_StaysAPositionalPath_NeverAFlag()
    {
        // A repository-controlled file named like a shellcheck flag (e.g.
        // --severity=error.sh) must reach the tool as a path, not as an
        // option that changes the auditor's severity floor.
        const string dashLeadingJson = """
            {
              "comments": [
                {
                  "file": "./--severity=error.sh",
                  "line": 1,
                  "endLine": 1,
                  "column": 6,
                  "endColumn": 8,
                  "level": "warning",
                  "code": 2006,
                  "message": "Use $(...) notation instead of legacy backticks.",
                  "fix": null
                }
              ]
            }
            """;

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./--severity=error.sh\n./scripts/deploy.sh\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, dashLeadingJson, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        // One argv entry carrying the ./ prefix after "--" — the tool's
        // option parser sees a path, and the entry is never split or
        // reordered.
        Assert.Contains("./--severity=error.sh", argv);
        Assert.Contains("./scripts/deploy.sh", argv);
        Assert.DoesNotContain("--severity=error.sh", argv);
        // shellcheck echoes the ./ prefix in the file field; the parser
        // strips it so locations stay repository-relative.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("--severity=error.sh:1", finding.Location);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Targets_LeadingDashEntry_FailsClosedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ShellcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "--severity=error, scripts/deploy.sh",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(ShellcheckAuditor.TargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_SkipDiscovery()
    {
        SandboxExec? scanExec = null;
        var discoveryExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
            {
                discoveryExecs++;
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ShellcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "scripts/web.sh, deploy/api.sh",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(0, discoveryExecs);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("./scripts/web.sh", argv);
        Assert.Contains("./deploy/api.sh", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_UntrustedRepositoryConfig_PassesNorc()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ShellcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "false",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("--norc", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustedRepositoryConfig_OmitsNorc()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--norc", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArgumentsNorc_WinsOverTrust()
    {
        SandboxExec? scanExec = null;
        var norcCount = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ShellcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "false",
                ["Scoped:ExtraArguments"] = "--norc",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        foreach (var arg in scanExec!.Argv)
            if (arg == "--norc")
                norcCount++;
        Assert.Equal(1, norcCount);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "./scripts/deploy.sh\n./vendor/charts/helpers.sh\n./node_modules/pkg/install.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new ShellcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("scripts/deploy.sh:1", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./scripts/deploy.sh\n", ""));
            return Task.FromResult(new SandboxExecResult(1, JsonWithMixedFindings, ""));
        });

        var auditor = new ShellcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "SC2045",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("SC2045", finding.Title, StringComparison.Ordinal);
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
            s => s.PluginId == ShellcheckAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("shellcheck", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresShellcheckRequirement_WithAptPackage()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ShellcheckAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ShellcheckAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("shellcheck", tool.Binary);
        // Apt-installable by design: the declared package installs the tool
        // into the sandbox baseline automatically when this plugin is enabled.
        Assert.Equal("shellcheck", tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("shellcheck", install, StringComparison.Ordinal);
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("shellcheck", string.Join(" ", verification.Argv), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0,
                    "ShellCheck - shell script analysis tool\nversion: 0.8.0\nlicense: GNU General Public License, version 3\n",
                    ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ShellcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.8.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    [Trait("requires_shellcheck", "true")]
    public async Task RealShellcheck_DirtyScriptFixture_ProducesFinding()
    {
        var installed = InstalledShellcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedShellcheckFixtureRepoAsync(
            "#!/bin/sh\nfor f in $(ls *.m3u); do echo $f; done\n");

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

            var auditor = new ShellcheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // SC2045 is error-severity: the audit fails with the rule id
            // and a repository-relative file:line location.
            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("SC2045", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("scripts/run.sh:2", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_shellcheck", "true")]
    public async Task RealShellcheck_ParseErrorFixture_FailsWithErrorFinding()
    {
        var installed = InstalledShellcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedShellcheckFixtureRepoAsync(
            "#!/bin/bash\nif [ $x = 1 ]; then\n  echo hi\n");

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

            var auditor = new ShellcheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("SC1046", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("scripts/run.sh:2", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_shellcheck", "true")]
    public async Task RealShellcheck_CleanScriptFixture_Passes()
    {
        var installed = InstalledShellcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedShellcheckFixtureRepoAsync(
            "#!/bin/sh\nx=1\nprintf '%s\\n' \"$x\"\n");

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

            var auditor = new ShellcheckAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ShellcheckAuditorPlugin.dll");
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
            PluginId: ShellcheckAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ShellCheck Shell Script Analyser",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(
                0,
                "ShellCheck - shell script analysis tool\nversion: " + ShellcheckAuditor.DefaultExpectedVersion + "\nlicense: GNU General Public License, version 3\n",
                "");
        if (IsDiscoveryProbe(exec))
            return new SandboxExecResult(0, "./scripts/run.sh\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("shellcheck", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "shellcheck" && exec.Argv[1] == "--version";

    private static bool IsDiscoveryProbe(SandboxExec exec)
        => exec.Argv.Count > 0 && exec.Argv[0] == "find";

    private static bool IsRepoConfigProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal)
            && exec.Argv.Contains(".shellcheckrc", StringComparer.Ordinal);

    private static async Task<string> SeedShellcheckFixtureRepoAsync(string scriptContent)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-shellcheck-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));

        await File.WriteAllTextAsync(Path.Combine(dir, "scripts", "run.sh"), scriptContent);

        return dir;
    }

    private static string? ProbeInstalledShellcheckVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shellcheck",
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
