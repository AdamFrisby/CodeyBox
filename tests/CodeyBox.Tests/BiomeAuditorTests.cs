using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.BiomeAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the biome auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming biome (never a pass or finding).
/// - Exit codes 0 and 1 with a Biome JSON report are verdicts; other exits are infrastructure.
/// - Exit 1 without JSON (biome's usage-failure convention) fails closed as infrastructure.
/// - Biome JSON maps to findings with category rule ids, locations, and mapped severity.
/// - Raw tool severities (error/warning/info/fatal) go through the declared mapping.
/// - Default exclusions (vendored + generated trees) and scoped options (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_biome", "true")].
/// </summary>
public sealed class BiomeAuditorTests
{
    private static readonly string? InstalledBiomeVersion = ProbeInstalledBiomeVersion();

    // Shape captured from biome 2.5.14 `lint --reporter=json` against a
    // fixture with a JavaScript and a TypeScript issue: one error-level
    // diagnostic in src/bad.js, one warning-level diagnostic in src/bad.ts.
    private const string JsonWithJsErrorAndTsWarning = """
        {
          "summary": {"changed": 0, "unchanged": 2, "errors": 1, "warnings": 1, "infos": 0},
          "diagnostics": [
            {
              "severity": "error",
              "message": "Using == may be unsafe if you are relying on type coercion.",
              "category": "lint/suspicious/noDoubleEquals",
              "location": {"path": "src/bad.js", "start": {"line": 2, "column": 10}, "end": {"line": 2, "column": 12}},
              "advices": []
            },
            {
              "severity": "warning",
              "message": "Unexpected any. Specify a different type.",
              "category": "lint/suspicious/noExplicitAny",
              "location": {"path": "src/bad.ts", "start": {"line": 1, "column": 22}, "end": {"line": 1, "column": 25}},
              "advices": []
            }
          ],
          "command": "lint"
        }
        """;

    private const string JsonClean = """
        {
          "summary": {"changed": 0, "unchanged": 1, "errors": 0, "warnings": 0, "infos": 0},
          "diagnostics": [],
          "command": "lint"
        }
        """;

