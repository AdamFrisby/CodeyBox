using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.GitleaksAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
    // Mirrors what gitleaks v8.30.1 writes to stdout for `--report-format sarif
    // --report-path -`: no per-result "level" (the shared parser supplies
    // "warning"), ruleId, message text naming rule/file/commit, and the first
    // physical location's artifact uri plus region.startLine. The driver
    // semanticVersion is upstream's hardcoded "v8.0.0" — intentionally not the
    // release version, which is why the plugin probes `gitleaks version`.
    private const string SarifWithSecret = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "gitleaks",
                "semanticVersion": "v8.0.0",
                "informationUri": "https://github.com/gitleaks/gitleaks",
                "rules": [{ "id": "generic-api-key", "shortDescription": { "text": "Generic API Key" } }]
              }
            },
            "results": [{
              "message": { "text": "generic-api-key has detected secret for file src/config.py at commit 0123456789abcdef." },
              "ruleId": "generic-api-key",
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "src/config.py" },
                  "region": { "startLine": 12, "startColumn": 15, "endLine": 12, "endColumn": 50, "snippet": { "text": "***" } }
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
            "tool": { "driver": { "name": "gitleaks", "semanticVersion": "v8.0.0", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGitleaks_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
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
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
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
    }

    [Fact]
    public async Task RepoGitleaksIgnore_FailsClosed_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, ".gitleaksignore\n", "")); // file present
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
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsWorktreeSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, ".gitleaks.toml\n", "")); // file present
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
        // The config-path exemption covers every commit: a .gitleaks.toml
        // committed and then deleted still hides any secret it contained, so
        // the gate checks git history, not just the worktree.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsWorktreeSuppressionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsHistorySuppressionProbe(exec))
                return Task.FromResult(
                    new SandboxExecResult(0, "0123456789abcdef0123456789abcdef01234567\n", ""));
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
    public async Task TrustedRepositorySuppression_OptsIn_ScanRunsWithoutGuards()
    {
        var auditor = new GitleaksAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + GitleaksAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        var suppressionProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
            {
                suppressionProbes++;
                return Task.FromResult(new SandboxExecResult(1, "", "")); // file present
            }
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
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
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
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
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, SarifWithSecret),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // gitleaks's fatal/error exit is 1 — shared with the *default* findings
        // code, which is exactly why the plugin moves findings to 4. Even with
        // parseable SARIF on stdout, exit 1 means "could not run".
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, SarifWithSecret), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, "usage: gitleaks ..."), "/work", FakeContext(), CancellationToken.None));
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
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, noteSarif),
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
                return Task.FromResult(new SandboxExecResult(0, "8.16.0\n", ""));
            if (IsSuppressionProbe(exec))
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
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "8.26.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
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
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsSuppressionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
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
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = GitleaksAuditor.DefaultExpectedVersion,
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "generic-api-key",
            }),
            CancellationToken.None);

        var vendored = SarifWithSecret.Replace("src/config.py", "vendor/pkg/config.py");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("generic-api-key", "other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(GitleaksAuditor.LeaksFoundExitCode, otherRule),
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

    private static readonly string? _installedGitleaksVersion = ProbeInstalledGitleaksVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture git repository with a committed
    /// secret is scanned by the actual gitleaks through a real process exec —
    /// exercising argv construction, the pinned-ruleset environment, the
    /// findings exit code, and SARIF parsing together, so a broken real
    /// invocation (e.g. an unsupported flag) cannot stay green. Runs only
    /// where a gitleaks binary is on PATH; the auditor's version pin is set to
    /// the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_SecretInSourceAndHistory_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedGitleaksVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(_fixtureSecretLine);
        try
        {
            var auditor = new GitleaksAuditor();
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
    [Fact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedGitleaksVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(null);
        try
        {
            var auditor = new GitleaksAuditor();
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
    /// Real-binary check for the config-path exemption: a secret committed
    /// inside <c>.gitleaks.toml</c> and then deleted survives in git history
    /// while gitleaks exempts that path from scanning in every commit. With
    /// the operator opt-in the scan runs and reports nothing — proving the
    /// exemption is real — and by default the gate fails closed instead.
    /// </summary>
    [Fact]
    [Trait("requires_gitleaks", "true")]
    public async Task RealGitleaks_GitleaksTomlDeletedFromHistory_EvadesScan_GateFailsClosed()
    {
        var installed = _installedGitleaksVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(null);
        try
        {
            var tomlPath = Path.Combine(repo, ".gitleaks.toml");
            await File.WriteAllTextAsync(
                tomlPath, "[extend]\nuseDefault = true\n\n# " + _fixtureSecretLine + "\n");
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "add gitleaks config");
            File.Delete(tomlPath);
            await TestSupport.RunGit(repo, "add", "-A");
            await TestSupport.RunGit(repo, "commit", "-m", "drop gitleaks config");

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
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
            Assert.Contains(".gitleaks.toml", ex.Message, StringComparison.Ordinal);

            var trusting = new GitleaksAuditor();
            await trusting.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
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
        var assemblyPath = PluginAssemblyPath();
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

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GitleaksAuditorPlugin.dll");
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
            PluginId: GitleaksAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Gitleaks Secrets",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, GitleaksAuditor.DefaultExpectedVersion + "\n", "")
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
        => IsWorktreeSuppressionProbe(exec) || IsHistorySuppressionProbe(exec);

    private static bool IsWorktreeSuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv.Contains(".gitleaksignore", StringComparer.Ordinal)
            && exec.Argv.Contains(".gitleaks.toml", StringComparer.Ordinal);

    private static bool IsHistorySuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 2
            && exec.Argv[0] == "git"
            && exec.Argv.Contains(".gitleaks.toml", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "gitleaks" && exec.Argv[1] == "version";

    private static async Task<string> SeedFixtureRepoAsync(string? secretLine)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-gitleaks-fixture-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string? ProbeInstalledGitleaksVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gitleaks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var version = stdout.Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch
        {
            // Any failure means no usable gitleaks on PATH — the gated tests
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
