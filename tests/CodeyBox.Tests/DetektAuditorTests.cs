using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.DetektAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the detekt auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming detekt (never a pass or finding).
/// - Exit codes 0 (clean / under maxIssues) and 2 (IssuesFound) are verdicts; exit 1 is
///   detekt's UNEXPECTED-ERROR exit — the inverted convention, not findings — and exit 3
///   (InvalidConfig) is infrastructure too.
/// - The SARIF report is read from stderr (--report sarif:/dev/stderr): detekt's console
///   reports and IssuesFound message go to stdout, so a verdict exit with no SARIF on
///   stderr fails closed as infrastructure.
/// - detekt SARIF maps to findings with namespaced rule ids, locations, and mapped severity
///   (error→Error, warning→Warning, note→Info) — raw levels never pass through.
/// - Default exclusions (vendored/generated paths), repo detekt.yml discovery under
///   TrustRepositoryConfig, and the operator-owned ConfigPath posture.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution test under [Trait("requires_detekt", "true")].
/// </summary>
public sealed class DetektAuditorTests
{
    private static readonly string? InstalledDetektVersion = ProbeInstalledDetektVersion();

    // Shape mirrors real `detekt --report sarif:...` (1.23.8): ruleIds are
    // "detekt.<ruleset>.<rule>", level is the SARIF vocabulary
    // (error/warning/note) detekt derives from its SeverityLevel.
    private const string SarifWithFindings = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "detekt", "version": "1.23.8", "rules": [] } },
              "results": [
                {
                  "ruleId": "detekt.style.MagicNumber",
                  "level": "error",
                  "message": { "text": "This expression contains a magic number." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/main/kotlin/example/App.kt" },
                        "region": { "startLine": 7 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "detekt.style.NestedBlockDepth",
                  "level": "warning",
                  "message": { "text": "Function is nested too deeply." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/main/kotlin/example/App.kt" },
                        "region": { "startLine": 12 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "detekt.comments.CommentOverPrivateFunction",
                  "level": "note",
                  "message": { "text": "Comment over private function." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/main/kotlin/example/App.kt" },
                        "region": { "startLine": 3 }
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
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "detekt", "version": "1.23.8", "rules": [] } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifAllLevels = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "detekt", "version": "1.23.8", "rules": [] } },
              "results": [
                {
                  "ruleId": "detekt.style.MagicNumber",
                  "level": "error",
                  "message": { "text": "Magic number." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/App.kt" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "detekt.style.NestedBlockDepth",
                  "level": "warning",
                  "message": { "text": "Nested too deep." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/App.kt" }, "region": { "startLine": 2 } } }]
                },
                {
                  "ruleId": "detekt.comments.CommentOverPrivateFunction",
                  "level": "note",
                  "message": { "text": "Comment over private function." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/App.kt" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "detekt.custom.OpinionatedRule",
                  "level": "none",
                  "message": { "text": "Informational result." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/App.kt" }, "region": { "startLine": 4 } } }]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifVendoredPaths = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "detekt", "version": "1.23.8", "rules": [] } },
              "results": [
                {
                  "ruleId": "detekt.style.MagicNumber",
                  "level": "error",
                  "message": { "text": "Magic number." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/main/kotlin/App.kt" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "detekt.style.MagicNumber",
                  "level": "error",
                  "message": { "text": "Magic number." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/lib/Lib.kt" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "detekt.style.MagicNumber",
                  "level": "error",
                  "message": { "text": "Magic number." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "build/generated/Gen.kt" }, "region": { "startLine": 7 } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_YieldsInfrastructureFailure_NamingDetekt_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            // The repo-config discovery probe precedes the binary check in
            // RunAsync; answer it, then fail the presence probe.
            if (IsConfigProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "command -v: detekt not found"));
            throw new InvalidOperationException("scan must not run when the presence probe fails");
        });

        IAuditor auditor = new DetektAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        // A security scanner that silently passes because it did not run is
        // the worst outcome: this throws (infrastructure) instead, naming detekt.
        Assert.Contains("detekt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongVersion_YieldsInfrastructureFailure_NamingDetekt()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "9.99.9\n", ""));
            throw new InvalidOperationException("scan must not run on a version mismatch");
        });

        IAuditor auditor = new DetektAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detekt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyFixture_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            // detekt's "issues found" exit is 2 — NOT the conventional 1 — and
            // the SARIF report is on stderr while console reports fill stdout.
            return Task.FromResult(new SandboxExecResult(2, "console findings noise", SarifWithFindings));
        });

        IAuditor auditor = new DetektAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var magic = Assert.Single(
            result.Findings, f => f.Title.Contains("detekt.style.MagicNumber", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, magic.Severity);
        Assert.Equal("src/main/kotlin/example/App.kt:7", magic.Location);

        var nested = Assert.Single(
            result.Findings, f => f.Title.Contains("detekt.style.NestedBlockDepth", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, nested.Severity);
        Assert.Equal("src/main/kotlin/example/App.kt:12", nested.Location);

        var comment = Assert.Single(
            result.Findings, f => f.Title.Contains("detekt.comments.CommentOverPrivateFunction", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, comment.Severity);
        Assert.Equal("src/main/kotlin/example/App.kt:3", comment.Location);
    }

    [Fact]
    public async Task AdvisoryOnlyFindings_DoNotFailTheAudit()
    {
        const string advisoryOnly = """
            {
              "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "detekt", "version": "1.23.8", "rules": [] } },
                  "results": [
                    {
                      "ruleId": "detekt.style.NestedBlockDepth",
                      "level": "warning",
                      "message": { "text": "Nested too deep." },
                      "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a/App.kt" }, "region": { "startLine": 1 } } }]
                    }
                  ]
                }
              ]
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", advisoryOnly));
        });

        IAuditor auditor = new DetektAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("a/App.kt:1", finding.Location);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", SarifClean));
        });

        IAuditor auditor = new DetektAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FoundSomethingExit_IsVerdict_WhileErrorExits_AreInfrastructure()
    {
        // Exit 2 (IssuesFound) carries the SARIF report on stderr: findings.
        var findingsSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", SarifWithFindings));
        });

        IAuditor auditor = new DetektAuditor();
        var result = await auditor.RunAsync(findingsSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);

        // Exit 1 in detekt's convention is UnexpectedError (crashes, argument
        // violations) — the common "1 = findings" reading is exactly wrong here.
        var errorSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", SarifWithFindings));
        });

        IAuditor failingAuditor = new DetektAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => failingAuditor.RunAsync(errorSandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("detekt", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Exit 3 (InvalidConfig) is "could not run" too, even alongside output.
        var configSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, "", SarifWithFindings));
        });

        IAuditor configAuditor = new DetektAuditor();
        var configEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => configAuditor.RunAsync(configSandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("detekt", configEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyStderr_WithCleanExit_IsInfrastructure_NeverAPass()
    {
        // The scan always streams SARIF to stderr, so silence there (e.g. an
        // operator --report redirect) fails closed rather than reading as clean.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new DetektAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detekt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SarifOnStdout_Only_IsInfrastructure_NotFindings()
    {
        // detekt prints console output to stdout; the auditor deliberately reads
        // the report from stderr. A report on stdout alone is not a verdict.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithFindings, ""));
        });

        IAuditor auditor = new DetektAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detekt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructure_EvenWithSarifReport()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(42, "", SarifWithFindings));
        });

        IAuditor auditor = new DetektAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detekt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeverityMapping_Applies_DetektSarifLevels()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", SarifAllLevels));
        });

        IAuditor auditor = new DetektAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Raw tool severities never pass through: every finding carries a
        // CodeyBox severity from the declared map.
        Assert.Equal(4, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error, SeverityOf(result, "detekt.style.MagicNumber"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "detekt.style.NestedBlockDepth"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "detekt.comments.CommentOverPrivateFunction"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "detekt.custom.OpinionatedRule"));
        Assert.False(result.Passed);

        static AuditSeverity SeverityOf(AuditResult r, string rule)
            => Assert.Single(r.Findings, f => f.Title.Contains(rule, StringComparison.Ordinal)).Severity;
    }

    [Fact]
    public async Task BuiltArguments_ScanWholeTree_WithSarifReportOnStderr()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", SarifClean));
        });

        IAuditor auditor = new DetektAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("detekt", argv[0]);
        var inputIndex = argv.ToList().IndexOf("--input");
        Assert.True(inputIndex >= 0 && inputIndex + 1 < argv.Count);
        Assert.Equal(".", argv[inputIndex + 1]);
        var reportIndex = argv.ToList().IndexOf("--report");
        Assert.True(reportIndex >= 0 && reportIndex + 1 < argv.Count);
        Assert.Equal("sarif:/dev/stderr", argv[reportIndex + 1]);
        // No repo detekt.yml present in the fixture: no --config is passed.
        Assert.DoesNotContain("--config", argv);
    }

    [Fact]
    public async Task RepositoryConfig_Discovered_WhenTrusted_AndPassedAsConfig()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsConfigProbe(exec))
            {
                // The presence script echoes each candidate file that exists.
                return Task.FromResult(new SandboxExecResult(0, "detekt.yml\n", ""));
            }

            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", SarifClean));
        });

        IAuditor auditor = new DetektAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("detekt.yml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task RepositoryConfig_NotLoaded_WhenTrustDisabled()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsConfigProbe(exec))
                throw new InvalidOperationException(
                    "repo config probe must not run when TrustRepositoryConfig=false");
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", SarifClean));
        });

        var auditor = new DetektAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "false",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--config", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_PassedAsConfig_SkipsDiscovery()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsConfigProbe(exec))
                throw new InvalidOperationException(
                    "repo config probe must not run when ConfigPath is set");
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", SarifClean));
        });

        var auditor = new DetektAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/detekt.operator.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/detekt.operator.yml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.23.7\n", ""));
            return Task.FromResult(new SandboxExecResult(0, "", SarifClean));
        });

        var auditor = new DetektAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.23.7",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGenerated()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", SarifVendoredPaths));
        });

        IAuditor auditor = new DetektAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/ and build/ findings are dropped by the default ExcludePaths;
        // the src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/main/kotlin/App.kt:3", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsConfigProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", SarifWithFindings));
        });

        var auditor = new DetektAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "detekt.style.NestedBlockDepth",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("detekt.style.NestedBlockDepth", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/main/kotlin/example/App.kt:12", finding.Location);
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
            s => s.PluginId == DetektAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("detekt", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresDetektRequirement_OnlyWhenEnabled()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [DetektAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == DetektAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("detekt", tool.Binary);
        // No apt package carries a version pin: presence is verified at bake
        // time and the operator provisions the release via the install hint.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("detekt", string.Join(" ", verification.Argv), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_detekt", "true")]
    public async Task RealDetekt_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledDetektVersion;
        if (installed is null)
            return;

        var fixtureDir = SeedDetektFixtureRepo(detektClean: false);
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

            var auditor = new DetektAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:TrustRepositoryConfig"] = "false",
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
    [Trait("requires_detekt", "true")]
    public async Task RealDetekt_CleanFixture_Passes()
    {
        var installed = InstalledDetektVersion;
        if (installed is null)
            return;

        var fixtureDir = SeedDetektFixtureRepo(detektClean: true);
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

            var auditor = new DetektAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:TrustRepositoryConfig"] = "false",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.DetektAuditorPlugin.dll");
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
            PluginId: DetektAuditor.PluginId,
            PluginDisplayName: "CodeyBox: detekt Kotlin Analyser",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, DetektAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("detekt", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "detekt" && exec.Argv[1] == "--version";

    // The repository-config discovery probe is the file-presence script —
    // distinct from the binary presence probe, which carries "command -v".
    private static bool IsConfigProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    private static string SeedDetektFixtureRepo(bool detektClean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-detekt-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // detekt analyses sources directly — no compile step needed. The dirty
        // fixture trips MagicNumber (a default-active style rule) reliably.
        var source = detektClean
            ? "package fixture\n\nfun add(a: Int, b: Int): Int = a + b\n"
            : "package fixture\n\nfun answer(): Int {\n    return 42\n}\n";
        File.WriteAllText(Path.Combine(dir, "Fixture.kt"), source);
        return dir;
    }

    private static string? ProbeInstalledDetektVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "detekt",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
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
