using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.OasdiffAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the oasdiff auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming oasdiff (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (1 = --fail-on WARN tripped); 100+ typed failures and others are infrastructure.
/// - Exit 1 with a changeless report contradicts the contract and fails closed as infrastructure.
/// - The per-spec JSON report maps to findings with check ids, locations, and mapped severity.
/// - Raw tool levels (numeric 1/2/3) are mapped to <see cref="AuditSeverity"/> (never passed through).
/// - Baseline resolution (merge-base default, BaseRef/--base overrides), spec discovery, and the
///   repository-config suppression gate.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution test under [Trait("requires_oasdiff", "true")].
/// </summary>
public sealed class OasdiffAuditorTests
{
    private const string BaseSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string? InstalledOasdiffVersion = ProbeInstalledOasdiffVersion();

    private const string ReportWithBreakingChanges = """
        === openapi.yaml ===
        [{"id":"api-path-removed-without-deprecation","text":"removed the path '/pets'","level":3,"operation":"GET","path":"/pets","section":"paths","revisionSource":{"file":"openapi.yaml","line":12,"column":3}},{"id":"request-property-became-required","text":"added the required property 'tag'","level":2,"operation":"POST","path":"/pets","revisionSource":{"file":"api/swagger.yaml","line":40}}]
        === api/new-openapi.yaml ===
        new file, not in base ref, skipped
        === api/swagger.yaml ===
        [{"id":"response-success-status-removed","text":"removed the success response with the status '200'","level":3,"operation":"GET","path":"/orders","baseSource":{"file":"api/swagger.yaml","line":77}}]
        """;

    private const string ReportClean = """
        === openapi.yaml ===
        []
        === api/swagger.yaml ===
        []
        """;

    private const string ReportWithLevels = """
        === openapi.yaml ===
        [{"id":"check-err","text":"err-level change","level":3,"revisionSource":{"file":"openapi.yaml","line":5}},{"id":"check-warn","text":"warn-level change","level":2,"revisionSource":{"file":"openapi.yaml","line":9}},{"id":"check-info","text":"info-level change","level":1,"revisionSource":{"file":"openapi.yaml","line":13}},{"id":"check-unknown","text":"change at an unmapped level","level":7,"revisionSource":{"file":"openapi.yaml","line":17}}]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingOasdiff_NeverAPass()
    {
        var handler = new Handler { PresenceExitCode = 1 };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingOasdiff()
    {
        var handler = new Handler { VersionExitCode = 127, VersionStdout = "" };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var handler = new Handler { VersionStdout = "oasdiff version 1.20.0\n" };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.20.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(OasdiffAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task Fixture_WithBreakingChanges_YieldsFindings_WithRuleIdAndLocation()
    {
        var handler = new Handler
        {
            DiscoveryOutput = "openapi.yaml\0api/new-openapi.yaml\0api/swagger.yaml\0",
            PresentNames = { "api/new-openapi.yaml", "api/swagger.yaml" },
            ScanExitCode = 1,
            ScanStdout = ReportWithBreakingChanges,
        };

        IAuditor auditor = new OasdiffAuditor();
        var result = await auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var errFinding = Assert.Single(result.Findings,
            f => f.Title.Contains("api-path-removed-without-deprecation", StringComparison.Ordinal));
        Assert.Equal("codeybox:oasdiff", errFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, errFinding.Severity);
        Assert.Equal("openapi.yaml:12", errFinding.Location);
        Assert.Contains("GET /pets", errFinding.Description, StringComparison.Ordinal);

        var warnFinding = Assert.Single(result.Findings,
            f => f.Title.Contains("request-property-became-required", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warnFinding.Severity);
        Assert.Equal("api/swagger.yaml:40", warnFinding.Location);

        var baseSourced = Assert.Single(result.Findings,
            f => f.Title.Contains("response-success-status-removed", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, baseSourced.Severity);
        Assert.Equal("api/swagger.yaml:77", baseSourced.Location);

        Assert.NotNull(handler.ScanExec);
        var argv = handler.ScanExec!.Argv;
        Assert.Equal("oasdiff", argv[0]);
        Assert.Equal("breaking-files", argv[1]);
        AssertFlagValue(argv, "--fail-on", "WARN");
        AssertFlagValue(argv, "--format", "json");
        Assert.Contains("--allow-external-refs=false", argv);
        AssertFlagValue(argv, "--base", BaseSha);
        Assert.Contains("openapi.yaml", argv);
        Assert.Contains("api/new-openapi.yaml", argv);
        Assert.Contains("api/swagger.yaml", argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var handler = new Handler { ScanStdout = ReportClean };

        IAuditor auditor = new OasdiffAuditor();
        var result = await auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NewFileSkipNotice_YieldsNoFindings_AndPasses()
    {
        var handler = new Handler
        {
            ScanStdout = """
                === openapi.yaml ===
                new file, not in base ref, skipped
                """,
        };

        IAuditor auditor = new OasdiffAuditor();
        var result = await auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(102)]
    [InlineData(123)]
    public async Task FailureExitCodes_AreInfrastructure_NotFindings(int exitCode)
    {
        var handler = new Handler
        {
            ScanExitCode = exitCode,
            ScanStdout = "",
            ScanStderr = "failed to load revision spec",
        };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithChangelessReport_ContradictsContract_IsInfrastructureFailure()
    {
        var handler = new Handler { ScanExitCode = 1, ScanStdout = ReportClean };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_NonJsonOutput_IsInfrastructureFailure()
    {
        var handler = new Handler
        {
            ScanExitCode = 1,
            ScanStdout = "=== openapi.yaml ===\nerror\t[oops] something else\n",
        };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawLevelsPassedThrough()
    {
        var handler = new Handler { ScanExitCode = 1, ScanStdout = ReportWithLevels };

        IAuditor auditor = new OasdiffAuditor();
        var result = await auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("check-err")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("check-warn")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("check-info")).Severity);
        // Unmapped level falls back to the declared default, never passed through.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("check-unknown")).Severity);
        Assert.DoesNotContain(result.Findings, f => f.Description.Contains("Severity (tool): 3", StringComparison.Ordinal));
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
            s => s.PluginId == OasdiffAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("oasdiff", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresOasdiffAndGitRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [OasdiffAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == OasdiffAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var oasdiff = Assert.Single(tools, t => t.Binary == "oasdiff");
        Assert.Null(oasdiff.AptPackage);
        var git = Assert.Single(tools, t => t.Binary == "git");
        Assert.Null(git.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var verificationArgv = contributions.VerificationCommands
            .SelectMany(static v => v.Argv)
            .ToList();
        Assert.Contains("oasdiff", verificationArgv);
        Assert.Contains("git", verificationArgv);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public async Task ScopedConfiguration_SpecPathsAndBaseRef_SkipDiscoveryAndMergeBaseProbes()
    {
        var handler = new Handler
        {
            ScanStdout = ReportClean,
            PresentNames = { "apis/petstore.openapi.yml" },
        };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpecPaths"] = "apis/petstore.openapi.yml",
                ["Scoped:BaseRef"] = "release-2026.08",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(
            handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(handler.ScanExec);
        var argv = handler.ScanExec!.Argv;
        AssertFlagValue(argv, "--base", "release-2026.08");
        Assert.Contains("apis/petstore.openapi.yml", argv);
        // No git probes ran: an explicit base ref needs no merge-base, and a
        // configured spec list needs no ls-files discovery.
        Assert.DoesNotContain(handler.Execs, e => e.Argv[0] == "git");
    }

    [Fact]
    public async Task ScopedConfiguration_BaseRefInBothChannels_IsDeterministicFailure()
    {
        var handler = new Handler { ScanStdout = ReportClean };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaseRef"] = "release-2026.08",
                ["Scoped:ExtraArguments"] = "--base,main",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(
                handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task NoResolvableBaseline_IsDeterministicInfrastructureFailure()
    {
        var handler = new Handler { GitExitCode = 128, GitStdout = "" };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("oasdiff", ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task NoSpecsDiscovered_IsDeterministicInfrastructureFailure_NeverAPass()
    {
        var handler = new Handler { DiscoveryOutput = "" };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(OasdiffAuditor.SpecPathsKey, ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task Discovery_ExcludesVendoredAndNonArgumentPaths()
    {
        var handler = new Handler
        {
            DiscoveryOutput =
                "openapi.yaml\0vendor/petstore-openapi.yaml\0node_modules/pkg/swagger.json\0"
                + "api:old-openapi.yaml\0--weird-openapi.yaml\0",
            ScanStdout = ReportClean,
        };

        IAuditor auditor = new OasdiffAuditor();
        var result = await auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(handler.ScanExec);
        var argv = handler.ScanExec!.Argv;
        Assert.Contains("openapi.yaml", argv);
        Assert.DoesNotContain("vendor/petstore-openapi.yaml", argv);
        Assert.DoesNotContain("node_modules/pkg/swagger.json", argv);
        Assert.DoesNotContain("api:old-openapi.yaml", argv);
        Assert.DoesNotContain("--weird-openapi.yaml", argv);
    }

    [Fact]
    public async Task ConfiguredSpecPath_NotInWorktree_IsDeterministicFailure()
    {
        var handler = new Handler { ScanStdout = ReportClean };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpecPaths"] = "apis/missing-openapi.yaml",
                ["Scoped:BaseRef"] = "main",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(
                handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("apis/missing-openapi.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task InvalidConfiguredSpecPath_IsDeterministicFailure()
    {
        var handler = new Handler { ScanStdout = ReportClean };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SpecPaths"] = "apis/../outside-openapi.yaml",
                ["Scoped:BaseRef"] = "main",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(
                handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task RepositoryOasdiffConfig_FailsClosed_BeforeScan()
    {
        var handler = new Handler
        {
            ScanStdout = ReportClean,
            PresentNames = { "openapi.yaml", ".oasdiff.yaml" },
        };

        IAuditor auditor = new OasdiffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(".oasdiff.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains(OasdiffAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task RepositoryOasdiffConfig_WhenTrusted_RunsScan()
    {
        var handler = new Handler
        {
            ScanStdout = ReportClean,
            PresentNames = { "openapi.yaml", ".oasdiff.yaml" },
        };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(
            handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(handler.ScanExec);
    }

    [Fact]
    public async Task ExtraArguments_FormatOverride_IsDeterministicFailure()
    {
        var handler = new Handler { ScanStdout = ReportClean };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--format,yaml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(
                handler.Sandbox(), "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Null(handler.ScanExec);
    }

    [Fact]
    public async Task ExtraArguments_AreAppendedToScanArgv()
    {
        var handler = new Handler { ScanStdout = ReportClean };

        var auditor = new OasdiffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--stability-level,beta",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(
            handler.Sandbox(), "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(handler.ScanExec);
        AssertFlagValue(handler.ScanExec!.Argv, "--stability-level", "beta");
    }

    [Fact]
    [Trait("requires_oasdiff", "true")]
    public async Task RealOasdiff_BreakingSpecFixture_YieldsFinding()
    {
        var installed = InstalledOasdiffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedOasdiffFixtureRepoAsync(breaking: true);
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

            var auditor = new OasdiffAuditor();
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
            Assert.Contains(result.Findings, f =>
                f.Title.Contains("api-path-removed", StringComparison.Ordinal)
                && f.Severity == AuditSeverity.Error);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_oasdiff", "true")]
    public async Task RealOasdiff_UnchangedSpec_Passes()
    {
        var installed = InstalledOasdiffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedOasdiffFixtureRepoAsync(breaking: false);
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

            var auditor = new OasdiffAuditor();
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

    private static void AssertFlagValue(IReadOnlyList<string> argv, string flag, string expected)
    {
        var index = argv.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < argv.Count, $"argv lacks '{flag} {expected}'");
        Assert.Equal(expected, argv[index + 1]);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.OasdiffAuditorPlugin.dll");
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
            PluginId: OasdiffAuditor.PluginId,
            PluginDisplayName: "CodeyBox: oasdiff OpenAPI Breaking Changes",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static async Task<string> SeedOasdiffFixtureRepoAsync(bool breaking)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-oasdiff-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        const string baseSpec = """
            openapi: 3.0.0
            info:
              title: Fixture API
              version: 1.0.0
            paths:
              /pets:
                get:
                  responses:
                    '200':
                      description: ok
            """;

        await GitAsync(dir, "init -b main");
        await GitAsync(dir, "config user.email test@example.invalid");
        await GitAsync(dir, "config user.name test");
        await File.WriteAllTextAsync(Path.Combine(dir, "openapi.yaml"), baseSpec);
        await GitAsync(dir, "add openapi.yaml");
        await GitAsync(dir, "commit -m baseline");

        if (breaking)
        {
            const string revisionSpec = """
                openapi: 3.0.0
                info:
                  title: Fixture API
                  version: 1.0.0
                paths: {}
                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "openapi.yaml"), revisionSpec);
        }

        return dir;
    }

    private static async Task GitAsync(string workdir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static string? ProbeInstalledOasdiffVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "oasdiff",
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

    /// <summary>
    /// Scripted <see cref="ISandbox"/> exec routing for the oasdiff auditor's
    /// probe sequence: git baseline/discovery probes, the binary presence
    /// check, the version probe, the repository-file presence probes (spec
    /// existence and the suppression gate share the
    /// <c>RepositoryFilePresenceScript</c> shape — names live at argv[4..]),
    /// then the scan. <see cref="PresentNames"/> is the set of worktree files
    /// the presence probes report as existing.
    /// </summary>
    private sealed class Handler
    {
        public List<SandboxExec> Execs { get; } = [];
        public SandboxExec? ScanExec { get; private set; }
        public HashSet<string> PresentNames { get; } = new(StringComparer.Ordinal) { "openapi.yaml" };
        public int PresenceExitCode { get; init; }
        public int VersionExitCode { get; init; }
        public string VersionStdout { get; init; } =
            "oasdiff version " + OasdiffAuditor.DefaultExpectedVersion + "\n";
        public int GitExitCode { get; init; }
        public string GitStdout { get; init; } = BaseSha + "\n";
        public int DiscoveryExitCode { get; init; }
        public string DiscoveryOutput { get; init; } = "openapi.yaml\0";
        public int ScanExitCode { get; init; }
        public string ScanStdout { get; init; } = ReportClean;
        public string ScanStderr { get; init; } = "";

        public ISandbox Sandbox() => new FakeSandbox(Handle);

        private Task<SandboxExecResult> Handle(SandboxExec exec, CancellationToken ct)
        {
            Execs.Add(exec);
            var argv = exec.Argv;

            if (argv.Count >= 2 && argv[0] == "oasdiff" && argv[1] == "--version")
                return Task.FromResult(new SandboxExecResult(VersionExitCode, VersionStdout, ""));
            if (argv.Count >= 2 && argv[0] == "oasdiff" && argv[1] == "breaking-files")
            {
                ScanExec = exec;
                return Task.FromResult(new SandboxExecResult(ScanExitCode, ScanStdout, ScanStderr));
            }
            if (argv.Count >= 2 && argv[0] == "git" && argv[1] == "ls-files")
                return Task.FromResult(new SandboxExecResult(DiscoveryExitCode, DiscoveryOutput, ""));
            if (argv[0] == "git")
                return Task.FromResult(new SandboxExecResult(GitExitCode, GitStdout, ""));
            if (argv.Count >= 3 && argv[0] == "sh" && argv[2].Contains("command -v", StringComparison.Ordinal))
                return Task.FromResult(new SandboxExecResult(PresenceExitCode, "", ""));
            if (argv.Count >= 4 && argv[0] == "sh")
            {
                var present = argv.Skip(4).Where(PresentNames.Contains).ToList();
                return Task.FromResult(new SandboxExecResult(
                    0, present.Count == 0 ? "" : string.Join("\n", present) + "\n", ""));
            }
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }
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