    private const string JsonWithSeveritiesAndExcludedPaths = """
        {
          "summary": {"changed": 0, "unchanged": 5, "errors": 4, "warnings": 2, "infos": 1},
          "diagnostics": [
            {
              "severity": "error",
              "message": "Using == may be unsafe if you are relying on type coercion.",
              "category": "lint/suspicious/noDoubleEquals",
              "location": {"path": "src/app.ts", "start": {"line": 4, "column": 9}, "end": {"line": 4, "column": 11}},
              "advices": []
            },
            {
              "severity": "warning",
              "message": "Unexpected any. Specify a different type.",
              "category": "lint/suspicious/noExplicitAny",
              "location": {"path": "src/app.ts", "start": {"line": 1, "column": 22}, "end": {"line": 1, "column": 25}},
              "advices": []
            },
            {
              "severity": "info",
              "message": "This code style could be improved.",
              "category": "lint/style/noInferrableTypes",
              "location": {"path": "src/app.ts", "start": {"line": 2, "column": 5}, "end": {"line": 2, "column": 9}},
              "advices": []
            },
            {
              "severity": "fatal",
              "message": "Something failed catastrophically.",
              "category": "lint/correctness/noUnknown",
              "location": {"path": "src/app.ts", "start": {"line": 3, "column": 1}, "end": {"line": 3, "column": 5}},
              "advices": []
            },
            {
              "severity": "not-a-biome-level",
              "message": "A diagnostic from a future reporter shape.",
              "category": "lint/suspicious/noFuture",
              "location": {"path": "src/app.ts", "start": {"line": 5, "column": 1}, "end": {"line": 5, "column": 3}},
              "advices": []
            },
            {
              "message": "A diagnostic carrying no severity at all.",
              "category": "lint/suspicious/noSeverity",
              "location": {"path": "src/app.ts", "start": {"line": 6, "column": 1}, "end": {"line": 6, "column": 3}},
              "advices": []
            },
            {
              "severity": "error",
              "message": "Using == may be unsafe if you are relying on type coercion.",
              "category": "lint/suspicious/noDoubleEquals",
              "location": {"path": "vendor/lib.js", "start": {"line": 1, "column": 1}, "end": {"line": 1, "column": 3}},
              "advices": []
            },
            {
              "severity": "error",
              "message": "Using == may be unsafe if you are relying on type coercion.",
              "category": "lint/suspicious/noDoubleEquals",
              "location": {"path": "node_modules/pkg/index.js", "start": {"line": 2, "column": 1}, "end": {"line": 2, "column": 3}},
              "advices": []
            },
            {
              "severity": "error",
              "message": "Using == may be unsafe if you are relying on type coercion.",
              "category": "lint/suspicious/noDoubleEquals",
              "location": {"path": "dist/bundle.js", "start": {"line": 1, "column": 1}, "end": {"line": 1, "column": 3}},
              "advices": []
            }
          ],
          "command": "lint"
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingBiome_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "biome: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("biome", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingBiome()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("biome", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Version: 1.9.4\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("biome", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.9.4", ex.Message, StringComparison.Ordinal);
        Assert.Contains(BiomeAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new BiomeAuditor();
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
    public async Task Fixture_WithJsErrorAndTsWarning_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsErrorAndTsWarning, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var jsFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("noDoubleEquals", StringComparison.Ordinal));
        Assert.Equal("codeybox:biome", jsFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, jsFinding.Severity);
        Assert.Equal("src/bad.js:2", jsFinding.Location);
        Assert.Contains("noDoubleEquals", jsFinding.Description, StringComparison.Ordinal);
        Assert.Contains("Using ==", jsFinding.Description, StringComparison.Ordinal);

        var tsFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("noExplicitAny", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, tsFinding.Severity);
        Assert.Equal("src/bad.ts:1", tsFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("biome", scanExec!.Argv[0]);
        Assert.Equal("lint", scanExec.Argv[1]);
        Assert.Contains("--reporter", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--max-diagnostics", scanExec.Argv);
        Assert.Contains("none", scanExec.Argv);
        Assert.Contains("--no-errors-on-unmatched", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("biome ", StringComparison.Ordinal));
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

        IAuditor auditor = new BiomeAuditor();
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
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsErrorAndTsWarning, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task WarningsOnly_ExitCode0_ReportsAdvisoryFindings_AndPasses()
    {
        const string warningsOnly = """
            {
              "summary": {"changed": 0, "unchanged": 1, "errors": 0, "warnings": 1, "infos": 0},
              "diagnostics": [
                {
                  "severity": "warning",
                  "message": "Unexpected any. Specify a different type.",
                  "category": "lint/suspicious/noExplicitAny",
                  "location": {"path": "src/w.ts", "start": {"line": 1, "column": 22}, "end": {"line": 1, "column": 25}},
                  "advices": []
                }
              ],
              "command": "lint"
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, warningsOnly, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("src/w.ts:1", finding.Location);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // biome's usage-failure convention: text on stderr, nothing JSON on stdout.
            return Task.FromResult(new SandboxExecResult(1, "", "Error: `--bogus-flag` is not expected in this context\n"));
        });

        IAuditor auditor = new BiomeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("biome", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithJsErrorAndTsWarning, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("biome", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "biome: command not found"));
        });

        IAuditor auditor = new BiomeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("biome", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("noDoubleEquals", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("src/app.ts:4", error.Location);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(findings, f => f.Title.Contains("noExplicitAny", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Contains("Severity (tool): warning", warning.Description, StringComparison.Ordinal);

        var info = Assert.Single(findings, f => f.Title.Contains("noInferrableTypes", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Contains("Severity (tool): info", info.Description, StringComparison.Ordinal);

        var fatal = Assert.Single(findings, f => f.Title.Contains("noUnknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, fatal.Severity);
        Assert.Contains("Severity (tool): fatal", fatal.Description, StringComparison.Ordinal);

        var unknown = Assert.Single(findings, f => f.Title.Contains("noFuture", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("noSeverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // declared fallback default

        Assert.All(findings, f => Assert.IsType<AuditSeverity>(f.Severity));
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
            s => s.PluginId == BiomeAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("biome", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresBiomeRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [BiomeAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == BiomeAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("biome", tool.Binary);
        // Verify-only by design: biome ships via npm/standalone, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("biome", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Version: 9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new BiomeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "9.9.9",
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

        var auditor = new BiomeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/biome.operator.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config-path");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/biome.operator.json", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new BiomeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("node_modules/", StringComparison.Ordinal)
                || f.Location.StartsWith("dist/", StringComparison.Ordinal)));
        Assert.Contains(result.Findings, f => f.Location is not null
            && f.Location.StartsWith("src/app.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithJsErrorAndTsWarning, ""));
        });

        var auditor = new BiomeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "lint/suspicious/noExplicitAny",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("noExplicitAny", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArguments_AreStructuredArgv_NotAShellString()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new BiomeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--only=lint/suspicious,--diagnostic-level=warn",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("biome", scanExec!.Argv[0]);
        Assert.Contains("--only=lint/suspicious", scanExec.Argv);
        Assert.Contains("--diagnostic-level=warn", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(" --diagnostic-level", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("requires_biome", "true")]
    public async Task RealBiome_JsAndTsFixture_YieldsFindings_WithRuleIdAndLocation()
    {
        var installed = InstalledBiomeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedBiomeFixtureRepoAsync(clean: false);

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

            var auditor = new BiomeAuditor();
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

            var jsFinding = Assert.Single(result.Findings, f => f.Location == "src/index.js:2");
            Assert.Contains("noDoubleEquals", jsFinding.Title, StringComparison.Ordinal);
            Assert.Equal(AuditSeverity.Error, jsFinding.Severity);

            var tsFinding = Assert.Single(result.Findings, f => f.Location == "src/math.ts:2");
            Assert.Contains("noDoubleEquals", tsFinding.Title, StringComparison.Ordinal);
            Assert.Equal(AuditSeverity.Error, tsFinding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_biome", "true")]
    public async Task RealBiome_CleanFixture_Passes()
    {
        var installed = InstalledBiomeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedBiomeFixtureRepoAsync(clean: true);

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

            var auditor = new BiomeAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.BiomeAuditorPlugin.dll");
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
            PluginId: BiomeAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Biome JavaScript/TypeScript Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Version: " + BiomeAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("biome", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "biome" && exec.Argv[1] == "--version";

    private static async Task<string> SeedBiomeFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-biome-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "good.ts"),
                "export function add(a: number, b: number): number {\n  return a + b;\n}\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "index.js"),
                "export function isOne(x) {\n  return x == 1;\n}\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "math.ts"),
                "export function isTwo(x: number): boolean {\n  return x == 2;\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledBiomeVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "biome",
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
