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
/// infrastructure naming the tool (never a pass), gosec's ambiguous exit 1
/// is discriminated by the SARIF on stdout plus the JSON
/// <c>"Golang errors"</c> channel on stderr (findings + empty channel →
/// verdict, empty report / recorded errors / unreadable channel →
/// infrastructure), findings carry the gosec rule id and file:line,
/// severity goes through the declared mapping on gosec's native
/// HIGH/MEDIUM/LOW vocabulary — recovered from rule descriptors, not the
/// flattened SARIF level — and the plugin is inert until enabled. A
/// pre-scan <c>find</c> gate fails closed on nested Go module roots, which
/// <c>go list ./...</c> silently skips. Every run is dispatched through
/// <see cref="IAuditor"/> so the version-pin precondition cannot be
/// bypassed by interface dispatch.
/// </summary>
public sealed class GosecAuditorTests
{
    // Mirrors gosec 2.28's `-fmt sarif -stdout` shape: runs[].tool.driver
    // .rules[] carry properties.tags = ["security", "<SEVERITY>"] and
    // help.text "Severity: HIGH\nConfidence: ..."; results carry ruleId,
    // ruleIndex, the *flattened* SARIF level (LOW→warning, MEDIUM/HIGH→
    // error), message.text, and locations[0].physicalLocation with the
    // repo-relative artifact uri and region.startLine.
    private const string SarifWithWeakRand = """
        {
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "gosec",
                "version": "2.28.0",
                "semanticVersion": "2.28.0",
                "informationUri": "https://github.com/securego/gosec/",
                "rules": [{
                  "id": "G404",
                  "name": "Insecure random number source (rand)",
                  "shortDescription": { "text": "Use of weak random number generator" },
                  "help": { "text": "Use of weak random number generator\nSeverity: HIGH\nConfidence: MEDIUM\n" },
                  "properties": { "tags": ["security", "HIGH"], "precision": "medium" },
                  "defaultConfiguration": { "level": "error" }
                }]
              }
            },
            "results": [{
              "ruleId": "G404",
              "ruleIndex": 0,
              "level": "error",
              "message": { "text": "Use of weak random number generator (math/rand or math/rand/v2 instead of crypto/rand)" },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "main.go" },
                  "region": { "startLine": 8, "endLine": 8, "startColumn": 14, "endColumn": 14, "sourceLanguage": "go" }
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
            "tool": { "driver": { "name": "gosec", "version": "2.28.0", "rules": [] } },
            "results": []
          }]
        }
        """;

    // The JSON report gosec writes to stderr (-fmt json -out /dev/stderr):
    // "Golang errors" is the per-package analysis-error channel SARIF
    // lacks — empty on a fully-analyzed run.
    private const string ErrorsJsonEmpty = """
        {
          "Golang errors": {},
          "Issues": [],
          "Stats": { "numfiles": 2, "numlines": 40, "numnosec": 0, "numfound": 0 },
          "GosecVersion": "2.28.0"
        }
        """;

