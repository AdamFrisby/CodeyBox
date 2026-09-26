using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.KnipAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the knip auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming knip (never a pass or finding).
/// - Exit codes 0 and 1 with a knip JSON report are verdicts; exit 2 and others are infrastructure.
/// - Exit 1 without JSON (knip's parseArgs usage-failure convention) fails closed as infrastructure.
/// - knip JSON maps to findings with issue-type rule ids, locations, and mapped severity.
/// - Raw tool severities (error/warn/high) go through the declared mapping.
/// - Default exclusions (vendored + generated trees) and scoped options (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_knip", "true")].
/// </summary>
public sealed class KnipAuditorTests
{
    private static readonly string? InstalledKnipVersion = ProbeInstalledKnipVersion();

    // Captured from knip 6.38.0 `--reporter json --include files,exports,dependencies`
    // against a fixture with an unused file, unused export, and unused dependency.
    private const string JsonWithUnusedFileExportAndDependency = """
        {
          "issues": [
            {
              "file": "src/orphaned.js",
              "dependencies": [],
              "devDependencies": [],
              "exports": [],
              "files": [{ "name": "src/orphaned.js" }],
              "optionalPeerDependencies": []
            },
            {
              "file": "package.json",
              "dependencies": [{ "name": "left-pad", "line": 7, "col": 6, "pos": 122 }],
              "devDependencies": [],
              "exports": [],
              "files": [],
              "optionalPeerDependencies": []
            },
            {
              "file": "src/math.js",
              "dependencies": [],
              "devDependencies": [],
              "exports": [{ "name": "unusedExport", "line": 4, "col": 17, "pos": 55 }],
              "files": [],
              "optionalPeerDependencies": []
            }
          ]
        }
        """;

    private const string JsonClean = """
        { "issues": [] }
        """;

