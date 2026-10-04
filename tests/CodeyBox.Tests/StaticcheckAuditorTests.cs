using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.StaticcheckAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the staticcheck auditor plugin:
/// - Missing staticcheck (or go toolchain) binary is an infrastructure failure naming the binary (never a pass or finding).
/// - Exit codes 0 and 1 with a staticcheck JSON stream are verdicts; other exits are infrastructure.
/// - Exit 1 without JSON is infrastructure; a location-less code:compile record is a finding, not infrastructure.
/// - staticcheck JSON lines map to findings with check ids and relativized locations.
/// - Raw tool severities go through the declared mapping; unknown levels map to Warning.
/// - Default exclusions (vendor/ + third_party/) and scoped options (ExpectedVersion, IncludedRules, ExcludePaths, ExtraArguments).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_staticcheck", "true")].
/// </summary>
public sealed class StaticcheckAuditorTests
{
    private static readonly string? InstalledStaticcheckVersion = ProbeInstalledStaticcheckVersion();

    // Shape captured from staticcheck 2025.1.1 `-f json` against a fixture
    // with an ignored-Sprintf (SA4017 + S1039, error severity under the
    // default -fail all): absolute file names, one JSON object per line.
    private const string JsonLinesWithFindings =
        "{\"code\":\"SA4017\",\"severity\":\"error\",\"location\":{\"file\":\"/work/main.go\",\"line\":8,\"column\":2},\"end\":{\"file\":\"/work/main.go\",\"line\":8,\"column\":22},\"message\":\"Sprintf doesn't have side effects and its return value is ignored\"}\n"
        + "{\"code\":\"S1039\",\"severity\":\"error\",\"location\":{\"file\":\"/work/pkg/server.go\",\"line\":12,\"column\":2},\"end\":{\"file\":\"/work/pkg/server.go\",\"line\":12,\"column\":22},\"message\":\"unnecessary use of fmt.Sprintf\"}\n";

    private const string JsonLinesWithSeveritiesAndExcludedPaths =
        "{\"code\":\"SA4006\",\"severity\":\"error\",\"location\":{\"file\":\"/work/main.go\",\"line\":10,\"column\":15},\"end\":{\"file\":\"/work/main.go\",\"line\":10,\"column\":25},\"message\":\"this value of x is never used\"}\n"
        + "{\"code\":\"S1002\",\"severity\":\"warning\",\"location\":{\"file\":\"/work/pkg/server.go\",\"line\":4,\"column\":3},\"end\":{\"file\":\"/work/pkg/server.go\",\"line\":4,\"column\":10},\"message\":\"should omit comparison to bool constant, can be simplified to !x\"}\n"
        + "{\"code\":\"ST1000\",\"severity\":\"ignored\",\"location\":{\"file\":\"/work/pkg/server.go\",\"line\":1,\"column\":1},\"end\":{\"file\":\"/work/pkg/server.go\",\"line\":1,\"column\":8},\"message\":\"at least one file in a package should have a package comment\"}\n"
        + "{\"code\":\"U1000\",\"severity\":\"not-a-staticcheck-level\",\"location\":{\"file\":\"/work/pkg/unused.go\",\"line\":9,\"column\":6},\"end\":{\"file\":\"/work/pkg/unused.go\",\"line\":9,\"column\":9},\"message\":\"func helper is unused\"}\n"
        + "{\"code\":\"SA4017\",\"severity\":\"error\",\"location\":{\"file\":\"/work/vendor/upstream/lib.go\",\"line\":1,\"column\":1},\"end\":{\"file\":\"/work/vendor/upstream/lib.go\",\"line\":1,\"column\":10},\"message\":\"Sprintf doesn't have side effects and its return value is ignored\"}\n"
        + "{\"code\":\"SA4017\",\"severity\":\"error\",\"location\":{\"file\":\"/work/third_party/fork/tool.go\",\"line\":2,\"column\":1},\"end\":{\"file\":\"/work/third_party/fork/tool.go\",\"line\":2,\"column\":10},\"message\":\"Sprintf doesn't have side effects and its return value is ignored\"}\n";

