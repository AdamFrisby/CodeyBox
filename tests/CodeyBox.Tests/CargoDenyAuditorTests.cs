using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.CargoDenyAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the cargo-deny auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cargo-deny (never a pass or finding).
/// - cargo-deny's check exit code is a bitset of failing checks (advisories 0x1, bans 0x2,
///   licenses 0x4, sources 0x8) — every value 0..15 is findings-producing; the
///   {"type":"summary"} stderr record is the discriminator between "ran" and
///   "could not run" (a run failure also exits 1 but emits only a log record).
/// - NDJSON diagnostics on stderr map to findings with cargo-deny/&lt;code&gt; rule ids;
///   cargo-deny's severity tokens go through the declared mapping, never raw.
/// - deny.exceptions.toml is a repo-controlled suppression surface and fails closed.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_cargo_deny", "true")] need
///   cargo-deny AND cargo on PATH and run offline with a path-dependency fixture.
/// </summary>
public sealed class CargoDenyAuditorTests
{
    private static readonly string? InstalledCargoDenyVersion = ProbeInstalledVersion("cargo-deny", "--version");
    private static readonly bool CargoAvailable = ProbeInstalledVersion("cargo", "--version") is not null;

    private const string NdjsonWithFindings =
        """
        {"type":"log","fields":{"timestamp":"2026-09-25T00:00:00Z","level":"INFO","message":"gathering crates for /work/Cargo.toml"}}
        {"type":"diagnostic","fields":{"severity":"error","code":"vulnerability","message":"vulnerability found in crate 'oldlib'","labels":[{"message":"oldlib = 0.1.0","span":"oldlib 0.1.0","line":4,"column":1}],"advisory":{"id":"RUSTSEC-2024-0001","title":"Oldlib buffer overflow"}}}
        {"type":"diagnostic","fields":{"severity":"error","code":"banned","message":"crate 'denyme' is explicitly banned","labels":[{"message":"banned crate","span":"denyme","line":7,"column":1}]}}
        {"type":"diagnostic","fields":{"severity":"error","code":"unlicensed","message":"license expression was not found for crate 'noCrate'"}}
        {"type":"diagnostic","fields":{"severity":"warning","code":"source-not-allowed","message":"crate 'gitcrate' uses a git source not in the allow list","labels":[{"message":"source","span":"git+https://example.com/gitcrate","line":9,"column":1}]}}
        {"type":"summary","fields":{"advisories":{"errors":1,"warnings":0,"notes":0,"helps":0},"bans":{"errors":1,"warnings":0,"notes":0,"helps":0},"licenses":{"errors":1,"warnings":0,"notes":0,"helps":0},"sources":{"errors":0,"warnings":1,"notes":0,"helps":0}}}
        """;

    private const string NdjsonClean =
        """
        {"type":"log","fields":{"timestamp":"2026-09-25T00:00:00Z","level":"INFO","message":"gathering crates for /work/Cargo.toml"}}
        {"type":"summary","fields":{"advisories":{"errors":0,"warnings":0,"notes":0,"helps":0},"bans":{"errors":0,"warnings":0,"notes":0,"helps":0},"licenses":{"errors":0,"warnings":0,"notes":0,"helps":0},"sources":{"errors":0,"warnings":0,"notes":0,"helps":0}}}
        """;

