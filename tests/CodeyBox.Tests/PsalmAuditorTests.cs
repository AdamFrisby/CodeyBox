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
/// Covers the Psalm auditor plugin:
/// - Missing psalm binary (or the php interpreter its phar/binstub shebang needs,
///   which surfaces through the version probe) and wrong version are infrastructure
///   failures naming the tool (never a pass or finding).
/// - Exit code 2 is the documented "found issues" verdict; exit 1 is "could not
///   run" (missing config, bad flags, internal errors) and is infrastructure.
///   Exit 0 + empty array report = pass.
/// - Findings carry the Psalm issue type and a repository-relative file:line
///   location (absolute report paths are relativized against the exec working
///   directory).
/// - Severity mapping: Psalm "error" maps to Error (blocking), "info" maps to
///   Info (advisory) — raw tool severities never pass through.
/// - Default exclusions (vendored + generated trees) and scoped options
///   (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_psalm", "true")].
/// </summary>
public sealed class PsalmAuditorTests
{
    private static readonly string? InstalledPsalmVersion = ProbeInstalledPsalmVersion();

    // Verbatim psalm --output-format=json shape: a JSON array of issue
    // objects with severity/type/message/file_name/line_from.
    private const string JsonWithIssues = """
        [{"severity":"error","line_from":3,"line_to":3,"type":"UndefinedFunction","message":"Function greet does not exist.","file_name":"src/Foo.php","snippet":"greet($x);","link":"https://psalm.dev/001"},{"severity":"info","line_from":10,"line_to":10,"type":"MixedReturnStatement","message":"Could not infer a return type.","file_name":"lib/Util.php","snippet":"return $x;","link":"https://psalm.dev/002"}]
        """;

    private const string JsonClean = """[]""";

    private const string JsonMixedIssues = """
        [{"severity":"error","line_from":3,"line_to":3,"type":"UndefinedFunction","message":"Function greet does not exist.","file_name":"src/Foo.php","snippet":"greet($x);"},{"severity":"info","line_from":10,"line_to":10,"type":"MixedReturnStatement","message":"Could not infer a return type.","file_name":"lib/Util.php","snippet":"return $x;"},{"severity":"error","line_from":1,"line_to":1,"type":"UnusedVariable","message":"$v is never used.","file_name":"src/Bar.php","snippet":"$v = 1;"}]
        """;

    private const string JsonWithFilteredPaths = """
        [{"severity":"error","line_from":2,"line_to":2,"type":"UndefinedFunction","message":"Function isInt does not exist.","file_name":"src/main.php","snippet":"isInt($v);"},{"severity":"error","line_from":1,"line_to":1,"type":"PossiblyUndefinedVariable","message":"Variable $v might not be defined.","file_name":"vendor/lib.php","snippet":"echo $v;"},{"severity":"error","line_from":1,"line_to":1,"type":"PossiblyUndefinedVariable","message":"Variable $v might not be defined.","file_name":"node_modules/pkg/a.php","snippet":"echo $v;"},{"severity":"error","line_from":1,"line_to":1,"type":"PossiblyUndefinedVariable","message":"Variable $v might not be defined.","file_name":"dist/bundle.php","snippet":"echo $v;"}]
        """;

