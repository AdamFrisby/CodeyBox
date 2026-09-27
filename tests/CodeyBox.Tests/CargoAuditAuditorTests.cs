using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.CargoAuditAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the cargo-audit auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cargo-audit (never a pass or finding).
/// - cargo-audit's exit convention is NOT the common 0/1/2 one: 0 = report generated
///   and clean, 1 = report generated with vulnerabilities OR advisory-db fetch/load
///   failure, 2 = could not run (usage error, lockfile load failure). The SARIF
///   document on stdout is the discriminator between "ran" and "could not run".
/// - SARIF results map to findings with advisory-id rule ids and the Cargo.lock
///   location; SARIF levels go through the declared mapping, never raw.
/// - .cargo/audit.toml is a repo-controlled suppression surface and fails closed.
/// - --file is always passed so the cargo-update lockfile-generation path never runs.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_cargo_audit", "true")] need
///   cargo-audit on PATH and run fully offline against a seeded advisory database.
/// </summary>
public sealed class CargoAuditAuditorTests
{
    private static readonly string? InstalledCargoAuditVersion = ProbeInstalledVersion("cargo-audit", "--version");

    private const string SarifWithFindings =
        """
        {"$schema":"https://json.schemastore.org/sarif-2.1.0.json","version":"2.1.0","runs":[{"tool":{"driver":{"name":"cargo-audit","version":"0.22.2","semanticVersion":"0.22.2","rules":[{"id":"RUSTSEC-2020-0071","name":"RUSTSEC-2020-0071","shortDescription":{"text":"Potential segfault in the time crate"},"defaultConfiguration":{"level":"error"},"properties":{"tags":["security","vulnerability"],"precision":"very-high","security-severity":"7.5"}},{"id":"yanked","name":"yanked","shortDescription":{"text":"Package version has been yanked from the registry"},"defaultConfiguration":{"level":"warning"},"properties":{"tags":["security","warning"],"precision":"high","problem.severity":"warning"}}]}},"results":[{"ruleId":"RUSTSEC-2020-0071","message":{"text":"time 0.1.45 is vulnerable to RUSTSEC-2020-0071 (Potential segfault in the time crate)"},"level":"error","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}],"partialFingerprints":{"cargo-audit/advisory-fingerprint":"RUSTSEC-2020-0071:time:0.1.45"}},{"ruleId":"yanked","message":{"text":"oldlib 0.1.0 has a yanked warning"},"level":"warning","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}],"partialFingerprints":{"cargo-audit/advisory-fingerprint":"yanked:oldlib:0.1.0"}}]}]}
        """;

