using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.PmdAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the PMD auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming pmd (never a pass or finding).
/// - Exit codes 0 (clean) and 4 (violations) are findings-producing verdicts; 1 (exception),
///   2 (usage error), 5 (recoverable errors — partial coverage by contract) and anything
///   else are infrastructure.
/// - PMD XML on stdout maps to findings with rule ids, locations, and priority-mapped severity.
/// - Severity mapping is declared and applied (priorities 1–5 plus the parser's synthetic
///   processing-error/config-error tokens); raw tool levels never pass through.
/// - Default flags: XML report, quickstart ruleset, whole-worktree scan, repo-relative paths
///   (-z with the absolute worktree), NOPMD neutralisation; scoped knobs (ExpectedVersion,
///   RulesetPath, TrustRepositorySuppression, ExtraArguments, ExcludePaths).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled; when
///   enabled it declares pmd (no apt package — distros ship PMD 6) and the java runtime.
/// - Real binary execution tests under [Trait("requires_pmd", "true")].
/// </summary>
public sealed class PmdAuditorTests
{
    private static readonly string? InstalledPmdVersion = ProbeInstalledPmdVersion();

    private const string WorkDir = "/work";

    // Shape mirrors real `pmd check -d . -f xml -z /work` output (7.26.0):
    // the XML report arrives on stdout; slf4j logs stay on stderr.
    private const string VersionBanner = """
          ████                            ████
          ██                                ██
        PMD {0} (0123abcd, 2026-06-29T09:00:00Z)
        Java version: 21.0.3, vendor: Eclipse Adoptium, runtime: /usr/lib/jvm/temurin-21
        """;

    private const string XmlClean = """
        <?xml version="1.0" encoding="UTF-8"?>
        <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0"
             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
             xsi:schemaLocation="http://pmd.sourceforge.net/report/2.0.0 https://pmd.github.io/schema/report_2_0_0.xsd"
             version="7.26.0" timestamp="2026-09-27T12:00:00.000">
        </pmd>
        """;

    private const string XmlAdvisoryOnly = """
        <?xml version="1.0" encoding="UTF-8"?>
        <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0"
             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
             xsi:schemaLocation="http://pmd.sourceforge.net/report/2.0.0 https://pmd.github.io/schema/report_2_0_0.xsd"
             version="7.26.0" timestamp="2026-09-27T12:00:00.000">
        <file name="src/Foo.java">
        <violation beginline="3" endline="3" begincolumn="9" endcolumn="10" rule="UnusedLocalVariable" ruleset="Best Practices" package="com.example" class="Foo" method="bar" variable="x" externalInfoUrl="https://docs.pmd-code.org/pmd-doc-7.26.0/pmd_rules_java_bestpractices.html#unusedlocalvariable" priority="3">
        Avoid unused local variables such as &apos;x&apos;.
        </violation>
        </file>
        </pmd>
        """;

    private const string XmlWithFindings = """
        <?xml version="1.0" encoding="UTF-8"?>
        <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0"
             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
             xsi:schemaLocation="http://pmd.sourceforge.net/report/2.0.0 https://pmd.github.io/schema/report_2_0_0.xsd"
             version="7.26.0" timestamp="2026-09-27T12:00:00.000">
        <file name="src/Foo.java">
        <violation beginline="3" endline="3" begincolumn="9" endcolumn="10" rule="UnusedLocalVariable" ruleset="Best Practices" package="com.example" class="Foo" method="bar" variable="x" externalInfoUrl="https://docs.pmd-code.org/pmd-doc-7.26.0/pmd_rules_java_bestpractices.html#unusedlocalvariable" priority="3">
        Avoid unused local variables such as &apos;x&apos;.
        </violation>
        </file>
        <file name="src/Bad.java">
        <violation beginline="7" endline="9" begincolumn="5" endcolumn="5" rule="EmptyCatchBlock" ruleset="Error Prone" externalInfoUrl="https://docs.pmd-code.org/pmd-doc-7.26.0/pmd_rules_java_errorprone.html#emptycatchblock" priority="1">
        Avoid empty catch blocks.
        </violation>
        </file>
        </pmd>
        """;

