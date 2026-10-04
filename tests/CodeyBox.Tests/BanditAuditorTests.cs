using System.Diagnostics;
using CodeyBox.BanditAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Bandit auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), exits 0 and 1 with SARIF
/// results are the findings verdict while every other non-zero exit
/// (notably 2 for configuration and usage errors) is infrastructure, SARIF
/// maps to findings with rule id and file/line, severity goes through the
/// declared mapping (both SARIF levels and Bandit's native HIGH/MEDIUM/LOW
/// severities) rather than passing through, repo-authored <c># nosec</c>
/// comments stay inert and a repo-authored <c>.bandit</c> project file fails
/// closed unless the operator opts in, and the plugin is inert — unloaded
/// and absent from baseline provisioning — until an operator enables it.
/// Every run is dispatched through <see cref="IAuditor"/> so the version
/// pin cannot be bypassed by interface dispatch.
/// </summary>
public sealed class BanditAuditorTests
{
    // Mirrors what `bandit -r . -f sarif` writes for a shell=True issue
    // (shape verified against bandit's SARIF formatter,
    // bandit/formatters/sarif.py): each result carries its own "level"
    // derived from the issue severity (error for HIGH), the test id as
    // ruleId (here B602), message text, the native severity/confidence
    // properties, and the first physical location's repo-relative artifact
    // uri plus region.startLine.
    private const string SarifWithShellTrue = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "Bandit",
                "organization": "PyCQA",
                "semanticVersion": "1.9.4",
                "rules": [{
                  "id": "B602",
                  "name": "subprocess_popen_with_shell_equals_true",
                  "properties": {
                    "tags": ["security"],
                    "precision": "high"
                  },
                  "helpUri": "https://bandit.readthedocs.io/en/1.9.4/plugins/b602_subprocess_popen_with_shell_equals_true.html"
                }]
              }
            },
            "results": [{
              "ruleId": "B602",
              "ruleIndex": 0,
              "level": "error",
              "message": { "text": "Possible process with shell equals true." },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "app.py" },
                  "region": { "startLine": 3 }
                }
              }],
              "properties": {
                "issue_confidence": "HIGH",
                "issue_severity": "HIGH"
              }
            }]
          }]
        }
        """;

    private const string SarifClean = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "Bandit", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingBandit_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "bandit: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BanditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("bandit", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task SubprocessShellTrueInFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, SarifWithShellTrue, ""));
        });

        IAuditor auditor = new BanditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:bandit", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("B602", finding.Title, StringComparison.Ordinal);
        Assert.Equal("app.py:3", finding.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("bandit", argv[0]);
        Assert.Contains("-r", argv);
        var recursiveFlag = argv.ToList().IndexOf("-r");
        Assert.True(recursiveFlag >= 0 && recursiveFlag + 1 < argv.Count);
        Assert.Equal(".", argv[recursiveFlag + 1]);
        Assert.Contains("-f", argv);
        var formatFlag = argv.ToList().IndexOf("-f");
        Assert.True(formatFlag >= 0 && formatFlag + 1 < argv.Count);
        Assert.Equal("sarif", argv[formatFlag + 1]);
        Assert.Contains("--ignore-nosec", argv);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new BanditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task IssuesExit_IsFindings_WhileErrorExits_AreInfrastructure()
    {
        // Bandit exits 1 when issues were found — the SARIF document is
        // still the verdict, so exit 1 with results is findings.
        IAuditor auditor = new BanditAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(1, SarifWithShellTrue),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // Exit 0 with results is findings too (e.g. under --exit-zero,
        // which the auditor rejects in configuration but still parses as
        // a verdict rather than infrastructure when the tool ran).
        var foundOnZero = await auditor.RunAsync(
            HealthyTool(0, SarifWithShellTrue),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(foundOnZero.Passed);
        Assert.Single(foundOnZero.Findings);

        // Exit 2 is Bandit's "could not run" exit (invalid config, no
        // targets, empty profile, baseline misuse, argument rejection).
        // Even with parseable SARIF on stdout it means the scan did not
        // complete.
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, SarifWithShellTrue), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 2", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(3, "some other failure"), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // The fixture's error-level result maps to a blocking Error.
        IAuditor auditor = new BanditAuditor();
        var error = await auditor.RunAsync(
            HealthyTool(1, SarifWithShellTrue),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);
        Assert.False(error.Passed);

        // A warning-level result is advisory: findings without a failed audit.
        var warning = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(SarifWithShellTrue, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        var warningFinding = Assert.Single(warning.Findings);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.True(warning.Passed);

        // A note-level result is informational.
        var note = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(SarifWithShellTrue, "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(note.Findings).Severity);
        Assert.True(note.Passed);

        // An unrecognised tool level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(SarifWithShellTrue, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);
    }

    [Fact]
    public async Task NativeBanditSeverities_MapThroughDeclaredMapping()
    {
        // Bandit's native vocabulary never reaches findings raw: HIGH
        // blocks, MEDIUM is advisory, LOW is informational.
        IAuditor auditor = new BanditAuditor();

        var high = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(SarifWithShellTrue, "HIGH")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(high.Findings).Severity);
        Assert.False(high.Passed);

        var medium = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(SarifWithShellTrue, "MEDIUM")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(medium.Findings).Severity);
        Assert.True(medium.Passed);

        var low = await auditor.RunAsync(
            HealthyTool(1, WithResultLevel(SarifWithShellTrue, "LOW")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(low.Findings).Severity);
        Assert.True(low.Passed);
    }

    [Fact]
    public async Task NosecComments_AreIgnored_ByDefault_AndOptIn()
    {
        SandboxExec? defaultExec = null;
        var defaultSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            defaultExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor defaultAuditor = new BanditAuditor();
        await defaultAuditor.RunAsync(defaultSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(defaultExec);
        Assert.Contains("--ignore-nosec", defaultExec!.Argv);

        SandboxExec? trustingExec = null;
        var trustingSandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            trustingExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var trustingAuditor = new BanditAuditor();
        await trustingAuditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);
        await ((IAuditor)trustingAuditor).RunAsync(trustingSandbox, "/work", FakeContext(), CancellationToken.None);
        Assert.NotNull(trustingExec);
        Assert.DoesNotContain("--ignore-nosec", trustingExec!.Argv);
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
                return Task.FromResult(new SandboxExecResult(0, "bandit 1.7.0\n  python version = 3.12.3\n", ""));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BanditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("1.7.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(BanditAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task RealisticVersionOutput_IsAccepted()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0,
                    "bandit " + BanditAuditor.DefaultExpectedVersion + "\n  python version = 3.12.3 (main, Jun 18 2025)\n",
                    ""));
            if (IsRepoProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BanditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "1.7.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "bandit 1.7.0\n  python version = 3.12.3\n", ""));
            if (IsRepoProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            return Task.FromResult(new SandboxExecResult(1, SarifWithShellTrue, ""));
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
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BanditAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "B602",
            }),
            CancellationToken.None);

        var vendored = SarifWithShellTrue.Replace(
            "\"uri\": \"app.py\"", "\"uri\": \"vendor/pkg/app.py\"", StringComparison.Ordinal);
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(1, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace(
            "B602", "B101", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(1, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    [Fact]
    public async Task RepositoryProjectFile_Present_FailsClosed_UnlessTrusted()
    {
        // Bandit walks the -r scan targets for a project .bandit INI file
        // and silently honors a single match — its exclude/tests/skips
        // entries shape the gate, so presence fails closed as
        // infrastructure before the scan runs.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./tools/.bandit\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new BanditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("bandit", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".bandit", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepositoryProjectFile_Present_TrustedOperator_ScanRuns()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./tools/.bandit\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, scanExecs);
    }

    // Gate-shaping ExtraArguments would collapse the exit-code contract
    // (--exit-zero) or replace/divert the SARIF the parser expects
    // (-f/--format, -o/--output): all fail closed deterministically before
    // the scan runs, in both trust modes.
    [Theory]
    [InlineData("--exit-zero")]
    [InlineData("--format,sarif")]
    [InlineData("-fsarif")]
    [InlineData("--format=json")]
    [InlineData("--output,/tmp/out.sarif")]
    [InlineData("-o,/tmp/out.sarif")]
    public async Task ReservedExtraArguments_AreRejected_BeforeScan(string extraArguments)
    {
        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    // An operator file flag whose value resolves inside the audited
    // worktree hands gate-shaping content (scanner config, argument INI,
    // finding baseline) to repo-controlled bytes — the flags resolve
    // against the tool's cwd — so it fails closed before the scan runs.
    [Theory]
    [InlineData("--configfile,ops/bandit.yaml")]
    [InlineData("--configfile=ops/bandit.yaml")]
    [InlineData("-cops/bandit.yaml")]
    [InlineData("--ini,ops/args.ini")]
    [InlineData("--baseline,ops/baseline.json")]
    [InlineData("-bops/baseline.json")]
    public async Task ExtraArgumentsFileFlag_ResolvingInsideWorktree_FailsClosed(string extraArguments)
    {
        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArguments,
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(RealpathResult(insideWorktree: true));
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
    public async Task ExtraArgumentsFileFlag_WithoutValue_FailsClosed()
    {
        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--configfile",
            }),
            CancellationToken.None);

        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("--configfile", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExtraArgumentsFileFlag_ResolvingOutsideWorktree_ScanRuns()
    {
        var auditor = new BanditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--configfile,/etc/bandit/config.yaml",
            }),
            CancellationToken.None);

        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(RealpathResult(insideWorktree: false));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var result = await ((IAuditor)auditor).RunAsync(
            sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        // The gate contains, it does not strip — the operator flag still
        // reaches the scan argv verbatim.
        var argv = scanExec!.Argv.ToList();
        var configFlag = argv.IndexOf("--configfile");
        Assert.True(configFlag >= 0 && configFlag + 1 < argv.Count);
        Assert.Equal("/etc/bandit/config.yaml", argv[configFlag + 1]);
    }

    // Fixture sources assembled at runtime so this test file does not itself
    // carry a scanner-detectable shell=True call literal. The vulnerable app
    // builds a shell command from input with shell enabled (Bandit's
    // textbook high-severity shell injection); the clean app never shells
    // out.
    private static readonly string _shellEnabled = "shell" + "=True";

    private static readonly string _fixtureVulnApp =
        "import subprocess\n"
        + "import sys\n"
        + "\n"
        + "\n"
        + "def run(name):\n"
        + "    return subprocess.Popen(\"ls \" + name, " + _shellEnabled + ")\n";

    private static readonly string _fixtureCleanApp =
        "def run(name):\n"
        + "    return name.strip()\n";

    private static readonly string? _installedBanditVersion = ProbeInstalledBanditVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture repository with a known
    /// shell-injection issue is scanned by the actual Bandit CLI through a
    /// real process exec — exercising the invocation, the stdout SARIF
    /// sink, and the exit-1-with-findings convention together, so a broken
    /// real invocation cannot stay green. Runs only where a bandit binary
    /// is on PATH; the auditor's version pin is set to the installed
    /// release.
    /// </summary>
    [Fact]
    [Trait("requires_bandit", "true")]
    public async Task RealBandit_ShellInjection_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedBanditVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(("vuln.py", _fixtureVulnApp));
        try
        {
            var auditor = new BanditAuditor();
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
            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("B602", StringComparison.Ordinal));
            Assert.Equal("codeybox:bandit", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.NotNull(finding.Location);
            Assert.StartsWith("vuln.py", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture repository with no findings
    /// passes with zero findings — and the analysis exits 0.
    /// </summary>
    [Fact]
    [Trait("requires_bandit", "true")]
    public async Task RealBandit_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedBanditVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(("clean.py", _fixtureCleanApp));
        try
        {
            var auditor = new BanditAuditor();
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
            s => s.PluginId == BanditAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("bandit", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresBanditRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [BanditAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == BanditAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("bandit", tool.Binary);
        // Verify-only by design: Bandit ships as a Python package, not a
        // distro package, so no apt line can carry the version pin — the
        // baseline verifies presence and the operator provisions the pinned
        // release. No install commands are emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("bandit", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.BanditAuditorPlugin.dll");
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
            PluginId: BanditAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Bandit Python SAST",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static string WithResultLevel(string sarif, string level)
        => sarif.Replace(
            "\"level\": \"error\",",
            "\"level\": \"" + level + "\",",
            StringComparison.Ordinal);

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "bandit " + BanditAuditor.DefaultExpectedVersion + "\n  python version = 3.12.3\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoProbe(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "bandit" && exec.Argv[1] == "--version";

    // The VerifyToolAsync repository-suppression probe is an sh -c
    // find-based script without the "command -v" presence marker;
    // answering it with exit 0 and empty stdout means "no .bandit present".
    private static bool IsRepoProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "realpath";

    // Two-line realpath -m answer: the canonicalized configured path and
    // the canonicalized worktree. Inside-worktree values fail the
    // out-of-worktree containment gate; outside values let the scan run.
    private static SandboxExecResult RealpathResult(bool insideWorktree)
        => insideWorktree
            ? new SandboxExecResult(0, "/work/ops/bandit.yaml\n/work\n", "")
            : new SandboxExecResult(0, "/etc/bandit/config.yaml\n/work\n", "");

    private static async Task<string> SeedFixtureRepoAsync(params (string Name, string Content)[] files)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-bandit-fixture-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string? ProbeInstalledBanditVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bandit",
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
            // Any failure means no usable bandit on PATH — the gated tests
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
