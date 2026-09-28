using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.ConftestAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the conftest auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming conftest (never a pass or finding).
/// - Exits 0 and 1 are findings-producing; exit 1 without the JSON check-result array
///   (no policies found, bad flags, unreadable input) and any other exit fail closed as
///   infrastructure through the parser — the report, not the exit code, is the discriminator.
/// - JSON results map to findings with rule queries (metadata.query) and file/line locations.
/// - Tool categories are mapped through the declared severity mapping (never passed through).
/// - --output, --fail-on-warn, --no-fail, --quiet, --suppress-exceptions and --update in
///   ExtraArguments are rejected deterministically (parsing contract, exit-code contract,
///   no remote fetches during the audit).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_conftest", "true")] use a
///   fixture-local policy directory, so they need the binary but no network.
/// </summary>
public sealed class ConftestAuditorTests
{
    private static readonly string? InstalledConftestVersion = ProbeInstalledConftestVersion();

    private const string JsonWithFailure = """
        [
          {
            "filename": "deployment.yaml",
            "namespace": "main",
            "successes": 2,
            "failures": [
              {
                "msg": "Containers must not run as root",
                "metadata": {"query": "data.main.deny"},
                "loc": {"file": "deployment.yaml", "line": 12}
              }
            ]
          }
        ]
        """;

    private const string JsonClean = "[]";

    private const string JsonWithOnlySkipped = """
        [
          {
            "filename": "deployment.yaml",
            "namespace": "main",
            "successes": 1,
            "skipped": [
              {
                "msg": "a skip is not a problem",
                "metadata": {"query": "data.main.skip"}
              }
            ]
          }
        ]
        """;

    private const string JsonWithCategories = """
        [
          {
            "filename": "deployment.yaml",
            "namespace": "main",
            "successes": 1,
            "warnings": [
              {
                "msg": "Found service hello-kubernetes but services are not allowed",
                "metadata": {"query": "data.main.warn"}
              }
            ],
            "failures": [
              {
                "msg": "Containers must not run as root",
                "metadata": {"query": "data.main.deny"},
                "loc": {"file": "deployment.yaml", "line": 12}
              }
            ],
            "exceptions": [
              {
                "msg": "rego runtime error: divide by zero",
                "metadata": {"query": "data.main.deny"}
              }
            ]
          }
        ]
        """;

