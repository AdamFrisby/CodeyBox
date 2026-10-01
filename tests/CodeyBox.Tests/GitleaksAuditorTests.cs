using CodeyBox.Core;
using CodeyBox.GitleaksAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static CodeyBox.Tests.SecretsAuditorTestSupport;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the gitleaks auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), the findings exit code the
/// plugin assigns is a verdict while gitleaks's error exits are not, SARIF
/// maps to findings with rule id and file/line, severity goes through the
/// declared mapping rather than passing through, repository-controlled
/// suppression surfaces are neutralized unless the operator opts in, and the
/// plugin is inert — unloaded and absent from baseline provisioning — until
/// an operator enables it. Every run is dispatched through <see cref="IAuditor"/>
/// so the version-pin precondition cannot be bypassed by interface dispatch.
/// </summary>
public sealed class GitleaksAuditorTests
{
    // The per-tool constants the shared secrets-auditor scaffolding varies
    // by — the probe argv classifiers key off these names.
    private static readonly SecretsAuditorTestProfile Profile = new(
        Tool: "gitleaks",
        PluginId: GitleaksAuditor.PluginId,
        PluginDisplayName: "CodeyBox: Gitleaks Secrets",
        PluginAssemblyFileName: "CodeyBox.GitleaksAuditorPlugin.dll",
        ExpectedVersion: GitleaksAuditor.DefaultExpectedVersion,
        WorktreeSuppressionFiles: [".gitleaksignore", ".gitleaks.toml"]);

    // Mirrors what gitleaks v8.30.1 writes to stdout for `--report-format
    // sarif --report-path -` — the shared builder emits the shape both
    // tools stamp (see SecretsAuditorTestSupport.SarifWithFinding).
    private static readonly string SarifWithSecret = SarifWithFinding(
        toolName: "gitleaks",
        informationUri: "https://github.com/gitleaks/gitleaks",
        ruleId: "generic-api-key",
        ruleTitle: "Generic API Key",
        file: "src/config.py",
        startLine: 12);

