using System.Diagnostics;
using CodeyBox.CodeqlAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the CodeQL auditor plugin: a missing or wrong-version binary is
/// infrastructure naming the tool (never a pass), database-creation failure
/// is infrastructure and the analysis never runs, exit 0 with SARIF results
/// is the findings verdict while every non-zero exit is infrastructure,
/// SARIF maps to findings with rule id and file/line with severity recovered
/// from CodeQL rule metadata, severity goes through the declared mapping
/// rather than passing through, and the plugin is inert — unloaded and
/// absent from baseline provisioning — until an operator enables it. Every
/// run is dispatched through <see cref="IAuditor"/> so the version-pin and
/// database preconditions cannot be bypassed by interface dispatch.
/// </summary>
public sealed class CodeqlAuditorTests
{
    // Mirrors what `codeql database analyze --format sarifv2.1.0` writes for a
    // Python command-injection alert (verified against CodeQL 2.27.1):
    // results carry no "level" — the severity lives once per run in
    // tool.driver.rules[] as defaultConfiguration.level (with the query's
    // properties["problem.severity"] as fallback) — ruleId, message text, and
    // the first physical location's source-root-relative artifact uri plus
    // region.startLine.
    private const string SarifWithCommandInjection = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": {
              "driver": {
                "name": "CodeQL",
                "rules": [{
                  "id": "py/command-line-injection",
                  "name": "Uncontrolled command line",
                  "shortDescription": { "text": "Uncontrolled command line" },
                  "defaultConfiguration": { "enabled": true, "level": "error" },
                  "properties": {
                    "id": "py/command-line-injection",
                    "kind": "path-problem",
                    "problem.severity": "error",
                    "security-severity": "9.8"
                  }
                }]
              }
            },
            "results": [{
              "ruleId": "py/command-line-injection",
              "ruleIndex": 0,
              "message": { "text": "This command line depends on a [user-provided value](1)." },
              "locations": [{
                "physicalLocation": {
                  "artifactLocation": { "uri": "vuln.py", "uriBaseId": "%SRCROOT%" },
                  "region": { "startLine": 6, "startColumn": 15, "endColumn": 29 }
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
            "tool": { "driver": { "name": "CodeQL", "rules": [] } },
            "results": []
          }]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCodeql_NeverAPass()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "codeql: command not found"));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("codeql", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task CommandInjectionInFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? createExec = null;
        SandboxExec? analyzeExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCreateExec(exec))
            {
                createExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            analyzeExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifWithCommandInjection, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        await ((CodeqlAuditor)auditor).InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:Language"] = "python" }),
            CancellationToken.None);
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:codeql", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("py/command-line-injection", finding.Title, StringComparison.Ordinal);
        Assert.Equal("vuln.py:6", finding.Location);

        Assert.NotNull(createExec);
        Assert.Equal(
            ["codeql", "database", "create"],
            createExec!.Argv.Take(3).ToList());
        Assert.Contains("--language=python", createExec.Argv);
        Assert.Contains("--source-root=.", createExec.Argv);
        Assert.Contains("--overwrite", createExec.Argv);

        Assert.NotNull(analyzeExec);
        var argv = analyzeExec!.Argv;
        Assert.Equal("codeql", argv[0]);
        Assert.Equal("database", argv[1]);
        Assert.Equal("analyze", argv[2]);
        Assert.Contains("--format", argv);
        Assert.Contains("sarifv2.1.0", argv);
        var outputFlag = argv.ToList().IndexOf("--output");
        Assert.True(outputFlag >= 0 && outputFlag + 1 < argv.Count);
        Assert.Equal("/dev/stdout", argv[outputFlag + 1]);
        Assert.Contains("--no-print-diagnostics-summary", argv);
        Assert.Contains("--no-print-metrics-summary", argv);

        // The analysis runs against the database the pre-scan step created:
        // both phases agree on the database path.
        Assert.Equal(createExec.Argv[3], argv[3]);
    }

    [Fact]
    public async Task DatabaseCreationFailure_IsInfrastructure_AnalysisNeverRuns()
    {
        var analyzeExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCreateExec(exec))
                return Task.FromResult(new SandboxExecResult(2, "", "A fatal error occurred: no build command"));
            analyzeExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("codeql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, analyzeExecs);
    }

    [Fact]
    public async Task CleanFixture_Passes_WithNoFindings()
    {
        var sandbox = HealthyTool(scanExit: 0, scanStdout: SarifClean);
        IAuditor auditor = new CodeqlAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsRideExitZero_WhileNonZeroExits_AreInfrastructure()
    {
        // CodeQL exits 0 whether or not alerts were produced — the SARIF
        // document is the verdict, so exit 0 with results is findings.
        IAuditor auditor = new CodeqlAuditor();
        var found = await auditor.RunAsync(
            HealthyTool(0, SarifWithCommandInjection),
            "/work", FakeContext(), CancellationToken.None);
        Assert.False(found.Passed);
        Assert.Single(found.Findings);

        // Exit 2 is CodeQL's failure exit (unknown language, missing
        // database, analysis failure). Even with parseable SARIF on stdout
        // it means "could not run" — there is no non-zero findings exit.
        var errorEx = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(2, SarifWithCommandInjection), "/work", FakeContext(), CancellationToken.None));
        Assert.Contains("exit 2", errorEx.Message, StringComparison.Ordinal);

        // Any other undeclared convention is infrastructure too.
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(
                HealthyTool(1, "some other failure"), "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task SeverityMapping_IsDeclared_NotRawPassThrough()
    {
        // The fixture's error-level rule maps to a blocking Error.
        IAuditor auditor = new CodeqlAuditor();
        var error = await auditor.RunAsync(
            HealthyTool(0, SarifWithCommandInjection),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Error, Assert.Single(error.Findings).Severity);
        Assert.False(error.Passed);

        // A warning-level rule is advisory: findings without a failed audit.
        var warning = await auditor.RunAsync(
            HealthyTool(0, WithRuleSeverity(SarifWithCommandInjection, "warning")),
            "/work", FakeContext(), CancellationToken.None);
        var warningFinding = Assert.Single(warning.Findings);
        Assert.Equal(AuditSeverity.Warning, warningFinding.Severity);
        Assert.True(warning.Passed);

        // A recommendation is informational.
        var note = await auditor.RunAsync(
            HealthyTool(0, WithRuleSeverity(SarifWithCommandInjection, "recommendation", level: "note")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Info, Assert.Single(note.Findings).Severity);
        Assert.True(note.Passed);

        // An unrecognised tool level falls back to the declared default,
        // not to a raw pass-through.
        var unknown = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithCommandInjection, "cosmic")),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Equal(AuditSeverity.Warning, Assert.Single(unknown.Findings).Severity);
    }

    [Fact]
    public async Task RuleSeverity_FallsBackToProblemSeverity_WhenDefaultConfigurationAbsent()
    {
        // CodeQL rule metadata may omit defaultConfiguration; the query's own
        // problem.severity still resolves the finding's severity.
        var withoutDefault = SarifWithCommandInjection.Replace(
            "\"defaultConfiguration\": { \"enabled\": true, \"level\": \"error\" },",
            string.Empty,
            StringComparison.Ordinal);
        IAuditor auditor = new CodeqlAuditor();
        var result = await auditor.RunAsync(
            HealthyTool(0, withoutDefault),
            "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(AuditSeverity.Error, Assert.Single(result.Findings).Severity);
    }

    [Fact]
    public async Task ExplicitResultLevel_WinsOver_RuleMetadata()
    {
        // When a result carries its own level it is honored as-is through
        // the declared mapping, not overwritten from the rule.
        IAuditor auditor = new CodeqlAuditor();
        var result = await auditor.RunAsync(
            HealthyTool(0, WithResultLevel(SarifWithCommandInjection, "note")),
            "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(AuditSeverity.Info, Assert.Single(result.Findings).Severity);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task UnknownLanguage_IsDeterministicInfrastructure_ScanNeverRuns()
    {
        var toolExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CodeqlAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:Language"] = "klingon" }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("klingon", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, toolExecs);
    }

    [Fact]
    public async Task Language_IsConfigurable_AndNormalized()
    {
        SandboxExec? createExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsCreateExec(exec))
            {
                createExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CodeqlAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:Language"] = "Python" }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(createExec);
        Assert.Contains("--language=python", createExec!.Argv);
    }

    [Fact]
    public async Task QuerySuites_AreAppendedToAnalyze_WhenConfigured()
    {
        SandboxExec? analyzeExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCreateExec(exec))
                return Task.FromResult(Ok(exec));
            analyzeExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new CodeqlAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Language"] = "csharp",
                ["Scoped:QuerySuites"] = "csharp-security-extended.qls",
            }),
            CancellationToken.None);
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(analyzeExec);
        Assert.Contains("csharp-security-extended.qls", analyzeExec!.Argv);
    }

    [Fact]
    public async Task DefaultQueries_Run_WhenNoQuerySuitesConfigured()
    {
        SandboxExec? analyzeExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCreateExec(exec))
                return Task.FromResult(Ok(exec));
            analyzeExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(analyzeExec);
        Assert.DoesNotContain(analyzeExec!.Argv, arg => arg.EndsWith(".qls", StringComparison.Ordinal));
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
                return Task.FromResult(new SandboxExecResult(0, "CodeQL command-line toolchain release 2.16.0.\n", ""));
            toolExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("2.16.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CodeqlAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
                    "CodeQL command-line toolchain release "
                        + CodeqlAuditor.DefaultExpectedVersion + ".\n"
                        + "Copyright (C) 2019-2026 GitHub, Inc.\n",
                    ""));
            if (IsCreateExec(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExpectedVersion_IsConfigurable_ThroughScopedConfig()
    {
        var auditor = new CodeqlAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?> { ["Scoped:ExpectedVersion"] = "2.26.0" }),
            CancellationToken.None);

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsCreateExec(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "CodeQL command-line toolchain release 2.26.0.\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifWithCommandInjection, ""));
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
            if (IsPresenceProbe(exec) || IsCreateExec(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "dev-build\n", ""));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new CodeqlAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task OperatorOptions_BindThroughScopedConfig()
    {
        var auditor = new CodeqlAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                // The operator restores vendored paths and narrows to one rule.
                ["Scoped:ExcludePaths"] = "docs/",
                ["Scoped:IncludedRules"] = "py/command-line-injection",
            }),
            CancellationToken.None);

        var vendored = SarifWithCommandInjection.Replace("vuln.py", "vendor/pkg/vuln.py");
        var kept = await ((IAuditor)auditor).RunAsync(
            HealthyTool(0, vendored),
            "/work", FakeContext(), CancellationToken.None);
        Assert.Single(kept.Findings);

        var otherRule = vendored.Replace(
            "py/command-line-injection", "py/other-rule", StringComparison.Ordinal);
        var dropped = await ((IAuditor)auditor).RunAsync(
            HealthyTool(0, otherRule),
            "/work", FakeContext(), CancellationToken.None);
        Assert.True(dropped.Passed);
        Assert.Empty(dropped.Findings);
    }

    // Fixture source assembled at runtime so this test file does not itself
    // carry a scanner-detectable vulnerable literal. Verified against CodeQL
    // 2.27.1 default Python queries to produce exactly one
    // py/command-line-injection alert at vuln.py:6.
    private static readonly string _fixtureVulnPy =
        "import os\n"
        + "from flask import request\n"
        + "\n"
        + "def handle():\n"
        + "    name = request.args.get(\"name\", \"\")\n"
        + "    os.system(\"echo \" + name)\n";

    private static readonly string _fixtureCleanPy =
        "import subprocess\n"
        + "\n"
        + "def run_fixed_command():\n"
        + "    subprocess.call([\"echo\", \"hi\"], shell=False)\n";

    private static readonly string? _installedCodeqlVersion = ProbeInstalledCodeqlVersion();

    /// <summary>
    /// Real-binary end-to-end check: a fixture repository with a known
    /// command injection is scanned by the actual CodeQL CLI through a real
    /// process exec — exercising database creation, the /dev/stdout SARIF
    /// sink, the exit-0-with-findings convention, and severity recovery
    /// together, so a broken real invocation cannot stay green. Runs only
    /// where a codeql binary is on PATH; the auditor's version pin is set to
    /// the installed release.
    /// </summary>
    [Fact]
    [Trait("requires_codeql", "true")]
    public async Task RealCodeql_CommandInjection_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = _installedCodeqlVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(("vuln.py", _fixtureVulnPy));
        try
        {
            var auditor = new CodeqlAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Language"] = "python",
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
            Assert.Equal("codeybox:codeql", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("py/command-line-injection", finding.Title, StringComparison.Ordinal);
            Assert.Equal("vuln.py:6", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    /// <summary>
    /// Companion real-binary check: a fixture repository with no alerts
    /// passes with zero findings — and the analysis still exits 0.
    /// </summary>
    [Fact]
    [Trait("requires_codeql", "true")]
    public async Task RealCodeql_CleanFixtureRepo_Passes_WithNoFindings()
    {
        var installed = _installedCodeqlVersion;
        if (installed is null)
            return;

        var repo = await SeedFixtureRepoAsync(("clean.py", _fixtureCleanPy));
        try
        {
            var auditor = new CodeqlAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Language"] = "python",
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
            s => s.PluginId == CodeqlAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("codeql", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCodeqlRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CodeqlAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CodeqlAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("codeql", tool.Binary);
        // Verify-only by design: CodeQL ships as a release bundle, not a
        // distro package, so no apt line can carry the version pin — the
        // baseline verifies presence and the operator provisions the pinned
        // bundle. No install commands are emitted for this tool.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("codeql", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CodeqlAuditorPlugin.dll");
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
            PluginId: CodeqlAuditor.PluginId,
            PluginDisplayName: "CodeyBox: CodeQL Deep Dataflow SAST",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static string WithRuleSeverity(string sarif, string severity, string? level = null)
        => sarif
            .Replace(
                "\"defaultConfiguration\": { \"enabled\": true, \"level\": \"error\" }",
                "\"defaultConfiguration\": { \"enabled\": true, \"level\": \"" + (level ?? severity) + "\" }",
                StringComparison.Ordinal)
            .Replace(
                "\"problem.severity\": \"error\"",
                "\"problem.severity\": \"" + severity + "\"",
                StringComparison.Ordinal);

    private static string WithResultLevel(string sarif, string level)
        => sarif.Replace(
            "\"ruleIndex\": 0,",
            "\"level\": \"" + level + "\", \"ruleIndex\": 0,",
            StringComparison.Ordinal);

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, CodeqlAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static FakeSandbox HealthyTool(int scanExit, string scanStdout)
        => new((exec, _) => Task.FromResult(
            IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCreateExec(exec)
                ? Ok(exec)
                : new SandboxExecResult(scanExit, scanStdout, "")));

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "codeql" && exec.Argv[1] == "version";

    private static bool IsCreateExec(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "codeql"
            && exec.Argv[1] == "database"
            && exec.Argv[2] == "create";

    private static async Task<string> SeedFixtureRepoAsync((string Name, string Content) file)
    {
        var repo = Path.Combine(
            Path.GetTempPath(), "codeybox-codeql-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        await TestSupport.RunGit(repo, "init", "-b", "main");
        await TestSupport.RunGit(repo, "config", "user.email", "t@l");
        await TestSupport.RunGit(repo, "config", "user.name", "T");
        await File.WriteAllTextAsync(Path.Combine(repo, file.Name), file.Content);
        await TestSupport.RunGit(repo, "add", "-A");
        await TestSupport.RunGit(repo, "commit", "-m", "seed");
        return repo;
    }

    private static string? ProbeInstalledCodeqlVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "codeql",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("version");
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
            // Any failure means no usable codeql on PATH — the gated tests
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
