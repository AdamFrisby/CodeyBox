using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.PipAuditAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the pip-audit auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming pip-audit (never a pass or finding).
/// - pip-audit's exit convention (verified against 2.10.1's documented codes
///   and _cli.py): 0 is "ran clean", 1 is EITHER "ran and matched" (JSON
///   manifest on stdout) OR a fatal run failure (stderr only — the parser
///   fails closed, so it can neither pass nor surface as findings); 2 is
///   usage errors. The JSON manifest — not the exit code — is the verdict.
/// - JSON results map to findings with pip-audit's advisory ids; severities
///   come from the declared mapping (the tool reports none), never raw.
/// - Skipped dependencies surface as advisory coverage-gap findings.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_pip-audit", "true")]
///   need pip-audit on PATH plus package-index and vulnerability-service
///   access (network).
/// </summary>
public sealed class PipAuditAuditorTests
{
    private static readonly string? InstalledPipAuditVersion = ProbeInstalledVersion("pip-audit", "--version");
    private static readonly Lazy<bool> PipAuditEndToEndAvailable = new(ProbePipAuditEndToEnd);

    private const string JsonWithFindings =
        """
        {
          "dependencies": [
            {
              "name": "flask",
              "version": "0.5",
              "vulns": [
                {
                  "id": "PYSEC-2019-179",
                  "fix_versions": ["1.0"],
                  "aliases": ["CVE-2019-1010083", "GHSA-5wv5-4vpf-pj6m"],
                  "description": "The Pallets Project Flask before 1.0 is vulnerable."
                },
                {
                  "id": "PYSEC-2018-66",
                  "fix_versions": ["0.12.3"],
                  "aliases": ["CVE-2018-1000656"],
                  "description": "Old Flask denial of service."
                }
              ]
            },
            {
              "name": "django",
              "version": "1.2",
              "vulns": []
            }
          ],
          "fixes": []
        }
        """;

    private const string JsonClean =
        """
        {
          "dependencies": [
            {
              "name": "django",
              "version": "4.2",
              "vulns": []
            }
          ],
          "fixes": []
        }
        """;