    private const string NdjsonRunFailure =
        """
        {"type":"log","fields":{"timestamp":"2026-09-25T00:00:00Z","level":"ERROR","message":"failed to parse config: TOML parse error at line 3"}}
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCargoDeny_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "cargo-deny: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-deny", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "cargo-deny 0.19.9\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-deny", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.19.9", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CargoDenyAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleId()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // Bitset exit: advisories(1) | licenses(4) | bans(2) errored -> 7.
            return Task.FromResult(new SandboxExecResult(7, "", NdjsonWithFindings));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);

        var vulnerability = Assert.Single(
            result.Findings, f => f.Title.Contains("cargo-deny/vulnerability", StringComparison.Ordinal));
        Assert.Equal("codeybox:cargo-deny", vulnerability.AuditorName);
        Assert.Equal(AuditSeverity.Error, vulnerability.Severity);
        Assert.Contains("RUSTSEC-2024-0001", vulnerability.Description, StringComparison.Ordinal);
        Assert.Contains("line 4", vulnerability.Description, StringComparison.Ordinal);

        Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/banned", StringComparison.Ordinal));
        Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/unlicensed", StringComparison.Ordinal));

        var source = Assert.Single(
            result.Findings, f => f.Title.Contains("cargo-deny/source-not-allowed", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, source.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("cargo-deny", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "json");
        var checkIndex = argv.ToList().IndexOf("check");
        Assert.True(checkIndex > formatIndex, "the check subcommand must follow the root options");
        Assert.Contains("--hide-inclusion-graph", argv.Skip(checkIndex));
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_WithSummary_ReportsFindings()
    {
        // Bitset exit 4 = licenses produced errors; a completed run's summary
        // on stderr is what makes this a verdict rather than a failure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, "", NdjsonWithFindings));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutSummary_IsInfrastructureFailure()
    {
        // cargo-deny exits 1 on ANY run failure (config parse, cargo metadata,
        // advisory fetch) — ambiguous with the advisories bit (0x1). Only a
        // "log" ERROR record is emitted; no summary means "could not run".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", NdjsonRunFailure));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-deny", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageError_InsideBitsetRange_StillIsInfrastructureFailure()
    {
        // Clap usage errors exit 2 — which is also the bans bit (0x2), so it
        // sits inside the findings exit range. The discriminator holds: a
        // usage error writes text, no summary record, so the run fails closed
        // as infrastructure through the parser rather than surfacing as a
        // bans finding.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "error: unexpected argument"));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-deny", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode16_OutsideBitset_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(16, "", "unexpected exit"));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-deny", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 16", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "cargo-deny: command not found"));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-deny", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_NoRawStringsPassedThrough()
    {
        const string ndjson =
            """
            {"type":"diagnostic","fields":{"severity":"error","code":"banned","message":"crate denied"}}
            {"type":"diagnostic","fields":{"severity":"warning","code":"unmatched-skip","message":"skip entry never matched"}}
            {"type":"diagnostic","fields":{"severity":"note","code":"allowed","message":"crate explicitly allowed"}}
            {"type":"diagnostic","fields":{"severity":"brand-new-future-level","code":"future","message":"unrecognized level"}}
            {"type":"summary","fields":{"bans":{"errors":1,"warnings":1,"notes":1,"helps":0}}}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", ndjson));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/banned")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/unmatched-skip")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/allowed")).Severity);
        // Unknown tool level falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/future")).Severity);
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping.
        var banned = Assert.Single(result.Findings, f => f.Title.Contains("cargo-deny/banned"));
        Assert.Contains("Severity (tool): error", banned.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionsFile_FailsClosed_AsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "deny.exceptions.toml\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        IAuditor auditor = new CargoDenyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("deny.exceptions.toml", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExceptionsFile_TrustedViaScopedConfig_ScanProceeds()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        var auditor = new CargoDenyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_Checks_And_RootFlags_ShapeTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        var auditor = new CargoDenyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Checks"] = "licenses, sources",
                ["Scoped:ConfigPath"] = "/opt/policy/deny.toml",
                ["Scoped:ManifestPath"] = "crates/app/Cargo.toml",
                ["Scoped:Offline"] = "true",
                ["Scoped:Locked"] = "true",
                ["Scoped:Targets"] = "x86_64-unknown-linux-gnu",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var checkIndex = argv.IndexOf("check");
        Assert.True(checkIndex > 0);

        // Root-level flags precede the subcommand; WHICH names follow it.
        Assert.True(argv.IndexOf("--offline") >= 0 && argv.IndexOf("--offline") < checkIndex);
        Assert.True(argv.IndexOf("--locked") >= 0 && argv.IndexOf("--locked") < checkIndex);
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex < checkIndex
            && argv[configIndex + 1] == "/opt/policy/deny.toml");
        var manifestIndex = argv.IndexOf("--manifest-path");
        Assert.True(manifestIndex >= 0 && manifestIndex < checkIndex
            && argv[manifestIndex + 1] == "crates/app/Cargo.toml");
        var targetIndex = argv.IndexOf("--target");
        Assert.True(targetIndex >= 0 && targetIndex < checkIndex
            && argv[targetIndex + 1] == "x86_64-unknown-linux-gnu");
        Assert.True(argv.IndexOf("licenses") > checkIndex);
        Assert.True(argv.IndexOf("sources") > checkIndex);
    }

    [Fact]
    public async Task ScopedConfiguration_InvalidCheck_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", NdjsonClean));
        });

        var auditor = new CargoDenyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Checks"] = "licenses, bogus",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Checks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(7, "", NdjsonWithFindings));
        });

        var auditor = new CargoDenyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "cargo-deny/vulnerability,cargo-deny/banned,cargo-deny/unlicensed",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the warning-level sources diagnostic survives the exclusion.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsWarnings()
    {
        const string warningsOnly =
            """
            {"type":"diagnostic","fields":{"severity":"warning","code":"unmatched-skip","message":"skip entry never matched"}}
            {"type":"summary","fields":{"bans":{"errors":0,"warnings":1,"notes":0,"helps":0}}}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", warningsOnly));
        });

        var auditor = new CargoDenyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolsAbsentFromBaselineProvisioning()
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
            s => s.PluginId == CargoDenyAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cargo-deny", flattened, StringComparison.Ordinal);
        Assert.DoesNotContain("cargo", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CargoDenyAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CargoDenyAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        Assert.Equal(2, tools.Count);
        var cargoDeny = Assert.Single(tools, t => t.Binary == "cargo-deny");
        var cargo = Assert.Single(tools, t => t.Binary == "cargo");
        // Verify-only by design: no distro package carries a pinned
        // cargo-deny, and cargo comes with the Rust toolchain — both must be
        // provisioned into the baseline by the operator.
        Assert.Null(cargoDeny.AptPackage);
        Assert.Null(cargo.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Equal(2, contributions.VerificationCommands.Count);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("cargo-deny", verification, StringComparison.Ordinal);
        Assert.Contains("cargo", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_cargo_deny", "true")]
    public async Task RealCargoDeny_BannedPathDependency_ProducesFinding()
    {
        var installed = InstalledCargoDenyVersion;
        if (installed is null || !CargoAvailable)
            return;

        var fixtureDir = await SeedCargoDenyFixtureRepoAsync(bannedDep: true);

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

            var auditor = new CargoDenyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    // bans only, offline: a path dependency needs no registry
                    // index and no advisory database.
                    ["Scoped:Checks"] = "bans",
                    ["Scoped:Offline"] = "true",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("cargo-deny/banned", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("denyme", finding.Description, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cargo_deny", "true")]
    public async Task RealCargoDeny_CleanFixture_Passes()
    {
        var installed = InstalledCargoDenyVersion;
        if (installed is null || !CargoAvailable)
            return;

        var fixtureDir = await SeedCargoDenyFixtureRepoAsync(bannedDep: false);

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

            var auditor = new CargoDenyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Checks"] = "bans",
                    ["Scoped:Offline"] = "true",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CargoDenyAuditorPlugin.dll");
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
            PluginId: CargoDenyAuditor.PluginId,
            PluginDisplayName: "CodeyBox: cargo-deny Rust Dependency Policy",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "cargo-deny " + CargoDenyAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cargo-deny", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cargo-deny" && exec.Argv[1] == "--version";

    private static bool IsRepoFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("-e", StringComparison.Ordinal)
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static async Task<string> SeedCargoDenyFixtureRepoAsync(bool bannedDep)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-cargo-deny-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "denyme"));
        Directory.CreateDirectory(Path.Combine(dir, "app"));

        // A path dependency keeps `cargo metadata` fully offline — no
        // registry index, no lockfile resolution against crates.io.
        await File.WriteAllTextAsync(Path.Combine(dir, "denyme", "Cargo.toml"), """
            [package]
            name = "denyme"
            version = "0.1.0"
            edition = "2021"
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "denyme", "lib.rs"), "pub fn f() {}\n");

        var depBlock = bannedDep
            ? "[dependencies]\ndenyme = { path = \"../denyme\" }\n"
            : "[dependencies]\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "app", "Cargo.toml"), $$"""
            [package]
            name = "app"
            version = "0.1.0"
            edition = "2021"

            {{depBlock}}
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "app", "lib.rs"), "pub fn g() {}\n");

        var denyToml = bannedDep
            ? """
              [bans]
              deny = [{ name = "denyme" }]
              """
            : """
              [bans]
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "deny.toml"), denyToml);
        await File.WriteAllTextAsync(Path.Combine(dir, "Cargo.toml"), """
            [workspace]
            members = ["app", "denyme"]
            resolver = "2"
            """);

        return dir;
    }

    private static string? ProbeInstalledVersion(string binary, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(argument);
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
