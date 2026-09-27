using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.SpotBugsAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the SpotBugs auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming spotbugs (never a pass or finding).
/// - Exit codes 0 (clean) and 1 (bugs found) are verdicts (findings-producing); exits 2/3
///   (missing classes) and 4+ (analysis errors) are infrastructure.
/// - Exit 0/1 without SARIF output fails closed as infrastructure.
/// - SpotBugs SARIF on stdout maps to findings with rule ids, locations, and mapped severity.
/// - Raw tool severities go through the declared mapping (all four SpotBugs SARIF levels).
/// - Default exclusions (vendored paths), the operator-owned filter-file posture, and scoped
///   options (ExpectedVersion, FilterFilePath, ConfidenceLevel, EffortLevel).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled;
///   when enabled its apt package is installed by provisioning.
/// - Real binary execution tests under [Trait("requires_spotbugs", "true")].
/// </summary>
public sealed class SpotBugsAuditorTests
{
    private static readonly string? InstalledSpotBugsVersion = ProbeInstalledSpotBugsVersion();

    // Shape mirrors real `spotbugs -textui -sarif=/dev/stdout` (4.10.4): the
    // SARIF document arrives on stdout while stderr stays quiet (-quiet).
    // Levels are the reporter's own vocabulary (error/warning/note/none from
    // its Level enum); ruleIds are bug patterns; locations carry the source
    // file resolved from class debug info.
    private const string SarifWithFindings = """
        {
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "SpotBugs", "version": "4.10.4", "rules": [] } },
              "results": [
                {
                  "ruleId": "NP_NULL_ON_SOME_PATH",
                  "level": "error",
                  "message": { "text": "Possible null pointer dereference in Dirty.greet(String) on exception path" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/main/java/example/Dirty.java" },
                        "region": { "startLine": 7 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "UWF_UNWRITTEN_FIELD",
                  "level": "warning",
                  "message": { "text": "Unwritten field: example.Dirty.count" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/main/java/example/Dirty.java" },
                        "region": { "startLine": 3 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "REC_CATCH_EXCEPTION",
                  "level": "note",
                  "message": { "text": "Exception is caught when Exception is not thrown in example.Dirty.run()" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/main/java/example/Dirty.java" },
                        "region": { "startLine": 12 }
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
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "SpotBugs", "version": "4.10.4", "rules": [] } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifAllLevels = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "SpotBugs", "version": "4.10.4", "rules": [] } },
              "results": [
                {
                  "ruleId": "NP_NULL_ON_SOME_PATH",
                  "level": "error",
                  "message": { "text": "Possible null pointer dereference." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/Foo.java" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "UWF_UNWRITTEN_FIELD",
                  "level": "warning",
                  "message": { "text": "Unwritten field." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/Foo.java" }, "region": { "startLine": 2 } } }]
                },
                {
                  "ruleId": "REC_CATCH_EXCEPTION",
                  "level": "note",
                  "message": { "text": "Caught exception is not thrown." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/Foo.java" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "CBX_CUSTOM_BUILT_XML",
                  "level": "none",
                  "message": { "text": "Informational result." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/Foo.java" }, "region": { "startLine": 4 } } }]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifVendoredPaths = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "SpotBugs", "version": "4.10.4", "rules": [] } },
              "results": [
                {
                  "ruleId": "NP_NULL_ON_SOME_PATH",
                  "level": "error",
                  "message": { "text": "Possible null pointer dereference." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/main/java/example/App.java" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "NP_NULL_ON_SOME_PATH",
                  "level": "error",
                  "message": { "text": "Possible null pointer dereference." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/lib/Lib.java" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "NP_NULL_ON_SOME_PATH",
                  "level": "error",
                  "message": { "text": "Possible null pointer dereference." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "external/dep/Dep.java" }, "region": { "startLine": 7 } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_YieldsInfrastructureFailure_NamingSpotbugs_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "command -v: spotbugs not found"));
            throw new InvalidOperationException("scan must not run when the presence probe fails");
        });

        IAuditor auditor = new SpotBugsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        // A security scanner that silently passes because it did not run is
        // the worst outcome: this throws (infrastructure) instead, naming spotbugs.
        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongVersion_YieldsInfrastructureFailure_NamingSpotbugs()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "SpotBugs 9.99.9\n", ""));
            throw new InvalidOperationException("scan must not run on a version mismatch");
        });

        IAuditor auditor = new SpotBugsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyFixture_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // The SARIF report arrives on stdout; exit 1 is BUGS_FOUND_FLAG.
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var nullDereference = Assert.Single(
            result.Findings, f => f.Title.Contains("NP_NULL_ON_SOME_PATH", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, nullDereference.Severity);
        Assert.Equal("src/main/java/example/Dirty.java:7", nullDereference.Location);

        var unwritten = Assert.Single(
            result.Findings, f => f.Title.Contains("UWF_UNWRITTEN_FIELD", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unwritten.Severity);
        Assert.Equal("src/main/java/example/Dirty.java:3", unwritten.Location);

        var note = Assert.Single(
            result.Findings, f => f.Title.Contains("REC_CATCH_EXCEPTION", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);
        Assert.Equal("src/main/java/example/Dirty.java:12", note.Location);
    }

    [Fact]
    public async Task AdvisoryOnlyFindings_DoNotFailTheAudit()
    {
        const string advisoryOnly = """
            {
              "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "SpotBugs", "version": "4.10.4", "rules": [] } },
                  "results": [
                    {
                      "ruleId": "UWF_UNWRITTEN_FIELD",
                      "level": "warning",
                      "message": { "text": "Unwritten field." },
                      "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/Foo.java" }, "region": { "startLine": 1 } } }]
                    }
                  ]
                }
              ]
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, advisoryOnly, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("a/Foo.java:1", finding.Location);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FoundSomethingExit_IsVerdict_WhileErrorExit_IsInfrastructure()
    {
        // Exit 1 (BUGS_FOUND_FLAG) carries the SARIF report: findings.
        var findingsSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var result = await auditor.RunAsync(findingsSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);

        // Exit 4 (ERROR_FLAG: serious analysis errors) means the tool could
        // not run: infrastructure, even alongside partial output.
        var errorSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, SarifWithFindings, "Analysis failed"));
        });

        IAuditor failingAuditor = new SpotBugsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => failingAuditor.RunAsync(errorSandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingClassExit_IsInfrastructure_NotVerdict()
    {
        // Exit 2 (MISSING_CLASS_FLAG) means the analysis ran degraded
        // (dependencies absent from the classpath): not a verdict.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithFindings, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyStdout_WithCleanExit_IsInfrastructure_NeverAPass()
    {
        // Exit 0 with no report is not a pass: the scan always streams SARIF
        // to stdout, so silence (e.g. an operator -sarif redirect) fails
        // closed.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructure_EvenWithSarifReport()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(42, SarifWithFindings, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeverityMapping_Applies_AllFourSpotBugsLevels()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifAllLevels, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Raw tool severities never pass through: every finding carries a
        // CodeyBox severity from the declared map.
        Assert.Equal(4, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error, SeverityOf(result, "NP_NULL_ON_SOME_PATH"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "UWF_UNWRITTEN_FIELD"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "REC_CATCH_EXCEPTION"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "CBX_CUSTOM_BUILT_XML"));
        Assert.False(result.Passed);

        static AuditSeverity SeverityOf(AuditResult r, string rule)
            => Assert.Single(r.Findings, f => f.Title.Contains(rule, StringComparison.Ordinal)).Severity;
    }

    [Fact]
    public async Task BuiltArguments_SelectSarifReport_WithExitcode_AndNoClassOk()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("spotbugs", argv[0]);
        // First option selects the command-line UI (the default would open the GUI).
        Assert.Equal("-textui", argv[1]);
        Assert.Contains("-sarif=/dev/stdout", argv);
        Assert.Contains("-exitcode", argv);
        Assert.Contains("-noClassOk", argv);
        Assert.Contains("-quiet", argv);
        Assert.Contains("-medium", argv);
        Assert.Contains("-effort:default", argv);
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
            s => s.PluginId == SpotBugsAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("spotbugs", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresSpotbugsRequirement_WithAptPackage()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SpotBugsAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SpotBugsAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("spotbugs", tool.Binary);
        // The distro package installs the tool into the sandbox baseline,
        // but only when this plugin is enabled.
        Assert.Equal("spotbugs", tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("spotbugs", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("spotbugs", install, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "SpotBugs 4.9.8\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpotBugsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "4.9.8",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_FilterFilePath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpotBugsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:FilterFilePath"] = "/opt/codeybox/spotbugs.operator.xml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var excludeIndex = argv.ToList().IndexOf("-exclude");
        Assert.True(excludeIndex >= 0 && excludeIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/spotbugs.operator.xml", argv[excludeIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfidenceLevel_SelectsReportingFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpotBugsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfidenceLevel"] = "high",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("-high", argv);
        Assert.DoesNotContain("-medium", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_EffortLevel_SelectsEffortFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpotBugsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:EffortLevel"] = "max",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("-effort:max", argv);
        Assert.DoesNotContain("-effort:default", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_UnknownConfidenceLevel_IsInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            throw new InvalidOperationException("scan must not run with an unparseable confidence level");
        });

        var auditor = new SpotBugsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfidenceLevel"] = "extreme",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("spotbugs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifVendoredPaths, ""));
        });

        IAuditor auditor = new SpotBugsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ and external/ findings are dropped by the default
        // ExcludePaths; the src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/main/java/example/App.java:3", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        var auditor = new SpotBugsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "UWF_UNWRITTEN_FIELD",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("UWF_UNWRITTEN_FIELD", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/main/java/example/Dirty.java:3", finding.Location);
    }

    [Fact]
    [Trait("requires_spotbugs", "true")]
    public async Task RealSpotbugs_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledSpotBugsVersion;
        if (installed is null || ProbeInstalledJavacVersion() is null)
            return;

        var fixtureDir = await SeedSpotBugsFixtureRepoAsync(spotbugsClean: false);
        if (fixtureDir is null)
            return;

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

            var auditor = new SpotBugsAuditor();
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
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Title));
                Assert.False(string.IsNullOrWhiteSpace(f.Location));
            });
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_spotbugs", "true")]
    public async Task RealSpotbugs_CleanFixture_Passes()
    {
        var installed = InstalledSpotBugsVersion;
        if (installed is null || ProbeInstalledJavacVersion() is null)
            return;

        var fixtureDir = await SeedSpotBugsFixtureRepoAsync(spotbugsClean: true);
        if (fixtureDir is null)
            return;

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

            var auditor = new SpotBugsAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SpotBugsAuditorPlugin.dll");
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
            PluginId: SpotBugsAuditor.PluginId,
            PluginDisplayName: "CodeyBox: SpotBugs Java Bytecode Analyser",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "SpotBugs " + SpotBugsAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("spotbugs", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "spotbugs" && exec.Argv[1] == "-version";

    private static async Task<string?> SeedSpotBugsFixtureRepoAsync(bool spotbugsClean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-spotbugs-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var source = spotbugsClean
            ? "public final class Fixture {\n    private Fixture() {}\n    public static int add(int a, int b) {\n        return a + b;\n    }\n}\n"
            : "public class Fixture {\n    private int count;\n    public String greet(String name) {\n        if (name == null) {\n            return name.toString();\n        }\n        count = count + 1;\n        return \"hi \" + name;\n    }\n}\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "Fixture.java"), source);

        if (!TryCompileFixture(dir))
            return null;
        return dir;
    }

    private static bool TryCompileFixture(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "javac",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            psi.ArgumentList.Add("Fixture.java");
            using var process = Process.Start(psi)!;
            process.WaitForExit(milliseconds: 60_000);
            return process.ExitCode == 0 && File.Exists(Path.Combine(dir, "Fixture.class"));
        }
        catch
        {
            return false;
        }
    }

    private static string? ProbeInstalledSpotBugsVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "spotbugs",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 60_000))
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

    private static string? ProbeInstalledJavacVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "javac",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 30_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            return process.ExitCode == 0 ? "present" : null;
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