    private const string XmlAllPriorities = """
        <?xml version="1.0" encoding="UTF-8"?>
        <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0" version="7.26.0">
        <file name="src/A.java">
        <violation beginline="1" endline="1" begincolumn="1" endcolumn="1" rule="P1Rule" ruleset="Error Prone" priority="1">p1</violation>
        <violation beginline="2" endline="2" begincolumn="1" endcolumn="1" rule="P2Rule" ruleset="Error Prone" priority="2">p2</violation>
        <violation beginline="3" endline="3" begincolumn="1" endcolumn="1" rule="P3Rule" ruleset="Code Style" priority="3">p3</violation>
        <violation beginline="4" endline="4" begincolumn="1" endcolumn="1" rule="P4Rule" ruleset="Code Style" priority="4">p4</violation>
        <violation beginline="5" endline="5" begincolumn="1" endcolumn="1" rule="P5Rule" ruleset="Code Style" priority="5">p5</violation>
        </file>
        </pmd>
        """;

    private const string XmlVendoredPaths = """
        <?xml version="1.0" encoding="UTF-8"?>
        <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0" version="7.26.0">
        <file name="src/App.java">
        <violation beginline="3" endline="3" begincolumn="1" endcolumn="1" rule="UnusedLocalVariable" ruleset="Best Practices" priority="3">unused</violation>
        </file>
        <file name="vendor/lib/Dep.java">
        <violation beginline="1" endline="1" begincolumn="1" endcolumn="1" rule="UnusedLocalVariable" ruleset="Best Practices" priority="3">unused</violation>
        </file>
        <file name="target/generated-sources/Gen.java">
        <violation beginline="1" endline="1" begincolumn="1" endcolumn="1" rule="UnusedLocalVariable" ruleset="Best Practices" priority="3">unused</violation>
        </file>
        </pmd>
        """;

    // Shape of a run under operator-supplied --no-fail-on-error: exit stays
    // 0/4 and partial-coverage gaps are report elements, not the exit code.
    private const string XmlWithProcessingAndConfigErrors = """
        <?xml version="1.0" encoding="UTF-8"?>
        <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0" version="7.26.0">
        <file name="src/Foo.java">
        <violation beginline="3" endline="3" begincolumn="9" endcolumn="10" rule="UnusedLocalVariable" ruleset="Best Practices" priority="3">unused</violation>
        </file>
        <error filename="src/Broken.java" msg="PMDException: Error while parsing src/Broken.java"><![CDATA[stack detail]]></error>
        <configerror rule="GhostRule" msg="Unable to find referenced rule GhostRule"/>
        <suppressedviolation filename="src/Supp.java" suppressiontype="nopmd" msg="suppressed" usermsg=""/>
        </pmd>
        """;

