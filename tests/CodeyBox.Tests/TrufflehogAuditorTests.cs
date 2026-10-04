using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.TrufflehogAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the trufflehog auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), the findings exit code the
/// plugin assigns (183 via <c>--fail</c>) is a verdict while the tool's error
/// exits are not, SARIF maps to findings with detector id and file/line,
/// verified findings block while unverified ones are advisory through the
/// declared severity mapping, and the plugin is inert — unloaded and absent
/// from baseline provisioning — until an operator enables it. Every run is
/// dispatched through <see cref="IAuditor"/> so the version-pin precondition
/// cannot be bypassed by interface dispatch.
/// </summary>
public sealed class TrufflehogAuditorTests
{
    private const string Tool = "trufflehog";
    private const string PluginAssemblyFileName = "CodeyBox.TrufflehogAuditorPlugin.dll";

    // Mirrors what trufflehog 3.97.9 writes to stdout for `git file://. --sarif`:
    // one SARIF document, ruleId = detector name, level error for a verified
    // (live) credential and warning otherwise, artifact uri plus startLine.
    private static string SarifWithFinding(string level, string ruleId, string message, string file, int line)
        => $$"""
            {
              "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
              "version": "2.1.0",
              "runs": [{
                "tool": {
                  "driver": {
                    "name": "trufflehog",
                    "semanticVersion": "3.97.9",
                    "informationUri": "https://github.com/trufflesecurity/trufflehog",
                    "rules": [{ "id": "{{ruleId}}", "shortDescription": { "text": "{{ruleId}} detector" } }]
                  }
                },
                "results": [{
                  "ruleId": "{{ruleId}}",
                  "level": "{{level}}",
                  "message": { "text": "{{message}}" },
                  "locations": [{
                    "physicalLocation": {
                      "artifactLocation": { "uri": "{{file}}" },
                      "region": { "startLine": {{line}} }
                    }
                  }],
                  "partialFingerprints": { "trufflehogFingerprint/v1": "0123456789abcdef" }
                }]
              }]
            }
            """;

    private static readonly string SarifVerified = SarifWithFinding(
        "error", "PrivateKey", "Found verified result for detector PrivateKey.", "deploy_key", 1);

    private static readonly string SarifUnverified = SarifWithFinding(
        "warning", "PrivateKey", "Found unverified result for detector PrivateKey.", "deploy_key", 1);

