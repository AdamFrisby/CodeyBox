using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.HadolintAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the hadolint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming hadolint (never a pass or finding).
/// - Exits 0 and 1 with a SARIF report are verdicts; exit 1 without one is "could not run"
///   (infrastructure) — except the tool's own zero-file-arguments diagnostic, which is a clean pass.
/// - SARIF results map to findings with rule ids (DLxxxx/SCxxxx/DL1000) and file:line locations.
/// - Tool levels are mapped through the declared severity mapping (error→Error, warning→Warning,
///   note→Info; never passed through); only error-severity findings fail the audit.
/// - Dockerfile discovery contributes positional targets; probe failures and overflows fail closed.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_hadolint", "true")] lint fixture
///   Dockerfiles locally (hadolint is fully offline), so they need the binary but no network.
/// </summary>
public sealed class HadolintAuditorTests
{
    private static readonly string? InstalledHadolintVersion = ProbeInstalledHadolintVersion();

    private const string SarifWithMixedFindings = """
        {
          "$schema": "http://json.schemastore.org/sarif-2.1.0",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Hadolint", "version": "2.15.1" } },
              "results": [
                {
                  "ruleId": "DL3006",
                  "level": "warning",
                  "message": { "text": "Always tag the version of an image explicitly" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Dockerfile" },
                        "region": { "startLine": 1, "endLine": 1, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "DL3015",
                  "level": "note",
                  "message": { "text": "Avoid additional packages by specifying `--no-install-recommends`" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Dockerfile" },
                        "region": { "startLine": 2, "endLine": 2, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "DL1000",
                  "level": "error",
                  "message": { "text": "missing whitespace" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "broken/Dockerfile" },
                        "region": { "startLine": 2, "endLine": 2, "startColumn": 4, "endColumn": 4 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifClean = """
        {
          "$schema": "http://json.schemastore.org/sarif-2.1.0",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Hadolint", "version": "2.15.1" } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifWarningsOnly = """
        {
          "$schema": "http://json.schemastore.org/sarif-2.1.0",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Hadolint", "version": "2.15.1" } },
              "results": [
                {
                  "ruleId": "DL3006",
                  "level": "warning",
                  "message": { "text": "Always tag the version of an image explicitly" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Dockerfile" },
                        "region": { "startLine": 1, "endLine": 1, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "DL3008",
                  "level": "warning",
                  "message": { "text": "Pin versions in apt get install." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "deploy/Dockerfile.api" },
                        "region": { "startLine": 4, "endLine": 4, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithUnknownLevel = """
        {
          "$schema": "http://json.schemastore.org/sarif-2.1.0",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Hadolint", "version": "2.15.1" } },
              "results": [
                {
                  "ruleId": "DL3006",
                  "level": "error",
                  "message": { "text": "Always tag the version of an image explicitly" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Dockerfile" },
                        "region": { "startLine": 1, "endLine": 1, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "DL9999",
                  "level": "severe",
                  "message": { "text": "hypothetical future severity" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Dockerfile" },
                        "region": { "startLine": 3, "endLine": 3, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithFilteredPaths = """
        {
          "$schema": "http://json.schemastore.org/sarif-2.1.0",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Hadolint", "version": "2.15.1" } },
              "results": [
                {
                  "ruleId": "DL3006",
                  "level": "warning",
                  "message": { "text": "root Dockerfile violation" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "Dockerfile" },
                        "region": { "startLine": 1, "endLine": 1, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "DL3006",
                  "level": "warning",
                  "message": { "text": "vendored Dockerfile violation" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "vendor/charts/Dockerfile" },
                        "region": { "startLine": 1, "endLine": 1, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "DL3006",
                  "level": "warning",
                  "message": { "text": "dependency Dockerfile violation" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "node_modules/pkg/Dockerfile" },
                        "region": { "startLine": 1, "endLine": 1, "startColumn": 1, "endColumn": 1 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingHadolint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "hadolint: command not found"));
            if (IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Haskell Dockerfile Linter 2.12.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.12.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(HadolintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new HadolintAuditor();
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
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n./broken/Dockerfile\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithMixedFindings, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The DL1000 error-severity finding fails the audit.
        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var warning = Assert.Single(
            result.Findings, f => f.Title.Contains("DL3006", StringComparison.Ordinal));
        Assert.Equal("codeybox:hadolint", warning.AuditorName);
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("Dockerfile:1", warning.Location);
        Assert.Contains("Always tag the version", warning.Description, StringComparison.Ordinal);

        var note = Assert.Single(
            result.Findings, f => f.Title.Contains("DL3015", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);
        Assert.Equal("Dockerfile:2", note.Location);

        var error = Assert.Single(
            result.Findings, f => f.Title.Contains("DL1000", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("broken/Dockerfile:2", error.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("hadolint", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("-f", argv);
        var formatIndex = argv.ToList().IndexOf("-f");
        Assert.Equal("sarif", argv[formatIndex + 1]);
        // Discovery contributed the positional targets.
        Assert.Contains("Dockerfile", argv);
        Assert.Contains("broken/Dockerfile", argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new HadolintAuditor();
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
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(1, SarifWarningsOnly, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Warning, f.Severity));
        Assert.Contains(result.Findings, f => f.Location == "Dockerfile:1");
        Assert.Contains(result.Findings, f => f.Location == "deploy/Dockerfile.api:4");
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithSarifReport_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(1, SarifWithMixedFindings, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutSarifReport_IsInfrastructureFailure()
    {
        // hadolint exits 1 (not the usual 2) for flag/usage errors and
        // unreadable files too — "could not run" writes no SARIF report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(
                1, "", "Invalid option `--bogus-flag'\n\nUsage: hadolint ..."));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_NoDockerfilesSentinel_IsCleanPass()
    {
        // Zero file arguments: discovery found no Dockerfiles and no Targets
        // were configured — nothing checkable is a clean pass, not a failure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            return Task.FromResult(new SandboxExecResult(
                1, "Please provide a Dockerfile\n", ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(2, "", "unexpected exit"));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(127, "", "hadolint: command not found"));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(1, SarifWithUnknownLevel, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        var error = Assert.Single(result.Findings, f => f.Title.Contains("DL3006", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        // The raw tool level is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("error", error.Description, StringComparison.OrdinalIgnoreCase);

        // An unrecognized future level maps through the declared default,
        // not the tool's raw string.
        var unknown = Assert.Single(result.Findings, f => f.Title.Contains("DL9999", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);
        Assert.DoesNotContain("severe", unknown.Severity.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discovery_ContributesSortedPositionalTargets()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "./deploy/Dockerfile.api\n./Dockerfile\n./Containerfile\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("hadolint", argv[0]);
        // No shell: the discovery probe itself is argv, and its results are
        // passed as positional args with the ./ prefix stripped, sorted.
        var targets = argv.Skip(3).ToList();
        Assert.Equal(["Containerfile", "Dockerfile", "deploy/Dockerfile.api"], targets);
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
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
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
                var many = string.Join("\n", Enumerable.Range(0, HadolintAuditor.MaxDiscoveredTargets + 1)
                    .Select(i => $"./service-{i}/Dockerfile"));
                return Task.FromResult(new SandboxExecResult(0, many + "\n", ""));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("hadolint", ex.Message, StringComparison.Ordinal);
        Assert.Contains(HadolintAuditor.TargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
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
            s => s.PluginId == HadolintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("hadolint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresHadolintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [HadolintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == HadolintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("hadolint", tool.Binary);
        // Verify-only by design: no distro package carries a version pin, so
        // the pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("hadolint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
            if (IsDiscoveryProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Haskell Dockerfile Linter 2.12.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new HadolintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "2.12.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
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
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new HadolintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "docker/web.Dockerfile, deploy/api.Dockerfile",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(0, discoveryExecs);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("docker/web.Dockerfile", argv);
        Assert.Contains("deploy/api.Dockerfile", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_BecomesConfigArgument()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new HadolintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/etc/codeybox/hadolint.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/etc/codeybox/hadolint.yaml");
    }

    [Fact]
    public async Task ScopedConfiguration_UntrustedRepositoryConfig_NeutralizesRepoDiscovery()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new HadolintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "false",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/dev/null");
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArgumentsConfig_WinsOverConfigPath()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new HadolintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/etc/codeybox/hadolint.yaml",
                ["Scoped:ExtraArguments"] = "--config,/operator/extra.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain("/etc/codeybox/hadolint.yaml", argv);
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/operator/extra.yaml");
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
                    0, "./Dockerfile\n./vendor/charts/Dockerfile\n./node_modules/pkg/Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFilteredPaths, ""));
        });

        IAuditor auditor = new HadolintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("Dockerfile:1", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Dockerfile\n", ""));
            return Task.FromResult(new SandboxExecResult(1, SarifWithMixedFindings, ""));
        });

        var auditor = new HadolintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "DL1000",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("DL1000", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_hadolint", "true")]
    public async Task RealHadolint_InvalidDockerfileFixture_ProducesFinding()
    {
        var installed = InstalledHadolintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedHadolintFixtureRepoAsync(
            "FROM ubuntu\nRUN apt-get update && apt-get install -y curl\n");

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

            var auditor = new HadolintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // Style/best-practice violations are advisory: findings without
            // an error-severity entry keep the audit green.
            Assert.True(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("DL3006", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Warning, finding.Severity);
            Assert.Equal("Dockerfile:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_hadolint", "true")]
    public async Task RealHadolint_ParseErrorFixture_FailsWithErrorFinding()
    {
        var installed = InstalledHadolintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedHadolintFixtureRepoAsync(
            "FROM ubuntu:22.04\nRUNX broken (((\n");

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

            var auditor = new HadolintAuditor();
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
                result.Findings, f => f.Title.Contains("DL1000", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("Dockerfile:2", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_hadolint", "true")]
    public async Task RealHadolint_ValidDockerfileFixture_Passes()
    {
        var installed = InstalledHadolintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedHadolintFixtureRepoAsync("FROM ubuntu:22.04\n");

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

            var auditor = new HadolintAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.HadolintAuditorPlugin.dll");
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
            PluginId: HadolintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Hadolint Dockerfile Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "Haskell Dockerfile Linter " + HadolintAuditor.DefaultExpectedVersion + "\n", "");
        if (IsDiscoveryProbe(exec))
            return new SandboxExecResult(0, "./Dockerfile\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("hadolint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "hadolint" && exec.Argv[1] == "--version";

    private static bool IsDiscoveryProbe(SandboxExec exec)
        => exec.Argv.Count > 0 && exec.Argv[0] == "find";

    private static bool IsRepoConfigProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal)
            && exec.Argv.Contains(".hadolint.yaml", StringComparer.Ordinal);

    private static async Task<string> SeedHadolintFixtureRepoAsync(string dockerfileContent)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-hadolint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "Dockerfile"), dockerfileContent);

        return dir;
    }

    private static string? ProbeInstalledHadolintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "hadolint",
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
