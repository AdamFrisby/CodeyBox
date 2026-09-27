using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.CppcheckAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Cppcheck auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming cppcheck (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
/// - Exit 1 without XML output fails closed as infrastructure, except the exact
///   no-input-files diagnostic (a tree with no C/C++ files), which is a clean pass.
/// - cppcheck XML on stderr maps to findings with rule ids, locations, and mapped severity;
///   report channels are split (XML on stderr, progress on stdout).
/// - Raw tool severities go through the declared mapping (all six cppcheck levels).
/// - Default exclusions (checkersReport, vendored paths), the inert-by-default
///   suppression posture, and scoped options (ExpectedVersion, SuppressionsPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled;
///   when enabled its apt package is installed by provisioning.
/// - Real binary execution tests under [Trait("requires_cppcheck", "true")].
/// </summary>
public sealed class CppcheckAuditorTests
{
    private static readonly string? InstalledCppcheckVersion = ProbeInstalledCppcheckVersion();

    // Shape mirrors real `cppcheck --xml --xml-version=2 --enable=all
    // --error-exitcode=1 --quiet` (2.13.0): the XML report arrives on stderr
    // while stdout carries only progress lines.
    private const string ProgressStdout = """
        Checking src/null.c ...
        2/2 files checked 100% done
        """;

    private const string XmlWithFindings = """
        <?xml version="1.0" encoding="UTF-8"?>
        <results version="2">
            <cppcheck version="2.13.0"/>
            <errors>
                <error id="nullPointer" severity="error" msg="Null pointer dereference: p" verbose="Null pointer dereference: p" cwe="476" file0="src/null.c">
                    <location file="src/null.c" line="3" column="13" info="Null pointer dereference"/>
                    <location file="src/null.c" line="2" column="14" info="Assignment &apos;p=0&apos;, assigned value is 0"/>
                    <symbol>p</symbol>
                </error>
                <error id="uninitvar" severity="error" msg="Uninitialized variable: x" verbose="Uninitialized variable: x" cwe="457" file0="src/bug.cpp">
                    <location file="src/bug.cpp" line="5" column="9" info="Uninitialized variable: x"/>
                </error>
                <error id="constVariablePointer" severity="style" msg="Variable &apos;p&apos; can be declared as pointer to const" verbose="Variable &apos;p&apos; can be declared as pointer to const" cwe="398" file0="src/null.c">
                    <location file="src/null.c" line="2" column="10" info="Variable &apos;p&apos; can be declared as pointer to const"/>
                    <symbol>p</symbol>
                </error>
            </errors>
        </results>
        """;

    private const string XmlClean = """
        <?xml version="1.0" encoding="UTF-8"?>
        <results version="2">
            <cppcheck version="2.13.0"/>
            <errors>
            </errors>
        </results>
        """;

    // Shape of a real --enable=all run over a clean tree (2.13.0): exit 0
    // with the location-less checkersReport meta-diagnostic present.
    private const string XmlCheckersReportOnly = """
        <?xml version="1.0" encoding="UTF-8"?>
        <results version="2">
            <cppcheck version="2.13.0"/>
            <errors>
                <error id="checkersReport" severity="information" msg="Active checkers: 106/592 (use --checkers-report=&lt;filename&gt; to see details)" verbose="Active checkers: 106/592 (use --checkers-report=&lt;filename&gt; to see details)"/>
            </errors>
        </results>
        """;

    private const string XmlAllSeverities = """
        <?xml version="1.0" encoding="UTF-8"?>
        <results version="2">
            <cppcheck version="2.13.0"/>
            <errors>
                <error id="nullPointer" severity="error" msg="Null pointer dereference: p" verbose="Null pointer dereference: p" cwe="476">
                    <location file="src/a.c" line="1" column="1"/>
                </error>
                <error id="uninitvar" severity="warning" msg="Uninitialized variable: x" verbose="Uninitialized variable: x">
                    <location file="src/a.c" line="2" column="1"/>
                </error>
                <error id="unusedFunction" severity="style" msg="The function &apos;f&apos; is never used." verbose="The function &apos;f&apos; is never used.">
                    <location file="src/a.c" line="3" column="1"/>
                </error>
                <error id="unusedVariable" severity="performance" msg="Unused variable: y" verbose="Unused variable: y">
                    <location file="src/a.c" line="4" column="1"/>
                </error>
                <error id="signConversion" severity="portability" msg="Sign conversion" verbose="Sign conversion">
                    <location file="src/a.c" line="5" column="1"/>
                </error>
                <error id="missingInclude" severity="information" msg="Include file not found." verbose="Include file not found.">
                    <location file="src/a.c" line="6" column="1"/>
                </error>
            </errors>
        </results>
        """;

    private const string XmlVendoredPaths = """
        <?xml version="1.0" encoding="UTF-8"?>
        <results version="2">
            <cppcheck version="2.13.0"/>
            <errors>
                <error id="nullPointer" severity="error" msg="Null pointer dereference: p" verbose="Null pointer dereference: p">
                    <location file="src/app.c" line="3" column="1"/>
                </error>
                <error id="nullPointer" severity="error" msg="Null pointer dereference: q" verbose="Null pointer dereference: q">
                    <location file="vendor/lib.c" line="1" column="1"/>
                </error>
                <error id="nullPointer" severity="error" msg="Null pointer dereference: r" verbose="Null pointer dereference: r">
                    <location file="external/dep.cpp" line="7" column="1"/>
                </error>
            </errors>
        </results>
        """;

    [Fact]
    public async Task MissingBinary_YieldsInfrastructureFailure_NamingCppcheck_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "command -v: cppcheck not found"));
            throw new InvalidOperationException("scan must not run when the presence probe fails");
        });

        IAuditor auditor = new CppcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        // A security scanner that silently passes because it did not run is
        // the worst outcome: this throws (infrastructure) instead, naming cppcheck.
        Assert.Contains("cppcheck", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongVersion_YieldsInfrastructureFailure_NamingCppcheck()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Cppcheck 9.99.9\n", ""));
            throw new InvalidOperationException("scan must not run on a version mismatch");
        });

        IAuditor auditor = new CppcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cppcheck", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyFixture_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // The XML report arrives on stderr; stdout carries progress only.
            return Task.FromResult(new SandboxExecResult(1, ProgressStdout, XmlWithFindings));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var nullDereference = Assert.Single(
            result.Findings, f => f.Title.Contains("nullPointer", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, nullDereference.Severity);
        Assert.Equal("src/null.c:3", nullDereference.Location);

        // The first <location> child wins for multi-location diagnostics.
        var cppFinding = Assert.Single(
            result.Findings, f => f.Title.Contains("uninitvar", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, cppFinding.Severity);
        Assert.Equal("src/bug.cpp:5", cppFinding.Location);

        var style = Assert.Single(
            result.Findings, f => f.Title.Contains("constVariablePointer", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, style.Severity);
        Assert.Equal("src/null.c:2", style.Location);
    }

    [Fact]
    public async Task StyleOnlyFindings_AreAdvisory_NotBlocking()
    {
        const string styleOnly = """
            <?xml version="1.0" encoding="UTF-8"?>
            <results version="2">
                <cppcheck version="2.13.0"/>
                <errors>
                    <error id="unusedFunction" severity="style" msg="The function &apos;f&apos; is never used." verbose="The function &apos;f&apos; is never used.">
                        <location file="src/f.c" line="1" column="1"/>
                    </error>
                </errors>
            </results>
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", styleOnly));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("src/f.c:1", finding.Location);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", XmlClean));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task CheckersReport_IsExcluded_ByDefault()
    {
        // A real --enable=all run over a clean tree exits 0 with the
        // location-less checkersReport meta-diagnostic: excluded through the
        // shared rule-exclusion default, so the clean tree stays a clean pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", XmlCheckersReportOnly));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task CheckersReport_Surfaces_WhenExcludedRulesOverridden()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, "", XmlCheckersReportOnly));
        });

        var auditor = new CppcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "someOtherRule",
            }),
            CancellationToken.None);

        // Overriding ExcludedRules replaces the default (per Bind semantics),
        // so checkersReport is reported as an informational finding.
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("checkersReport", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitOne_WithoutXmlReport_IsInfrastructure()
    {
        // Usage errors print text to stdout with no XML on stderr (verified:
        // `unrecognized command line option`, exit 1) — fails closed.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "cppcheck: error: unrecognized command line option: \"--bogus\".\n", ""));
        });

        IAuditor auditor = new CppcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cppcheck", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructure_EvenWithXmlReport()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", XmlWithFindings));
        });

        IAuditor auditor = new CppcheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("cppcheck", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoInputFilesDiagnostic_IsCleanPass_NotInfrastructure()
    {
        // A tree with no C/C++ files: exit 1 with the exact sentinel on
        // stdout and no XML (verified against 2.13.0) — nothing to check.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "cppcheck: error: could not find or open any of the paths given.\n", ""));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task SeverityMapping_Applies_AllSixCppcheckLevels()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", XmlAllSeverities));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Raw tool severities never pass through: every finding carries a
        // CodeyBox severity from the declared map.
        Assert.Equal(6, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error, SeverityOf(result, "nullPointer"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "uninitvar"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "unusedFunction"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "unusedVariable"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "signConversion"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "missingInclude"));
        Assert.False(result.Passed);

        static AuditSeverity SeverityOf(AuditResult r, string rule)
            => Assert.Single(r.Findings, f => f.Title.Contains(rule, StringComparison.Ordinal)).Severity;
    }

    [Fact]
    public async Task BuiltArguments_SelectXmlReport_WithEnableAll_AndNoInlineSuppression()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", XmlClean));
        });

        IAuditor auditor = new CppcheckAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("cppcheck", argv[0]);
        Assert.Contains("--xml", argv);
        var xmlVersionIndex = argv.ToList().IndexOf("--xml-version");
        Assert.True(xmlVersionIndex >= 0 && xmlVersionIndex + 1 < argv.Count);
        Assert.Equal("2", argv[xmlVersionIndex + 1]);
        var exitCodeIndex = argv.ToList().IndexOf("--error-exitcode");
        Assert.True(exitCodeIndex >= 0 && exitCodeIndex + 1 < argv.Count);
        Assert.Equal("1", argv[exitCodeIndex + 1]);
        Assert.Contains("--quiet", argv);
        var enableIndex = argv.ToList().IndexOf("--enable");
        Assert.True(enableIndex >= 0 && enableIndex + 1 < argv.Count);
        Assert.Equal("all", argv[enableIndex + 1]);
        // The audited tree must not be able to silence the audit.
        Assert.DoesNotContain("--inline-suppr", argv);
        Assert.DoesNotContain("--output-file", argv);
        Assert.Equal(".", argv[^1]);
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
            s => s.PluginId == CppcheckAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("cppcheck", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCppcheckRequirement_WithAptPackage()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CppcheckAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CppcheckAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("cppcheck", tool.Binary);
        // cppcheck ships as a distro package: provisioning installs it via
        // apt, but only when this plugin is enabled.
        Assert.Equal("cppcheck", tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("cppcheck", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        var install = Assert.Single(contributions.InstallCommands);
        Assert.Contains("cppcheck", install, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Cppcheck 2.12.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", XmlClean));
        });

        var auditor = new CppcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "2.12.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_SuppressionsPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", XmlClean));
        });

        var auditor = new CppcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SuppressionsPath"] = "/opt/codeybox/cppcheck.operator.supp",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var suppressionsIndex = argv.ToList().IndexOf("--suppressions-list");
        Assert.True(suppressionsIndex >= 0 && suppressionsIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/cppcheck.operator.supp", argv[suppressionsIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_EnableOverride_ReplacesEnableAll()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", XmlClean));
        });

        var auditor = new CppcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "--enable=warning",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Contains("--enable=warning", argv);
        // cppcheck --enable is additive: the operator selection replaces
        // --enable=all rather than stacking on it.
        Assert.DoesNotContain("all", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", XmlVendoredPaths));
        });

        IAuditor auditor = new CppcheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ and external/ findings are dropped by the default
        // ExcludePaths; the src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/app.c:3", finding.Location);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ProgressStdout, XmlWithFindings));
        });

        var auditor = new CppcheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "uninitvar",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("uninitvar", finding.Title, StringComparison.Ordinal);
        Assert.Equal("src/bug.cpp:5", finding.Location);
    }

    [Fact]
    public void Parser_MalformedXml_ThrowsParseException()
    {
        var parser = new CppcheckXmlOutputParser();
        var ex = Assert.Throws<ExternalToolParseException>(
            () => parser.Parse(new ExternalToolParseInput("cppcheck", "", "not xml at all <<<", 1)));
        Assert.Contains("cppcheck", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_XmlWithoutResultsRoot_ThrowsParseException()
    {
        var parser = new CppcheckXmlOutputParser();
        Assert.Throws<ExternalToolParseException>(
            () => parser.Parse(new ExternalToolParseInput(
                "cppcheck", "", "<errors><error id=\"x\"/></errors>", 1)));
    }

    [Fact]
    public void Parser_EmptyStderr_WithCleanExit_ThrowsParseException()
    {
        // Exit 0 with no report is not a pass: the scan always emits XML, so
        // silence (e.g. an operator --output-file redirect) fails closed.
        // Only the exact no-input-files diagnostic on stdout is a pass.
        var parser = new CppcheckXmlOutputParser();
        Assert.Throws<ExternalToolParseException>(
            () => parser.Parse(new ExternalToolParseInput("cppcheck", "Checking src/a.c ...\n", "", 0)));
    }

    [Fact]
    public void Parser_ErrorAttributes_FallBack_WhenNoLocationChildren()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <results version="2">
                <errors>
                    <error id="nullPointer" severity="error" msg="Null pointer dereference" verbose="Null pointer dereference" file="src/old.c" line="9"/>
                </errors>
            </results>
            """;
        var parser = new CppcheckXmlOutputParser();
        var finding = Assert.Single(parser.Parse(new ExternalToolParseInput("cppcheck", "", xml, 1)));

        Assert.Equal("error", finding.SeverityLevel);
        Assert.Equal("nullPointer", finding.RuleId);
        Assert.Equal("src/old.c", finding.Path);
        Assert.Equal(9, finding.Line);
    }

    [Fact]
    [Trait("requires_cppcheck", "true")]
    public async Task RealCppcheck_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledCppcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCppcheckFixtureRepoAsync(cppcheckClean: false);

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

            var auditor = new CppcheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);

            var nullDereference = Assert.Single(
                result.Findings, f => f.Title.Contains("nullPointer", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, nullDereference.Severity);
            Assert.EndsWith("null.c:3", nullDereference.Location, StringComparison.Ordinal);

            var uninitialized = Assert.Single(
                result.Findings, f => f.Title.Contains("uninitvar", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, uninitialized.Severity);
            Assert.EndsWith("bug.cpp:3", uninitialized.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_cppcheck", "true")]
    public async Task RealCppcheck_CleanFixture_Passes()
    {
        var installed = InstalledCppcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCppcheckFixtureRepoAsync(cppcheckClean: true);

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

            var auditor = new CppcheckAuditor();
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
    [Trait("requires_cppcheck", "true")]
    public async Task RealCppcheck_InlineSuppressComment_SurfacesAsFinding_ByDefault()
    {
        // The default scan never passes --inline-suppr: a defect the fixture
        // silences with `// cppcheck-suppress nullPointer` still surfaces.
        var installed = InstalledCppcheckVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(
            Path.GetTempPath(), "codeybox-cppcheck-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixtureDir, "suppr.c"),
                "int main(void) {\n    int *p = 0; // cppcheck-suppress nullPointer\n    return *p;\n}\n");

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts = [new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir }],
                },
                CancellationToken.None);

            var auditor = new CppcheckAuditor();
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
                result.Findings, f => f.Title.Contains("nullPointer", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CppcheckAuditorPlugin.dll");
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
            PluginId: CppcheckAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Cppcheck C/C++ Analyser",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Cppcheck " + CppcheckAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("cppcheck", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cppcheck" && exec.Argv[1] == "--version";

    private static async Task<string> SeedCppcheckFixtureRepoAsync(bool cppcheckClean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-cppcheck-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (cppcheckClean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "main.c"), "int main(void) { return 0; }\n");
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "null.c"), "int main(void) {\n    int *p = 0;\n    return *p;\n}\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "bug.cpp"), "int f() {\n    int x;\n    return x;\n}\n");
        }

        return dir;
    }

    private static string? ProbeInstalledCppcheckVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cppcheck",
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
