using System.Diagnostics;
using CodeyBox.AstGrepAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the ast-grep auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass); exits 0 and 1 are the
/// findings verdict (0 rides warning/info/hint matches as well as clean
/// trees, 1 signals error-severity matches) while every other exit is
/// infrastructure; SARIF maps to findings with rule id and file/line;
/// severity goes through the declared mapping rather than passing through;
/// the project resolves from operator <c>Config</c> or a repository
/// <c>sgconfig.yml</c>/<c>sgconfig.yaml</c> source (neither resolving is
/// deterministic infrastructure, never a pass); and the plugin is inert —
/// unloaded and absent from baseline provisioning — until an operator enables
/// it. Every run is dispatched through <see cref="IAuditor"/> so the version
/// pin cannot be bypassed by interface dispatch.
/// </summary>
public sealed class AstGrepAuditorTests
{
    // Mirrors what `ast-grep scan --format sarif` writes for a match
    // (verified against the pinned release's real output): each result
    // carries its own "level" (error/warning/note — info and hint both
    // render as note), the rule id, message text, and the first physical
    // location's repo-relative artifact uri plus region.startLine.
    private const string SarifWithDangerousCall = """
        {
          "version": "0.45.3",
          "runs": [{
            "tool": {
              "driver": { "name": "ast-grep" }
            },
            "results": [{
              "ruleId": "fixture-dangerous-call",
              "level": "error",
              "message": { "text": "dangerous_sink invoked with untrusted input" },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "vuln.py" },
                  "region": {
                    "startLine": 2,
                    "startColumn": 1,
                    "endLine": 2,
                    "endColumn": 27,
                    "snippet": { "text": "dangerous_sink(user_input)" }
                  }
                }
              }]
            }]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "0.45.3",
          "runs": [{
            "tool": { "driver": { "name": "ast-grep" } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingAstGrep_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "ast-grep: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task MissingBinary_NamesAstGrep_EvenWhenNoRulesetResolves()
    {
        // Project resolution happens inside the same run, but a missing
        // binary must surface as "ast-grep not installed" — never as a
        // confusing no-config failure that hides the real provisioning gap.
        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(
            IsPresenceProbe(exec)
                ? new SandboxExecResult(1, "", "")
                : new SandboxExecResult(127, "", "ast-grep: command not found")));

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not installed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownIssueInFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, "sgconfig.yml"));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithDangerousCall, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:ast-grep", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("fixture-dangerous-call", finding.Title, StringComparison.Ordinal);
        Assert.Equal("vuln.py:2", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("ast-grep", argv[0]);
        Assert.Equal("scan", argv[1]);
        Assert.Contains("--format", argv);
        Assert.Contains("sarif", argv);
        Assert.Contains("--color", argv);
        Assert.Contains("never", argv);
        var configFlag = argv.ToList().IndexOf("-c");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal("sgconfig.yml", argv[configFlag + 1]);
        // The audited worktree is the positional scan target.
        Assert.Contains(".", argv);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsRideExitsZeroAndOne_WhileOtherNonZeroExits_AreInfrastructure()
    {
        // ast-grep scan exits 1 when error-severity matches exist — the
        // dedicated "ran and found problems" verdict.
        IAuditor auditor = new AstGrepAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(1, SarifWithDangerousCall),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // Exit 0 rides warning/info/hint findings as well as clean trees —
        // the SARIF document is what carries findings, never the exit code.
        var warning = await auditor.RunAsync(
            HealthyTool(0, WithToolLevel(SarifWithDangerousCall, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(warning.Passed);
        Assert.Single(warning.Findings);

        // The verdict comes from the document even when the exit disagrees:
        // exit 0 alongside error-level results is still findings, failed.
        var disagreeing = await auditor.RunAsync(
            HealthyTool(0, SarifWithDangerousCall),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(disagreeing.Passed);
        Assert.Single(disagreeing.Findings);

        // 2 is a CLI usage error — "could not run" — even with parseable
        // SARIF on stdout. There is no other findings-producing exit.
        var usageEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, SarifWithDangerousCall), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 2", usageEx.Message, StringComparison.Ordinal);

        // 3 is a missing project configuration — infrastructure, not a verdict.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(3, ""), "/work", FakeContext(), CancellationToken.None));

        // 6 is an unreadable configuration file — infrastructure.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(6, ""), "/work", FakeContext(), CancellationToken.None));

        // 8 is an invalid rule file — infrastructure.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(8, ""), "/work", FakeContext(), CancellationToken.None));

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(130, ""), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // The fixture's error-level result maps to a blocking Error.
        IAuditor auditor = new AstGrepAuditor();
        var error = await auditor.RunAsync(
            HealthyTool(1, SarifWithDangerousCall),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);
        Assert.False(error.Passed);

        // A warning-severity rule is advisory: findings without a failed audit.
        var warning = await auditor.RunAsync(
            HealthyTool(0, WithToolLevel(SarifWithDangerousCall, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        var warningFinding = Assert.Single(warning.Findings);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.True(warning.Passed);

        // A note-severity rule (info/hint render as note in SARIF) is
        // informational.
        var note = await auditor.RunAsync(
            HealthyTool(0, WithToolLevel(SarifWithDangerousCall, "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(note.Findings).Severity);
        Assert.True(note.Passed);

        // The raw hint token maps the same way, wherever the tool spells it.
        var hint = await auditor.RunAsync(
            HealthyTool(0, WithToolLevel(SarifWithDangerousCall, "hint")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(hint.Findings).Severity);

        // An unrecognised tool level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(0, WithToolLevel(SarifWithDangerousCall, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);

        // A result with no level keeps the SARIF default (warning) — the
        // mapping still applies.
        var noLevel = await auditor.RunAsync(
            HealthyTool(0, WithoutLevel(SarifWithDangerousCall)),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(noLevel.Findings).Severity);
    }

    [Fact]
    public async Task RepositoryConfig_IsDiscovered_WhenOperatorConfigAbsent()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, "sgconfig.yml"));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configFlag = argv.ToList().IndexOf("-c");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal("sgconfig.yml", argv[configFlag + 1]);
    }

    [Fact]
    public async Task YamlConfigSpelling_IsDiscovered_WhenYmlAbsent()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, "sgconfig.yaml"));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configFlag = argv.IndexOf("-c");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal("sgconfig.yaml", argv[configFlag + 1]);
    }

    [Fact]
    public async Task OperatorConfig_SuppliesConfigArgument_WithoutProbing()
    {
        var probed = false;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
            {
                probed = true;
                return Task.FromResult(RepositoryProbeResult(exec));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Config"] = "/opt/ast-grep/sgconfig.yml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.False(probed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configFlag = argv.IndexOf("-c");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal("/opt/ast-grep/sgconfig.yml", argv[configFlag + 1]);
    }

    [Fact]
    public async Task NoProjectAnywhere_IsDeterministicInfrastructure_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Config", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sgconfig.yml", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task WrongToolVersion_IsInfrastructure_ScanNeverRuns()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "ast-grep 0.39.0\n", ""));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, "sgconfig.yml"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("0.39.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(AstGrepAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "0.39.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "ast-grep 0.39.0\n", ""));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, "sgconfig.yml"));
            return Task.FromResult(new SandboxExecResult(1, SarifWithDangerousCall, ""));
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
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "dev-build\n", ""));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, "sgconfig.yml"));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "fixture-dangerous-call",
            }),
            CancellationToken.None);

        var vendored = SarifWithDangerousCall.Replace("vuln.py", "vendor/pkg/vuln.py");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(1, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace(
            "fixture-dangerous-call", "fixture-other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(1, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Fixture sources assembled at runtime so this test file does not itself
    // carry a scanner-detectable suspicious-call literal.
    private static readonly string _fixtureSgconfigYml =
        "ruleDirs:\n"
        + "  - rules\n";

    private static readonly string _fixtureRuleYml =
        "id: fixture-dangerous-call\n"
        + "message: fixture sink invoked\n"
        + "severity: error\n"
        + "language: python\n"
        + "rule:\n"
        + "  pattern: dangerous_sink" + "($ARG)\n";

    private static readonly string _fixtureVulnPy =
        "from x import dangerous_sink" + "\n"
        + "dangerous_sink" + "(user_input)\n";

    private static readonly string _fixtureCleanPy =
        "def go():\n"
        + "    print" + "(\"hello\")\n";

    private static readonly string? _installedAstGrepVersion = ProbeInstalledAstGrepVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture repository with an
    /// <c>sgconfig.yml</c> project and a known match is scanned by the actual
    /// ast-grep CLI through a real process exec — exercising the project
    /// discovery, the scan invocation, the stdout SARIF sink, and the
    /// exit-1-with-error-findings convention together, so a broken real
    /// invocation cannot stay green. Runs only where an ast-grep binary is on
    /// PATH; the auditor's version pin is set to the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_ast_grep", "true")]
    public async Task RealAstGrep_KnownIssue_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedAstGrepVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            ("sgconfig.yml", _fixtureSgconfigYml),
            ("rules/dangerous-call.yml", _fixtureRuleYml),
            ("vuln.py", _fixtureVulnPy));
        try
        {
            var auditor = new AstGrepAuditor();
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
            Assert.Equal("codeybox:ast-grep", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("fixture-dangerous-call", finding.Title, StringComparison.Ordinal);
            Assert.EndsWith("vuln.py:2", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture repository whose ruleset finds
    /// nothing passes with zero findings — and the analysis still exits 0.
    /// </summary>
    [Fact]
    [Trait("requires_ast_grep", "true")]
    public async Task RealAstGrep_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedAstGrepVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            ("sgconfig.yml", _fixtureSgconfigYml),
            ("rules/dangerous-call.yml", _fixtureRuleYml),
            ("clean.py", _fixtureCleanPy));
        try
        {
            var auditor = new AstGrepAuditor();
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
            s => s.PluginId == AstGrepAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("ast-grep", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresAstGrepRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [AstGrepAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == AstGrepAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("ast-grep", tool.Binary);
        // Verify-only by design: ast-grep ships through language channels
        // (npm, cargo, pip, brew), not as a distro package, so no apt line
        // can carry the version pin — the baseline verifies presence and the
        // operator provisions the pinned release. No install commands are
        // emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("ast-grep", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.AstGrepAuditorPlugin.dll");
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
            PluginId: AstGrepAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ast-grep Structural SAST",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    // Real ast-grep results carry their own "level" — vary it to vary a
    // finding's level.
    private static string WithToolLevel(string sarif, string level)
        => sarif.Replace(
            "\"level\": \"error\"",
            "\"level\": \"" + level + "\"",
            StringComparison.Ordinal);

    // Drops the result's own "level", which SARIF then defaults to warning.
    private static string WithoutLevel(string sarif)
        => sarif.Replace(
            "\"level\": \"error\",",
            "",
            StringComparison.Ordinal);

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "ast-grep " + AstGrepAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec)
                ? Ok(exec)
                : IsRepositoryFileProbe(exec)
                    ? RepositoryProbeResult(exec, "sgconfig.yml")
                    : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "ast-grep" && exec.Argv[1] == "--version";

    private static bool IsRepositoryFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    // The presence script echoes each requested name that exists, one per
    // line; simulate by intersecting argv[4..] with the supplied present set.
    private static SandboxExecResult RepositoryProbeResult(SandboxExec exec, params string[] present)
    {
        var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
        var stdout = string.Concat(
            exec.Argv.Skip(4).Where(presentSet.Contains).Select(static p => p + "\n"));
        return new SandboxExecResult(0, stdout, "");
    }

    private static async Task<string> SeedFixtureRepoAsync(params (string Name, string Content)[] files)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-ast-grep-fixture-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string? ProbeInstalledAstGrepVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ast-grep",
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
            if (process.ExitCode != 0)
                return null;
            var match = System.Text.RegularExpressions.Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return match.Success ? match.Value : null;
        }
        catch
        {
            // Any failure means no usable ast-grep on PATH — the gated tests
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