    private const string JsonWithVendoredPaths = """
        [
          {
            "filename": "deploy/app.yaml",
            "namespace": "main",
            "successes": 0,
            "failures": [
              {"msg": "root violation", "metadata": {"query": "data.main.deny"}}
            ]
          },
          {
            "filename": "vendor/charts/app.yaml",
            "namespace": "main",
            "successes": 0,
            "failures": [
              {"msg": "vendored violation", "metadata": {"query": "data.main.deny"}}
            ]
          },
          {
            "filename": ".terraform/modules/vpc/main.tf",
            "namespace": "main",
            "successes": 0,
            "failures": [
              {"msg": "generated module violation", "metadata": {"query": "data.main.deny"}}
            ]
          }
        ]
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingConftest_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "conftest: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ConftestAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("conftest", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingConftest()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "unknown command"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ConftestAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("conftest", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Conftest: 0.60.0\nOPA: 0.68.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ConftestAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("conftest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.60.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ConftestAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new ConftestAuditor();
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
    public async Task Fixture_WithKnownFailure_YieldsFinding_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithFailure, ""));
        });

        IAuditor auditor = new ConftestAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // A deny failure is blocking: reported as an error, and the audit fails.
        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("codeybox:conftest", finding.AuditorName);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("data.main.deny", finding.Title, StringComparison.Ordinal);
        Assert.Equal("deployment.yaml:12", finding.Location);
        Assert.Contains("Containers must not run as root", finding.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("conftest", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        Assert.Contains("test", argv);
        var outputIndex = argv.ToList().IndexOf("--output");
        Assert.True(outputIndex >= 0 && argv[outputIndex + 1] == "json");
        // Default scope: the whole work tree, no operator policy override.
        Assert.Contains(".", argv);
        Assert.DoesNotContain("--policy", argv);
        Assert.DoesNotContain("--namespace", argv);
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

        IAuditor auditor = new ConftestAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task SkippedOnlyFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithOnlySkipped, ""));
        });

        IAuditor auditor = new ConftestAuditor();
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
            return Task.FromResult(new SandboxExecResult(1, JsonWithFailure, ""));
        });

        IAuditor auditor = new ConftestAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.Contains(
            result.Findings,
            f => f.Title.Contains("data.main.deny", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "Error: no policies found in [policy]")]
    [InlineData(2, "policy failures with --fail-on-warn")]
    public async Task FailedToRunExit_WithoutJsonReport_IsInfrastructureFailure(int exitCode, string stderr)
    {
        // Exit 1 is ambiguous in conftest: failures print the JSON report,
        // while run failures (no policies found, bad flags, unreadable
        // input) write plain text — the parser fails closed. Exit 2 only
        // arises with the rejected --fail-on-warn flag.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", stderr));
        });

        IAuditor auditor = new ConftestAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("conftest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExit_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, "", "unexpected exit"));
        });

        IAuditor auditor = new ConftestAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("conftest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "conftest: command not found"));
        });

        IAuditor auditor = new ConftestAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("conftest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsCategoriesToCodeyBoxSeverities_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithCategories, ""));
        });

        IAuditor auditor = new ConftestAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);

        var failure = Assert.Single(result.Findings, f => f.Location == "deployment.yaml:12");
        Assert.Equal(AuditSeverity.Error, failure.Severity);

        // An evaluation exception fails closed as an error — loud, never a
        // silent pass — and falls back to the evaluated file for location.
        var exception = Assert.Single(result.Findings, f => f.Title.Contains("deny", StringComparison.Ordinal)
            && f.Description.Contains("divide by zero", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, exception.Severity);
        Assert.Equal("deployment.yaml", exception.Location);

        var warning = Assert.Single(result.Findings, f => f.Title.Contains("data.main.warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warning.Severity);
        Assert.Equal("deployment.yaml", warning.Location);

        // Only the error-severity findings fail the audit: the advisory
        // warning alone would pass.
        Assert.False(result.Passed);

        // The raw tool category is preserved in the description (tool
        // severity line), proving the value flowed through the mapping
        // rather than the severity field itself.
        Assert.Contains("failure", failure.Description, StringComparison.Ordinal);
        Assert.Contains("warning", warning.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinimumSeverity_DropsAdvisoryFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithCategories, ""));
        });

        var auditor = new ConftestAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        // Exit 0 carries warnings and exceptions in the report (warnings
        // never affect conftest's exit code); the error threshold drops the
        // warning but keeps the failure and the exception.
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("--output", "json")]
    [InlineData("-o", "json")]
    [InlineData("--fail-on-warn", "")]
    [InlineData("--no-fail", "")]
    [InlineData("--quiet", "")]
    [InlineData("--suppress-exceptions", "")]
    [InlineData("--update", "oci://example.com/policies")]
    [InlineData("-u", "oci://example.com/policies")]
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

        var auditor = new ConftestAuditor();
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

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside")]
    public async Task NonRepoRelativeTarget_IsRejectedAsDeterministicInfrastructure(string target)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ConftestAuditor();
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
            s => s.PluginId == ConftestAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("conftest", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresConftestRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ConftestAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ConftestAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("conftest", tool.Binary);
        // Verify-only by design: no distro package carries conftest, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("conftest", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Conftest: 0.69.0\nOPA: 1.8.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ConftestAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "0.69.0",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_PolicyPathNamespaceAndTargets_BecomeToolArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ConftestAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:PolicyPath"] = "/etc/codeybox/policy",
                ["Scoped:Namespace"] = "k8s.main",
                ["Scoped:Targets"] = "deploy/app.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var policyIndex = argv.ToList().IndexOf("--policy");
        Assert.True(policyIndex >= 0 && argv[policyIndex + 1] == "/etc/codeybox/policy");
        var namespaceIndex = argv.ToList().IndexOf("--namespace");
        Assert.True(namespaceIndex >= 0 && argv[namespaceIndex + 1] == "k8s.main");
        Assert.Contains("deploy/app.yaml", argv);
        // An explicit target replaces the default whole-tree scan.
        Assert.DoesNotContain(".", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_AllNamespaces_SuppressesNamespaceSelection()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ConftestAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Namespace"] = "k8s.main",
                ["Scoped:AllNamespaces"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--all-namespaces", scanExec!.Argv);
        Assert.DoesNotContain("--namespace", scanExec.Argv);
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

        IAuditor auditor = new ConftestAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("deploy/app.yaml", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFailure, ""));
        });

        var auditor = new ConftestAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "data.main.warn",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.True(result.Passed);
    }

    [Fact]
    [Trait("requires_conftest", "true")]
    public async Task RealConftest_IssueFixture_ProducesFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledConftestVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedConftestFixtureRepoAsync(denyViolation: true);

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

            var auditor = new ConftestAuditor();
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
                f => f.Title.Contains("data.main.deny", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("deployment.yaml", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_conftest", "true")]
    public async Task RealConftest_CleanFixture_Passes()
    {
        var installed = InstalledConftestVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedConftestFixtureRepoAsync(denyViolation: false);

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

            var auditor = new ConftestAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ConftestAuditorPlugin.dll");
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
            PluginId: ConftestAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Conftest Policy-as-Code",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            // conftest --version prints its own release first, then the OPA
            // version: the shared first-token extraction must pin Conftest,
            // not OPA.
            ? new SandboxExecResult(0, $"Conftest: {ConftestAuditor.DefaultExpectedVersion}\nOPA: 1.10.0\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("conftest", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "conftest" && exec.Argv[1] == "--version";

    private static async Task<string> SeedConftestFixtureRepoAsync(bool denyViolation)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-conftest-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var policyDir = Path.Combine(dir, "policy");
        Directory.CreateDirectory(policyDir);

        // A hermetic Rego v1 policy over the deployment's security context:
        // no plugins to install, no network.
        var policy = """
            package main

            deny contains msg if {
              input.kind == "Deployment"
              not input.spec.template.spec.securityContext.runAsNonRoot
              msg := "Containers must not run as root"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(policyDir, "deployment.rego"), policy);

        // A deny without _loc metadata reports the evaluated file as
        // its location.
        var manifest = denyViolation
            ? """
              apiVersion: apps/v1
              kind: Deployment
              metadata:
                name: bad-deploy
              spec:
                replicas: 1
              """
            : """
              apiVersion: apps/v1
              kind: Deployment
              metadata:
                name: good-deploy
              spec:
                template:
                  spec:
                    securityContext:
                      runAsNonRoot: true
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "deployment.yaml"), manifest);

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

    private static string? ProbeInstalledConftestVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "conftest",
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