    private static readonly string SarifClean = SarifCleanReport("gitleaks");

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGitleaks_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(127, "", "gitleaks: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GitleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("gitleaks", ex.Message, StringComparison.Ordinal);
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
            return Task.FromResult(new SandboxExecResult(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        IAuditor auditor = new GitleaksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:gitleaks", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("generic-api-key", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/config.py:12", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("gitleaks", argv[0]);
        Assert.Equal("git", argv[1]);
        Assert.Contains("--report-format", argv);
        Assert.Contains("sarif", argv);
        Assert.Contains("--report-path", argv);
        Assert.Contains("-", argv);
        Assert.Contains("--redact=100", argv);
        var exitFlag = argv.ToList().IndexOf("--exit-code");
        Assert.True(exitFlag >= 0 && exitFlag + 1 < argv.Count);
        Assert.Equal(
            GitleaksAuditor.LeaksFoundExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            argv[exitFlag + 1]);

        // Repository-controlled suppression is neutralized by default: inline
        // gitleaks:allow comments disabled, the ignore-file flag pointed away
        // from the repo, and the built-in ruleset pinned via env so a repo
        // .gitleaks.toml cannot extend rules or add allowlists.
        Assert.Contains("--ignore-gitleaks-allow", argv);
        var ignoreFlag = argv.ToList().IndexOf("--gitleaks-ignore-path");
        Assert.True(ignoreFlag >= 0 && ignoreFlag + 1 < argv.Count);
        Assert.StartsWith("/", argv[ignoreFlag + 1], StringComparison.Ordinal);
        Assert.NotNull(scanExec.ExtraEnvironment);
        Assert.Contains(
            "useDefault",
            Assert.Contains("GITLEAKS_CONFIG_TOML", scanExec.ExtraEnvironment),
            StringComparison.Ordinal);

        // The precedence-2 config-PATH env var is unset on the scan: a
        // baseline-exported GITLEAKS_CONFIG would otherwise outrank the
        // pinned ruleset with a path the canonicalization guard never sees.
        Assert.Contains("GITLEAKS_CONFIG", scanExec.EnvironmentVariablesToUnset);

        // Ambient GIT_* variables are unset on the scan too: they would
        // redirect or re-configure the `git log` the tool spawns (and the
        // gate's own history probe).
        Assert.Contains("GIT_DIR", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_CONFIG_PARAMETERS", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_LITERAL_PATHSPECS", scanExec.EnvironmentVariablesToUnset);

        // .gitattributes countermeasure: the pinned log opts re-state
        // gitleaks's default rev args (--log-opts replaces them wholesale)
        // and add --text, so a committed `path -diff`/`binary` attribute
        // cannot blank the patch stream git log feeds the scanner.
        var logOpts = argv.ToList().IndexOf("--log-opts");
        Assert.True(logOpts >= 0 && logOpts + 1 < argv.Count);
        Assert.Equal("--full-history --all --diff-filter=tuxdb --text", argv[logOpts + 1]);
    }

    [Fact]
    public async Task RepoGitleaksIgnore_FailsClosed_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
            if (IsWorktreeSuppressionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, ".gitleaksignore\n", "")); // file present
            if (IsHistorySuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GitleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(".gitleaksignore", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            GitleaksAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepoGitleaksToml_FailsClosed_ScanNeverRuns()
    {
        // gitleaks exempts its own config path from the scan, so a
        // repo-root .gitleaks.toml is gated exactly like .gitleaksignore.
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
            if (IsWorktreeSuppressionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, ".gitleaks.toml\n", "")); // file present
            if (IsHistorySuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GitleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(".gitleaks.toml", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            GitleaksAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepoGitleaksTomlInHistory_FailsClosed_ScanNeverRuns()
    {
        // The exemption covers every commit: a *gitleaks.toml* path
        // committed and then deleted still hides any secret it contained,
        // so the gate checks git history, not just the worktree.
        var scanExecs = 0;
        SandboxExec? historyProbe = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile) || IsWorktreeSuppressionProbe(exec, Profile)
                || IsPathGlobProbe(exec))
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

        IAuditor auditor = new GitleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("*gitleaks.toml*", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            GitleaksAuditor.TrustRepositorySuppressionKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);

        // The history probe anchors at the repository root with git's
        // DEFAULT pathspec semantics (`*`/`?` cross '/') — `glob` magic's
        // FNM_PATHNAME matching would miss nested paths like
        // docs/gitleaks.toml — and walks --full-history so a commit hidden
        // behind a TREESAME merge still trips the gate.
        Assert.Equal(":(top)*gitleaks.toml*", historyProbe!.Argv[^1]);
        Assert.Contains("--full-history", historyProbe.Argv);
        Assert.Contains("--all", historyProbe.Argv);
        Assert.Contains("GIT_DIR", historyProbe.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_GLOB_PATHSPECS", historyProbe.EnvironmentVariablesToUnset);
    }

    // The exemption is keyed on the NAME, not the location: a nested
    // docs/gitleaks.toml is just as invisible to the scanner as the root
    // file, so the worktree gate must catch it at any depth.
    [Fact]
    public async Task NestedGitleaksTomlPath_InWorktree_FailsClosed_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile)
                || IsWorktreeSuppressionProbe(exec, Profile) || IsHistorySuppressionProbe(exec))
                return Task.FromResult(Ok(exec, Profile));
            if (IsPathGlobProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./docs/gitleaks.toml\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GitleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("docs/gitleaks.toml", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task TrustedRepositorySuppression_OptsIn_ScanRunsWithoutGuards()
    {
        var auditor = new GitleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:" + GitleaksAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        var suppressionProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec, Profile))
            {
                suppressionProbes++;
                return Task.FromResult(new SandboxExecResult(0, ".gitleaksignore\n", "")); // file present
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec, Profile))
                return Task.FromResult(Ok(exec, Profile));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.Equal(0, suppressionProbes);
        Assert.DoesNotContain("--ignore-gitleaks-allow", scanExec!.Argv);
        Assert.False(scanExec.ExtraEnvironment?.ContainsKey("GITLEAKS_CONFIG_TOML") ?? false);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(Profile, scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new GitleaksAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsExit_IsVerdict_WhileErrorExits_AreInfrastructure()
    {
        // The plugin-assigned findings exit produces a verdict.
        IAuditor auditor = new GitleaksAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(Profile, GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // gitleaks's fatal/error exit is 1 — shared with the *default* findings
        // code, which is exactly why the plugin moves findings to 4. Even with
        // parseable SARIF on stdout, exit 1 means "could not run".
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(Profile, 1, SarifWithSecret), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(Profile, 2, "usage: gitleaks ..."), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // A fabricated "note" level would map to Info under the shared default
        // map; this auditor declares every gitleaks result a blocking Error.
        var noteSarif = SarifWithSecret.Replace(
            "\"ruleId\": \"generic-api-key\"",
            "\"level\": \"note\", \"ruleId\": \"generic-api-key\"",
            StringComparison.Ordinal);
        IAuditor auditor = new GitleaksAuditor();
        var result = await auditor.RunAsync(
            HealthyTool(Profile, GitleaksAuditor.LeaksFoundExitCode, noteSarif),
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
                return Task.FromResult(new SandboxExecResult(0, "8.16.0\n", ""));
            if (IsSuppressionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GitleaksAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("8.16.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GitleaksAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new GitleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(Profile, new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "8.26.0" }),
            CancellationToken.None);

        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec, Profile))
                return Task.FromResult(new SandboxExecResult(0, "8.26.0\n", ""));
            return Task.FromResult(new SandboxExecResult(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret, ""));
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

        IAuditor auditor = new GitleaksAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new GitleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(Profile, new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = GitleaksAuditor.DefaultExpectedVersion,
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "generic-api-key",
            }),
            CancellationToken.None);

        var vendored = SarifWithSecret.Replace("src/config.py", "vendor/pkg/config.py");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(Profile, GitleaksAuditor.LeaksFoundExitCode, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("generic-api-key", "other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(Profile, GitleaksAuditor.LeaksFoundExitCode, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Synthetic token in the slack-bot-token rule's shape — not a real
    // credential; it exists so the pinned default ruleset produces exactly
    // one finding at a known location. Assembled at runtime so this test file
    // does not itself carry a scanner-detectable literal.
    private static readonly string _fixtureSecretLine =
        "token = \"xoxb-" + "123456789012-1234567890123-abcdefghijklmnopqrstuvwx\"";

    private static readonly string? _installedGitleaksVersion = ProbeInstalledToolVersion(Profile);

    /// <summary>
    /// Real-binary end-to-end check: a fixture git repository with a committed
    /// secret is scanned by the actual gitleaks through a real process exec —
    /// exercising argv construction, the pinned-ruleset environment, the
    /// findings exit code, and SARIF parsing together, so a broken real
    /// invocation (e.g. an unsupported flag) cannot stay green. Runs only
    /// where a gitleaks binary is on PATH; the auditor's version pin is set to
    /// the installed release.
    /// </summary>
    [SkippableFact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_SecretInSourceAndHistory_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedGitleaksVersion;
        Skip.If(installed is null, "gitleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, _fixtureSecretLine);
        try
        {
            var auditor = new GitleaksAuditor();
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
            Assert.Equal("codeybox:gitleaks", finding.AuditorName);
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
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedGitleaksVersion;
        Skip.If(installed is null, "gitleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, null);
        try
        {
            var auditor = new GitleaksAuditor();
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
    /// Real-binary check for the config-path exemption: a secret committed
    /// inside <c>.gitleaks.toml</c> and then deleted survives in git history
    /// while gitleaks exempts that path from scanning in every commit. With
    /// the operator opt-in the scan runs and reports nothing — proving the
    /// exemption is real — and by default the gate fails closed instead.
    /// </summary>
    [SkippableFact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_GitleaksTomlDeletedFromHistory_EvadesScan_GateFailsClosed()
    {
        var installed = _installedGitleaksVersion;
        Skip.If(installed is null, "gitleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, null);
        try
        {
            await CommitThenDeleteAsync(
                repo, ".gitleaks.toml", "[extend]\nuseDefault = true\n\n# " + _fixtureSecretLine + "\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var auditor = new GitleaksAuditor();
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

            var trusting = new GitleaksAuditor();
            await trusting.InitializeAsync(
                BuildPluginContext(Profile, new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:" + GitleaksAuditor.TrustRepositorySuppressionKey] = "true",
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
    /// Real-binary regression for the history gate's pathspec: a secret
    /// committed inside <c>docs/gitleaks.toml</c> and then DELETED exists
    /// only in git history, so only the <c>git log</c> probe can catch it —
    /// and only if the pathspec's <c>*</c> crosses directory separators.
    /// With git's <c>glob</c> pathspec magic (FNM_PATHNAME) this fixture
    /// would pass silently. The trusted opt-in scan still reports nothing,
    /// proving the secret was skipped on its path, not absent.
    /// </summary>
    [SkippableFact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_NestedGitleaksTomlDeletedFromHistory_GateFailsClosed()
    {
        var installed = _installedGitleaksVersion;
        Skip.If(installed is null, "gitleaks binary not on PATH");

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

            var auditor = new GitleaksAuditor();
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

            var trusting = new GitleaksAuditor();
            await trusting.InitializeAsync(
                BuildPluginContext(Profile, new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:" + GitleaksAuditor.TrustRepositorySuppressionKey] = "true",
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
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_GitleaksTomlMergeHiddenInHistory_GateFailsClosed()
    {
        var installed = _installedGitleaksVersion;
        Skip.If(installed is null, "gitleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, null);
        try
        {
            await CommitMergeHiddenAsync(
                repo, ".gitleaks.toml", "[extend]\nuseDefault = true\n\n# " + _fixtureSecretLine + "\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = repo }],
                },
                CancellationToken.None);

            var auditor = new GitleaksAuditor();
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

    /// <summary>
    /// Real-binary regression for the <c>.gitattributes</c> suppression
    /// channel: marking a secret-bearing path <c>-diff</c> makes
    /// <c>git log -p</c> emit "Binary files differ" with no patch content,
    /// silently blanking the scanner's input. The pinned
    /// <c>--log-opts … --text</c> must still surface the finding.
    /// </summary>
    [SkippableFact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_SecretMarkedDiffSuppressed_StillYieldsFinding()
    {
        var installed = _installedGitleaksVersion;
        Skip.If(installed is null, "gitleaks binary not on PATH");

        var repo = await SeedFixtureRepoAsync(Profile, _fixtureSecretLine);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(repo, ".gitattributes"), "secrets.txt -diff\n");
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "mark secrets.txt -diff");

            var auditor = new GitleaksAuditor();
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

    // An operator file flag whose value resolves inside the audited worktree
    // hands gate-shaping content (config ruleset, baseline, ignore
    // fingerprints) to repo-controlled bytes — the flag resolves against the
    // tool's cwd — so it fails closed before the scan runs.
    [Theory]
    [InlineData("--config,ops/rules.toml")]
    [InlineData("--config=ops/rules.toml")]
    // pflag shorthand bundling: "-vc" binds the NEXT token to -c, so the
    // guard must see through the cluster or "ops/rules.toml" escapes the
    // canonicalization check.
    [InlineData("-vc,ops/rules.toml")]
    [InlineData("--baseline-path,ops/baseline.json")]
    public async Task ExtraArgumentsFileFlag_ResolvingInsideWorktree_FailsClosed(string extraArguments)
    {
        var auditor = new GitleaksAuditor();
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
            s => s.PluginId == GitleaksAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("gitleaks", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresGitleaksRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath(Profile);
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [GitleaksAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GitleaksAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("gitleaks", tool.Binary);
        // Verify-only by design: the distro package cannot carry the version
        // pin, so the baseline verifies presence and the operator provisions
        // the pinned release. No apt line is emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("gitleaks", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }
}
