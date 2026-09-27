using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PsalmAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the psalm auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming psalm (never a pass or finding).
/// - Psalm's inverted exit convention: 0 = no error-severity issues (info issues may still
///   be in the report), 2 = error-severity issues found — both are verdicts; 1 (could not
///   run: bad args, missing/unparseable psalm.xml, uncaught exception), 255 (PHP fatal) and
///   all others are infrastructure.
/// - A findings-producing exit without a JSON issue array fails closed as infrastructure.
/// - psalm JSON output maps to findings with issue type as rule id and file:line locations;
///   absolute file_path values are relativized against the scan root; out-of-root paths keep
///   a file:// marker; line_from is already 1-based.
/// - Raw tool severities ("error"/"info") go through the declared mapping.
/// - TrustRepositoryConfig=false fails closed unless ConfigPath (or ExtraArguments --config)
///   supplies an operator-owned configuration.
/// - Default exclusions (vendored, framework-generated, build output) and scoped options.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_psalm", "true")].
/// </summary>
public sealed class PsalmAuditorTests
{
    private static readonly string? InstalledPsalmVersion = ProbeInstalledPsalmVersion();

    private const string JsonWithIssues = """
        [
          {
            "severity": "error",
            "line_from": 3,
            "line_to": 3,
            "type": "InvalidReturnType",
            "message": "The declared return type 'int' for answer is incorrect, got 'string'",
            "file_name": "src/Kernel.php",
            "file_path": "/work/src/Kernel.php",
            "snippet": "function answer(): int {",
            "selected_text": "int",
            "from": 60,
            "to": 90,
            "snippet_from": 55,
            "snippet_to": 95,
            "column_from": 25,
            "column_to": 28,
            "shortcode": 11,
            "error_level": 1,
            "link": "https://psalm.dev/011",
            "taint_trace": null,
            "other_references": null
          },
          {
            "severity": "info",
            "line_from": 8,
            "line_to": 8,
            "type": "PossiblyUnusedVariable",
            "message": "Variable $helper is never referenced",
            "file_name": "src/Util.php",
            "file_path": "/work/src/Util.php",
            "snippet": "$helper = compute();",
            "selected_text": "$helper",
            "from": 120,
            "to": 128,
            "snippet_from": 110,
            "snippet_to": 140,
            "column_from": 5,
            "column_to": 12,
            "shortcode": 78,
            "error_level": -1,
            "link": "https://psalm.dev/078",
            "taint_trace": null,
            "other_references": null
          }
        ]
        """;

