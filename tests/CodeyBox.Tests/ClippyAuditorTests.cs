using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.ClippyAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the cargo-clippy auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cargo-clippy (never a pass or finding).
/// - Exit codes 0 and 101 with a clippy JSON report are verdicts; other exits are infrastructure.
/// - Exit 101 without diagnostics is infrastructure; exit 1 (usage error) is infrastructure.
/// - Clippy JSON maps to findings with lint codes, locations, and mapped severity.
/// - Raw tool severities go through the declared mapping; unknown levels map to Warning.
/// - Default exclusions (vendor/ + third_party/ + target/) and scoped options (ExpectedVersion, ManifestPath, Offline, AllTargets).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_cargo-clippy", "true")].
/// </summary>
public sealed class ClippyAuditorTests
{
    private static readonly string? InstalledClippyVersion = ProbeInstalledClippyVersion();

    // Shape captured from clippy 0.1.99 `clippy --message-format=json --all-targets`:
    // artifact records are skipped, compiler-message records become findings —
    // one advisory warning in src/main.rs, one blocking error in src/lib.rs.
    private const string JsonWithWarningAndError = """
        {"reason":"compiler-artifact","package_id":"path+file:///work#0.1.0","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["/work/target/debug/demo"],"executable":null,"fresh":false}
        {"reason":"compiler-message","package_id":"path+file:///work#0.1.0","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: used `unwrap()` on `Some` value\n --> src/main.rs:6:14\n","$message_type":"diagnostic","children":[],"level":"warning","message":"used `unwrap()` on `Some` value","spans":[{"byte_end":104,"byte_start":88,"column_end":30,"column_start":14,"expansion":null,"file_name":"src/main.rs","is_primary":true,"label":null,"line_end":6,"line_start":6,"suggested_replacement":null,"suggestion_applicability":null,"text":[]}],"code":{"code":"clippy::unnecessary_literal_unwrap","explanation":null}}}
        {"reason":"compiler-message","package_id":"path+file:///work#0.1.0","manifest_path":"/work/Cargo.toml","target":{"kind":["lib"],"crate_types":["lib"],"name":"demo","src_path":"/work/src/lib.rs","edition":"2021","doctest":true,"test":true},"message":{"rendered":"error[E0308]: mismatched types\n --> src/lib.rs:2:18\n","$message_type":"diagnostic","children":[],"level":"error","message":"mismatched types","spans":[{"byte_end":41,"byte_start":29,"column_end":30,"column_start":18,"expansion":null,"file_name":"src/lib.rs","is_primary":true,"label":"expected `i32`, found `&str`","line_end":2,"line_start":2,"suggested_replacement":null,"suggestion_applicability":null,"text":[]}],"code":{"code":"E0308","explanation":null}}}
        {"reason":"build-finished","success":true}
        """;

    private const string JsonClean = """
        {"reason":"compiler-artifact","package_id":"path+file:///work#0.1.0","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["/work/target/debug/demo"],"executable":null,"fresh":false}
        {"reason":"build-finished","success":true}
        """;

