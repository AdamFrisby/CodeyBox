using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.KubeLinterAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the kube-linter auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming kube-linter (never a pass or finding).
/// - Exits 0 and 1 are verdicts; a run failure also exits 1 but writes no SARIF report —
///   the parser fails closed so "could not run" is infrastructure, not findings.
/// - Vacuous scans (zero checks enabled, no valid objects) exit 0/1 with empty stdout and
///   fail closed as infrastructure — never a pass.
/// - SARIF results map to findings with the check name as rule id and the manifest
///   path as location (kube-linter's SARIF emitter reports a fixed startLine of 1).
/// - kube-linter carries no severity level, so every finding maps to Error through the
///   declared severity mapping (never passed through raw).
/// - --format/--output/--config in ExtraArguments are rejected deterministically
///   (parsing contract, no writes into the audited tree, unambiguous config source).
/// - A worktree-root .kube-linter.yaml/.yml (repo-controlled check selection) fails
///   closed unless TrustRepositorySuppression or an out-of-tree ConfigFile is set.
/// - Targets entries must be repo-relative — rooted/traversing paths are rejected.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_kube-linter", "true")] need only
///   the binary: kube-linter renders everything locally, no network.
/// </summary>
public sealed class KubeLinterAuditorTests
{
    private static readonly string? InstalledKubeLinterVersion = ProbeInstalledKubeLinterVersion();

