using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.DependencyCheckAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the dependency-check auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming
///   dependency-check (never a pass or finding).
/// - dependency-check's exit convention (verified against the 12.1.0 App
///   source with the pinned --failOnCVSS 0): 0 is "ran clean", 15 is "ran
///   and matched at least one vulnerability" — both carry the JSON report
///   as the verdict. Anything else (1/2 CLI parse, 4 settings, 8/9 update,
///   11 database, 12 report, 13/14 analysis, 126/127 cannot execute) is
///   "could not run" infrastructure.
/// - JSON (dependency, vulnerability) pairs map to findings with the
///   vulnerability name as rule id and the relativized dependency filePath;
///   the tool's severity vocabulary goes through the declared mapping,
///   never raw.
/// - Suppression files travel only through the guarded SuppressionPaths
///   knob: in-worktree resolutions fail closed and deterministically.
/// - Plugin is disabled by default, absent from baseline provisioning until
///   enabled.
/// - Real binary execution tests under
///   [Trait("requires_dependency_check", "true")] need dependency-check on
///   PATH plus a seeded NVD database (network).
/// </summary>
public sealed class DependencyCheckAuditorTests
{
    private static readonly string? InstalledDependencyCheckVersion =
        ProbeInstalledVersion("dependency-check", "--version");
    private static readonly Lazy<bool> DependencyCheckEndToEndAvailable = new(ProbeDependencyCheckEndToEnd);

    private const string JsonWithFindings =
        """
        {
          "scanInfo": {"engineVersion": "12.1.0"},
          "projectInfo": {"name": "fixture", "reportDate": "2026-01-01T00:00:00.000Z", "credits": {}},
          "dependencies": [
            {
              "isVirtual": false,
              "fileName": "package-lock.json",
              "filePath": "/work/package-lock.json",
              "md5": "d41d8cd98f00b204e9800998ecf8427e",
              "sha1": "da39a3ee5e6b4b0d3255bfef95601890afd80709",
              "vulnerabilities": [
                {
                  "source": "NVD",
                  "name": "CVE-2021-23337",
                  "severity": "HIGH",
                  "description": "Lodash command injection before 4.17.21.",
                  "cvssv3": {"baseScore": 7.2, "baseSeverity": "HIGH"},
                  "cwes": ["CWE-94"]
                },
                {
                  "source": "NVD",
                  "name": "CVE-2020-28500",
                  "severity": "MEDIUM",
                  "description": "Lodash denial of service.",
                  "cvssv3": {"baseScore": 5.3, "baseSeverity": "MEDIUM"},
                  "cwes": ["CWE-400"]
                },
                {
                  "source": "OSSINDEX",
                  "name": "CVE-2019-9999",
                  "severity": "LOW",
                  "description": "Minor issue.",
                  "cwes": []
                },
                {
                  "source": "OSSINDEX",
                  "name": "CVE-2019-8888",
                  "unscored": "true",
                  "severity": "Unknown",
                  "description": "No CVSS available."
                }
              ]
            },
            {
              "isVirtual": false,
              "fileName": "struts-core-2.0.0.jar",
              "filePath": "/work/lib/struts-core-2.0.0.jar",
              "md5": "d41d8cd98f00b204e9800998ecf8427e",
              "sha1": "da39a3ee5e6b4b0d3255bfef95601890afd80709",
              "vulnerabilities": [
                {
                  "source": "NVD",
                  "name": "CVE-2017-5638",
                  "severity": "CRITICAL",
                  "description": "Struts2 Jakarta Multipart parser RCE.",
                  "cvssv3": {"baseScore": 10.0, "baseSeverity": "CRITICAL"},
                  "cwes": ["CWE-20"]
                }
              ]
            }
          ]
        }
        """;