    private const string JsonWithSeveritiesAndExcludedPaths = """
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"a warning lint","spans":[{"file_name":"src/main.rs","line_start":10,"line_end":10,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/lib.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"error: x\n","$message_type":"diagnostic","children":[],"level":"error","message":"a denied lint","spans":[{"file_name":"src/lib.rs","line_start":4,"line_end":4,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::unwrap_used","explanation":null}}}
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/note.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"note: x\n","$message_type":"diagnostic","children":[],"level":"note","message":"context note","spans":[{"file_name":"src/note.rs","line_start":3,"line_end":3,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_borrow","explanation":null}}}
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/future.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"x\n","$message_type":"diagnostic","children":[],"level":"ice","message":"a diagnostic from a future level vocabulary","spans":[{"file_name":"src/future.rs","line_start":7,"line_end":7,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::future_lint","explanation":null}}}
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/vendor/upstream/lib.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"vendored warning","spans":[{"file_name":"vendor/upstream/lib.rs","line_start":1,"line_end":1,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/third_party/fork/tool.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"mirrored warning","spans":[{"file_name":"third_party/fork/tool.rs","line_start":2,"line_end":2,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
        {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/target/debug/build/gen.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"generated warning","spans":[{"file_name":"target/debug/build/gen.rs","line_start":3,"line_end":3,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
        {"reason":"build-finished","success":true}
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCargoClippy_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "cargo-clippy: command not found"));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingCargoClippy()
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

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "clippy 0.1.0 (abc 2020-01-01)\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.1.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ClippyAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new ClippyAuditor();
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
    public async Task Fixture_WithWarningAndError_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonWithWarningAndError, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The E0308 error is blocking, so the audit fails even though the
        // clippy warning alone would be advisory.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var warningFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("unnecessary_literal_unwrap", StringComparison.Ordinal));
        Assert.Equal("codeybox:clippy", warningFinding.AuditorName);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.Equal("src/main.rs:6", warningFinding.Location);
        Assert.Contains("unnecessary_literal_unwrap", warningFinding.Description, StringComparison.Ordinal);
        Assert.Contains("unwrap", warningFinding.Description, StringComparison.Ordinal);

        var errorFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("E0308", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
        Assert.Equal("src/lib.rs:2", errorFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("cargo-clippy", scanExec!.Argv[0]);
        Assert.Equal("clippy", scanExec.Argv[1]);
        Assert.Contains("--message-format=json", scanExec.Argv);
        Assert.Contains("--all-targets", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("cargo-clippy ", StringComparison.Ordinal));
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

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WarningOnly_ExitCode0_ReportsWarningFindings_AndPasses()
    {
        const string warningOnly = """
            {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"needless return","spans":[{"file_name":"src/main.rs","line_start":3,"line_end":3,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
            {"reason":"build-finished","success":true}
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // Clippy warnings do not fail the build: findings arrive with exit 0.
            return Task.FromResult(new SandboxExecResult(0, warningOnly, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("src/main.rs:3", finding.Location);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code101_WithJson_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(101, JsonWithWarningAndError, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task FailedToRunExit_Code1_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // Cargo usage error: no JSON report.
            return Task.FromResult(new SandboxExecResult(
                1, "", "error: unexpected argument '--bad-flag-xyz' found\n"));
        });

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsExit_WithoutDiagnostics_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // Missing manifest: exit 101 with empty stdout — could not run,
            // never a pass.
            return Task.FromResult(new SandboxExecResult(
                101, "", "error: could not find `Cargo.toml` in `/work` or any parent directory\n"));
        });

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, JsonWithWarningAndError, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "cargo-clippy: command not found"));
        });

        IAuditor auditor = new ClippyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-clippy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(101, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, third_party/, and target/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(4, findings.Count);

        var warning = Assert.Single(findings, f => f.Title.Contains("needless_return", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("src/main.rs:10", warning.Location);
        Assert.Contains("Severity (tool): warning", warning.Description, StringComparison.Ordinal);

        var error = Assert.Single(findings, f => f.Title.Contains("unwrap_used", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var note = Assert.Single(findings, f => f.Title.Contains("needless_borrow", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);
        Assert.Contains("Severity (tool): note", note.Description, StringComparison.Ordinal);

        var unknown = Assert.Single(findings, f => f.Title.Contains("future_lint", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default
        Assert.Contains("Severity (tool): ice", unknown.Description, StringComparison.Ordinal);

        Assert.DoesNotContain(findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("third_party/", StringComparison.Ordinal)
                || f.Location.StartsWith("target/", StringComparison.Ordinal)));

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
            s => s.PluginId == ClippyAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cargo-clippy", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCargoClippyRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ClippyAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ClippyAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("cargo-clippy", tool.Binary);
        // Verify-only by design: clippy ships with the Rust toolchain via rustup, no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("cargo-clippy", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "clippy 9.9.9 (abc 2026-01-01)\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ClippyAuditor();
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
    public async Task ScopedConfiguration_ManifestPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ClippyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ManifestPath"] = "/work/crates/demo/Cargo.toml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var manifestIndex = argv.ToList().IndexOf("--manifest-path");
        Assert.True(manifestIndex >= 0 && manifestIndex + 1 < argv.Count);
        Assert.Equal("/work/crates/demo/Cargo.toml", argv[manifestIndex + 1]);
    }

    [Fact]
    public async Task ScopedManifestPathPlusExtraArgumentsFlag_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ClippyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ManifestPath"] = "crates/demo/Cargo.toml",
                ["Scoped:ExtraArguments"] = "--manifest-path=other/Cargo.toml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("--manifest-path", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task OutOfRootSpanPaths_AreMarkedNotRepoRelative()
    {
        const string json = """
            {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"absolute escape","spans":[{"file_name":"/outside/evil.rs","line_start":1,"line_end":1,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
            {"reason":"compiler-message","package_id":"p","manifest_path":"/work/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"demo","src_path":"/work/src/main.rs","edition":"2021","doctest":false,"test":true},"message":{"rendered":"warning: x\n","$message_type":"diagnostic","children":[],"level":"warning","message":"dot-dot escape","spans":[{"file_name":"vendor/../../escape.rs","line_start":5,"line_end":5,"column_start":1,"column_end":5,"is_primary":true}],"code":{"code":"clippy::needless_return","explanation":null}}}
            {"reason":"build-finished","success":true}
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, json, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        foreach (var finding in result.Findings)
            Assert.StartsWith("file://", finding.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_Offline_PassesOfflineFlag_AndDropsNetworkCapability()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ClippyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Offline"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("--offline", scanExec!.Argv);
        Assert.Equal(AuditCapabilities.None, ((IAuditor)auditor).Required);
    }

    [Fact]
    public async Task ScopedConfiguration_AllTargets_FalseOmitsFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ClippyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:AllTargets"] = "false",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--all-targets", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(101, JsonWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new ClippyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("third_party/", StringComparison.Ordinal)
                || f.Location.StartsWith("target/", StringComparison.Ordinal)));
        Assert.Contains(result.Findings, f => f.Location is not null
            && f.Location.StartsWith("src/main.rs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithWarningAndError, ""));
        });

        var auditor = new ClippyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "E0308",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("E0308", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArguments_AreStructuredArgv_NotAShellString()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ClippyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--locked,--, --deny,warnings",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("cargo-clippy", scanExec!.Argv[0]);
        Assert.Contains("--locked", scanExec.Argv);
        Assert.Contains("--", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains("--locked,--", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("requires_cargo-clippy", "true")]
    public async Task RealCargoClippy_FixtureWithIssue_YieldsFindings_WithRuleIdAndLocation()
    {
        var installed = InstalledClippyVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedClippyFixtureRepoAsync(clean: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                    Environment = ClippyToolEnvironment(),
                },
                CancellationToken.None);

            var auditor = new ClippyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // Clippy lints default to warnings: findings are reported but the
            // audit passes through the real path.
            Assert.True(result.Passed);
            Assert.NotEmpty(result.Findings);

            var lintFinding = Assert.Single(result.Findings, f => f.Location == "src/main.rs:2");
            Assert.Contains("clippy::", lintFinding.Title, StringComparison.Ordinal);
            Assert.Equal(AuditSeverity.Warning, lintFinding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cargo-clippy", "true")]
    public async Task RealCargoClippy_CleanFixture_Passes()
    {
        var installed = InstalledClippyVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedClippyFixtureRepoAsync(clean: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                    Environment = ClippyToolEnvironment(),
                },
                CancellationToken.None);

            var auditor = new ClippyAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ClippyAuditorPlugin.dll");
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
            PluginId: ClippyAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Clippy Rust Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "clippy " + ClippyAuditor.DefaultExpectedVersion + " (abc 2026-01-01)\n", "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cargo-clippy", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cargo-clippy" && exec.Argv[1] == "--version";

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static async Task<string> SeedClippyFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-clippy-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        await File.WriteAllTextAsync(
            Path.Combine(dir, "Cargo.toml"),
            """
            [package]
            name = "codeyboxfixture"
            version = "0.1.0"
            edition = "2021"
            """);

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "main.rs"),
                "fn main() {\n    println!(\"hi\");\n}\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "main.rs"),
                "fn main() {\n    let _y = Some(5).unwrap();\n    println!(\"hi\");\n}\n");
        }

        return dir;
    }

    /// <summary>
    /// Propagates a rustup-based toolchain layout into the process sandbox,
    /// which otherwise starts with a scrubbed environment (PATH plus the
    /// spec's own entries). Entries are passed only when set on the test
    /// host, so a system-wide toolchain without rustup shims is unaffected.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ClippyToolEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "RUSTUP_HOME", "CARGO_HOME", "RUSTUP_TOOLCHAIN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                environment[name] = value;
        }

        return environment;
    }

    private static string? ProbeInstalledClippyVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cargo-clippy",
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
        catch { /* best-effort fixture teardown */ }
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