    // kube-linter SARIF results carry no "level" — the shared parser
    // supplies the SARIF-default "warning" token, which the declared mapping
    // then sends to Error.
    private const string SarifWithViolations = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "kube-linter", "version": "0.8.3" } },
              "results": [
                {
                  "ruleId": "latest-tag",
                  "message": { "text": "The container \"sec-ctx-demo\" is using an invalid container image, \"busybox\".\nobject: <no namespace>/sec-ctx-demo /v1, Kind=Pod" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "deploy/pod.yaml" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "run-as-non-root",
                  "message": { "text": "container \"sec-ctx-demo\" is not set to runAsNonRoot\nobject: <no namespace>/sec-ctx-demo /v1, Kind=Pod" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "deploy/pod.yaml" },
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
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "kube-linter", "version": "0.8.3" } },
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
              "tool": { "driver": { "name": "kube-linter" } },
              "results": [
                {
                  "ruleId": "latest-tag",
                  "message": { "text": "absent level → SARIF-default token" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.yaml" } } }]
                },
                {
                  "ruleId": "dangling-service",
                  "level": "note",
                  "message": { "text": "explicit SARIF note level" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "b.yaml" } } }]
                },
                {
                  "ruleId": "future-check",
                  "level": "unheardof",
                  "message": { "text": "unknown level from a foreign build" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "c.yaml" } } }]
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
              "tool": { "driver": { "name": "kube-linter" } },
              "results": [
                {
                  "ruleId": "latest-tag",
                  "message": { "text": "root manifest violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "deploy/pod.yaml" } } }]
                },
                {
                  "ruleId": "latest-tag",
                  "message": { "text": "vendored chart violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/charts/app/pod.yaml" } } }]
                },
                {
                  "ruleId": "latest-tag",
                  "message": { "text": "third-party violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "third_party/ops/pod.yaml" } } }]
                },
                {
                  "ruleId": "latest-tag",
                  "message": { "text": "dependency violation" },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "node_modules/pkg/pod.yaml" } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingKubeLinter_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "kube-linter: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingKubeLinter()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "unknown command"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "0.7.9\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.7.9", ex.Message, StringComparison.Ordinal);
        Assert.Contains(KubeLinterAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
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
    public async Task Fixture_WithKnownViolations_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithViolations, "Error: found 2 lint errors"));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var finding = Assert.Single(
            result.Findings, f => f.Title.Contains("latest-tag", StringComparison.Ordinal));
        Assert.Equal("codeybox:kube-linter", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("deploy/pod.yaml:1", finding.Location);
        Assert.Contains("sec-ctx-demo", finding.Description, StringComparison.Ordinal);
        Assert.Contains(
            result.Findings, f => f.Title.Contains("run-as-non-root", StringComparison.Ordinal));

        Assert.NotNull(scanExec);
        Assert.Equal("kube-linter", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Equal("lint", argv[1]);
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        // "Nothing to check" is kube-linter's own failure, not a vacuous pass.
        Assert.Contains("--fail-if-no-objects-found", argv);
        // Default scope: whole work tree, last positional argument.
        Assert.Equal(".", argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FoundSomethingExit_Code1_WithSarifReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithViolations, "Error: found 2 lint errors"));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task FailedToRunExit_Code1_WithoutSarifReport_IsInfrastructureFailure()
    {
        // kube-linter exits 1 for flag/usage errors too — "could not run"
        // writes a plain-text error to stderr and no report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "Error: unknown flag: --bogus-flag"));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VacuousScan_Exit0_WithoutSarifReport_IsInfrastructureFailure()
    {
        // A config enabling zero checks exits 0 with only a stderr warning
        // ("Warning: no checks enabled.") — "ran clean" without a report is
        // not a pass. Targets holding no objects exit 1 instead under
        // --fail-if-no-objects-found.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", "Warning: no checks enabled."));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "unexpected exit"));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "kube-linter: command not found"));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kube-linter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsEveryViolationToError_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithLevels, "Error: found 3 lint errors"));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // kube-linter has no severity vocabulary: the absent level (SARIF
        // default "warning"), an explicit "note", and an unrecognized level
        // all map to Error — a fired check is a policy failure, never a raw
        // pass-through and never silently advisory.
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        var absent = Assert.Single(result.Findings, f => f.Location == "a.yaml");
        Assert.Contains("warning", absent.Description, StringComparison.Ordinal);
        var note = Assert.Single(result.Findings, f => f.Location == "b.yaml");
        Assert.Contains("note", note.Description, StringComparison.Ordinal);
        var unknown = Assert.Single(result.Findings, f => f.Location == "c.yaml");
        Assert.Contains("unheardof", unknown.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormatExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
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
    public async Task OutputExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--output,results.sarif",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--output", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ConfigExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--config=/tmp/conf.yaml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--config", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == KubeLinterAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("kube-linter", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresKubeLinterRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [KubeLinterAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == KubeLinterAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("kube-linter", tool.Binary);
        // Verify-only by design: no distro package carries kube-linter, so
        // the pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("kube-linter", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "0.9.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.9.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_CheckSelectionAndConfig_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/etc/codeybox/kube-linter.yaml",
                ["Scoped:IncludeChecks"] = "latest-tag, run-as-non-root",
                ["Scoped:ExcludeChecks"] = "unset-cpu-requirements",
                ["Scoped:IgnorePaths"] = "**/vendor/**",
                ["Scoped:DoNotAutoAddDefaults"] = "true",
                ["Scoped:AddAllBuiltIn"] = "true",
                ["Scoped:Targets"] = "manifests/, deploy/app.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/etc/codeybox/kube-linter.yaml");

        var includes = argv
            .Select((arg, i) => (arg, i))
            .Where(t => t.arg == "--include")
            .Select(t => argv[t.i + 1])
            .ToList();
        Assert.Equal(["latest-tag", "run-as-non-root"], includes);

        var excludeIndex = argv.ToList().IndexOf("--exclude");
        Assert.True(excludeIndex >= 0 && argv[excludeIndex + 1] == "unset-cpu-requirements");

        var ignoreIndex = argv.ToList().IndexOf("--ignore-paths");
        Assert.True(ignoreIndex >= 0 && argv[ignoreIndex + 1] == "**/vendor/**");

        Assert.Contains("--do-not-auto-add-defaults", argv);
        Assert.Contains("--add-all-built-in", argv);

        Assert.DoesNotContain(".", argv);
        Assert.Contains("manifests/", argv);
        Assert.Contains("deploy/app.yaml", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithVendoredPaths, ""));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("deploy/pod.yaml", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithViolations, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "latest-tag",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("latest-tag", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_FlagLikeTarget_IsDeterministicInfrastructure()
    {
        // pflag interleaves flags and positionals, so a dash-leading target
        // would be read as a flag — rejected deterministically instead.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "--verbose",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Targets", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("/repo/deploy")]
    [InlineData("../outside")]
    [InlineData("deploy/../../etc")]
    [InlineData("a\\..\\b.yaml")]
    public async Task RootedOrTraversingTarget_IsRejectedAsDeterministicInfrastructure(string target)
    {
        // Targets keep the repo-relative location contract: a rooted or
        // traversing entry would scan outside the worktree and produce
        // finding paths ExcludePaths cannot match.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = target,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Targets", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData(".kube-linter.yaml")]
    [InlineData(".kube-linter.yml")]
    public async Task RepoKubeLinterConfig_FailsClosed_ScanNeverRuns(string configFile)
    {
        // kube-linter auto-loads a worktree-root .kube-linter.yaml/.yml for
        // check selection — a committed config could exclude every check the
        // diff would violate and still produce a clean audit, so presence
        // fails closed unless the operator opts in.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, configFile + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new KubeLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(configFile, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            KubeLinterAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task TrustedRepositorySuppression_SkipsRepoConfigGate_ScanRuns()
    {
        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + KubeLinterAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        var suppressionProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
            {
                suppressionProbes++;
                return Task.FromResult(new SandboxExecResult(0, ".kube-linter.yaml\n", ""));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.Equal(0, suppressionProbes);
    }

    [Theory]
    [InlineData("/etc/codeybox/kube-linter.yaml")]
    [InlineData("../policy/kube-linter.yaml")] // resolves to /policy/…, outside the "/work" tree
    public async Task PinnedConfigFile_SkipsRepoConfigGate(string configFile)
    {
        // --config disables the worktree-root auto-load, so an
        // operator-pinned config outside the audited tree removes the repo
        // file from check selection — no probe is needed.
        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = configFile,
            }),
            CancellationToken.None);

        var suppressionProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
            {
                suppressionProbes++;
                return Task.FromResult(new SandboxExecResult(0, ".kube-linter.yaml\n", ""));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, suppressionProbes);
        Assert.NotNull(scanExec);
        var configIndex = scanExec!.Argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && scanExec.Argv[configIndex + 1] == configFile);
    }

    [Theory]
    [InlineData("ci/kube-linter.yaml")] // relative → resolves inside "/work"
    [InlineData("/work/deploy/kube-linter.yaml")] // absolute inside "/work"
    public async Task InTreeConfigFile_FailsClosed_AsRepositoryControlled(string configFile)
    {
        // A pinned --config inside the audited tree hands check selection to
        // the repository under audit — repository-controlled by another
        // name, so it fails closed like a worktree-root .kube-linter.yaml.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new KubeLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = configFile,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ConfigFile", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            KubeLinterAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    // Both real-binary runs pin the check set instead of trusting the
    // installed build's default catalogue: the test overrides ExpectedVersion
    // with whatever is installed, so default-catalogue drift across versions
    // cannot turn the clean fixture noisy or the violating fixture quiet.
    private static readonly IReadOnlyDictionary<string, string?> RealBinaryScopedConfig =
        new Dictionary<string, string?>
        {
            ["Scoped:DoNotAutoAddDefaults"] = "true",
            ["Scoped:IncludeChecks"] = "latest-tag, run-as-non-root",
        };

    [Fact]
    [Trait("requires_kube-linter", "true")]
    public async Task RealKubeLinter_ViolatingPodFixture_ProducesFindings_WithRuleIdAndLocation()
    {
        var installed = InstalledKubeLinterVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKubeLinterFixtureRepoAsync(violating: true);

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

            var auditor = new KubeLinterAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>(RealBinaryScopedConfig)
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("latest-tag", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.StartsWith("pod.yaml", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_kube-linter", "true")]
    public async Task RealKubeLinter_CleanPodFixture_Passes()
    {
        var installed = InstalledKubeLinterVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKubeLinterFixtureRepoAsync(violating: false);

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

            var auditor = new KubeLinterAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>(RealBinaryScopedConfig)
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.KubeLinterAuditorPlugin.dll");
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
            PluginId: KubeLinterAuditor.PluginId,
            PluginDisplayName: "CodeyBox: KubeLinter Kubernetes Manifests",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, KubeLinterAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("kube-linter", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "kube-linter" && exec.Argv[1] == "version";

    // The repository-config gate probes the worktree root for
    // .kube-linter.yaml/.kube-linter.yml via the shared presence script.
    private static bool IsSuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && (exec.Argv.Contains(".kube-linter.yaml", StringComparer.Ordinal)
                || exec.Argv.Contains(".kube-linter.yml", StringComparer.Ordinal));

    private static async Task<string> SeedKubeLinterFixtureRepoAsync(bool violating)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-kube-linter-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // kube-linter's default checks are compiled into the binary — no
        // config, schemas, or network needed. An untagged image is a known
        // violation (latest-tag) under the default set.
        var manifest = violating
            ? """
              apiVersion: v1
              kind: Pod
              metadata:
                name: sec-ctx-demo
              spec:
                containers:
                  - name: sec-ctx-demo
                    image: busybox
              """
            : """
              apiVersion: v1
              kind: Pod
              metadata:
                name: clean-demo
              spec:
                securityContext:
                  runAsNonRoot: true
                  seccompProfile:
                    type: RuntimeDefault
                containers:
                  - name: demo
                    image: busybox:1.36
                    securityContext:
                      readOnlyRootFilesystem: true
                      allowPrivilegeEscalation: false
                      capabilities:
                        drop: ["ALL"]
                    resources:
                      requests:
                        cpu: 100m
                        memory: 128Mi
                      limits:
                        cpu: 200m
                        memory: 256Mi
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "pod.yaml"), manifest);

        return dir;
    }

    private static string? ProbeInstalledKubeLinterVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kube-linter",
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
