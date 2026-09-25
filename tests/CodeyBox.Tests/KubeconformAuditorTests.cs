using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.KubeconformAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the kubeconform auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming kubeconform (never a pass or finding).
/// - Exits 0 and 1 are verdicts; a run failure also exits 1 but writes no JSON report —
///   the parser fails closed so "could not run" is infrastructure, not findings.
/// - JSON resources map to findings with synthesized rule ids (kubeconform/&lt;Kind&gt;,
///   kubeconform/error) and file locations.
/// - Tool statuses are mapped through the declared severity mapping (never passed through).
/// - Flag-looking ExtraArguments are rejected deterministically (Go flag parsing stops at
///   positional targets, so they would silently become file names).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_kubeconform", "true")] use a
///   fixture-local schema directory, so they need the binary but no network.
/// </summary>
public sealed class KubeconformAuditorTests
{
    private static readonly string? InstalledKubeconformVersion = ProbeInstalledKubeconformVersion();

    private const string JsonWithInvalidAndErrorResources = """
        {
          "resources": [
            {
              "filename": "deploy/bad.yaml",
              "kind": "Deployment",
              "name": "bad-deploy",
              "version": "apps/v1",
              "status": "statusInvalid",
              "msg": "problem validating schema. Check JSON formatting",
              "validationErrors": [
                { "path": "/spec/replicas", "msg": "expected integer or null, but got string" }
              ]
            },
            {
              "filename": "deploy/broken.yaml",
              "status": "statusError",
              "msg": "error while parsing: yaml: line 4: could not find expected ':'"
            },
            {
              "filename": "deploy/good.yaml",
              "kind": "Pod",
              "name": "good-pod",
              "version": "v1",
              "status": "statusValid",
              "msg": ""
            },
            {
              "filename": "deploy/skipped.yaml",
              "kind": "ConfigMap",
              "name": "skipped",
              "version": "v1",
              "status": "statusSkipped",
              "msg": ""
            }
          ],
          "summary": { "valid": 1, "invalid": 1, "errors": 1, "skipped": 1 }
        }
        """;

    private const string JsonClean = """
        {
          "resources": [],
          "summary": { "valid": 2, "invalid": 0, "errors": 0, "skipped": 0 }
        }
        """;

    private const string JsonWithStatuses = """
        {
          "resources": [
            { "filename": "a.yaml", "kind": "Deployment", "name": "a", "version": "apps/v1",
              "status": "statusInvalid", "msg": "schema violation" },
            { "filename": "b.yaml", "status": "statusError", "msg": "could not find schema" },
            { "filename": "c.yaml", "kind": "Pod", "name": "c", "version": "v1",
              "status": "statusSomethingNew", "msg": "unknown future status" },
            { "filename": "d.yaml", "kind": "Pod", "name": "d", "version": "v1",
              "status": "statusValid", "msg": "" },
            { "filename": "e.yaml", "kind": "Pod", "name": "e", "version": "v1",
              "status": "statusSkipped", "msg": "" }
          ],
          "summary": { "valid": 1, "invalid": 1, "errors": 1, "skipped": 1 }
        }
        """;

