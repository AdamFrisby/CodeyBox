using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.DepcruiseAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the depcruise auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming depcruise (never a pass or finding).
/// - Exit codes 0 and 1 with a dependency-cruiser JSON report are verdicts; other exits are infrastructure.
/// - Exit 1 without JSON (missing config, unreadable target, usage error) fails closed as infrastructure.
/// - dependency-cruiser JSON maps to findings with rule-name ids, from-module locations, and mapped severity.
/// - Raw tool severities (error/warn/info/ignore) go through the declared mapping.
/// - Default exclusions (vendored + generated trees) and scoped options (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_depcruise", "true")].
/// </summary>
public sealed class DepcruiseAuditorTests
{
    private static readonly string? InstalledDepcruiseVersion = ProbeInstalledDepcruiseVersion();

    // Captured from depcruise 18.4.0 `--output-type json --progress none .`
    // against a fixture with a forbidden dependency and a circular dependency.
    private const string JsonWithErrorAndCycleViolations = """
        {
          "modules": [],
          "summary": {
            "violations": [
              {
                "type": "dependency",
                "from": "src/b.js",
                "to": "src/a.js",
                "rule": { "severity": "error", "name": "no-src-b-to-a" }
              },
              {
                "type": "cycle",
                "from": "src/circ-a.js",
                "to": "src/circ-b.js",
                "rule": { "severity": "warn", "name": "no-circular" },
                "cycle": [{ "name": "src/circ-b.js" }, { "name": "src/circ-a.js" }]
              }
            ],
            "error": 1,
            "warn": 1,
            "info": 0,
            "ignore": 0,
            "totalCruised": 4,
            "totalDependenciesCruised": 3
          }
        }
        """;

    private const string JsonClean = """
        {
          "modules": [],
          "summary": {
            "violations": [],
            "error": 0,
            "warn": 0,
            "info": 0,
            "ignore": 0,
            "totalCruised": 2,
            "totalDependenciesCruised": 1
          }
        }
        """;

    private const string JsonWithSeveritiesAndExcludedPaths = """
        {
          "modules": [],
          "summary": {
            "violations": [
              {
                "type": "dependency",
                "from": "src/app.js",
                "to": "src/lib.js",
                "rule": { "severity": "error", "name": "rule-error" }
              },
              {
                "type": "dependency",
                "from": "src/app.js",
                "to": "src/lib.js",
                "rule": { "severity": "warn", "name": "rule-warn" }
              },
              {
                "type": "dependency",
                "from": "src/app.js",
                "to": "src/lib.js",
                "rule": { "severity": "info", "name": "rule-info" }
              },
              {
                "type": "dependency",
                "from": "src/app.js",
                "to": "src/lib.js",
                "rule": { "severity": "ignore", "name": "rule-ignore" }
              },
              {
                "type": "dependency",
                "from": "src/app.js",
                "to": "src/lib.js",
                "rule": { "severity": "not-a-depcruise-level", "name": "rule-unknown" }
              },
              {
                "type": "module",
                "from": "src/lonely.js",
                "to": "src/lonely.js",
                "rule": { "severity": "error", "name": "not-an-orphan" }
              },
              {
                "type": "dependency",
                "from": "vendor/lib.js",
                "to": "vendor/other.js",
                "rule": { "severity": "error", "name": "rule-vendored" }
              },
              {
                "type": "dependency",
                "from": "node_modules/pkg/index.js",
                "to": "node_modules/pkg/other.js",
                "rule": { "severity": "error", "name": "rule-bundled" }
              },
              {
                "type": "dependency",
                "from": "dist/bundle.js",
                "to": "dist/chunk.js",
                "rule": { "severity": "error", "name": "rule-generated" }
              }
            ],
            "error": 5,
            "warn": 1,
            "info": 1,
            "ignore": 1,
            "totalCruised": 9,
            "totalDependenciesCruised": 9
          }
        }
        """;

