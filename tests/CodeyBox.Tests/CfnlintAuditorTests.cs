using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.CfnlintAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the cfn-lint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cfn-lint (never a pass or finding).
/// - Exits 0, 2, 4, 6, 8, 10, 12, and 14 are findings-producing; exit 1 writes no SARIF report —
///   the parser fails closed so "could not run" is infrastructure, not findings.
/// - SARIF results map to findings with rule ids and file/line locations; warning results carry no
///   level (SARIF defaults the absent level to warning) and the no-templates E1001 carries no location.
/// - Tool severities are mapped through the declared severity mapping (never passed through).
/// - Flag-like ExtraArguments are rejected deterministically (variadic selectors plus the --
///   separator would misread them as template paths).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_cfnlint", "true")] use the bundled
///   resource-provider schemas only, so they need the binary but no network.
/// </summary>
public sealed class CfnlintAuditorTests
{
    private static readonly string? InstalledCfnLintVersion = ProbeInstalledCfnLintVersion();

    private const string SarifWithError = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": [
                {
                  "message": { "text": "Additional properties are not allowed ('NotARealProp' was unexpected)" },
                  "level": "error",
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "template.yaml" },
                        "region": { "startLine": 8 }
                      }
                    }
                  ],
                  "ruleId": "E3002"
                }
              ]
            }
          ]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": []
            }
          ]
        }
        """;

    // Real cfn-lint shape: warning results carry no level at all.
    private const string SarifWithWarningNoLevel = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": [
                {
                  "message": { "text": "Parameter UnusedParam not used." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "template.yaml" },
                        "region": { "startLine": 3 }
                      }
                    }
                  ],
                  "ruleId": "W2001"
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithNote = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": [
                {
                  "message": { "text": "Prefer using Fn::Sub over Fn::Join with an empty delimiter" },
                  "level": "note",
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "template.yaml" },
                        "region": { "startLine": 9 }
                      }
                    }
                  ],
                  "ruleId": "I1022"
                }
              ]
            }
          ]
        }
        """;

    // Real cfn-lint shape for a run with no templates: no artifact uri.
    private const string SarifNoTemplates = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": [
                {
                  "message": { "text": "'Resources' is a required property" },
                  "level": "error",
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uriBaseId": "EXECUTIONROOT" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ],
                  "ruleId": "E1001"
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithSeverities = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": [
                {
                  "ruleId": "E3002",
                  "level": "error",
                  "message": { "text": "additional properties not allowed" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.yaml" } } }]
                },
                {
                  "ruleId": "W2001",
                  "level": "warning",
                  "message": { "text": "unused parameter" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "b.yaml" } } }]
                },
                {
                  "ruleId": "I1022",
                  "level": "note",
                  "message": { "text": "prefer sub over join" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "c.yaml" } } }]
                },
                {
                  "ruleId": "E9999",
                  "level": "critical",
                  "message": { "text": "unknown future level" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "d.yaml" } } }]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithVendoredPaths = """
        {
          "version": "2.1.0",
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "runs": [
            {
              "tool": { "driver": { "name": "cfn-lint", "version": "1.57.0" } },
              "results": [
                {
                  "ruleId": "E3002",
                  "level": "error",
                  "message": { "text": "root violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "template.yaml" } } }]
                },
                {
                  "ruleId": "E3002",
                  "level": "error",
                  "message": { "text": "vendored violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/shared/template.yaml" } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCfnLint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "cfn-lint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cfn-lint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingCfnLint()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "failed to parse options"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cfn-lint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "cfn-lint 1.40.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cfn-lint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.40.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CfnlintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
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
    public async Task Fixture_WithKnownIssue_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(2, SarifWithError, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // An error-severity result fails the audit.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:cfn-lint", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("E3002", finding.Title, StringComparison.Ordinal);
        Assert.Equal("template.yaml:8", finding.Location);
        Assert.Contains("NotARealProp", finding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("cfn-lint", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        // Default scope: no template arguments; the tool falls back to the
        // repository .cfnlintrc templates list. The -- separator is always
        // emitted so variadic selectors cannot swallow template paths.
        Assert.Contains("--", argv);
    }

    [Fact]
    public async Task WarningFixture_WithoutLevel_MapsToWarning_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, SarifWithWarningNoLevel, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("W2001", finding.Title, StringComparison.Ordinal);
        Assert.Equal("template.yaml:3", finding.Location);
        // The tool supplied no level: the shared SARIF parser defaults the
        // absent level to warning, and the description records that —
        // proving the severity came through the mapping, never raw.
        Assert.Contains("warning", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InformationalFixture_MapsToInfo_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(8, SarifWithNote, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("I1022", finding.Title, StringComparison.Ordinal);
        Assert.Equal("template.yaml:9", finding.Location);
    }

    [Fact]
    public async Task NoTemplatesFixture_YieldsErrorFinding_WithoutLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifNoTemplates, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // A tree with no templates is a loud failure, never a vacuous pass.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("E1001", finding.Title, StringComparison.Ordinal);
        Assert.Null(finding.Location);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    public async Task FoundSomethingExits_WithSarifReport_ReportFindings(int exitCode)
    {
        // cfn-lint returns a bitwise OR over the severities it found; every
        // combination writes the SARIF report and is findings-producing.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, SarifWithError, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(
            result.Findings,
            f => f.Title.Contains("E3002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedToRunExit_Code1_WithoutSarifReport_IsInfrastructureFailure()
    {
        // Exit 1 is usage errors (unknown flags): plain-text usage on stderr
        // and no SARIF report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "usage: \nBasic: cfn-lint test.yaml"));
        });

        IAuditor auditor = new CfnlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cfn-lint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(32, "", "unexpected exit"));
        });

        IAuditor auditor = new CfnlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cfn-lint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "cfn-lint: command not found"));
        });

        IAuditor auditor = new CfnlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cfn-lint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsToCodeyBoxSeverities_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(14, SarifWithSeverities, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);

        var error = Assert.Single(result.Findings, f => f.Location == "a.yaml");
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(result.Findings, f => f.Location == "b.yaml");
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var note = Assert.Single(result.Findings, f => f.Location == "c.yaml");
        Assert.Equal(AuditSeverity.Info, note.Severity);

        // An unrecognized level from a foreign build stays visible as a
        // warning rather than passing through raw or dropping to info.
        var unknown = Assert.Single(result.Findings, f => f.Location == "d.yaml");
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);

        // Only the error-severity finding fails the audit: warnings and
        // informationals are advisory.
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("error", error.Description, StringComparison.Ordinal);
        Assert.Contains("warning", warning.Description, StringComparison.Ordinal);
        Assert.Contains("note", note.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagLikeExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--regions,eu-west-1",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task FormatExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--format,json",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--format", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == CfnlintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cfn-lint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCfnLintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CfnlintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CfnlintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("cfn-lint", tool.Binary);
        // Verify-only by design: the tool is pip-installed (with the sarif
        // extra), so no distro package carries it — the pinned release must
        // be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("cfn-lint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "cfn-lint 1.58.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.58.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ChecksRegionsAndConfig_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/etc/codeybox/cfnlintrc.yaml",
                ["Scoped:IncludeChecks"] = "I",
                ["Scoped:IgnoreChecks"] = "W2001",
                ["Scoped:Regions"] = "us-east-1,eu-west-1",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config-file");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/etc/codeybox/cfnlintrc.yaml");
        var includeIndex = argv.ToList().IndexOf("--include-checks");
        Assert.True(includeIndex >= 0 && argv[includeIndex + 1] == "I");
        var ignoreIndex = argv.ToList().IndexOf("--ignore-checks");
        Assert.True(ignoreIndex >= 0 && argv[ignoreIndex + 1] == "W2001");
        var regionsIndex = argv.ToList().IndexOf("--regions");
        Assert.True(
            regionsIndex >= 0
            && argv[regionsIndex + 1] == "us-east-1"
            && argv[regionsIndex + 2] == "eu-west-1");
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_BecomePositionalsAfterSeparator()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludeChecks"] = "E",
                ["Scoped:Targets"] = "infra/vpc.yaml,infra/app.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        // The variadic --include-checks values come first, then the --
        // separator, then the positional targets: nothing positional can be
        // swallowed into the flag.
        var separatorIndex = argv.IndexOf("--");
        Assert.True(separatorIndex >= 0);
        var includeIndex = argv.IndexOf("--include-checks");
        Assert.True(includeIndex >= 0 && includeIndex < separatorIndex);
        Assert.Equal(
            ["cfn-lint", "--format", "sarif", "--include-checks", "E", "--", "infra/vpc.yaml", "infra/app.yaml"],
            argv);
    }

    [Fact]
    public async Task ScopedConfiguration_EscapingTarget_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "../escape.yaml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(CfnlintAuditor.TargetsKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithVendoredPaths, ""));
        });

        IAuditor auditor = new CfnlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("template.yaml", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithError, ""));
        });

        var auditor = new CfnlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "W2001",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    [Trait("requires_cfnlint", "true")]
    public async Task RealCfnLint_IssueFixture_ProducesFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledCfnLintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCfnLintFixtureRepoAsync(issue: true);

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

            var auditor = new CfnlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Targets"] = "template.yaml",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("E3002", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("template.yaml:8", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cfnlint", "true")]
    public async Task RealCfnLint_CleanFixture_Passes()
    {
        var installed = InstalledCfnLintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCfnLintFixtureRepoAsync(issue: false);

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

            var auditor = new CfnlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Targets"] = "template.yaml",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CfnlintAuditorPlugin.dll");
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
            PluginId: CfnlintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Cfn-Lint CloudFormation",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "cfn-lint " + CfnlintAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cfn-lint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cfn-lint" && exec.Argv[1] == "--version";

    private static async Task<string> SeedCfnLintFixtureRepoAsync(bool issue)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-cfnlint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // The resource-provider schemas are bundled in the installed package:
        // no plugins to install, no network. An unknown property is a known
        // error-severity issue.
        var manifest = issue
            ? """
              AWSTemplateFormatVersion: '2010-09-09'
              Description: Fixture with an unknown property
              Resources:
                MyBucket:
                  Type: AWS::S3::Bucket
                  Properties:
                    BucketName: my-test-bucket-12345
                    NotARealProp: 123
              """
            : """
              AWSTemplateFormatVersion: '2010-09-09'
              Description: Clean fixture
              Resources:
                MyBucket:
                  Type: AWS::S3::Bucket
                  Properties:
                    BucketName: my-test-bucket-12345
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "template.yaml"), manifest);

        return dir;
    }

    private static string? ProbeInstalledCfnLintVersion()
    {
        // The process sandbox remaps HOME, so a pip --user install that only
        // resolves via the user's HOME would not run inside the audit. Probe
        // with a bare HOME: a properly provisioned (system) cfn-lint answers
        // regardless, while a HOME-dependent one correctly yields null and
        // the real-binary tests below become no-ops.
        var probeHome = Path.Combine(
            Path.GetTempPath(), "codeybox-cfnlint-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(probeHome);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cfn-lint",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--version");
                psi.Environment["HOME"] = probeHome;
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
            finally
            {
                TryDeleteDirectory(probeHome);
            }
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