    private const string JsonWithSkip =
        """
        {
          "dependencies": [
            {
              "name": "unresolvable-pkg",
              "skip_reason": "client error querying PyPI"
            }
          ],
          "fixes": []
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingPipAudit_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "pip-audit: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "pip-audit 2.9.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2.9.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PipAuditAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndPackageIdentity()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            // Exit 1: ran and matched at least one vulnerability.
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, "Found 2 known vulnerabilities in 1 package\n"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var first = Assert.Single(
            result.Findings, f => f.Title.Contains("PYSEC-2019-179", StringComparison.Ordinal));
        Assert.Equal("codeybox:pip-audit", first.AuditorName);
        Assert.Equal(AuditSeverity.Error, first.Severity);
        // pip-audit supplies no file/line: the package identity is the location.
        Assert.Null(first.Location);
        Assert.Contains("flask@0.5", first.Description, StringComparison.Ordinal);
        Assert.Contains("CVE-2019-1010083", first.Description, StringComparison.Ordinal);
        Assert.Contains("1.0", first.Description, StringComparison.Ordinal);

        var second = Assert.Single(
            result.Findings, f => f.Title.Contains("PYSEC-2018-66", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, second.Severity);
        Assert.Null(second.Location);

        Assert.NotNull(scanExec);
        Assert.Equal(
            new[]
            {
                "pip-audit", "--format", "json", "--desc", "on", "--aliases", "on",
                "--progress-spinner", "off", "--vulnerability-service", "pypi", ".",
            },
            scanExec!.Argv.ToArray());
    }

    [Fact]
    public async Task Exit0_WithJsonVulns_StillReportsFindings()
    {
        // The JSON manifest is the verdict, not the exit code: a completed
        // scan must surface parsed results whatever the exit says.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, "No known vulnerabilities found\n"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit1_WithoutJsonManifest_IsInfrastructureFailure_NeverFindingsNorPass()
    {
        // pip-audit's fatal path exits 1 with only a stderr diagnostic (no
        // manifest on stdout): the parser discriminator fails closed as
        // infrastructure — the run neither passes nor reports findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(
                1, "", "ERROR: couldn't find a supported project file in .\n"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "usage error")]
    [InlineData(3, "unknown convention")]
    public async Task FailedRunExits_AreInfrastructureFailures(int exitCode, string _)
    {
        // Every non-{0,1} exit means "could not run": usage errors and
        // unknown codes fail closed as infrastructure — never as findings,
        // never as a pass.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", "scan failed"));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "not json at all", ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pip-audit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsUngradedVulnsToError_NoRawValuesPassedThrough()
    {
        // pip-audit reports no per-vulnerability severity: real findings
        // arrive with no tool level and take the declared default (error —
        // blocking, matching the tool's own any-match verdict). A severity
        // property, when present, flows through the declared mapping.
        const string json =
            """
            {
              "dependencies": [
                {
                  "name": "a",
                  "version": "1.0",
                  "vulns": [
                    {"id": "PYSEC-2024-0001-ungraded", "fix_versions": ["1.1"]},
                    {"id": "PYSEC-2024-0002-high", "fix_versions": [], "severity": "high"},
                    {"id": "PYSEC-2024-0003-medium", "fix_versions": [], "severity": "medium"},
                    {"id": "PYSEC-2024-0004-low", "fix_versions": [], "severity": "low"}
                  ]
                }
              ],
              "fixes": []
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, json, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(4, result.Findings.Count);
        var ungraded = Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-2024-0001-ungraded"));
        Assert.Equal(AuditSeverity.Error, ungraded.Severity);
        Assert.Contains("Severity (tool): (none)", ungraded.Description, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-2024-0002-high")).Severity);
        Assert.Equal(AuditSeverity.Warning,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-2024-0003-medium")).Severity);
        Assert.Equal(AuditSeverity.Info,
            Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-2024-0004-low")).Severity);
        var medium = Assert.Single(result.Findings, f => f.Title.Contains("PYSEC-2024-0003-medium"));
        Assert.Contains("Severity (tool): medium", medium.Description, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task SkippedDependency_YieldsAdvisoryFinding_NeverBlocking()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithSkip, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Contains("unresolvable-pkg", finding.Title, StringComparison.Ordinal);
        Assert.Contains("client error querying PyPI", finding.Description, StringComparison.Ordinal);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_RequirementsFiles_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:RequirementsFiles"] = "requirements.txt, requirements-dev.txt",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToArray();
        Assert.DoesNotContain(".", argv);
        var first = Array.IndexOf(argv, "-r");
        Assert.True(first >= 0 && argv[first + 1] == "requirements.txt");
        Assert.Contains("requirements-dev.txt", argv);
    }

    [Fact]
    public async Task ScopedConfiguration_RequirementsFileOutsideWorktree_IsRejectedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:RequirementsFiles"] = "/etc/passwd",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(PipAuditAuditor.RequirementsFilesKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_VulnerabilityService_ShapesTheArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:VulnerabilityService"] = "OSV",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToArray();
        var serviceIndex = Array.IndexOf(argv, "--vulnerability-service");
        // Case-insensitive operator input, canonical lower-case argv.
        Assert.True(serviceIndex >= 0 && argv[serviceIndex + 1] == "osv");
    }

    [Fact]
    public async Task ScopedConfiguration_UnknownVulnerabilityService_IsRejectedDeterministically()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:VulnerabilityService"] = "vendor-feed",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(PipAuditAuditor.VulnerabilityServiceKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludedRules_ReachTheTool_AndFilterFindings()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "PYSEC-2019-179",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToArray();
        var ignoreIndex = Array.IndexOf(argv, "--ignore-vuln");
        Assert.True(ignoreIndex >= 0 && argv[ignoreIndex + 1] == "PYSEC-2019-179");

        var finding = Assert.Single(result.Findings);
        Assert.Contains("PYSEC-2018-66", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "PYSEC-2018-66",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("PYSEC-2018-66", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFindings, ""));
        });