    // Verbatim stderr depcruise prints (exit 1, empty stdout) when the
    // repository carries no dependency-cruiser configuration.
    private const string MissingConfigStderr = """
        ERROR: Can't open a config file (.dependency-cruiser.(c)js) at the default location. Does it exist?
                 - You can create one by running 'npx dependency-cruiser --init'
                 - Want to run a without a config file? Use --no-config
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingDepcruise_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "depcruise: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("depcruise", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingDepcruise()
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

        IAuditor auditor = new DepcruiseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("depcruise", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "17.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("depcruise", ex.Message, StringComparison.Ordinal);
        Assert.Contains("17.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DepcruiseAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new DepcruiseAuditor();
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
    public async Task Fixture_WithErrorAndCycleViolations_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonWithErrorAndCycleViolations, ""));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var errorFinding = Assert.Single(result.Findings, f => f.Title.Contains("no-src-b-to-a", StringComparison.Ordinal));
        Assert.Equal("codeybox:depcruise", errorFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
        Assert.Equal("src/b.js", errorFinding.Location);
        Assert.Contains("no-src-b-to-a", errorFinding.Description, StringComparison.Ordinal);
        Assert.Contains("Forbidden dependency", errorFinding.Description, StringComparison.Ordinal);

        var cycleFinding = Assert.Single(result.Findings, f => f.Title.Contains("no-circular", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, cycleFinding.Severity);
        Assert.Equal("src/circ-a.js", cycleFinding.Location);
        Assert.Contains("Circular dependency", cycleFinding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("depcruise", scanExec!.Argv[0]);
        Assert.Contains("--output-type", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--progress", scanExec.Argv);
        Assert.Contains("none", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("depcruise ", StringComparison.Ordinal));
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

        IAuditor auditor = new DepcruiseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFoundSomethingExit_Code1_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithErrorAndCycleViolations, ""));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Title.Contains("no-src-b-to-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Missing dependency-cruiser configuration: exit 1, error text on stderr, empty stdout.
            return Task.FromResult(new SandboxExecResult(1, "", MissingConfigStderr));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("depcruise", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "some failure\n"));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("depcruise", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "depcruise: command not found"));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("depcruise", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new DepcruiseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("src/app.js", error.Location);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);
        Assert.Contains("Severity (tool): warn", warn.Description, StringComparison.Ordinal);

        var info = Assert.Single(findings, f => f.Title.Contains("rule-info", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Contains("Severity (tool): info", info.Description, StringComparison.Ordinal);

        var ignored = Assert.Single(findings, f => f.Title.Contains("rule-ignore", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, ignored.Severity);
        Assert.Contains("Severity (tool): ignore", ignored.Description, StringComparison.Ordinal);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var orphan = Assert.Single(findings, f => f.Title.Contains("not-an-orphan", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, orphan.Severity);
        Assert.Equal("src/lonely.js", orphan.Location);

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
            s => s.PluginId == DepcruiseAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("depcruise", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresDepcruiseRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [DepcruiseAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == DepcruiseAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("depcruise", tool.Binary);
        // Verify-only by design: dependency-cruiser ships via npm, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("depcruise", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "17.5.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new DepcruiseAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "17.5.0",
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

        var auditor = new DepcruiseAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/depcruise.operator.cjs",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/depcruise.operator.cjs", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new DepcruiseAuditor();
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
            return Task.FromResult(new SandboxExecResult(0, JsonWithErrorAndCycleViolations, ""));
        });

        var auditor = new DepcruiseAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "no-circular",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("no-circular", finding.Title, StringComparison.Ordinal);
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

        var auditor = new DepcruiseAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--exclude,node_modules,--metrics",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("depcruise", scanExec!.Argv[0]);
        Assert.Contains("--exclude", scanExec.Argv);
        Assert.Contains("node_modules", scanExec.Argv);
        Assert.Contains("--metrics", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(" --metrics", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("requires_depcruise", "true")]
    public async Task RealDepcruise_ViolatedFixture_YieldsFindings()
    {
        var installed = InstalledDepcruiseVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedDepcruiseFixtureRepoAsync(clean: false);

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

            var auditor = new DepcruiseAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("no-index-to-internal", finding.Title, StringComparison.Ordinal);
            Assert.Equal("src/index.js", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_depcruise", "true")]
    public async Task RealDepcruise_CleanFixture_Passes()
    {
        var installed = InstalledDepcruiseVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedDepcruiseFixtureRepoAsync(clean: true);

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

            var auditor = new DepcruiseAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.DepcruiseAuditorPlugin.dll");
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
            PluginId: DepcruiseAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Depcruise JS/TS Dependency Architecture Rules",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, DepcruiseAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("depcruise", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "depcruise" && exec.Argv[1] == "--version";

    private static async Task<string> SeedDepcruiseFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-depcruise-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        await File.WriteAllTextAsync(Path.Combine(dir, "package.json"), """
            {
              "name": "depcruise-fixture",
              "version": "1.0.0",
              "type": "module"
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(dir, ".dependency-cruiser.cjs"),
            """
            module.exports = {
              forbidden: [
                {
                  name: 'no-index-to-internal',
                  severity: 'error',
                  from: { path: 'src/index\\.js$' },
                  to: { path: 'src/internal\\.js$' },
                },
              ],
            };
            """);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "src", "internal.js"),
            "export function internal() {\n  return 1;\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(dir, "src", "index.js"),
            clean
                ? "export function main() {\n  return 0;\n}\n"
                : "import { internal } from './internal.js';\nexport function main() {\n  return internal();\n}\n");

        return dir;
    }

    private static string? ProbeInstalledDepcruiseVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "depcruise",
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
