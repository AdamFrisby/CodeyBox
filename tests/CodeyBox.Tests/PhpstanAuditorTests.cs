using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PhpstanAuditorPlugin;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the PHPStan auditor plugin:
/// - Missing phpstan binary (or the php interpreter its phar/binstub shebang needs,
///   which surfaces through the version probe) and wrong version are infrastructure
///   failures naming the tool (never a pass or finding).
/// - Exit code 1 is ambiguous upstream — "found errors" and "could not run" share it —
///   so the JSON report on stdout is the discriminator: valid report = verdict,
///   anything else = infrastructure. Exit 0 + valid empty report = pass.
/// - Findings carry the PHPStan error identifier and a repository-relative
///   file:line location (the report keys files by absolute path, relativized
///   against the exec working directory).
/// - Default exclusions (vendored + generated trees) and scoped options
///   (ExpectedVersion, ConfigPath, Level).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_phpstan", "true")].
/// </summary>
public sealed class PhpstanAuditorTests
{
    private static readonly string? InstalledPhpstanVersion = ProbeInstalledPhpstanVersion();

    // Verbatim phpstan --error-format=json shape: files keyed by absolute
    // path, each with an errors count and a messages array.
    private const string JsonWithIssues = """
        {"totals":{"errors":0,"file_errors":2},"files":{"/work/src/Foo.php":{"errors":1,"messages":[{"message":"Parameter #1 $x of function greet expects int, string given.","line":3,"ignorable":true,"identifier":"argument.type"}]},"/work/lib/Util.php":{"errors":1,"messages":[{"message":"Function helper not found.","line":10,"ignorable":true,"identifier":"function.notFound","tip":"Check the function name spelling."}]}},"errors":[]}
        """;

    private const string JsonClean = """{"totals":{"errors":0,"file_errors":0},"files":{},"errors":[]}""";

    private const string JsonWithNonFileError = """
        {"totals":{"errors":1,"file_errors":0},"files":{},"errors":["Ignored error pattern #nope# was not matched in reported errors."]}
        """;

    private const string JsonMixedIssues = """
        {"totals":{"errors":1,"file_errors":2},"files":{"/work/src/Foo.php":{"errors":1,"messages":[{"message":"Parameter #1 $x of function greet expects int, string given.","line":3,"ignorable":true,"identifier":"argument.type"}]},"/work/lib/Util.php":{"errors":1,"messages":[{"message":"Function helper not found.","line":10,"ignorable":true,"identifier":"function.notFound"}]}},"errors":["Ignored error pattern #nope# was not matched in reported errors."]}
        """;

