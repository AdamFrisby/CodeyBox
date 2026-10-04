using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.ScancodeAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the scancode auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming scancode (never a pass or finding).
/// - scancode's exit convention (verified in the 32.5.0 cli.py source:
///   <c>rc = 0 if success else 1</c>): scancode has NO non-zero "found
///   something" exit — 0 is "scan completed" with or without detections
///   (the JSON report is the verdict, so exit 0 with detections still
///   reports findings), and every other exit (1 for usage errors,
///   unreadable inputs, interrupted/crashed scans) is "could not run"
///   infrastructure. The generic "non-zero means findings" convention
///   explicitly does NOT hold here and is not encoded.
/// - Report detections map to findings with scancode's licence keys as rule
///   ids and file/line locations; categories go through the declared
///   mapping, never raw.
/// - A missing or unparseable report fails closed as infrastructure.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_scancode", "true")]
///   need scancode on PATH (fully local scan, no network).
/// </summary>
public sealed class ScancodeAuditorTests
{
    private static readonly string? InstalledScancodeVersion = ProbeInstalledVersion("scancode", "--version");

    private const string ReportWithFindings =
        """
        {
          "headers": [{"tool_name": "scancode-toolkit", "tool_version": "32.5.0"}],
          "files": [
            {
              "path": "src/server.py",
              "type": "file",
              "licenses": [
                {
                  "key": "gpl-3.0",
                  "score": 100.0,
                  "name": "GNU General Public License 3.0",
                  "short_name": "GPL 3.0",
                  "category": "Copyleft",
                  "start_line": 1,
                  "end_line": 12,
                  "matched_rule": {"identifier": "gpl-3.0_196.RULE"}
                }
              ],
              "license_expressions": ["gpl-3.0"],
              "copyrights": [
                {"copyright": "Copyright (c) 2024 Example Corp", "start_line": 1, "end_line": 1}
              ],
              "holders": [
                {"holder": "Example Corp", "start_line": 1, "end_line": 1}
              ],
              "scan_errors": []
            },
            {
              "path": "src/util.py",
              "type": "file",
              "licenses": [
                {
                  "key": "mit",
                  "score": 100.0,
                  "name": "MIT License",
                  "short_name": "MIT",
                  "category": "Permissive",
                  "start_line": 1,
                  "end_line": 2
                }
              ],
              "license_expressions": ["mit"],
              "copyrights": [
                {"copyright": "Copyright (c) 2024 Example Corp", "start_line": 1, "end_line": 1}
              ],
              "holders": [],
              "scan_errors": []
            },
            {
              "path": "vendor/lib.js",
              "type": "file",
              "licenses": [
                {
                  "key": "apache-2.0",
                  "score": 100.0,
                  "name": "Apache License 2.0",
                  "category": "Permissive",
                  "start_line": 1,
                  "end_line": 5
                }
              ],
              "license_expressions": ["apache-2.0"],
              "copyrights": [],
              "scan_errors": []
            },
            {
              "path": "bin/blob.bin",
              "type": "binary",
              "licenses": [],
              "copyrights": [],
              "scan_errors": ["ERROR: cannot scan binary file"]
            }
          ]
        }
        """;

