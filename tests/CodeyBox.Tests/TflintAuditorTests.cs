using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.TflintAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the tflint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming tflint (never a pass or finding).
/// - Exits 0, 1, and 2 are findings-producing; a run failure also exits 1 but writes no SARIF report —
///   the parser fails closed so "could not run" is infrastructure, not findings.
/// - SARIF results map to findings with rule ids (tflint and tflint-errors runs) and file/line locations.
/// - Tool severities are mapped through the declared severity mapping (never passed through).
/// - --format/-f and --fix in ExtraArguments are rejected deterministically (parsing contract, no mutation).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_tflint", "true")] use the bundled terraform
///   ruleset only, so they need the binary but no network.
/// </summary>
public sealed class TflintAuditorTests
{
    private static readonly string? InstalledTflintVersion = ProbeInstalledTflintVersion();

    private const string SarifWithWarning = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "tflint", "version": "0.64.0" } },
              "results": [
                {
                  "ruleId": "terraform_unused_declarations",
                  "level": "warning",
                  "message": { "text": "variable \"unused_var\" is declared but not used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "main.tf" },
                        "region": { "startLine": 3 }
                      }
                    }
                  ]
                }
              ]
            },
            {
              "tool": { "driver": { "name": "tflint-errors", "version": "0.64.0" } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifWithBrokenHcl = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "tflint", "version": "0.64.0" } },
              "results": []
            },
            {
              "tool": { "driver": { "name": "tflint-errors", "version": "0.64.0" } },
              "results": [
                {
                  "ruleId": "Invalid expression",
                  "level": "error",
                  "message": { "text": "Expected the start of an expression, but found an invalid expression token." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "broken.tf" },
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
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "tflint", "version": "0.64.0" } },
              "results": []
            },
            {
              "tool": { "driver": { "name": "tflint-errors", "version": "0.64.0" } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifWithSeverities = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "tflint", "version": "0.64.0" } },
              "results": [
                {
                  "ruleId": "terraform_deprecated_syntax",
                  "level": "error",
                  "message": { "text": "deprecated syntax" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.tf" } } }]
                },
                {
                  "ruleId": "terraform_unused_declarations",
                  "level": "warning",
                  "message": { "text": "unused declaration" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "b.tf" } } }]
                },
                {
                  "ruleId": "terraform_comment_syntax",
                  "level": "notice",
                  "message": { "text": "comment style" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "c.tf" } } }]
                },
                {
                  "ruleId": "terraform_future_rule",
                  "level": "critical",
                  "message": { "text": "unknown future level" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "d.tf" } } }]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithVendoredPaths = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "tflint", "version": "0.64.0" } },
              "results": [
                {
                  "ruleId": "terraform_unused_declarations",
                  "level": "warning",
                  "message": { "text": "root violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "main.tf" } } }]
                },
                {
                  "ruleId": "terraform_unused_declarations",
                  "level": "warning",
                  "message": { "text": "downloaded module violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": ".terraform/modules/vpc/main.tf" } } }]
                },
                {
                  "ruleId": "terraform_unused_declarations",
                  "level": "warning",
                  "message": { "text": "vendored violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/shared/main.tf" } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTflint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "tflint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TflintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("tflint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingTflint()
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

        IAuditor auditor = new TflintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("tflint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "TFLint version 0.59.1\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TflintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("tflint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.59.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(TflintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new TflintAuditor();
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
            return Task.FromResult(new SandboxExecResult(2, SarifWithWarning, ""));
        });

        IAuditor auditor = new TflintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // A warning-severity result is advisory: reported, but the audit passes.
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:tflint", finding.AuditorName);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("terraform_unused_declarations", finding.Title, StringComparison.Ordinal);
        Assert.Equal("main.tf:3", finding.Location);
        Assert.Contains("unused_var", finding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("tflint", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        // Default scope: recursive scan from the work-tree root.
        Assert.Contains("--recursive", argv);
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

        IAuditor auditor = new TflintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FoundSomethingExit_Code2_WithSarifReport_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithWarning, ""));
        });

        IAuditor auditor = new TflintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(
            result.Findings,
            f => f.Title.Contains("terraform_unused_declarations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepositoryProblemExit_Code1_WithSarifReport_ReportsFindings()
    {
        // Broken HCL exits 1 but still writes a SARIF report via the
        // tflint-errors run — a repository problem, reported as findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithBrokenHcl, ""));
        });

        IAuditor auditor = new TflintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("Invalid expression", finding.Title, StringComparison.Ordinal);
        Assert.Equal("broken.tf:3", finding.Location);
    }

    [Fact]
    public async Task FailedToRunExit_Code1_WithoutSarifReport_IsInfrastructureFailure()
    {
        // tflint exits 1 (not the usual 2) for flag/usage errors too —
        // "could not run" writes a plain-text error to stderr and no report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "Failed to parse CLI options; --bogus-flag is unknown option"));
        });

        IAuditor auditor = new TflintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("tflint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, "", "unexpected exit"));
        });

        IAuditor auditor = new TflintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("tflint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "tflint: command not found"));
        });

        IAuditor auditor = new TflintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("tflint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsToCodeyBoxSeverities_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithSeverities, ""));
        });

        IAuditor auditor = new TflintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);

        var error = Assert.Single(result.Findings, f => f.Location == "a.tf");
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(result.Findings, f => f.Location == "b.tf");
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var notice = Assert.Single(result.Findings, f => f.Location == "c.tf");
        Assert.Equal(AuditSeverity.Info, notice.Severity);

        // An unrecognized level from a foreign build stays visible as a
        // warning rather than passing through raw or dropping to info.
        var unknown = Assert.Single(result.Findings, f => f.Location == "d.tf");
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);

        // Only the error-severity finding fails the audit: warnings and
        // notices are advisory.
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("warning", warning.Description, StringComparison.Ordinal);
        Assert.Contains("notice", notice.Description, StringComparison.Ordinal);
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

        var auditor = new TflintAuditor();
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
    public async Task FixExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TflintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--fix",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--fix", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == TflintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("tflint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresTflintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [TflintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == TflintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("tflint", tool.Binary);
        // Verify-only by design: no distro package carries tflint, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("tflint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "TFLint version 0.65.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TflintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.65.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_RuleSelectionAndConfig_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TflintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/etc/codeybox/tflint.hcl",
                ["Scoped:EnableRules"] = "terraform_required_version",
                ["Scoped:DisableRules"] = "terraform_comment_syntax",
                ["Scoped:OnlyRules"] = "terraform_unused_declarations",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/etc/codeybox/tflint.hcl");
        var enableIndex = argv.ToList().IndexOf("--enable-rule");
        Assert.True(enableIndex >= 0 && argv[enableIndex + 1] == "terraform_required_version");
        var disableIndex = argv.ToList().IndexOf("--disable-rule");
        Assert.True(disableIndex >= 0 && argv[disableIndex + 1] == "terraform_comment_syntax");
        var onlyIndex = argv.ToList().IndexOf("--only");
        Assert.True(onlyIndex >= 0 && argv[onlyIndex + 1] == "terraform_unused_declarations");
    }

    [Fact]
    public async Task ScopedConfiguration_RecursiveFalse_OmitsRecursiveFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TflintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Recursive"] = "false",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--recursive", scanExec!.Argv);
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

        IAuditor auditor = new TflintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("main.tf", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithWarning, ""));
        });

        var auditor = new TflintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "terraform_required_version",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    [Trait("requires_tflint", "true")]
    public async Task RealTflint_IssueFixture_ProducesFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledTflintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedTflintFixtureRepoAsync(unusedVariable: true);

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

            var auditor = new TflintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("terraform_unused_declarations", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Warning, finding.Severity);
            Assert.Equal("main.tf:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_tflint", "true")]
    public async Task RealTflint_CleanFixture_Passes()
    {
        var installed = InstalledTflintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedTflintFixtureRepoAsync(unusedVariable: false);

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

            var auditor = new TflintAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.TflintAuditorPlugin.dll");
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
            PluginId: TflintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: TFLint Terraform",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "TFLint version " + TflintAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("tflint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "tflint" && exec.Argv[1] == "--version";

    private static async Task<string> SeedTflintFixtureRepoAsync(bool unusedVariable)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-tflint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // The bundled terraform ruleset (recommended preset) is local to the
        // binary: no plugins to install, no network. An unused variable is a
        // known warning-severity issue under that preset.
        var manifest = unusedVariable
            ? """
              variable "unused_var" {
                type    = string
                default = "x"
              }
              """
            : """
              terraform {
                required_version = ">= 1.0"
              }
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "main.tf"), manifest);

        return dir;
    }

    private static string? ProbeInstalledTflintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tflint",
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
