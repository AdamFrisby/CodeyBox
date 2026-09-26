using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.CargoSemverChecksAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the cargo-semver-checks auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cargo-semver-checks (never a pass or finding).
/// - Exits 0 and 100 are verdicts; 101/2/127 and a 100 exit with no failure section are infrastructure.
/// - Report sections map to findings with lint rule ids and file:line locations; warn-level sections are advisory.
/// - Tool section levels are mapped through the declared severity mapping (never passed through).
/// - Default baseline resolves the merge-base of origin/&lt;base&gt;/&lt;base&gt; and HEAD via git probes;
///   a configured Baseline* key or an ExtraArguments --baseline-* flag skips resolution;
///   conflicting sources fail deterministically.
/// - Repo lint config (a cargo-semver-checks table reported by cargo metadata) fails closed
///   unless TrustRepositorySuppression; a failed probe is infrastructure, not a pass.
/// - A missing root Cargo.toml fails deterministically; ManifestPath points the tool elsewhere.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_cargo_semver_checks", "true")] use a fixture
///   git repository and need cargo-semver-checks plus a cargo toolchain, but no network.
/// </summary>
public sealed class CargoSemverChecksAuditorTests
{
    private const string BaseSha = "0123456789abcdef0123456789abcdef01234567";
    private const string MergeBaseSha = "89abcdef0123456789abcdef0123456789abcdef";

    private static readonly string? InstalledToolVersion = ProbeInstalledToolVersion();
    private static readonly bool HasCargoToolchain = ProbeBinary("cargo", "--version");

    private const string ReportWithFindings = """

        --- failure function_missing: pub fn removed or renamed ---

        Description:
        A publicly-visible function cannot be imported by its prior path. A `pub use` may have been removed, or the function itself may have been renamed or removed entirely.
               ref: https://doc.rust-lang.org/cargo/reference/semver.html#item-remove
              impl: https://github.com/obi1kenobi/cargo-semver-checks/tree/v0.50.0/src/lints/function_missing.ron

        Failed in:
          function fixture_lib::gone, previously in file src/lib.rs:5
          function fixture_lib::also_gone, previously in file src/other.rs:9

        --- warning struct_gone: pub struct removed or renamed ---

        Description:
        A struct can no longer be imported by its prior path.
              impl: https://github.com/obi1kenobi/cargo-semver-checks/tree/v0.50.0/src/lints/struct_gone.ron

        Failed in:
          struct fixture_lib::Old, previously in file src/old.rs:3

        """;