    private static readonly string SarifClean =
        """{"version": "2.1.0", "runs": [{"tool": {"driver": {"name": "trufflehog"}}, "results": []}]}""";

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTrufflehog_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "trufflehog: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrufflehogAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("trufflehog", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VerifiedSecret_YieldsErrorFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(RealpathResult(exec, insideWorktree: false));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(183, SarifVerified, ""));
        });

        IAuditor auditor = new TrufflehogAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // A verified (live) credential blocks the audit.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:trufflehog", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("PrivateKey", finding.Title, StringComparison.Ordinal);
        Assert.Equal("deploy_key:1", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("trufflehog", argv[0]);
        Assert.Equal("git", argv[1]);
        Assert.Equal("file://.", argv[2]);
        Assert.Contains("--sarif", argv);
        Assert.Contains("--fail", argv);
        Assert.Contains("--no-update", argv);
        Assert.Contains("--results=verified,unverified,unknown", argv);
        Assert.Contains("--no-color", argv);

        // Repository-controlled suppression is neutralized by default:
        // trufflehog:ignore comments are not honored unless the operator
        // opts in.
        Assert.Contains("--no-ignore-tag", argv);

        // Ambient GIT_* variables are unset on the scan: they would
        // redirect or re-configure the clone the git source performs.
        Assert.Contains("GIT_DIR", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_CONFIG_PARAMETERS", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("GIT_LITERAL_PATHSPECS", scanExec.EnvironmentVariablesToUnset);
    }

    [Fact]
    public async Task UnverifiedSecret_YieldsWarningFinding_DoesNotFailAudit()
    {
        var sandbox = HealthyTool(scanExit: 183, scanStdout: SarifUnverified);
        IAuditor auditor = new TrufflehogAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // An unconfirmed detection is advisory: reported, but not blocking.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("PrivateKey", finding.Title, StringComparison.Ordinal);
        Assert.Equal("deploy_key:1", finding.Location);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new TrufflehogAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsExit_IsVerdict_WhileErrorExits_AreInfrastructure()
    {
        // The plugin-assigned findings exit (via --fail) produces a verdict.
        IAuditor auditor = new TrufflehogAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(183, SarifVerified), "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // A clean run shares exit 0 with every scanner that does not take
        // --fail; here 0 plus an empty report is a pass.
        var clean = await auditor.RunAsync(
            HealthyTool(0, SarifClean), "/work", FakeContext(), CancellationToken.None);
        Assert.True(clean.Passed);

        // Exit 1 is trufflehog's scan/config error — "could not run", even
        // with a parseable SARIF document on stdout.
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, SarifVerified), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 1", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, "usage: trufflehog ..."), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // Verified maps to Error (blocking), unverified to Warning
        // (advisory) — the tool's verification vocabulary, not raw levels.
        IAuditor auditor = new TrufflehogAuditor();
        var verified = await auditor.RunAsync(
            HealthyTool(183, SarifVerified), "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(verified.Findings).Severity);

        var unverified = await auditor.RunAsync(
            HealthyTool(183, SarifUnverified), "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unverified.Findings).Severity);

        // A level outside the tool's vocabulary falls back to the declared
        // default rather than passing through as a new severity.
        var noteSarif = SarifWithFinding(
            "note", "PrivateKey", "Found unverified result for detector PrivateKey.", "deploy_key", 1);
        var note = await auditor.RunAsync(
            HealthyTool(183, noteSarif), "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(note.Findings).Severity);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "trufflehog 3.90.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrufflehogAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("3.90.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(TrufflehogAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new TrufflehogAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "3.90.0" }),
            CancellationToken.None);

        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "trufflehog 3.90.0\n", ""));
            return Task.FromResult(new SandboxExecResult(183, SarifVerified, ""));
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
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "dev-build\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrufflehogAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new TrufflehogAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = TrufflehogAuditor.DefaultExpectedVersion,
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "PrivateKey",
            }),
            CancellationToken.None);

        var vendored = SarifVerified.Replace("deploy_key", "vendor/pkg/deploy_key");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(183, vendored), "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace("PrivateKey", "OtherDetector", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(183, otherRule), "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    [Fact]
    public async Task MinimumSeverityError_ReportsOnlyLiveCredentials()
    {
        var auditor = new TrufflehogAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:MinimumSeverity"] = "error" }),
            CancellationToken.None);

        var warnings = await ((IAuditor)auditor).RunAsync(
            HealthyTool(183, SarifUnverified), "/work", FakeContext(), CancellationToken.None);
        Assert.True(warnings.Passed);
        Assert.Empty(warnings.Findings);

        var live = await ((IAuditor)auditor).RunAsync(
            HealthyTool(183, SarifVerified), "/work", FakeContext(), CancellationToken.None);
        Assert.False(live.Passed);
        Assert.Single(live.Findings);
    }

    [Fact]
    public async Task TrustedRepositorySuppression_OptsIn_ScanRunsWithoutIgnoreTagGuard()
    {
        var auditor = new TrufflehogAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:" + TrufflehogAuditor.TrustRepositorySuppressionKey] = "true",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "trufflehog " + TrufflehogAuditor.DefaultExpectedVersion + "\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(183, SarifVerified, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Findings);
        Assert.DoesNotContain("--no-ignore-tag", scanExec!.Argv);
    }

    [Theory]
    [InlineData("--config,ops/rules.toml")]
    [InlineData("--config=ops/rules.toml")]
    [InlineData("--exclude-paths,ops/excludes.txt")]
    [InlineData("--exclude-paths=ops/excludes.txt")]
    [InlineData("-x,ops/excludes.txt")]
    [InlineData("-xops/excludes.txt")]
    public async Task ExtraArgumentsFileFlag_ResolvingInsideWorktree_FailsClosed(string extraArguments)
    {
        var auditor = new TrufflehogAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
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

    [Theory]
    [InlineData("--json")]
    [InlineData("--json-legacy")]
    [InlineData("--github-actions")]
    [InlineData("--no-verification")]
    public async Task ReservedExtraArguments_AreRejected_BeforeAnyScan(string flag)
    {
        var auditor = new TrufflehogAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExtraArguments"] = flag }),
            CancellationToken.None);

        var execs = 0;
        var sandbox = FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains(flag, ex.Message, StringComparison.Ordinal);
        // Deterministic config rejection: no sandbox exec at all, not even
        // the presence probe.
        Assert.Equal(0, execs);
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
            s => s.PluginId == TrufflehogAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("trufflehog", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresTrufflehogRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [TrufflehogAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == TrufflehogAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("trufflehog", tool.Binary);
        // Verify-only by design: the tool is not apt-installable with a
        // version pin, so the baseline verifies presence and the operator
        // provisions the pinned release. No apt line is emitted for it.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("trufflehog", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    /// <summary>
    /// Real-binary end-to-end check: a fixture git repository holding a
    /// freshly generated (random, never-live) private key is scanned by the
    /// actual trufflehog through a real process exec — exercising argv
    /// construction, the findings exit code, and SARIF parsing together, so
    /// a broken real invocation (e.g. an unsupported flag) cannot stay
    /// green. Runs only where a trufflehog binary is on PATH; the auditor's
    /// version pin is set to the installed release. The finding's severity
    /// depends on verification egress (unreachable providers verify
    /// nothing), so the test asserts the finding — rule id and location —
    /// not the verdict.
    /// </summary>
    [SkippableFact]
    [Trait("requires_trufflehog", "true")]
    public async Task RealTrufflehog_SecretInHistory_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = ProbeInstalledToolVersion();
        Skip.If(installed is null, "trufflehog binary not on PATH");

        var repo = await SeedFixtureRepoAsync(withSecret: true);
        try
        {
            var auditor = new TrufflehogAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = installed }),
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

            var finding = Assert.Single(result.Findings);
            Assert.Equal("codeybox:trufflehog", finding.AuditorName);
            Assert.Contains("PrivateKey", finding.Title, StringComparison.Ordinal);
            Assert.Equal("deploy_key:1", finding.Location);
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
    [Trait("requires_trufflehog", "true")]
    public async Task RealTrufflehog_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = ProbeInstalledToolVersion();
        Skip.If(installed is null, "trufflehog binary not on PATH");

        var repo = await SeedFixtureRepoAsync(withSecret: false);
        try
        {
            var auditor = new TrufflehogAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = installed }),
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

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == Tool && exec.Argv[1] == "--version";

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count == 5
            && exec.Argv[0] == "realpath"
            && exec.Argv[1] == "-m";

    // Emulates `realpath -m -- <arg> .`: one line for the canonicalized
    // configured path, one for the canonicalized exec working directory.
    private static SandboxExecResult RealpathResult(SandboxExec exec, bool insideWorktree)
    {
        var arg = exec.Argv[3];
        var canonical = insideWorktree && !arg.StartsWith("/", StringComparison.Ordinal)
            ? "/work/" + arg
            : arg;
        return new SandboxExecResult(0, canonical + "\n/work\n", "");
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, Tool + " " + TrufflehogAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static ISandbox HealthyTool(int scanExit, string scanStdout)
        => FakeSandbox((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRealpathProbe(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    private static ISandbox FakeSandbox(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> onExec)
        => new DelegatingSandbox(onExec);

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: TrufflehogAuditor.PluginId,
            PluginDisplayName: "CodeyBox: TruffleHog Secrets",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, PluginAssemblyFileName);
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    private static async Task<string> SeedFixtureRepoAsync(bool withSecret)
    {
        var repo = Path.Combine(
            Path.GetTempPath(),
            "codeybox-trufflehog-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        if (withSecret)
        {
            // Freshly generated, random, never-live key material: scanners
            // detect the format, providers can never verify it. Generated at
            // runtime so this file carries no scanner-detectable literal.
            var keyPath = Path.Combine(repo, "deploy_key");
            var psi = new ProcessStartInfo
            {
                FileName = "ssh-keygen",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("rsa");
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add("2048");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(keyPath);
            psi.ArgumentList.Add("-N");
            psi.ArgumentList.Add(string.Empty);
            using var sshKeygen = Process.Start(psi);
            Assert.NotNull(sshKeygen);
            await sshKeygen.WaitForExitAsync();
            Assert.Equal(0, sshKeygen.ExitCode);
            File.Delete(keyPath + ".pub");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "clean\n");
        }
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    private static string? ProbeInstalledToolVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Tool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var output = stdoutTask.GetAwaiter().GetResult();
            var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+\.\d+[\w.\-]*");
            return process.ExitCode == 0 && match.Success ? match.Value.TrimEnd('.') : null;
        }
        catch
        {
            // Any failure means no usable binary on PATH — the gated tests
            // skip rather than fail on a host without the tool.
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort fixture cleanup */ }
    }

    private static AuditContext FakeContext()
        => new(WorkItemId.New(), "feature", "main", 1, "do x");

    private sealed class TestPluginHost(IConfigurationSection scoped) : IPluginHost
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IConfigurationSection ScopedConfig { get; } = scoped;
    }

    private sealed class DelegatingSandbox(
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
