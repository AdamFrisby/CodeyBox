using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PlutoAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the pluto auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming pluto (never a pass or finding).
/// - A missing or malformed TargetKubernetesVersion is a deterministic configuration failure (never an assumed target).
/// - Exits 0/2/3/4 are verdicts; exit 1 (finder/flag failure) writes no JSON report —
///   the parser fails closed so "could not run" is infrastructure, not findings.
/// - Reports without a target-versions block, malformed JSON, truncated output, and empty
///   stdout are infrastructure, never a pass; a missing items array on a completed report is clean.
/// - JSON items map to findings with stable rule ids (pluto/removed, pluto/deprecated,
///   pluto/replacement-unavailable, pluto/api) and repo-relative locations; removed and
///   replacement-unavailable findings are errors, deprecated findings are warnings.
/// - A scan root with no candidate manifests is infrastructure ("inspected nothing"), never a pass.
/// - Verdict-shaping ExtraArguments are rejected deterministically; PLUTO_* environment
///   variables are removed from the scan exec.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// </summary>
public sealed class PlutoAuditorTests
{
    private const string TargetVersion = "v1.22.0";

    private const string JsonWithAllClasses = """
        {
          "items": [
            {
              "name": "old-ingress",
              "namespace": "default",
              "filePath": "deploy/ingress.yaml",
              "api": {
                "version": "extensions/v1beta1",
                "kind": "Ingress",
                "deprecated-in": "v1.14.0",
                "removed-in": "v1.22.0",
                "replacement-api": "networking.k8s.io/v1",
                "replacement-available-in": "v1.19.0",
                "component": "k8s"
              },
              "deprecated": true,
              "removed": true,
              "replacementAvailable": true
            },
            {
              "name": "old-deploy",
              "namespace": "default",
              "filePath": "deploy/app.yaml",
              "api": {
                "version": "extensions/v1beta1",
                "kind": "Deployment",
                "deprecated-in": "v1.9.0",
                "removed-in": "v1.16.0",
                "replacement-api": "apps/v1",
                "replacement-available-in": "v1.9.0",
                "component": "k8s"
              },
              "deprecated": true,
              "removed": false,
              "replacementAvailable": true
            },
            {
              "name": "old-psp",
              "namespace": "",
              "filePath": "deploy/psp.yaml",
              "api": {
                "version": "extensions/v1beta1",
                "kind": "PodSecurityPolicy",
                "deprecated-in": "v1.10.0",
                "removed-in": "v1.25.0",
                "replacement-api": "",
                "replacement-available-in": "",
                "component": "k8s"
              },
              "deprecated": true,
              "removed": false,
              "replacementAvailable": false
            }
          ],
          "target-versions": { "k8s": "v1.22.0" }
        }
        """;

    private const string JsonDeprecatedOnly = """
        {
          "items": [
            {
              "name": "old-deploy",
              "namespace": "default",
              "filePath": "deploy/app.yaml",
              "api": {
                "version": "extensions/v1beta1",
                "kind": "Deployment",
                "deprecated-in": "v1.9.0",
                "removed-in": "v1.16.0",
                "replacement-api": "apps/v1",
                "replacement-available-in": "v1.9.0",
                "component": "k8s"
              },
              "deprecated": true,
              "removed": false,
              "replacementAvailable": true
            }
          ],
          "target-versions": { "k8s": "v1.12.0" }
        }
        """;

    private const string JsonCleanOmittedItems = """
        {
          "target-versions": { "k8s": "v1.22.0" }
        }
        """;

    private const string JsonCleanEmptyItems = """
        {
          "items": [],
          "target-versions": { "k8s": "v1.22.0" }
        }
        """;

    private const string JsonMissingTargetVersions = """
        {
          "items": [
            {
              "name": "old-deploy",
              "filePath": "deploy/app.yaml",
              "api": { "version": "extensions/v1beta1", "kind": "Deployment" },
              "deprecated": true,
              "removed": false,
              "replacementAvailable": true
            }
          ]
        }
        """;

    private const string JsonUnknownItemShape = """
        {
          "items": [
            { "name": "mystery", "filePath": "deploy/mystery.yaml" }
          ],
          "target-versions": { "k8s": "v1.22.0" }
        }
        """;

    private const string JsonExplicitlyCleanItem = """
        {
          "items": [
            {
              "name": "fine",
              "filePath": "deploy/fine.yaml",
              "api": { "version": "apps/v1", "kind": "Deployment" },
              "deprecated": false,
              "removed": false,
              "replacementAvailable": true
            }
          ],
          "target-versions": { "k8s": "v1.22.0" }
        }
        """;

