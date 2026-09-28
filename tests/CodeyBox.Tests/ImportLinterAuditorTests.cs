using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.ImportLinterAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Import Linter auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming lint-imports (never a pass or finding).
/// - lint-imports has only exits 0 and 1: broken contracts AND every could-not-run path share
///   exit 1, so the report summary line — not the exit code — separates verdict from failure.
/// - "Broken contracts" detail lines map to findings with the contract name as rule id and
///   module:line locations; "Warnings" entries are advisory.
/// - Raw tool severities go through the declared mapping (error -> Error, warning -> Warning).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_lint_imports", "true")].
/// </summary>
public sealed class ImportLinterAuditorTests
{
    private static readonly string? InstalledImportLinterVersion = ProbeInstalledVersion();

    // Shape mirrors real `lint-imports --no-logo --no-cache` output (2.15).
    private const string ReportBrokenWithWarning = """

        ---------
        Contracts
        ---------

        Analyzed 3 files, 2 dependencies.
        ---------------------------------

        Layering KEPT
        No low in high BROKEN (1 warning)

        Contracts: 1 kept, 1 broken.

        --------
        Warnings
        --------

        No low in high
        --------------

        - No matches for ignored import mypkg.nonexistent -> mypkg.low.


        ----------------
        Broken contracts
        ----------------

        No low in high
        --------------

        mypkg.high is not allowed to import mypkg.low:

        -   mypkg.high -> mypkg.low (l.1)


        """;

    private const string ReportClean = """

        ---------
        Contracts
        ---------

        Analyzed 3 files, 0 dependencies.
        ---------------------------------

        Layering KEPT
        No low in high KEPT

        Contracts: 2 kept, 0 broken.
        """;

    // Clean verdict plus advisory warnings: exit 0, still passes.
    private const string ReportCleanWithWarning = """

        ---------
        Contracts
        ---------

        Analyzed 3 files, 0 dependencies.
        ---------------------------------

        No low in high KEPT (1 warning)

        Contracts: 1 kept, 0 broken.

        --------
        Warnings
        --------

        No low in high
        --------------

        - No matches for ignored import mypkg.nonexistent -> mypkg.low.

        """;

    private const string ReportBrokenUnrecognisedDetails = """

        ---------
        Contracts
        ---------

        Analyzed 3 files, 2 dependencies.
        ---------------------------------

        Custom contract BROKEN

        Contracts: 0 kept, 1 broken.


        ----------------
        Broken contracts
        ----------------

        Custom contract
        ---------------

        Some free-form text a custom contract renderer produced.

        """;

    private const string ReportBrokenVendored = """

        ---------
        Contracts
        ---------

        Analyzed 4 files, 3 dependencies.
        ---------------------------------

        No vendored imports BROKEN

        Contracts: 0 kept, 1 broken.


        ----------------
        Broken contracts
        ----------------

        No vendored imports
        -------------------

        mypkg.low is not allowed to import vendor.lib:

        -   mypkg.low -> vendor.lib (l.2)
        -   vendor.lib.deep -> other.mod (l.7)


        """;

    // Exit-1 failure shapes — no report summary on any of them.
    private const string MissingConfigOutput = "\nCould not read any configuration.\n";

