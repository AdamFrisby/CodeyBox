using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.SquawkAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the squawk auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming squawk (never a pass or finding).
/// - Exits 0 and 1 are verdicts only when stdout carries the JSON violations array —
///   squawk exits 1 for violations AND for run failures, so the report discriminates
///   and "could not run" fails closed as infrastructure.
/// - The JSON array maps to findings with squawk rule ids and file/line locations
///   (squawk's 0-based line converted to 1-based).
/// - squawk levels (Warning/Error) map through the declared severity mapping —
///   never passed through.
/// - Repository .squawk.toml is neutralized by the default --config /dev/null pin;
///   ConfigPath/TrustRepositoryConfig/ExtraArguments can restore it.
/// - The /dev/null sentinel keeps squawk off its stdin branch when no files match.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_squawk", "true")] skip when
///   squawk is not installed.
/// </summary>
public sealed class SquawkAuditorTests
{
    private static readonly string? InstalledSquawkVersion = ProbeInstalledSquawkVersion();

    private const string JsonWithViolations = """
        [
          {
            "file": "db/migrations/002_add_constraint.sql",
            "line": 1,
            "column": 24,
            "level": "Warning",
            "message": "By default new constraints require a table scan and block writes to the table while that scan occurs.",
            "help": "Use `NOT VALID` with a later `VALIDATE CONSTRAINT` call.",
            "rule_name": "constraint-missing-not-valid",
            "column_end": 60,
            "line_end": 1
          },
          {
            "file": "db/migrations/003_bad.sql",
            "line": 0,
            "column": 36,
            "level": "Error",
            "message": "missing comma",
            "help": null,
            "rule_name": "syntax-error",
            "column_end": 36,
            "line_end": 0
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWithLevels = """
        [
          { "file": "a.sql", "line": 0, "column": 0, "level": "Warning",
            "message": "warning-level violation", "help": null, "rule_name": "ban-drop-table",
            "column_end": 1, "line_end": 0 },
          { "file": "b.sql", "line": 2, "column": 4, "level": "Error",
            "message": "syntax problem", "help": null, "rule_name": "syntax-error",
            "column_end": 5, "line_end": 2 },
          { "file": "c.sql", "line": 0, "column": 0, "level": "FutureLevel",
            "message": "level this parser predates", "help": null, "rule_name": "future-rule",
            "column_end": 1, "line_end": 0 }
        ]
        """;

    private const string JsonWithFilteredPaths = """
        [
          { "file": "migrations/001.sql", "line": 0, "column": 0, "level": "Warning",
            "message": "root violation", "help": null, "rule_name": "ban-drop-table",
            "column_end": 1, "line_end": 0 },
          { "file": "vendor/pkg/001.sql", "line": 0, "column": 0, "level": "Warning",
            "message": "vendored violation", "help": null, "rule_name": "ban-drop-table",
            "column_end": 1, "line_end": 0 },
          { "file": "third_party/lib/001.sql", "line": 0, "column": 0, "level": "Warning",
            "message": "third-party violation", "help": null, "rule_name": "ban-drop-table",
            "column_end": 1, "line_end": 0 },
          { "file": "node_modules/pkg/001.sql", "line": 0, "column": 0, "level": "Warning",
            "message": "dependency violation", "help": null, "rule_name": "ban-drop-table",
            "column_end": 1, "line_end": 0 },
          { "file": ".git/hooks/sample.sql", "line": 0, "column": 0, "level": "Warning",
            "message": "git-internals violation", "help": null, "rule_name": "ban-drop-table",
            "column_end": 1, "line_end": 0 }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingSquawk_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "squawk: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingSquawk()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(2, "", "unexpected argument"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "squawk 2.52.1\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.52.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SquawkAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
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
    public async Task Fixture_WithViolations_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithViolations, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var warning = Assert.Single(
            result.Findings, f => f.Title.Contains("constraint-missing-not-valid", StringComparison.Ordinal));
        Assert.Equal("codeybox:squawk", warning.AuditorName);
        Assert.Equal(AuditSeverity.Error, warning.Severity);
        // squawk's 0-based line 1 is the file's 1-based line 2.
        Assert.Equal("db/migrations/002_add_constraint.sql:2", warning.Location);
        Assert.Contains("NOT VALID", warning.Description, StringComparison.Ordinal);

        var syntax = Assert.Single(
            result.Findings, f => f.Title.Contains("syntax-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, syntax.Severity);
        Assert.Equal("db/migrations/003_bad.sql:1", syntax.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("squawk", argv[0]);
        var reporterIndex = argv.ToList().IndexOf("--reporter");
        Assert.True(reporterIndex >= 0 && reporterIndex + 1 < argv.Count);
        Assert.Equal("json", argv[reporterIndex + 1]);
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/dev/null", argv[configIndex + 1]);
        Assert.Contains("**/*.sql", argv);
        // Empty-file sentinel keeps squawk off its stdin branch — last arg.
        Assert.Equal("/dev/null", argv[^1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJsonReport_ReportsFindings_AndFails()
    {
        // squawk exits 1 (not the usual separate code) for "ran with violations".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithViolations, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        // squawk also exits 1 for "could not run" — glob errors, unreadable
        // files, config parse errors — with a plain-text error on stderr and
        // no report on stdout.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "Failed to find files: glob pattern error"));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode0_WithoutJsonReport_IsInfrastructureFailure()
    {
        // squawk exits 0 with plain-text help on stdout for the no-input
        // help/none branches — not a verdict, so it fails closed.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                0, "Find problems in your SQL\n\nUsage: squawk [OPTIONS] [path]...", ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        // clap usage errors exit 2 — never a verdict.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                2, "", "error: unexpected argument '--bogus' found"));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "squawk: command not found"));
        });

        IAuditor auditor = new SquawkAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("squawk", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsSquawkLevelsToError_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithLevels, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Warning, Error, and the unrecognized FutureLevel all map to Error —
        // squawk's levels are not advisory severities, and an unknown level
        // fails closed through the declared default.
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.Contains(result.Findings, f => f.Location == "a.sql:1");
        Assert.Contains(result.Findings, f => f.Location == "b.sql:3");
        Assert.Contains(result.Findings, f => f.Location == "c.sql:1");

        // The raw tool level is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than the
        // severity field itself.
        var warning = Assert.Single(result.Findings, f => f.Location == "a.sql:1");
        Assert.Contains("Warning", warning.Description, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, warning.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_Patterns_OverrideDefaultScope()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Patterns"] = "db/migrations/*.sql, db/schema.sql",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain("**/*.sql", argv);
        Assert.Contains("db/migrations/*.sql", argv);
        Assert.Contains("db/schema.sql", argv);
        Assert.Equal("/dev/null", argv[^1]);
    }

    [Fact]
    public async Task FlagLikeOrSubcommandPatterns_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Patterns"] = "server, -e, migrations/*.sql",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Patterns", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_OverridesInertPin()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = ".squawk.toml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal(".squawk.toml", argv[configIndex + 1]);
        Assert.DoesNotContain("/dev/null", argv.Take(argv.Count - 1));
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositoryConfig_OmitsConfigFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositoryConfig"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--config", scanExec!.Argv);
    }

    [Fact]
    public async Task ExtraArguments_ConfigFlag_OutranksInertPin()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--config,/etc/codeybox/squawk.toml,--verbose",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/etc/codeybox/squawk.toml", argv[configIndex + 1]);
        Assert.Equal(1, argv.Count(a => a == "--config"));
        Assert.Contains("--verbose", argv);
        // The inert config pin was suppressed; /dev/null remains only as the
        // sentinel pattern (ExtraArguments append after it).
        Assert.Single(argv, a => a == "/dev/null");
    }

    [Fact]
    public async Task ScopedConfiguration_PgVersion_AndTransactionAssumption_BecomeFlags()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:PgVersion"] = "16.4",
                ["Scoped:AssumeInTransaction"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var pgIndex = argv.ToList().IndexOf("--pg-version");
        Assert.True(pgIndex >= 0 && argv[pgIndex + 1] == "16.4");
        Assert.Contains("--assume-in-transaction", argv);
        Assert.DoesNotContain("--no-assume-in-transaction", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_AssumeInTransactionFalse_EmitsNegatedFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:AssumeInTransaction"] = "false",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--no-assume-in-transaction", scanExec!.Argv);
        Assert.DoesNotContain("--assume-in-transaction", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_InvalidAssumeInTransaction_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:AssumeInTransaction"] = "maybe",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("AssumeInTransaction", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGitFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new SquawkAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("migrations/001.sql:1", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithViolations, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "constraint-missing-not-valid",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("constraint-missing-not-valid", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "squawk 2.65.0\n", ""));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SquawkAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "2.65.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
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
            s => s.PluginId == SquawkAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("squawk", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresSquawkRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SquawkAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SquawkAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("squawk", tool.Binary);
        // Verify-only by design: no distro package carries squawk, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("squawk", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_squawk", "true")]
    public async Task RealSquawk_UnsafeMigrationFixture_ProducesFinding()
    {
        var installed = InstalledSquawkVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSquawkFixtureRepoAsync(safe: false);

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

            var auditor = new SquawkAuditor();
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
                result.Findings, f => f.Title.Contains("adding-required-field", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.StartsWith("migrations/001_unsafe.sql:", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_squawk", "true")]
    public async Task RealSquawk_BenignMigrationFixture_Passes()
    {
        var installed = InstalledSquawkVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSquawkFixtureRepoAsync(safe: true);

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

            var auditor = new SquawkAuditor();
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

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SquawkAuditorPlugin.dll");
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
            PluginId: SquawkAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Squawk PostgreSQL Migrations",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "squawk " + SquawkAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("squawk", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "squawk" && exec.Argv[1] == "--version";

    private static async Task<string> SeedSquawkFixtureRepoAsync(bool safe)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-squawk-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "migrations"));

        // Safe: a SELECT violates no migration-safety rule. Unsafe: NOT NULL
        // column without a default on an existing table — squawk reports
        // adding-required-field (among others).
        var sql = safe
            ? "SELECT 1;\n"
            : "ALTER TABLE users ADD COLUMN email text NOT NULL;\n";
        await File.WriteAllTextAsync(
            Path.Combine(dir, "migrations", safe ? "001_safe.sql" : "001_unsafe.sql"), sql);

        return dir;
    }

    private static string? ProbeInstalledSquawkVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "squawk",
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