    private const string JsonWithVendoredFindings = """
        {
          "items": [
            {
              "name": "root-ingress",
              "filePath": "deploy/ingress.yaml",
              "api": { "version": "extensions/v1beta1", "kind": "Ingress" },
              "deprecated": true,
              "removed": true,
              "replacementAvailable": true
            },
            {
              "name": "vendored-ingress",
              "filePath": "vendor/charts/ingress.yaml",
              "api": { "version": "extensions/v1beta1", "kind": "Ingress" },
              "deprecated": true,
              "removed": true,
              "replacementAvailable": true
            }
          ],
          "target-versions": { "k8s": "v1.22.0" }
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingPluto_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "pluto: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingPluto()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "unknown command \"version\""));
            if (IsCoverageProbe(exec))
                return Task.FromResult(CoverageFound(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Version:5.20.0 Commit:abc123\n", ""));
            if (IsCoverageProbe(exec))
                return Task.FromResult(CoverageFound(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
        Assert.Contains("5.20.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PlutoAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingTargetKubernetesVersion_IsDeterministicInfrastructure_NeverAssumesTarget()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        // No InitializeAsync: the default accessor supplies no target version.
        IAuditor auditor = new PlutoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("TargetKubernetesVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Theory]
    [InlineData("1.29.0")]
    [InlineData("v1.29")]
    [InlineData("latest")]
    [InlineData("v1.29.0 " + "\nrm -rf /")]
    public async Task InvalidTargetKubernetesVersion_IsDeterministicInfrastructure(string target)
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = target,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("TargetKubernetesVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task MultipleTargets_AreRejectedAsDeterministicInfrastructure()
    {
        var execs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            execs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:Targets"] = "manifests/,overlays/",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("single directory", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, execs);
    }

    [Fact]
    public async Task Fixture_AllFindingClasses_YieldFindings_WithRuleIdSeverityAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(3, JsonWithAllClasses, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var removed = Assert.Single(
            result.Findings, f => f.Title.Contains("pluto/removed", StringComparison.Ordinal));
        Assert.Equal("codeybox:pluto", removed.AuditorName);
        Assert.Equal(AuditSeverity.Error, removed.Severity);
        Assert.Equal("deploy/ingress.yaml", removed.Location);
        Assert.Contains("extensions/v1beta1", removed.Description, StringComparison.Ordinal);
        Assert.Contains("k8s=v1.22.0", removed.Description, StringComparison.Ordinal);
        Assert.Contains("networking.k8s.io/v1", removed.Description, StringComparison.Ordinal);

        var deprecated = Assert.Single(
            result.Findings, f => f.Title.Contains("pluto/deprecated", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, deprecated.Severity);
        Assert.Equal("deploy/app.yaml", deprecated.Location);
        Assert.Contains("apps/v1", deprecated.Description, StringComparison.Ordinal);

        var unavailable = Assert.Single(
            result.Findings, f => f.Title.Contains("pluto/replacement-unavailable", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, unavailable.Severity);
        Assert.Equal("deploy/psp.yaml", unavailable.Location);

        Assert.NotNull(scanExec);
        Assert.Equal(
            new[] { "pluto", "detect-files", "-d", ".", "--target-versions", "k8s=v1.22.0", "-o", "json" },
            scanExec!.Argv);
        Assert.Contains("PLUTO_TARGET_VERSIONS", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("PLUTO_ADDITIONAL_VERSIONS", scanExec.EnvironmentVariablesToUnset);
        Assert.Contains("PLUTO_IGNORE_REMOVALS", scanExec.EnvironmentVariablesToUnset);
    }

    [Fact]
    public async Task DeprecatedOnly_PassesWithWarningFinding()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonDeprecatedOnly, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("pluto/deprecated", finding.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(JsonCleanOmittedItems)]
    [InlineData(JsonCleanEmptyItems)]
    public async Task CleanReport_Passes_WithProofOfTarget(string stdout)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, stdout, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FindingsExits_WithJsonReport_ReportFindings(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, JsonWithAllClasses, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutJsonReport_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "Error running finder: lstat /work/missing: no such file or directory"));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(125)]
    public async Task UnknownExit_IsInfrastructureFailure(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, JsonWithAllClasses, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "pluto: command not found"));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"items\": [{\"name\": \"cut off")]
    [InlineData("[1, 2, 3]")]
    public async Task MalformedOrEmptyOutput_IsInfrastructureFailure(string stdout)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(2, stdout, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportWithoutTargetVersions_IsInfrastructureFailure_NeverAcceptsEmptyProof()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonMissingTargetVersions, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("target-versions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownItemShape_YieldsErrorFinding_NeverPassesSilently()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(2, JsonUnknownItemShape, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("pluto/api", finding.Title, StringComparison.Ordinal);
        Assert.Equal("deploy/mystery.yaml", finding.Location);
    }

    [Fact]
    public async Task ExplicitlyCleanItem_IsSkipped()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonExplicitlyCleanItem, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NoCandidateManifests_IsInfrastructureFailure_NeverAVacuousPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            if (IsCoverageProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("no candidate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scanExecs);
    }

    [Theory]
    [InlineData("--target-versions=k8s=v9.9.9")]
    [InlineData("--output=yaml")]
    [InlineData("-d")]
    [InlineData("-f=versions.yaml")]
    [InlineData("--additional-versions=versions.yaml")]
    [InlineData("--ignore-removals")]
    [InlineData("--components=cert-manager")]
    [InlineData("-r")]
    public async Task VerdictShapingExtraArguments_AreRejectedAsDeterministicInfrastructure(string extra)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:ExtraArguments"] = extra,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScanTimeout_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox(async (exec, ct) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return ProbeOk(exec);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new SandboxExecResult(0, JsonCleanOmittedItems, "");
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:TimeoutSeconds"] = "1",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pluto", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledRun_PropagatesCancellation_NeverReturnsVerdict()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), cts.Token));
    }

    [Fact]
    public async Task MaxFindings_TruncatesResultSet()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(3, JsonWithAllClasses, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:MaxFindings"] = "2",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public async Task DefaultExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(3, JsonWithVendoredFindings, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(TargetContext(), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("deploy/ingress.yaml", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            return Task.FromResult(new SandboxExecResult(3, JsonWithAllClasses, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:IncludedRules"] = "pluto/removed",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("pluto/removed", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_SingleTarget_BecomesDashD()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            if (IsCoverageProbe(exec))
                return Task.FromResult(CoverageFound(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:Targets"] = "manifests",
            }),
            CancellationToken.None);
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Equal(
            new[] { "pluto", "detect-files", "-d", "manifests", "--target-versions", "k8s=v1.22.0", "-o", "json" },
            scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_OnlyShowRemoved_BecomesArgument()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsCoverageProbe(exec))
                return Task.FromResult(ProbeOk(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:OnlyShowRemoved"] = "true",
            }),
            CancellationToken.None);
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("--only-show-removed", scanExec!.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ExpectedVersion_OverridesDefault()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "Version:5.21.0 Commit:abc123\n", ""));
            if (IsCoverageProbe(exec))
                return Task.FromResult(CoverageFound(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonCleanOmittedItems, ""));
        });

        var auditor = new PlutoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TargetKubernetesVersion"] = TargetVersion,
                ["Scoped:ExpectedVersion"] = "5.21.0",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
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
            s => s.PluginId == PlutoAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("pluto", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresPlutoRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PlutoAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PlutoAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("pluto", tool.Binary);
        // Verify-only by design: no distro package carries pluto, so the
        // pinned release must be provisioned into the baseline by the operator.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("pluto", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static PluginContext TargetContext()
        => BuildPluginContext(new Dictionary<string, string?>
        {
            ["Scoped:TargetKubernetesVersion"] = TargetVersion,
        });

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PlutoAuditorPlugin.dll");
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
            PluginId: PlutoAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Pluto Kubernetes Deprecations",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult ProbeOk(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Version:" + PlutoAuditor.DefaultExpectedVersion + " Commit:deadbeef\n", "")
            : IsCoverageProbe(exec)
                ? CoverageFound(exec)
                : new SandboxExecResult(0, "", "");

    private static SandboxExecResult CoverageFound(SandboxExec exec)
    {
        // Glob probes (find -path) echo "./"-relative matches: answer with a
        // path under the requested glob's literal prefix so the auditor's
        // re-verification accepts it.
        if (exec.Argv.Count >= 3
            && exec.Argv[2].Contains("find .", StringComparison.Ordinal))
        {
            var glob = exec.Argv.Count > 4 ? exec.Argv[4] : "*.yaml";
            var star = glob.IndexOf('*');
            var prefix = star >= 0 ? glob[..star] : glob.TrimEnd('/') + "/";
            return new SandboxExecResult(0, "./" + prefix + "app.yaml\n", "");
        }
        // Exact-file probes echo the requested path verbatim.
        var first = exec.Argv.Count > 4 ? exec.Argv[4] : "deploy/app.yaml";
        return new SandboxExecResult(0, first + "\n", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("pluto", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "pluto" && exec.Argv[1] == "version";

    private static bool IsCoverageProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

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