    private const string ReportClean =
        """
        {
          "headers": [{"tool_name": "scancode-toolkit", "tool_version": "32.5.0"}],
          "files": [
            {"path": "src/main.py", "type": "file", "licenses": [], "copyrights": [], "scan_errors": []},
            {"path": "README.md", "type": "file"}
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingScancode_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "scancode: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scancode", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "ScanCode version: 31.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scancode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("31.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ScancodeAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithLicenceAndCopyrightIssue_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        SandboxExec? reportRead = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
            {
                reportRead = exec;
                return Task.FromResult(new SandboxExecResult(0, ReportWithFindings, ""));
            }
            scanExec = exec;
            // Scan completed with detections: scancode still exits 0 — the
            // report, not the exit, is the verdict.
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        // gpl-3.0 + 2 copyright notices + mit + apache-2.0 (vendor, excluded by default) + scan-error.
        var copyleft = Assert.Single(
            result.Findings, f => f.Title.Contains("gpl-3.0", StringComparison.Ordinal));
        Assert.Equal("codeybox:scancode", copyleft.AuditorName);
        Assert.Equal(AuditSeverity.Error, copyleft.Severity);
        Assert.Contains("src/server.py", copyleft.Location, StringComparison.Ordinal);
        Assert.Contains(":1", copyleft.Location, StringComparison.Ordinal);
        Assert.Contains("Copyleft", copyleft.Description, StringComparison.Ordinal);

        var copyright = result.Findings
            .Where(f => f.Title.Contains("copyright-notice", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, copyright.Count);
        Assert.All(copyright, f => Assert.Equal(AuditSeverity.Info, f.Severity));
        Assert.Contains(copyright, f => f.Location != null && f.Location.Contains("src/server.py", StringComparison.Ordinal));

        var permissive = Assert.Single(
            result.Findings, f => f.Title.Contains("mit", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, permissive.Severity);
        Assert.Contains("src/util.py", permissive.Location, StringComparison.Ordinal);

        var scanError = Assert.Single(
            result.Findings, f => f.Title.Contains("scan-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, scanError.Severity);
        Assert.Contains("bin/blob.bin", scanError.Location, StringComparison.Ordinal);

        // vendor/ is excluded by default: the apache-2.0 finding never surfaces.
        Assert.DoesNotContain(result.Findings,
            f => f.Location != null && f.Location.Contains("vendor/", StringComparison.Ordinal));

        // The scan argv pins the invocation shape: licence + copyright over
        // the worktree, repo-relative paths, quiet streams, no phone-home,
        // JSON report to the per-run scratch file.
        Assert.NotNull(scanExec);
        Assert.Equal("scancode", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        Assert.Contains("--license", argv, StringComparer.Ordinal);
        Assert.Contains("--copyright", argv, StringComparer.Ordinal);
        Assert.Contains("--strip-root", argv, StringComparer.Ordinal);
        Assert.Contains("--quiet", argv, StringComparer.Ordinal);
        Assert.Contains("--no-check-version", argv, StringComparer.Ordinal);
        Assert.Contains(".", argv, StringComparer.Ordinal);
        var jsonIndex = argv.IndexOf("--json");
        Assert.True(jsonIndex >= 0 && argv[jsonIndex + 1].EndsWith(
            ScancodeAuditor.ReportFileName, StringComparison.Ordinal));

        // The report is read back from the exact file the scan was told to
        // write — never from captured stdout.
        Assert.NotNull(reportRead);
        Assert.Equal("cat", reportRead!.Argv[0]);
        Assert.Equal("--", reportRead.Argv[1]);
        Assert.Equal(argv[jsonIndex + 1], reportRead.Argv[2]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit1_FailedRun_IsInfrastructureFailure_NeverFindings()
    {
        // scancode exits 1 when the scan could not run (usage error,
        // unreadable input, interrupted/crashed scan) — error text, no
        // verdict — so it fails closed as infrastructure. There is no
        // non-zero "found something" exit to declare.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "ERROR: Input path does not exist"));
        });

        IAuditor auditor = new ScancodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scancode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit2_UnknownConvention_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, ReportWithFindings, ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scancode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLicenceCategories_NoRawValuesPassedThrough()
    {
        const string report =
            """
            {
              "headers": [],
              "files": [
                {"path": "a.py", "licenses": [{"key": "gpl-2.0", "category": "Copyleft", "start_line": 1}], "copyrights": []},
                {"path": "b.py", "licenses": [{"key": "lgpl-2.1", "category": "Copyleft Limited", "start_line": 2}], "copyrights": []},
                {"path": "c.py", "licenses": [{"key": "mit", "category": "Permissive", "start_line": 1}], "copyrights": []},
                {"path": "d.py", "licenses": [{"key": "unlicense", "category": "Public Domain", "start_line": 1}], "copyrights": []},
                {"path": "e.py", "licenses": [{"key": "proprietary-license", "category": "Proprietary Free", "start_line": 1}], "copyrights": []},
                {"path": "f.py", "licenses": [{"key": "agpl-1.0", "category": "Something-New-Upstream", "start_line": 1}], "copyrights": []},
                {"path": "g.py", "licenses": [], "copyrights": [{"copyright": "Copyright 2024 Someone", "start_line": 3}]},
                {"path": "h.py", "licenses": [], "copyrights": [], "scan_errors": ["timeout"]}
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, report, ""));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(AuditSeverity.Error, SeverityOf(result, "gpl-2.0"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "lgpl-2.1"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "mit"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "unlicense"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "proprietary-license"));
        // An unrecognised category falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "agpl-1.0"));
        Assert.Equal(AuditSeverity.Info, SeverityOf(result, "copyright-notice"));
        Assert.Equal(AuditSeverity.Warning, SeverityOf(result, "scan-error"));

        // The tool's category tokens are recorded for transparency but the
        // finding severity is the mapped CodeyBox value, not the raw token.
        var copyleft = Assert.Single(result.Findings, f => f.Title.Contains("gpl-2.0", StringComparison.Ordinal));
        Assert.Contains("Copyleft", copyleft.Description, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, copyleft.Severity);

