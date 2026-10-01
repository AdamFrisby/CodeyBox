using System.Diagnostics;
using CodeyBox.BetterleaksAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the betterleaks auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), the findings exit code the
/// plugin assigns is a verdict while betterleaks's error exits are not, SARIF
/// maps to findings with rule id and file/line, severity goes through the
/// declared mapping rather than passing through, repository-controlled
/// suppression surfaces are neutralized unless the operator opts in, and the
/// plugin is inert — unloaded and absent from baseline provisioning — until
/// an operator enables it. Every run is dispatched through <see cref="IAuditor"/>
/// so the version-pin precondition cannot be bypassed by interface dispatch.
/// </summary>
public sealed class BetterleaksAuditorTests
{
    // Mirrors what betterleaks v1.8.1 writes to stdout for `--report-format
    // sarif --report-path -`: no per-result "level" (the shared parser
    // supplies "warning"), ruleId, message text naming rule/file/commit, and
    // the first physical location's artifact uri plus region.startLine. The
    // driver semanticVersion is the hardcoded "v8.0.0" inherited from the
    // gitleaks lineage — intentionally not the release version, which is why
    // the plugin probes `betterleaks version`.
    private const string SarifWithSecret = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "betterleaks",
                "semanticVersion": "v8.0.0",
                "informationUri": "https://github.com/betterleaks/betterleaks",
                "rules": [{ "id": "slack-bot-token", "shortDescription": { "text": "Slack Bot token" } }]
              }
            },
            "results": [{
              "message": { "text": "slack-bot-token has detected secret for file secrets.txt at commit 0123456789abcdef." },
              "ruleId": "slack-bot-token",
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "secrets.txt" },
                  "region": { "startLine": 1, "startColumn": 10, "endLine": 1, "endColumn": 65, "snippet": { "text": "REDACTED" } }
                }
              }],
              "partialFingerprints": { "commitSha": "0123456789abcdef", "email": "a@b.c", "author": "a", "date": "2026-01-01", "commitMessage": "x" },
              "properties": { "tags": [] }
            }]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "betterleaks", "semanticVersion": "v8.0.0", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingBetterleaks_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "betterleaks: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("betterleaks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SecretInSourceAndHistory_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(BetterleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:betterleaks", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("slack-bot-token", finding.Title, StringComparison.Ordinal);
        Assert.Equal("secrets.txt:1", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("betterleaks", argv[0]);
        Assert.Equal("git", argv[1]);
        Assert.Contains("--report-format", argv);
        Assert.Contains("sarif", argv);
        Assert.Contains("--report-path", argv);
        Assert.Contains("-", argv);
        Assert.Contains("--redact=100", argv);
        var exitFlag = argv.ToList().IndexOf("--exit-code");
        Assert.True(exitFlag >= 0 && exitFlag + 1 < argv.Count);
        Assert.Equal(
            BetterleaksAuditor.LeaksFoundExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            argv[exitFlag + 1]);

        // Repository-controlled suppression is neutralized by default: inline
        // allow comments disabled, the ignore-file flag pointed away from the
        // repo, and the built-in ruleset pinned via env so a repo config file
        // cannot add filters or rewrite rules.
        Assert.Contains("--ignore-gitleaks-allow", argv);
        var ignoreFlag = argv.ToList().IndexOf("--gitleaks-ignore-path");
        Assert.True(ignoreFlag >= 0 && ignoreFlag + 1 < argv.Count);
        Assert.StartsWith("/", argv[ignoreFlag + 1], StringComparison.Ordinal);
        Assert.NotNull(scanExec.ExtraEnvironment);
        Assert.Contains(
            "useDefault",
            Assert.Contains("BETTERLEAKS_CONFIG_TOML", scanExec.ExtraEnvironment),
            StringComparison.Ordinal);

        // The precedence-2 config-PATH env vars are unset on the scan: a
        // baseline-exported BETTERLEAKS_CONFIG — or GITLEAKS_CONFIG, which
        // betterleaks reads as a fallback spelling — would otherwise outrank
        // the pinned ruleset with a path the canonicalization guard never
        // sees.
        Assert.Contains("BETTERLEAKS_CONFIG", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GITLEAKS_CONFIG", scanExec.EnvironmentVariablesToUnset);

        // .gitattributes countermeasure: the pinned log opts re-state the
        // tool's default rev args (--log-opts replaces them wholesale) and
        // add --text, so a committed `path -diff`/`binary` attribute cannot
        // blank the patch stream git log feeds the scanner.
        var logOpts = argv.ToList().IndexOf("--log-opts");
        Assert.True(logOpts >= 0 && logOpts + 1 < argv.Count);
        Assert.Equal("--full-history --all --diff-filter=tuxdb --text", argv[logOpts + 1]);
    }

    [Theory]
    [InlineData(".betterleaksignore")]
    [InlineData(".gitleaksignore")]
    [InlineData(".betterleaks.toml")]
    [InlineData(".gitleaks.toml")]
    public async Task RepoSuppressionFile_FailsClosed_ScanNeverRuns(string suppressionFile)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, suppressionFile + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(suppressionFile, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            BetterleaksAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    // betterleaks's stock prefilter drops every fragment whose path contains
    // the literal "gitleaks.toml" — at any depth, under any affixed spelling —
    // before any rule runs, so a secret in such a file is never reported. The
    // gate fails closed on a match in the worktree rather than passing over a
    // path the scanner cannot see.
    [Theory]
    [InlineData("./docs/gitleaks.toml\n")]
    [InlineData("./x-gitleaks.toml.bak\n")]
    [InlineData("./deep/nested/gitleaks.toml\n./other.png\n")] // non-matching chatter is ignored
    public async Task RulesetExemptedPath_InWorktree_FailsClosed_ScanNeverRuns(string probeOutput)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec)
                || IsWorktreeSuppressionProbe(exec) || IsHistorySuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsPathGlobProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, probeOutput, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("*gitleaks.toml*", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            BetterleaksAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    // The exemption covers every commit, not just the worktree: a matching
    // path committed and then deleted still hides any secret it contained.
    [Fact]
    public async Task RulesetExemptedPath_InGitHistory_FailsClosed_ScanNeverRuns()
    {
        var scanExecs = 0;
        SandboxExec? historyProbe = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec)
                || IsWorktreeSuppressionProbe(exec) || IsPathGlobProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsHistorySuppressionProbe(exec))
            {
                historyProbe = exec;
                return Task.FromResult(
                    new SandboxExecResult(0, "0123456789abcdef0123456789abcdef01234567\n", ""));
            }
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("*gitleaks.toml*", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
        // The history probe anchors at the repository root with glob
        // semantics so a nested or affixed path cannot slip past.
        Assert.Contains(
            ":(top,glob)*gitleaks.toml*",
            historyProbe!.Argv[^1],
            StringComparison.Ordinal);
    }

    // A failed history probe can never substitute for the gate: a non-zero
    // git log exit is infrastructure, not evidence the family is absent.
    [Fact]
    public async Task RulesetExemptedPath_HistoryProbeError_IsInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec)
                || IsWorktreeSuppressionProbe(exec) || IsPathGlobProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsHistorySuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(128, "", "fatal: not a git repo"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task TrustedRepositorySuppression_OptsIn_ScanRunsWithoutGuards()
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + BetterleaksAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        var suppressionProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
            {
                suppressionProbes++;
                return Task.FromResult(new SandboxExecResult(0, ".betterleaksignore\n", ""));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(BetterleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(0, suppressionProbes);
        Assert.DoesNotContain("--ignore-gitleaks-allow", scanExec!.Argv);
        Assert.False(scanExec.ExtraEnvironment?.ContainsKey("BETTERLEAKS_CONFIG_TOML") ?? false);
        // The config-path env removals apply in trust mode too: a baseline
        // *_CONFIG would otherwise outrank the very repo config trust mode
        // exists to honor.
        Assert.Contains("BETTERLEAKS_CONFIG", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GITLEAKS_CONFIG", scanExec.EnvironmentVariablesToUnset);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new BetterleaksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsExit_IsVerdict_WhileErrorExits_AreInfrastructure()
    {
        // The plugin-assigned findings exit produces a verdict.
        IAuditor auditor = new BetterleaksAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(BetterleaksAuditor.LeaksFoundExitCode, SarifWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // betterleaks's fatal/error exit is 1 — shared with the *default*
        // findings code, which is exactly why the plugin moves findings to 4.
        // Even with parseable SARIF on stdout, exit 1 means "could not run".
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, SarifWithSecret), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, "usage: betterleaks ..."), "/work", FakeContext(), CancellationToken.None));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("critical")]
    [InlineData("medium")]
    [InlineData("low")]
    [InlineData("note")]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough(string toolLevel)
    {
        // betterleaks reports confidence, not severity, and its SARIF carries
        // no level at all — so no raw tool string may survive as a finding
        // severity. Whatever level a report claims, the declared mapping
        // sends it to the blocking Error.
        var leveledSarif = SarifWithSecret.Replace(
            "\"ruleId\": \"slack-bot-token\"",
            "\"level\": \"" + toolLevel + "\", \"ruleId\": \"slack-bot-token\"",
            StringComparison.Ordinal);
        IAuditor auditor = new BetterleaksAuditor();
        var result = await auditor.RunAsync(
            HealthyTool(BetterleaksAuditor.LeaksFoundExitCode, leveledSarif),
            "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
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
                return Task.FromResult(new SandboxExecResult(0, "1.7.0\n", ""));
            if (IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("1.7.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(BetterleaksAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "1.7.4" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.7.4\n", ""));
            return Task.FromResult(new SandboxExecResult(BetterleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task UnparseableVersionOutput_IsInfrastructure_NotAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "dev-build\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = BetterleaksAuditor.DefaultExpectedVersion,
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "slack-bot-token",
            }),
            CancellationToken.None);

        var vendored = SarifWithSecret.Replace("secrets.txt", "vendor/pkg/secrets.txt");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(BetterleaksAuditor.LeaksFoundExitCode, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("slack-bot-token", "other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(BetterleaksAuditor.LeaksFoundExitCode, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    [Fact]
    public async Task MissingGitBinary_IsInfrastructure_NamingGit()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
            {
                // betterleaks itself exists; the git binary `git` mode shells
                // out to does not — the failure names the missing piece.
                var binary = exec.Argv[^1];
                return Task.FromResult(new SandboxExecResult(
                    string.Equals(binary, "git", StringComparison.Ordinal) ? 1 : 0, "", ""));
            }
            if (IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BetterleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("'git'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    // An operator file flag whose value resolves inside the audited worktree
    // hands gate-shaping content (config ruleset, baseline, ignore
    // fingerprints) to repo-controlled bytes — the flag resolves against the
    // tool's cwd — so it fails closed before the scan runs.
    [Theory]
    [InlineData("--config,ops/rules.toml")]
    [InlineData("--config=ops/rules.toml")]
    [InlineData("-cops/rules.toml")]
    // pflag shorthand bundling: "-vc" binds the NEXT token to -c, so the
    // guard must see through the cluster or "ops/rules.toml" escapes the
    // canonicalization check.
    [InlineData("-vc,ops/rules.toml")]
    [InlineData("--baseline-path,ops/baseline.json")]
    [InlineData("--gitleaks-ignore-path,ops/ignore.txt")]
    public async Task ExtraArgumentsFileFlag_ResolvingInsideWorktree_FailsClosed(string extraArguments)
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(RealpathResult(exec, insideWorktree: true));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("worktree", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExtraArgumentsFileFlag_ResolvingOutsideWorktree_ScanRuns()
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--config,/etc/betterleaks/rules.toml",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(RealpathResult(exec, insideWorktree: false));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        // The gate contains, it does not strip — the operator flag still
        // reaches the scan argv verbatim.
        var configFlag = scanExec!.Argv.ToList().IndexOf("--config");
        Assert.True(configFlag >= 0 && configFlag + 1 < scanExec.Argv.Count);
        Assert.Equal("/etc/betterleaks/rules.toml", scanExec.Argv[configFlag + 1]);
    }

    // The same cluster, resolving outside the worktree: the guard extracts
    // the value and lets the scan run — it contains, it does not strip.
    [Fact]
    public async Task ExtraArgumentsFileFlag_ClusteredShortForm_ResolvingOutside_ScanRuns()
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-vc,/etc/betterleaks/rules.toml",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(RealpathResult(exec, insideWorktree: false));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("-vc", scanExec!.Argv);
    }

    [Fact]
    public async Task ExtraArgumentsFileFlag_WithoutValue_FailsClosed()
    {
        var auditor = new BetterleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--config",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("--config", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    // Synthetic token in the slack-bot-token rule's shape — not a real
    // credential; it exists so the pinned default ruleset produces exactly
    // one finding at a known location. Assembled at runtime so this test file
    // does not itself carry a scanner-detectable literal.
    private static readonly string _fixtureSecretLine =
        "token = \"xoxb-" + "123456789012-1234567890123-abcdefghijklmnopqrstuvwx\"";

    private static readonly string? _installedBetterleaksVersion = ProbeInstalledBetterleaksVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture git repository with a committed
    /// secret is scanned by the actual betterleaks through a real process exec —
    /// exercising argv construction, the pinned-ruleset environment, the
    /// findings exit code, and SARIF parsing together, so a broken real
    /// invocation (e.g. an unsupported flag) cannot stay green. Runs only
    /// where a betterleaks binary is on PATH; the auditor's version pin is set to
    /// the installed release.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_SecretInSourceAndHistory_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(_fixtureSecretLine);
        try
        {
            var auditor = new BetterleaksAuditor();
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
            Assert.Equal("codeybox:betterleaks", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("slack-bot-token", finding.Title, StringComparison.Ordinal);
            Assert.Equal("secrets.txt:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture repository with no secrets
    /// passes with zero findings.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(null);
        try
        {
            var auditor = new BetterleaksAuditor();
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

    /// <summary>
    /// Real-binary check for repository-controlled suppression: a repo-root
    /// <c>.betterleaks.toml</c> carrying a global filter that discards every
    /// finding silences the scan, so the default gate fails closed instead of
    /// reporting a pass — and with the operator opt-in the scan runs.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_RepoConfigSuppressesEverything_GateFailsClosed()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(_fixtureSecretLine);
        try
        {
            // Top-level keys must precede the [extend] table header — anything
            // after it belongs to the extend table, not the global filter.
            await File.WriteAllTextAsync(
                Path.Combine(repo, ".betterleaks.toml"),
                "filter = '''true'''\n\n[extend]\nuseDefault = true\n");
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "add suppressing config");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var auditor = new BetterleaksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains(".betterleaks.toml", ex.Message, StringComparison.Ordinal);

            var trusting = new BetterleaksAuditor();
            await trusting.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:" + BetterleaksAuditor.TrustRepositorySuppressionKey] = "true",
                }),
                CancellationToken.None);
            var trusted = await ((IAuditor)trusting).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // The trusted scan honors the repo filter and reports nothing —
            // proving the suppression is real and the gate is load-bearing.
            Assert.True(trusted.Passed);
            Assert.Empty(trusted.Findings);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Real-binary regression for the <c>.gitattributes</c> suppression
    /// channel: marking a secret-bearing path <c>-diff</c> makes
    /// <c>git log -p</c> emit "Binary files differ" with no patch content,
    /// silently blanking the scanner's input. The pinned
    /// <c>--log-opts … --text</c> must still surface the finding.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_SecretMarkedDiffSuppressed_StillYieldsFinding()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(_fixtureSecretLine);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repo, ".gitattributes"), "secrets.txt -diff\n");
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "mark secrets.txt -diff");

            var auditor = new BetterleaksAuditor();
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
            Assert.Equal("secrets.txt:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Real-binary regression for the stock-prefilter exemption: a secret
    /// committed inside <c>docs/gitleaks.toml</c> — a path the ruleset
    /// drops on name alone — can never be reported, so the default gate
    /// fails closed on the exempted-path family, and with the operator
    /// opt-in the scan runs and reports nothing, proving the exemption is
    /// real.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_NestedGitleaksTomlPath_EvadesScan_GateFailsClosed()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(null);
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            await File.WriteAllTextAsync(
                Path.Combine(repo, "docs", "gitleaks.toml"),
                "# example config\n" + _fixtureSecretLine + "\n");
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "add nested gitleaks-named file");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var auditor = new BetterleaksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains("docs/gitleaks.toml", ex.Message, StringComparison.Ordinal);

            var trusting = new BetterleaksAuditor();
            await trusting.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:" + BetterleaksAuditor.TrustRepositorySuppressionKey] = "true",
                }),
                CancellationToken.None);
            var trusted = await ((IAuditor)trusting).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            // The trusted scan runs clean and reports nothing — the file
            // was skipped on its name alone, proving the blind spot the
            // gate guards is real.
            Assert.True(trusted.Passed);
            Assert.Empty(trusted.Findings);
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
            s => s.PluginId == BetterleaksAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("betterleaks", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresBetterleaksRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [BetterleaksAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == BetterleaksAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("betterleaks", tool.Binary);
        // Verify-only by design: the tool is not apt-installable and no
        // distro package can carry the version pin, so the baseline verifies
        // presence and the operator provisions the pinned release. No apt
        // line is emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("betterleaks", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.BetterleaksAuditorPlugin.dll");
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
            PluginId: BetterleaksAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Betterleaks Secrets",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, BetterleaksAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsSuppressionProbe(SandboxExec exec)
        => IsWorktreeSuppressionProbe(exec)
            || IsPathGlobProbe(exec)
            || IsHistorySuppressionProbe(exec);

    private static bool IsWorktreeSuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv.Contains(".betterleaks.toml", StringComparer.Ordinal);

    // The any-depth worktree probe rides a find -path script with the
    // declared glob as its only operand.
    private static bool IsPathGlobProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv.Contains("*gitleaks.toml*", StringComparer.Ordinal);

    private static bool IsHistorySuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 2
            && exec.Argv[0] == "git"
            && exec.Argv.Any(static a => a.Contains("gitleaks.toml", StringComparison.Ordinal));

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count == 5
            && exec.Argv[0] == "realpath"
            && exec.Argv[1] == "-m";

    // Emulates `realpath -m -- <arg> .`: one line for the canonicalized
    // configured path, one for the canonicalized exec working directory.
    // insideWorktree maps a relative operand under the /work scan root.
    private static SandboxExecResult RealpathResult(SandboxExec exec, bool insideWorktree)
    {
        var arg = exec.Argv[3];
        var canonical = insideWorktree && !arg.StartsWith("/", StringComparison.Ordinal)
            ? "/work/" + arg
            : arg;
        return new SandboxExecResult(0, canonical + "\n/work\n", "");
    }

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "betterleaks" && exec.Argv[1] == "version";

    private static async Task<string> SeedFixtureRepoAsync(string? secretLine)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-betterleaks-fixture-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string? ProbeInstalledBetterleaksVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "betterleaks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
            using var process = Process.Start(psi)!;
            // Drain both pipes concurrently and bound the wait BEFORE reading
            // the buffered output: a synchronous ReadToEnd blocks until the
            // child closes the pipe, so a hung `betterleaks version` — or one
            // blocked writing to a full stderr pipe — would never reach the
            // timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var version = stdoutTask.GetAwaiter().GetResult().Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch
        {
            // Any failure means no usable betterleaks on PATH — the gated tests
            // skip rather than fail on a host without the tool.
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
