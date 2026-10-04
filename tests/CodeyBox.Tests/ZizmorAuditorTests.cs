using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.ZizmorAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the zizmor auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming zizmor (never a pass or finding).
/// - Exit 0 (scan completed, with or without findings — SARIF mode never uses a non-zero findings exit)
///   and exits 11-14 (findings verdicts from non-SARIF modes) are findings-producing; the SARIF
///   document is the discriminator. Exits 1 (audit error), 2 (usage error) and 3 (no inputs collected)
///   carry no SARIF report, so the parser fails closed and "could not run" is infrastructure.
/// - SARIF results map to findings with rule ids (zizmor/&lt;audit&gt;) and file/line locations.
/// - Tool levels are mapped through the declared severity mapping (never passed through).
/// - --format, --fix, --config/-c/--no-config, lone "-", and --gh-token in ExtraArguments are
///   rejected deterministically (parsing contract, no tree mutation, managed config resolution,
///   no stdin redirect, no credentials on auditor argv).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_zizmor", "true")] run fully offline
///   (--offline is the auditor default), so they need the binary but no network.
/// </summary>
public sealed class ZizmorAuditorTests
{
    private static readonly string? InstalledZizmorVersion = ProbeInstalledZizmorVersion();

    private const string SarifWithTemplateInjection = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": {
                "driver": {
                  "name": "zizmor",
                  "rules": [
                    { "id": "zizmor/template-injection" },
                    { "id": "zizmor/excessive-permissions" }
                  ]
                }
              },
              "results": [
                {
                  "ruleId": "zizmor/template-injection",
                  "level": "error",
                  "message": { "text": "code injection via template expansion: may expand into attacker-controllable code" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/ci.yml" },
                        "region": { "startLine": 6 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "zizmor/excessive-permissions",
                  "level": "warning",
                  "message": { "text": "overly broad permissions: default permissions used due to no permissions block" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/ci.yml" },
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
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "zizmor", "rules": [] } },
              "results": []
            }
          ]
        }
        """;

    private const string SarifWithLevels = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "zizmor", "rules": [] } },
              "results": [
                {
                  "ruleId": "zizmor/template-injection",
                  "level": "error",
                  "message": { "text": "code injection via template expansion" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/ci.yml" },
                        "region": { "startLine": 6 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "zizmor/excessive-permissions",
                  "level": "warning",
                  "message": { "text": "overly broad permissions" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/ci.yml" },
                        "region": { "startLine": 3 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "zizmor/anonymous-definition",
                  "level": "note",
                  "message": { "text": "workflow without a name field" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/ci.yml" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "zizmor/future-audit",
                  "level": "future-level",
                  "message": { "text": "some future audit fired" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/future.yml" },
                        "region": { "startLine": 2 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "zizmor/unleveled-audit",
                  "message": { "text": "a result without its own level" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/other.yml" },
                        "region": { "startLine": 4 }
                      }
                    }
                  ]
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
              "tool": { "driver": { "name": "zizmor", "rules": [] } },
              "results": [
                {
                  "ruleId": "zizmor/template-injection",
                  "level": "error",
                  "message": { "text": "root violation" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": ".github/workflows/ci.yml" },
                        "region": { "startLine": 6 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "zizmor/template-injection",
                  "level": "error",
                  "message": { "text": "vendored violation" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "vendor/upstream/.github/workflows/ci.yml" },
                        "region": { "startLine": 6 }
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
    public async Task MissingBinary_IsInfrastructureFailure_NamingZizmor_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "zizmor: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingZizmor()
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

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "zizmor 1.29.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.29.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ZizmorAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new ZizmorAuditor();
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
            return Task.FromResult(new SandboxExecResult(0, SarifWithTemplateInjection, ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // A high-severity (error-level) finding fails the audit.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        var finding = Assert.Single(
            result.Findings,
            f => f.Title.Contains("template-injection", StringComparison.Ordinal));
        Assert.Equal("codeybox:zizmor", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal(".github/workflows/ci.yml:6", finding.Location);
        Assert.Contains("template expansion", finding.Description, StringComparison.Ordinal);

        var advisory = Assert.Single(
            result.Findings,
            f => f.Title.Contains("excessive-permissions", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, advisory.Severity);
        Assert.Equal(".github/workflows/ci.yml:3", advisory.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("zizmor", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("--offline", argv);
        Assert.Contains("--no-ignores", argv);
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        // Default scope: the work-tree root; the tool collects inputs itself.
        Assert.Contains(".", argv);
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

        IAuditor auditor = new ZizmorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task FoundSomethingExit_WithSarifReport_ReportsFindings(int exitCode)
    {
        // In SARIF mode the scan exits 0 with or without findings; 11-14 are
        // the dedicated findings verdicts of non-SARIF modes. All are
        // findings-producing when the body parses as SARIF.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, SarifWithTemplateInjection, ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(
            result.Findings,
            f => f.Title.Contains("template-injection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "fatal: no audit was performed")]
    [InlineData(2, "error: unexpected argument '--bogus-flag' found")]
    [InlineData(3, "error: no inputs collected")]
    public async Task FailedToRunExit_WithoutSarifReport_IsInfrastructureFailure(int exitCode, string stderr)
    {
        // zizmor exits 1 for audit errors, 2 for flag/usage errors and 3
        // when no inputs were collected — all write diagnostics, not the
        // SARIF report, so the parser fails closed.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", stderr));
        });

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsExit_WithNonSarifBody_IsInfrastructureFailure()
    {
        // A findings exit carrying plain diagnostics instead of the SARIF
        // report means the scan did not honor the parsing contract — fail
        // closed as infrastructure, never as findings and never as a pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(14, "error[template-injection]: plain diagnostics", ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(5, "", "unexpected exit"));
        });

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "zizmor: command not found"));
        });

        IAuditor auditor = new ZizmorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("zizmor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsToCodeyBoxSeverities_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithLevels, ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);

        var high = Assert.Single(result.Findings, f => f.Location == ".github/workflows/ci.yml:6");
        Assert.Equal(AuditSeverity.Error, high.Severity);

        var medium = Assert.Single(result.Findings, f => f.Location == ".github/workflows/ci.yml:3");
        Assert.Equal(AuditSeverity.Warning, medium.Severity);

        var low = Assert.Single(result.Findings, f => f.Location == ".github/workflows/ci.yml:1");
        Assert.Equal(AuditSeverity.Info, low.Severity);

        // An unrecognised level from a foreign build stays visible as a
        // warning rather than passing through raw or dropping to info.
        var unknown = Assert.Single(result.Findings, f => f.Location == ".github/workflows/future.yml:2");
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);

        // A result without its own level takes SARIF's warning default.
        var unleveled = Assert.Single(result.Findings, f => f.Location == ".github/workflows/other.yml:4");
        Assert.Equal(AuditSeverity.Warning, unleveled.Severity);

        // Only the error-severity finding fails the audit.
        Assert.False(result.Passed);

        // The raw tool level is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("error", high.Description, StringComparison.Ordinal);
        Assert.Contains("note", low.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithTemplateInjection, ""));
        });

        var auditor = new ZizmorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, result.Findings[0].Severity);
    }

    [Theory]
    [InlineData("--format", "sarif")]
    [InlineData("--fix", "")]
    [InlineData("--fix=all", "")]
    [InlineData("--config", "/tmp/zizmor.yml")]
    [InlineData("-c", "/tmp/zizmor.yml")]
    [InlineData("--no-config", "")]
    [InlineData("-", "")]
    [InlineData("--gh-token", "secret")]
    public async Task ManagedExtraArguments_AreRejectedAsDeterministicInfrastructure(string flag, string value)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new ZizmorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = string.IsNullOrEmpty(value) ? flag : $"{flag},{value}",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside")]
    public async Task EscapingTarget_IsRejectedAsDeterministicInfrastructure(string target)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new ZizmorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = target,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
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
            s => s.PluginId == ZizmorAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("zizmor", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresZizmorRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ZizmorAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ZizmorAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("zizmor", tool.Binary);
        // Verify-only by design: no distro package carries a version-pinned
        // zizmor, so the pinned release must be provisioned into the baseline
        // by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("zizmor", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "zizmor 1.31.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new ZizmorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.31.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigFileAndTargets_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new ZizmorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/etc/codeybox/zizmor.yml",
                ["Scoped:Targets"] = ".github/workflows/ci.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/etc/codeybox/zizmor.yml");
        Assert.Contains(".github/workflows/ci.yml", argv);
        // Explicit targets replace the default work-tree root.
        Assert.DoesNotContain(".", argv);
    }

    [Fact]
    public async Task DefaultScan_PassesNoIgnores_UnlessSuppressionTrusted()
    {
        SandboxExec? defaultExec = null;
        var defaultSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            defaultExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor defaultAuditor = new ZizmorAuditor();
        await defaultAuditor.RunAsync(defaultSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(defaultExec);
        Assert.Contains("--no-ignores", defaultExec!.Argv);

        SandboxExec? trustingExec = null;
        var trustingSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            trustingExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var trustingAuditor = new ZizmorAuditor();
        await trustingAuditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);
        await ((IAuditor)trustingAuditor).RunAsync(trustingSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(trustingExec);
        Assert.DoesNotContain("--no-ignores", trustingExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithVendoredPaths, ""));
        });

        IAuditor auditor = new ZizmorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal(".github/workflows/ci.yml:6", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithTemplateInjection, ""));
        });

        var auditor = new ZizmorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "zizmor/excessive-permissions",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("excessive-permissions", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_zizmor", "true")]
    public async Task RealZizmor_IssueFixture_ProducesFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledZizmorVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedZizmorFixtureRepoAsync(broken: true);

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

            var auditor = new ZizmorAuditor();
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
                f => f.Title.Contains("template-injection", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal(".github/workflows/ci.yml:6", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_zizmor", "true")]
    public async Task RealZizmor_CleanFixture_Passes()
    {
        var installed = InstalledZizmorVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedZizmorFixtureRepoAsync(broken: false);

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

            var auditor = new ZizmorAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ZizmorAuditorPlugin.dll");
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
            PluginId: ZizmorAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Zizmor GitHub Actions Security",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "zizmor " + ZizmorAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("zizmor", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "zizmor" && exec.Argv[1] == "--version";

    private static async Task<string> SeedZizmorFixtureRepoAsync(bool broken)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-zizmor-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var workflowsDir = Path.Combine(dir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Template expansion of an attacker-controllable context is a known
        // high-severity (error-level) template-injection finding in the
        // default persona: no plugins to install, no network (--offline).
        // The clean variant pins least-privilege permissions and avoids
        // expansions entirely.
        var manifest = broken
            ? """
              on: push
              jobs:
                greet:
                  runs-on: ubuntu-latest
                  steps:
                    - run: echo "Hello ${{ github.event.issue.title }}"
              """
            : """
              on: push
              permissions: {}
              jobs:
                build:
                  runs-on: ubuntu-latest
                  steps:
                    - run: echo hi
              """;
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "ci.yml"), manifest);

        await RunGitAsync(dir, "init", "-q");
        await RunGitAsync(dir, "add", ".");
        return dir;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
    }

    private static string? ProbeInstalledZizmorVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zizmor",
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