        static AuditSeverity SeverityOf(AuditResult r, string rule)
            => Assert.Single(r.Findings, f => f.Title.StartsWith(rule + ":", StringComparison.Ordinal)).Severity;
    }

    [Fact]
    public async Task MissingReportFile_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(1, "", "cat: No such file or directory"));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scancode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnparseableReport_IsInfrastructureFailure_NeverAPass()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, "this is not json", ""));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        IAuditor auditor = new ScancodeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("scancode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsInfos()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, ReportWithFindings, ""));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        var auditor = new ScancodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the copyleft match survives the threshold.
        var finding = Assert.Single(result.Findings);
        Assert.Contains("gpl-3.0", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, ReportWithFindings, ""));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        var auditor = new ScancodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "gpl-3.0,copyright-notice,scan-error,mit",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePathsOverride_ReincludesVendor()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsReportRead(exec))
                return Task.FromResult(new SandboxExecResult(0, ReportWithFindings, ""));
            return Task.FromResult(new SandboxExecResult(0, "Scanning done.", ""));
        });

        var auditor = new ScancodeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludePaths"] = "third_party/,node_modules/",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Contains(result.Findings,
            f => f.Location != null && f.Location.Contains("vendor/lib.js", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReservedExtraArguments_FailClosed_AsDeterministicInfrastructure()
    {
        foreach (var reserved in new[] { "--json", "--strip-root", "--from-json", "--max-depth", "--quiet", "--no-check-version", "--license-text" })
        {
            var sandbox = new FakeSandbox((exec, _) =>
            {
                if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsReportPrepProbe(exec))
                    return Task.FromResult(Ok(exec));
                if (IsReportRead(exec))
                    return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
                Assert.Fail($"Scan must not run with reserved flag '{reserved}'.");
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            });

            var auditor = new ScancodeAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExtraArguments"] = reserved,
                }),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
                () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

            Assert.True(ex.IsDeterministic);
            // The (--check-version, --no-check-version) pair is reported
            // under its canonical long flag.
            var expected = reserved == "--no-check-version" ? "--check-version" : reserved;
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }
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
            s => s.PluginId == ScancodeAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("scancode", flattened, StringComparison.Ordinal);
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
                Enabled = [ScancodeAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ScancodeAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var scancode = Assert.Single(tools, t => t.Binary == "scancode");
        // Verify-only by design: no distro package carries a pinned
        // scancode — the operator provisions scancode-toolkit via pip or
        // the upstream release archive.
        Assert.Null(scancode.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("scancode", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_scancode", "true")]
    public async Task RealScancode_LicenceAndCopyrightFixture_ProducesFindingWithRuleIdAndLocation()
    {
        if (InstalledScancodeVersion is null)
            return;

        var fixtureDir = await SeedScancodeFixtureRepoAsync(copyrightedLicensed: true);

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

            var auditor = new ScancodeAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledScancodeVersion,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var licenceFinding = result.Findings.FirstOrDefault(
                f => f.Title.StartsWith("gpl-", StringComparison.Ordinal));
            Assert.NotNull(licenceFinding);
            Assert.Equal(AuditSeverity.Error, licenceFinding!.Severity);
            Assert.Contains("notice.py", licenceFinding.Location, StringComparison.Ordinal);
            Assert.Contains(
                result.Findings,
                f => f.Title.Contains("copyright-notice", StringComparison.Ordinal)
                    && f.Location != null
                    && f.Location.Contains("notice.py", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_scancode", "true")]
    public async Task RealScancode_CleanFixture_Passes()
    {
        if (InstalledScancodeVersion is null)
            return;

        var fixtureDir = await SeedScancodeFixtureRepoAsync(copyrightedLicensed: false);

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

            var auditor = new ScancodeAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledScancodeVersion,
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ScancodeAuditorPlugin.dll");
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
            PluginId: ScancodeAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ScanCode Licence and Copyright",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "ScanCode version: " + ScancodeAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("scancode", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "scancode" && exec.Argv[1] == "--version";

    private static bool IsReportPrepProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("mkdir -m 700 -p", StringComparison.Ordinal);

    private static bool IsReportRead(SandboxExec exec)
        => exec.Argv.Count == 3 && exec.Argv[0] == "cat" && exec.Argv[1] == "--";

    private static async Task<string> SeedScancodeFixtureRepoAsync(bool copyrightedLicensed)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-scancode-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (!copyrightedLicensed)
        {
            // Plain arithmetic with no licence header and no copyright
            // notice: nothing for scancode to detect by construction.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "plain.py"), "def add(a, b):\n    return a + b\n");
            return dir;
        }

        // The canonical GPL-3.0 short notice plus a copyright line: scancode
        // matches the notice text against its licence rules with no build
        // or install step.
        await File.WriteAllTextAsync(Path.Combine(dir, "notice.py"), """
            # Copyright (c) 2024 Example Corp
            # This program is free software: you can redistribute it and/or modify
            # it under the terms of the GNU General Public License as published by
            # the Free Software Foundation, either version 3 of the License, or
            # (at your option) any later version.
            def add(a, b):
                return a + b
            """);
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
