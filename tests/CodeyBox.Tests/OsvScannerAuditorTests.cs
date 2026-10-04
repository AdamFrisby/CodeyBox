using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.OsvScannerAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the osv-scanner auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming osv-scanner (never a pass or finding).
/// - osv-scanner's exit convention (verified against 2.6.0 with the auditor's
///   pinned flags): 0 is "ran", 1 is "ran and matched at least one
///   vulnerability", anything else (127 for bad flags and unreadable configs,
///   128 for no package sources, 129 for database query failures, 130 for
///   invalid configs) is "could not run" infrastructure. The SARIF report —
///   not the exit code — is the verdict, so exit 0 with results still reports
///   findings.
/// - SARIF results map to findings with osv-scanner's rule ids and file
///   locations; severities come from each rule's CVSS security-severity score
///   through the declared mapping, never raw (the SARIF level is a constant
///   "warning" upstream and carries no signal).
/// - A repository osv-scanner.toml at any depth is a repo-controlled
///   suppression surface and fails closed.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_osv-scanner", "true")]
///   need osv-scanner on PATH plus OSV database access (network).
/// </summary>
public sealed class OsvScannerAuditorTests
{
    private static readonly string? InstalledOsvScannerVersion = ProbeInstalledVersion("osv-scanner", "--version");
    private static readonly Lazy<bool> OsvScannerEndToEndAvailable = new(ProbeOsvScannerEndToEnd);

    private const string SarifWithFindings =
        """
        {
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": {
                "driver": {
                  "name": "osv-scanner",
                  "version": "2.6.0",
                  "informationUri": "https://github.com/google/osv-scanner",
                  "rules": [
                    {"id": "CVE-2021-23337", "shortDescription": {"text": "CVE-2021-23337: Command Injection in lodash"}, "properties": {"security-severity": "8.1"}},
                    {"id": "CVE-2020-28500", "shortDescription": {"text": "CVE-2020-28500: Regular Expression Denial of Service in lodash"}, "properties": {"security-severity": "5.3"}},
                    {"id": "CVE-2025-13465", "shortDescription": {"text": "CVE-2025-13465: Prototype Pollution in lodash"}, "properties": {"security-severity": "6.9"}},
                    {"id": "CVE-2020-8203", "shortDescription": {"text": "CVE-2020-8203: Prototype Pollution in lodash"}, "properties": {"security-severity": "7.4"}},
                    {"id": "GHSA-unscored-example", "shortDescription": {"text": "GHSA-unscored-example: example without a score"}}
                  ]
                }
              },
              "results": [
                {
                  "ruleId": "CVE-2021-23337",
                  "level": "warning",
                  "message": {"text": "Package 'lodash@4.17.15' is vulnerable to 'CVE-2021-23337' (also known as 'GHSA-35jh-r3h4-6jhm')."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/package-lock.json"}}}]
                },
                {
                  "ruleId": "CVE-2020-28500",
                  "level": "warning",
                  "message": {"text": "Package 'lodash@4.17.15' is vulnerable to 'CVE-2020-28500' (also known as 'GHSA-29mw-wpgm-hmr9')."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/package-lock.json"}}}]
                },
                {
                  "ruleId": "CVE-2025-13465",
                  "level": "warning",
                  "message": {"text": "Package 'lodash@4.17.15' is vulnerable to 'CVE-2025-13465'."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/package-lock.json"}}}]
                },
                {
                  "ruleId": "CVE-2020-8203",
                  "level": "warning",
                  "message": {"text": "Package 'lodash@4.17.15' is vulnerable to 'CVE-2020-8203' (also known as 'GHSA-p6mc-m468-83gw')."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/package-lock.json"}}}]
                },
                {
                  "ruleId": "GHSA-unscored-example",
                  "level": "warning",
                  "message": {"text": "Package 'lodash@4.17.15' is vulnerable to 'GHSA-unscored-example'."},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/sub/package-lock.json"}}}]
                }
              ]
            }
          ]
        }
        """;

