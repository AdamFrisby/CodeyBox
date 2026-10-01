using CodeyBox.BetterleaksAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static CodeyBox.Tests.SecretsAuditorTestSupport;

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
    // The per-tool constants the shared secrets-auditor scaffolding varies
    // by — the probe classifiers key off these names.
    private static readonly SecretsAuditorTestProfile Profile = new(
        Tool: "betterleaks",
        PluginId: BetterleaksAuditor.PluginId,
        PluginDisplayName: "CodeyBox: Betterleaks Secrets",
        PluginAssemblyFileName: "CodeyBox.BetterleaksAuditorPlugin.dll",
        ExpectedVersion: BetterleaksAuditor.DefaultExpectedVersion,
        WorktreeSuppressionFiles:
        [
            ".betterleaksignore",
            ".gitleaksignore",
            ".betterleaks.toml",
            ".gitleaks.toml",
        ]);

    // Mirrors what betterleaks v1.8.1 writes to stdout for `--report-format
    // sarif --report-path -` — the shared builder emits the shape both
    // tools stamp (see SecretsAuditorTestSupport.SarifWithFinding).
    private static readonly string SarifWithSecret = SarifWithFinding(
        toolName: "betterleaks",
        informationUri: "https://github.com/betterleaks/betterleaks",
        ruleId: "slack-bot-token",
        ruleTitle: "Slack Bot token",
        file: "secrets.txt",
        startLine: 1);

    private static readonly string SarifClean = SarifCleanReport("betterleaks");

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingBetterleaks_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec, Profile))
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
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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

        // Ambient GIT_* variables are unset on the scan too: they would
        // redirect or re-configure the `git log` the tool spawns (and the
        // gate's own history probe).
        Assert.Contains("GIT_DIR", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_CONFIG_PARAMETERS", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_LITERAL_PATHSPECS", scanExec.EnvironmentVariablesToUnset);

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
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
            if (IsSuppressionProbe(exec, Profile))
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
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile)
                || IsWorktreeSuppressionProbe(exec, Profile) || IsHistorySuppressionProbe(exec))
                return Task.FromResult(Ok(exec, Profile));
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
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile)
                || IsWorktreeSuppressionProbe(exec, Profile) || IsPathGlobProbe(exec))
                return Task.FromResult(Ok(exec, Profile));
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
        // The history probe anchors at the repository root with git's
        // DEFAULT pathspec semantics (`*`/`?` cross '/') — `glob` magic's
        // FNM_PATHNAME matching would miss nested paths like
        // docs/gitleaks.toml — and walks --full-history so a commit hidden
        // behind a TREESAME merge still trips the gate.
        Assert.Equal(
            ":(top)*gitleaks.toml*",
            historyProbe!.Argv[^1]);
        Assert.Contains("--full-history", historyProbe.Argv);
        Assert.Contains("--all", historyProbe.Argv);
        // Ambient GIT_* variables are stripped from the probe — e.g.
        // GIT_GLOB_PATHSPECS would re-impose FNM_PATHNAME semantics and
        // GIT_DIR would redirect the repository.
        Assert.Contains("GIT_DIR", historyProbe.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_GLOB_PATHSPECS", historyProbe.EnvironmentVariablesToUnset);
    }

    // A failed history probe can never substitute for the gate: a non-zero
    // git log exit is infrastructure, not evidence the family is absent.
    [Fact]
    public async Task RulesetExemptedPath_HistoryProbeError_IsInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile)
                || IsWorktreeSuppressionProbe(exec, Profile) || IsPathGlobProbe(exec))
                return Task.FromResult(Ok(exec, Profile));
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
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:" + BetterleaksAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        var suppressionProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec, Profile))
            {
                suppressionProbes++;
                return Task.FromResult(new SandboxExecResult(0, ".betterleaksignore\n", ""));
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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
        var sandbox = HealthyTool(Profile, scanExit: 0, scanStdout: SarifClean);
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
            HealthyTool(Profile, BetterleaksAuditor.LeaksFoundExitCode, SarifWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // betterleaks's fatal/error exit is 1 — shared with the *default*
        // findings code, which is exactly why the plugin moves findings to 4.
        // Even with parseable SARIF on stdout, exit 1 means "could not run".
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(Profile, 1, SarifWithSecret), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(Profile, 2, "usage: betterleaks ..."), "/work", FakeContext(), CancellationToken.None));
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
            HealthyTool(Profile, BetterleaksAuditor.LeaksFoundExitCode, leveledSarif),
            "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, "1.7.0\n", ""));
            if (IsSuppressionProbe(exec, Profile))
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
            BuildPluginContext(Profile, new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "1.7.4" }),
            CancellationToken.None);

        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec, Profile))
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
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec, Profile))
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
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = BetterleaksAuditor.DefaultExpectedVersion,
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "slack-bot-token",
            }),
            CancellationToken.None);

        var vendored = SarifWithSecret.Replace("secrets.txt", "vendor/pkg/secrets.txt");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(Profile, BetterleaksAuditor.LeaksFoundExitCode, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("slack-bot-token", "other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(Profile, BetterleaksAuditor.LeaksFoundExitCode, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    [Fact]
    public async Task MissingGitBinary_IsInfrastructure_NamingGit()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
            {
                // betterleaks itself exists; the git binary `git` mode shells
                // out to does not — the failure names the missing piece.
                var binary = exec.Argv[^1];
                return Task.FromResult(new SandboxExecResult(
                    string.Equals(binary, "git", StringComparison.Ordinal) ? 1 : 0, "", ""));
            }
            if (IsVersionProbe(exec, Profile) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--config,/etc/betterleaks/rules.toml",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-vc,/etc/betterleaks/rules.toml",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--config",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
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

    private static readonly string? _installedBetterleaksVersion = ProbeInstalledToolVersion(Profile);

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

        var repo = await SeedFixtureRepoAsync(Profile, _fixtureSecretLine);
        try
        {
            var auditor = new BetterleaksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(Profile, new Dictionary<string, string?>
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

        var repo = await SeedFixtureRepoAsync(Profile, null);
        try
        {
            var auditor = new BetterleaksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(Profile, new Dictionary<string, string?>
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

        var repo = await SeedFixtureRepoAsync(Profile, _fixtureSecretLine);
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
                BuildPluginContext(Profile, new Dictionary<string, string?>
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
                BuildPluginContext(Profile, new Dictionary<string, string?>
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

        var repo = await SeedFixtureRepoAsync(Profile, _fixtureSecretLine);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repo, ".gitattributes"), "secrets.txt -diff\n");
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "mark secrets.txt -diff");

            var auditor = new BetterleaksAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(Profile, new Dictionary<string, string?>
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

        var repo = await SeedFixtureRepoAsync(Profile, null);
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
                BuildPluginContext(Profile, new Dictionary<string, string?>
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
                BuildPluginContext(Profile, new Dictionary<string, string?>
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

    /// <summary>
    /// Real-binary regression for the history gate's pathspec: a secret
    /// committed inside <c>docs/gitleaks.toml</c> and then DELETED exists
    /// only in git history, so only the <c>git log</c> probe can catch it —
    /// and only if the pathspec's <c>*</c> crosses directory separators.
    /// With git's <c>glob</c> pathspec magic (FNM_PATHNAME) this fixture
    /// would pass silently. The trusted opt-in scan still reports nothing,
    /// proving the secret was skipped on its path, not absent.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_NestedGitleaksTomlDeletedFromHistory_GateFailsClosed()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, null);
        try
        {
            await CommitThenDeleteAsync(
                repo, "docs/gitleaks.toml", "# example config\n" + _fixtureSecretLine + "\n");

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
                BuildPluginContext(Profile, new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains("*gitleaks.toml*", ex.Message, StringComparison.Ordinal);

            var trusting = new BetterleaksAuditor();
            await trusting.InitializeAsync(
                BuildPluginContext(Profile, new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:" + BetterleaksAuditor.TrustRepositorySuppressionKey] = "true",
                }),
                CancellationToken.None);
            var trusted = await ((IAuditor)trusting).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(trusted.Passed);
            Assert.Empty(trusted.Findings);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Real-binary regression for the history gate's
    /// <c>--full-history</c> traversal: a <c>.gitleaks.toml</c> carrying a
    /// secret committed on a side branch and merged with <c>-s ours</c> is
    /// TREESAME to the mainline parent for that path, so default history
    /// simplification prunes it — the commit is invisible to
    /// <c>git log --all</c> without <c>--full-history</c> while the
    /// scanner's own pinned log-opts still walk it.
    /// </summary>
    [SkippableFact]
    [Trait("requires_betterleaks", "true")]
    public async Task RealBetterleaks_GitleaksTomlMergeHiddenInHistory_GateFailsClosed()
    {
        var installed = _installedBetterleaksVersion;
        Skip.If(installed is null, "betterleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, null);
        try
        {
            await CommitMergeHiddenAsync(
                repo, ".gitleaks.toml", "# example config\n" + _fixtureSecretLine + "\n");

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
                BuildPluginContext(Profile, new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains("*gitleaks.toml*", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolAbsentFromBaselineProvisioning()
    {
        var assemblyPath = PluginAssemblyPath(Profile);
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
        var assemblyPath = PluginAssemblyPath(Profile);
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
}
