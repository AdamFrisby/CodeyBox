using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.AstGrepAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the ast-grep auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming ast-grep (never a pass or finding).
/// - Exits 0 and 1 are verdicts; exit 1 is also the anyhow fallback, so an exit without a
///   parseable JSON array fails closed as infrastructure through the parser.
/// - ast-grep's typed failure exits (2 usage, 3 no project/rules, 6 read, 8 parse) are
///   infrastructure, not findings.
/// - JSON diagnostics map to findings with ruleId, file, and 1-based line (JSON is 0-based).
/// - Tool severities map through the declared mapping (error→Error, warning→Warning,
///   info/hint→Info); raw tokens never reach the severity field.
/// - Exit 0 with warning-only diagnostics still yields advisory findings and a pass.
/// - Configured Targets must exist in the worktree and not be symlinks: ast-grep reports a
///   missing target as exit 0 with an empty report, so the auditor probes for existence and
///   fails closed; a symlinked scan root would escape the worktree.
/// - The walker cannot hide code: all six --no-ignore classes are passed by default so repo
///   .gitignore/.ignore files and hidden paths stay in the crawl, and ExcludePaths doubles
///   as the crawl boundary via --globs.
/// - A repo-root sgconfig.yml declaring customLanguages/libraryPath fails closed (ast-grep
///   would dlopen repo-supplied native code into the scanner); an operator-pinned ConfigFile
///   or TrustRepositoryCustomLanguages bypasses the gate.
/// - ast-grep-ignore markers become visible Warning findings via a pre-scan grep sweep —
///   rule-scoped suppressions are silent in the report otherwise.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_astgrep", "true")] need only the
///   pinned binary — fixtures carry their own sgconfig.yml and rules, no network.
/// </summary>
public sealed class AstGrepAuditorTests
{
    private static readonly string? InstalledAstGrepVersion = ProbeInstalledAstGrepVersion();

    private const string JsonWithErrorAndWarning = """
        [
          {
            "text": "eval(\"1\")",
            "range": { "byteOffset": { "start": 25, "end": 34 },
                       "start": { "line": 1, "column": 10 },
                       "end": { "line": 1, "column": 19 } },
            "file": "src/bad.js",
            "lines": "  var x = eval(\"1\");",
            "charCount": { "leading": 10, "trailing": 1 },
            "language": "JavaScript",
            "ruleId": "no-eval",
            "severity": "error",
            "note": null,
            "message": "avoid eval"
          },
          {
            "text": "var x = eval(\"1\");",
            "range": { "byteOffset": { "start": 17, "end": 35 },
                       "start": { "line": 1, "column": 2 },
                       "end": { "line": 1, "column": 20 } },
            "file": "src/bad.js",
            "lines": "  var x = eval(\"1\");",
            "charCount": { "leading": 2, "trailing": 0 },
            "language": "JavaScript",
            "ruleId": "no-var",
            "severity": "warning",
            "note": "var is function-scoped.",
            "message": "use let/const"
          }
        ]
        """;

