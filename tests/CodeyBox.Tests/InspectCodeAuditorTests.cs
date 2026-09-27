using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.InspectCodeAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the InspectCode auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming inspectcode (never a pass or finding).
/// - Exit 0 is the only findings-producing verdict (with or without issues); exits 1 and 3 and
///   non-SARIF stdout are infrastructure. There is no non-zero "found something" exit.
/// - Solution discovery (shallowest .sln/.slnx) and the SolutionPath override, both fail-closed.
/// - SARIF output maps to findings with rule ids, locations, and mapped severity.
/// - Raw tool severities go through the declared mapping, and the tool-side -e floor moves with MinimumSeverity.
/// - Default exclusions (vendored + generated trees), solution-settings suppression posture,
///   and scoped options (ExpectedVersion, SolutionPath, SettingsPath, TrustRepositorySuppression, BuildSolution).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_inspectcode", "true")].
/// </summary>
public sealed class InspectCodeAuditorTests
{
    private static readonly string? InstalledInspectCodeVersion = ProbeInstalledInspectCodeVersion();

    private const string SarifWithIssues = """
        {
          "$schema": "https://schemastore.azurewebsites.net/schemas/json/sarif-2.1.0-rtm.6.json",
          "version": "2.1.0",
          "runs": [
            {
              "results": [
                {
                  "ruleId": ".CSharpErrors",
                  "level": "error",
                  "message": { "text": "Program using top-level statements must be an executable" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "App/Program.cs" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "NotAccessedVariable.Compiler",
                  "level": "warning",
                  "message": { "text": "Variable 'unusedLocal' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "App/Program.cs" },
                        "region": { "startLine": 6 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "UnusedMember.Global",
                  "level": "note",
                  "message": { "text": "Method 'UnusedMethod' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "App/Sample.cs" },
                        "region": { "startLine": 3 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "UnusedType.Global",
                  "level": "note",
                  "message": { "text": "Class 'Sample' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "App/Sample.cs" },
                        "region": { "startLine": 1 }
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
          "$schema": "https://schemastore.azurewebsites.net/schemas/json/sarif-2.1.0-rtm.6.json",
          "version": "2.1.0",
          "runs": [{ "results": [] }]
        }
        """;

    private const string SarifWithFilteredPaths = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "results": [
                {
                  "ruleId": "NotAccessedVariable.Compiler",
                  "level": "warning",
                  "message": { "text": "Variable 'x' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/Program.cs" },
                        "region": { "startLine": 2 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "NotAccessedVariable.Compiler",
                  "level": "warning",
                  "message": { "text": "Variable 'y' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "obj/Generated.cs" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "UnusedMember.Global",
                  "level": "note",
                  "message": { "text": "Method 'M' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "vendor/lib.cs" },
                        "region": { "startLine": 5 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "UnusedMember.Global",
                  "level": "note",
                  "message": { "text": "Method 'N' is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "node_modules/pkg/index.cs" },
                        "region": { "startLine": 7 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithSeverities = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "results": [
                {
                  "ruleId": "rule-error",
                  "level": "error",
                  "message": { "text": "Error-level inspection." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "A.cs" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "rule-warning",
                  "level": "warning",
                  "message": { "text": "Warning-level inspection." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "A.cs" }, "region": { "startLine": 2 } } }]
                },
                {
                  "ruleId": "rule-note",
                  "level": "note",
                  "message": { "text": "Note-level inspection." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "A.cs" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "rule-none",
                  "level": "none",
                  "message": { "text": "None-level inspection." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "A.cs" }, "region": { "startLine": 4 } } }]
                },
                {
                  "ruleId": "rule-nolevel",
                  "message": { "text": "No level field." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "A.cs" }, "region": { "startLine": 5 } } }]
                },
                {
                  "ruleId": "rule-unknown",
                  "level": "bogus",
                  "message": { "text": "Unrecognised level." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "A.cs" }, "region": { "startLine": 6 } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingInspectCode_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "inspectcode: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingInspectCode()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "JetBrains Inspect Code 9.99.0\nVersion: 9.99.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.99.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(InspectCodeAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new InspectCodeAuditor();
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
    public async Task NoSolutionDiscovered_IsDeterministicInfrastructure_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SolutionPath", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DiscoveryFailure_IsInfrastructureFailure_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "find: .: Permission denied"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Discovery_SelectsShallowestSolution_Deterministically()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src/App.sln\nApp.sln\nvendor/dep/dep.sln\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        // The root solution wins over deeper ones; leading-dash-safe positional form.
        Assert.Equal("./App.sln", scanExec!.Argv[^1]);
    }

    [Fact]
    public async Task ConfiguredSolutionPath_IsUsed_AfterPresenceCheck()
    {
        SandboxExec? scanExec = null;
        var seenPresenceCheck = false;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsSolutionPresenceProbe(exec))
            {
                seenPresenceCheck = true;
                return Task.FromResult(new SandboxExecResult(0, "src/App.sln\n", ""));
            }
            if (IsDiscoveryProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Should/NotBeUsed.sln\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SolutionPath"] = "src/App.sln",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.True(seenPresenceCheck);
        Assert.NotNull(scanExec);
        Assert.Equal("./src/App.sln", scanExec!.Argv[^1]);
    }

    [Fact]
    public async Task MissingConfiguredSolution_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsSolutionPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SolutionPath"] = "src/App.sln",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("src/App.sln", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("../evil.sln")]
    [InlineData("/abs/App.sln")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public async Task InvalidSolutionPath_IsDeterministicInfrastructure(string solutionPath)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec) || IsSolutionPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SolutionPath"] = solutionPath,
            }),
            CancellationToken.None);