    [Fact]
    public async Task MissingBinary_YieldsInfrastructureFailure_NamingPmd_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "command -v: pmd not found"));
            throw new InvalidOperationException("scan must not run when the presence probe fails");
        });

        IAuditor auditor = new PmdAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None));

        // A scanner that silently passes because it did not run is the worst
        // outcome: this throws (infrastructure) instead, naming pmd.
        Assert.Contains("pmd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongVersion_YieldsInfrastructureFailure_NamingPmd()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(
                    0, "PMD 6.55.0 (abc, 2023-01-01)\nJava version: 17.0.9\n", ""));
            throw new InvalidOperationException("scan must not run on a version mismatch");
        });

        IAuditor auditor = new PmdAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None));

        Assert.Contains("pmd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyFixture_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, XmlWithFindings, ""));
        });

        IAuditor auditor = new PmdAuditor();
        var result = await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        // Priority 1 (EmptyCatchBlock) maps to Error and blocks; priority 3
        // (UnusedLocalVariable) is advisory.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var unused = Assert.Single(
            result.Findings, f => f.Title.Contains("UnusedLocalVariable", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unused.Severity);
        Assert.Equal("src/Foo.java:3", unused.Location);

        var emptyCatch = Assert.Single(
            result.Findings, f => f.Title.Contains("EmptyCatchBlock", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, emptyCatch.Severity);
        Assert.Equal("src/Bad.java:7", emptyCatch.Location);
    }

    [Fact]
    public async Task AdvisoryOnlyFindings_Pass_WhenNoBlockingSeverity()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, XmlAdvisoryOnly, ""));
        });

        IAuditor auditor = new PmdAuditor();
        var result = await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("src/Foo.java:3", finding.Location);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, XmlClean, ""));
        });

        IAuditor auditor = new PmdAuditor();
        var result = await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitFive_IsInfrastructure_EvenWithXmlReport()
    {
        // 5 = recoverable errors: the report is partial coverage by PMD's own
        // contract — "could not fully run" fails closed, never a verdict.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(5, XmlWithFindings, "PMDException: Error while parsing src/Broken.java"));
        });

        IAuditor auditor = new PmdAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None));

        Assert.Contains("pmd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExceptionAndUsageErrorExits_AreInfrastructure(int exitCode)
    {
        // 1 = exception during execution; 2 = usage error (e.g. missing -R):
        // "could not run" regardless of what stdout carries.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", "pmd: usage error"));
        });

        IAuditor auditor = new PmdAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None));

        Assert.Contains("pmd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerdictExit_WithoutXmlReport_IsInfrastructure()
    {
        // Exit 0/4 with no report (e.g. an operator --report-file redirect)
        // fails closed — silence is not a pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, "", "running PMD"));
        });

        IAuditor auditor = new PmdAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None));

        Assert.Contains("pmd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeverityMapping_Applies_AllFivePriorities()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, XmlAllPriorities, ""));
        });

        IAuditor auditor = new PmdAuditor();
        var result = await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        // Raw tool severities never pass through: every finding carries a
        // CodeyBox severity from the declared map.
        Assert.Equal(5, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error, SeverityOf(result, "P1Rule"));
        Assert.Equal(AuditSeverity.Error, SeverityOf(result, "P2Rule"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "P3Rule"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "P4Rule"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "P5Rule"));
        Assert.False(result.Passed);

        static AuditSeverity SeverityOf(AuditResult r, string rule)
            => Assert.Single(r.Findings, f => f.Title.Contains(rule, StringComparison.Ordinal)).Severity;
    }

    [Fact]
    public async Task ProcessingAndConfigErrors_Surface_AsAdvisoryFindings()
    {
        // Under operator --no-fail-on-error the exit stays verdict (0/4) and
        // partial-coverage gaps arrive as report elements — surfaced as
        // advisory findings, never silently dropped.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, XmlWithProcessingAndConfigErrors, ""));
        });

        IAuditor auditor = new PmdAuditor();
        var result = await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        // Violation + processing error + config error; the suppressed
        // violation element is never a finding.
        Assert.Equal(3, result.Findings.Count);
        Assert.True(result.Passed);

        var processingError = Assert.Single(
            result.Findings, f => f.Location == "src/Broken.java");
        Assert.Equal(AuditSeverity.Warning, processingError.Severity);
        Assert.Contains("Error while parsing", processingError.Title, StringComparison.Ordinal);

        var configError = Assert.Single(
            result.Findings, f => f.Title.Contains("GhostRule", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, configError.Severity);
    }

    [Fact]
    public async Task BuiltArguments_SelectXmlReport_QuickstartRuleset_AndNeutralizedSuppression()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, XmlClean, ""));
        });

        IAuditor auditor = new PmdAuditor();
        await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("pmd", argv[0]);
        Assert.Equal("check", argv[1]);
        var formatIndex = argv.ToList().IndexOf("-f");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("xml", argv[formatIndex + 1]);
        Assert.Contains("--no-progress", argv);
        var rulesetIndex = argv.ToList().IndexOf("--rulesets");
        Assert.True(rulesetIndex >= 0 && rulesetIndex + 1 < argv.Count);
        Assert.Equal(PmdAuditor.DefaultRuleset, argv[rulesetIndex + 1]);
        var dirIndex = argv.ToList().IndexOf("--dir");
        Assert.True(dirIndex >= 0 && dirIndex + 1 < argv.Count);
        Assert.Equal(".", argv[dirIndex + 1]);
        var relativizeIndex = argv.ToList().IndexOf("--relativize-paths-with");
        Assert.True(relativizeIndex >= 0 && relativizeIndex + 1 < argv.Count);
        Assert.Equal(Path.GetFullPath(WorkDir), argv[relativizeIndex + 1]);
        // The audited tree must not be able to silence findings by comment:
        // the NOPMD marker is pinned to an operator-side token by default.
        var markerIndex = argv.ToList().IndexOf("--suppress-marker");
        Assert.True(markerIndex >= 0 && markerIndex + 1 < argv.Count);
        Assert.Equal(PmdAuditor.DisabledSuppressMarker, argv[markerIndex + 1]);
        Assert.DoesNotContain("--show-suppressed", argv);
        Assert.DoesNotContain("--report-file", argv);
        Assert.DoesNotContain("--cache", argv);
    }

    [Fact]
    public void DisabledPlugin_IsNotLoaded_AndToolsAbsentFromBaselineProvisioning()
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
            s => s.PluginId == PmdAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("pmd", flattened, StringComparison.Ordinal);
        Assert.DoesNotContain("java", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresPmdAndJavaRuntimeRequirements()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PmdAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PmdAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var pmd = Assert.Single(tools, t => t.Binary == "pmd");
        // PMD 7 has no pinned distro package (Debian carries PMD 6, whose CLI
        // predates `check`): the operator provisions the versioned release.
        Assert.Null(pmd.AptPackage);
        var java = Assert.Single(tools, t => t.Binary == "java");
        Assert.Equal("default-jre-headless", java.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Equal(2, contributions.VerificationCommands.Count);
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("default-jre-headless", install, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "PMD 7.25.0 (abc, 2026-05-29)\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, XmlClean, ""));
        });

        var auditor = new PmdAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "7.25.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_RulesetPath_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, XmlClean, ""));
        });

        var auditor = new PmdAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:RulesetPath"] = "/opt/codeybox/pmd-ruleset.xml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var rulesetIndex = argv.ToList().IndexOf("--rulesets");
        Assert.True(rulesetIndex >= 0 && rulesetIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/pmd-ruleset.xml", argv[rulesetIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_RulesetInExtraArguments_ReplacesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, XmlClean, ""));
        });

        var auditor = new PmdAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // ExtraArguments is a comma-separated argv list: each entry
                // is one token.
                ["Scoped:ExtraArguments"] = "-R,rulesets/java/errorprone.xml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var rulesetIndex = argv.ToList().IndexOf("-R");
        Assert.True(rulesetIndex >= 0 && rulesetIndex + 1 < argv.Count);
        Assert.Equal("rulesets/java/errorprone.xml", argv[rulesetIndex + 1]);
        Assert.DoesNotContain(PmdAuditor.DefaultRuleset, argv);
    }

    [Fact]
    public async Task ScopedConfiguration_TrustRepositorySuppression_RestoresNopmdMarker()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, XmlClean, ""));
        });

        var auditor = new PmdAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        // No --suppress-marker flag: PMD's default NOPMD marker is honored again.
        Assert.DoesNotContain("--suppress-marker", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, XmlVendoredPaths, ""));
        });

        IAuditor auditor = new PmdAuditor();
        var result = await auditor.RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        // vendor/ and target/ findings are dropped by the default
        // ExcludePaths; the src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/App.java:3", finding.Location);
    }

    [Fact]
    public async Task MinimumSeverity_Drops_InfoFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(4, XmlAllPriorities, ""));
        });

        var auditor = new PmdAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "warning",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, WorkDir, FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);
        Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Info);
    }

    [Fact]
    public void Parser_MalformedXml_ThrowsParseException()
    {
        var parser = new PmdXmlOutputParser();
        var ex = Assert.Throws<ExternalToolParseException>(
            () => parser.Parse(new ExternalToolParseInput("pmd", "not xml at all <<<", "", 4)));
        Assert.Contains("pmd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_XmlWithoutPmdRoot_ThrowsParseException()
    {
        var parser = new PmdXmlOutputParser();
        Assert.Throws<ExternalToolParseException>(
            () => parser.Parse(new ExternalToolParseInput(
                "pmd", "<results><violation rule=\"x\"/></results>", "", 4)));
    }

    [Fact]
    public void Parser_EmptyStdout_ThrowsParseException()
    {
        // Exit 0/4 with no report is not a pass: the scan always emits XML,
        // so silence fails closed.
        var parser = new PmdXmlOutputParser();
        Assert.Throws<ExternalToolParseException>(
            () => parser.Parse(new ExternalToolParseInput("pmd", "", "PMD log lines", 0)));
    }

    [Fact]
    public void Parser_ViolationOutsideFileElement_CarriesNoPath()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <pmd xmlns="http://pmd.sourceforge.net/report/2.0.0" version="7.26.0">
            <violation beginline="4" endline="4" begincolumn="1" endcolumn="1" rule="LooseRule" ruleset="X" priority="2">msg</violation>
            </pmd>
            """;
        var parser = new PmdXmlOutputParser();
        var finding = Assert.Single(parser.Parse(new ExternalToolParseInput("pmd", xml, "", 4)));

        Assert.Equal("2", finding.SeverityLevel);
        Assert.Equal("LooseRule", finding.RuleId);
        Assert.Null(finding.Path);
        Assert.Equal(4, finding.Line);
    }

    [Fact]
    public void ExtractPmdVersion_AnchorsOnPmdToken_NotJavaRuntime()
    {
        var banner = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, VersionBanner, "7.26.0");
        Assert.Equal("7.26.0", PmdAuditor.ExtractPmdVersion(banner));
        Assert.Null(PmdAuditor.ExtractPmdVersion("Java version: 21.0.3, vendor: X"));
        Assert.Null(PmdAuditor.ExtractPmdVersion("no version here"));
    }

    [Fact]
    [Trait("requires_pmd", "true")]
    public async Task RealPmd_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledPmdVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPmdFixtureRepoAsync(pmdClean: false);
        var rulesetPath = Path.Combine(fixtureDir, "audit-ruleset.xml");
        // Operator-owned ruleset pinning a priority-1 rule so the violation
        // is blocking — the fixture exercises the full gate, not just parse.
        await File.WriteAllTextAsync(rulesetPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <ruleset name="audit" xmlns="http://pmd.sourceforge.net/ruleset/2.0.0">
                <description>Audit fixture ruleset</description>
                <rule ref="category/java/bestpractices.xml/UnusedLocalVariable"><priority>1</priority></rule>
            </ruleset>
            """);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = WorkDir,
                    Mounts = [new SandboxMount { SandboxPath = WorkDir, HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new PmdAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:RulesetPath"] = "audit-ruleset.xml",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, WorkDir, FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("UnusedLocalVariable", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.EndsWith("Foo.java:3", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_pmd", "true")]
    public async Task RealPmd_CleanFixture_Passes()
    {
        var installed = InstalledPmdVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPmdFixtureRepoAsync(pmdClean: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = WorkDir,
                    Mounts = [new SandboxMount { SandboxPath = WorkDir, HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new PmdAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, WorkDir, FakeContext(), CancellationToken.None);

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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PmdAuditorPlugin.dll");
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
            PluginId: PmdAuditor.PluginId,
            PluginDisplayName: "CodeyBox: PMD Java/Multi-Language Analyser",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(
                0,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    VersionBanner,
                    PmdAuditor.DefaultExpectedVersion),
                "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("pmd", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "pmd" && exec.Argv[1] == "--version";

    private static async Task<string> SeedPmdFixtureRepoAsync(bool pmdClean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-pmd-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));

        if (pmdClean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "Ok.java"),
                "public class Ok {\n    public int value() {\n        return 42;\n    }\n}\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "src", "Foo.java"),
                "public class Foo {\n    public void bar() {\n        int x = 0;\n    }\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledPmdVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pmd",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 15_000))
            {
                try { process.Kill(); } catch { /* best-effort probe teardown */ }
                return null;
            }
            var match = Regex.Match(stdout, @"PMD\s+(\d+\.\d+\.\d+[\w.\-]*)");
            return process.ExitCode == 0 && match.Success ? match.Groups[1].Value.TrimEnd('.') : null;
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