    private const string JsonWarningOnly = """
        [
          {
            "text": "var x = 1;",
            "range": { "byteOffset": { "start": 0, "end": 10 },
                       "start": { "line": 0, "column": 0 },
                       "end": { "line": 0, "column": 10 } },
            "file": "a.js",
            "lines": "var x = 1;",
            "charCount": { "leading": 0, "trailing": 0 },
            "language": "JavaScript",
            "ruleId": "no-var",
            "severity": "warning",
            "note": null,
            "message": "use let/const"
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWithAllSeverityLevels = """
        [
          { "ruleId": "r-error", "severity": "error", "message": "m1",
            "file": "a.js", "range": { "start": { "line": 0, "column": 0 },
            "end": { "line": 0, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } },
          { "ruleId": "r-warning", "severity": "warning", "message": "m2",
            "file": "b.js", "range": { "start": { "line": 1, "column": 0 },
            "end": { "line": 1, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } },
          { "ruleId": "r-info", "severity": "info", "message": "m3",
            "file": "c.js", "range": { "start": { "line": 2, "column": 0 },
            "end": { "line": 2, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } },
          { "ruleId": "r-hint", "severity": "hint", "message": "m4",
            "file": "d.js", "range": { "start": { "line": 3, "column": 0 },
            "end": { "line": 3, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } },
          { "ruleId": "r-future", "severity": "brand-new-level", "message": "m5",
            "file": "e.js", "range": { "start": { "line": 4, "column": 0 },
            "end": { "line": 4, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } }
        ]
        """;

    private const string JsonWithVendoredFindings = """
        [
          { "ruleId": "no-eval", "severity": "error", "message": "root eval",
            "file": "src/app.js", "range": { "start": { "line": 0, "column": 0 },
            "end": { "line": 0, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } },
          { "ruleId": "no-eval", "severity": "error", "message": "vendored eval",
            "file": "vendor/lib/x.js", "range": { "start": { "line": 0, "column": 0 },
            "end": { "line": 0, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } },
          { "ruleId": "no-eval", "severity": "error", "message": "generated eval",
            "file": "dist/bundle.js", "range": { "start": { "line": 0, "column": 0 },
            "end": { "line": 0, "column": 1 }, "byteOffset": { "start": 0, "end": 1 } } }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingAstGrep_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingAstGrep()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "ast-grep: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "ast-grep 0.44.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.44.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(AstGrepAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithErrorDiagnostic_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithErrorAndWarning, "Error: 1 error(s) found in code."));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var error = Assert.Single(
            result.Findings, f => f.Title.Contains("no-eval", StringComparison.Ordinal));
        Assert.Equal("codeybox:ast-grep", error.AuditorName);
        Assert.Equal(AuditSeverity.Error, error.Severity);
        // ast-grep JSON positions are 0-based; the finding carries the 1-based line.
        Assert.Equal("src/bad.js:2", error.Location);
        Assert.Contains("avoid eval", error.Description, StringComparison.Ordinal);
        Assert.Contains("ast-grep", error.Description, StringComparison.Ordinal);

        var warning = Assert.Single(
            result.Findings, f => f.Title.Contains("no-var", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("src/bad.js:2", warning.Location);
        Assert.Contains("var is function-scoped.", warning.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("ast-grep", argv[0]);
        Assert.Equal("scan", argv[1]);
        Assert.Contains("--json=compact", argv);
        Assert.Contains("--error=no-suppress-all", argv);
        // Default scope: whole work tree, last positional argument.
        Assert.Equal(".", argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task WarningOnlyDiagnostics_Exit0_AreFindings_ButPass()
    {
        // ast-grep exits 0 when no error-severity diagnostic fired — the
        // JSON array can still carry warning/info/hint findings, and the
        // gate is severity-driven, not "any finding fails".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(0, JsonWarningOnly, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("a.js:1", finding.Location);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        // Exit 1 is also ast-grep's anyhow fallback: run failures print
        // "Error: …" to stderr and write no report — "could not run" must not
        // be mistaken for "found problems".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(1, "", "Error: task join failure"));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]   // clap usage error
    [InlineData(3)]   // no project config / no rules
    [InlineData(6)]   // config or rule read failure
    [InlineData(8)]   // config or rule parse failure
    public async Task TypedFailureExits_AreInfrastructureFailures(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(exitCode, "", "Error: could not run"));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("ast-grep", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_RawTokensNeverInSeverity()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(1, JsonWithAllSeverityLevels, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            result.Findings.Single(f => f.Location == "a.js:1").Severity);
        Assert.Equal(AuditSeverity.Warning,
            result.Findings.Single(f => f.Location == "b.js:2").Severity);
        Assert.Equal(AuditSeverity.Info,
            result.Findings.Single(f => f.Location == "c.js:3").Severity);
        Assert.Equal(AuditSeverity.Info,
            result.Findings.Single(f => f.Location == "d.js:4").Severity);
        // An unrecognized level from a foreign build maps to the declared
        // default (Warning), never passed through.
        var future = result.Findings.Single(f => f.Location == "e.js:5");
        Assert.Equal(AuditSeverity.Warning, future.Severity);
        // The raw tool token is preserved in the description — proof the
        // value flowed through the mapping rather than the severity field.
        Assert.Contains("brand-new-level", future.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultScope_DropsVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(1, JsonWithVendoredFindings, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/app.js:1", finding.Location);
    }

    [Fact]
    public async Task ConfigFile_And_RuleFile_BothSet_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "sgconfig.yml",
                ["Scoped:RuleFile"] = "rules/one.yml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ConfigFile", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigFile_BecomesConfigArgument()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/opt/sg/sgconfig.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var index = argv.ToList().IndexOf("--config");
        Assert.True(index >= 0 && argv[index + 1] == "/opt/sg/sgconfig.yml");
        Assert.DoesNotContain("--rule", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_RuleFile_BecomesRuleArgument()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:RuleFile"] = "/opt/sg/rules/no-eval.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var index = argv.ToList().IndexOf("--rule");
        Assert.True(index >= 0 && argv[index + 1] == "/opt/sg/rules/no-eval.yml");
        Assert.DoesNotContain("--config", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_OverrideDefaultScope_AndAreProbed()
    {
        SandboxExec? scanExec = null;
        var targetProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRepositoryFileProbe(exec) && !exec.Argv.Contains("sgconfig.yml", StringComparer.Ordinal))
            {
                targetProbes++;
                // The presence script echoes each existing path, one per line.
                return Task.FromResult(new SandboxExecResult(0, "src\nrules\n", ""));
            }
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "src, rules",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain(".", argv.Skip(1));
        Assert.Equal("src", argv[^2]);
        Assert.Equal("rules", argv[^1]);
        Assert.Equal(1, targetProbes);
    }

    [Fact]
    public async Task ScopedConfiguration_MissingTarget_IsDeterministicInfrastructure()
    {
        // ast-grep answers a nonexistent target with exit 0 and an empty
        // report — a silent pass. The auditor probes first and fails closed.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src\n", ""));
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "src, nonexistent-dir",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("nonexistent-dir", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("/abs/path")]
    [InlineData("../escape")]
    [InlineData("-c")]
    public async Task ScopedConfiguration_NonRelativeTargets_AreDeterministicInfrastructure(string target)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = target,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(1, JsonWithErrorAndWarning, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "no-eval",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("no-eval", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanArgv_NeutralizesRepositoryIgnoreFiles_ByDefault()
    {
        // The audit subject owns .gitignore/.ignore and dot-directories —
        // without --no-ignore the walker would silently skip hidden or
        // ignored code and report an empty pass.
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        foreach (var ignoreClass in new[] { "hidden", "dot", "exclude", "global", "parent", "vcs" })
            Assert.Contains("--no-ignore=" + ignoreClass, argv);
        Assert.Contains("--error=" + AstGrepAuditor.NoSuppressAllRuleId, argv);
        // ExcludePaths double as the crawl boundary under --no-ignore.
        Assert.Contains("!.git/**", argv);
        Assert.Contains("!vendor/**", argv);
        Assert.Contains("!node_modules/**", argv);
    }

    [Fact]
    public async Task TrustRepositorySuppression_DropsNoIgnoreFlagsAndMarkerSweep()
    {
        var grepExecs = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsGrepProbe(exec))
            {
                grepExecs++;
                return Task.FromResult(new SandboxExecResult(0, "src/ignored.js\n", ""));
            }
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        Assert.NotNull(scanExec);
        Assert.DoesNotContain(scanExec!.Argv, a => a.StartsWith("--no-ignore", StringComparison.Ordinal));
        Assert.DoesNotContain("--error=" + AstGrepAuditor.NoSuppressAllRuleId, scanExec.Argv);
        // The suppression-marker sweep is part of the trusted surface —
        // under the opt-in it does not run at all.
        Assert.Equal(0, grepExecs);
    }

    [Fact]
    public async Task RepoSgconfig_WithCustomLanguages_IsDeterministicInfrastructure_NeverAPass()
    {
        // A repo-root sgconfig.yml declaring customLanguages makes ast-grep
        // dlopen a repository-pathed native library — code the audit subject
        // controls, which could also emit a clean report. Fail closed.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "sgconfig.yml\n", ""));
            if (IsConfigContentProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("customLanguages", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepoSgconfig_WithoutDynamicLoadKeys_Scans()
    {
        var configProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "sgconfig.yml\n", ""));
            if (IsConfigContentProbe(exec))
            {
                configProbes++;
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            }
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(1, configProbes);
    }

    [Fact]
    public async Task RepoSgconfig_DynamicLoadKeys_TrustedViaOptIn_ScansWithoutConfigProbe()
    {
        var configProbes = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsConfigContentProbe(exec))
            {
                configProbes++;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryCustomLanguages"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, configProbes);
    }

    [Fact]
    public async Task OperatorPinnedConfigFile_SkipsRepositoryConfigGate()
    {
        // -c/--config replaces sgconfig discovery entirely, so a repo-root
        // sgconfig.yml is never loaded — the gate does not run.
        var configProbes = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRepositoryFileProbe(exec) && exec.Argv.Contains("sgconfig.yml", StringComparer.Ordinal))
            {
                configProbes++;
                return Task.FromResult(new SandboxExecResult(0, "sgconfig.yml\n", ""));
            }
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/opt/sg/sgconfig.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(0, configProbes);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task SuppressionMarkers_BecomeVisibleWarningFindings()
    {
        // Rule-scoped ast-grep-ignore suppressions leave no trace in the JSON
        // report — the pre-scan sweep surfaces each file carrying the marker
        // so a clean report cannot silently mean "suppressed".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsMarkerProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src/hidden.js\n./lib/x.js\n", ""));
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, result.Findings.Count);
        var hidden = result.Findings.Single(f => f.Location == "src/hidden.js");
        Assert.Equal(AuditSeverity.Warning, hidden.Severity);
        Assert.Contains(AstGrepAuditor.SuppressionSiteRuleId, hidden.Title, StringComparison.Ordinal);
        Assert.Equal("lib/x.js", result.Findings.Single(f => f.Location != "src/hidden.js").Location);
    }

    [Fact]
    public async Task SuppressionMarkerSweep_Failure_IsInfrastructureFailure()
    {
        // grep exits 2 on unreadable input — "could not confirm" is never
        // evidence that no suppression directives exist.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsMarkerProbe(exec))
                return Task.FromResult(new SandboxExecResult(2, "", "grep: read error"));
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new AstGrepAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("suppression-marker", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SymlinkedTarget_IsDeterministicInfrastructure()
    {
        // ast-grep follows a symlinked scan root even without --follow —
        // a repo-committed link would point the crawl outside the worktree.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsRepositoryFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src\n", ""));
            if (IsSymlinkProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "src\n", ""));
            if (ProbeAnswer(exec) is { } probe)
                return Task.FromResult(probe);
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new AstGrepAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "src",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("symlink", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
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
        // Verify-only by design: no distro package carries ast-grep, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("ast-grep", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_astgrep", "true")]
    public async Task RealAstGrep_ViolationFixture_ProducesFinding()
    {
        var installed = InstalledAstGrepVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedAstGrepFixtureRepoAsync(violation: true);

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

            var auditor = new AstGrepAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("no-eval", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("src/bad.js:2", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_astgrep", "true")]
    public async Task RealAstGrep_CleanFixture_Passes()
    {
        var installed = InstalledAstGrepVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedAstGrepFixtureRepoAsync(violation: false);

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

            var auditor = new AstGrepAuditor();
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
    [Trait("requires_astgrep", "true")]
    public async Task RealAstGrep_MissingProjectConfig_IsInfrastructureFailure()
    {
        var installed = InstalledAstGrepVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(
            Path.GetTempPath(), "codeybox-astgrep-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);
        await File.WriteAllTextAsync(Path.Combine(fixtureDir, "a.js"), "var x = 1;\n");

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

            var auditor = new AstGrepAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            // No sgconfig.yml and no RuleFile: ast-grep exits 3 — "could not
            // run" is infrastructure, never a pass and never a finding.
            await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_astgrep", "true")]
    public async Task RealAstGrep_SuppressAllComment_IsBlockingFinding()
    {
        var installed = InstalledAstGrepVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedAstGrepFixtureRepoAsync(violation: false);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDir, "src", "suppressed.js"),
                "function f() {\n  var x = eval(\"1\"); // ast-grep-ignore\n  return x;\n}\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new AstGrepAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.Findings,
                f => f.Title.Contains(AstGrepAuditor.NoSuppressAllRuleId, StringComparison.Ordinal)
                    && f.Severity == AuditSeverity.Error);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
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
            PluginDisplayName: "CodeyBox: ast-grep Structural Patterns",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    /// <summary>
    /// Benign answers for every pre-scan probe the auditor issues — presence,
    /// version, repository-file presence (nothing present), symlink check (no
    /// links), and the grep probes (no matches). Returns null for the scan
    /// itself, so a test only scripts the execs it cares about.
    /// </summary>
    private static SandboxExecResult? ProbeAnswer(SandboxExec exec)
    {
        if (IsPresenceProbe(exec) || IsVersionProbe(exec))
            return Ok(exec);
        if (IsRepositoryFileProbe(exec) || IsSymlinkProbe(exec))
            return new SandboxExecResult(0, "", "");
        if (IsGrepProbe(exec))
            return new SandboxExecResult(1, "", "");
        return null;
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "ast-grep " + AstGrepAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("ast-grep", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "ast-grep" && exec.Argv[1] == "--version";

    // The shared presence script echoes existing (-e or -L) repo-relative
    // paths, one per line.
    private static bool IsRepositoryFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in \"$@\"", StringComparison.Ordinal)
            && exec.Argv[2].Contains("[ -e", StringComparison.Ordinal);

    // The auditor's sibling probe echoes only symlinked (-L) paths; it shares
    // the "for f in $@" shape but never tests -e.
    private static bool IsSymlinkProbe(SandboxExec exec)
        => exec.Argv.Count >= 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in \"$@\"", StringComparison.Ordinal)
            && exec.Argv[2].Contains("[ -L", StringComparison.Ordinal)
            && !exec.Argv[2].Contains("[ -e", StringComparison.Ordinal);

    private static bool IsGrepProbe(SandboxExec exec)
        => exec.Argv.Count >= 3 && exec.Argv[0] == "grep";

    // Content check for dynamic-load keys in the repo's sgconfig.yml.
    private static bool IsConfigContentProbe(SandboxExec exec)
        => IsGrepProbe(exec) && exec.Argv.Contains("-qEe", StringComparer.Ordinal);

    // Suppression-marker sweep over the scan targets.
    private static bool IsMarkerProbe(SandboxExec exec)
        => IsGrepProbe(exec) && exec.Argv.Contains("ast-grep-ignore", StringComparer.Ordinal);

    private static async Task<string> SeedAstGrepFixtureRepoAsync(bool violation)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-astgrep-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        await File.WriteAllTextAsync(
            Path.Combine(dir, "sgconfig.yml"),
            "ruleDirs:\n  - rules\n");
        await File.WriteAllTextAsync(
            Path.Combine(dir, "rules", "no-eval.yml"),
            "id: no-eval\nlanguage: JavaScript\nseverity: error\nmessage: avoid eval\nrule:\n  pattern: eval($ARG)\n");
        await File.WriteAllTextAsync(
            Path.Combine(dir, "src", "bad.js"),
            violation
                ? "function f() {\n  var x = eval(\"1\");\n  return x;\n}\n"
                : "function f() {\n  let x = 1;\n  return x;\n}\n");

        return dir;
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
            // Drain both streams concurrently: a full stderr pipe would block
            // the child on write while stdout stays open, deadlocking the
            // synchronous read ahead of the timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromSeconds(5));
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
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