    private const string ReportWarningsOnly = """

        --- warning struct_gone: pub struct removed or renamed ---

        Description:
        A struct can no longer be imported by its prior path.
              impl: https://github.com/obi1kenobi/cargo-semver-checks/tree/v0.50.0/src/lints/struct_gone.ron

        Failed in:
          struct fixture_lib::Old, previously in file src/old.rs:3

        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-semver-checks", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "cargo-semver-checks 0.49.0\n", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-semver-checks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.49.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CargoSemverChecksAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChange_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(100, ReportWithFindings, ""));
            }
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var finding = Assert.Single(
            result.Findings, f => f.Location == "src/lib.rs:5");
        Assert.Equal("codeybox:cargo-semver-checks", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("function_missing", finding.Title, StringComparison.Ordinal);
        Assert.Contains("fixture_lib::gone", finding.Description, StringComparison.Ordinal);
        // The lint's explanation text rides along for the rework prompt.
        Assert.Contains("cannot be imported by its prior path", finding.Description, StringComparison.Ordinal);

        var second = Assert.Single(result.Findings, f => f.Location == "src/other.rs:9");
        Assert.Equal(AuditSeverity.Error, second.Severity);

        var advisory = Assert.Single(result.Findings, f => f.Location == "src/old.rs:3");
        Assert.Equal(AuditSeverity.Warning, advisory.Severity);
        Assert.Contains("struct_gone", advisory.Title, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("cargo-semver-checks", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Equal("check-release", argv[1]);
        var colorIndex = argv.ToList().IndexOf("--color");
        Assert.True(colorIndex >= 0 && argv[colorIndex + 1] == "never");
        var revIndex = argv.ToList().IndexOf("--baseline-rev");
        Assert.True(revIndex >= 0 && revIndex + 1 < argv.Count);
        Assert.Equal(MergeBaseSha, argv[revIndex + 1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec) ? new SandboxExecResult(0, "", "") : Ok(exec)));

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode100_WithFailureSections_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec) ? new SandboxExecResult(100, ReportWithFindings, "") : Ok(exec)));

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithWarningSections_ReportsAdvisoryFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec) ? new SandboxExecResult(0, ReportWarningsOnly, "") : Ok(exec)));

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("src/old.rs:3", finding.Location);
    }

    [Fact]
    public async Task ExitCode100_WithoutFailureSection_IsInfrastructureFailure()
    {
        // Exit 100 asserts deny-level findings exist; a report without a
        // failure section (suppressed verbosity, foreign build, truncation)
        // contradicts the contract — the check did not verifiably run.
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(100, "some non-report output", "")
                : Ok(exec)));

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-semver-checks", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(2)]
    [InlineData(127)]
    public async Task NonVerdictExitCodes_AreInfrastructureFailures(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(exitCode, "", "error: manifest not found")
                : Ok(exec)));

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-semver-checks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_FailureMapsToError_WarningToWarning_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec) ? new SandboxExecResult(100, ReportWithFindings, "") : Ok(exec)));

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(2, result.Findings.Count(f => f.Severity == AuditSeverity.Error));
        Assert.Equal(1, result.Findings.Count(f => f.Severity == AuditSeverity.Warning));
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than the
        // severity field itself.
        var error = Assert.Single(result.Findings, f => f.Location == "src/lib.rs:5");
        Assert.Contains("failure", error.Description, StringComparison.Ordinal);
        var warning = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.Contains("warning", warning.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultBaseline_ResolvesMergeBase_ThroughGitProbes()
    {
        var revParseCandidates = new List<string>();
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            if (IsRevParseProbe(exec))
            {
                revParseCandidates.Add(exec.Argv[^1]);
                return Task.FromResult(new SandboxExecResult(0, BaseSha + "\n", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        // origin/<base> resolved on the first probe, so the bare branch was
        // never tried; the merge-base sha is what the tool is handed.
        var candidate = Assert.Single(revParseCandidates);
        Assert.Equal("origin/main^{commit}", candidate);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var revIndex = argv.ToList().IndexOf("--baseline-rev");
        Assert.True(revIndex >= 0 && argv[revIndex + 1] == MergeBaseSha);
    }

    [Fact]
    public async Task DefaultBaseline_FallsBackToBareBranch_WhenOriginRefMissing()
    {
        var revParseCandidates = new List<string>();
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsRevParseProbe(exec))
            {
                revParseCandidates.Add(exec.Argv[^1]);
                var ok = exec.Argv[^1] == "main^{commit}";
                return Task.FromResult(ok
                    ? new SandboxExecResult(0, BaseSha + "\n", "")
                    : new SandboxExecResult(128, "", "fatal: Needed a single revision"));
            }
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(["origin/main^{commit}", "main^{commit}"], revParseCandidates);
    }

    [Fact]
    public async Task UnresolvableBaseRef_IsDeterministicInfrastructure_NamingBaselineKnobs()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            if (IsRevParseProbe(exec))
                return Task.FromResult(new SandboxExecResult(128, "", "fatal: Needed a single revision"));
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("BaselineRev", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task NoMergeBase_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            if (IsMergeBaseProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("merge base", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ConfiguredBaselineRev_SkipsGitResolution_AndPinsArgument()
    {
        var gitProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (exec.Argv[0] == "git")
                gitProbes++;
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaselineRev"] = "v1.4.0",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(0, gitProbes);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var revIndex = argv.ToList().IndexOf("--baseline-rev");
        Assert.True(revIndex >= 0 && argv[revIndex + 1] == "v1.4.0");
    }

    [Fact]
    public async Task MultipleBaselinesConfigured_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaselineRev"] = "v1.4.0",
                ["Scoped:BaselineRoot"] = "/opt/baseline",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("one", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task NoCargoToml_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsManifestProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Cargo.toml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ManifestPath", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ManifestPath_SkipsRootProbe_AndAddsArgument()
    {
        var manifestProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsManifestProbe(exec))
                manifestProbes++;
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ManifestPath"] = "rust/member/Cargo.toml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(0, manifestProbes);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var index = argv.ToList().IndexOf("--manifest-path");
        Assert.True(index >= 0 && argv[index + 1] == "rust/member/Cargo.toml");
    }

    [Fact]
    public async Task RepoLintConfig_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./Cargo.toml\n", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("cargo-semver-checks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TrustRepositorySuppression", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SuppressionProbeFailure_IsInfrastructure_NotAPass()
    {
        // A cargo metadata failure (e.g. an unparseable manifest, exit 3 from
        // the probe script) means "could not confirm clean" — it must surface
        // as infrastructure, never as evidence that lint config is absent.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(3, "", "error: failed to parse manifest"));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        IAuditor auditor = new CargoSemverChecksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cargo-semver-checks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ManifestPath_IsForwardedToSuppressionProbe()
    {
        SandboxExec? probe = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
                probe = exec;
            return Task.FromResult(IsScanExec(exec) ? new SandboxExecResult(0, "", "") : Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ManifestPath"] = "rust/member/Cargo.toml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(probe);
        var index = probe!.Argv.ToList().IndexOf("--manifest-path");
        Assert.True(index >= 0 && probe.Argv[index + 1] == "rust/member/Cargo.toml");
    }

    [Theory]
    [InlineData("--manifest-path=rust/member/Cargo.toml")]
    [InlineData("--manifest-path,rust/member/Cargo.toml")]
    public async Task ExtraArgumentsManifestPath_ProbesSameManifest_AsScan(string extraArguments)
    {
        var manifestProbes = 0;
        SandboxExec? suppressionProbe = null;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsManifestProbe(exec))
                manifestProbes++;
            if (IsSuppressionProbe(exec))
                suppressionProbe = exec;
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        // An operator-supplied --manifest-path skips the root Cargo.toml
        // presence check exactly like the scoped ManifestPath key.
        Assert.Equal(0, manifestProbes);
        Assert.NotNull(suppressionProbe);
        Assert.NotNull(scanExec);
        // Probe and scan must agree on the manifest: the suppression gate
        // covers the same manifest cargo-semver-checks reads lint config
        // from, whichever argv spelling carried it.
        var probeIndex = suppressionProbe!.Argv.ToList().IndexOf("--manifest-path");
        Assert.True(probeIndex >= 0);
        Assert.Equal("rust/member/Cargo.toml", suppressionProbe.Argv[probeIndex + 1]);
        var scanIndex = scanExec!.Argv.ToList().IndexOf("--manifest-path");
        if (scanIndex >= 0)
        {
            Assert.Equal("rust/member/Cargo.toml", scanExec.Argv[scanIndex + 1]);
        }
        else
        {
            Assert.Contains("--manifest-path=rust/member/Cargo.toml", scanExec.Argv);
        }
    }

    [Fact]
    public async Task ExtraArgumentsManifestPath_WithoutValue_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--manifest-path",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("manifest-path", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExtraArgumentsBaseline_DefersToOperator_AndSkipsGitProbes()
    {
        var gitProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (exec.Argv[0] == "git")
                gitProbes++;
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--baseline-rev=v2.0.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, gitProbes);
        Assert.NotNull(scanExec);
        // The operator's flag arrives once, via ExtraArguments — the auditor
        // neither resolves nor emits a competing --baseline-* flag.
        var baselineFlags = scanExec!.Argv
            .Count(static a => a.StartsWith("--baseline-", StringComparison.Ordinal));
        Assert.Equal(1, baselineFlags);
    }

    [Fact]
    public async Task ScopedBaselinePlusExtraArgumentsFlag_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:BaselineRev"] = "v1.4.0",
                ["Scoped:ExtraArguments"] = "--baseline-version=1.0.0",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("baseline", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedManifestPathPlusExtraArgumentsFlag_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ManifestPath"] = "rust/member/Cargo.toml",
                ["Scoped:ExtraArguments"] = "--manifest-path=other/Cargo.toml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("manifest-path", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task TrustRepositorySuppression_SkipsLintConfigProbe()
    {
        var suppressionProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
                suppressionProbes++;
            return Task.FromResult(IsScanExec(exec) ? new SandboxExecResult(0, "", "") : Ok(exec));
        });

        var auditor = new CargoSemverChecksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, suppressionProbes);
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
            s => s.PluginId == CargoSemverChecksAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cargo-semver-checks", flattened, StringComparison.Ordinal);
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
                Enabled = [CargoSemverChecksAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CargoSemverChecksAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, t => t.Binary == "cargo-semver-checks");
        Assert.Contains(tools, t => t.Binary == "cargo");
        Assert.Contains(tools, t => t.Binary == "git");
        // Verify-only by design: no distro package carries a pinned
        // cargo-semver-checks, and the toolchain's rustdoc JSON format must
        // match the pinned release — both are operator-provisioned. git
        // ships in the stock baseline.
        Assert.All(tools, t => Assert.Null(t.AptPackage));

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        Assert.Equal(3, contributions.VerificationCommands.Count);
        var verificationArgv = string.Join(
            "\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("cargo-semver-checks", verificationArgv, StringComparison.Ordinal);
        Assert.Contains("cargo", verificationArgv, StringComparison.Ordinal);
        Assert.Contains("git", verificationArgv, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_cargo_semver_checks", "true")]
    public async Task RealTool_BreakingChangeFixture_ProducesFinding()
    {
        if (InstalledToolVersion is null || !HasCargoToolchain || !ProbeBinary("git", "--version"))
            return;

        var fixtureDir = await SeedFixtureRepoAsync(breaking: true);
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

            var auditor = new CargoSemverChecksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledToolVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("function_missing", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("lib.rs", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cargo_semver_checks", "true")]
    public async Task RealTool_CleanFixture_Passes()
    {
        if (InstalledToolVersion is null || !HasCargoToolchain || !ProbeBinary("git", "--version"))
            return;

        var fixtureDir = await SeedFixtureRepoAsync(breaking: false);
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

            var auditor = new CargoSemverChecksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledToolVersion,
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CargoSemverChecksAuditorPlugin.dll");
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
            PluginId: CargoSemverChecksAuditor.PluginId,
            PluginDisplayName: "CodeyBox: cargo-semver-checks Rust API Compatibility",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(
                0, "cargo-semver-checks " + CargoSemverChecksAuditor.DefaultExpectedVersion + "\n", "");
        if (IsRevParseProbe(exec))
            return new SandboxExecResult(0, BaseSha + "\n", "");
        if (IsMergeBaseProbe(exec))
            return new SandboxExecResult(0, MergeBaseSha + "\n", "");
        if (IsManifestProbe(exec))
            return new SandboxExecResult(0, "Cargo.toml\n", "");
        if (IsSuppressionProbe(exec))
            return new SandboxExecResult(1, "", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsScanExec(SandboxExec exec)
        => exec.Argv.Count >= 2
            && exec.Argv[0] == "cargo-semver-checks"
            && exec.Argv[1] == "check-release";

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cargo-semver-checks", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cargo-semver-checks" && exec.Argv[1] == "--version";

    private static bool IsRevParseProbe(SandboxExec exec)
        => exec.Argv.Count >= 3 && exec.Argv[0] == "git" && exec.Argv[1] == "rev-parse";

    private static bool IsMergeBaseProbe(SandboxExec exec)
        => exec.Argv.Count >= 3 && exec.Argv[0] == "git" && exec.Argv[1] == "merge-base";

    private static bool IsManifestProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal)
            && exec.Argv.Contains("Cargo.toml", StringComparer.Ordinal);

    private static bool IsSuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("cargo metadata", StringComparison.Ordinal);

    private static async Task<string> SeedFixtureRepoAsync(bool breaking)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-cargosemver-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        async Task WriteCrateAsync(string version, bool withGone)
        {
            var lib = withGone
                ? "pub fn kept() {}\npub fn gone() {}\n"
                : "pub fn kept() {}\n";
            await File.WriteAllTextAsync(
                Path.Combine(dir, "Cargo.toml"),
                $"[package]\nname = \"fixture-lib\"\nversion = \"{version}\"\nedition = \"2021\"\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "src", "lib.rs"), lib);
        }

        await WriteCrateAsync("0.1.0", withGone: true);
        await RunGitAsync(dir, "init", "-b", "main");
        await RunGitAsync(dir, "add", "-A");
        await RunGitAsync(dir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "v1");
        // The change under audit lives on a feature branch so the merge-base
        // with main is the v1 commit — the baseline public API.
        await RunGitAsync(dir, "checkout", "-b", "feature");
        // The version stays at 0.1.0: under cargo semver rules a 0.x minor
        // bump (0.1.0 -> 0.2.0) is the major-equivalent, so bumping it would
        // satisfy the lints' required update and mask the unresolved
        // breakage this fixture exists to produce.
        await WriteCrateAsync("0.1.0", withGone: !breaking);
        await RunGitAsync(dir, "add", "-A");
        await RunGitAsync(dir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "v2");
        return dir;
    }

    private static async Task RunGitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }

    private static string? ProbeInstalledToolVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cargo-semver-checks",
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

    private static bool ProbeBinary(string binary, string arg)
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
            psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            return process.WaitForExit(milliseconds: 10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
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