    private const string SarifClean =
        """
        {
          "$schema": "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": {
                "driver": {
                  "name": "osv-scanner",
                  "version": "2.6.0",
                  "informationUri": "https://github.com/google/osv-scanner"
                }
              },
              "results": []
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingOsvScanner_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "osv-scanner: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("osv-scanner", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "osv-scanner version: 2.5.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("osv-scanner", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.5.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(OsvScannerAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // Exit 1: ran and matched at least one vulnerability.
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(5, result.Findings.Count);

        var high = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2021-23337", StringComparison.Ordinal));
        Assert.Equal("codeybox:osv-scanner", high.AuditorName);
        Assert.Equal(AuditSeverity.Error, high.Severity);
        Assert.Equal("package-lock.json", high.Location);
        Assert.Contains("lodash@4.17.15", high.Description, StringComparison.Ordinal);

        var medium = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2020-28500", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, medium.Severity);
        Assert.Equal("package-lock.json", medium.Location);

        var nested = Assert.Single(
            result.Findings, f => f.Title.Contains("GHSA-unscored-example", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, nested.Severity);
        Assert.Equal("sub/package-lock.json", nested.Location);

        Assert.NotNull(scanExec);
        Assert.Equal(
            new[] { "osv-scanner", "scan", "source", "--format", "sarif", "--recursive", "--allow-no-lockfiles", "." },
            scanExec!.Argv.ToArray());
    }

    [Fact]
    public async Task Exit0_WithSarifResults_StillReportsFindings()
    {
        // The SARIF report is the verdict, not the exit code: a completed
        // scan exits 0 in edge cases yet must still surface parsed results.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(5, result.Findings.Count);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(127, "bad flag")]
    [InlineData(128, "no package sources")]
    [InlineData(129, "database query failure")]
    [InlineData(130, "invalid config")]
    [InlineData(3, "unknown convention")]
    public async Task FailedRunExits_AreInfrastructureFailures(int exitCode, string _)
    {
        // Every non-{0,1} exit means "could not run": bad flags, no package
        // sources, database failures, invalid configs, or unknown codes fail
        // closed as infrastructure — never as findings, never as a pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", "scan failed"));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("osv-scanner", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsCvssScores_NoRawValuesPassedThrough()
    {
        const string sarif =
            """
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {"driver": {"name": "osv-scanner", "version": "2.6.0", "rules": [
                    {"id": "CVE-2024-0001-critical", "properties": {"security-severity": "9.8"}},
                    {"id": "CVE-2024-0002-high", "properties": {"security-severity": "7.0"}},
                    {"id": "CVE-2024-0003-medium", "properties": {"security-severity": 4.0}},
                    {"id": "CVE-2024-0004-low", "properties": {"security-severity": "0.1"}},
                    {"id": "CVE-2024-0005-unscored"}
                  ]}},
                  "results": [
                    {"ruleId": "CVE-2024-0001-critical", "level": "warning", "message": {"text": "Package 'a@1' is vulnerable to 'CVE-2024-0001-critical'."}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}}}]},
                    {"ruleId": "CVE-2024-0002-high", "level": "warning", "message": {"text": "Package 'a@1' is vulnerable to 'CVE-2024-0002-high'."}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}}}]},
                    {"ruleId": "CVE-2024-0003-medium", "level": "warning", "message": {"text": "Package 'a@1' is vulnerable to 'CVE-2024-0003-medium'."}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}}}]},
                    {"ruleId": "CVE-2024-0004-low", "level": "warning", "message": {"text": "Package 'a@1' is vulnerable to 'CVE-2024-0004-low'."}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}}}]},
                    {"ruleId": "CVE-2024-0005-unscored", "level": "warning", "message": {"text": "Package 'a@1' is vulnerable to 'CVE-2024-0005-unscored'."}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "a.lock"}}}]}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, sarif, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(5, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0001-critical")).Severity);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0002-high")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0003-medium")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0004-low")).Severity);
        // Unscored results fall back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0005-unscored")).Severity);
        // The mapped token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping.
        var high = Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0002-high"));
        Assert.Contains("Severity (tool): high", high.Description, StringComparison.Ordinal);
        var critical = Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0001-critical"));
        Assert.Contains("Severity (tool): critical", critical.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void MapScoreToToken_FollowsCvssBands()
    {
        Assert.Equal("critical", OsvScannerSarifParser.MapScoreToToken(10.0));
        Assert.Equal("critical", OsvScannerSarifParser.MapScoreToToken(9.0));
        Assert.Equal("high", OsvScannerSarifParser.MapScoreToToken(8.9));
        Assert.Equal("high", OsvScannerSarifParser.MapScoreToToken(7.0));
        Assert.Equal("medium", OsvScannerSarifParser.MapScoreToToken(6.9));
        Assert.Equal("medium", OsvScannerSarifParser.MapScoreToToken(4.0));
        Assert.Equal("low", OsvScannerSarifParser.MapScoreToToken(3.9));
        Assert.Equal("low", OsvScannerSarifParser.MapScoreToToken(0.0));
        Assert.Equal("unknown", OsvScannerSarifParser.MapScoreToToken(-1.0));
        Assert.Equal("unknown", OsvScannerSarifParser.MapScoreToToken(double.NaN));
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
                  "tool": {"driver": {"name": "osv-scanner", "version": "2.6.0", "rules": [
                    {"id": "CVE-2024-0001-pkg", "properties": {"security-severity": "8.0"}}
                  ]}},
                  "results": [
                    {"ruleId": "CVE-2024-0001-pkg", "level": "warning", "message": {"text": "vendored copy"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/vendor/upstream/package-lock.json"}}}]},
                    {"ruleId": "CVE-2024-0001-pkg", "level": "warning", "message": {"text": "manifest match"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "file:///work/package-lock.json"}}}]}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, sarif, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("manifest match", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoConfigFile_AtAnyDepth_FailsClosed_AsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoGlobProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./nested/osv-scanner.toml\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new OsvScannerAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("nested/osv-scanner.toml", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RepoConfigFile_TrustedViaScopedConfig_ScanProceeds()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoGlobProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./osv-scanner.toml\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new OsvScannerAuditor();
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
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/baseline/osv-scanner.toml\n/work\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new OsvScannerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/baseline/osv-scanner.toml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/baseline/osv-scanner.toml");
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPathInsideWorktree_IsRejectedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRealpathProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work/custom.toml\n/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new OsvScannerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "custom.toml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("ConfigPath", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        var auditor = new OsvScannerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the two CVSS >= 7.0 matches survive the threshold.
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, SarifWithFindings, ""));
        });

        var auditor = new OsvScannerAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "CVE-2021-23337,CVE-2020-8203",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("-f")]
    [InlineData("--output")]
    [InlineData("--output-file")]
    [InlineData("--config")]
    [InlineData("--recursive")]
    [InlineData("-r")]
    [InlineData("--allow-no-lockfiles")]
    [InlineData("--lockfile")]
    [InlineData("-L")]
    [InlineData("--sbom")]
    [InlineData("-S")]
    [InlineData("--licenses")]
    [InlineData("--serve")]
    public async Task ReservedExtraArguments_AreRejectedDeterministically(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoGlobProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new OsvScannerAuditor();
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
            s => s.PluginId == OsvScannerAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("osv-scanner", flattened, StringComparison.Ordinal);
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
                Enabled = [OsvScannerAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == OsvScannerAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var osvScanner = Assert.Single(tools, t => t.Binary == "osv-scanner");
        // Verify-only by design: no distro package carries a pinned
        // osv-scanner — the operator provisions the upstream release binary.
        Assert.Null(osvScanner.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("osv-scanner", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_osv-scanner", "true")]
    public async Task RealOsvScanner_VulnerableFixture_ProducesFindingWithRuleIdAndLocation()
    {
        if (InstalledOsvScannerVersion is null || !OsvScannerEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedOsvScannerFixtureRepoAsync(vulnerable: true);

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

            var auditor = new OsvScannerAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledOsvScannerVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);
            // The live database may report the same rule id more than once
            // (one result per affected package instance), so assert presence
            // rather than singularity.
            Assert.Contains(
                result.Findings, f => f.Title.Contains("CVE-2021-23337", StringComparison.Ordinal));
            var finding = result.Findings.First(
                f => f.Title.Contains("CVE-2021-23337", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("package-lock.json", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_osv-scanner", "true")]
    public async Task RealOsvScanner_CleanFixture_Passes()
    {
        if (InstalledOsvScannerVersion is null || !OsvScannerEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedOsvScannerFixtureRepoAsync(vulnerable: false);

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

            var auditor = new OsvScannerAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledOsvScannerVersion,
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.OsvScannerAuditorPlugin.dll");
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
            PluginId: OsvScannerAuditor.PluginId,
            PluginDisplayName: "CodeyBox: OSV-Scanner Dependency Vulnerabilities",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "osv-scanner version: " + OsvScannerAuditor.DefaultExpectedVersion + "\n", "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("osv-scanner", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "osv-scanner" && exec.Argv[1] == "--version";

    private static bool IsRepoGlobProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("find", StringComparison.Ordinal)
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsRealpathProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "realpath";

    private static async Task<string> SeedOsvScannerFixtureRepoAsync(bool vulnerable)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-osvscanner-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (!vulnerable)
        {
            // The clean fixture carries no packages at all: any catalogued
            // package could gain an advisory in a later database, but a
            // package-free tree matches nothing by construction (and the
            // auditor's --allow-no-lockfiles keeps the empty scan a pass).
            await File.WriteAllTextAsync(
                Path.Combine(dir, "README.md"), "# clean fixture\n");
            return dir;
        }

        // A pinned npm lockfile entry: osv-scanner matches it against the OSV
        // database with no build or install step.
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

    private static bool ProbeOsvScannerEndToEnd()
    {
        // The real-binary tests need OSV database access (network), not just
        // the binary: run the exact scan the auditor performs against an
        // empty directory and require a clean verdict.
        try
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "codeybox-osvscanner-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "osv-scanner",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("scan");
                psi.ArgumentList.Add("source");
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add("sarif");
                psi.ArgumentList.Add("--recursive");
                psi.ArgumentList.Add("--allow-no-lockfiles");
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
