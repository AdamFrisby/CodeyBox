using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.RegalAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Regal auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming regal (never a pass or finding).
/// - Exit codes 0, 2, and 3 are verdicts (findings-producing), while any other exit is infrastructure.
/// - Findings exits without SARIF output fail closed as infrastructure failures.
/// - SARIF output maps to findings with rule IDs, locations, and mapped severity.
/// - Raw tool severities are mapped to <see cref="AuditSeverity"/> (never passed through).
/// - Malformed and truncated SARIF fail closed as infrastructure.
/// - Default exclusions (vendor/, third_party/, node_modules/) and options (config path, targets).
/// - Read-only posture: --fix is rejected; --format is the auditor's parsing contract.
/// - Cancellation propagates; scan timeouts are infrastructure failures.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_regal", "true")].
/// </summary>
public sealed class RegalAuditorTests
{
    private static readonly string? InstalledRegalVersion = ProbeInstalledRegalVersion();

    private const string SarifWithErrorAndWarning = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "regal",
                "version": "0.40.0",
                "rules": [
                  { "id": "use-assignment-operator", "shortDescription": { "text": "Prefer := over = for assignment" } },
                  { "id": "prefer-snake-case", "shortDescription": { "text": "Prefer snake_case for names" } }
                ]
              }
            },
            "results": [
              {
                "level": "error",
                "message": { "text": "Prefer := over = for assignment" },
                "ruleId": "use-assignment-operator",
                "locations": [{
                  "physicalLocation": {
                    "artifactLocation": { "uri": "policy/authz.rego" },
                    "region": { "startLine": 5, "startColumn": 1 }
                  }
                }]
              },
              {
                "level": "warning",
                "message": { "text": "Prefer snake_case for names" },
                "ruleId": "prefer-snake-case",
                "locations": [{
                  "physicalLocation": {
                    "artifactLocation": { "uri": "policy/authz.rego" },
                    "region": { "startLine": 12, "startColumn": 1 }
                  }
                }]
              }
            ]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "regal", "version": "0.40.0", "rules": [] } },
            "results": []
          }]
        }
        """;

    private const string SarifWithSeverities = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "regal", "version": "0.40.0", "rules": [] } },
            "results": [
              {
                "level": "error",
                "message": { "text": "Error message" },
                "ruleId": "rule-error",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/a.rego" }, "region": { "startLine": 1 } } }]
              },
              {
                "level": "warning",
                "message": { "text": "Warning message" },
                "ruleId": "rule-full-warning",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/a.rego" }, "region": { "startLine": 2 } } }]
              },
              {
                "level": "warn",
                "message": { "text": "Warn message" },
                "ruleId": "rule-short-warn",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/a.rego" }, "region": { "startLine": 3 } } }]
              },
              {
                "level": "note",
                "message": { "text": "Note message" },
                "ruleId": "rule-note",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/a.rego" }, "region": { "startLine": 4 } } }]
              },
              {
                "level": "info",
                "message": { "text": "Info message" },
                "ruleId": "rule-info",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/a.rego" }, "region": { "startLine": 5 } } }]
              },
              {
                "level": "custom-unknown",
                "message": { "text": "Unknown level message" },
                "ruleId": "rule-unknown",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/a.rego" }, "region": { "startLine": 6 } } }]
              }
            ]
          }]
        }
        """;

    private const string SarifWithFilteredPathsAndRules = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "regal", "version": "0.40.0", "rules": [] } },
            "results": [
              {
                "level": "error",
                "message": { "text": "Root policy error" },
                "ruleId": "use-assignment-operator",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "policy/authz.rego" }, "region": { "startLine": 10 } } }]
              },
              {
                "level": "error",
                "message": { "text": "Vendor policy error" },
                "ruleId": "use-assignment-operator",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/policy.rego" }, "region": { "startLine": 15 } } }]
              },
              {
                "level": "error",
                "message": { "text": "Node modules policy error" },
                "ruleId": "use-assignment-operator",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "node_modules/pkg/policy.rego" }, "region": { "startLine": 20 } } }]
              },
              {
                "level": "warning",
                "message": { "text": "Third party style warning" },
                "ruleId": "prefer-snake-case",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "third_party/other.rego" }, "region": { "startLine": 25 } } }]
              }
            ]
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingRegal_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "regal: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingRegal()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Regal version 0.39.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.39.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(RegalAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, RegalAuditor.DefaultExpectedVersion + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
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
    public async Task Fixture_WithErrorAndWarning_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(3, SarifWithErrorAndWarning, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var errorFinding = Assert.Single(result.Findings, f => f.Title.Contains("use-assignment-operator", StringComparison.Ordinal));
        Assert.Equal("codeybox:regal", errorFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
        Assert.Equal("policy/authz.rego:5", errorFinding.Location);

        var warningFinding = Assert.Single(result.Findings, f => f.Title.Contains("prefer-snake-case", StringComparison.Ordinal));
        Assert.Equal("codeybox:regal", warningFinding.AuditorName);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.Equal("policy/authz.rego:12", warningFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("regal", scanExec!.Argv[0]);
        Assert.Equal("lint", scanExec.Argv[1]);
        Assert.Contains("--format", scanExec.Argv);
        Assert.Contains("sarif", scanExec.Argv);
        Assert.Contains(".", scanExec.Argv);
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

        IAuditor auditor = new RegalAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_WithWarningsSarif_ReportsAdvisoryFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithErrorAndWarning, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "prefer-snake-case",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("prefer-snake-case", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ExitCode3_WithErrorsSarif_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithErrorAndWarning, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Regal reserves 0/2/3 for lint outcomes; 1 is a usage or runtime failure.
            return Task.FromResult(new SandboxExecResult(1, "Error: unknown flag: --bogus\n", ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsExit_WithoutSarif_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 3 with usage text rather than SARIF fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(3, "Error: no such file\n", ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "regal: command not found"));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedJsonOutput_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, "this is not JSON {", ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedJsonOutput_IsInfrastructureFailure()
    {
        var truncated = SarifWithErrorAndWarning[..(SarifWithErrorAndWarning.Length / 2)];
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, truncated, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStdout_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, "", ""));
        });

        IAuditor auditor = new RegalAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithSeverities, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-full-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-short-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("rule-note", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var info = Assert.Single(findings, f => f.Title.Contains("rule-info", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // fallback default
    }

    [Fact]
    public async Task MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithErrorAndWarning, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("use-assignment-operator", finding.Title, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task MaxFindings_TruncatesAndLabelsRawOutput()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithErrorAndWarning, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MaxFindings"] = "1",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Contains("truncated", result.RawOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledToken_PropagatesCancellation_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RegalAuditor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure_NamingRegal()
    {
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Ok(exec);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new SandboxExecResult(0, SarifClean, "");
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TimeoutSeconds"] = "1",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("regal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == RegalAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("regal", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresRegalRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [RegalAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == RegalAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("regal", tool.Binary);
        // Verify-only by design: no distro package carries a pinned Regal release
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("regal", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Regal version 1.0.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.0.0",
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
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/etc/codeybox/regal-config.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config-file");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/etc/codeybox/regal-config.yaml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_OverridesDefaultDot()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "policy/, bundles/authz",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain(".", argv);
        Assert.Contains("policy/", argv);
        Assert.Contains("bundles/authz", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_AbsoluteTarget_IsDeterministicInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "/etc/passwd",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("repo-relative", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithFilteredPathsAndRules, ""));
        });

        IAuditor auditor = new RegalAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, and third_party/ findings must be excluded by default ExcludePaths
        var finding = Assert.Single(result.Findings);
        Assert.Equal("policy/authz.rego:10", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithErrorAndWarning, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "prefer-snake-case",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("prefer-snake-case", finding.Title, StringComparison.Ordinal);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ExtraArguments_FormatFlag_IsDeterministicInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--format, pretty",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--format", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExtraArguments_FixFlag_IsDeterministicInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RegalAuditor();
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
    [Trait("requires_regal", "true")]
    public async Task RealRegal_PolicyWithViolations_ReportsFindings()
    {
        var installed = InstalledRegalVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRegalFixtureRepoAsync(valid: false);

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

            var auditor = new RegalAuditor();
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

            var assignment = Assert.Single(
                result.Findings,
                f => f.Title.Contains("use-assignment-operator", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, assignment.Severity);
            Assert.Equal("policy/authz.rego:5", assignment.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_regal", "true")]
    public async Task RealRegal_CleanPolicy_Passes()
    {
        var installed = InstalledRegalVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRegalFixtureRepoAsync(valid: true);

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

            var auditor = new RegalAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.RegalAuditorPlugin.dll");
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
            PluginId: RegalAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Regal Rego Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Regal " + RegalAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("regal", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "regal" && exec.Argv[1] == "version";

    private static async Task<string> SeedRegalFixtureRepoAsync(bool valid)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-regal-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "policy"));

        var content = valid
            ? """
              package authz

              import rego.v1

              default allow := false

              allow if {
                  input.user.is_admin
              }
              """
            : """
              package authz

              import rego.v1

              default allow = false

              allow if {
                  input.user.is_admin
              }
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "policy", "authz.rego"), content);

        return dir;
    }

    private static string? ProbeInstalledRegalVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "regal",
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
