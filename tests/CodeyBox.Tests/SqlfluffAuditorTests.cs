using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.SqlfluffAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the sqlfluff auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming sqlfluff (never a pass or finding).
/// - Exits 0 and 1 are verdicts carrying the JSON file-results array;
///   exit 2 ("could not run": bad flags, unknown dialect, unreadable config,
///   nonexistent paths) carries no report and fails closed as infrastructure.
/// - The JSON report maps to findings with sqlfluff rule codes and file/line
///   locations (sqlfluff lines are already 1-based).
/// - The warning flag maps through the declared severity mapping — rule
///   violations block, repository-downgraded warnings are advisory — never
///   passed through raw.
/// - Inline noqa suppression is neutralized by the default --disable-noqa pin;
///   TrustRepositorySuppression can restore it.
/// - The plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_sqlfluff", "true")] skip when
///   sqlfluff is not installed.
/// </summary>
public sealed class SqlfluffAuditorTests
{
    private static readonly string? InstalledSqlfluffVersion = ProbeInstalledSqlfluffVersion();

    private const string JsonWithViolations = """
        [
          {
            "filepath": "migrations/001_add_index.sql",
            "violations": [
              {
                "start_line_no": 1,
                "start_line_pos": 7,
                "code": "LT01",
                "description": "Expected only single space before numeric literal. Found '  '.",
                "name": "layout.spacing",
                "warning": false,
                "start_file_pos": 6,
                "end_line_no": 1,
                "end_line_pos": 9,
                "end_file_pos": 8
              }
            ],
            "statistics": {},
            "timings": {}
          },
          {
            "filepath": "migrations/002_soft.sql",
            "violations": [
              {
                "start_line_no": 3,
                "start_line_pos": 1,
                "code": "CP01",
                "description": "Inconsistent capitalisation of keywords.",
                "name": "capitalisation.keywords",
                "warning": true,
                "start_file_pos": 20,
                "end_line_no": 3,
                "end_line_pos": 7,
                "end_file_pos": 26
              }
            ],
            "statistics": {},
            "timings": {}
          },
          {
            "filepath": "migrations/003_clean.sql",
            "violations": [],
            "statistics": {},
            "timings": {}
          }
        ]
        """;

    private const string JsonClean = """
        [
          {
            "filepath": "migrations/001.sql",
            "violations": [],
            "statistics": {},
            "timings": {}
          }
        ]
        """;

