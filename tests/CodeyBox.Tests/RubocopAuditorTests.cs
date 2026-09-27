using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.RubocopAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the RuboCop auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming rubocop (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
/// - Exit 1 without JSON output fails closed as an infrastructure failure.
/// - RuboCop JSON output maps to findings with cop names, locations, and mapped severity.
/// - Raw tool severities go through the declared mapping (error/fatal fail; warning advises;
///   convention/refactor/info are informational).
/// - Default exclusions, --ignore-disable-comments suppression posture, and scoped options
///   (ExpectedVersion, ConfigPath, TrustRepositorySuppression).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_rubocop", "true")].
/// </summary>
public sealed class RubocopAuditorTests
{
    private static readonly string? InstalledRubocopVersion = ProbeInstalledRubocopVersion();

    // Shape mirrors real `rubocop --format json` (see lib/rubocop/formatter/json_formatter.rb):
    // files[].path plus per-offense severity/message/cop_name/location.
    private const string JsonWithFindings = """
        {
          "metadata": {
            "rubocop_version": "1.91.0",
            "ruby_engine": "ruby",
            "ruby_version": "3.4.0",
            "ruby_patchlevel": "0",
            "ruby_platform": "x86_64-linux"
          },
          "files": [
            {
              "path": "lib/app.rb",
              "offenses": [
                {
                  "severity": "convention",
                  "message": "Missing frozen string literal comment.",
                  "cop_name": "Style/FrozenStringLiteralComment",
                  "corrected": false,
                  "correctable": true,
                  "location": { "start_line": 1, "start_column": 1, "last_line": 1, "last_column": 1, "length": 0, "line": 1, "column": 1 }
                },
                {
                  "severity": "error",
                  "message": "Useless assignment to variable `x`.",
                  "cop_name": "Lint/UselessAssignment",
                  "corrected": false,
                  "correctable": false,
                  "location": { "start_line": 3, "start_column": 3, "last_line": 3, "last_column": 4, "length": 1, "line": 3, "column": 3 }
                }
              ]
            }
          ],
          "summary": { "offense_count": 2, "target_file_count": 1, "inspected_file_count": 1 }
        }
        """;

    private const string JsonClean = """
        {
          "metadata": {
            "rubocop_version": "1.91.0",
            "ruby_engine": "ruby",
            "ruby_version": "3.4.0",
            "ruby_patchlevel": "0",
            "ruby_platform": "x86_64-linux"
          },
          "files": [
            { "path": "lib/app.rb", "offenses": [] }
          ],
          "summary": { "offense_count": 0, "target_file_count": 1, "inspected_file_count": 1 }
        }
        """;

    private const string JsonConventionOnly = """
        {
          "metadata": { "rubocop_version": "1.91.0" },
          "files": [
            {
              "path": "lib/app.rb",
              "offenses": [
                {
                  "severity": "convention",
                  "message": "Prefer single-quoted strings.",
                  "cop_name": "Style/StringLiterals",
                  "corrected": false,
                  "correctable": true,
                  "location": { "start_line": 2, "start_column": 6, "last_line": 2, "last_column": 13, "length": 7, "line": 2, "column": 6 }
                }
              ]
            },
            {
              "path": "vendor/lib.rb",
              "offenses": [
                {
                  "severity": "convention",
                  "message": "Missing frozen string literal comment.",
                  "cop_name": "Style/FrozenStringLiteralComment",
                  "corrected": false,
                  "correctable": true,
                  "location": { "start_line": 1, "start_column": 1, "last_line": 1, "last_column": 1, "length": 0, "line": 1, "column": 1 }
                }
              ]
            }
          ],
          "summary": { "offense_count": 2, "target_file_count": 2, "inspected_file_count": 2 }
        }
        """;

    private const string JsonWithSeverities = """
        {
          "metadata": { "rubocop_version": "1.91.0" },
          "files": [
            {
              "path": "a.rb",
              "offenses": [
                { "severity": "fatal", "message": "Fatal-level offense.", "cop_name": "Lint/Syntax", "corrected": false, "correctable": false, "location": { "start_line": 1, "start_column": 1, "last_line": 1, "last_column": 2, "length": 1, "line": 1, "column": 1 } },
                { "severity": "error", "message": "Error-level offense.", "cop_name": "cop-error", "corrected": false, "correctable": false, "location": { "start_line": 2, "start_column": 1, "last_line": 2, "last_column": 2, "length": 1, "line": 2, "column": 1 } },
                { "severity": "warning", "message": "Warning-level offense.", "cop_name": "cop-warning", "corrected": false, "correctable": false, "location": { "start_line": 3, "start_column": 1, "last_line": 3, "last_column": 2, "length": 1, "line": 3, "column": 1 } },
                { "severity": "convention", "message": "Convention-level offense.", "cop_name": "cop-convention", "corrected": false, "correctable": false, "location": { "start_line": 4, "start_column": 1, "last_line": 4, "last_column": 2, "length": 1, "line": 4, "column": 1 } },
                { "severity": "refactor", "message": "Refactor-level offense.", "cop_name": "cop-refactor", "corrected": false, "correctable": false, "location": { "start_line": 5, "start_column": 1, "last_line": 5, "last_column": 2, "length": 1, "line": 5, "column": 1 } },
                { "severity": "info", "message": "Info-level offense.", "cop_name": "cop-info", "corrected": false, "correctable": false, "location": { "start_line": 6, "start_column": 1, "last_line": 6, "last_column": 2, "length": 1, "line": 6, "column": 1 } },
                { "severity": "blocker", "message": "Unrecognised level.", "cop_name": "cop-unknown", "corrected": false, "correctable": false, "location": { "start_line": 7, "start_column": 1, "last_line": 7, "last_column": 2, "length": 1, "line": 7, "column": 1 } },
                { "message": "No severity field.", "cop_name": "cop-noseverity", "corrected": false, "correctable": false, "location": { "start_line": 8, "start_column": 1, "last_line": 8, "last_column": 2, "length": 1, "line": 8, "column": 1 } }
              ]
            }
          ],
          "summary": { "offense_count": 8, "target_file_count": 1, "inspected_file_count": 1 }
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingRubocop_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "rubocop: command not found"));
            Assert.Fail("Scan must not execute when the binary is missing.");
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("rubocop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingRubocop()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "boom"));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("rubocop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.0.0\n", ""));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("rubocop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var style = Assert.Single(
            result.Findings, f => f.Title.Contains("Style/FrozenStringLiteralComment", StringComparison.Ordinal));
        Assert.Equal("lib/app.rb:1", style.Location);
        Assert.Equal(AuditSeverity.Info, style.Severity);

        var lint = Assert.Single(
            result.Findings, f => f.Title.Contains("Lint/UselessAssignment", StringComparison.Ordinal));
        Assert.Equal("lib/app.rb:3", lint.Location);
        Assert.Equal(AuditSeverity.Error, lint.Severity);
    }

    [Fact]
    public async Task ConventionOnly_FindingsAreAdvisory_AndPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonConventionOnly, ""));
        });

        // Default ExcludePaths drops the vendor/ finding; the lib/ finding
        // survives as Info and the run passes: the gate is severity-driven,
        // not blocking on every finding.
        IAuditor auditor = new RubocopAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("Style/StringLiterals", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        IAuditor auditor = new RubocopAuditor();
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
            return Task.FromResult(new SandboxExecResult(2, "", "Error: unrecognized cop Lint/NoSuchCop found in .rubocop.yml"));
        });

        IAuditor auditor = new RubocopAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("rubocop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", ""));
        });

        IAuditor auditor = new RubocopAuditor();
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
            return Task.FromResult(new SandboxExecResult(127, "", "rubocop: command not found"));
        });

        IAuditor auditor = new RubocopAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeverities, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(8, findings.Count);

        var fatal = Assert.Single(findings, f => f.Title.Contains("Lint/Syntax", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, fatal.Severity);

        var error = Assert.Single(findings, f => f.Title.Contains("cop-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("cop-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var convention = Assert.Single(findings, f => f.Title.Contains("cop-convention", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, convention.Severity);

        var refactor = Assert.Single(findings, f => f.Title.Contains("cop-refactor", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, refactor.Severity);

        var info = Assert.Single(findings, f => f.Title.Contains("cop-info", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("cop-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("cop-noseverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> declared default
    }

    [Fact]
    public async Task DefaultArguments_RunJsonFormat_CacheFalse_IgnoreDisableComments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("rubocop", argv[0]);
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("json", argv[formatIndex + 1]);
        var cacheIndex = argv.ToList().IndexOf("--cache");
        Assert.True(cacheIndex >= 0 && cacheIndex + 1 < argv.Count);
        Assert.Equal("false", argv[cacheIndex + 1]);
        Assert.Contains("--ignore-disable-comments", argv);
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
            s => s.PluginId == RubocopAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("rubocop", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresRubocopRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [RubocopAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == RubocopAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("rubocop", tool.Binary);
        // Verify-only by design: rubocop ships as a gem; no distro apt
        // package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("rubocop", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.80.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new RubocopAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.80.0",
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
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new RubocopAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/rubocop.operator.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/rubocop.operator.yml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonConventionOnly, ""));
        });

        IAuditor auditor = new RubocopAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ finding is dropped by the default ExcludePaths; the
        // lib/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("lib/app.rb:2", finding.Location);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        var auditor = new RubocopAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "Lint/UselessAssignment",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("Lint/UselessAssignment", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_RemovesIgnoreDisableComments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new RubocopAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--ignore-disable-comments", scanExec!.Argv);
    }

    [Fact]
    [Trait("requires_rubocop", "true")]
    public async Task RealRubocop_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledRubocopVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRubocopFixtureRepoAsync(clean: false);

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

            var auditor = new RubocopAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // Style findings are advisory: the run reports them without failing.
            Assert.True(result.Passed);
            Assert.NotEmpty(result.Findings);

            var frozen = Assert.Single(
                result.Findings,
                f => f.Title.Contains("Style/FrozenStringLiteralComment", StringComparison.Ordinal));
            Assert.EndsWith("bad.rb:1", frozen.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_rubocop", "true")]
    public async Task RealRubocop_CleanFixture_Passes()
    {
        var installed = InstalledRubocopVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRubocopFixtureRepoAsync(clean: true);

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

            var auditor = new RubocopAuditor();
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

    [Fact]
    [Trait("requires_rubocop", "true")]
    public async Task RealRubocop_DisableComment_SurfacesAsFinding_ByDefault()
    {
        // The default scan passes --ignore-disable-comments: an offense the
        // fixture silences with `# rubocop:disable` still surfaces.
        var installed = InstalledRubocopVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(
            Path.GetTempPath(), "codeybox-rubocop-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDir, "disabled.rb"),
                "puts \"hello\" # rubocop:disable Style/StringLiterals,Style/FrozenStringLiteralComment\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new RubocopAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.NotEmpty(result.Findings);
            Assert.Contains(
                result.Findings,
                f => f.Title.Contains("Style/FrozenStringLiteralComment", StringComparison.Ordinal)
                    || f.Title.Contains("Style/StringLiterals", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.RubocopAuditorPlugin.dll");
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
            PluginId: RubocopAuditor.PluginId,
            PluginDisplayName: "CodeyBox: RuboCop Ruby Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, RubocopAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("rubocop", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "rubocop" && exec.Argv[1] == "--version";

    private static async Task<string> SeedRubocopFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-rubocop-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.rb"), "# frozen_string_literal: true\nputs 'hello'\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.rb"), "puts \"hello\"\n");
        }

        return dir;
    }

    private static string? ProbeInstalledRubocopVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rubocop",
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
