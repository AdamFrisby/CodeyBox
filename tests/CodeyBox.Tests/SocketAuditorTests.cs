using CodeyBox.SocketAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the socket auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming socket (never a pass or finding).
/// - Socket's exit convention (verified against 1.4.1 with --report): 0 is
///   "ran, report healthy", 1 is both "ran, report unhealthy" and operational
///   failure — the JSON envelope on stdout disambiguates ({ok:true} is the
///   verdict, {ok:false} is infrastructure). Anything else (2 for incorrect
///   usage and failed input validation such as a missing token or org) is
///   "could not run" infrastructure.
/// - Report alerts map to findings with the Socket alert type as rule id and
///   file/line locations; policy actions go through the declared mapping,
///   never raw. An unhealthy report with no alerts fails closed (no silent pass).
/// - A repository socket.json at any depth is a repo-controlled
///   suppression/steering surface and fails closed.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - No live-binary tests: a real scan needs a Socket API token, an
///   organisation, network, and quota, none of which the test environment
///   provides. The FakeSandbox tests above cover the full contract
///   (invocation, exit classification, parsing, mapping, gating).
/// </summary>
public sealed class SocketAuditorTests
{
    private const string ReportWithFindings =
        """
        {
          "ok": true,
          "data": {
            "healthy": false,
            "orgSlug": "acme",
            "scanId": "scan-1",
            "options": {"fold": "none", "reportLevel": "monitor"},
            "alerts": {
              "npm": {
                "evil-pkg": {
                  "9.9.9": {
                    "package-lock.json": {
                      "12:3": {"type": "malware", "policy": "error", "url": "https://socket.dev/npm/package/evil-pkg", "manifest": ["package-lock.json"]},
                      "14:1": {"type": "telemetry", "policy": "warn", "url": "https://socket.dev/npm/package/evil-pkg", "manifest": ["package-lock.json"]}
                    }
                  }
                },
                "old-pkg": {
                  "1.0.0": {
                    "package.json": {
                      "5:10": {"type": "deprecated", "policy": "monitor", "url": "https://socket.dev/npm/package/old-pkg", "manifest": ["package.json"]}
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ReportClean =
        """
        {
          "ok": true,
          "data": {
            "healthy": true,
            "orgSlug": "acme",
            "scanId": "scan-2",
            "options": {"fold": "none", "reportLevel": "monitor"},
            "alerts": {}
          }
        }
        """;

    private const string ReportWarnOnly =
        """
        {
          "ok": true,
          "data": {
            "healthy": true,
            "orgSlug": "acme",
            "scanId": "scan-3",
            "options": {"fold": "none", "reportLevel": "monitor"},
            "alerts": {
              "npm": {
                "chatty-pkg": {
                  "2.0.0": {
                    "package-lock.json": {
                      "7:1": {"type": "telemetry", "policy": "warn", "url": "https://socket.dev/npm/package/chatty-pkg", "manifest": ["package-lock.json"]}
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ErrorEnvelope =
        """
        {
          "ok": false,
          "message": "Input error",
          "data": "Please review the input requirements and try again\n\n  ✖ This command requires a Socket API token for access (try `socket login`)"
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingSocket_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "socket: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("socket", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.0.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("socket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(SocketAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            // Unhealthy report: exit 1 carries the findings verdict.
            return Task.FromResult(new SandboxExecResult(1, ReportWithFindings, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var malware = Assert.Single(
            result.Findings, f => f.Title.Contains("malware", StringComparison.Ordinal));
        Assert.Equal("codeybox:socket", malware.AuditorName);
        Assert.Equal(AuditSeverity.Error, malware.Severity);
        Assert.Contains("package-lock.json", malware.Location, StringComparison.Ordinal);
        Assert.Contains("package-lock.json:12", malware.Location, StringComparison.Ordinal);
        Assert.Contains("evil-pkg", malware.Description, StringComparison.Ordinal);

        var telemetry = Assert.Single(
            result.Findings, f => f.Title.Contains("telemetry", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, telemetry.Severity);

        var deprecated = Assert.Single(
            result.Findings, f => f.Title.Contains("deprecated", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, deprecated.Severity);
        Assert.Contains("package.json:5", deprecated.Location, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("socket", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        Assert.Equal(new[] { "scan", "create" }, argv.Skip(1).Take(2).ToArray());
        Assert.Contains("--json", argv, StringComparer.Ordinal);
        Assert.Contains("--report", argv, StringComparer.Ordinal);
        var reportLevelIndex = argv.IndexOf("--report-level");
        Assert.True(reportLevelIndex >= 0 && argv[reportLevelIndex + 1] == "monitor");
        Assert.Contains("--tmp", argv, StringComparer.Ordinal);
        Assert.Contains("--no-interactive", argv, StringComparer.Ordinal);
        Assert.Contains("--no-banner", argv, StringComparer.Ordinal);
        Assert.Contains("--no-spinner", argv, StringComparer.Ordinal);
        Assert.DoesNotContain("--org", argv, StringComparer.Ordinal);
        Assert.Equal(".", argv[^1]);
    }

    [Fact]
    public async Task Exit1_ErrorEnvelope_IsInfrastructureFailure_NotFindings()
    {
        // Exit 1 is ambiguous: an {ok:false} envelope means the scan could
        // not run (here: no API token), so it fails closed as
        // infrastructure rather than reporting findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ErrorEnvelope, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("socket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit2_InputValidation_IsInfrastructureFailure()
    {
        // Missing org and token fail input validation with exit 2 and an
        // error envelope — "could not run", never findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, ErrorEnvelope, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("socket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit3_UnknownConvention_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, ReportWithFindings, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("socket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit0_WithReportAlerts_StillReportsFindings()
    {
        // The JSON report is the verdict, not the exit code: a healthy scan
        // (exit 0) carrying a warn-level alert must still surface it — as an
        // advisory finding that does not fail the audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, ReportWarnOnly, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("telemetry", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task UnhealthyReport_WithoutAlerts_FailsClosed_NeverPassesSilently()
    {
        const string unhealthyNoAlerts =
            """
            {"ok": true, "data": {"healthy": false, "orgSlug": "acme", "scanId": "scan-9", "alerts": {}}}
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, unhealthyNoAlerts, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("socket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsPolicyActions_NoRawStringsPassedThrough()
    {
        const string report =
            """
            {
              "ok": true,
              "data": {
                "healthy": false,
                "alerts": {
                  "npm": {
                    "pkg": {
                      "1.0.0": {
                        "package.json": {
                          "1:1": {"type": "a-error", "policy": "error", "url": "", "manifest": ["package.json"]},
                          "2:1": {"type": "a-warn", "policy": "warn", "url": "", "manifest": ["package.json"]},
                          "3:1": {"type": "a-monitor", "policy": "monitor", "url": "", "manifest": ["package.json"]},
                          "4:1": {"type": "a-ignore", "policy": "ignore", "url": "", "manifest": ["package.json"]},
                          "5:1": {"type": "a-defer", "policy": "defer", "url": "", "manifest": ["package.json"]},
                          "6:1": {"type": "a-future", "policy": "brand-new-future-policy", "url": "", "manifest": ["package.json"]}
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, report, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(6, result.Findings.Count);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("a-error")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("a-warn")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("a-monitor")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("a-ignore")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("a-defer")).Severity);
        // Unknown policy action falls back to the declared default, never raw.
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("a-future")).Severity);
        // The raw tool token is preserved in the description (tool policy
        // line), proving the value flowed through the mapping.
        var error = Assert.Single(result.Findings, f => f.Title.Contains("a-error"));
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultExcludePaths_DropsVendoredFindings()
    {
        const string report =
            """
            {
              "ok": true,
              "data": {
                "healthy": false,
                "alerts": {
                  "npm": {
                    "lodash": {
                      "4.17.15": {
                        "vendor/upstream/package-lock.json": {
                          "3:1": {"type": "telemetry", "policy": "warn", "url": "", "manifest": ["vendor/upstream/package-lock.json"]}
                        },
                        "package-lock.json": {
                          "3:1": {"type": "telemetry", "policy": "warn", "url": "", "manifest": ["package-lock.json"]}
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, report, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("package-lock.json:3", finding.Location, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor", finding.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FoldedReport_FallsBackToManifestFile()
    {
        // With --fold=version the report nests the leaf directly under the
        // version: no file keys exist, so the finding location comes from
        // the alert's manifest list rather than inventing one.
        const string report =
            """
            {
              "ok": true,
              "data": {
                "healthy": false,
                "options": {"fold": "version", "reportLevel": "monitor"},
                "alerts": {
                  "npm": {
                    "evil-pkg": {
                      "9.9.9": {"type": "malware", "policy": "error", "url": "", "manifest": ["package-lock.json"]}
                    }
                  }
                }
              }
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, report, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("malware", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("package-lock.json", finding.Location);
    }

    [Fact]
    public async Task RepoConfigFile_FailsClosed_AsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsRepoFileProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "./packages/api/socket.json\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        IAuditor auditor = new SocketAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("socket.json", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "./socket.json\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new SocketAuditor();
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
    public async Task ScopedConfiguration_Org_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new SocketAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Org"] = "acme",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var orgIndex = argv.IndexOf("--org");
        Assert.True(orgIndex >= 0 && argv[orgIndex + 1] == "acme");
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithFindings, ""));
        });

        var auditor = new SocketAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the policy-error match survives the threshold.
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, ReportWithFindings, ""));
        });

        var auditor = new SocketAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "malware",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("--short")]
    [InlineData("--report-level")]
    [InlineData("--dry-run")]
    [InlineData("--read-only")]
    [InlineData("--json")]
    [InlineData("--report")]
    [InlineData("--org")]
    [InlineData("--cwd")]
    [InlineData("--interactive")]
    [InlineData("--no-tmp")]
    [InlineData("--set-as-alerts-page")]
    public async Task ReservedExtraArguments_RejectedDeterministically_BeforeScan(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, ReportClean, ""));
        });

        var auditor = new SocketAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = flag,
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(flag, ex.Message, StringComparison.Ordinal);
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
            s => s.PluginId == SocketAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("socket", flattened, StringComparison.Ordinal);
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
                Enabled = [SocketAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == SocketAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var socket = Assert.Single(tools, t => t.Binary == "socket");
        // Verify-only by design: no distro package carries a pinned socket —
        // the operator provisions the versioned npm release.
        Assert.Null(socket.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("socket", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.SocketAuditorPlugin.dll");
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
            PluginId: SocketAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Socket Dependency Supply-Chain Risks",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "1.4.1\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("socket", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "socket" && exec.Argv[1] == "--version";

    private static bool IsRepoFileProbe(SandboxExec exec)
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
