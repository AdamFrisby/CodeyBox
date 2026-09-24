using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.SpectralAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Spectral auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming spectral (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing), while exit code 2 and others are infrastructure.
/// - Exit code 1 without SARIF output fails closed as an infrastructure failure.
/// - SARIF output maps to findings with rule IDs, locations, and mapped severity.
/// - Raw tool severities are mapped to <see cref="AuditSeverity"/> (never passed through).
/// - Default exclusions (vendor/, third_party/, node_modules/) and options (ruleset, target patterns).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_spectral", "true")].
/// </summary>
public sealed class SpectralAuditorTests
{
    private static readonly string? InstalledSpectralVersion = ProbeInstalledSpectralVersion();

    private const string SarifWithOpenApiAndAsyncApiIssues = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "spectral",
                "semanticVersion": "6.16.3",
                "rules": [
                  { "id": "oas3-schema", "shortDescription": { "text": "Validate structure of OpenAPI v3 specification." } },
                  { "id": "asyncapi-schema", "shortDescription": { "text": "Validate structure of AsyncAPI specification." } }
                ]
              }
            },
            "results": [
              {
                "level": "error",
                "message": { "text": "\"info\" property must have required property \"version\"." },
                "ruleId": "oas3-schema",
                "locations": [{
                  "physicalLocation": {
                    "artifactLocation": { "uri": "apis/openapi.yaml" },
                    "region": { "startLine": 2, "startColumn": 6 }
                  }
                }]
              },
              {
                "level": "error",
                "message": { "text": "\"info\" property must have required property \"version\"." },
                "ruleId": "asyncapi-schema",
                "locations": [{
                  "physicalLocation": {
                    "artifactLocation": { "uri": "events/asyncapi.yaml" },
                    "region": { "startLine": 4, "startColumn": 1 }
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
            "tool": { "driver": { "name": "spectral", "semanticVersion": "6.16.3", "rules": [] } },
            "results": []
          }]
        }
        """;

    private const string SarifWithSeverities = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "spectral", "semanticVersion": "6.16.3", "rules": [] } },
            "results": [
              {
                "level": "error",
                "message": { "text": "Error message" },
                "ruleId": "rule-error",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 1 } } }]
              },
              {
                "level": "warn",
                "message": { "text": "Warn message" },
                "ruleId": "rule-short-warn",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 2 } } }]
              },
              {
                "level": "warning",
                "message": { "text": "Warning message" },
                "ruleId": "rule-full-warning",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 3 } } }]
              },
              {
                "level": "info",
                "message": { "text": "Info message" },
                "ruleId": "rule-short-info",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 4 } } }]
              },
              {
                "level": "information",
                "message": { "text": "Information message" },
                "ruleId": "rule-full-information",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 5 } } }]
              },
              {
                "level": "hint",
                "message": { "text": "Hint message" },
                "ruleId": "rule-hint",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 6 } } }]
              },
              {
                "level": "note",
                "message": { "text": "Note message" },
                "ruleId": "rule-note",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 7 } } }]
              },
              {
                "level": "custom-unknown",
                "message": { "text": "Unknown level message" },
                "ruleId": "rule-unknown",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "spec.yaml" }, "region": { "startLine": 8 } } }]
              }
            ]
          }]
        }
        """;

    private const string SarifWithFilteredPathsAndRules = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "spectral", "semanticVersion": "6.16.3", "rules": [] } },
            "results": [
              {
                "level": "error",
                "message": { "text": "Root API error" },
                "ruleId": "oas3-schema",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "apis/openapi.yaml" }, "region": { "startLine": 10 } } }]
              },
              {
                "level": "error",
                "message": { "text": "Vendor API error" },
                "ruleId": "oas3-schema",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "vendor/api.yaml" }, "region": { "startLine": 15 } } }]
              },
              {
                "level": "error",
                "message": { "text": "Node modules API error" },
                "ruleId": "oas3-schema",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "node_modules/pkg/api.json" }, "region": { "startLine": 20 } } }]
              },
              {
                "level": "error",
                "message": { "text": "AsyncAPI error" },
                "ruleId": "asyncapi-schema",
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "events/asyncapi.yaml" }, "region": { "startLine": 25 } } }]
              }
            ]
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingSpectral_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "spectral: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spectral", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingSpectral()
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

        IAuditor auditor = new SpectralAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spectral", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "6.15.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spectral", ex.Message, StringComparison.Ordinal);
        Assert.Contains("6.15.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SpectralAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, SpectralAuditor.DefaultExpectedVersion + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpectralAuditor();
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
    public async Task Fixture_WithOpenApiAndAsyncApiErrors_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithOpenApiAndAsyncApiIssues, ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var oasFinding = Assert.Single(result.Findings, f => f.Title.Contains("oas3-schema", StringComparison.Ordinal));
        Assert.Equal("codeybox:spectral", oasFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, oasFinding.Severity);
        Assert.Equal("apis/openapi.yaml:2", oasFinding.Location);

        var asyncFinding = Assert.Single(result.Findings, f => f.Title.Contains("asyncapi-schema", StringComparison.Ordinal));
        Assert.Equal("codeybox:spectral", asyncFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, asyncFinding.Severity);
        Assert.Equal("events/asyncapi.yaml:4", asyncFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("spectral", scanExec!.Argv[0]);
        Assert.Equal("lint", scanExec.Argv[1]);
        Assert.Contains("-f", scanExec.Argv);
        Assert.Contains("sarif", scanExec.Argv);
        Assert.Contains("-q", scanExec.Argv);
        Assert.Contains("--ignore-unknown-format", scanExec.Argv);
        Assert.Contains("**/*.{json,yml,yaml}", scanExec.Argv);
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

        IAuditor auditor = new SpectralAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithSarif_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithOpenApiAndAsyncApiIssues, ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Spectral returns exit code 2 on configuration / ruleset error
            return Task.FromResult(new SandboxExecResult(2, "", "No ruleset has been found."));
        });

        IAuditor auditor = new SpectralAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spectral", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutSarif_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 with invalid / usage output rather than SARIF fails closed as infrastructure
            return Task.FromResult(new SandboxExecResult(1, "Unknown argument: --foo\n", ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spectral", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "spectral: command not found"));
        });

        IAuditor auditor = new SpectralAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("spectral", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithSeverities, ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(8, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-short-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-full-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var info = Assert.Single(findings, f => f.Title.Contains("rule-short-info", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);

        var information = Assert.Single(findings, f => f.Title.Contains("rule-full-information", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, information.Severity);

        var hint = Assert.Single(findings, f => f.Title.Contains("rule-hint", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, hint.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("rule-note", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // fallback default
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
            s => s.PluginId == SpectralAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("spectral", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresSpectralRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SpectralAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SpectralAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("spectral", tool.Binary);
        // Verify-only by design: tool is provisioned via npm or binary, so no distro package is specified
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("spectral", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "7.0.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpectralAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "7.0.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_RulesetPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpectralAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:RulesetPath"] = "custom/ruleset.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var rulesetIndex = argv.ToList().IndexOf("--ruleset");
        Assert.True(rulesetIndex >= 0 && rulesetIndex + 1 < argv.Count);
        Assert.Equal("custom/ruleset.yaml", argv[rulesetIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_RulesetKey_Alternative_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpectralAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Ruleset"] = "custom/spectral.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var rulesetIndex = argv.ToList().IndexOf("--ruleset");
        Assert.True(rulesetIndex >= 0 && rulesetIndex + 1 < argv.Count);
        Assert.Equal("custom/spectral.json", argv[rulesetIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_TargetPatterns_OverridesDefaultGlob()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new SpectralAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetPatterns"] = "spec/api.yaml, spec/events.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain("**/*.{json,yml,yaml}", argv);
        Assert.Contains("spec/api.yaml", argv);
        Assert.Contains("spec/events.json", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFilteredPathsAndRules, ""));
        });

        IAuditor auditor = new SpectralAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/api.yaml and node_modules/pkg/api.json must be excluded by default ExcludePaths
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Location == "apis/openapi.yaml:10");
        Assert.Contains(result.Findings, f => f.Location == "events/asyncapi.yaml:25");
        Assert.DoesNotContain(result.Findings, f => f.Location?.StartsWith("vendor/", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Findings, f => f.Location?.StartsWith("node_modules/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFilteredPathsAndRules, ""));
        });

        var auditor = new SpectralAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "asyncapi-schema",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("asyncapi-schema", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_spectral", "true")]
    public async Task RealSpectral_OpenApiAndAsyncApi_Fixtures()
    {
        var installed = InstalledSpectralVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSpectralFixtureRepoAsync(
            openApiValid: false,
            asyncApiValid: false);

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

            var auditor = new SpectralAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:RulesetPath"] = ".spectral.yaml",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);

            var oasFinding = Assert.Single(result.Findings, f => f.Title.Contains("oas3-schema", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, oasFinding.Severity);
            Assert.Equal("openapi.yaml:2", oasFinding.Location);

            var asyncFinding = Assert.Single(result.Findings, f => f.Title.Contains("asyncapi-schema", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, asyncFinding.Severity);
            Assert.Equal("asyncapi.yaml:2", asyncFinding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_spectral", "true")]
    public async Task RealSpectral_CleanOpenApi_Passes()
    {
        var installed = InstalledSpectralVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSpectralFixtureRepoAsync(
            openApiValid: true,
            asyncApiValid: true);

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

            var auditor = new SpectralAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:RulesetPath"] = ".ruleset-clean.yaml",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SpectralAuditorPlugin.dll");
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
            PluginId: SpectralAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Spectral Schema Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, SpectralAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("spectral", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "spectral" && exec.Argv[1] == "--version";

    private static async Task<string> SeedSpectralFixtureRepoAsync(bool openApiValid, bool asyncApiValid)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-spectral-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var defaultRuleset = """
            extends: ["spectral:oas", "spectral:asyncapi"]
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, ".spectral.yaml"), defaultRuleset);

        var cleanRuleset = """
            extends: [["spectral:oas", "off"], ["spectral:asyncapi", "off"]]
            rules:
              oas3-schema: true
              asyncapi-schema: true
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, ".ruleset-clean.yaml"), cleanRuleset);

        var openApiContent = openApiValid
            ? """
              openapi: 3.0.0
              info:
                title: Sample Valid API
                version: 1.0.0
              paths: {}
              """
            : """
              openapi: 3.0.0
              info:
                title: Sample Invalid API
              paths: {}
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "openapi.yaml"), openApiContent);

        if (!asyncApiValid)
        {
            var asyncApiContent = """
                asyncapi: 2.0.0
                info:
                  title: Sample Invalid AsyncAPI
                channels: {}
                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "asyncapi.yaml"), asyncApiContent);
        }

        return dir;
    }

    private static string? ProbeInstalledSpectralVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "spectral",
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