    private const string JsonWithSeveritiesAndExcludedPaths = """
        {
          "issues": [
            {
              "file": "src/app.js",
              "files": [],
              "exports": [
                { "name": "rule-error", "line": 1, "col": 1 },
                { "name": "rule-warn", "line": 2, "col": 1, "severity": "warn" },
                { "name": "rule-high", "line": 3, "col": 1, "severity": "high" },
                { "name": "rule-unknown", "line": 4, "col": 1, "severity": "not-a-knip-level" },
                { "name": "rule-noseverity", "line": 5, "col": 1 }
              ],
              "dependencies": [],
              "cycles": [[{ "name": "src/a.js" }, { "name": "src/b.js" }]]
            },
            {
              "file": "vendor/lib.js",
              "files": [{ "name": "vendor/lib.js" }],
              "exports": [],
              "dependencies": []
            },
            {
              "file": "node_modules/pkg/index.js",
              "exports": [{ "name": "unusedFromPkg", "line": 2, "col": 1 }],
              "files": [],
              "dependencies": []
            },
            {
              "file": "dist/bundle.js",
              "files": [{ "name": "dist/bundle.js" }],
              "exports": [],
              "dependencies": []
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingKnip_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "knip: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KnipAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("knip", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingKnip()
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

        IAuditor auditor = new KnipAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("knip", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "v5.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KnipAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("knip", ex.Message, StringComparison.Ordinal);
        Assert.Contains("5.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(KnipAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new KnipAuditor();
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
    public async Task Fixture_WithUnusedFileExportAndDependency_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithUnusedFileExportAndDependency, ""));
        });

        IAuditor auditor = new KnipAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var fileFinding = Assert.Single(result.Findings, f => f.Title.Contains("files", StringComparison.Ordinal));
        Assert.Equal("codeybox:knip", fileFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, fileFinding.Severity);
        Assert.Equal("src/orphaned.js", fileFinding.Location);
        Assert.Contains("files", fileFinding.Description, StringComparison.Ordinal);
        Assert.Contains("Unused file", fileFinding.Description, StringComparison.Ordinal);

        var depFinding = Assert.Single(result.Findings, f => f.Title.Contains("dependencies", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, depFinding.Severity);
        Assert.Equal("package.json:7", depFinding.Location);
        Assert.Contains("left-pad", depFinding.Title, StringComparison.Ordinal);

        var exportFinding = Assert.Single(result.Findings, f => f.Title.Contains("exports", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, exportFinding.Severity);
        Assert.Equal("src/math.js:4", exportFinding.Location);
        Assert.Contains("unusedExport", exportFinding.Title, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("knip", scanExec!.Argv[0]);
        Assert.Contains("--reporter", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--no-progress", scanExec.Argv);
        Assert.Contains("--include", scanExec.Argv);
        Assert.Contains(KnipAuditor.DefaultIncludeIssueTypes, scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("knip ", StringComparison.Ordinal));
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

        IAuditor auditor = new KnipAuditor();
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
            return Task.FromResult(new SandboxExecResult(1, JsonWithUnusedFileExportAndDependency, ""));
        });

        IAuditor auditor = new KnipAuditor();
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
            // knip exits 2 when it cannot run, e.g. no package.json in the repo.
            return Task.FromResult(new SandboxExecResult(2, "Run `knip --help` or visit https://knip.dev for help\n", "ERROR: Unable to find package.json\n"));
        });

        IAuditor auditor = new KnipAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("knip", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // knip's parseArgs catch exits 1 with help text, not a JSON report.
            return Task.FromResult(new SandboxExecResult(1, "Usage: knip [options]\n", "Unknown option '--bogus'\n"));
        });

        IAuditor auditor = new KnipAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("knip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "knip: command not found"));
        });

        IAuditor auditor = new KnipAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("knip", ex.Message, StringComparison.Ordinal);
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

        IAuditor auditor = new KnipAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);
        Assert.Contains("Severity (tool): warn", warn.Description, StringComparison.Ordinal);

        var high = Assert.Single(findings, f => f.Title.Contains("rule-high", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, high.Severity);
        Assert.Contains("Severity (tool): high", high.Description, StringComparison.Ordinal);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-noseverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, missing.Severity); // exports default to knip's "error" rule

        var cycle = Assert.Single(findings, f => f.Title.Contains("cycles", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, cycle.Severity); // knip's documented default for cycles
        Assert.Contains("Severity (tool): warn", cycle.Description, StringComparison.Ordinal);

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
            s => s.PluginId == KnipAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("knip", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresKnipRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [KnipAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == KnipAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("knip", tool.Binary);
        // Verify-only by design: knip ships via npm, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("knip", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "v9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KnipAuditor();
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

        var auditor = new KnipAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/knip.operator.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/knip.operator.json", argv[configIndex + 1]);
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

        IAuditor auditor = new KnipAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("node_modules/", StringComparison.Ordinal)
                || f.Location.StartsWith("dist/", StringComparison.Ordinal)));
        Assert.Contains(result.Findings, f => f.Location is not null
            && f.Location.StartsWith("src/app.js", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithUnusedFileExportAndDependency, ""));
        });

        var auditor = new KnipAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "exports",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("exports", finding.Title, StringComparison.Ordinal);
        Assert.Contains("unusedExport", finding.Title, StringComparison.Ordinal);
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

        var auditor = new KnipAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--include-entry-exports,--production",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("knip", scanExec!.Argv[0]);
        Assert.Contains("--include-entry-exports", scanExec.Argv);
        Assert.Contains("--production", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(" --production", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("requires_knip", "true")]
    public async Task RealKnip_UnusedFileExportAndDependencyFixture_YieldsFindings()
    {
        var installed = InstalledKnipVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKnipFixtureRepoAsync(clean: false);

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

            var auditor = new KnipAuditor();
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

            var fileFinding = Assert.Single(result.Findings, f => f.Title.Contains("files", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, fileFinding.Severity);
            Assert.Equal("src/orphaned.js", fileFinding.Location);

            var exportFinding = Assert.Single(result.Findings, f => f.Title.Contains("exports", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, exportFinding.Severity);
            Assert.Equal("src/math.js:4", exportFinding.Location);

            var depFinding = Assert.Single(result.Findings, f => f.Title.Contains("dependencies", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, depFinding.Severity);
            Assert.Equal("package.json:7", depFinding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_knip", "true")]
    public async Task RealKnip_CleanFixture_Passes()
    {
        var installed = InstalledKnipVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKnipFixtureRepoAsync(clean: true);

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

            var auditor = new KnipAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.KnipAuditorPlugin.dll");
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
            PluginId: KnipAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Knip Unused JS/TS Files, Exports and Dependencies",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, KnipAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("knip", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "knip" && exec.Argv[1] == "--version";

    private static async Task<string> SeedKnipFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-knip-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "package.json"), """
                {
                  "name": "clean-fixture",
                  "version": "1.0.0",
                  "type": "module",
                  "main": "src/index.js"
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "src", "index.js"), "export function used() { return 1; }\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "package.json"), """
                {
                  "name": "dirty-fixture",
                  "version": "1.0.0",
                  "type": "module",
                  "main": "src/index.js",
                  "dependencies": {
                    "left-pad": "^1.3.0"
                  }
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "src", "index.js"), "import { used } from './math.js';\nused();\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "math.js"),
                "export function used() {\n  return 1;\n}\nexport function unusedExport() {\n  return 2;\n}\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "orphaned.js"),
                "export function orphan() {\n  return 3;\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledKnipVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "knip",
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
