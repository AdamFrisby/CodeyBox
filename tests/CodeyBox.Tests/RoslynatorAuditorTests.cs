using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.RoslynatorAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Roslynator auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming roslynator (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
/// - Exit 0 with console-only output (the tool writes no report for a clean tree) passes with zero findings;
///   exit 0 with a SARIF payload (operator --return-success-on-diagnostics) still reports findings.
/// - Exit 1 without SARIF output fails closed as an infrastructure failure.
/// - Roslynator SARIF output maps to findings with rule ids, locations, and mapped severity;
///   absolute file:// artifact URIs are preserved scheme-stripped (see the absolute-location test).
/// - Raw tool severities go through the declared mapping (verified levels: error, warning, none;
///   the map also covers the wider SARIF/Roslyn vocabulary).
/// - Default arguments (analyze, SARIF to stdout, severity floor) and scoped options
///   (ExpectedVersion, ProjectPath, MinimumSeverity, IncludedRules).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_roslynator", "true")].
/// </summary>
public sealed class RoslynatorAuditorTests
{
    private static readonly string? InstalledRoslynatorVersion = ProbeInstalledRoslynatorVersion();

    // Shape mirrors real `roslynator analyze -o /dev/stdout --output-format sarif` (1.0.0.0):
    // absolute file:// artifact URIs, per-result level/message/region, BOM-prefixed payload.
    private const string SarifWithFindings = "\uFEFF{\n"
        + "  \"$schema\": \"https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json\",\n"
        + "  \"version\": \"2.1.0\",\n"
        + "  \"runs\": [\n"
        + "    {\n"
        + "      \"tool\": { \"driver\": { \"name\": \"Roslynator\", \"version\": \"1.0.0.0\", \"informationUri\": \"https://github.com/dotnet/roslynator\" } },\n"
        + "      \"results\": [\n"
        + "        {\n"
        + "          \"ruleId\": \"CS0219\",\n"
        + "          \"level\": \"warning\",\n"
        + "          \"message\": { \"text\": \"The variable 'unused' is assigned but its value is never used\" },\n"
        + "          \"locations\": [\n"
        + "            {\n"
        + "              \"physicalLocation\": {\n"
        + "                \"artifactLocation\": { \"uri\": \"file:///work/src/Program.cs\" },\n"
        + "                \"region\": { \"startLine\": 6, \"endLine\": 6, \"startColumn\": 13, \"endColumn\": 19 }\n"
        + "              }\n"
        + "            }\n"
        + "          ]\n"
        + "        },\n"
        + "        {\n"
        + "          \"ruleId\": \"CS0029\",\n"
        + "          \"level\": \"error\",\n"
        + "          \"message\": { \"text\": \"Cannot implicitly convert type 'string' to 'int'\" },\n"
        + "          \"locations\": [\n"
        + "            {\n"
        + "              \"physicalLocation\": {\n"
        + "                \"artifactLocation\": { \"uri\": \"file:///work/src/Broken.cs\" },\n"
        + "                \"region\": { \"startLine\": 6, \"endLine\": 6, \"startColumn\": 17, \"endColumn\": 23 }\n"
        + "              }\n"
        + "            }\n"
        + "          ]\n"
        + "        }\n"
        + "      ]\n"
        + "    }\n"
        + "  ]\n"
        + "}";

    // A clean roslynator run writes no report file: stdout carries only the
    // console log. The auditor reads this shape as zero findings (a pass).
    private const string CleanConsoleOutput = """
        Loading project '/work/Fixture.csproj'...
        Analyze 'Fixture'
        Analyzed project '/work/Fixture.csproj' (in 1.8 s)

        0 diagnostics found
        """;

    private const string SarifWithLevels = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Roslynator", "version": "1.0.0.0" } },
              "results": [
                {
                  "ruleId": "rule-error",
                  "level": "error",
                  "message": { "text": "Error-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 1 } } }]
                },
                {
                  "ruleId": "rule-warning",
                  "level": "warning",
                  "message": { "text": "Warning-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 2 } } }]
                },
                {
                  "ruleId": "rule-none",
                  "level": "none",
                  "message": { "text": "Hidden-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 3 } } }]
                },
                {
                  "ruleId": "rule-note",
                  "level": "note",
                  "message": { "text": "Info-level diagnostic." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 4 } } }]
                },
                {
                  "ruleId": "rule-unknown",
                  "level": "blocker",
                  "message": { "text": "Unrecognised level." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 5 } } }]
                },
                {
                  "ruleId": "rule-nolevel",
                  "message": { "text": "No level field." },
                  "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 6 } } }]
                }
              ]
            }
          ]
        }
        """;

    // Relative artifact URIs to exercise the finding-level ExcludePaths
    // mechanism through this auditor.
    private const string SarifWithRelativePaths = """
        {
          "$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Roslynator", "version": "1.0.0.0" } },
              "results": [
                {
                  "ruleId": "CS0219",
                  "level": "warning",
                  "message": { "text": "The variable 'unused' is assigned but its value is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "src/Program.cs" },
                        "region": { "startLine": 3 }
                      }
                    }
                  ]
                },
                {
                  "ruleId": "CS0219",
                  "level": "warning",
                  "message": { "text": "The variable 'unused' is assigned but its value is never used" },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "vendor/lib.cs" },
                        "region": { "startLine": 1 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingRoslynator_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "roslynator: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingRoslynator()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "0.9.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var warning = Assert.Single(result.Findings, f => f.Title.Contains("CS0219", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.EndsWith("src/Program.cs:6", warning.Location, StringComparison.Ordinal);

        var error = Assert.Single(result.Findings, f => f.Title.Contains("CS0029", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.EndsWith("src/Broken.cs:6", error.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsoluteArtifactUris_ArePreservedSchemeStripped_NotRelativized()
    {
        // Roslynator emits absolute file:// artifact URIs with no scan-root
        // metadata, so the shared SARIF parser preserves the
        // sandbox-absolute path. This test pins that behavior: locations
        // stay usable, but repo-relative ExcludePaths prefixes cannot match
        // them (see the plugin README).
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings, f => f.Title.Contains("CS0219", StringComparison.Ordinal));
        Assert.Equal("work/src/Program.cs:6", finding.Location);
    }

    [Fact]
    public async Task CleanFixture_ExitZeroConsoleOutput_YieldsZeroFindings_AndPasses()
    {
        // A clean roslynator run writes no SARIF report: stdout carries only
        // the console log. That console text is the tool's clean verdict,
        // not a parse failure.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, CleanConsoleOutput, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithSarif_StillFails_ExitZeroCannotSilenceGate()
    {
        // An operator --return-success-on-diagnostics moves roslynator's
        // exit to 0 but the SARIF report still carries the diagnostics:
        // findings still fail the audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithSarif_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithRelativePaths, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Both findings are warnings (advisory), so the audit passes — but
        // the src/ finding still surfaces while the vendor/ one is dropped
        // by the default ExcludePaths.
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/Program.cs:3", finding.Location);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "Could not find MSBuild project or solution file in '/work'"));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutSarif_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "some console text, not a report"));
        });

        IAuditor auditor = new RoslynatorAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "roslynator: command not found"));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithLevels, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(6, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);

        var none = Assert.Single(findings, f => f.Title.Contains("rule-none", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, none.Severity);

        var note = Assert.Single(findings, f => f.Title.Contains("rule-note", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, note.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-nolevel", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> SARIF "warning" -> Warning
    }

    [Fact]
    public async Task DefaultArguments_Analyze_SarifToStdout_InfoFloor()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, CleanConsoleOutput, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("roslynator", argv[0]);
        Assert.Contains("analyze", argv);
        var outputIndex = argv.ToList().IndexOf("--output");
        Assert.True(outputIndex >= 0 && outputIndex + 1 < argv.Count);
        Assert.Equal("/dev/stdout", argv[outputIndex + 1]);
        var formatIndex = argv.ToList().IndexOf("--output-format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("sarif", argv[formatIndex + 1]);
        var severityIndex = argv.ToList().IndexOf("--severity-level");
        Assert.True(severityIndex >= 0 && severityIndex + 1 < argv.Count);
        Assert.Equal("info", argv[severityIndex + 1]);
        var verbosityIndex = argv.ToList().IndexOf("--verbosity");
        Assert.True(verbosityIndex >= 0 && verbosityIndex + 1 < argv.Count);
        Assert.Equal("quiet", argv[verbosityIndex + 1]);
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
            s => s.PluginId == RoslynatorAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("roslynator", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresRoslynatorRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [RoslynatorAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == RoslynatorAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var roslynator = Assert.Single(tools, t => t.Binary == "roslynator");
        // Verify-only by design: roslynator ships as a .NET global tool, not
        // a distro apt package — no apt package carries a version pin.
        Assert.Null(roslynator.AptPackage);
        // Analysis loads MSBuild projects, so the .NET SDK is declared too
        // (verify-only: the operator provisions the SDK in the baseline).
        var dotnet = Assert.Single(tools, t => t.Binary == "dotnet");
        Assert.Null(dotnet.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = string.Join(
            "\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("roslynator", verification, StringComparison.Ordinal);
        Assert.Contains("dotnet", verification, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "0.9.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, CleanConsoleOutput, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.9.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverityWarning_RaisesToolFloor()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, CleanConsoleOutput, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "warning",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var severityIndex = argv.ToList().IndexOf("--severity-level");
        Assert.True(severityIndex >= 0 && severityIndex + 1 < argv.Count);
        Assert.Equal("warning", argv[severityIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ProjectPath_AppendedAsPositional()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsProjectPresenceProbe(exec, "src/App.sln"))
                return Task.FromResult(new SandboxExecResult(0, "src/App.sln\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, CleanConsoleOutput, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = "src/App.sln",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal("./src/App.sln", scanExec!.Argv[^1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ProjectPath_MissingFile_IsDeterministicInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Presence probe reports nothing: the configured file is absent.
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = "src/App.sln",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("roslynator", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
    }

    [Fact]
    public async Task ScopedConfiguration_ProjectPath_Traversal_IsRejected()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, CleanConsoleOutput, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = "../outside/App.sln",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("codeybox.roslynator", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithRelativePaths, ""));
        });

        IAuditor auditor = new RoslynatorAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The vendor/ finding is dropped by the default ExcludePaths; the
        // src/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/Program.cs:3", finding.Location);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        var auditor = new RoslynatorAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "CS0029",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("CS0029", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_roslynator", "true")]
    public async Task RealRoslynator_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledRoslynatorVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRoslynatorFixtureRepoAsync(clean: false);

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

            var auditor = new RoslynatorAuditor();
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

            var unused = Assert.Single(result.Findings, f => f.Title.Contains("CS0219", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Warning, unused.Severity);
            Assert.EndsWith("Program.cs:6", unused.Location, StringComparison.Ordinal);

            var broken = Assert.Single(result.Findings, f => f.Title.Contains("CS0029", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, broken.Severity);
            Assert.EndsWith("Program.cs:7", broken.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_roslynator", "true")]
    public async Task RealRoslynator_CleanFixture_Passes()
    {
        var installed = InstalledRoslynatorVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedRoslynatorFixtureRepoAsync(clean: true);

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

            var auditor = new RoslynatorAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.RoslynatorAuditorPlugin.dll");
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
            PluginId: RoslynatorAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Roslynator C# Static Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, RoslynatorAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("roslynator", StringComparer.Ordinal);

    private static bool IsProjectPresenceProbe(SandboxExec exec, string projectPath)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains(projectPath, StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "roslynator" && exec.Argv[1] == "--version";

    private static async Task<string> SeedRoslynatorFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-roslynator-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "Fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "Program.cs"),
            clean
                ? "using System;\nclass Program\n{\n    static void Main()\n    {\n        Console.WriteLine(\"hi\");\n    }\n}\n"
                : "using System;\nclass Program\n{\n    static void Main()\n    {\n        int unused = 42;\n        int broken = \"nope\";\n        Console.WriteLine(\"hi\");\n    }\n}\n");

        return dir;
    }

    private static string? ProbeInstalledRoslynatorVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "roslynator",
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