        var auditor = new PipAuditAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScanExec_RemovesReportRedirectEnvironment()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PipAuditAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        Assert.Contains("PIP_AUDIT_OUTPUT", scanExec!.EnvironmentVariablesToUnset);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("-f")]
    [InlineData("--output")]
    [InlineData("-o")]
    [InlineData("--fix")]
    [InlineData("--dry-run")]
    [InlineData("-d")]
    [InlineData("--requirement")]
    [InlineData("-r")]
    [InlineData("--local")]
    [InlineData("-l")]
    [InlineData("--vulnerability-service")]
    [InlineData("-s")]
    public async Task ReservedExtraArguments_AreRejectedDeterministically(string flag)
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PipAuditAuditor();
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
            s => s.PluginId == PipAuditAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("pip-audit", flattened, StringComparison.Ordinal);
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
                Enabled = [PipAuditAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PipAuditAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var pipAudit = Assert.Single(tools, t => t.Binary == "pip-audit");
        // Verify-only by design: no distro package carries a pinned
        // pip-audit — the operator provisions the pinned PyPI release.
        Assert.Null(pipAudit.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("pip-audit", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    public void Parser_EmptyStdout_Throws()
    {
        var parser = new PipAuditJsonParser();
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("pip-audit", "", "", 1)));
    }

    [Fact]
    public void Parser_InvalidJson_Throws()
    {
        var parser = new PipAuditJsonParser();
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("pip-audit", "{oops", "", 1)));
    }

    [Theory]
    [InlineData("""{"fixes": []}""")]
    [InlineData("""[]""")]
    [InlineData("Found 2 known vulnerabilities in 1 package")]
    public void Parser_WithoutDependenciesArray_Throws(string stdout)
    {
        // A bare array, a foreign object, or the human-readable summary is
        // not a pip-audit manifest — fail closed, never a partial verdict.
        var parser = new PipAuditJsonParser();
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("pip-audit", stdout, "", 1)));
    }

    [Fact]
    public void Parser_VulnerabilityWithoutId_Throws()
    {
        const string json =
            """{"dependencies": [{"name": "a", "version": "1", "vulns": [{"fix_versions": []}]}], "fixes": []}""";
        var parser = new PipAuditJsonParser();
        Assert.Throws<ExternalToolParseException>(() => parser.Parse(
            new ExternalToolParseInput("pip-audit", json, "", 1)));
    }

    [Fact]
    public void Parser_ToleratesMissingOptionalFields()
    {
        const string json =
            """{"dependencies": [{"name": "a", "vulns": [{"id": "PYSEC-1", "fix_versions": []}]}], "fixes": []}""";
        var parser = new PipAuditJsonParser();
        var finding = Assert.Single(parser.Parse(
            new ExternalToolParseInput("pip-audit", json, "", 1)));

        Assert.Equal("PYSEC-1", finding.RuleId);
        Assert.Null(finding.SeverityLevel);
        Assert.Null(finding.Path);
        Assert.Null(finding.Line);
        Assert.Contains("a@(unknown)", finding.Message, StringComparison.Ordinal);
        Assert.Contains("no fixed release reported", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_SkippedDependency_EmitsSkippedToken()
    {
        var parser = new PipAuditJsonParser();
        var finding = Assert.Single(parser.Parse(
            new ExternalToolParseInput("pip-audit", JsonWithSkip, "", 0)));

        Assert.Null(finding.RuleId);
        Assert.Equal(PipAuditJsonParser.SkippedDependencyToken, finding.SeverityLevel);
        Assert.Contains("unresolvable-pkg", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_pip-audit", "true")]
    public async Task RealPipAudit_VulnerableFixture_ProducesFindingWithRuleId()
    {
        if (InstalledPipAuditVersion is null || !PipAuditEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedPipAuditFixtureRepoAsync(vulnerable: true);

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

            var auditor = new PipAuditAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledPipAuditVersion,
                    ["Scoped:RequirementsFiles"] = "requirements.txt",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);
            Assert.All(result.Findings, f => Assert.False(
                string.IsNullOrWhiteSpace(f.Title), "Finding title must name the advisory."));
            Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_pip-audit", "true")]
    public async Task RealPipAudit_CleanFixture_Passes()
    {
        if (InstalledPipAuditVersion is null || !PipAuditEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedPipAuditFixtureRepoAsync(vulnerable: false);

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

            var auditor = new PipAuditAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledPipAuditVersion,
                    ["Scoped:RequirementsFiles"] = "requirements.txt",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PipAuditAuditorPlugin.dll");
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
            PluginId: PipAuditAuditor.PluginId,
            PluginDisplayName: "CodeyBox: pip-audit Python Dependency Vulnerabilities",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "pip-audit " + PipAuditAuditor.DefaultExpectedVersion + "\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("pip-audit", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "pip-audit" && exec.Argv[1] == "--version";

    private static async Task<string> SeedPipAuditFixtureRepoAsync(bool vulnerable)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-pipaudit-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // A requirements file audits without a build or install step. The
        // vulnerable pin is ancient on purpose: its advisories are stable
        // history, not a moving live-database target for a specific id
        // assertion (the test asserts presence, not identity).
        await File.WriteAllTextAsync(
            Path.Combine(dir, "requirements.txt"),
            vulnerable ? "flask==0.5\n" : "# no dependencies\n");
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

    private static bool ProbePipAuditEndToEnd()
    {
        // The real-binary tests need the package index and the vulnerability
        // service (network), not just the binary: run the exact scan the
        // auditor performs against a dependency-free requirements file and
        // require a clean manifest verdict.
        try
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "codeybox-pipaudit-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "requirements.txt"), "# no dependencies\n");
                var psi = new ProcessStartInfo
                {
                    FileName = "pip-audit",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add("json");
                psi.ArgumentList.Add("--desc");
                psi.ArgumentList.Add("on");
                psi.ArgumentList.Add("--aliases");
                psi.ArgumentList.Add("on");
                psi.ArgumentList.Add("--progress-spinner");
                psi.ArgumentList.Add("off");
                psi.ArgumentList.Add("--vulnerability-service");
                psi.ArgumentList.Add("pypi");
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add(Path.Combine(dir, "requirements.txt"));
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
                return process.ExitCode == 0 && stdout.Contains("\"dependencies\"", StringComparison.Ordinal);
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