    private const string JsonWithAbsolutePaths = """
        [{"severity":"error","line_from":3,"line_to":3,"type":"UndefinedFunction","message":"Function greet does not exist.","file_name":"/work/src/Foo.php","snippet":"greet($x);"}]
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
                return Task.FromResult(new SandboxExecResult(0, "Psalm 9.9.9\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.9.9", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PsalmAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
    public async Task Fixture_WithAnalysisIssues_YieldsFindings_WithIssueTypeAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(2, JsonWithIssues, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var undefined = Assert.Single(result.Findings, f => f.Title.Contains("UndefinedFunction", StringComparison.Ordinal));
        Assert.Equal("codeybox:psalm", undefined.AuditorName);
        Assert.Equal(AuditSeverity.Error, undefined.Severity);
        Assert.Equal("src/Foo.php:3", undefined.Location);

        var mixed = Assert.Single(result.Findings, f => f.Title.Contains("MixedReturnStatement", StringComparison.Ordinal));
        Assert.Equal("lib/Util.php:10", mixed.Location);
        // Informational Psalm issues stay advisory rather than failing the gate.
        Assert.Equal(AuditSeverity.Info, mixed.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("psalm", scanExec!.Argv[0]);
        var formatIndex = scanExec.Argv.ToList().IndexOf("--output-format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < scanExec.Argv.Count);
        Assert.Equal("json", scanExec.Argv[formatIndex + 1]);
        Assert.Contains("--no-cache", scanExec.Argv);
        // Bare run: the repository's psalm.xml projectFiles declare the scope.
        Assert.DoesNotContain(".", scanExec.Argv);
    }

    [Fact]
    public async Task AbsoluteReportPaths_AreRelativizedAgainstWorkingDirectory()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithAbsolutePaths, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/Foo.php:3", finding.Location);
    }

    [Fact]
    public async Task CleanFixture_EmptyArrayReport_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_WithJsonReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithIssues, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_MissingConfigText_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Psalm exit 1 is "could not run" (missing config, bad flags,
            // internal errors) — printed as text, no report.
            return Task.FromResult(new SandboxExecResult(
                1, "", "Could not locate a config file in the current directory."));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_ZeroFindingReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 2 claims issues; a report containing none is corrupt, not
            // a clean pass — fail closed.
            return Task.FromResult(new SandboxExecResult(2, JsonClean, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no findings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_NonJsonOutput_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                2, "Internal error: something went wrong\n", ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode3_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, "", "Unexpected error."));
        });

        IAuditor auditor = new PsalmAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("psalm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
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
    public async Task SeverityMapping_MapsErrorAndInfoSeparately_NotRaw()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonMixedIssues, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Every Psalm issue maps through the declared map: "error" ->
        // AuditSeverity.Error, "info" -> AuditSeverity.Info — the enum on the
        // finding is the mapped value, the tool's vocabulary survives only as
        // text in the description.
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(2, result.Findings.Count(f => f.Severity == AuditSeverity.Error));
        Assert.Equal(1, result.Findings.Count(f => f.Severity == AuditSeverity.Info));
        var info = Assert.Single(result.Findings, f => f.Title.Contains("MixedReturnStatement", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);
        Assert.Contains("Severity (tool): info", info.Description, StringComparison.Ordinal);
        var error = Assert.Single(result.Findings, f => f.Title.Contains("UndefinedFunction", StringComparison.Ordinal));
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);
        // Info findings are advisory, error findings fail the gate.
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task InfoOnlyFixture_Passes_WithAdvisoryFindings()
    {
        const string infoOnly = """
            [{"severity":"info","line_from":10,"line_to":10,"type":"MixedReturnStatement","message":"Could not infer a return type.","file_name":"lib/Util.php","snippet":"return $x;"}]
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, infoOnly, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
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
    public void EnabledPlugin_DeclaresPsalmRequirement_VerifyOnly_AndPhpRuntime()
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

        var tools = loader.GetEnabledPluginTools();
        var psalm = Assert.Single(tools, t => t.Binary == "psalm");
        // Verify-only by design: no distro package carries a version pin;
        // provisioning is via Composer or the signed phar.
        Assert.Null(psalm.AptPackage);
        var php = Assert.Single(tools, t => t.Binary == "php");
        Assert.Equal("php-cli", php.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("psalm", flattened, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Psalm 9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PsalmAuditor();
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

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/psalm.operator.xml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/psalm.operator.xml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new PsalmAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by the default
        // ExcludePaths; the src/ diagnostic survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/main.php:2", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonWithIssues, ""));
        });

        var auditor = new PsalmAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "MixedReturnStatement",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("MixedReturnStatement", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_psalm", "true")]
    public async Task RealPsalm_ErrorFixture_YieldsFindings()
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
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("bad.php:2", finding.Location);
            Assert.Contains("UndefinedFunction", finding.Title, StringComparison.Ordinal);
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
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_psalm", "true")]
    public async Task RealPsalm_MissingConfig_IsInfrastructureFailure()
    {
        var installed = InstalledPsalmVersion;
        if (installed is null)
            return;

        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-psalm-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "bad.php"), "<?php\nthis_function_does_not_exist(42);\n");

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = dir }],
                },
                CancellationToken.None);

            var auditor = new PsalmAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            // No psalm.xml: Psalm exits 1 with "Could not locate a config
            // file" and no report — the check could not run: infrastructure,
            // never a verdict.
            await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(dir);
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
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Psalm " + PsalmAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "psalm" && exec.Argv[1] == "--version";

    private static async Task<string> SeedPsalmFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-psalm-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "psalm.xml"),
            """
            <?xml version="1.0"?>
            <psalm errorLevel="1" resolveFromConfigFile="false">
              <projectFiles>
                <directory name="." />
              </projectFiles>
            </psalm>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(dir, clean ? "clean.php" : "bad.php"),
            clean ? "<?php\n$greeting = 'hello';\n" : "<?php\nthis_function_does_not_exist(42);\n");
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