    private const string JsonInfoOnly = """
        [
          {
            "severity": "info",
            "line_from": 8,
            "line_to": 8,
            "type": "PossiblyUnusedVariable",
            "message": "Variable $helper is never referenced",
            "file_name": "src/Util.php",
            "file_path": "/work/src/Util.php",
            "snippet": "$helper = compute();",
            "selected_text": "$helper",
            "from": 120,
            "to": 128,
            "snippet_from": 110,
            "snippet_to": 140,
            "column_from": 5,
            "column_to": 12,
            "shortcode": 78,
            "error_level": -1,
            "link": "https://psalm.dev/078",
            "taint_trace": null,
            "other_references": null
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWithFilteredPathsAndNoLocation = """
        [
          {
            "severity": "error",
            "line_from": 2,
            "line_to": 2,
            "type": "UndefinedClass",
            "message": "Class or interface Missing\\Dep does not exist",
            "file_name": "src/broken.php",
            "file_path": "/work/src/broken.php",
            "snippet": "",
            "selected_text": "",
            "from": 40,
            "to": 52,
            "snippet_from": 30,
            "snippet_to": 60,
            "column_from": 10,
            "column_to": 22,
            "shortcode": 19,
            "error_level": 1,
            "link": "https://psalm.dev/019",
            "taint_trace": null,
            "other_references": null
          },
          {
            "severity": "error",
            "line_from": 1,
            "line_to": 1,
            "type": "ParseError",
            "message": "Syntax error in vendored file",
            "file_name": "vendor/acme/lib/Bad.php",
            "file_path": "/work/vendor/acme/lib/Bad.php",
            "snippet": "",
            "selected_text": "",
            "from": 0,
            "to": 10,
            "snippet_from": 0,
            "snippet_to": 1,
            "column_from": 1,
            "column_to": 5,
            "shortcode": 173,
            "error_level": 1,
            "link": "https://psalm.dev/173",
            "taint_trace": null,
            "other_references": null
          },
          {
            "severity": "error",
            "line_from": 1,
            "line_to": 1,
            "type": "InvalidArgument",
            "message": "Issue in generated proxy",
            "file_name": "generated/proxies/FooProxy.php",
            "file_path": "/work/generated/proxies/FooProxy.php",
            "snippet": "",
            "selected_text": "",
            "from": 0,
            "to": 10,
            "snippet_from": 0,
            "snippet_to": 1,
            "column_from": 1,
            "column_to": 5,
            "shortcode": 0,
            "error_level": 1,
            "link": "",
            "taint_trace": null,
            "other_references": null
          },
          {
            "severity": "error",
            "line_from": 7,
            "line_to": 7,
            "type": "InvalidReturnType",
            "message": "Stub outside the audited tree",
            "file_name": "/opt/php-stubs/vendor-dep.php",
            "file_path": "/opt/php-stubs/vendor-dep.php",
            "snippet": "",
            "selected_text": "",
            "from": 0,
            "to": 10,
            "snippet_from": 0,
            "snippet_to": 1,
            "column_from": 1,
            "column_to": 5,
            "shortcode": 11,
            "error_level": 1,
            "link": "https://psalm.dev/011",
            "taint_trace": null,
            "other_references": null
          },
          {
            "severity": "error",
            "line_from": 0,
            "line_to": 0,
            "type": "UnusedBaselineEntry",
            "message": "Baseline for issue \"UndefinedClass\" has 1 extra entry.",
            "file_name": "",
            "file_path": "",
            "snippet": "",
            "selected_text": "",
            "from": 0,
            "to": 0,
            "snippet_from": 0,
            "snippet_to": 0,
            "column_from": 0,
            "column_to": 0,
            "shortcode": 166,
            "error_level": 1,
            "link": "https://psalm.dev/166",
            "taint_trace": null,
            "other_references": null
          }
        ]
        """;

    private const string JsonWithSeverities = """
        [
          { "severity": "error", "line_from": 1, "line_to": 1, "type": "IssueError", "message": "Error-level issue.", "file_name": "src/a.php", "file_path": "/work/src/a.php", "snippet": "", "selected_text": "", "from": 0, "to": 1, "snippet_from": 0, "snippet_to": 1, "column_from": 1, "column_to": 2, "shortcode": 0, "error_level": 1, "link": "", "taint_trace": null, "other_references": null },
          { "severity": "info", "line_from": 2, "line_to": 2, "type": "IssueInfo", "message": "Info-level issue.", "file_name": "src/a.php", "file_path": "/work/src/a.php", "snippet": "", "selected_text": "", "from": 10, "to": 11, "snippet_from": 1, "snippet_to": 2, "column_from": 1, "column_to": 2, "shortcode": 0, "error_level": -1, "link": "", "taint_trace": null, "other_references": null },
          { "severity": "unspecified-future-level", "line_from": 3, "line_to": 3, "type": "IssueUnknown", "message": "Unrecognised level.", "file_name": "src/a.php", "file_path": "/work/src/a.php", "snippet": "", "selected_text": "", "from": 20, "to": 21, "snippet_from": 2, "snippet_to": 3, "column_from": 1, "column_to": 2, "shortcode": 0, "error_level": 1, "link": "", "taint_trace": null, "other_references": null },
          { "line_from": 4, "line_to": 4, "type": "IssueNoSeverity", "message": "No severity field.", "file_name": "src/a.php", "file_path": "/work/src/a.php", "snippet": "", "selected_text": "", "from": 30, "to": 31, "snippet_from": 3, "snippet_to": 4, "column_from": 1, "column_to": 2, "shortcode": 0, "error_level": 1, "link": "", "taint_trace": null, "other_references": null }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingPsalm_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "psalm: command not found"));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingPsalm()
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

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Psalm 5.26.1@abcd1234\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("5.26.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PsalmAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new PsalmAuditor();
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
    public async Task Fixture_WithIssues_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // Psalm's "found issues" exit is 2 — the report is emitted to
            // stdout before the non-zero exit.
            return Task.FromResult(new SandboxExecResult(2, JsonWithIssues, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var errorFinding = Assert.Single(result.Findings, f => f.Title.Contains("InvalidReturnType", StringComparison.Ordinal));
        Assert.Equal("codeybox:psalm", errorFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
        // psalm reports absolute file_path and 1-based line_from; the
        // auditor relativizes to the repo root.
        Assert.Equal("src/Kernel.php:3", errorFinding.Location);

        var infoFinding = Assert.Single(result.Findings, f => f.Title.Contains("PossiblyUnusedVariable", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, infoFinding.Severity);
        Assert.Equal("src/Util.php:8", infoFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("psalm", scanExec!.Argv[0]);
        Assert.Contains("--output-format=json", scanExec.Argv);
        Assert.Contains("--show-info=true", scanExec.Argv);
        Assert.Contains("--no-cache", scanExec.Argv);
        Assert.Contains("--no-progress", scanExec.Argv);
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

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithInfoOnlyFindings_IsVerdict_PassesWithAdvisoryFindings()
    {
        // Psalm exits 0 when only info-severity issues exist — the report
        // still carries findings; they are advisory, not a gate failure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonInfoOnly, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("PossiblyUnusedVariable", finding.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)] // could not run: bad arguments, missing/unparseable psalm.xml, uncaught exception
    [InlineData(255)] // PHP engine fatal
    [InlineData(3)] // unknown convention — fails loud rather than guessed
    public async Task CouldNotRunExitCodes_AreInfrastructureFailures(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // psalm could not run at all — text on stderr, not a JSON report.
            return Task.FromResult(new SandboxExecResult(exitCode, "", "Could not locate a psalm.xml config file"));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 2 with text rather than a JSON report fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(2, "", "psalm died mid-run"));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "psalm: command not found"));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
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

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(4, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("IssueError", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var info = Assert.Single(findings, f => f.Title.Contains("IssueInfo", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("IssueUnknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("IssueNoSeverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> default
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
            s => s.PluginId == PsalmAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("psalm", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresPsalmRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PsalmAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PsalmAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("psalm", tool.Binary);
        // Verify-only by design: psalm ships via composer/phive/phar and needs
        // PHP on PATH; no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("psalm", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Psalm 5.26.1@abcd1234\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "5.26.1",
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

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/psalm.xml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/psalm.xml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task DistrustRepoConfig_WithoutConfigPath_IsDeterministicInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "false",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("TrustRepositoryConfig", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConfigPath", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DistrustRepoConfig_WithConfigPath_RunsWithOperatorConfig()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "false",
                ["Scoped:ConfigPath"] = "/opt/codeybox/psalm.xml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("--config", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithFilteredPathsAndNoLocation, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/ and generated/ findings are dropped by the default
        // ExcludePaths; the src/ error, the out-of-root stub, and the
        // config-level UnusedBaselineEntry survive.
        Assert.Equal(3, result.Findings.Count);

        var srcFinding = Assert.Single(result.Findings, f => f.Title.StartsWith("UndefinedClass:", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, srcFinding.Severity);
        Assert.Equal("src/broken.php:2", srcFinding.Location);

        // Out-of-root absolute paths keep a file:// marker: the base strips a
        // bare leading '/', so without the marker "opt/..." would read as a
        // repository-relative path.
        var outsideFinding = Assert.Single(result.Findings, f => f.Title.Contains("Stub outside", StringComparison.Ordinal));
        Assert.Equal("file:///opt/php-stubs/vendor-dep.php:7", outsideFinding.Location);

        // Config-level issues carry no file: no location, but the finding
        // still reports (psalm emits these on findings exits).
        var baselineFinding = Assert.Single(result.Findings, f => f.Title.Contains("UnusedBaselineEntry", StringComparison.Ordinal));
        Assert.Null(baselineFinding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithIssues, ""));
        });

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "PossiblyUnusedVariable",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("PossiblyUnusedVariable", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRuns_ScanRootStaysPerInvocation()
    {
        // Plugin auditor types are DI singletons shared across concurrent
        // work-item audits, and each run's sandbox may translate the same
        // working directory differently. Run B resolving its scan root while
        // run A is between probe and parse must not relativize A's findings
        // against B's tree — the root travels on the invocation's async
        // context, not on the shared instance.
        var bRootResolved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var sandbox = new FakeSandbox(async (exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Ok(exec);
            if (IsScanRootProbe(exec))
            {
                if (exec.WorkingDirectory == "/b")
                    bRootResolved.TrySetResult();
                return new SandboxExecResult(0, exec.WorkingDirectory + "\n", "");
            }
            // Hold run A's report until B has resolved its own scan root: if
            // the root were shared state on the auditor, B's value would
            // overwrite A's before A's output is parsed.
            if (exec.WorkingDirectory == "/a")
                await bRootResolved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new SandboxExecResult(2, JsonForFile(exec.WorkingDirectory + "/src/Kernel.php"), "");
        });

        IAuditor auditor = new PsalmAuditor();
        var results = await Task.WhenAll(
            auditor.RunAsync(sandbox, "/a", FakeContext(), CancellationToken.None),
            auditor.RunAsync(sandbox, "/b", FakeContext(), CancellationToken.None));

        Assert.Equal("src/Kernel.php:3", Assert.Single(results[0].Findings).Location);
        Assert.Equal("src/Kernel.php:3", Assert.Single(results[1].Findings).Location);
    }

    private static string JsonForFile(string absoluteFile) => $$"""
        [
          {
            "severity": "error",
            "line_from": 3,
            "line_to": 3,
            "type": "InvalidReturnType",
            "message": "The declared return type 'int' for answer is incorrect, got 'string'",
            "file_name": "src/Kernel.php",
            "file_path": "{{absoluteFile}}",
            "snippet": "",
            "selected_text": "",
            "from": 0,
            "to": 10,
            "snippet_from": 0,
            "snippet_to": 1,
            "column_from": 1,
            "column_to": 5,
            "shortcode": 11,
            "error_level": 1,
            "link": "https://psalm.dev/011",
            "taint_trace": null,
            "other_references": null
          }
        ]
        """;

    [Fact]
    [Trait("requires_psalm", "true")]
    public async Task RealPsalm_IssueFixture_YieldsFindings()
    {
        var installed = InstalledPsalmVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPsalmFixtureRepoAsync(clean: false);

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

            var auditor = new PsalmAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);

            var finding = Assert.Single(result.Findings, f => f.Title.Contains("InvalidReturnType", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.StartsWith("src/bad.php:", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_psalm", "true")]
    public async Task RealPsalm_CleanFixture_Passes()
    {
        var installed = InstalledPsalmVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPsalmFixtureRepoAsync(clean: true);

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

            var auditor = new PsalmAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Error);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PsalmAuditorPlugin.dll");
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
            PluginId: PsalmAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Psalm PHP Static Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "Psalm " + PsalmAuditor.DefaultExpectedVersion + "@abcdef12\n", "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("psalm", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "psalm" && exec.Argv[1] == "--version";

    private static async Task<string> SeedPsalmFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-psalm-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        // A minimal psalm project: the tool cannot run without psalm.xml, and
        // level 2 keeps the fixture's own code honest under a strict contract.
        await File.WriteAllTextAsync(Path.Combine(dir, "psalm.xml"), """
            <?xml version="1.0"?>
            <psalm errorLevel="2">
              <projectFiles>
                <directory name="src" />
              </projectFiles>
            </psalm>
            """ + "\n");

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "clean.php"),
                "<?php\n\nfunction answer(): int\n{\n    return 42;\n}\n");
        }
        else
        {
            // A real declared-return-type error at every analysis level.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "bad.php"),
                "<?php\n\nfunction answer(): int\n{\n    return \"nope\";\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledPsalmVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "psalm",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            // Drain stdout asynchronously while waiting for exit: a synchronous
            // ReadToEnd before WaitForExit can deadlock on a full pipe and
            // would hang the whole class — this probe feeds a static field.
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var match = Regex.Match(stdout.GetAwaiter().GetResult(), @"\d+\.\d+\.\d+[\w.\-]*");
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