    private const string JsonWithFilteredPaths = """
        {"totals":{"errors":0,"file_errors":4},"files":{"/work/src/main.php":{"errors":1,"messages":[{"message":"Call to function isInt() with int will always evaluate to true.","line":2,"ignorable":true,"identifier":"function.alreadyNarrowedType"}]},"/work/vendor/lib.php":{"errors":1,"messages":[{"message":"Variable $v might not be defined.","line":1,"ignorable":true,"identifier":"variable.undefined"}]},"/work/node_modules/pkg/a.php":{"errors":1,"messages":[{"message":"Variable $v might not be defined.","line":1,"ignorable":true,"identifier":"variable.undefined"}]},"/work/dist/bundle.php":{"errors":1,"messages":[{"message":"Variable $v might not be defined.","line":1,"ignorable":true,"identifier":"variable.undefined"}]}},"errors":[]}
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingPhpstan_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "phpstan: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingPhpstan()
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

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "PHPStan - PHP Static Analysis Tool 9.9.9\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.9.9", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PhpstanAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new PhpstanAuditor();
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
    public async Task Fixture_WithAnalysisErrors_YieldsFindings_WithIdentifierAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var argumentType = Assert.Single(result.Findings, f => f.Title.Contains("argument.type", StringComparison.Ordinal));
        Assert.Equal("codeybox:phpstan", argumentType.AuditorName);
        Assert.Equal(AuditSeverity.Error, argumentType.Severity);
        // The report keys files by absolute path; findings are relativized
        // against the exec working directory.
        Assert.Equal("src/Foo.php:3", argumentType.Location);

        var notFound = Assert.Single(result.Findings, f => f.Title.Contains("function.notFound", StringComparison.Ordinal));
        Assert.Equal("lib/Util.php:10", notFound.Location);
        // The tip PHPStan attached survives inside the finding description.
        Assert.Contains("Check the function name spelling.", notFound.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("phpstan", scanExec!.Argv[0]);
        Assert.Equal("analyse", scanExec.Argv[1]);
        var formatIndex = scanExec.Argv.ToList().IndexOf("--error-format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < scanExec.Argv.Count);
        Assert.Equal("json", scanExec.Argv[formatIndex + 1]);
        Assert.Contains("--no-progress", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task NonFileSpecificErrors_BecomeFindings_WithoutLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithNonFileError, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Null(finding.Location);
        Assert.Contains("Ignored error pattern", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_EmptyReport_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithJsonReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // PHPStan exit 1 is ambiguous: it is also every "could not run"
            // (missing config, zero files) and internal-error exit — printed
            // as plain text, no report. No JSON => infrastructure.
            return Task.FromResult(new SandboxExecResult(
                1, "", "Project config file at path /work/phpstan.neon does not exist."));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_InternalErrorText_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Internal errors short-circuit before the JSON formatter: the
            // message goes to stdout as text and the command returns 1 — no
            // report, so the analysis is incomplete, not a verdict.
            return Task.FromResult(new SandboxExecResult(
                1, "Internal error: Child process timed out while analysing src/Foo.php\n", ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_ZeroFindingReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 claims errors; a report containing none is corrupt, not
            // a clean pass — fail closed.
            return Task.FromResult(new SandboxExecResult(1, JsonClean, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no findings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "Unexpected error."));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "phpstan: command not found"));
        });

        IAuditor auditor = new PhpstanAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("phpstan", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsReportedErrorsToErrorSeverity_NotRaw()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonMixedIssues, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Every PHPStan issue maps through the declared map ("error" ->
        // AuditSeverity.Error): the enum on the finding is the mapped value —
        // the tool's vocabulary survives only as text in the description.
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.All(result.Findings, f =>
            Assert.Contains("Severity (tool): error", f.Description, StringComparison.Ordinal));
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
            s => s.PluginId == PhpstanAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("phpstan", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresPhpstanRequirement_VerifyOnly_AndPhpRuntime()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PhpstanAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PhpstanAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var phpstan = Assert.Single(tools, t => t.Binary == "phpstan");
        // Verify-only by design: no distro package carries a version pin;
        // provisioning is via Composer or the signed phar.
        Assert.Null(phpstan.AptPackage);
        var php = Assert.Single(tools, t => t.Binary == "php");
        Assert.Equal("php-cli", php.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("phpstan", flattened, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "PHPStan - PHP Static Analysis Tool 9.9.9\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PhpstanAuditor();
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

        var auditor = new PhpstanAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/phpstan.operator.neon",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("-c");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/phpstan.operator.neon", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_Level_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PhpstanAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Level"] = "max",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var levelIndex = argv.ToList().IndexOf("-l");
        Assert.True(levelIndex >= 0 && levelIndex + 1 < argv.Count);
        Assert.Equal("max", argv[levelIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_InvalidLevel_IsDeterministicInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new PhpstanAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Level"] = "eleven",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Level", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new PhpstanAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, node_modules/, dist/ findings are dropped by the default
        // ExcludePaths; the src/ diagnostic survives — only possible because
        // the absolute report paths were relativized against the workdir.
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
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        var auditor = new PhpstanAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "function.notFound",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("function.notFound", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_phpstan", "true")]
    public async Task RealPhpstan_ErrorFixture_YieldsFindings()
    {
        var installed = InstalledPhpstanVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPhpstanFixtureRepoAsync(clean: false);

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

            var auditor = new PhpstanAuditor();
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
            Assert.Contains("function.notFound", finding.Title, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_phpstan", "true")]
    public async Task RealPhpstan_CleanFixture_Passes()
    {
        var installed = InstalledPhpstanVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPhpstanFixtureRepoAsync(clean: true);

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

            var auditor = new PhpstanAuditor();
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
    [Trait("requires_phpstan", "true")]
    public async Task RealPhpstan_NoPhpSources_IsInfrastructureFailure()
    {
        var installed = InstalledPhpstanVersion;
        if (installed is null)
            return;

        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-phpstan-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "README.txt"), "no php here\n");

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

            var auditor = new PhpstanAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            // "No files found to analyse." exits 1 with no JSON report —
            // the check could not run: infrastructure, never a verdict.
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PhpstanAuditorPlugin.dll");
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
            PluginId: PhpstanAuditor.PluginId,
            PluginDisplayName: "CodeyBox: PHPStan PHP Static Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "PHPStan - PHP Static Analysis Tool " + PhpstanAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "phpstan" && exec.Argv[1] == "--version";

    private static async Task<string> SeedPhpstanFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-phpstan-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // Level 0 (the config-less default) already reports calls to
        // undefined functions; the clean fixture raises nothing at any level.
        await File.WriteAllTextAsync(
            Path.Combine(dir, clean ? "clean.php" : "bad.php"),
            clean ? "<?php\n$greeting = 'hello';\n" : "<?php\nthis_function_does_not_exist(42);\n");
        return dir;
    }

    private static string? ProbeInstalledPhpstanVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "phpstan",
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
