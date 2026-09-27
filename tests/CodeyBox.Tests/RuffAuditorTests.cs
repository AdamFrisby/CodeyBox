using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.RuffAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Ruff auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming ruff (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
/// - Exit 1 without SARIF output fails closed as an infrastructure failure.
/// - Ruff SARIF output maps to findings with rule ids, locations, and mapped severity;
///   absolute file:// artifact URIs are preserved scheme-stripped (see the absolute-location test).
/// - Raw tool severities go through the declared mapping (ruff 0.14.7 reports every
///   diagnostic at "error"; the map also covers warning/note-family levels).
/// - Default exclusions, --ignore-noqa suppression posture, and scoped options
///   (ExpectedVersion, ConfigPath, TrustRepositorySuppression).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_ruff", "true")].
/// </summary>
public sealed class RuffAuditorTests
{
    private static readonly string? InstalledRuffVersion = ProbeInstalledRuffVersion();

    // Shape mirrors real `ruff check --output-format sarif` (0.14.7):
    // absolute file:// artifact URIs, per-result level/message/region.
    private const string SarifWithFindings = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "ruff", "version": "0.14.7", "informationUri": "https://github.com/astral-sh/ruff", "rules": [] } },
              "results": [
                {
                  "ruleId": "F401",
                  "level": "error",
                  "message": { "text": "`os` imported but unused" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "file:///work/src/app.py" },
                        "region": { "startLine": 1, "startColumn": 8, "endLine": 1, "endColumn": 10 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "invalid-syntax",
                  "level": "error",
                  "message": { "text": "Expected a parameter or the end of the parameter list" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "file:///work/src/broken.py" },
                        "region": { "startLine": 2, "startColumn": 12, "endLine": 2, "endColumn": 13 }
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
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "ruff", "version": "0.14.7", "informationUri": "https://github.com/astral-sh/ruff", "rules": [] } },
              "results": []
            }
          ]
        }
        """;

    // Relative artifact URIs (as other SARIF emitters produce) to exercise the
    // finding-level ExcludePaths mechanism through this auditor.
    private const string SarifWithRelativePaths = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "ruff", "version": "0.14.7", "rules": [] } },
              "results": [
                {
                  "ruleId": "F401",
                  "level": "error",
                  "message": { "text": "`os` imported but unused" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/app.py" },
                        "region": { "startLine": 3, "startColumn": 8 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "F401",
                  "level": "error",
                  "message": { "text": "`sys` imported but unused" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "vendor/lib.py" },
                        "region": { "startLine": 1, "startColumn": 8 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifWithLevels = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "ruff", "version": "0.14.7", "rules": [] } },
              "results": [
                {
                  "ruleId": "rule-error",
                  "level": "error",
                  "message": { "text": "Error-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.py" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "rule-warning",
                  "level": "warning",
                  "message": { "text": "Warning-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.py" }, "region": { "startLine": 2 } } }]
                },
                {
                  "ruleId": "rule-note",
                  "level": "note",
                  "message": { "text": "Note-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.py" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "rule-unknown",
                  "level": "blocker",
                  "message": { "text": "Unrecognised level." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.py" }, "region": { "startLine": 4 } } }]
                },
                {
                  "ruleId": "rule-nolevel",
                  "message": { "text": "No level field." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.py" }, "region": { "startLine": 5 } } }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingRuff_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "ruff: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ruff", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingRuff()
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

        IAuditor auditor = new RuffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ruff", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "ruff 0.13.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ruff", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var unused = Assert.Single(result.Findings, f => f.Title.Contains("F401", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, unused.Severity);
        Assert.EndsWith("src/app.py:1", unused.Location, StringComparison.Ordinal);

        var syntax = Assert.Single(result.Findings, f => f.Title.Contains("invalid-syntax", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, syntax.Severity);
        Assert.EndsWith("src/broken.py:2", syntax.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsoluteArtifactUris_ArePreservedSchemeStripped_NotRelativized()
    {
        // Ruff emits absolute file:// URIs with no scan-root metadata, so the
        // shared SARIF parser preserves the sandbox-absolute path. This test
        // pins that behavior: locations stay usable, but repo-relative
        // ExcludePaths prefixes cannot match them (see the plugin README).
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings, f => f.Title.Contains("F401", StringComparison.Ordinal));
        Assert.Equal("work/src/app.py:1", finding.Location);
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

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithFindings_StillFails_ExitZeroCannotSilenceGate()
    {
        // An operator --exit-zero turns ruff's exit to 0 but the SARIF report
        // still carries the diagnostics: findings still fail the audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithSarif_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithRelativePaths, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        // The src/ finding surfaces; the vendor/ one is dropped by the
        // default ExcludePaths (covered in depth by ScopedConfiguration_ExcludePaths_FiltersVendoredFindings).
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/app.py:3", finding.Location);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "error: invalid value '--bogus' for '--output-format <OUTPUT_FORMAT>'"));
        });

        IAuditor auditor = new RuffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ruff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutSarif_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "some usage text, not a report"));
        });

        IAuditor auditor = new RuffAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "ruff: command not found"));
        });

        IAuditor auditor = new RuffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ruff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithLevels, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(5, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("rule-note", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-nolevel", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> SARIF "warning" -> Warning
    }

    [Fact]
    public async Task DefaultArguments_RunSarifCheck_NoCache_IgnoreNoqa()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new RuffAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("ruff", argv[0]);
        Assert.Contains("check", argv);
        var formatIndex = argv.ToList().IndexOf("--output-format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("sarif", argv[formatIndex + 1]);
        Assert.Contains("--no-cache", argv);
        Assert.Contains("--ignore-noqa", argv);
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
            s => s.PluginId == RuffAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("ruff", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresRuffRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [RuffAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == RuffAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("ruff", tool.Binary);
        // Verify-only by design: ruff ships via pip/standalone binary; no
        // distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("ruff", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "ruff 0.13.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RuffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.13.0",
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

        var auditor = new RuffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/ruff.operator.toml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/ruff.operator.toml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithRelativePaths, ""));
        });

        IAuditor auditor = new RuffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ finding is dropped by the default ExcludePaths; the
        // src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/app.py:3", finding.Location);
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

        var auditor = new RuffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "invalid-syntax",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("invalid-syntax", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_RemovesIgnoreNoqa()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new RuffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--ignore-noqa", scanExec!.Argv);
    }

    [Fact]
    [Trait("requires_ruff", "true")]
    public async Task RealRuff_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledRuffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRuffFixtureRepoAsync(clean: false);

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

            var auditor = new RuffAuditor();
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

            var unused = Assert.Single(result.Findings, f => f.Title.Contains("F401", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, unused.Severity);
            Assert.EndsWith("bad.py:1", unused.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_ruff", "true")]
    public async Task RealRuff_CleanFixture_Passes_AndWritesNoCache()
    {
        var installed = InstalledRuffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRuffFixtureRepoAsync(clean: true);

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

            var auditor = new RuffAuditor();
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
            Assert.False(Directory.Exists(Path.Combine(fixtureDir, ".ruff_cache")));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_ruff", "true")]
    public async Task RealRuff_NoqaComment_SurfacesAsFinding_ByDefault()
    {
        // The default scan passes --ignore-noqa: a violation the fixture
        // silences with `# noqa: F401` still surfaces as a finding.
        var installed = InstalledRuffVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(
            Path.GetTempPath(), "codeybox-ruff-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixtureDir, "noqa.py"), "import os  # noqa: F401\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new RuffAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings, f => f.Title.Contains("F401", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.RuffAuditorPlugin.dll");
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
            PluginId: RuffAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Ruff Python Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "ruff " + RuffAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("ruff", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "ruff" && exec.Argv[1] == "--version";

    private static async Task<string> SeedRuffFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-ruff-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.py"), "print(\"hello\")\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.py"), "import os\n");
        }

        return dir;
    }

    private static string? ProbeInstalledRuffVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ruff",
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