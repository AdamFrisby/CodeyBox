using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.TrivyAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the trivy auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming trivy (never a pass or finding).
/// - Trivy's exit convention (verified against 0.74.0 with no --exit-code flag):
///   0 is "ran" (clean or with findings — the SARIF is the verdict), anything else
///   (1 for bad flags, unscannable targets, bad config, database failures) is
///   "could not run" infrastructure. --exit-code is never passed because trivy
///   exits 1 both for threshold-tripped findings and for fatal errors.
/// - SARIF results map to findings with trivy's rule ids and file locations;
///   trivy's SARIF levels go through the declared mapping, never raw.
/// - Repository trivy.yaml / .trivyignore / trivy-secret.yaml files are a
///   repo-controlled suppression surface and fail closed.
/// - An operator ConfigPath resolving inside the worktree is rejected
///   deterministically; reserved ExtraArguments flags are rejected likewise.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_trivy", "true")] need
///   trivy on PATH plus a usable vulnerability database (network).
/// </summary>
public sealed class TrivyAuditorTests
{
    private static readonly string? InstalledTrivyVersion = ProbeInstalledVersion("trivy", "--version");
    private static readonly Lazy<bool> TrivyEndToEndAvailable = new(ProbeTrivyEndToEnd);

    private const string SarifWithFindings =
        """
        {
          "version": "2.1.0",
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json",
          "runs": [
            {
              "tool": {
                "driver": {
                  "fullName": "Trivy Vulnerability Scanner",
                  "informationUri": "https://github.com/aquasecurity/trivy",
                  "name": "Trivy",
                  "version": "0.74.0"
                }
              },
              "results": [
                {
                  "ruleId": "CVE-2020-8203",
                  "level": "error",
                  "message": {"text": "Package: lodash\nInstalled Version: 4.17.15\nVulnerability CVE-2020-8203\nSeverity: HIGH\nFixed Version: 4.17.19"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "package-lock.json"}, "region": {"startLine": 1, "startColumn": 1, "endLine": 1, "endColumn": 1}}}]
                },
                {
                  "ruleId": "CVE-2023-44487",
                  "level": "error",
                  "message": {"text": "Package: nghttp2\nInstalled Version: 1.43.0\nVulnerability CVE-2023-44487\nSeverity: HIGH\nFixed Version: 1.52.0"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "var/lib/dpkg/status"}, "region": {"startLine": 1, "startColumn": 1, "endLine": 1, "endColumn": 1}}}]
                },
                {
                  "ruleId": "DS-0002",
                  "level": "error",
                  "message": {"text": "Artifact: Dockerfile\nType: dockerfile\nVulnerability DS-0002\nSeverity: HIGH\nMessage: Last USER command in Dockerfile should not be 'root'"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "Dockerfile"}, "region": {"startLine": 1, "startColumn": 1, "endLine": 1, "endColumn": 1}}}]
                },
                {
                  "ruleId": "CVE-2020-28500",
                  "level": "warning",
                  "message": {"text": "Package: lodash\nInstalled Version: 4.17.15\nVulnerability CVE-2020-28500\nSeverity: MEDIUM\nFixed Version: 4.17.21"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "package-lock.json"}, "region": {"startLine": 1, "startColumn": 1, "endLine": 1, "endColumn": 1}}}]
                },
                {
                  "ruleId": "AWS-0089",
                  "level": "note",
                  "message": {"text": "Artifact: main.tf\nType: terraform\nVulnerability AWS-0089\nSeverity: LOW\nMessage: Bucket has logging disabled"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "main.tf"}, "region": {"startLine": 1, "startColumn": 1, "endLine": 1, "endColumn": 1}}}]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifClean =
        """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": {
                "driver": {
                  "fullName": "Trivy Vulnerability Scanner",
                  "name": "Trivy",
                  "version": "0.74.0"
                }
              },
              "results": []
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTrivy_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "trivy: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("trivy", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "Version: 0.60.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("trivy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.60.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(TrivyAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(5, result.Findings.Count);

        var dependency = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2020-8203", StringComparison.Ordinal));
        Assert.Equal("codeybox:trivy", dependency.AuditorName);
        Assert.Equal(AuditSeverity.Error, dependency.Severity);
        Assert.Contains("package-lock.json", dependency.Location, StringComparison.Ordinal);

        var container = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2023-44487", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, container.Severity);
        Assert.Contains("nghttp2", container.Description, StringComparison.Ordinal);

        var config = Assert.Single(
            result.Findings, f => f.Title.Contains("DS-0002", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, config.Severity);
        Assert.Contains("Dockerfile", config.Location, StringComparison.Ordinal);

        var medium = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2020-28500", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, medium.Severity);

        var low = Assert.Single(
            result.Findings, f => f.Title.Contains("AWS-0089", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, low.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("trivy", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        Assert.Contains("fs", argv, StringComparer.Ordinal);
        Assert.Contains(".", argv, StringComparer.Ordinal);
        var formatIndex = argv.IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "sarif");
        var scannersIndex = argv.IndexOf("--scanners");
        Assert.True(scannersIndex >= 0 && argv[scannersIndex + 1] == TrivyAuditor.DefaultScanners);
        var severityIndex = argv.IndexOf("--severity");
        Assert.True(severityIndex >= 0 && argv[severityIndex + 1] == "UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL");
        Assert.Contains("--quiet", argv, StringComparer.Ordinal);
        Assert.Contains("--skip-version-check", argv, StringComparer.Ordinal);
        Assert.DoesNotContain("--exit-code", argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit1_FailedRun_IsInfrastructureFailure()
    {
        // Trivy exits 1 on every run failure (bad flag, unscannable target,
        // unparseable config, database load failure) — log text, no SARIF
        // verdict — so it fails closed as infrastructure, never as findings.
        // This is also the exit --exit-code 1 would produce for findings,
        // which is why the auditor never passes that flag: the exit alone
        // cannot separate "found something" from "could not run".
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "FATAL\tFatal error\trun error: fs scan error"));
        });

        IAuditor auditor = new TrivyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("trivy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit2_UnknownConvention_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithFindings, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("trivy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_NoRawStringsPassedThrough()
    {
        const string sarif =
            """
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {"driver": {"name": "Trivy", "version": "0.74.0"}},
                  "results": [
                    {"ruleId": "CVE-2024-0001-highpkg", "level": "error", "message": {"text": "high"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "CVE-2024-0002-medpkg", "level": "warning", "message": {"text": "medium"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "CVE-2024-0003-lowpkg", "level": "note", "message": {"text": "low"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "CVE-2024-0004-futurepkg", "level": "brand-new-future-level", "message": {"text": "future"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}, "region": {"startLine": 1}}}]}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, sarif, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0001-highpkg")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0002-medpkg")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0003-lowpkg")).Severity);
        // Unknown tool level falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0004-futurepkg")).Severity);
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping.
        var high = Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0001-highpkg"));
        Assert.Contains("Severity (tool): error", high.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultExcludePaths_DropsVendoredFindings()
    {
        const string sarif =
            """
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {"driver": {"name": "Trivy", "version": "0.74.0"}},
                  "results": [
                    {"ruleId": "CVE-2020-8203", "level": "error", "message": {"text": "vendored copy"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "vendor/upstream/lodash.js"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "CVE-2021-23337", "level": "error", "message": {"text": "manifest match"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "package-lock.json"}, "region": {"startLine": 1}}}]}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, sarif, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("CVE-2021-23337", finding.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trivy.yaml")]
    [InlineData(".trivyignore")]
    [InlineData("trivy-secret.yaml")]
    public async Task RepoConfigFile_FailsClosed_AsDeterministicInfrastructure(string fileName)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, fileName + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new TrivyAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(fileName, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepoConfigFile_TrustedViaScopedConfig_ScanProceeds()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "trivy.yaml\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:TrustRepositorySuppression"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/baseline/trivy.yaml\n/work\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/baseline/trivy.yaml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/baseline/trivy.yaml");
    }

    [Fact]
    public async Task ScopedConfiguration_InTreeConfigPath_IsRejectedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work/policy/trivy.yaml\n/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "policy/trivy.yaml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(TrivyAuditor.ConfigPathKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_Scanners_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Scanners"] = "vuln",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var scannersIndex = argv.IndexOf("--scanners");
        Assert.True(scannersIndex >= 0 && argv[scannersIndex + 1] == "vuln");
    }

    [Fact]
    public async Task ScopedConfiguration_UnknownScanner_FailsClosedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Scanners"] = "vuln,nonsense",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(TrivyAuditor.ScannersKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the three error-level matches survive the threshold.
        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "CVE-2020-8203,CVE-2023-44487,DS-0002",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("-f")]
    [InlineData("--output")]
    [InlineData("-o")]
    [InlineData("--exit-code")]
    [InlineData("--config")]
    [InlineData("-c")]
    [InlineData("--scanners")]
    [InlineData("--severity")]
    [InlineData("-s")]
    [InlineData("--ignorefile")]
    [InlineData("--secret-config")]
    public async Task ReservedExtraArguments_AreRejectedDeterministically(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new TrivyAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = flag,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ExtraArguments", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
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
            s => s.PluginId == TrivyAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("trivy", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirements_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [TrivyAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == TrivyAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var trivy = Assert.Single(tools, t => t.Binary == "trivy");
        // Verify-only by design: no distro package carries a pinned trivy —
        // the operator provisions the upstream release binary.
        Assert.Null(trivy.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("trivy", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_trivy", "true")]
    public async Task RealTrivy_VulnerableFixture_ProducesFindingWithRuleIdAndLocation()
    {
        if (InstalledTrivyVersion is null || !TrivyEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedTrivyFixtureRepoAsync(vulnerable: true);

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

            var auditor = new TrivyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledTrivyVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var lodashFindings = result.Findings
                .Where(f => f.Description.Contains("lodash", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(lodashFindings);
            var finding = lodashFindings.First(f => f.Severity == AuditSeverity.Error);
            Assert.Contains("CVE-", finding.Title, StringComparison.Ordinal);
            Assert.Contains("package-lock.json", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_trivy", "true")]
    public async Task RealTrivy_CleanFixture_Passes()
    {
        if (InstalledTrivyVersion is null || !TrivyEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedTrivyFixtureRepoAsync(vulnerable: false);

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

            var auditor = new TrivyAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledTrivyVersion,
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.TrivyAuditorPlugin.dll");
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
            PluginId: TrivyAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Trivy Dependency, Config and Container Vulnerabilities",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "Version: " + TrivyAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("trivy", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "trivy" && exec.Argv[1] == "--version";

    private static bool IsRepoFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("-e", StringComparison.Ordinal)
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "realpath";

    private static async Task<string> SeedTrivyFixtureRepoAsync(bool vulnerable)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-trivy-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (!vulnerable)
        {
            // The clean fixture carries no packages at all: any catalogued
            // package could gain an advisory in a later database, but an
            // empty tree matches nothing by construction.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "README.md"), "# clean fixture\n");
            return dir;
        }

        // A pinned npm lockfile entry: trivy matches it against the
        // vulnerability database with no build or install step.
        const string lodashVersion = "4.17.15";
        await File.WriteAllTextAsync(Path.Combine(dir, "package.json"), """
            {"name": "fixture", "version": "1.0.0", "dependencies": {"lodash": "LODASH"}}
            """.Replace("LODASH", lodashVersion));
        await File.WriteAllTextAsync(Path.Combine(dir, "package-lock.json"), """
            {
              "name": "fixture",
              "version": "1.0.0",
              "lockfileVersion": 3,
              "requires": true,
              "packages": {
                "": {"name": "fixture", "version": "1.0.0", "dependencies": {"lodash": "LODASH"}},
                "node_modules/lodash": {"version": "LODASH", "resolved": "https://registry.npmjs.org/lodash/-/lodash-LODASH.tgz"}
              }
            }
            """.Replace("LODASH", lodashVersion));

        return dir;
    }

    private static string? ProbeInstalledVersion(string binary, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(argument);
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

    private static bool ProbeTrivyEndToEnd()
    {
        // The real-binary tests need a usable vulnerability database
        // (network), not just the binary: run the exact scan the auditor
        // performs against an empty directory and require a clean verdict.
        try
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "codeybox-trivy-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "trivy",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("fs");
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add("sarif");
                psi.ArgumentList.Add("--quiet");
                psi.ArgumentList.Add("--scanners");
                psi.ArgumentList.Add("vuln");
                psi.ArgumentList.Add(dir);
                using var process = Process.Start(psi)!;
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(milliseconds: 180_000))
                {
                    try { process.Kill(); } catch { /* best-effort probe teardown */ }
                    return false;
                }
                Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromSeconds(5));
                var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
                return process.ExitCode == 0 && stdout.Contains("\"results\"", StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(dir);
            }
        }
        catch
        {
            return false;
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