    private const string CouldNotRunReport = """

        Contract "No low in high" is not configured correctly:
            source_modules: This is a required field.

        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingLintImports_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "lint-imports: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lint-imports", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "import-linter 2.14\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lint-imports", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBrokenContract_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportBrokenWithWarning, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var broken = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Error);
        Assert.Equal("No low in high", BrokenRuleId(broken));
        Assert.Equal("mypkg/high:1", broken.Location);
        Assert.Contains("mypkg.high is not allowed to import mypkg.low", broken.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_BrokenContracts_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportBrokenUnrecognisedDetails, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The custom-contract block renders no import links: the per-contract
        // fallback still surfaces a finding — a BROKEN verdict never parses
        // to zero findings.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("Custom contract", BrokenRuleId(finding));
    }

    [Fact]
    public async Task ExitCode1_WithoutReport_IsInfrastructureFailure()
    {
        // "Could not read any configuration." — a could-not-run exit shares
        // exit 1 with broken contracts; only the missing summary line
        // separates the two.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, MissingConfigOutput, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lint-imports", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_InvalidContractOptions_IsInfrastructureFailure()
    {
        // The could-not-run report prints no summary line — the check did not
        // run, so this is infrastructure rather than findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, CouldNotRunReport, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode1_ContradictorySummary_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportClean, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "Error: No such option '--bogus'"));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lint-imports", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "lint-imports: command not found"));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("lint-imports", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_Applied_BrokenBlocksWarningsAdvisory_NoRawPassThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportBrokenWithWarning, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed); // the error finding blocks
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
        var warning = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.Contains("No matches for ignored import", warning.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarningsOnly_Report_Passes_WithAdvisoryFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportCleanWithWarning, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var warning = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
    }

    [Fact]
    public async Task DefaultExcludePaths_DropVendoredModuleFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportBrokenVendored, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // "vendor.lib.deep -> other.mod" lands under vendor/ and is dropped;
        // "mypkg.low -> vendor.lib" keeps its location under mypkg/.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("mypkg/low:2", finding.Location);
    }

    [Fact]
    public async Task DefaultArguments_RunNoLogoNoCache_DumbTerminal()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new ImportLinterAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("lint-imports", argv[0]);
        Assert.Contains("--no-logo", argv);
        Assert.Contains("--no-cache", argv);
        Assert.Equal("dumb", scanExec.ExtraEnvironment?["TERM"]);
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
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new ImportLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/importlinter.operator.ini",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/importlinter.operator.ini", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherContracts()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportBrokenWithWarning, ""));
        });

        var auditor = new ImportLinterAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "Layering",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // "No low in high" is not in the include set — findings drop, and
        // lint-imports' exit 1 + report still parses to an empty finding
        // list: the tool ran and produced a verdict the operator scoped out.
        Assert.Empty(result.Findings);
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
            s => s.PluginId == ImportLinterAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("lint-imports", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresLintImportsRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ImportLinterAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ImportLinterAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("lint-imports", tool.Binary);
        // Verify-only by design: import-linter ships via pip/pipx; no distro
        // apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("lint-imports", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_lint_imports", "true")]
    public async Task RealLintImports_BrokenContract_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledImportLinterVersion;
        var pythonPath = ImportLinterSitePackages;
        if (installed is null || pythonPath is null)
            return;

        var fixtureDir = await SeedImportLinterFixtureRepoAsync(broken: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    Environment = new Dictionary<string, string> { ["PYTHONPATH"] = pythonPath },
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new ImportLinterAuditor();
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
            Assert.Equal("No low in high", BrokenRuleId(finding));
            Assert.Equal("mypkg/high:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_lint_imports", "true")]
    public async Task RealLintImports_CleanFixture_Passes_AndWritesNoCache()
    {
        var installed = InstalledImportLinterVersion;
        var pythonPath = ImportLinterSitePackages;
        if (installed is null || pythonPath is null)
            return;

        var fixtureDir = await SeedImportLinterFixtureRepoAsync(broken: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    Environment = new Dictionary<string, string> { ["PYTHONPATH"] = pythonPath },
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new ImportLinterAuditor();
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
            Assert.False(Directory.Exists(Path.Combine(fixtureDir, ".import_linter_cache")));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_lint_imports", "true")]
    public async Task RealLintImports_MissingConfig_IsInfrastructureFailure()
    {
        var installed = InstalledImportLinterVersion;
        var pythonPath = ImportLinterSitePackages;
        if (installed is null || pythonPath is null)
            return;

        var fixtureDir = Path.Combine(
            Path.GetTempPath(), "codeybox-il-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    Environment = new Dictionary<string, string> { ["PYTHONPATH"] = pythonPath },
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new ImportLinterAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            // No .importlinter/pyproject.toml/setup.cfg: exit 1 without a
            // report — infrastructure, never a pass.
            await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string? BrokenRuleId(AuditFinding finding)
        => finding.Description
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("Rule: ", StringComparison.Ordinal))?[6..];

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ImportLinterAuditorPlugin.dll");
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
            PluginId: ImportLinterAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Import Linter Architecture Contracts",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "import-linter " + InstalledVersion2Part + "\n", "")
            : new SandboxExecResult(0, "", "");

    private const string InstalledVersion2Part = "2.15"; // probe output for the 2.15.0 pin

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("lint-imports", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "lint-imports" && exec.Argv[1] == "--version";

    private static async Task<string> SeedImportLinterFixtureRepoAsync(bool broken)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-il-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "mypkg", "high"));
        Directory.CreateDirectory(Path.Combine(dir, "mypkg", "low"));

        await File.WriteAllTextAsync(Path.Combine(dir, "mypkg", "__init__.py"), "");
        await File.WriteAllTextAsync(
            Path.Combine(dir, "mypkg", "high", "__init__.py"),
            broken ? "import mypkg.low\n" : "");
        await File.WriteAllTextAsync(Path.Combine(dir, "mypkg", "low", "__init__.py"), "");
        await File.WriteAllTextAsync(
            Path.Combine(dir, ".importlinter"),
            """
            [importlinter]
            root_package = mypkg
            contracts =
                forbidden

            [importlinter:contract:forbidden]
            name = No low in high
            type = forbidden
            source_modules =
                mypkg.high
            forbidden_modules =
                mypkg.low
            """);

        return dir;
    }

    private static readonly string? ImportLinterSitePackages = ProbeImportLinterSitePackages();

    /// <summary>
    /// Directory containing the importlinter package (its site-packages
    /// root). A pip --user install lands under ~/.local, which the process
    /// sandbox's remapped HOME hides — the real tests pass it as PYTHONPATH
    /// so the console script resolves its package regardless of HOME.
    /// </summary>
    private static string? ProbeImportLinterSitePackages()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import importlinter, os, sys; sys.stdout.write(os.path.dirname(os.path.dirname(importlinter.__file__)))");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            return process.ExitCode == 0 && Directory.Exists(stdout) ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ProbeInstalledVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lint-imports",
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
            var match = Regex.Match(stdout, @"\d+\.\d+(?:\.\d+)*");
            if (process.ExitCode != 0 || !match.Success)
                return null;
            // Reported "2.15" normalises to the three-part "2.15.0" pin form.
            return match.Value.Split('.').Length == 2 ? match.Value + ".0" : match.Value;
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