    private const string JsonWithFilteredPaths = """
        [
          { "filepath": "migrations/001.sql", "violations": [
            { "start_line_no": 1, "start_line_pos": 1, "code": "LT01",
              "description": "root violation", "name": "layout.spacing", "warning": false } ] },
          { "filepath": "vendor/pkg/001.sql", "violations": [
            { "start_line_no": 1, "start_line_pos": 1, "code": "LT01",
              "description": "vendored violation", "name": "layout.spacing", "warning": false } ] },
          { "filepath": "third_party/lib/001.sql", "violations": [
            { "start_line_no": 1, "start_line_pos": 1, "code": "LT01",
              "description": "third-party violation", "name": "layout.spacing", "warning": false } ] },
          { "filepath": "node_modules/pkg/001.sql", "violations": [
            { "start_line_no": 1, "start_line_pos": 1, "code": "LT01",
              "description": "dependency violation", "name": "layout.spacing", "warning": false } ] },
          { "filepath": ".git/hooks/sample.sql", "violations": [
            { "start_line_no": 1, "start_line_pos": 1, "code": "LT01",
              "description": "git-internals violation", "name": "layout.spacing", "warning": false } ] }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingSqlfluff_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "sqlfluff: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingSqlfluff()
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

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "sqlfluff, version 9.9.9\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
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

        IAuditor auditor = new SqlfluffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var error = Assert.Single(
            result.Findings, f => f.Title.Contains("LT01", StringComparison.Ordinal));
        Assert.Equal("codeybox:sqlfluff", error.AuditorName);
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("migrations/001_add_index.sql:1", error.Location);
        Assert.Contains("single space", error.Description, StringComparison.Ordinal);
        Assert.Contains("layout.spacing", error.Description, StringComparison.Ordinal);

        // The repository-downgraded CP01 (warning: true) is advisory.
        var advisory = Assert.Single(
            result.Findings, f => f.Title.Contains("CP01", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, advisory.Severity);
        Assert.Equal("migrations/002_soft.sql:3", advisory.Location);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("sqlfluff", argv[0]);
        Assert.Contains("lint", argv);
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("json", argv[formatIndex + 1]);
        var dialectIndex = argv.ToList().IndexOf("--dialect");
        Assert.True(dialectIndex >= 0 && dialectIndex + 1 < argv.Count);
        Assert.Equal("ansi", argv[dialectIndex + 1]);
        Assert.Contains("--disable-progress-bar", argv);
        // Inline noqa suppression is inert by default.
        Assert.Contains("--disable-noqa", argv);
        Assert.Contains(".", argv);
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

        IAuditor auditor = new SqlfluffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task AdvisoryOnlyFixture_Passes_WithWarningFindings()
    {
        // Exit 0 carrying warning:true violations — the repository downgraded
        // every violated rule via its warnings list (verified sqlfluff 3.4.2
        // behaviour). Still a verdict, with advisory findings.
        const string warningsOnly = """
            [
              { "filepath": "migrations/001.sql", "violations": [
                { "start_line_no": 2, "start_line_pos": 1, "code": "CP01",
                  "description": "Inconsistent capitalisation of keywords.",
                  "name": "capitalisation.keywords", "warning": true } ] }
            ]
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, warningsOnly, ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("migrations/001.sql:2", finding.Location);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJsonReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithViolations, ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_WithoutJsonReport_IsInfrastructureFailure()
    {
        // sqlfluff exits 2 for "could not run" — unknown dialect, unreadable
        // --config, nonexistent paths, usage errors — with a plain-text
        // error on stderr and no report on stdout.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                2, "", "User Error: Unknown dialect 'nosuchdialect'"));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "traceback (most recent call last): ..."));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode0_WithoutJsonReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "All Finished!\n", ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "sqlfluff: command not found"));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("sqlfluff", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsWarningFlag_NotRawValues()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithViolations, ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // warning:false (LT01) blocks; warning:true (CP01) is advisory.
        var error = Assert.Single(result.Findings, f => f.Location == "migrations/001_add_index.sql:1");
        Assert.Equal(AuditSeverity.Error, error.Severity);
        var advisory = Assert.Single(result.Findings, f => f.Location == "migrations/002_soft.sql:3");
        Assert.Equal(AuditSeverity.Warning, advisory.Severity);

        // The mapped level is surfaced in the description (tool severity
        // line), proving the value flowed through the declared mapping
        // rather than reaching the severity field raw.
        Assert.Contains("error", error.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warning", advisory.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedConfiguration_Paths_OverrideDefaultScope()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Paths"] = "migrations, queries/report.sql",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain(".", argv);
        Assert.Contains("migrations", argv);
        Assert.Contains("queries/report.sql", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_Dialect_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Dialect"] = "postgres",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var dialectIndex = argv.ToList().IndexOf("--dialect");
        Assert.True(dialectIndex >= 0 && dialectIndex + 1 < argv.Count);
        Assert.Equal("postgres", argv[dialectIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_BlankDialect_OmitsFlag_DeferringToRepositoryConfig()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Dialect"] = string.Empty,
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--dialect", scanExec!.Argv);
    }

    [Fact]
    public async Task FlagLikeOrStdinPaths_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Paths"] = "-, --dialect, migrations",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Paths", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_BecomesFlag()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/baseline/sqlfluff.cfg",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/baseline/sqlfluff.cfg", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_OmitsDisableNoqa()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.DoesNotContain("--disable-noqa", scanExec!.Argv);
    }

    [Fact]
    public async Task ExtraArguments_DialectFlag_OutranksBuiltDialect()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Dialect"] = "postgres",
                ["Scoped:ExtraArguments"] = "--dialect,snowflake",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain("postgres", argv);
        Assert.Contains("snowflake", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new SqlfluffAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("migrations/001.sql:1", finding.Location);
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

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "LT01",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("LT01", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "sqlfluff, version 3.5.0\n", ""));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new SqlfluffAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "3.5.0",
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
            s => s.PluginId == SqlfluffAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("sqlfluff", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresSqlfluffRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [SqlfluffAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SqlfluffAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("sqlfluff", tool.Binary);
        // Verify-only by design: sqlfluff is PyPI-distributed with no
        // version-pinned distro package, so the pinned release must be
        // provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("sqlfluff", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_sqlfluff", "true")]
    public async Task RealSqlfluff_LintViolationFixture_ProducesFinding()
    {
        var installed = InstalledSqlfluffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSqlfluffFixtureRepoAsync(unclean: true);

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

            var auditor = new SqlfluffAuditor();
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
                result.Findings, f => f.Title.Contains("LT01", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.StartsWith("migrations/001_unclean.sql:", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_sqlfluff", "true")]
    public async Task RealSqlfluff_CleanFixture_Passes()
    {
        var installed = InstalledSqlfluffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSqlfluffFixtureRepoAsync(unclean: false);

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

            var auditor = new SqlfluffAuditor();
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
    [Trait("requires_sqlfluff", "true")]
    public async Task RealSqlfluff_WarningsConfig_DowngradesToAdvisory()
    {
        var installed = InstalledSqlfluffVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedSqlfluffFixtureRepoAsync(unclean: true, warnings: "LT01");

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

            var auditor = new SqlfluffAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("LT01", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Warning, finding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SqlfluffAuditorPlugin.dll");
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
            PluginId: SqlfluffAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Sqlfluff SQL Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "sqlfluff, version " + SqlfluffAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("sqlfluff", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "sqlfluff" && exec.Argv[1] == "--version";

    private static async Task<string> SeedSqlfluffFixtureRepoAsync(bool unclean, string? warnings = null)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-sqlfluff-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "migrations"));

        // Unclean: double space before the literal violates LT01 on every
        // sqlfluff release carrying the rule. Clean: canonical single spaces.
        var sql = unclean
            ? "SELECT  1;\n"
            : "SELECT 1;\n";
        await File.WriteAllTextAsync(
            Path.Combine(dir, "migrations", unclean ? "001_unclean.sql" : "001_clean.sql"), sql);

        if (warnings is not null)
            await File.WriteAllTextAsync(
                Path.Combine(dir, ".sqlfluff"),
                "[sqlfluff]\ndialect = ansi\nwarnings = " + warnings + "\n");

        return dir;
    }

    private static string? ProbeInstalledSqlfluffVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sqlfluff",
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