    private const string JsonWithFilteredPaths = """
        {
          "resources": [
            { "filename": "deploy/bad.yaml", "kind": "Deployment", "name": "a", "version": "apps/v1",
              "status": "statusInvalid", "msg": "root manifest violation" },
            { "filename": "vendor/charts/bad.yaml", "kind": "Deployment", "name": "v", "version": "apps/v1",
              "status": "statusInvalid", "msg": "vendored manifest violation" },
            { "filename": "third_party/ops/bad.yaml", "kind": "Service", "name": "t", "version": "v1",
              "status": "statusInvalid", "msg": "third-party manifest violation" },
            { "filename": "node_modules/pkg/bad.yaml", "kind": "Pod", "name": "n", "version": "v1",
              "status": "statusInvalid", "msg": "dependency manifest violation" }
          ],
          "summary": { "valid": 0, "invalid": 4, "errors": 0, "skipped": 0 }
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingKubeconform_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "kubeconform: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingKubeconform()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "flag provided but not defined"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task DevelopmentBuild_IsInfrastructureFailure()
    {
        // A kubeconform built from source reports "development" — unpinned.
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "development\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "v0.7.1\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.7.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(KubeconformAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new KubeconformAuditor();
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
    public async Task Fixture_WithInvalidAndErrorResources_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithInvalidAndErrorResources, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var invalid = Assert.Single(
            result.Findings, f => f.Title.Contains("kubeconform/Deployment", StringComparison.Ordinal));
        Assert.Equal("codeybox:kubeconform", invalid.AuditorName);
        Assert.Equal(AuditSeverity.Error, invalid.Severity);
        Assert.Equal("deploy/bad.yaml", invalid.Location);
        Assert.Contains("/spec/replicas", invalid.Description, StringComparison.Ordinal);
        Assert.Contains("bad-deploy", invalid.Description, StringComparison.Ordinal);

        var error = Assert.Single(
            result.Findings, f => f.Title.Contains("kubeconform/error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);
        Assert.Equal("deploy/broken.yaml", error.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("kubeconform", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        var outputIndex = argv.ToList().IndexOf("-output");
        Assert.True(outputIndex >= 0 && outputIndex + 1 < argv.Count);
        Assert.Equal("json", argv[outputIndex + 1]);
        Assert.Contains("-summary", argv);
        // Default scope: whole work tree, last positional argument.
        Assert.Equal(".", argv[^1]);
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

        IAuditor auditor = new KubeconformAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJsonReport_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithInvalidAndErrorResources, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        // kubeconform exits 1 (not the usual 2) for flag/usage errors too —
        // "could not run" writes a plain-text error to stderr and no report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "failed parsing command line: flag provided but not defined: -bogus"));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "unexpected exit"));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "kubeconform: command not found"));
        });

        IAuditor auditor = new KubeconformAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("kubeconform", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsStatusesToError_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithStatuses, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // statusInvalid, statusError, and the unrecognized statusSomethingNew
        // are findings; statusValid and statusSkipped are never reported.
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.All(result.Findings,
            f => Assert.DoesNotContain("statusInvalid", f.Title, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Findings, f => f.Location == "a.yaml");
        Assert.Contains(result.Findings, f => f.Location == "b.yaml");
        Assert.Contains(result.Findings, f => f.Location == "c.yaml");
        Assert.DoesNotContain(result.Findings, f => f.Location == "d.yaml");
        Assert.DoesNotContain(result.Findings, f => f.Location == "e.yaml");

        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping rather than the
        // severity field itself.
        var invalid = Assert.Single(result.Findings, f => f.Location == "a.yaml");
        Assert.Contains("statusInvalid", invalid.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagLikeExtraArguments_AreRejectedAsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KubeconformAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = "-strict",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == KubeconformAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("kubeconform", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresKubeconformRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [KubeconformAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == KubeconformAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("kubeconform", tool.Binary);
        // Verify-only by design: no distro package carries kubeconform, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("kubeconform", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "v0.9.0\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KubeconformAuditor();
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
    public async Task ScopedConfiguration_SchemaLocations_BecomeRepeatableArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KubeconformAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SchemaLocations"] = "/opt/k8s-schemas,https://schemas.example.com/{{ .ResourceKind }}.json",
                ["Scoped:KubernetesVersion"] = "1.32.0",
                ["Scoped:Strict"] = "true",
                ["Scoped:IgnoreMissingSchemas"] = "true",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var locations = argv
            .Select((arg, i) => (arg, i))
            .Where(t => t.arg == "-schema-location")
            .Select(t => argv[t.i + 1])
            .ToList();
        Assert.Equal(2, locations.Count);
        Assert.Contains("/opt/k8s-schemas", locations);
        Assert.Contains("https://schemas.example.com/{{ .ResourceKind }}.json", locations);
        var versionIndex = argv.ToList().IndexOf("-kubernetes-version");
        Assert.True(versionIndex >= 0 && argv[versionIndex + 1] == "1.32.0");
        Assert.Contains("-strict", argv);
        Assert.Contains("-ignore-missing-schemas", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_InvalidKubernetesVersion_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KubeconformAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:KubernetesVersion"] = "v1.32",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("KubernetesVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_Targets_OverrideDefaultScope()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new KubeconformAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Targets"] = "manifests/, deploy/app.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.DoesNotContain(".", argv);
        Assert.Contains("manifests/", argv);
        Assert.Contains("deploy/app.yaml", argv);
        // Targets stay positional: flags precede them in argv so Go's flag
        // parser (which stops at the first non-flag) still parses them all.
        var outputIndex = argv.ToList().IndexOf("-output");
        var targetsIndex = argv.ToList().IndexOf("manifests/");
        Assert.True(outputIndex >= 0 && outputIndex < targetsIndex);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new KubeconformAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Single(result.Findings);
        Assert.Equal("deploy/bad.yaml", result.Findings[0].Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithInvalidAndErrorResources, ""));
        });

        var auditor = new KubeconformAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "kubeconform/Deployment",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("kubeconform/Deployment", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_kubeconform", "true")]
    public async Task RealKubeconform_InvalidDeploymentFixture_ProducesFinding()
    {
        var installed = InstalledKubeconformVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKubeconformFixtureRepoAsync(valid: false);

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

            var auditor = new KubeconformAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    // Fixture-local schemas keep the real-binary test offline:
                    // kubeconform resolves <dir>/master-standalone/<kind>-<group>-<version>.json.
                    ["Scoped:SchemaLocations"] = Path.Combine(fixtureDir, "schemas"),
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("kubeconform/Deployment", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("deploy.yaml", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_kubeconform", "true")]
    public async Task RealKubeconform_ValidDeploymentFixture_Passes()
    {
        var installed = InstalledKubeconformVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedKubeconformFixtureRepoAsync(valid: true);

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

            var auditor = new KubeconformAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:SchemaLocations"] = Path.Combine(fixtureDir, "schemas"),
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.KubeconformAuditorPlugin.dll");
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
            PluginId: KubeconformAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Kubeconform Kubernetes Manifests",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "v" + KubeconformAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("kubeconform", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "kubeconform" && exec.Argv[1] == "-v";

    private static async Task<string> SeedKubeconformFixtureRepoAsync(bool valid)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-kubeconform-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var schemaDir = Path.Combine(dir, "schemas", "master-standalone");
        Directory.CreateDirectory(schemaDir);

        // Minimal schema for apps/v1 Deployment: draft-agnostic (no $schema),
        // only constructs every jsonschema version supports.
        var deploymentSchema = """
            {
              "type": "object",
              "required": ["apiVersion", "kind", "metadata"],
              "properties": {
                "apiVersion": { "type": "string" },
                "kind": { "type": "string" },
                "metadata": { "type": "object" },
                "spec": {
                  "type": "object",
                  "properties": {
                    "replicas": { "type": "integer" }
                  }
                }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(schemaDir, "deployment-apps-v1.json"), deploymentSchema);

        var manifest = valid
            ? """
              apiVersion: apps/v1
              kind: Deployment
              metadata:
                name: sample
              spec:
                replicas: 2
              """
            : """
              apiVersion: apps/v1
              kind: Deployment
              metadata:
                name: sample
              spec:
                replicas: "three"
              """;
        await File.WriteAllTextAsync(Path.Combine(dir, "deploy.yaml"), manifest);

        return dir;
    }

    private static string? ProbeInstalledKubeconformVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kubeconform",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-v");
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
