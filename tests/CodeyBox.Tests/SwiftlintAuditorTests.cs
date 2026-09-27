using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.SwiftlintAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the SwiftLint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming swiftlint (never a pass or finding).
/// - Exit codes 0 and 2 are verdicts (findings-producing); exit 1 (no lintable files), 64, 127 and others are infrastructure.
/// - Findings exits without JSON output fail closed as infrastructure failures.
/// - SwiftLint JSON output maps to findings with rule ids, locations, and mapped severity;
///   absolute file paths are relativized against the per-run scan root.
/// - Raw tool severities ("Warning"/"Error") go through the declared mapping; unknown levels fall back to Warning.
/// - Default exclusions (Swift-ecosystem vendored + generated trees) and scoped options
///   (ExpectedVersion, ConfigPath, IncludedRules/ExcludedRules, ExcludePaths).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_swiftlint", "true")].
/// </summary>
public sealed class SwiftlintAuditorTests
{
    private static readonly string? InstalledSwiftlintVersion = ProbeInstalledSwiftlintVersion();

    private const string JsonWithErrorAndWarning = """
        [
          {
            "character": 6,
            "file": "/work/Sources/bad.swift",
            "line": 1,
            "reason": "Variable name 'x' should be between 3 and 40 characters long",
            "rule_id": "identifier_name",
            "severity": "Error",
            "type": "Identifier Name"
          },
          {
            "character": 16,
            "file": "/work/Sources/bad.swift",
            "line": 10,
            "reason": "Lines should not have trailing whitespace",
            "rule_id": "trailing_whitespace",
            "severity": "Warning",
            "type": "Trailing Whitespace"
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWarningOnly = """
        [
          {
            "character": 16,
            "file": "/work/Sources/warn.swift",
            "line": 4,
            "reason": "Lines should not have trailing whitespace",
            "rule_id": "trailing_whitespace",
            "severity": "Warning",
            "type": "Trailing Whitespace"
          }
        ]
        """;

    private const string JsonWithSeverities = """
        [
          {
            "character": 1,
            "file": "/work/Sources/a.swift",
            "reason": "Error-level violation.",
            "rule_id": "rule-error",
            "severity": "Error",
            "line": 1,
            "type": "T"
          },
          {
            "character": 1,
            "file": "/work/Sources/a.swift",
            "reason": "Warn-level violation.",
            "rule_id": "rule-warn",
            "severity": "Warning",
            "line": 2,
            "type": "T"
          },
          {
            "character": 1,
            "file": "/work/Sources/a.swift",
            "reason": "Unrecognised level.",
            "rule_id": "rule-unknown",
            "severity": "Tracing",
            "line": 3,
            "type": "T"
          },
          {
            "character": 1,
            "file": "/work/Sources/a.swift",
            "reason": "No severity field.",
            "rule_id": "rule-noseverity",
            "line": 4,
            "type": "T"
          }
        ]
        """;

    private const string JsonWithFilteredPaths = """
        [
          {
            "character": 16,
            "file": "/work/Sources/real.swift",
            "line": 8,
            "reason": "Lines should not have trailing whitespace",
            "rule_id": "trailing_whitespace",
            "severity": "Warning",
            "type": "Trailing Whitespace"
          },
          {
            "character": 1,
            "file": "/work/Pods/Dep/dep.swift",
            "line": 1,
            "reason": "Lines should not have trailing whitespace",
            "rule_id": "trailing_whitespace",
            "severity": "Warning",
            "type": "Trailing Whitespace"
          },
          {
            "character": 1,
            "file": "/work/.build/checkouts/dep.swift",
            "line": 2,
            "reason": "Lines should not have trailing whitespace",
            "rule_id": "trailing_whitespace",
            "severity": "Warning",
            "type": "Trailing Whitespace"
          },
          {
            "character": 1,
            "file": "/work/DerivedData/Gen/gen.swift",
            "line": 3,
            "reason": "Lines should not have trailing whitespace",
            "rule_id": "trailing_whitespace",
            "severity": "Warning",
            "type": "Trailing Whitespace"
          }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingSwiftlint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "swiftlint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingSwiftlint()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "9.99.0\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.99.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SwiftlintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SwiftlintAuditor();
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
    public async Task ScanRootProbeFailed_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scan root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithErrorAndWarning_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(2, JsonWithErrorAndWarning, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var errorFinding = Assert.Single(result.Findings, f => f.Title.Contains("identifier_name", StringComparison.Ordinal));
        Assert.Equal("codeybox:swiftlint", errorFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
        // SwiftLint reports absolute file paths; the auditor relativizes to the scan root.
        Assert.Equal("Sources/bad.swift:1", errorFinding.Location);

        var warnFinding = Assert.Single(result.Findings, f => f.Title.Contains("trailing_whitespace", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warnFinding.Severity);
        Assert.Equal("Sources/bad.swift:10", warnFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("swiftlint", scanExec!.Argv[0]);
        Assert.Equal("lint", scanExec.Argv[1]);
        Assert.Contains("--reporter", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--no-cache", scanExec.Argv);
        // Warnings stay advisory by default: no --strict unless the operator asks for it.
        Assert.DoesNotContain("--strict", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WarningsOnly_Exit0_ReportsAdvisoryFindings_AndStillPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWarningOnly, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // SwiftLint exits 0 for warnings-only runs: the report is still the
        // verdict, and advisory findings do not fail the audit.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("Sources/warn.swift:4", finding.Location);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code2_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithErrorAndWarning, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_NoLintableFiles_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // SwiftLint exits 1 with "No lintable files found" and no JSON
            // report: the scan analyzed nothing, so this is infrastructure.
            return Task.FromResult(new SandboxExecResult(1, "", "Error: No lintable files found at paths: '/work'"));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 2 (violations found) with no report on stdout fails closed
            // as infrastructure rather than a clean pass.
            return Task.FromResult(new SandboxExecResult(2, "", ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode64_IllegalParameters_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(64, "", "Error: Unknown option '--bogus'"));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "swiftlint: command not found"));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("swiftlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithSeverities, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(4, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-noseverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> default

        // Every finding carries a mapped CodeyBox severity, never the tool's raw string.
        Assert.All(findings, f => Assert.True(Enum.IsDefined(f.Severity)));
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
            s => s.PluginId == SwiftlintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("swiftlint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresSwiftlintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SwiftlintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SwiftlintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("swiftlint", tool.Binary);
        // Verify-only by design: swiftlint ships as a release archive/pkg,
        // no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("swiftlint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "9.9.9\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SwiftlintAuditor();
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
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SwiftlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/.swiftlint.operator.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/.swiftlint.operator.yml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new SwiftlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Pods/, .build/, DerivedData/ findings are dropped by the default
        // ExcludePaths; the Sources/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("Sources/real.swift:8", finding.Location);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithErrorAndWarning, ""));
        });

        var auditor = new SwiftlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "trailing_whitespace",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("trailing_whitespace", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_swiftlint", "true")]
    public async Task RealSwiftlint_FixtureWithViolation_YieldsFindings()
    {
        var installed = InstalledSwiftlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSwiftlintFixtureRepoAsync(clean: false);

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

            var auditor = new SwiftlintAuditor();
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

            // Default ruleset: short variable names are errors.
            var errorFinding = Assert.Single(
                result.Findings, f => f.Title.Contains("identifier_name", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
            Assert.Equal("bad.swift:1", errorFinding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_swiftlint", "true")]
    public async Task RealSwiftlint_CleanFixture_Passes()
    {
        var installed = InstalledSwiftlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSwiftlintFixtureRepoAsync(clean: true);

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

            var auditor = new SwiftlintAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SwiftlintAuditorPlugin.dll");
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
            PluginId: SwiftlintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: SwiftLint Swift Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, SwiftlintAuditor.DefaultExpectedVersion + "\n", "")
            : IsScanRootProbe(exec)
                ? new SandboxExecResult(0, "/work\n", "")
                : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("swiftlint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "swiftlint" && exec.Argv[1] == "--version";

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static async Task<string> SeedSwiftlintFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-swiftlint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "clean.swift"), "let answer = 42\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.swift"), "let x = 1\n");
        }

        return dir;
    }

    private static string? ProbeInstalledSwiftlintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "swiftlint",
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
