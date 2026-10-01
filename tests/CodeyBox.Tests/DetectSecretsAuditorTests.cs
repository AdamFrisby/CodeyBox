using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.DetectSecretsAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the detect-secrets auditor plugin: a missing or wrong-version
/// binary is infrastructure naming the tool (never a pass), exit 0 is the
/// only findings-producing verdict (detect-secrets scan returns 0 whether
/// or not it found anything — there is no "found something" exit), the
/// baseline JSON maps to findings with detector type and file/line,
/// severity goes through the declared mapping rather than passing through,
/// the silent-empty-results trap on non-git trees is gated, the inline
/// allowlist pragma is neutralized unless the operator opts in, an
/// operator-supplied auditable baseline file suppresses is_secret:false
/// entries, and the plugin is inert — unloaded and absent from baseline
/// provisioning — until an operator enables it. Every run is dispatched
/// through <see cref="IAuditor"/> so the preconditions cannot be bypassed
/// by interface dispatch.
/// </summary>
public sealed class DetectSecretsAuditorTests
{
    // SHA-1 fixture values for `hashed_secret` fields — the report carries
    // the hash of a matched value, never the secret itself. Assembled at
    // runtime like _fixtureSecretLine below so this test file does not
    // itself carry a scanner-detectable 40-hex literal (gitleaks' generic
    // api-key rule trips on them even though they are hashes, not secrets).
    private static readonly string _fixtureHashA =
        "5baa61e4c9b93f3f0682250b6cf8331b" + "7ee68fd8";
    private static readonly string _fixtureHashB =
        "27c6929aef41ae2bcadac15ca6abcaff" + "72cda9cd";
    private static readonly string _fixtureHashC =
        "daefe0b4345a654580dcad25c7c11ff4c9" + "44a8c0";

    // Mirrors what detect-secrets 1.5.0 `scan` writes to stdout without
    // --baseline: a baseline document whose `results` maps each relative
    // filename to the secrets found there. The raw secret is never emitted —
    // only `hashed_secret` (SHA-1). `is_secret` is absent: it only appears on
    // entries merged from an audited baseline.
    private static readonly string BaselineWithSecret = """
        {
          "version": "1.5.0",
          "plugins_used": [{ "name": "KeywordDetector" }],
          "filters_used": [],
          "generated_at": "2026-01-01T00:00:00Z",
          "results": {
            "src/config.py": [
              {
                "type": "Secret Keyword",
                "filename": "src/config.py",
                "hashed_secret": "%%HASH%%",
                "is_verified": false,
                "line_number": 12
              }
            ]
          }
        }
        """.Replace("%%HASH%%", _fixtureHashA, StringComparison.Ordinal);