    private const string ErrorsJsonWithUnloadablePackage = """
        {
          "Golang errors": {
            "hidden/evil.go": [{ "line": 0, "column": 0, "error": "could not load package" }]
          },
          "Issues": [],
          "Stats": { "numfiles": 1, "numlines": 20, "numnosec": 0, "numfound": 1 },
          "GosecVersion": "2.28.0"
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGosec_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "gosec: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gosec", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WeakRandInSource_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(
                GosecAuditor.IssuesOrErrorsExitCode, SarifWithWeakRand, ErrorsJsonEmpty));
        });

        IAuditor auditor = new GosecAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:gosec", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("G404", finding.Title, StringComparison.Ordinal);
        Assert.Equal("main.go:8", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("gosec", argv[0]);
        // Two-channel report: -fmt json selects the -out (stderr) report —
        // the "Golang errors" channel — while -verbose sarif keeps the
        // stdout report SARIF and -log /dev/null keeps stderr JSON-only.
        var fmt = argv.ToList().IndexOf("-fmt");
        Assert.True(fmt >= 0 && fmt + 1 < argv.Count);
        Assert.Equal("json", argv[fmt + 1]);
        Assert.Contains("-stdout", argv);
        var verbose = argv.ToList().IndexOf("-verbose");
        Assert.True(verbose >= 0 && verbose + 1 < argv.Count);
        Assert.Equal("sarif", argv[verbose + 1]);
        var output = argv.ToList().IndexOf("-out");
        Assert.True(output >= 0 && output + 1 < argv.Count);
        Assert.Equal("/dev/stderr", argv[output + 1]);
        var log = argv.ToList().IndexOf("-log");
        Assert.True(log >= 0 && log + 1 < argv.Count);
        Assert.Equal("/dev/null", argv[log + 1]);
        // Generated files are scanned by default: the "Code generated …
        // DO NOT EDIT" marker is repo-authored, so honoring it by default
        // would let the subject erase a file from analysis.
        Assert.DoesNotContain("-exclude-generated", argv);
        // Positional ./... — required: gosec relativizes SARIF artifact URIs
        // against the positional scan roots, so -r alone would emit empty
        // locations. It must come after every flag.
        Assert.Equal("./...", argv[^1]);
        Assert.DoesNotContain("-r", argv);
        // Repository-authored #nosec suppression is inert by default.
        Assert.Contains("-nosec", argv);

        // gosec's AI autofix knobs are neutralized for the tool process.
        Assert.NotNull(scanExec.ExtraEnvironment);
        Assert.Equal("", scanExec.ExtraEnvironment["GOSEC_AI_PROVIDER"]);
        Assert.Equal("", scanExec.ExtraEnvironment["GOSEC_AI_API_KEY"]);
        Assert.Equal("", scanExec.ExtraEnvironment["GOSEC_AI_BASE_URL"]);
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
    public async Task ExitOneWithFindings_IsVerdict_WhileOtherFailures_AreInfrastructure()
    {
        IAuditor auditor = new GosecAuditor();

        // gosec exits 1 for findings — a verdict when SARIF results exist.
        var found = await auditor.RunAsync(
            HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, SarifWithWeakRand),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // gosec exits the same 1 for per-package analysis errors, which its
        // SARIF report does not carry: a valid-but-empty report on exit 1
        // means the scan errored, not that the tree is clean — the check
        // could not complete and must not pass.
        var emptyReport = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, SarifClean),
                "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("no findings", emptyReport.Message, StringComparison.OrdinalIgnoreCase);

        // And the same 1 for operational failure (no SARIF on stdout at
        // all): a non-Go repo's "No packages found", a bad -conf, a report
        // write failure. Not a verdict.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, "No packages found\n", ""),
                "/work", FakeContext(), CancellationToken.None));

        // Usage errors exit 2; any undeclared convention is infrastructure.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, "flag provided but not defined: -bogus\n", "flag provided but not defined: -bogus\n"),
                "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task FindingsCannotMaskAnalysisErrors_UnscannedPackagesFailClosed()
    {
        IAuditor auditor = new GosecAuditor();

        // Findings alongside recorded analysis errors on the stderr error
        // channel: part of the tree was never analyzed — a deliberately
        // unloadable package (broken go.mod, uncached dep) must not hide
        // behind the reported findings.
        var masked = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, SarifWithWeakRand, ErrorsJsonWithUnloadablePackage),
                "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("analysis error", masked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden/evil.go", masked.Message, StringComparison.Ordinal);

        // An absent or unreadable channel cannot prove the map is empty —
        // findings alone never suffice for a verdict. A null map is never
        // produced by a healthy run either (gosec initializes the map), so
        // it fails closed like every other non-object shape.
        foreach (var stderr in new[]
        {
            "",
            "not json",
            "{ }",
            """{ "Golang errors": "not-an-object" }""",
            """{ "Golang errors": null }""",
        })
        {
            await Assert.ThrowsAsync<AuditUnavailableException>(
                () => auditor.RunAsync(
                    HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, SarifWithWeakRand, stderr),
                    "/work", FakeContext(), CancellationToken.None));
        }

        // A clean-looking run with a missing channel is unverifiable too:
        // the JSON report is the only place analysis errors can appear.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(0, SarifClean, ""),
                "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_UsesNativeGosecVocabulary_NotFlattenedLevel()
    {
        // gosec flattens severity into SARIF level: MEDIUM and HIGH are both
        // "error". The auditor recovers the native token from the rule
        // descriptor's properties.tags, so a MEDIUM finding is advisory
        // (Warning), not blocking — and a fabricated SARIF level never
        // passes through raw.
        var mediumSarif = SarifWithWeakRand.Replace("HIGH", "MEDIUM", StringComparison.Ordinal);
        IAuditor auditor = new GosecAuditor();
        var medium = await auditor.RunAsync(
            HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, mediumSarif),
            "/work", FakeContext(), CancellationToken.None);
        var mediumFinding = Assert.Single(medium.Findings);
        Assert.Equal(AuditSeverity.Warning, mediumFinding.Severity);
        Assert.True(medium.Passed); // advisory findings do not fail the audit

        var lowSarif = SarifWithWeakRand
            .Replace("HIGH", "LOW", StringComparison.Ordinal)
            .Replace("\"level\": \"error\"", "\"level\": \"warning\"", StringComparison.Ordinal);
        var low = await auditor.RunAsync(
            HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, lowSarif),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(low.Findings).Severity);
        Assert.True(low.Passed);

        // A rule with no readable descriptor severity keeps the SARIF
        // level: "error" still maps to Error — the fallback is fail-secure.
        var noTags = SarifWithWeakRand
            .Replace("\"properties\": { \"tags\": [\"security\", \"HIGH\"], \"precision\": \"medium\" },", "")
            .Replace("\"help\": { \"text\": \"Use of weak random number generator\\nSeverity: HIGH\\nConfidence: MEDIUM\\n\" },", "");
        var fallback = await auditor.RunAsync(
            HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, noTags),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(fallback.Findings).Severity);
    }

    [Fact]
    public async Task TrustedRepositorySuppression_OmitsNosecFlag()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + GosecAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(
                GosecAuditor.IssuesOrErrorsExitCode, SarifWithWeakRand, ErrorsJsonEmpty));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.DoesNotContain("-nosec", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedFlags_BindToDedicatedKeys_BeforeThePositionalTarget()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + GosecAuditor.ConfigPathKey] = "/opt/gosec/org-rules.json",
                ["Scoped:" + GosecAuditor.ScanTestsKey] = "true",
                ["Scoped:" + GosecAuditor.BuildTagsKey] = "integration,prod",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var argv = scanExec!.Argv;
        var conf = argv.ToList().IndexOf("-conf");
        Assert.True(conf >= 0 && conf + 1 < argv.Count);
        Assert.Equal("/opt/gosec/org-rules.json", argv[conf + 1]);
        Assert.Contains("-tests", argv);
        var tags = argv.ToList().IndexOf("-tags");
        Assert.True(tags >= 0 && tags + 1 < argv.Count);
        Assert.Equal("integration,prod", argv[tags + 1]);
        // Every flag precedes the positional scan target so Go's flag parser
        // sees them.
        Assert.Equal(argv.Count - 1, argv.ToList().IndexOf("./..."));
    }

    [Fact]
    public async Task FlagShapedExtraArguments_AreRejectedDeterministically_ScanNeverRuns()
    {
        // Go's flag package stops flag parsing at the positional "./...";
        // a "-tests" extra would be swallowed as a package path and silently
        // do nothing. Loud rejection beats silent config evaporation.
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-tests",
            }),
            CancellationToken.None);

        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("-tests", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task PathShapedExtraArguments_AppendAsScanPatterns()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "./subpkg/...",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var argv = scanExec!.Argv;
        Assert.Equal("./subpkg/...", argv[^1]);
        Assert.Equal("./...", argv[^2]);
    }

    [Fact]
    public async Task BarePackagePatternExtras_AreConfinedToTheWorktree()
    {
        // gosec hands scan patterns to packages.Load, where only a ./-relative
        // pattern is directory-relative: a bare "std", "all", or
        // "golang.org/x/..." would resolve against GOROOT or the module build
        // list — outside the audited tree. Bare entries are normalized to
        // their ./ form so an extra can never widen scope beyond the worktree.
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "subpkg/..., std",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var argv = scanExec!.Argv;
        Assert.Equal("./std", argv[^1]);
        Assert.Equal("./subpkg/...", argv[^2]);
        Assert.Equal("./...", argv[^3]);
    }

    [Fact]
    public async Task TreeEscapingExtraArguments_AreRejectedDeterministically_ScanNeverRuns()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "../outside/...",
            }),
            CancellationToken.None);

        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("../outside/...", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task ExtraArguments_AreForwardedNormalized_NotVerbatim()
    {
        // The argv reaching gosec must carry the validated normalized form:
        // a backslash-separated pattern forwarded verbatim would be a
        // different, nonexistent package path to the tool.
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = @".\subpkg\...",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal("./subpkg/...", scanExec!.Argv[^1]);
    }

    [Fact]
    public async Task ExcludeGenerated_OptsIntoRepoAuthoredMarkerSkipping()
    {
        // Default off — honoring the "Code generated … DO NOT EDIT" marker
        // by default would let the audit subject erase a file from analysis
        // with one comment line. The operator opt-in passes the flag.
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + GosecAuditor.ExcludeGeneratedKey] = "true",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Contains("-exclude-generated", scanExec!.Argv);
    }

    [Fact]
    public async Task NestedModuleRoots_FailClosed_BeforeTheScanRuns()
    {
        // `go list ./...` never descends into a directory carrying its own
        // go.mod — a separate module root is skipped with no error recorded,
        // so subject code there would ride a passing verdict. The pre-scan
        // probe enumerates every go.mod under the worktree and fails closed
        // listing the roots beyond the root module's.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsModuleProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "./go.mod\n./services/hidden/go.mod\n./tools/x/go.mod\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("nested Go module root", ex.Message, StringComparison.Ordinal);
        Assert.Contains("services/hidden", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tools/x", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task NestedModuleRoots_PrunedTreesDoNotTripTheGate()
    {
        // go.mod files in trees the go tool never reaches — vendor/,
        // testdata/, dot- and underscore-prefixed dirs — are pruned by the
        // probe, so only the root module's remains and the scan proceeds.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsModuleProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./go.mod\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        IAuditor auditor = new GosecAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NestedModuleRoots_OperatorAcknowledgement_ProceedsWithWarning()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + GosecAuditor.AllowNestedModulesKey] = "true",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsModuleProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "./go.mod\n./services/hidden/go.mod\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, scanExecs);
    }

    [Fact]
    public async Task ModuleScopeProbe_FailsClosed_OnProbeFailure()
    {
        IAuditor auditor = new GosecAuditor();

        // A probe that cannot complete cannot prove the scan covers the
        // tree — unverifiable scope is infrastructure, never a pass.
        var failing = new FakeSandbox((exec, _) => Task.FromResult(
            IsModuleProbe(exec)
                ? new SandboxExecResult(1, "", "find: unknown predicate")
                : Ok(exec)));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(failing, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("module roots", ex.Message, StringComparison.OrdinalIgnoreCase);

        var unavailable = new FakeSandbox((exec, _) => Task.FromResult(
            IsModuleProbe(exec)
                ? new SandboxExecResult(0, "", "", ExecutionUnavailable: true)
                : Ok(exec)));
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(unavailable, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task WrongOrMissingVersion_IsInfrastructure_ScanNeverRuns()
    {
        // A different release changes gosec's rules and findings — fail
        // closed rather than audit against an unknown vocabulary.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Version: 2.20.0\nGit tag: v2.20.0\nBuild date: 2025-01-01\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ErrorsJsonEmpty));
        });

        IAuditor auditor = new GosecAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("2.20.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GosecAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);

        // `go install` builds report "Version: dev" — no usable version
        // string is infrastructure too, never silently accepted.
        var devBuild = new FakeSandbox((exec, _) => Task.FromResult(
            IsVersionProbe(exec)
                ? new SandboxExecResult(0, "Version: dev\nGit tag: \nBuild date: \n", "")
                : new SandboxExecResult(0, "", "")));
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(devBuild, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "2.27.1" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(
            IsVersionProbe(exec)
                ? new SandboxExecResult(0, "Version: 2.27.1\nGit tag: v2.27.1\nBuild date: 2026-06-01\n", "")
                : IsPresenceProbe(exec) || IsModuleProbe(exec)
                    ? new SandboxExecResult(0, "", "")
                    : new SandboxExecResult(GosecAuditor.IssuesOrErrorsExitCode, SarifWithWeakRand, ErrorsJsonEmpty)));

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new GosecAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // Operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:ExcludedRules"] = "G101",
            }),
            CancellationToken.None);

        var vendored = SarifWithWeakRand.Replace("main.go", "vendor/pkg/main.go", StringComparison.Ordinal);
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("G404", "G101", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(GosecAuditor.IssuesOrErrorsExitCode, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    /// <summary>
    /// Real-binary end-to-end check: a fixture Go module with a weak
    /// random source (G404, severity HIGH) is scanned by the actual gosec
    /// through a real process exec — exercising argv construction, the
    /// findings exit code, severity recovery, and SARIF parsing together,
    /// so a broken real invocation (e.g. an unsupported flag) cannot stay
    /// green. Runs only where gosec and the go toolchain are on PATH; the
    /// auditor's version pin is set to the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_gosec", "true")]
    public async Task RealGosec_WeakRandInSource_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = await ProbeInstalledGosecVersionAsync();
        if (installed is null || !await BinaryOnPathAsync("go"))
            return;

        var repo = await SeedFixtureRepoAsync(vulnerable: true);
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

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("codeybox:gosec", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("G404", finding.Title, StringComparison.Ordinal);
            Assert.StartsWith("main.go:", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Real-binary regression for the masked-error case: a package gosec's
    /// loader cannot analyze (an import no module provides — unresolvable
    /// in the no-network audit sandbox) sitting beside a real finding must
    /// fail closed as infrastructure. Exit 1 covers both cases and SARIF
    /// alone cannot tell them apart; only the stderr "Golang errors"
    /// channel can.
    /// </summary>
    [Fact]
    [Trait("requires_gosec", "true")]
    public async Task RealGosec_UnloadablePackageBesideFindings_IsInfrastructure()
    {
        var installed = await ProbeInstalledGosecVersionAsync();
        if (installed is null || !await BinaryOnPathAsync("go"))
            return;

        var repo = await SeedFixtureRepoAsync(vulnerable: true);
        var hiddenDir = Path.Combine(repo, "hidden");
        Directory.CreateDirectory(hiddenDir);
        await File.WriteAllTextAsync(
            Path.Combine(hiddenDir, "hidden.go"),
            "package hidden\n\nimport _ \"nonexistent.invalid/dep\"\n");
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

            await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture Go module with no issues
    /// passes with zero findings.
    /// </summary>
    [Fact]
    [Trait("requires_gosec", "true")]
    public async Task RealGosec_CleanFixtureModule_Passes_WithNoFindings()
    {
        var installed = await ProbeInstalledGosecVersionAsync();
        if (installed is null || !await BinaryOnPathAsync("go"))
            return;

        var repo = await SeedFixtureRepoAsync(vulnerable: false);
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
        // Verify-only by design: no distro package carries a version pin
        // for either tool, so the baseline verifies presence and the
        // operator provisions the pinned releases.
        Assert.All(tools, t => Assert.Null(t.AptPackage));

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var verificationArgv = contributions.VerificationCommands
            .SelectMany(static v => v.Argv)
            .ToList();
        Assert.Contains("gosec", verificationArgv);
        Assert.Contains("go", verificationArgv);
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
            PluginDisplayName: "CodeyBox: Gosec Go Security",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Version: " + GosecAuditor.DefaultExpectedVersion + "\nGit tag: v" + GosecAuditor.DefaultExpectedVersion + "\nBuild date: 2026-07-14\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout, string scanStderr = ErrorsJsonEmpty)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec) || IsModuleProbe(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, scanStderr)));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "gosec" && exec.Argv[1] == "-version";

    // The pre-scan nested-module gate: a `find` enumerating go.mod files
    // under the worktree root. An empty stdout means "no nested modules".
    private static bool IsModuleProbe(SandboxExec exec)
        => exec.Argv.Count == 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("go.mod", StringComparison.Ordinal);

    private static async Task<bool> BinaryOnPathAsync(string binary)
        => (await TryRunProbeAsync(binary, "version").ConfigureAwait(false))?.ExitCode == 0;

    private static async Task<string?> ProbeInstalledGosecVersionAsync()
    {
        var probe = await TryRunProbeAsync("gosec", "-version").ConfigureAwait(false);
        if (probe is null || probe.Value.ExitCode != 0)
            return null;
        // "Version: 2.28.0\nGit tag: v2.28.0\n..." — `go install` builds
        // report "dev" and can never satisfy the pin, so they yield no
        // gated run.
        var versionLine = probe.Value.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static l => l.StartsWith("Version:", StringComparison.Ordinal));
        var version = versionLine?["Version:".Length..].Trim();
        return string.IsNullOrWhiteSpace(version) || version == "dev" ? null : version;
    }

    // Runs a one-shot version-style probe with a hard bound: both streams
    // are drained concurrently with the wait so a chatty tool cannot block
    // on a full pipe, and a process outliving the timeout is killed. Null
    // means "could not run" — a missing binary, a start failure, or a
    // timeout — which the gated tests treat as absence and return early
    // rather than fail on a host without the tool.
    private static async Task<(int ExitCode, string Stdout)?> TryRunProbeAsync(string binary, string argument)
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
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(ProbeTimeoutMilliseconds);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch { /* drained pipes may fault after the kill — observed, not a result */ }
                return null;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (process.ExitCode, stdout.Result);
        }
        catch
        {
            return null;
        }
    }

    private const int ProbeTimeoutMilliseconds = 10_000;

    private static async Task<string> SeedFixtureRepoAsync(bool vulnerable)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-gosec-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        // A self-contained stdlib-only module: gosec loads it through the
        // local toolchain with no module downloads, so the gated test needs
        // no network.
        await File.WriteAllTextAsync(
            Path.Combine(repo, "go.mod"),
            "module fixture.local/audit\n\ngo 1.21\n");
        var main = vulnerable
            ? """
              package main

              import (
                  "fmt"
                  "math/rand"
              )

              func main() {
                  fmt.Println(rand.Intn(100))
              }
              """
            : """
              package main

              import "fmt"

              func main() {
                  fmt.Println("clean")
              }
              """;
        await File.WriteAllTextAsync(Path.Combine(repo, "main.go"), main);
        return repo;
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