    private const string SarifClean =
        """
        {"$schema":"https://json.schemastore.org/sarif-2.1.0.json","version":"2.1.0","runs":[{"tool":{"driver":{"name":"cargo-audit","version":"0.22.2","semanticVersion":"0.22.2","rules":[]}},"results":[]}]}
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCargoAudit_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "cargo-audit: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-audit", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "cargo-audit 0.21.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.21.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CargoAuditAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // cargo-audit exits 1 when the generated report has vulnerabilities.
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var vulnerability = Assert.Single(
            result.Findings, f => f.Title.Contains("RUSTSEC-2020-0071", StringComparison.Ordinal));
        Assert.Equal("codeybox:cargo-audit", vulnerability.AuditorName);
        Assert.Equal(AuditSeverity.Error, vulnerability.Severity);
        Assert.Equal("Cargo.lock:1", vulnerability.Location);
        Assert.Contains("time 0.1.45", vulnerability.Description, StringComparison.Ordinal);

        var yanked = Assert.Single(
            result.Findings, f => f.Title.Contains("yanked", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, yanked.Severity);
        Assert.Equal("Cargo.lock:1", yanked.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("cargo-audit", argv[0]);
        Assert.Equal("audit", argv[1]);
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        var fileIndex = argv.ToList().IndexOf("--file");
        Assert.True(fileIndex >= 0 && argv[fileIndex + 1] == "Cargo.lock");
        Assert.DoesNotContain("--no-fetch", argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithSarifReport_ReportsFindings()
    {
        // cargo-audit's "found something" exit is 1 — the report on stdout is
        // what makes it a verdict rather than a failure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutReport_IsInfrastructureFailure()
    {
        // cargo-audit also exits 1 when the advisory database cannot be fetched
        // or loaded — the same exit as "vulnerabilities found", but only a
        // stderr status line and no SARIF report. Fails closed, never a pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "error: couldn't fetch advisory database"));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_NotFindings()
    {
        // Unlike the common convention, for cargo-audit 2 is the "could not
        // run" code (clap usage errors, unloadable lockfile, audit errors) —
        // it must never surface as findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "error: unexpected argument"));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode0_WithoutReport_IsInfrastructureFailure()
    {
        // A completed run always writes the SARIF document; an empty stdout at
        // exit 0 contradicts the output contract (e.g. truncated capture) and
        // must not be mistaken for a clean audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be parsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "cargo-audit: command not found"));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_NoRawStringsPassedThrough()
    {
        const string sarif =
            """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"cargo-audit","version":"0.22.2","semanticVersion":"0.22.2","rules":[]}},"results":[{"ruleId":"RUSTSEC-2020-0071","message":{"text":"vulnerability finding"},"level":"error","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}]},{"ruleId":"yanked","message":{"text":"yanked warning"},"level":"warning","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}]},{"ruleId":"RUSTSEC-2021-0001","message":{"text":"note-level result"},"level":"note","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}]},{"ruleId":"RUSTSEC-2030-0001","message":{"text":"unrecognized level"},"level":"brand-new-future-level","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}]}]}]}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, sarif, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        var vulnerability = Assert.Single(
            result.Findings, f => f.Title.Contains("RUSTSEC-2020-0071", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, vulnerability.Severity);
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // passing through as a finding severity.
        Assert.Contains("Severity (tool): error", vulnerability.Description, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("yanked", StringComparison.Ordinal)).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("RUSTSEC-2021-0001", StringComparison.Ordinal)).Severity);
        // Unknown tool level falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("RUSTSEC-2030-0001", StringComparison.Ordinal)).Severity);
    }

    [Fact]
    public async Task RepositoryAuditToml_FailsClosed_AsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, ".cargo/audit.toml\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CargoAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(".cargo/audit.toml", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepositoryAuditToml_TrustedViaScopedConfig_ScanProceeds()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, ".cargo/audit.toml\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CargoAuditAuditor();
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
    public async Task ScopedConfiguration_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CargoAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:LockfilePath"] = "crates/app/Cargo.lock",
                ["Scoped:DatabasePath"] = "/opt/advisory-db",
                ["Scoped:DatabaseUrl"] = "https://mirror.example.invalid/advisory-db.git",
                ["Scoped:Offline"] = "true",
                ["Scoped:Stale"] = "true",
                ["Scoped:NoYanked"] = "true",
                ["Scoped:TargetArch"] = "x86_64, aarch64",
                ["Scoped:TargetOs"] = "linux",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        Assert.Equal("audit", argv[1]);
        var fileIndex = argv.IndexOf("--file");
        Assert.True(fileIndex >= 0 && argv[fileIndex + 1] == "crates/app/Cargo.lock");
        var dbIndex = argv.IndexOf("--db");
        Assert.True(dbIndex >= 0 && argv[dbIndex + 1] == "/opt/advisory-db");
        var urlIndex = argv.IndexOf("--url");
        Assert.True(urlIndex >= 0 && argv[urlIndex + 1] == "https://mirror.example.invalid/advisory-db.git");
        Assert.Contains("--no-fetch", argv, StringComparer.Ordinal);
        Assert.Contains("--stale", argv, StringComparer.Ordinal);
        Assert.Contains("--no-yanked", argv, StringComparer.Ordinal);
        var archIndices = argv.Select((a, i) => (a, i)).Where(t => t.a == "--target-arch").Select(t => t.i).ToList();
        Assert.Equal(2, archIndices.Count);
        Assert.Contains("x86_64", argv, StringComparer.Ordinal);
        Assert.Contains("aarch64", argv, StringComparer.Ordinal);
        var osIndex = argv.IndexOf("--target-os");
        Assert.True(osIndex >= 0 && argv[osIndex + 1] == "linux");
    }

    [Fact]
    public async Task ScopedConfiguration_StdinLockfile_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CargoAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:LockfilePath"] = "-",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("LockfilePath", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        var auditor = new CargoAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "RUSTSEC-2020-0071",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the warning-level yanked finding survives the exclusion.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsWarnings()
    {
        const string warningsOnly =
            """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"cargo-audit","version":"0.22.2","semanticVersion":"0.22.2","rules":[]}},"results":[{"ruleId":"yanked","message":{"text":"oldlib 0.1.0 has a yanked warning"},"level":"warning","locations":[{"physicalLocation":{"artifactLocation":{"uri":"Cargo.lock"},"region":{"startLine":1}}}]}]}]}
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, warningsOnly, ""));
        });

        var auditor = new CargoAuditAuditor();
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
            s => s.PluginId == CargoAuditAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cargo-audit", flattened, StringComparison.Ordinal);
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
                Enabled = [CargoAuditAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CargoAuditAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var cargoAudit = Assert.Single(tools);
        Assert.Equal("cargo-audit", cargoAudit.Binary);
        // Verify-only by design: no distro package carries a pinned
        // cargo-audit, and the pinned build plus its advisory database must be
        // provisioned into the baseline by the operator.
        Assert.Null(cargoAudit.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("cargo-audit", verification.Argv, StringComparer.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_cargo_audit", "true")]
    public async Task RealCargoAudit_VulnerableFixture_ProducesFinding()
    {
        var installed = InstalledCargoAuditVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCargoAuditFixtureRepoAsync(vulnerable: true);

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

            var auditor = new CargoAuditAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    // Fully offline: the seeded advisory database needs no
                    // fetch, and --no-yanked skips the crates.io index.
                    ["Scoped:DatabasePath"] = "/work/advisory-db",
                    ["Scoped:Offline"] = "true",
                    ["Scoped:NoYanked"] = "true",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("RUSTSEC-2020-0071", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("Cargo.lock:1", finding.Location);
            Assert.Contains("time 0.1.45", finding.Description, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cargo_audit", "true")]
    public async Task RealCargoAudit_CleanFixture_Passes()
    {
        var installed = InstalledCargoAuditVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCargoAuditFixtureRepoAsync(vulnerable: false);

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

            var auditor = new CargoAuditAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:DatabasePath"] = "/work/advisory-db",
                    ["Scoped:Offline"] = "true",
                    ["Scoped:NoYanked"] = "true",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CargoAuditAuditorPlugin.dll");
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
            PluginId: CargoAuditAuditor.PluginId,
            PluginDisplayName: "CodeyBox: cargo-audit Rust Dependency Vulnerabilities",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "cargo-audit " + CargoAuditAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cargo-audit", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cargo-audit" && exec.Argv[1] == "--version";

    private static bool IsRepoFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("-e", StringComparison.Ordinal)
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static async Task<string> SeedCargoAuditFixtureRepoAsync(bool vulnerable)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-cargo-audit-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // A committed Cargo.lock is the audit subject — no registry index or
        // cargo binary is needed to audit it.
        var timeVersion = vulnerable ? "0.1.45" : "0.2.27";
        await File.WriteAllTextAsync(Path.Combine(dir, "Cargo.toml"), """
            [package]
            name = "app"
            version = "0.1.0"
            edition = "2021"

            [dependencies]
            time = "=0.1.45"
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "Cargo.lock"), $$"""
            version = 3

            [[package]]
            name = "app"
            version = "0.1.0"
            dependencies = [
             "time",
            ]

            [[package]]
            name = "time"
            version = "{{timeVersion}}"
            source = "registry+https://github.com/rust-lang/crates.io-index"
            checksum = "0000000000000000000000000000000000000000000000000000000000000000"
            """);

        // A minimal RustSec advisory database layout: crates/<pkg>/<ID>.md
        // with TOML front matter — enough for `Database::open` under --no-fetch.
        var advisoryDir = Path.Combine(dir, "advisory-db", "crates", "time");
        Directory.CreateDirectory(advisoryDir);
        await File.WriteAllTextAsync(Path.Combine(advisoryDir, "RUSTSEC-2020-0071.md"), """
            ```toml
            [advisory]
            id = "RUSTSEC-2020-0071"
            package = "time"
            date = "2020-11-18"
            title = "Potential segfault in the time crate"
            description = "Affected versions of this crate caused a segmentation fault in a real upstream advisory; the fixture keeps the id and version range."
            url = "https://github.com/time-rs/time/issues/293"
            categories = ["memory-corruption"]

            [versions]
            patched = [">= 0.2.23"]
            ```

            # Potential segfault in the time crate

            Fixture advisory text.
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
            // Drain both streams concurrently: a full stderr pipe would block
            // the child on write while stdout stays open, deadlocking the
            // synchronous read ahead of the timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromSeconds(5));
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
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