    private const string JsonLineCompileFailure =
        "{\"code\":\"compile\",\"severity\":\"error\",\"location\":{\"file\":\"\",\"line\":0,\"column\":0},\"end\":{\"file\":\"\",\"line\":0,\"column\":0},\"message\":\"pattern ./...: directory prefix . does not contain main module or its selected dependencies\"}\n";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingStaticcheck_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsStaticcheckPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "staticcheck: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingStaticcheck()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec) && !IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec) && !IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "staticcheck 2024.1.1 (1.0.0)\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2024.1.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(StaticcheckAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new StaticcheckAuditor();
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
    public async Task GoToolchainMissing_IsInfrastructureFailure_NamingGo_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsStaticcheckPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGoPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("'go'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScanRootProbeFailed_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsStaticcheckPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("scan root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonLinesWithFindings, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Under the default -fail all every diagnostic is error-severity, so the audit fails.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var firstFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("SA4017", StringComparison.Ordinal));
        Assert.Equal("codeybox:staticcheck", firstFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, firstFinding.Severity);
        Assert.Equal("main.go:8", firstFinding.Location);
        Assert.Contains("SA4017", firstFinding.Description, StringComparison.Ordinal);
        Assert.Contains("Sprintf", firstFinding.Description, StringComparison.Ordinal);

        var secondFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("S1039", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, secondFinding.Severity);
        Assert.Equal("pkg/server.go:12", secondFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("staticcheck", scanExec!.Argv[0]);
        Assert.Contains("-f", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Equal("./...", scanExec.Argv[^1]);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(' ') && a.StartsWith("staticcheck ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithWarningStream_ReportsFindings_AndPasses()
    {
        // An operator narrowing -fail demotes diagnostics to warning: the
        // tool exits 0 but still prints the JSON stream — a verdict with
        // advisory findings, not a clean pass with hidden output.
        const string warningStream =
            "{\"code\":\"S1039\",\"severity\":\"warning\",\"location\":{\"file\":\"/work/pkg/foo.go\",\"line\":3,\"column\":2},\"end\":{\"file\":\"/work/pkg/foo.go\",\"line\":3,\"column\":12},\"message\":\"unnecessary use of fmt.Sprintf\"}\n";
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, warningStream, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("pkg/foo.go:3", finding.Location);
        Assert.Contains("S1039", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            // A crash before the report is written: text, not a report.
            return Task.FromResult(new SandboxExecResult(1, "", "panic: runtime error\n"));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_BadFlags_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                2, "", "flag provided but not defined: -bogus-flag\n"));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, JsonLinesWithFindings, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "staticcheck: command not found"));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("staticcheck", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileDiagnostic_SurfacesAsFinding_WithRuleId_AndNoLocation()
    {
        // A tree staticcheck cannot load exits 1 with a code:compile JSON
        // diagnostic and an empty location: the exit code keeps it a
        // verdict, and the record is a finding (rule id preserved, no
        // location guessed), never infrastructure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonLineCompileFailure, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("compile", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Null(finding.Location);
        Assert.Contains("Severity (tool): error", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonLinesWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/ and third_party/ findings are dropped by default ExcludePaths.
        var findings = result.Findings;
        Assert.Equal(4, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("SA4006", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("main.go:10", error.Location);
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(findings, f => f.Title.Contains("S1002", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Contains("Severity (tool): warning", warning.Description, StringComparison.Ordinal);

        // "ignored" only appears when the operator passes -show-ignored: informational, never blocking.
        var ignored = Assert.Single(findings, f => f.Title.Contains("ST1000", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, ignored.Severity);
        Assert.Contains("Severity (tool): ignored", ignored.Description, StringComparison.Ordinal);

        var unknown = Assert.Single(findings, f => f.Title.Contains("U1000", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        Assert.All(findings, f => Assert.IsType<AuditSeverity>(f.Severity));
    }

    [Fact]
    public async Task AbsolutePaths_RelativizedAgainstScanRoot_OutOfRootStaysMarked()
    {
        const string mixedRoots =
            "{\"code\":\"SA4006\",\"severity\":\"error\",\"location\":{\"file\":\"/work/pkg/foo.go\",\"line\":3,\"column\":2},\"end\":{\"file\":\"/work/pkg/foo.go\",\"line\":3,\"column\":5},\"message\":\"this value of x is never used\"}\n"
            + "{\"code\":\"SA4006\",\"severity\":\"error\",\"location\":{\"file\":\"/other/x.go\",\"line\":5,\"column\":1},\"end\":{\"file\":\"/other/x.go\",\"line\":5,\"column\":4},\"message\":\"this value of y is never used\"}\n";
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, mixedRoots, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        var inRoot = Assert.Single(result.Findings, f => f.Location == "pkg/foo.go:3");
        Assert.Contains("SA4006", inRoot.Title, StringComparison.Ordinal);

        // An out-of-root absolute path must not read as repo-relative (which
        // would let it accidentally match repo-relative ExcludePaths): it
        // stays marked and keeps its finding.
        var outOfRoot = Assert.Single(result.Findings, f => f.Location != "pkg/foo.go:3");
        Assert.StartsWith("file://", outOfRoot.Location, StringComparison.Ordinal);
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
            s => s.PluginId == StaticcheckAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("staticcheck", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresStaticcheckAndGoRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [StaticcheckAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == StaticcheckAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var staticcheckTool = Assert.Single(tools, t => t.Binary == "staticcheck");
        // Verify-only by design: staticcheck ships via go install / prebuilt
        // binaries — no distro apt package carries a version pin.
        Assert.Null(staticcheckTool.AptPackage);
        var goTool = Assert.Single(tools, t => t.Binary == "go");
        Assert.Null(goTool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Empty(contributions.InstallCommands);
        Assert.Equal(2, contributions.VerificationCommands.Count);
        Assert.Contains(
            contributions.VerificationCommands,
            v => v.Argv.Contains("staticcheck", StringComparer.Ordinal));
        Assert.Contains(
            contributions.VerificationCommands,
            v => v.Argv.Contains("go", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsStaticcheckPresenceProbe(exec) || IsGoPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "staticcheck 9.9.9 (9.9.9)\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new StaticcheckAuditor();
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
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonLinesWithSeveritiesAndExcludedPaths, ""));
        });

        IAuditor auditor = new StaticcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.DoesNotContain(result.Findings, f => f.Location is not null
            && (f.Location.StartsWith("vendor/", StringComparison.Ordinal)
                || f.Location.StartsWith("third_party/", StringComparison.Ordinal)));
        Assert.Contains(result.Findings, f => f.Location is not null
            && f.Location.StartsWith("main.go", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonLinesWithFindings, ""));
        });

        var auditor = new StaticcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "S1039",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("S1039", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArguments_AreStructuredArgv_NotAShellString()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new StaticcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-checks,all,-ST1000,-tags,integration",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("staticcheck", scanExec!.Argv[0]);
        Assert.Contains("-checks", scanExec.Argv);
        Assert.Contains("all", scanExec.Argv);
        Assert.Contains("-ST1000", scanExec.Argv);
        Assert.Contains("-tags", scanExec.Argv);
        Assert.Contains("integration", scanExec.Argv);
        Assert.DoesNotContain(scanExec.Argv, a => a.Contains(" -checks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopedConfiguration_ExtraArguments_OperatorFormatFlagIsNotDuplicated()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPreconditionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new StaticcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-f=json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal(1, scanExec!.Argv.Count(a => a == "-f" || a.StartsWith("-f=", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("requires_staticcheck", "true")]
    public async Task RealStaticcheck_FixtureWithIssue_YieldsFindings_WithRuleIdAndLocation()
    {
        var installed = InstalledStaticcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedStaticcheckFixtureRepoAsync(clean: false);

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

            var auditor = new StaticcheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // Under the default -fail all every diagnostic is error-severity:
            // findings fail the audit through the real path.
            Assert.False(result.Passed);
            Assert.Equal(2, result.Findings.Count);
            Assert.All(result.Findings, f => Assert.Equal("main.go:7", f.Location));
            Assert.Contains(result.Findings, f => f.Title.Contains("SA4017", StringComparison.Ordinal));
            Assert.Contains(result.Findings, f => f.Title.Contains("S1039", StringComparison.Ordinal));
            Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_staticcheck", "true")]
    public async Task RealStaticcheck_CleanFixture_Passes()
    {
        var installed = InstalledStaticcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedStaticcheckFixtureRepoAsync(clean: true);

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

            var auditor = new StaticcheckAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.StaticcheckAuditorPlugin.dll");
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
            PluginId: StaticcheckAuditor.PluginId,
            PluginDisplayName: "CodeyBox: staticcheck Go Analyzer",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "staticcheck " + StaticcheckAuditor.DefaultExpectedVersion + " (0.6.1)\n", "")
            : IsScanRootProbe(exec)
                ? new SandboxExecResult(0, "/work\n", "")
                : new SandboxExecResult(0, "", "");

    private static bool IsStaticcheckPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("staticcheck", StringComparer.Ordinal);

    private static bool IsGoPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("go", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "staticcheck" && exec.Argv[1] == "-version";

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsPreconditionProbe(SandboxExec exec)
        => IsStaticcheckPresenceProbe(exec) || IsGoPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec);

    private static async Task<string> SeedStaticcheckFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-staticcheck-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "go.mod"),
            "module example.com/codeyboxfixture\n\ngo 1.24\n");

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "main.go"),
                "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"hi\")\n}\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "main.go"),
                "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"hi\")\n\tfmt.Sprintf(\"hello\")\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledStaticcheckVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "staticcheck",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-version");
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
