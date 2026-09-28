using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.ActionlintAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the actionlint auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming actionlint (never a pass or finding).
/// - Exits 0 and 1 are findings-producing; exits 2 (usage error) and 3 (could not run: unreadable file,
///   no .github/workflows found) write plain text, not the JSON report — the parser fails closed so
///   "could not run" is infrastructure, not findings.
/// - JSON errors map to findings with rule ids (kinds) and file/line locations.
/// - Tool kinds are mapped through the declared severity mapping (never passed through).
/// - -format, -init-config, -shellcheck and -pyflakes in ExtraArguments are rejected deterministically
///   (parsing contract, no tree mutation, hermetic integrations).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_actionlint", "true")] use only the checks
///   embedded in the binary (shellcheck/pyflakes stay disabled), so they need the binary but no network.
/// </summary>
public sealed class ActionlintAuditorTests
{
    private static readonly string? InstalledActionlintVersion = ProbeInstalledActionlintVersion();

    private const string JsonWithActionError = """
        [
          {
            "message": "input \"bogus-input\" is not defined in action \"actions/checkout@v4\"",
            "filepath": ".github/workflows/ci.yml",
            "line": 8,
            "column": 11,
            "kind": "action",
            "snippet": "          bogus-input: true\n          ^~~~~~~~~~~~",
            "end_column": 22
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWithSeverities = """
        [
          {
            "message": "could not parse as YAML: did not find expected ',' or ']'",
            "filepath": ".github/workflows/broken.yml",
            "line": 2,
            "column": 1,
            "kind": "syntax-check",
            "snippet": "snippet",
            "end_column": 5
          },
          {
            "message": "\"password\" section in \"container\" section should be specified via secrets",
            "filepath": ".github/workflows/deploy.yml",
            "line": 12,
            "column": 7,
            "kind": "credentials",
            "snippet": "snippet",
            "end_column": 15
          },
          {
            "message": "shell name \"bogus-shell\" is invalid",
            "filepath": ".github/workflows/ci.yml",
            "line": 10,
            "column": 16,
            "kind": "shell-name",
            "snippet": "snippet",
            "end_column": 28
          },
          {
            "message": "some future check fired",
            "filepath": ".github/workflows/future.yml",
            "line": 1,
            "column": 1,
            "kind": "future-rule",
            "snippet": "snippet",
            "end_column": 2
          }
        ]
        """;

    private const string JsonWithVendoredPaths = """
        [
          {
            "message": "root violation",
            "filepath": ".github/workflows/ci.yml",
            "line": 4,
            "column": 1,
            "kind": "action",
            "snippet": "snippet",
            "end_column": 2
          },
          {
            "message": "vendored violation",
            "filepath": "vendor/upstream/.github/workflows/ci.yml",
            "line": 4,
            "column": 1,
            "kind": "action",
            "snippet": "snippet",
            "end_column": 2
          }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingActionlint_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "actionlint: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("actionlint", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingActionlint()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "failed to parse options"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("actionlint", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.6.0\ninstalled by downloading from release page\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("actionlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.6.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ActionlintAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new ActionlintAuditor();
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
    public async Task Fixture_WithKnownIssue_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithActionError, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // An action-kind error is advisory: reported, but the audit passes.
        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:actionlint", finding.AuditorName);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("action", finding.Title, StringComparison.Ordinal);
        Assert.Equal(".github/workflows/ci.yml:8", finding.Location);
        Assert.Contains("bogus-input", finding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("actionlint", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        var formatIndex = argv.ToList().IndexOf("-format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "{{json .}}");
        // Hermetic default: the unpinned shellcheck/pyflakes integrations stay off.
        Assert.Contains("-shellcheck=", argv);
        Assert.Contains("-pyflakes=", argv);
        // Default scope: tool discovery, no positional targets.
        Assert.DoesNotContain(".github/workflows/ci.yml", argv);
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

        IAuditor auditor = new ActionlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FoundSomethingExit_Code1_WithJsonReport_ReportsFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithActionError, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(
            result.Findings,
            f => f.Title.Contains("action", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2, "flag provided but not defined: -bogus-flag")]
    [InlineData(3, "no project was found in any parent directories")]
    public async Task FailedToRunExit_WithoutJsonReport_IsInfrastructureFailure(int exitCode, string stderr)
    {
        // actionlint exits 2 for flag/usage errors and 3 when the scan could
        // not run (unreadable file, no .github/workflows found) — both write
        // plain text, not the JSON report, so the parser fails closed.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", stderr));
        });

        IAuditor auditor = new ActionlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("actionlint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(5, "", "unexpected exit"));
        });

        IAuditor auditor = new ActionlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("actionlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "actionlint: command not found"));
        });

        IAuditor auditor = new ActionlintAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("actionlint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsKindsToCodeyBoxSeverities_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeverities, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);

        var syntax = Assert.Single(result.Findings, f => f.Location == ".github/workflows/broken.yml:2");
        Assert.Equal(AuditSeverity.Error, syntax.Severity);

        var credentials = Assert.Single(result.Findings, f => f.Location == ".github/workflows/deploy.yml:12");
        Assert.Equal(AuditSeverity.Error, credentials.Severity);

        var shellName = Assert.Single(result.Findings, f => f.Location == ".github/workflows/ci.yml:10");
        Assert.Equal(AuditSeverity.Warning, shellName.Severity);

        // An unrecognized kind from a foreign build stays visible as a
        // warning rather than passing through raw or dropping to info.
        var unknown = Assert.Single(result.Findings, f => f.Location == ".github/workflows/future.yml:1");
        Assert.Equal(AuditSeverity.Warning, unknown.Severity);

        // Only the error-severity findings fail the audit: the advisory
        // shell-name and future-rule findings do not.
        Assert.False(result.Passed);

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than
        // the severity field itself.
        Assert.Contains("shell-name", shellName.Description, StringComparison.Ordinal);
        Assert.Contains("syntax-check", syntax.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithActionError, ""));
        });

        var auditor = new ActionlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("-format", "{{json .}}")]
    [InlineData("--format", "{{json .}}")]
    [InlineData("-init-config", "")]
    [InlineData("--init-config", "")]
    [InlineData("-shellcheck", "/usr/bin/shellcheck")]
    [InlineData("--pyflakes", "/usr/bin/pyflakes")]
    public async Task ManagedExtraArguments_AreRejectedAsDeterministicInfrastructure(string flag, string value)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ActionlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = string.IsNullOrEmpty(value) ? flag : $"{flag},{value}",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(flag.TrimStart('-'), ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task AbsoluteTarget_IsRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ActionlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "/etc/passwd",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
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
            s => s.PluginId == ActionlintAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("actionlint", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresActionlintRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ActionlintAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ActionlintAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("actionlint", tool.Binary);
        // Verify-only by design: no distro package carries actionlint, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("actionlint", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.8.0\ninstalled by downloading from release page\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ActionlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.8.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigFileAndTargets_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ActionlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigFile"] = "/etc/codeybox/actionlint.yaml",
                ["Scoped:Targets"] = ".github/workflows/ci.yml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("-config-file");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/etc/codeybox/actionlint.yaml");
        Assert.Contains(".github/workflows/ci.yml", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithVendoredPaths, ""));
        });

        IAuditor auditor = new ActionlintAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal(".github/workflows/ci.yml:4", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithActionError, ""));
        });

        var auditor = new ActionlintAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "runner-label",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    [Trait("requires_actionlint", "true")]
    public async Task RealActionlint_IssueFixture_ProducesFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledActionlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedActionlintFixtureRepoAsync(broken: true);

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

            var auditor = new ActionlintAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("action", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Warning, finding.Severity);
            Assert.Equal(".github/workflows/ci.yml:8", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_actionlint", "true")]
    public async Task RealActionlint_CleanFixture_Passes()
    {
        var installed = InstalledActionlintVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedActionlintFixtureRepoAsync(broken: false);

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

            var auditor = new ActionlintAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ActionlintAuditorPlugin.dll");
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
            PluginId: ActionlintAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Actionlint GitHub Actions",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, ActionlintAuditor.DefaultExpectedVersion + "\ninstalled by downloading from release page\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("actionlint", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "actionlint" && exec.Argv[1] == "-version";

    private static async Task<string> SeedActionlintFixtureRepoAsync(bool broken)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-actionlint-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var workflowsDir = Path.Combine(dir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        // An unknown input on a popular action is a known action-kind error
        // from the binary's embedded database: no plugins to install, no
        // network. The clean variant is a minimal valid workflow.
        var manifest = broken
            ? """
              on: push
              jobs:
                build:
                  runs-on: ubuntu-latest
                  steps:
                    - uses: actions/checkout@v4
                      with:
                        bogus-input: true
              """
            : """
              on: push
              jobs:
                build:
                  runs-on: ubuntu-latest
                  steps:
                    - run: echo hi
              """;
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "ci.yml"), manifest);

        // actionlint discovers the nearest .github/workflows from inside a
        // project; initialize git so project detection succeeds.
        await RunGitAsync(dir, "init", "-q");
        await RunGitAsync(dir, "add", ".");
        return dir;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
    }

    private static string? ProbeInstalledActionlintVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "actionlint",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-version");
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
