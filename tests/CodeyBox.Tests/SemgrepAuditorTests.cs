using System.Diagnostics;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.SemgrepAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Semgrep auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass); <c>--error</c> makes exit
/// 1 the "ran and found problems" verdict while 0 is clean and every other
/// exit is infrastructure; SARIF maps to findings with rule id and
/// file/line; severity goes through the declared mapping rather than passing
/// through; rules resolve from operator <c>Config</c> or a repository
/// <c>.semgrep*</c> source (neither resolving is deterministic
/// infrastructure, never a pass); repo-authored <c>nosemgrep</c>/.
/// <c>semgrepignore</c> suppression is disabled unless the operator opts in;
/// and the plugin is inert — unloaded and absent from baseline provisioning —
/// until an operator enables it. Every run is dispatched through <see
/// cref="IAuditor"/> so the version pin cannot be bypassed by interface
/// dispatch.
/// </summary>
public sealed class SemgrepAuditorTests
{
    // Mirrors what `semgrep scan --config .semgrep --sarif --error .` writes
    // for a match (verified against the pinned release's real output): results
    // carry no "level" — the rule's severity surfaces once per run as
    // tool.driver.rules[].defaultConfiguration.level (ERROR→error,
    // WARNING→warning, INFO→note) — plus the rule id, message text, and the
    // first physical location's repo-relative artifact uri plus
    // region.startLine.
    private const string SarifWithDangerousCall = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "semgrep",
                "rules": [{
                  "id": "fixture.dangerous-call",
                  "name": "fixture.dangerous-call",
                  "defaultConfiguration": { "level": "error" }
                }]
              }
            },
            "results": [{
              "ruleId": "fixture.dangerous-call",
              "message": { "text": "dangerous_sink invoked with untrusted input" },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "vuln.py" },
                  "region": { "startLine": 2 }
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
            "tool": { "driver": { "name": "semgrep", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingSemgrep_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "semgrep: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SemgrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("semgrep", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task MissingBinary_NamesSemgrep_EvenWhenNoRulesetResolves()
    {
        // Ruleset resolution happens inside the same run, but a missing
        // binary must surface as "semgrep not installed" — never as a
        // confusing no-config failure that hides the real provisioning gap.
        var sandbox = new FakeSandbox((exec, _) => Task.FromResult(
            IsPresenceProbe(exec)
                ? new SandboxExecResult(1, "", "")
                : new SandboxExecResult(127, "", "semgrep: command not found")));

        IAuditor auditor = new SemgrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("semgrep", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep"));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithDangerousCall, ""));
        });

        IAuditor auditor = new SemgrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:semgrep", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("fixture.dangerous-call", finding.Title, StringComparison.Ordinal);
        Assert.Equal("vuln.py:2", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("semgrep", argv[0]);
        Assert.Equal("scan", argv[1]);
        Assert.Contains("--sarif", argv);
        Assert.Contains("--error", argv);
        Assert.Contains("--metrics=off", argv);
        Assert.Contains("--disable-version-check", argv);
        Assert.Contains("--oss-only", argv);
        Assert.Contains("--no-rewrite-rule-ids", argv);
        var configFlag = argv.ToList().IndexOf("--config");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal(".semgrep", argv[configFlag + 1]);
        // The audited worktree is the positional scan target.
        Assert.Contains(".", argv);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new SemgrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsRideExitOne_WhileOtherNonZeroExits_AreInfrastructure()
    {
        // semgrep scan exits 0 on completion regardless of findings unless
        // --error is passed — which the auditor does — so exit 1 is the
        // dedicated "ran and found problems" verdict.
        IAuditor auditor = new SemgrepAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(1, SarifWithDangerousCall),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // Exit 0 with results is also a verdict (e.g. operator overrode the
        // convention) — the SARIF document is what carries findings.
        var alsoFound = await auditor.RunAsync(
            HealthyTool(0, SarifWithDangerousCall),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(alsoFound.Passed);
        Assert.Single(alsoFound.Findings);

        // 2 is semgrep's fatal error — "could not run" — even with parseable
        // SARIF on stdout. There is no other findings-producing exit.
        var fatalEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, SarifWithDangerousCall), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 2", fatalEx.Message, StringComparison.Ordinal);

        // 7 is missing/invalid configuration — infrastructure, not a verdict.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(7, ""), "/work", FakeContext(), CancellationToken.None));

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(130, ""), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // The fixture's error-level result maps to a blocking Error.
        IAuditor auditor = new SemgrepAuditor();
        var error = await auditor.RunAsync(
            HealthyTool(1, SarifWithDangerousCall),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);
        Assert.False(error.Passed);

        // A warning-severity rule is advisory: findings without a failed audit.
        var warning = await auditor.RunAsync(
            HealthyTool(0, WithRuleLevel(SarifWithDangerousCall, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        var warningFinding = Assert.Single(warning.Findings);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.True(warning.Passed);

        // A note-severity rule (INFO) is informational.
        var note = await auditor.RunAsync(
            HealthyTool(0, WithRuleLevel(SarifWithDangerousCall, "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(note.Findings).Severity);
        Assert.True(note.Passed);

        // An unrecognised tool level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(0, WithRuleLevel(SarifWithDangerousCall, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);

        // A result that carries its own SARIF level wins over the rule's
        // defaultConfiguration (SARIF level-resolution order).
        var explicitLevel = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithDangerousCall, "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(explicitLevel.Findings).Severity);

        // A result whose rule is absent from rules[] keeps the SARIF
        // default level (warning) — the mapping still applies.
        var unmappedRule = SarifWithDangerousCall.Replace(
            "\"id\": \"fixture.dangerous-call\"", "\"id\": " + "\"other.rule\"",
            StringComparison.Ordinal);
        var defaulted = await auditor.RunAsync(
            HealthyTool(0, unmappedRule), "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(defaulted.Findings).Severity);
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
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep.yml"));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SemgrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configFlag = argv.ToList().IndexOf("--config");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal(".semgrep.yml", argv[configFlag + 1]);
    }

    [Fact]
    public async Task OperatorConfig_SuppliesConfigArguments_WithoutProbing()
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

        var auditor = new SemgrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Config"] = "rules.yaml,/opt/semgrep-rules",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.False(probed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var first = argv.IndexOf("--config");
        Assert.True(first >= 0 && first + 3 < argv.Count);
        Assert.Equal("rules.yaml", argv[first + 1]);
        Assert.Equal("--config", argv[first + 2]);
        Assert.Equal("/opt/semgrep-rules", argv[first + 3]);
    }

    [Fact]
    public async Task NoRulesetAnywhere_IsDeterministicInfrastructure_NeverAPass()
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

        IAuditor auditor = new SemgrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("semgrep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Config", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task RepositorySuppression_IsDisabled_ByDefault_AndOptIn()
    {
        SandboxExec? defaultExec = null;
        var defaultSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep"));
            defaultExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor defaultAuditor = new SemgrepAuditor();
        await defaultAuditor.RunAsync(defaultSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(defaultExec);
        Assert.Contains("--disable-nosem", defaultExec!.Argv);
        Assert.Contains("--x-ignore-semgrepignore-files", defaultExec.Argv);

        SandboxExec? trustingExec = null;
        var trustingSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep"));
            trustingExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var trustingAuditor = new SemgrepAuditor();
        await trustingAuditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);
        await ((IAuditor)trustingAuditor).RunAsync(trustingSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(trustingExec);
        Assert.DoesNotContain("--disable-nosem", trustingExec!.Argv);
        Assert.DoesNotContain("--x-ignore-semgrepignore-files", trustingExec.Argv);
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
                return Task.FromResult(new SandboxExecResult(0, "1.172.0\n", ""));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SemgrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("1.172.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SemgrepAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new SemgrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "1.172.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.172.0\n", ""));
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep"));
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
                return Task.FromResult(RepositoryProbeResult(exec, ".semgrep"));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new SemgrepAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new SemgrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "fixture.dangerous-call",
            }),
            CancellationToken.None);

        var vendored = SarifWithDangerousCall.Replace("vuln.py", "vendor/pkg/vuln.py");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(1, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace(
            "fixture.dangerous-call", "fixture.other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(1, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Fixture sources assembled at runtime so this test file does not itself
    // carry a scanner-detectable suspicious-call literal.
    private static readonly string _fixtureRulesYml =
        "rules:\n"
        + "  - id: fixture-dangerous-call\n"
        + "    languages: [python]\n"
        + "    severity: ERROR\n"
        + "    message: fixture sink invoked\n"
        + "    pattern: dangerous_sink" + "(...)\n";

    private static readonly string _fixtureVulnPy =
        "def go():\n"
        + "    dangerous_sink" + "(\"data\")\n";

    private static readonly string _fixtureCleanPy =
        "def go():\n"
        + "    safe_sink" + "(\"data\")\n";

    private static readonly string? _installedSemgrepVersion = ProbeInstalledSemgrepVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture repository with a
    /// <c>.semgrep</c> ruleset and a known match is scanned by the actual
    /// Semgrep CLI through a real process exec — exercising the repository
    /// config discovery, the scan invocation, the stdout SARIF sink, and the
    /// exit-1-with-findings convention together, so a broken real invocation
    /// cannot stay green. Runs only where a semgrep binary is on PATH; the
    /// auditor's version pin is set to the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_semgrep", "true")]
    public async Task RealSemgrep_KnownIssue_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedSemgrepVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            (".semgrep/rules.yml", _fixtureRulesYml),
            ("vuln.py", _fixtureVulnPy));
        try
        {
            var auditor = new SemgrepAuditor();
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
            Assert.Equal("codeybox:semgrep", finding.AuditorName);
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
    [Trait("requires_semgrep", "true")]
    public async Task RealSemgrep_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedSemgrepVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(
            (".semgrep/rules.yml", _fixtureRulesYml),
            ("clean.py", _fixtureCleanPy));
        try
        {
            var auditor = new SemgrepAuditor();
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
            s => s.PluginId == SemgrepAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("semgrep", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresSemgrepRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SemgrepAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SemgrepAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("semgrep", tool.Binary);
        // Verify-only by design: Semgrep ships as a Python package, not a
        // distro package, so no apt line can carry the version pin — the
        // baseline verifies presence and the operator provisions the pinned
        // release. No install commands are emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("semgrep", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SemgrepAuditorPlugin.dll");
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
            PluginId: SemgrepAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Semgrep Structural SAST",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    // Real semgrep results carry no "level"; the rule's severity lives in
    // its defaultConfiguration — vary that to vary a finding's level.
    private static string WithRuleLevel(string sarif, string level)
        => sarif.Replace(
            "\"level\": \"error\"",
            "\"level\": \"" + level + "\"",
            StringComparison.Ordinal);

    // Gives the result its own "level", which SARIF resolves ahead of the
    // rule's defaultConfiguration.
    private static string WithResultLevel(string sarif, string level)
        => sarif.Replace(
            "\"ruleId\": \"fixture.dangerous-call\"",
            "\"level\": \"" + level + "\", \"ruleId\": \"fixture.dangerous-call\"",
            StringComparison.Ordinal);

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, SemgrepAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec)
                ? Ok(exec)
                : IsRepositoryFileProbe(exec)
                    ? RepositoryProbeResult(exec, ".semgrep")
                    : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "semgrep" && exec.Argv[1] == "--version";

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
            Path.GetTempPath(), "codeybox-semgrep-fixture-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string? ProbeInstalledSemgrepVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "semgrep",
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
            // Any failure means no usable semgrep on PATH — the gated tests
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