        // An empty value falls back to discovery (which the fake answers), so only
        // non-empty invalid values fail here; assert accordingly.
        if (string.IsNullOrEmpty(solutionPath))
        {
            var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);
            return;
        }

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(InspectCodeAuditor.SolutionPathKey, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithIssues_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // InspectCode exits 0 whether or not issues were found: the verdict is in the SARIF document.
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);

        var error = Assert.Single(result.Findings, f => f.Title.Contains(".CSharpErrors", StringComparison.Ordinal));
        Assert.Equal("codeybox:inspectcode", error.AuditorName);
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("App/Program.cs:1", error.Location);

        var warning = Assert.Single(result.Findings, f => f.Title.Contains("NotAccessedVariable.Compiler", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("App/Program.cs:6", warning.Location);

        var note = Assert.Single(result.Findings, f => f.Title.Contains("UnusedMember.Global", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);
        Assert.Equal("App/Sample.cs:3", note.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("inspectcode", scanExec!.Argv[0]);
        Assert.Contains("-o=/dev/stdout", scanExec.Argv);
        Assert.Contains("--verbosity=OFF", scanExec.Argv);
        Assert.Contains("--no-build", scanExec.Argv);
        Assert.Contains("-e=INFO", scanExec.Argv);
        // Repo-authored solution settings are inert by default.
        Assert.Contains("--disable-settings-layers:SolutionShared;SolutionPersonal", scanExec.Argv);
        Assert.Equal("./Fixture.slnx", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            // InspectCode exits 1 when it could not run, e.g. a missing solution file.
            return Task.FromResult(new SandboxExecResult(1, "", "Unable to find target solution file in path /work/Nope.sln"));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode3_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            // InspectCode exits 3 for a solution with no files to inspect.
            return Task.FromResult(new SandboxExecResult(3, "", "No files to inspect were found."));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode0_WithoutSarif_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            // Console chatter instead of a SARIF report (e.g. an operator --verbosity
            // override defeating --verbosity=OFF) fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(
                0,
                "JetBrains Inspect Code 2026.2.2\nInspection report was written to /tmp/r.txt\n",
                ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("inspectcode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithSeverities, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("rule-note", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var none = Assert.Single(findings, f => f.Title.Contains("rule-none", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, none.Severity);

        // The shared SARIF parser defaults an absent level to "warning".
        var noLevel = Assert.Single(findings, f => f.Title.Contains("rule-nolevel", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, noLevel.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default
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
            s => s.PluginId == InspectCodeAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("inspectcode", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresInspectCodeRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [InspectCodeAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == InspectCodeAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("inspectcode", tool.Binary);
        // Verify-only by design: the Command Line Tools ship as a zip / .NET tool,
        // no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("inspectcode", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "JetBrains Inspect Code 9.9.9\nVersion: 9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new InspectCodeAuditor();
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
    public async Task ScopedConfiguration_SettingsPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SettingsPath"] = "/opt/codeybox/inspectcode.DotSettings",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var settingsIndex = argv.ToList().IndexOf("--settings");
        Assert.True(settingsIndex >= 0 && settingsIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/inspectcode.DotSettings", argv[settingsIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFilteredPaths, ""));
        });

        IAuditor auditor = new InspectCodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // obj/, vendor/ and node_modules/ findings are dropped by the default ExcludePaths;
        // the src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/Program.cs:2", finding.Location);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "NotAccessedVariable.Compiler",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("NotAccessedVariable.Compiler", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_RemovesSettingsLayerFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain(scanExec!.Argv, a => a.StartsWith("--disable-settings-layers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_BuildSolution_EmitsBuildFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BuildSolution"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--build", scanExec!.Argv);
        Assert.DoesNotContain("--no-build", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DerivesToolFloor_AndFiltersFindings()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsDiscoveryProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifWithIssues, ""));
        });

        var auditor = new InspectCodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("-e=ERROR", scanExec!.Argv);
        var finding = Assert.Single(result.Findings);
        Assert.Contains(".CSharpErrors", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    [Trait("requires_inspectcode", "true")]
    public async Task RealInspectCode_FixtureWithIssue_YieldsFindings()
    {
        var installed = InstalledInspectCodeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedInspectCodeFixtureRepoAsync(clean: false);

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

            var auditor = new InspectCodeAuditor();
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
            Assert.All(result.Findings, f =>
                Assert.StartsWith("App/", f.Location ?? string.Empty, StringComparison.Ordinal));

            var unused = Assert.Single(
                result.Findings,
                f => f.Title.Contains(".CSharpErrors", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, unused.Severity);
            Assert.Equal("App/Program.cs:6", unused.Location);

            var notAccessed = Assert.Single(
                result.Findings,
                f => f.Title.Contains("NotAccessedVariable", StringComparison.Ordinal)
                    && f.Location == "App/Program.cs:7");
            Assert.Equal(AuditSeverity.Warning, notAccessed.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_inspectcode", "true")]
    public async Task RealInspectCode_CleanFixture_Passes()
    {
        var installed = InstalledInspectCodeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedInspectCodeFixtureRepoAsync(clean: true);

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

            var auditor = new InspectCodeAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.InspectCodeAuditorPlugin.dll");
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
            PluginId: InspectCodeAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ReSharper InspectCode Static Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(
                0,
                "JetBrains Inspect Code " + InspectCodeAuditor.DefaultExpectedVersion + "\n"
                    + "Running on x64 OS in x64 architecture, .NET 10.0.12 under Ubuntu 24.04.5 LTS\n"
                    + "Version: " + InspectCodeAuditor.DefaultExpectedVersion + "\n",
                "");
        if (IsDiscoveryProbe(exec))
            return new SandboxExecResult(0, "./Fixture.slnx\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("inspectcode", StringComparer.Ordinal);

    private static bool IsSolutionPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "inspectcode" && exec.Argv[1] == "--version";

    private static bool IsDiscoveryProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("find .", StringComparison.Ordinal);

    private static async Task<string> SeedInspectCodeFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-inspectcode-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "App"));

        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "App", "App.csproj"), csproj);

        // Mirrors the manually verified shapes: the issue fixture references an
        // undefined type (a real .CSharpErrors error) and declares an unused
        // local (a NotAccessedVariable.Compiler warning); the clean fixture
        // is a bare top-level program.
        var program = clean
            ? "Console.WriteLine(\"clean\");\n"
            : "Console.WriteLine(\"hi\");\n"
                + "public static class Sample\n"
                + "{\n"
                + "    public static int UnusedMethod()\n"
                + "    {\n"
                + "        UndefinedType? broken = null;\n"
                + "        int unusedLocal = 42;\n"
                + "        return 1;\n"
                + "    }\n"
                + "}\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "App", "Program.cs"), program);

        var solution = """
            <Solution>
              <Project Path="App/App.csproj" />
            </Solution>
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "Fixture.slnx"), solution);

        return dir;
    }

    private static string? ProbeInstalledInspectCodeVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "inspectcode",
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