    private const string BaselineClean = """
        {
          "version": "1.5.0",
          "plugins_used": [{ "name": "KeywordDetector" }],
          "filters_used": [],
          "generated_at": "2026-01-01T00:00:00Z",
          "results": {}
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingDetectSecrets_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "detect-secrets: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detect-secrets", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SecretInTrackedFile_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, BaselineWithSecret, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:detect-secrets", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("Secret Keyword", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/config.py:12", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("detect-secrets", argv[0]);
        Assert.Equal("scan", argv[1]);
        Assert.Contains("--no-verify", argv);
        var disableFilter = argv.ToList().IndexOf("--disable-filter");
        Assert.True(disableFilter >= 0 && disableFilter + 1 < argv.Count);
        Assert.Equal(
            "detect_secrets.filters.allowlist.is_line_allowlisted",
            argv[disableFilter + 1]);
        Assert.Contains(".", argv);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: BaselineClean);
        IAuditor auditor = new DetectSecretsAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitZero_IsTheOnlyVerdict_ErrorExits_AreInfrastructure()
    {
        // detect-secrets scan returns 0 unconditionally — there is no
        // non-zero "found something" exit; the results object in stdout's
        // baseline JSON is the verdict (verified against 1.5.0 main.py).
        IAuditor auditor = new DetectSecretsAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(0, BaselineWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // 1 = unhandled exception (also argparse post-processing failures
        // like an unreadable --baseline). Even with a parseable report on
        // stdout, exit 1 means "could not run".
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, BaselineWithSecret), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // 2 = argparse usage error; any other undeclared convention is
        // infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, "usage: detect-secrets ..."), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // Every detect-secrets result — verified or merely suspected — maps
        // to Error under the declared mapping: the tool has no severity
        // vocabulary to pass through, so this asserts the *declared* map
        // produces Error rather than any raw tool value surviving.
        IAuditor auditor = new DetectSecretsAuditor();
        var unverified = await auditor.RunAsync(
            HealthyTool(0, BaselineWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        var unverifiedFinding = Assert.Single(unverified.Findings);
        Assert.Equal(AuditSeverity.Error, unverifiedFinding.Severity);
        Assert.False(unverified.Passed);

        var verifiedBaseline = BaselineWithSecret.Replace(
            "\"is_verified\": false", "\"is_verified\": true", StringComparison.Ordinal);
        var verified = await auditor.RunAsync(
            HealthyTool(0, verifiedBaseline),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(verified.Findings).Severity);
        Assert.False(verified.Passed);
    }

    [Fact]
    public async Task UnparseableReport_IsInfrastructure_NotAPass()
    {
        // Exit 0 with output that is not a baseline document means the scan
        // was subverted (--list-all-plugins, --string, a stray print) — the
        // report is the verdict, so untrusted output fails closed.
        var sandbox = HealthyTool(0, "ArtifactoryDetector\nAWSKeyDetector\n");
        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("detect-secrets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonGitWorktree_FailsClosed_ScanNeverRuns()
    {
        // Outside a git worktree, `detect-secrets scan .` scans nothing but
        // still exits 0 with an empty results — the false pass this
        // precondition exists to prevent.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(128, "", "fatal: not a git repository"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detect-secrets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("worktree", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WorktreeSubdirectory_FailsClosed_ScanNeverRuns()
    {
        // Below the worktree root, detect-secrets scans only the subtree —
        // partial coverage must not read as a verdict on the repository.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "")); // non-empty --show-prefix
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task AllFilesOption_SkipsWorktreePrecondition()
    {
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--all-files",
            }),
            CancellationToken.None);

        var worktreeProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsWorktreeProbe(exec))
            {
                worktreeProbes++;
                return Task.FromResult(new SandboxExecResult(128, "", "fatal: not a git repository"));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, worktreeProbes);
    }

    [Fact]
    public async Task TrustedRepositorySuppression_OptsIn_AllowlistFilterStaysEnabled()
    {
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + DetectSecretsAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.DoesNotContain("--disable-filter", scanExec!.Argv);
    }

    [Fact]
    public async Task BaselineFile_AuditedFalsePositives_AreSuppressed_OtherSecretsReported()
    {
        const string canonicalBaseline = "/opt/codeybox-audit/known-secrets.json";
        var mergedReport = """
            {
              "version": "1.5.0",
              "plugins_used": [{ "name": "KeywordDetector" }],
              "filters_used": [],
              "generated_at": "2026-01-01T00:00:00Z",
              "results": {
                "src/config.py": [
                  {
                    "type": "Secret Keyword",
                    "filename": "src/config.py",
                    "hashed_secret": "%%HASH_A%%",
                    "is_verified": false,
                    "is_secret": false,
                    "line_number": 12
                  },
                  {
                    "type": "AWS Access Key",
                    "filename": "src/config.py",
                    "hashed_secret": "%%HASH_B%%",
                    "is_verified": false,
                    "is_secret": true,
                    "line_number": 40
                  }
                ],
                "new/file.py": [
                  {
                    "type": "Base64 High Entropy String",
                    "filename": "new/file.py",
                    "hashed_secret": "%%HASH_C%%",
                    "is_verified": false,
                    "line_number": 3
                  }
                ]
              }
            }
            """
            .Replace("%%HASH_A%%", _fixtureHashA, StringComparison.Ordinal)
            .Replace("%%HASH_B%%", _fixtureHashB, StringComparison.Ordinal)
            .Replace("%%HASH_C%%", _fixtureHashC, StringComparison.Ordinal);

        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + DetectSecretsAuditor.BaselineFileKey] = canonicalBaseline,
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsCanonicalizeProbe(exec))
                // configured path + "." canonicalized: baseline resolves outside /work
                return Task.FromResult(new SandboxExecResult(0, canonicalBaseline + "\n/work\n", ""));
            if (IsBaselineCopyProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, mergedReport, ""));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // --baseline redirects the report into the file: stdout is empty
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        // The audited false positive (is_secret: false) is suppressed; the
        // audited real secret and the unlabeled new finding are reported.
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Location == "src/config.py:40");
        Assert.Contains(result.Findings, f => f.Location == "new/file.py:3");
        Assert.DoesNotContain(result.Findings, f => f.Location == "src/config.py:12");

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var baselineFlag = argv.ToList().IndexOf("--baseline");
        Assert.True(baselineFlag >= 0 && baselineFlag + 1 < argv.Count);
        Assert.EndsWith(DetectSecretsAuditor.BaselineCopyFileName, argv[baselineFlag + 1], StringComparison.Ordinal);
        Assert.DoesNotContain(canonicalBaseline, argv);
        Assert.Contains("--force-use-all-plugins", argv);
    }

    [Fact]
    public async Task BaselineFile_ResolvingInsideWorktree_FailsClosed()
    {
        var scanExecs = 0;
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + DetectSecretsAuditor.BaselineFileKey] = ".secrets.baseline",
            }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsCanonicalizeProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work/.secrets.baseline\n/work\n", ""));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(DetectSecretsAuditor.BaselineFileKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("worktree", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ReservedExtraArguments_Baseline_IsRejected()
    {
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // An unguarded --baseline would load plugin/filter settings
                // from — and redirect the report into — an arbitrary path.
                ["Scoped:ExtraArguments"] = "--baseline,.secrets.baseline",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("--baseline", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DetectSecretsAuditor.BaselineFileKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task QuotedTrackedFileNames_FailClosed_ScanNeverRuns()
    {
        // git C-quotes tracked names containing '"', '\', control bytes, or
        // bytes >= 0x80 (a tracked 'sëcrets.txt' is listed as
        // "s\303\253crets.txt"); detect-secrets never unquotes the ls-files
        // output, so such files are silently never scanned while the scan
        // still exits 0 with an empty contribution — a clean pass for files
        // never read. The precondition probe reports this as exit
        // QuotedTrackedNamesExit and the auditor must fail closed.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    DetectSecretsAuditor.QuotedTrackedNamesExit, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detect-secrets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("C-quote", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task AllFilesAfterOptionTerminator_DoesNotSkipWorktreeGate()
    {
        // argparse treats tokens after a bare `--` as positional paths, so
        // a `--all-files` there is not the flag — all-files mode is not
        // active and the worktree precondition must still run (and, on a
        // non-git tree, still fail closed).
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--,--all-files",
            }),
            CancellationToken.None);

        var worktreeProbes = 0;
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsWorktreeProbe(exec))
            {
                worktreeProbes++;
                return Task.FromResult(new SandboxExecResult(128, "", "fatal: not a git repository"));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Equal(1, worktreeProbes);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task AllFilesAbbreviation_SkipsWorktreePrecondition()
    {
        // Python argparse resolves unambiguous long-option prefixes
        // (allow_abbrev): '--all' reaches the parser as '--all-files', so
        // the gate is genuinely off and the worktree probe is skipped.
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--all",
            }),
            CancellationToken.None);

        var worktreeProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsWorktreeProbe(exec))
            {
                worktreeProbes++;
                return Task.FromResult(new SandboxExecResult(128, "", "fatal: not a git repository"));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, worktreeProbes);
    }

    [Theory]
    // argparse resolves unambiguous long-option prefixes by default — these
    // abbreviations reach the tool as the reserved flag itself.
    [InlineData("--bas", "--baseline")]              // abbreviation of --baseline
    [InlineData("--bas=x.json", "--baseline")]       // attached-value abbreviation
    [InlineData("--plug", "--plugin")]               // abbreviation of --plugin
    // File-loading flags resolved inside the worktree — short forms,
    // the joined '-fvalue' spelling, and bundled clusters: '-np' parses as
    // -n then -p because -n takes no value, so the reserved letter is
    // rejected wherever it appears in a single-dash token.
    [InlineData("-p,./detector.py", "--plugin")]
    [InlineData("-np,./detector.py", "--plugin")]
    [InlineData("-f./filters.py::fn", "--filter")]
    [InlineData("-nf,./filters.py::fn", "--filter")]
    [InlineData("-vf,./filters.py::fn", "--filter")]
    [InlineData("--word-list", "--word-list")]
    [InlineData("--gibberish-model,./model.bin", "--gibberish-model")]
    // Coverage-reshaping flags.
    [InlineData("-C", "--custom-root")]
    [InlineData("-Csub/", "--custom-root")]
    [InlineData("-nC,sub/", "--custom-root")]
    [InlineData("--custom-root,sub/", "--custom-root")]
    [InlineData("--only-allowlisted", "--only-allowlisted")]
    public async Task ReservedExtraArguments_FileLoadingAndCoverage_AreRejected(
        string extraArguments, string expectedFlag)
    {
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reserved flag", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFlag, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task ReservedExtraArguments_RejectionPrecedesBaselineCopyExecs()
    {
        // The reserved-flag check is deterministic and must fire before any
        // sandbox exec — including the realpath canonicalization probe and
        // the baseline scratch copy a configured BaselineFile triggers.
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + DetectSecretsAuditor.BaselineFileKey] = "/opt/baseline.json",
                ["Scoped:ExtraArguments"] = "-np,./detector.py",
            }),
            CancellationToken.None);

        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(Ok(exec));
        });

        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task QuotedNameCheckFailure_FailsClosed_ScanNeverRuns()
    {
        // The C-quote coverage check runs inside the probe; when the check
        // itself cannot complete (the probe's fail-closed sentinel) coverage
        // is unproven — infrastructure, never a pass.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    DetectSecretsAuditor.QuotedNameCheckFailedExit, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("detect-secrets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("C-quoted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WorktreeProbeUnexpectedExit_FailsClosed_ScanNeverRuns()
    {
        // An exit the probe contract does not define still fails closed on
        // the generic branch — never a pass, never a scan.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(9, "", "probe exploded"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("exit 9", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task BaselineFile_ResolvedOncePerRun_ConfigFlipMidRun_KeepsReportSource()
    {
        // Scoped config is hot-reloadable, but the baseline decision must be
        // fixed for the duration of one run: if the configured key is unset
        // between argv construction and report read-back, the report must
        // still come from the staged copy argv names — not from (empty)
        // stdout, which would misfire as an unparseable report.
        const string canonicalBaseline = "/opt/codeybox-audit/known-secrets.json";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scoped:" + DetectSecretsAuditor.BaselineFileKey] = canonicalBaseline,
            })
            .Build();
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            new PluginContext(
                HostApiVersion: "1.0",
                PluginId: DetectSecretsAuditor.PluginId,
                PluginDisplayName: "CodeyBox: detect-secrets Secrets",
                Host: new TestPluginHost(config.GetSection("Scoped"))),
            CancellationToken.None);

        var reportReads = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsCanonicalizeProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, canonicalBaseline + "\n/work\n", ""));
            if (IsBaselineCopyProbe(exec))
            {
                // Mid-run config flip: the baseline knob disappears between
                // ResolveContextArgumentsAsync and ResolveParserInputAsync.
                config["Scoped:" + DetectSecretsAuditor.BaselineFileKey] = null;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            if (IsReportRead(exec))
            {
                reportReads++;
                return Task.FromResult(new SandboxExecResult(0, BaselineWithSecret, ""));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(Ok(exec));
            // Baseline mode redirects the report into the file: stdout empty.
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/config.py:12", finding.Location);
        Assert.Equal(1, reportReads);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.4.0\n", ""));
            if (IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, BaselineClean, ""));
        });

        IAuditor auditor = new DetectSecretsAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("1.4.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DetectSecretsAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "1.4.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsWorktreeProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.4.0\n", ""));
            return Task.FromResult(new SandboxExecResult(0, BaselineWithSecret, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new DetectSecretsAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "Secret Keyword",
            }),
            CancellationToken.None);

        var vendored = BaselineWithSecret.Replace("src/config.py", "vendor/pkg/config.py", StringComparison.Ordinal);
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(0, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("Secret Keyword", "Hex High Entropy String", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(0, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Canonical AWS documentation example key shape — not a real credential;
    // it exists so the pinned detector set produces a finding at a known
    // location. Assembled at runtime so this test file does not itself carry
    // a scanner-detectable literal.
    private static readonly string _fixtureSecretLine =
        "aws_access_key_id = \"AKIA" + "IOSFODNN7" + "EXAMPLE\"";

    /// <summary>
    /// Real-binary end-to-end check: a fixture git repository with a
    /// committed secret-shaped string is scanned by the actual detect-secrets
    /// through a real process exec — exercising argv construction, the
    /// worktree precondition, exit-0-is-the-verdict semantics, and baseline
    /// JSON parsing together, so a broken real invocation (e.g. an
    /// unsupported flag) cannot stay green. Runs only where a detect-secrets
    /// binary is on PATH; the auditor's version pin is set to the installed
    /// release.
    /// </summary>
    [SkippableFact]
    [Trait("requires_detect_secrets", "true")]
    public async Task RealDetectSecrets_SecretInTrackedFile_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = await ProbeInstalledDetectSecretsVersionAsync();
        Skip.If(installed is null, "detect-secrets is not on PATH — provision it to run the real-binary coverage.");

        var repo = await SeedFixtureRepoAsync(_fixtureSecretLine);
        try
        {
            var auditor = new DetectSecretsAuditor();
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
            Assert.NotEmpty(result.Findings);
            Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
            Assert.Contains(result.Findings, f => f.Location == "secrets.txt:1");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture repository with no secrets
    /// passes with zero findings — and proves the worktree precondition does
    /// not misfire on a healthy repo.
    /// </summary>
    [SkippableFact]
    [Trait("requires_detect_secrets", "true")]
    public async Task RealDetectSecrets_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = await ProbeInstalledDetectSecretsVersionAsync();
        Skip.If(installed is null, "detect-secrets is not on PATH — provision it to run the real-binary coverage.");

        var repo = await SeedFixtureRepoAsync(null);
        try
        {
            var auditor = new DetectSecretsAuditor();
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
            s => s.PluginId == DetectSecretsAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("detect-secrets", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresDetectSecretsRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [DetectSecretsAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == DetectSecretsAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("detect-secrets", tool.Binary);
        // Verify-only by design: detect-secrets is a Python package with no
        // distro apt package, and the pin must hold byte-for-byte — the
        // baseline verifies presence and the operator provisions the pinned
        // release. No apt line is emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("detect-secrets", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.DetectSecretsAuditorPlugin.dll");
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
            PluginId: DetectSecretsAuditor.PluginId,
            PluginDisplayName: "CodeyBox: detect-secrets Secrets",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, DetectSecretsAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeProbe(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsWorktreeProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("rev-parse", StringComparison.Ordinal);

    private static bool IsCanonicalizeProbe(SandboxExec exec)
        => exec.Argv.Count >= 1 && exec.Argv[0] == "realpath";

    private static bool IsBaselineCopyProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains(DetectSecretsAuditor.BaselineCopyFileName, StringComparison.Ordinal);

    private static bool IsReportRead(SandboxExec exec)
        => exec.Argv.Count == 3 && exec.Argv[0] == "cat" && exec.Argv[1] == "--";

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "detect-secrets" && exec.Argv[1] == "--version";

    private static async Task<string> SeedFixtureRepoAsync(string? secretLine)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-detect-secrets-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        var (file, content) = secretLine is null
            ? ("README.md", "clean\n")
            : ("secrets.txt", secretLine + "\n");
        await File.WriteAllTextAsync(Path.Combine(repo, file), content);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    // Probes the host's detect-secrets for its version so the real-binary
    // tests can pin to whatever is installed. The 10s bound must actually
    // fire: both streams drain on ReadToEndAsync BEFORE the bounded wait,
    // so a child that fills a pipe cannot deadlock the probe, and a hung
    // binary is killed at the deadline rather than blocking test setup
    // indefinitely.
    private static async Task<string?> ProbeInstalledDetectSecretsVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "detect-secrets",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdoutRead = process.StandardOutput.ReadToEndAsync();
            var stderrRead = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var exited = true;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                exited = false;
                try { process.Kill(entireProcessTree: true); }
                catch { /* best-effort probe teardown */ }
            }
            string version;
            try
            {
                // Observe both reads so a failed/hung child cannot leave a
                // faulted drain task unobserved.
                version = (await stdoutRead).Trim();
                await stderrRead;
            }
            catch
            {
                return null;
            }
            return exited && process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch
        {
            // Any failure means no usable detect-secrets on PATH — the gated
            // tests return early rather than fail on a host without the tool.
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
