using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.ValeAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the vale auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming vale (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
///   Exit 0 is NOT a clean bill: warnings/suggestions still arrive in the JSON report.
/// - A verdict-class exit without vale JSON output fails closed as an infrastructure failure.
/// - Vale JSON output maps file-keyed alerts to findings with rule ids (Check), locations, and
///   mapped severity; "./"-prefixed and absolute report paths are normalized to repo-relative.
/// - Raw tool levels ("suggestion"/"warning"/"error") go through the declared mapping.
/// - Default scope ("." input, --no-global, ExcludePaths finding backstop) and scoped options
///   (ExpectedVersion, ConfigPath, Inputs).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_vale", "true")].
/// </summary>
public sealed class ValeAuditorTests
{
    private static readonly string? InstalledValeVersion = ProbeInstalledValeVersion();

    private const string JsonWithAlerts = """
        {
          "README.md": [
            {
              "Check": "Vale.Spelling",
              "Description": "",
              "Line": 6,
              "Link": "",
              "Message": "Did you really mean 'detials'?",
              "Severity": "error",
              "Span": [23, 30],
              "Match": "detials"
            },
            {
              "Check": "write-good.Weasel",
              "Description": "",
              "Line": 3,
              "Link": "",
              "Message": "'very' is a weasel word!",
              "Severity": "warning",
              "Span": [17, 21],
              "Match": "very"
            }
          ],
          "./docs/guide.md": [
            {
              "Check": "Vale.Repetition",
              "Description": "",
              "Line": 12,
              "Link": "",
              "Message": "Consider removing 'the the'.",
              "Severity": "suggestion",
              "Span": [5, 12],
              "Match": "the the"
            }
          ]
        }
        """;

    private const string JsonClean = "{}";

    private const string JsonWithVendoredPaths = """
        {
          "vendor/lib/README.md": [
            {
              "Check": "Vale.Spelling",
              "Line": 1,
              "Message": "Did you really mean 'teh'?",
              "Severity": "error",
              "Span": [1, 4]
            }
          ],
          "docs/guide.md": [
            {
              "Check": "Vale.Spelling",
              "Line": 7,
              "Message": "Did you really mean 'teh'?",
              "Severity": "error",
              "Span": [1, 4]
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingVale_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "vale: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingVale()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "vale version 0.99.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.99.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ValeAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new ValeAuditor();
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
    public async Task Fixture_WithAlerts_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithAlerts, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var spelling = Assert.Single(result.Findings, f => f.Title.Contains("detials", StringComparison.Ordinal));
        Assert.Equal("codeybox:vale", spelling.AuditorName);
        Assert.Equal(AuditSeverity.Error, spelling.Severity);
        Assert.Contains("Vale.Spelling", spelling.Title, StringComparison.Ordinal);
        Assert.Equal("README.md:6", spelling.Location);

        var weasel = Assert.Single(result.Findings, f => f.Title.Contains("weasel", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, weasel.Severity);
        Assert.Contains("write-good.Weasel", weasel.Title, StringComparison.Ordinal);
        Assert.Equal("README.md:3", weasel.Location);

        // Report keys carrying the walker's "./" prefix are relativized.
        var repetition = Assert.Single(result.Findings, f => f.Title.Contains("Vale.Repetition", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, repetition.Severity);
        Assert.Equal("docs/guide.md:12", repetition.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("vale", scanExec!.Argv[0]);
        Assert.Contains("--output", scanExec.Argv);
        Assert.Contains("JSON", scanExec.Argv);
        // Ambient user-level configuration never steers the verdict.
        Assert.Contains("--no-global", scanExec.Argv);
        Assert.Equal(".", scanExec.Argv[^1]);
    }

    [Fact]
    public async Task ExitCode0_WithWarningsOnly_StillReportsFindings_AndPasses()
    {
        // Vale's exit 0 means "no error-level alert" — warnings and
        // suggestions still arrive in the JSON report. The auditor must not
        // read the common "0 = clean" convention into this tool.
        const string warningsOnly = """
            {
              "README.md": [
                {
                  "Check": "write-good.Weasel",
                  "Line": 3,
                  "Message": "'very' is a weasel word!",
                  "Severity": "warning",
                  "Span": [17, 21]
                }
              ]
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, warningsOnly, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("write-good.Weasel", finding.Title, StringComparison.Ordinal);
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

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_IsTheFoundSomethingExit_ReportsFindings_NotInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithAlerts, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Vale exits 2 when it could not run: missing configuration, a
            // rule that failed to load, an unknown argument.
            return Task.FromResult(new SandboxExecResult(2, "", "E100 [doLint] configuration not found"));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerdictExit_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 with no JSON report fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(1, "", "internal error"));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountsShapedJson_IsInfrastructureFailure_NotASilentPass()
    {
        // An operator --counts override wraps the report as
        // {"files": {...}, "counts": {...}}: top-level entries stop being
        // alert arrays. Parsing that shape as zero findings would be a
        // silent pass — it must fail closed instead.
        const string countsShape = """
            {"files": {}, "counts": {"Test.Utilise": 0}}
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, countsShape, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "vale: command not found"));
        });

        IAuditor auditor = new ValeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("vale", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithAlerts, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // "error" -> Error; "warning" -> Warning; "suggestion" -> Info.
        // Raw vale vocabulary never reaches the finding severity — it stays
        // visible only in the description's "Severity (tool):" line.
        Assert.Equal(AuditSeverity.Error, Assert.Single(result.Findings, f => f.Title.Contains("Vale.Spelling", StringComparison.Ordinal)).Severity);
        var weasel = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.Contains("Severity (tool): warning", weasel.Description, StringComparison.Ordinal);
        var repetition = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Info);
        Assert.Contains("Severity (tool): suggestion", repetition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsoluteReportPaths_RelativizeAgainstWorkingDirectory()
    {
        const string absolutePaths = """
            {
              "/work/docs/guide.md": [
                {
                  "Check": "Vale.Spelling",
                  "Line": 2,
                  "Message": "Did you really mean 'teh'?",
                  "Severity": "error",
                  "Span": [1, 4]
                }
              ]
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, absolutePaths, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("docs/guide.md:2", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_PassedAsArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ValeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/etc/codeybox/vale.ini",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0, "Expected '--config' in vale argv.");
        Assert.Equal("/etc/codeybox/vale.ini", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_Inputs_ReplaceDefaultWholeTreeScope()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ValeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Inputs"] = "docs,README.md",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.DoesNotContain(".", scanExec!.Argv.Skip(1));
        Assert.Contains("docs", scanExec.Argv);
        Assert.Contains("README.md", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_Inputs_EscapingWorktree_FailsClosed()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ValeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Inputs"] = "../outside",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("Inputs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithVendoredPaths, ""));
        });

        IAuditor auditor = new ValeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("docs/guide.md:7", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_SelectsChecks()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithAlerts, ""));
        });

        var auditor = new ValeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "Vale.Spelling",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("Vale.Spelling", finding.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--output=JSON")]
    [InlineData("--output")]
    public async Task ExtraArguments_AttachedOutputForm_SuppressesBuiltin(string extraArg)
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ValeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArg,
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        // The operator's own --output wins; the auditor must not emit a
        // second one that would conflict with or silently override it.
        Assert.Equal(1, scanExec!.Argv.Count(a => a == "--output" || a.StartsWith("--output=", StringComparison.Ordinal)));
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
            s => s.PluginId == ValeAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);
    }

    [Fact]
    public void EnabledPlugin_DeclaresValeRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ValeAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ValeAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("vale", tool.Binary);
        // Verify-only by design: no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("vale", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_vale", "true")]
    public async Task RealVale_AlertFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledValeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedValeFixtureRepoAsync(clean: false);

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

            var auditor = new ValeAuditor();
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
            Assert.Contains("Test.Utilise", finding.Title, StringComparison.Ordinal);
            Assert.Equal("bad.md:1", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_vale", "true")]
    public async Task RealVale_CleanFixture_Passes()
    {
        var installed = InstalledValeVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedValeFixtureRepoAsync(clean: true);

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

            var auditor = new ValeAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ValeAuditorPlugin.dll");
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
            PluginId: ValeAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Vale Prose Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "vale version " + ValeAuditor.DefaultExpectedVersion + "\n", "")
            // Presence probes: exit 0 with empty stdout = probed files absent.
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("vale", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "vale" && exec.Argv[1] == "--version";

    private static async Task<string> SeedValeFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-vale-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "styles", "Test"));

        await File.WriteAllTextAsync(
            Path.Combine(dir, ".vale.ini"),
            """
            StylesPath = styles
            MinAlertLevel = suggestion

            [*.md]
            BasedOnStyles = Test
            """);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "styles", "Test", "Utilise.yml"),
            """
            extends: substitution
            message: "Use '%s' instead of '%s'."
            level: error
            swap:
              utilise: use
            """);

        if (clean)
            await File.WriteAllTextAsync(Path.Combine(dir, "ok.md"), "Please use the API.\n");
        else
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.md"), "Please utilise the API.\n");

        return dir;
    }

    private static string? ProbeInstalledValeVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "vale",
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
