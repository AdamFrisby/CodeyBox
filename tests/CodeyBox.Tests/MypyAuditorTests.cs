using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.MypyAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the mypy auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming mypy (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 — blocker diagnostics or
///   "could not run" — and everything else is infrastructure.
/// - Exit 1 without JSON diagnostics fails closed as an infrastructure failure, as does
///   a mixed JSON/plain-text stream.
/// - mypy JSONL output maps to findings with rule ids (error codes), locations, and mapped
///   severity; note-level diagnostics are advisory.
/// - Default exclusions (vendored + generated + Python env/cache trees) and scoped options
///   (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_mypy", "true")].
/// </summary>
public sealed class MypyAuditorTests
{
    private static readonly string? InstalledMypyVersion = ProbeInstalledMypyVersion();

    // Verbatim mypy --output json shape: one diagnostic object per line.
    private const string JsonlWithIssues = """
        {"file": "src/app.py", "line": 3, "column": 7, "message": "Incompatible types in assignment (expression has type \"str\", variable has type \"int\")", "hint": null, "code": "assignment", "severity": "error"}
        {"file": "pkg/util.py", "line": 10, "column": 12, "message": "Argument 1 to \"greet\" has incompatible type \"int\"; expected \"str\"", "hint": "Use str() to coerce", "code": "arg-type", "severity": "error"}
        """;

    // A clean run under --output json emits only the suppressed success line
    // (a blank line) on stdout.
    private const string JsonlClean = "\n";

    private const string JsonlNoteOnly = """
        {"file": "src/app.py", "line": 1, "column": 12, "message": "Revealed type is \"builtins.int\"", "hint": null, "code": "misc", "severity": "note"}
        """;

    private const string JsonlWithFilteredPaths = """
        {"file": "src/main.py", "line": 2, "column": 9, "message": "Incompatible types in assignment", "hint": null, "code": "assignment", "severity": "error"}
        {"file": "vendor/lib.py", "line": 1, "column": 9, "message": "Incompatible types in assignment", "hint": null, "code": "assignment", "severity": "error"}
        {"file": ".venv/lib/sitepkg.py", "line": 4, "column": 9, "message": "Incompatible types in assignment", "hint": null, "code": "assignment", "severity": "error"}
        {"file": "dist/bundle.py", "line": 6, "column": 9, "message": "Incompatible types in assignment", "hint": null, "code": "assignment", "severity": "error"}
        """;

    private const string JsonlWithSeverities = """
        {"file": "a.py", "line": 1, "column": 1, "message": "Error-level diagnostic.", "hint": null, "code": "assignment", "severity": "error"}
        {"file": "a.py", "line": 2, "column": 1, "message": "Note-level diagnostic.", "hint": null, "code": "misc", "severity": "note"}
        {"file": "a.py", "line": 3, "column": 1, "message": "Unrecognised level.", "hint": null, "code": "misc", "severity": "blocker"}
        {"file": "a.py", "line": 4, "column": 1, "message": "No severity field.", "hint": null, "code": "misc"}
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingMypy_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "mypy: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingMypy()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "mypy 9.9.9 (compiled: yes)\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.9.9", ex.Message, StringComparison.Ordinal);
        Assert.Contains(MypyAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        var auditor = new MypyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithTypeErrors_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonlWithIssues, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var assignment = Assert.Single(result.Findings, f => f.Title.Contains("assignment", StringComparison.Ordinal));
        Assert.Equal("codeybox:mypy", assignment.AuditorName);
        Assert.Equal(AuditSeverity.Error, assignment.Severity);
        Assert.Equal("src/app.py:3", assignment.Location);

        var argType = Assert.Single(result.Findings, f => f.Title.Contains("arg-type", StringComparison.Ordinal));
        Assert.Equal("pkg/util.py:10", argType.Location);
        // The hint mypy attached survives inside the finding description.
        Assert.Contains("Use str() to coerce", argType.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("mypy", scanExec!.Argv[0]);
        Assert.Contains("--output", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--no-color-output", scanExec.Argv);
        Assert.Contains("--no-error-summary", scanExec.Argv);
        var cacheIndex = scanExec.Argv.ToList().IndexOf("--cache-dir");
        Assert.True(cacheIndex >= 0 && cacheIndex + 1 < scanExec.Argv.Count);
        Assert.Equal("/dev/null", scanExec.Argv[cacheIndex + 1]);
        Assert.Equal(".", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_BlankStdout_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NotesOnlyExit0_ProducesInfoFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonlNoteOnly, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Equal("src/app.py:1", finding.Location);
        Assert.Contains("misc", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonlWithIssues, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_WithBlockerDiagnostics_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // mypy exits 2 on blocker errors (e.g. syntax errors) and emits them
            // as plain text, not JSON — and also on "could not run" cases. Either
            // way the analysis did not complete: infrastructure, not a verdict.
            return Task.FromResult(new SandboxExecResult(
                2,
                "broken.py:1: error: Invalid syntax  [syntax]\nFound 1 error in 1 file (errors prevented further checking)\n",
                ""));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_NoPythonSources_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // A repository with no Python files makes mypy exit 2 with the
            // reason on stderr — the check could not run, never a pass.
            return Task.FromResult(new SandboxExecResult(
                2, "", "There are no .py[i] files in directory '.'"));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonDiagnostics_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 claims diagnostics were reported; an empty stdout is a
            // crash or foreign output, not a verdict — fails closed.
            return Task.FromResult(new SandboxExecResult(1, "", "Traceback (most recent call last): ..."));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithNonJsonLine_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // A report that is not pure JSONL (here mypy's plain-text format
            // mixed in, e.g. after an operator --output override or partial
            // output) cannot be trusted as a complete verdict — fails closed.
            return Task.FromResult(new SandboxExecResult(
                1,
                "{\"file\": \"a.py\", \"line\": 1, \"column\": 1, \"message\": \"ok\", \"hint\": null, \"code\": \"misc\", \"severity\": \"error\"}\n"
                    + "b.py:2: error: Not JSON at all\n",
                ""));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "mypy: command not found"));
        });

        IAuditor auditor = new MypyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("mypy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonlWithSeverities, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(4, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("Error-level diagnostic", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("Note-level diagnostic", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("Unrecognised level", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("No severity field", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> default
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
            s => s.PluginId == MypyAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("mypy", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresMypyRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [MypyAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == MypyAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("mypy", tool.Binary);
        // Verify-only by design: the distro apt package is unpinned and
        // typically too old for --output=json; provisioning is via pip/pipx.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("mypy", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "mypy 9.9.9 (compiled: yes)\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        var auditor = new MypyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "9.9.9",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonlClean, ""));
        });

        var auditor = new MypyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/mypy.operator.ini",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config-file");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/mypy.operator.ini", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonlWithFilteredPaths, ""));
        });

        IAuditor auditor = new MypyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, .venv/, dist/ findings are dropped by the default ExcludePaths;
        // the src/ diagnostic survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/main.py:2", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonlWithIssues, ""));
        });

        var auditor = new MypyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "arg-type",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("arg-type", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_mypy", "true")]
    public async Task RealMypy_TypeErrorFixture_YieldsFindings()
    {
        var installed = InstalledMypyVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedMypyFixtureRepoAsync(clean: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new MypyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("bad.py:1", finding.Location);
            Assert.Contains("assignment", finding.Title, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_mypy", "true")]
    public async Task RealMypy_CleanFixture_Passes()
    {
        var installed = InstalledMypyVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedMypyFixtureRepoAsync(clean: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new MypyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.Empty(result.Findings);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_mypy", "true")]
    public async Task RealMypy_SyntaxErrorFixture_IsInfrastructureFailure()
    {
        var installed = InstalledMypyVersion;
        if (installed is null)
            return;

        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-mypy-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.py"), "def f(:\n");

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = dir }],
                },
                CancellationToken.None);

            var auditor = new MypyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            // A blocker (syntax error) exits 2 — "errors prevented further
            // checking" — which is infrastructure, never a verdict.
            await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(
                    sandbox, "/work", FakeContext(), CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.MypyAuditorPlugin.dll");
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
            PluginId: MypyAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Mypy Python Type Checker",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "mypy " + MypyAuditor.DefaultExpectedVersion + " (compiled: yes)\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("mypy", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "mypy" && exec.Argv[1] == "--version";

    private static async Task<string> SeedMypyFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-mypy-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(
            Path.Combine(dir, clean ? "clean.py" : "bad.py"),
            clean ? "x: int = 42\n" : "x: int = \"nope\"\n");
        return dir;
    }

    private static string? ProbeInstalledMypyVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "mypy",
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
            var match = Regex.Match(stdout, @"\d+\.\d+\.\d+[\w.\-]*");
            return process.ExitCode == 0 && match.Success ? match.Value : null;
        }
        catch
        {
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