    private const string JsonClean =
        """
        {
          "scanInfo": {"engineVersion": "12.1.0"},
          "projectInfo": {"name": "fixture", "reportDate": "2026-01-01T00:00:00.000Z", "credits": {}},
          "dependencies": []
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingDependencyCheck_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "dependency-check: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dependency-check", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "dependency-check version 11.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dependency-check", ex.Message, StringComparison.Ordinal);
        Assert.Contains("11.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DependencyCheckAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        SandboxExec? reportRead = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
            {
                reportRead = exec;
                return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
            }
            scanExec = exec;
            // --failOnCVSS 0 tripped: at least one vulnerability matched.
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(5, result.Findings.Count);

        var critical = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2017-5638", StringComparison.Ordinal));
        Assert.Equal("codeybox:dependency-check", critical.AuditorName);
        Assert.Equal(AuditSeverity.Error, critical.Severity);
        Assert.Contains("lib/struts-core-2.0.0.jar", critical.Location, StringComparison.Ordinal);
        Assert.Contains("CVSS 10", critical.Description, StringComparison.Ordinal);

        var high = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2021-23337", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, high.Severity);
        Assert.Contains("package-lock.json", high.Location, StringComparison.Ordinal);

        var medium = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2020-28500", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, medium.Severity);

        var low = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2019-9999", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, low.Severity);

        var unscored = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2019-8888", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unscored.Severity);

        // The scan is a structured argv headed by the tool — never a
        // constructed shell string — pinning the report sink, format, scope,
        // and exit contract.
        Assert.NotNull(scanExec);
        Assert.Equal("dependency-check", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        Assert.DoesNotContain("sh", argv, StringComparer.Ordinal);
        Assert.Contains("--scan", argv, StringComparer.Ordinal);
        Assert.True(argv.IndexOf("--scan") + 1 < argv.Count && argv[argv.IndexOf("--scan") + 1] == ".");
        var formatIndex = argv.IndexOf("--format");
        Assert.True(formatIndex >= 0 && argv[formatIndex + 1] == "JSON");
        var outIndex = argv.IndexOf("--out");
        Assert.True(outIndex >= 0 && argv[outIndex + 1].EndsWith(
            "dependency-check-report.json", StringComparison.Ordinal));
        var failOnIndex = argv.IndexOf("--failOnCVSS");
        Assert.True(failOnIndex >= 0 && argv[failOnIndex + 1] == "0");

        // The report is read back from the exact file the scan was told to
        // write — never from captured stdout.
        Assert.NotNull(reportRead);
        Assert.Equal("cat", reportRead!.Argv[0]);
        Assert.Equal(argv[outIndex + 1], reportRead.Argv[1]);
    }

    [Fact]
    public async Task Exit0_CleanReport_Passes()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit0_WithReportResults_StillReportsFindings()
    {
        // The JSON report is the verdict, not the exit code: a completed
        // scan below no threshold still exits 0 in principle, and any report
        // content must surface regardless.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
            return Task.FromResult(new SandboxExecResult(0, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(5, result.Findings.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task NonFindingsExit_IsInfrastructureFailure(int exitCode)
    {
        // Every exit outside {0, 15} means "could not run" — including 14,
        // which fires after reports are written but still marks an
        // incomplete scan, and 1/2, which share no code with the
        // findings-present signal.
        var reportReads = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
            {
                reportReads++;
                return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
            }
            return Task.FromResult(new SandboxExecResult(exitCode, "", $"simulated exit {exitCode}"));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dependency-check", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, reportReads);
    }

    [Fact]
    public async Task MissingReportFile_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "cat: no such file"));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dependency-check", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnparseableReportFile_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, "this is not json", ""));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dependency-check", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedReportFile_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, "", StdoutLimitExceeded: true));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("dependency-check", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsToolLevels_NoRawStringsPassedThrough()
    {
        const string json =
            """
            {
              "scanInfo": {"engineVersion": "12.1.0"},
              "projectInfo": {"name": "fixture"},
              "dependencies": [
                {
                  "fileName": "a.jar",
                  "filePath": "/work/a.jar",
                  "vulnerabilities": [
                    {"source": "NVD", "name": "CVE-2024-0001", "severity": "CRITICAL", "description": "c"},
                    {"source": "NVD", "name": "CVE-2024-0002", "severity": "HIGH", "description": "h"},
                    {"source": "NVD", "name": "CVE-2024-0003", "severity": "MEDIUM", "description": "m"},
                    {"source": "NVD", "name": "CVE-2024-0004", "severity": "LOW", "description": "l"},
                    {"source": "NVD", "name": "CVE-2024-0005", "severity": "NONE", "description": "n"},
                    {"source": "OSSINDEX", "name": "CVE-2024-0006", "severity": "Unknown", "description": "u"},
                    {"source": "NVD", "name": "CVE-2024-0007", "severity": "brand-new-future-level", "description": "f"},
                    {"source": "NVD", "name": "CVE-2024-0008", "description": "no severity at all"}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, json, ""));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(8, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0001")).Severity);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0002")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0003")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0004")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0005")).Severity);
        // Unscored "Unknown" is advisory, never blocking.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0006")).Severity);
        // Unknown tool levels and absent levels fall back to the declared
        // default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0007")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0008")).Severity);
        // The raw tool token is preserved in the description (tool severity
        // line), proving the value flowed through the mapping.
        var high = Assert.Single(result.Findings, f => f.Title.Contains("CVE-2024-0002"));
        Assert.Contains("Severity (tool): HIGH", high.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultExcludePaths_DropsVendoredFindings()
    {
        const string json =
            """
            {
              "scanInfo": {"engineVersion": "12.1.0"},
              "projectInfo": {"name": "fixture"},
              "dependencies": [
                {
                  "fileName": "lodash.js",
                  "filePath": "/work/vendor/upstream/lodash.js",
                  "vulnerabilities": [
                    {"source": "NVD", "name": "CVE-2024-1111", "severity": "HIGH", "description": "vendored copy"}
                  ]
                },
                {
                  "fileName": "package-lock.json",
                  "filePath": "/work/package-lock.json",
                  "vulnerabilities": [
                    {"source": "NVD", "name": "CVE-2024-2222", "severity": "HIGH", "description": "manifest match"}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, json, ""));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        IAuditor auditor = new DependencyCheckAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("CVE-2024-2222", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        var auditor = new DependencyCheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the HIGH and CRITICAL matches survive the threshold.
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
            return Task.FromResult(new SandboxExecResult(15, "scan complete\n", ""));
        });

        var auditor = new DependencyCheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "CVE-2021-23337,CVE-2017-5638",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(3, result.Findings.Count);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_NoUpdate_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "scan complete\n", ""));
        });

        var auditor = new DependencyCheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:NoUpdate"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("--noupdate", scanExec!.Argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SuppressionPath_InsideWorktree_FailsClosedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
                return Task.FromResult(EmulateRealpath(exec));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "scan complete\n", ""));
        });

        var auditor = new DependencyCheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SuppressionPaths"] = "suppression.xml",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("SuppressionPaths", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task SuppressionPath_OutsideWorktree_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsSuppressionProbe(exec))
                return Task.FromResult(EmulateRealpath(exec));
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "scan complete\n", ""));
        });

        var auditor = new DependencyCheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:SuppressionPaths"] = "/baseline/suppressions.xml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var suppressionIndex = argv.IndexOf("--suppression");
        Assert.True(suppressionIndex >= 0 && argv[suppressionIndex + 1] == "/baseline/suppressions.xml");
    }

    [Theory]
    [InlineData("--out")]
    [InlineData("--format")]
    [InlineData("--scan")]
    [InlineData("--failOnCVSS")]
    [InlineData("--updateonly")]
    [InlineData("--suppression")]
    [InlineData("--propertyfile")]
    [InlineData("--hints")]
    public async Task ReservedExtraArguments_AreRejectedDeterministically(string flag)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "scan complete\n", ""));
        });

        var auditor = new DependencyCheckAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = flag + ",some-value",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(flag, ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == DependencyCheckAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("dependency-check", flattened, StringComparison.Ordinal);
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
                Enabled = [DependencyCheckAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == DependencyCheckAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var dependencyCheck = Assert.Single(tools, t => t.Binary == "dependency-check");
        // Verify-only by design: no distro package carries a pinned
        // dependency-check — the operator provisions the upstream release
        // archive (and pre-seeds the NVD data directory) out of band.
        Assert.Null(dependencyCheck.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("dependency-check", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_dependency_check", "true")]
    public async Task RealDependencyCheck_CleanFixture_Passes()
    {
        if (InstalledDependencyCheckVersion is null || !DependencyCheckEndToEndAvailable.Value)
            return;

        var fixtureDir = SeedCleanFixtureRepo();

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

            var auditor = new DependencyCheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledDependencyCheckVersion,
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
    [Trait("requires_dependency_check", "true")]
    public async Task RealDependencyCheck_VulnerableFixture_ProducesFindingWithRuleIdAndLocation()
    {
        if (InstalledDependencyCheckVersion is null || !DependencyCheckEndToEndAvailable.Value)
            return;

        var fixtureDir = SeedVulnerableFixtureRepo();

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

            var auditor = new DependencyCheckAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledDependencyCheckVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            // The lodash advisory must surface with its CVE rule id and the
            // manifest location, at Error severity for a HIGH match.
            var finding = Assert.Single(
                result.Findings,
                f => f.Title.Contains("CVE-2021-23337", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Contains("package-lock.json", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.DependencyCheckAuditorPlugin.dll");
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
            PluginId: DependencyCheckAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Dependency-Check Dependency Vulnerabilities",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "dependency-check version " + DependencyCheckAuditor.DefaultExpectedVersion + "\n", "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("dependency-check", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "dependency-check" && exec.Argv[1] == "--version";

    private static bool IsReportPrepProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("mkdir -m 700 -p", StringComparison.Ordinal);

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsSuppressionProbe(SandboxExec exec)
        => exec.Argv.Count >= 2 && exec.Argv[0] == "realpath";

    private static bool IsReportRead(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "cat";

    // Emulates `realpath -m -- <path> .` for the suppression-path guard:
    // absolute paths resolve verbatim, relative ones against the /work cwd.
    private static SandboxExecResult EmulateRealpath(SandboxExec exec)
    {
        var configured = exec.Argv[3];
        var canonical = configured.StartsWith("/", StringComparison.Ordinal)
            ? configured
            : "/work/" + configured.TrimStart('.', '/');
        return new SandboxExecResult(0, canonical + "\n/work\n", "");
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

    private static bool ProbeDependencyCheckEndToEnd()
    {
        // The real-binary tests need a seeded vulnerability database
        // (network or a pre-seeded data directory), not just the binary: run
        // the exact scan the auditor performs against an empty directory and
        // require a clean verdict with a dependencies-bearing JSON report.
        try
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "codeybox-dependency-check-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var reportPath = Path.Combine(dir, "dependency-check-report.json");
                var psi = new ProcessStartInfo
                {
                    FileName = "dependency-check",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--scan");
                psi.ArgumentList.Add(dir);
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add("JSON");
                psi.ArgumentList.Add("--out");
                psi.ArgumentList.Add(reportPath);
                psi.ArgumentList.Add("--failOnCVSS");
                psi.ArgumentList.Add("0");
                using var process = Process.Start(psi)!;
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(milliseconds: 600_000))
                {
                    try { process.Kill(); } catch { /* best-effort probe teardown */ }
                    return false;
                }
                Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromSeconds(5));
                if (!File.Exists(reportPath))
                    return false;
                var report = File.ReadAllText(reportPath);
                return process.ExitCode == 0 && report.Contains("\"dependencies\"", StringComparison.Ordinal);
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

    private static string SeedCleanFixtureRepo()
    {
        // The clean fixture carries no packages at all: any analyzable
        // artifact could gain an advisory in a later NVD feed, but an empty
        // tree matches nothing by construction.
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-dependency-check-clean-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), "# clean fixture\n");
        return dir;
    }

    private static string SeedVulnerableFixtureRepo()
    {
        // A pinned npm lockfile entry: dependency-check's Node analyzers
        // match it against the vulnerability database with no build step.
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-dependency-check-vuln-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        const string lodashVersion = "4.17.15";
        File.WriteAllText(Path.Combine(dir, "package.json"), """
            {"name": "fixture", "version": "1.0.0", "dependencies": {"lodash": "LODASH"}}
            """.Replace("LODASH", lodashVersion));
        File.WriteAllText(Path.Combine(dir, "package-lock.json"), """
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
