using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.GosecAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the gosec auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass) — and so is a missing
/// <c>go</c> toolchain, which gosec shells out to; exit 0 is the clean
/// verdict while gosec's merged exit 1 is disambiguated by the
/// <c>"Golang errors"</c> oracle in the JSON side-report (errors →
/// infrastructure even when findings exist, findings and no errors →
/// findings, neither → a fails-closed parse failure); SARIF maps to
/// findings with rule id and file/line; severity is recovered from the
/// rule's native HIGH/MEDIUM/LOW tag rather than the flattened SARIF
/// level; flag-shaped and bare-import-path ExtraArguments/Targets die
/// deterministically because Go's flag parser takes nothing after the
/// positional patterns and go/packages patterns are not filesystem
/// paths; the operator -conf lands in argv as its canonicalized value
/// (the gated read IS the consumed read); the declared-offline tool
/// process is pinned GOPROXY=off/GOTOOLCHAIN=local and stripped of
/// flag/driver injection env; and the plugin is inert — unloaded and
/// absent from baseline provisioning — until an operator enables it.
/// Every run is dispatched through <see cref="IAuditor"/> so the version
/// pin cannot be bypassed by interface dispatch.
/// </summary>
public sealed class GosecAuditorTests
{
    // Mirrors what `gosec -verbose sarif` prints for a G404 (weak
    // randomness) issue — shape verified against gosec 2.29.0
    // report/sarif/formatter.go: the rule descriptor carries the native
    // severity in properties.tags (["security", "MEDIUM"]) and
    // defaultConfiguration.level flattened to "error" for MEDIUM/HIGH;
    // each result carries its own flattened "level", the rule id, message
    // text, and the first physical location's root-relative artifact uri
    // plus region.startLine.
    private const string SarifWithWeakRandom = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "gosec",
                "version": "2.29.0",
                "rules": [{
                  "id": "G404",
                  "name": "Insecure random number source (rand)",
                  "properties": {
                    "tags": ["security", "MEDIUM"],
                    "precision": "high"
                  },
                  "defaultConfiguration": { "level": "error" }
                }]
              }
            },
            "results": [{
              "ruleId": "G404",
              "ruleIndex": 0,
              "level": "error",
              "message": { "text": "Use of weak random number generator (math/rand instead of crypto/rand)" },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "main.go" },
                  "region": { "startLine": 9 }
                }
              }]
            }]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "gosec", "version": "2.29.0", "rules": [] } },
            "results": []
          }]
        }
        """;

    // The JSON render of the same ReportInfo written to the -out file:
    // the "Golang errors" section is the completeness oracle the SARIF
    // writer drops. Keyed by file/package path; empty means the scan
    // covered the whole tree.
    private const string JsonNoErrors = """
        { "Issues": [], "Stats": { "files": 1, "lines": 20, "nosec": 0, "found": 0 }, "Golang errors": {} }
        """;

    private const string JsonWithErrors = """
        {
          "Issues": [],
          "Stats": { "files": 1, "lines": 20, "nosec": 0, "found": 0 },
          "Golang errors": {
            "sub/broken.go": [{ "line": 3, "column": 10, "error": "expected ';', found '}'" }]
          }
        }
        """;

    [Fact]
    public async Task MissingGosecBinary_IsInfrastructureFailure_NamingGosec_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbeFor(exec, "gosec"))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "gosec: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gosec", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task MissingGoToolchain_IsInfrastructureFailure_NamingGo_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            // The gosec binary is present; the go toolchain it shells out
            // to is not.
            if (IsPresenceProbeFor(exec, "go"))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        // The failure names the missing piece and why it is needed — not
        // just the wrapper.
        Assert.Contains("'go'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gosec", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WeakRandomnessInFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithWeakRandom, ""));
        });

        IAuditor auditor = new GosecAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // MEDIUM maps to Warning through the declared map — a finding the
        // audit still passes.
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:gosec", finding.AuditorName);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("G404", finding.Title, StringComparison.Ordinal);
        Assert.Equal("main.go:9", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("gosec", argv[0]);
        AssertFlagValue(argv, "-fmt", "json");
        Assert.Contains("-out", argv);
        var outIndex = argv.ToList().IndexOf("-out");
        Assert.EndsWith("gosec-report.json", argv[outIndex + 1]);
        Assert.Contains("-stdout", argv);
        AssertFlagValue(argv, "-verbose", "sarif");
        Assert.Contains("-nosec", argv);
        Assert.Contains("-exclude-generated", argv);
        // The package patterns are the last argv entries: flags all
        // precede them so ExtraArguments cannot be misparsed as flags.
        Assert.Equal("./...", argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new GosecAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task IssuesExit_IsFindings_WhenOracleShowsNoErrors()
    {
        IAuditor auditor = new GosecAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(1, SarifWithWeakRandom),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(found.Findings);

        // The same report at exit 0 is also a verdict.
        var foundOnZero = await auditor.RunAsync(
            HealthyTool(0, SarifWithWeakRandom),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(foundOnZero.Findings);
    }

    [Fact]
    public async Task IssuesExit_WithProcessingErrors_IsInfrastructure_EvenWithFindings()
    {
        IAuditor auditor = new GosecAuditor();

        // Errors and no findings: a partial scan that would otherwise read
        // as a pass.
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, SarifClean, JsonWithErrors),
                "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("partial scan", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Errors alongside findings: still a partial scan, not a verdict.
        var withFindings = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, SarifWithWeakRandom, JsonWithErrors),
                "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("partial scan", withFindings.Message, StringComparison.OrdinalIgnoreCase);

        // Errors at exit 0 break the declared convention — also
        // infrastructure, not a pass.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(0, SarifClean, JsonWithErrors),
                "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task IssuesExit_WithoutFindingsOrErrors_IsInfrastructure_NotAPass()
    {
        // Exit 1 means issues-or-errors; the oracle shows neither and the
        // SARIF is empty, so the exit's reason is unverifiable — the parser
        // fails closed rather than passing on an unclassifiable scan.
        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, SarifClean, JsonNoErrors),
                "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gosec", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupFailureExit_NoReport_IsInfrastructure()
    {
        // gosec exits 1 on an unreadable -conf, "No packages found", and
        // analyzer failures — before either report is written, so the
        // side-report read fails closed first. (Flag-parse/usage errors
        // exit 2 through Go's flag.ExitOnError — covered by
        // UndeclaredExits_AreInfrastructure.)
        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                new FakeSandbox((exec, _) => Task.FromResult(
                    IsProbe(exec)
                        ? Ok(exec)
                        : IsCatProbe(exec)
                            ? new SandboxExecResult(1, "", "cat: can't open report: No such file")
                            : new SandboxExecResult(1, "", "gosec: No packages found"))),
                "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gosec", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndeclaredExits_AreInfrastructure()
    {
        IAuditor auditor = new GosecAuditor();
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, SarifWithWeakRandom), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 2", errorEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_RecoversNativeSeverity_NotRawSarifLevel()
    {
        IAuditor auditor = new GosecAuditor();

        // G404's SARIF level is "error" — gosec flattens MEDIUM and HIGH
        // both to error — but the rule's native MEDIUM tag must map to
        // Warning, not Error. A raw-level pass-through would block.
        var medium = await auditor.RunAsync(
            HealthyTool(1, SarifWithWeakRandom),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(medium.Findings).Severity);
        Assert.True(medium.Passed);

        // A HIGH tag (flattened to the same "error") blocks.
        var high = await auditor.RunAsync(
            HealthyTool(1, WithRuleTag(SarifWithWeakRandom, "HIGH")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(high.Findings).Severity);
        Assert.False(high.Passed);

        // A LOW tag (gosec emits level "warning") is informational.
        var low = await auditor.RunAsync(
            HealthyTool(1, WithRuleTag(WithResultLevel(SarifWithWeakRandom, "warning"), "LOW")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(low.Findings).Severity);
        Assert.True(low.Passed);
    }

    [Fact]
    public async Task SeverityMapping_FallsBackToLevel_WhenRuleCarriesNoTag()
    {
        IAuditor auditor = new GosecAuditor();
        var withoutTags = SarifWithWeakRandom.Replace(
            "\"tags\": [\"security\", \"MEDIUM\"],", "\"tags\": [\"security\"],", StringComparison.Ordinal);

        // error-level results still block when the tag is absent.
        var error = await auditor.RunAsync(
            HealthyTool(1, withoutTags), "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);

        // And an unrecognised level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(withoutTags, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbeFor(exec, "gosec"))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "Version: 2.20.0\nGit tag: v2.20.0\nBuild date: 2025-01-01\n", ""));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("2.20.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GosecAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "2.20.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
            {
                return Task.FromResult(IsVersionProbe(exec)
                    ? new SandboxExecResult(
                        0, "Version: 2.20.0\nGit tag: v2.20.0\nBuild date: 2025-01-01\n", "")
                    : Ok(exec));
            }

            return Task.FromResult(IsCatProbe(exec)
                ? new SandboxExecResult(0, JsonNoErrors, "")
                : new SandboxExecResult(1, SarifWithWeakRandom, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludeTests"] = "true",
                ["Scoped:IncludeGeneratedCode"] = "true",
                ["Scoped:BuildTags"] = "integration,netgo",
                ["Scoped:Targets"] = "./pkg/...",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("-tests", argv);
        Assert.DoesNotContain("-exclude-generated", argv);
        AssertFlagValue(argv, "-tags", "integration,netgo");
        Assert.Equal("./pkg/...", argv[^1]);
    }

    [Fact]
    public async Task FlagShapedExtraArguments_AreRejected_BeforeScan()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-no-fail",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task FlagFreeExtraArguments_AppendAsAdditionalPatterns()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "./extra/...",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("./extra/...", scanExec!.Argv[^1]);
    }

    [Fact]
    public async Task ConfigPath_ResolvingInsideWorktree_FailsClosed()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "ops/gosec.json",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work/ops/gosec.json\n/work\n", ""));
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("worktree", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ConfigPath_ResolvingOutsideWorktree_ScanRuns()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/etc/codeybox/gosec.json",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/etc/codeybox/gosec.json\n/work\n", ""));
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        AssertFlagValue(scanExec!.Argv, "-conf", "/etc/codeybox/gosec.json");
    }

    [Fact]
    public async Task ConfigPath_EmitsTheCanonicalPath_InArgv()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/links/gosec.json",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            // The configured path is a symlink whose canonical target
            // lands outside the worktree — argv must carry the gated
            // canonical value, not the configured spelling.
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "/etc/codeybox/gosec.json\n/work\n", ""));
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        AssertFlagValue(scanExec!.Argv, "-conf", "/etc/codeybox/gosec.json");
    }

    [Fact]
    public async Task BareImportPathTargets_AreRejected_BeforeAnyExec()
    {
        // gosec positional arguments are go/packages patterns, not
        // filesystem paths: 'net/http' resolves into GOROOT source outside
        // the audited worktree, so it is refused before the first probe.
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "net/http",
            }),
            CancellationToken.None);

        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(
                IsProbe(exec)
                    ? Ok(exec)
                    : IsCatProbe(exec)
                        ? new SandboxExecResult(0, JsonNoErrors, "")
                        : new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("'./'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task FileTreeTargets_StillScan_IncludingWorktreeRootDot()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = ".,./sub/...",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("./sub/...", scanExec!.Argv[^1]);
        Assert.Equal(".", scanExec.Argv[^2]);
    }

    [Fact]
    public async Task BareImportPathExtraArguments_AreRejected_BeforeScan()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "all",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongShapeRuleMetadata_YieldsFinding_NotAParserCrash()
    {
        // "properties": "x" and "rule": "G404" are well-formed JSON of the
        // wrong shape: string-indexing them throws InvalidOperationException,
        // surfacing as an untyped 'parser failed' instead of the declared
        // parse contract. The decorator shape-checks before indexing, so
        // the run produces the finding with its reported level.
        const string sarif = """
            {
              "version": "2.1.0",
              "runs": [{
                "tool": {
                  "driver": {
                    "name": "gosec",
                    "version": "2.29.0",
                    "rules": [{ "id": "G404", "properties": "x" }]
                  }
                },
                "results": [{
                  "rule": "G404",
                  "level": "error",
                  "message": { "text": "weak randomness" },
                  "locations": [{
                    "physicalLocation": {
                      "artifactLocation": { "uri": "main.go" },
                      "region": { "startLine": 9 }
                    }
                  }]
                }]
              }]
            }
            """;

        IAuditor auditor = new GosecAuditor();
        var result = await auditor.RunAsync(
            HealthyTool(1, sarif), "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("main.go:9", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ReportFile_Unreadable_IsInfrastructure()
    {
        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                new FakeSandbox((exec, _) => Task.FromResult(
                    IsProbe(exec)
                        ? Ok(exec)
                        : IsCatProbe(exec)
                            ? new SandboxExecResult(1, "", "cat: can't open report: No such file")
                            : new SandboxExecResult(0, SarifClean, ""))),
                "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gosec", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolAiEnvironment_IsStripped_FromTheToolProcess()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GosecAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("GOSEC_AI_PROVIDER", scanExec!.EnvironmentVariablesToUnset);
        Assert.Contains("GOSEC_AI_API_KEY", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GOSEC_AI_BASE_URL", scanExec.EnvironmentVariablesToUnset);
        // Ambient Go knobs that steer `go list` — flag injection and an
        // arbitrary package-loading driver — are stripped too.
        Assert.Contains("GOFLAGS", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GOPACKAGESDRIVER", scanExec.EnvironmentVariablesToUnset);
    }

    [Fact]
    public async Task OfflineGoEnvironment_IsPinned_OnTheToolProcess()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCatProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonNoErrors, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GosecAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The auditor declares no network capability, so the tool process
        // must not inherit fetch/exec paths: GOPROXY=off keeps the module
        // graph to cache/vendor and GOTOOLCHAIN=local blocks a repo-chosen
        // toolchain download.
        Assert.NotNull(scanExec);
        var env = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            scanExec!.ExtraEnvironment);
        Assert.Equal("off", env["GOPROXY"]);
        Assert.Equal("local", env["GOTOOLCHAIN"]);
    }

    // Fixture sources assembled at runtime so this test file does not
    // itself carry scanner-detectable literals. The vulnerable fixture
    // seeds a weak RNG (gosec G404, the textbook medium-severity
    // crypto/random hit); the clean fixture uses crypto/rand.
    private static readonly string _mathRand = "math" + "/rand";

    private static readonly string _fixtureVulnApp =
        "package main\n"
        + "\n"
        + "import (\n"
        + "\t\"fmt\"\n"
        + "\t\"" + _mathRand + "\"\n"
        + ")\n"
        + "\n"
        + "func main() {\n"
        + "\tfmt.Println(rand.Intn(100))\n"
        + "}\n";

    private static readonly string _fixtureCleanApp =
        "package main\n"
        + "\n"
        + "import \"fmt\"\n"
        + "\n"
        + "func main() {\n"
        + "\tfmt.Println(\"ok\")\n"
        + "}\n";

    private static readonly string _fixtureGoMod =
        "module fixture.local/audit\n"
        + "\n"
        + "go 1.22\n";

    private static readonly string? _installedGosecVersion = ProbeInstalledGosecVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture Go module with a known
    /// weak-randomness issue is scanned by the actual gosec CLI (and the
    /// real go toolchain it shells out to) through a real process exec —
    /// exercising the invocation shape, the stdout SARIF sink, the -out
    /// JSON oracle, and the exit-1-with-findings convention together, so
    /// a broken real invocation cannot stay green. Runs only where gosec
    /// is on PATH; the auditor's version pin is set to the installed
    /// release.
    /// </summary>
    [Fact]
    [Trait("requires_gosec", "true")]
    public async Task RealGosec_WeakRandomness_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedGosecVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            ("go.mod", _fixtureGoMod),
            ("main.go", _fixtureVulnApp));
        try
        {
            var auditor = new GosecAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.NotEmpty(result.Findings);
            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("G404", StringComparison.Ordinal));
            Assert.Equal("codeybox:gosec", finding.AuditorName);
            Assert.Equal(AuditSeverity.Warning, finding.Severity);
            Assert.NotNull(finding.Location);
            Assert.StartsWith("main.go", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture Go module with no findings
    /// passes with zero findings — and the analysis exits 0.
    /// </summary>
    [Fact]
    [Trait("requires_gosec", "true")]
    public async Task RealGosec_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedGosecVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            ("go.mod", _fixtureGoMod),
            ("main.go", _fixtureCleanApp));
        try
        {
            var auditor = new GosecAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
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
            s => s.PluginId == GosecAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("gosec", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGosecAndGoRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GosecAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GosecAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Binary == "gosec");
        Assert.Contains(tools, t => t.Binary == "go");
        // Verify-only by design: gosec ships as a Go module or release
        // tarball, not a distro package, so no apt line can carry the
        // version pin — the baseline verifies presence and the operator
        // provisions the pinned release. No install commands are emitted
        // for these tools.
        Assert.All(tools, t => Assert.Null(t.AptPackage));

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var verificationArgv = string.Join(
            "\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("gosec", verificationArgv, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GosecAuditorPlugin.dll");
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
            PluginId: GosecAuditor.PluginId,
            PluginDisplayName: "CodeyBox: gosec Go Security",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static string WithResultLevel(string sarif, string level)
        => sarif.Replace(
            "\"level\": \"error\",",
            "\"level\": \"" + level + "\",",
            StringComparison.Ordinal);

    private static string WithRuleTag(string sarif, string severity)
        => sarif.Replace(
            "\"tags\": [\"security\", \"MEDIUM\"]",
            "\"tags\": [\"security\", \"" + severity + "\"]",
            StringComparison.Ordinal);

    private static void AssertFlagValue(IReadOnlyList<string> argv, string flag, string value)
    {
        var index = argv.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < argv.Count, $"argv does not carry '{flag} {value}'");
        Assert.Equal(value, argv[index + 1]);
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(
                0,
                "Version: " + GosecAuditor.DefaultExpectedVersion
                    + "\nGit tag: v" + GosecAuditor.DefaultExpectedVersion
                    + "\nBuild date: 2026-09-01T00:00:00Z\n",
                "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(
        int scanExit, string scanStdout, string jsonOracle = JsonNoErrors)
        => new((exec, _) => Task.FromResult(
            IsProbe(exec)
                ? Ok(exec)
                : IsCatProbe(exec)
                    ? new SandboxExecResult(0, jsonOracle, "")
                    : new SandboxExecResult(scanExit, scanStdout, "")));

    // Probes the auditor issues before the scan: the sh -c presence
    // checks (gosec itself, then the go toolchain in VerifyToolAsync),
    // the gosec -version pin probe, the realpath -m canonicalization, and
    // the mkdir report-directory setup — all bounded sh/binary execs that
    // are neither the scan (argv[0] == "gosec" with -fmt) nor the cat
    // report read.
    private static bool IsProbe(SandboxExec exec)
        => IsPresenceProbe(exec)
            || IsVersionProbe(exec)
            || IsRealpathProbe(exec)
            || IsMkdirProbe(exec);

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsPresenceProbeFor(SandboxExec exec, string binary)
        => IsPresenceProbe(exec)
            && string.Equals(exec.Argv[^1], binary, StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "gosec" && exec.Argv[1] == "-version";

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "realpath";

    private static bool IsMkdirProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "mkdir";

    private static bool IsCatProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "cat";

    private static async Task<string> SeedFixtureRepoAsync(params (string Name, string Content)[] files)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-gosec-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(repo, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    private static string? ProbeInstalledGosecVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gosec",
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
            if (process.ExitCode != 0)
                return null;
            var match = System.Text.RegularExpressions.Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return match.Success ? match.Value : null;
        }
        catch
        {
            // Any failure means no usable gosec on PATH — the gated tests
            // return early rather than fail on a host without the tool.
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
