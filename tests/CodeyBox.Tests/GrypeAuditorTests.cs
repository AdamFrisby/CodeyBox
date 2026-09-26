using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.GrypeAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the grype auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming grype (never a pass or finding).
/// - Grype's exit convention (verified against 0.119.0 with --fail-on negligible):
///   0 is "ran", 2 is "ran and matched at/above negligible", anything else
///   (1 for bad flags, unscannable targets, bad config, database failures)
///   is "could not run" infrastructure. The SARIF report — not the exit code —
///   is the verdict, so exit 0 with results still reports findings.
/// - SARIF results map to findings with grype's rule ids and file locations;
///   grype's SARIF levels go through the declared mapping, never raw.
/// - A repository .grype.yaml is a repo-controlled suppression surface and fails closed.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_grype", "true")] need
///   grype on PATH plus a usable vulnerability database (network).
/// </summary>
public sealed class GrypeAuditorTests
{
    private static readonly string? InstalledGrypeVersion = ProbeInstalledVersion("grype", "--version");
    private static readonly Lazy<bool> GrypeEndToEndAvailable = new(ProbeGrypeEndToEnd);

    private const string SarifWithFindings =
        """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": {
                "driver": {
                  "name": "grype",
                  "version": "0.119.0",
                  "informationUri": "https://github.com/anchore/grype"
                }
              },
              "results": [
                {
                  "ruleId": "GHSA-35jh-r3h4-6jhm-lodash",
                  "level": "error",
                  "message": {"text": "A high vulnerability in npm package: lodash, version 4.17.15 was found at: /package-lock.json"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "/package-lock.json"}, "region": {"startLine": 1}}}]
                },
                {
                  "ruleId": "GHSA-29mw-wpgm-hmr9-lodash",
                  "level": "warning",
                  "message": {"text": "A medium vulnerability in npm package: lodash, version 4.17.15 was found at: /package-lock.json"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "/package-lock.json"}, "region": {"startLine": 1}}}]
                },
                {
                  "ruleId": "CVE-2023-44487-nghttp2",
                  "level": "error",
                  "message": {"text": "A high vulnerability in deb package: nghttp2, version 1.43.0 was found at: /var/lib/dpkg/status"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "/var/lib/dpkg/status"}, "region": {"startLine": 1}}}]
                },
                {
                  "ruleId": "CVE-2020-26137-urllib3",
                  "level": "note",
                  "message": {"text": "A low vulnerability in python package: urllib3, version 1.25.9 was found at: /requirements.txt"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "/requirements.txt"}, "region": {"startLine": 1}}}]
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
                  "name": "grype",
                  "version": "0.119.0",
                  "informationUri": "https://github.com/anchore/grype"
                }
              },
              "results": []
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingGrype_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "grype: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("grype", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "grype 0.110.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("grype", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.110.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GrypeAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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
            // --fail-on negligible tripped: a match at/above negligible.
            return Task.FromResult(new SandboxExecResult(2, SarifWithFindings, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);

        var dependency = Assert.Single(
            result.Findings, f => f.Title.Contains("GHSA-35jh-r3h4-6jhm-lodash", StringComparison.Ordinal));
        Assert.Equal("codeybox:grype", dependency.AuditorName);
        Assert.Equal(AuditSeverity.Error, dependency.Severity);
        Assert.Contains("package-lock.json", dependency.Location, StringComparison.Ordinal);

        var container = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2023-44487-nghttp2", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, container.Severity);
        Assert.Contains("nghttp2", container.Description, StringComparison.Ordinal);

        var medium = Assert.Single(
            result.Findings, f => f.Title.Contains("GHSA-29mw-wpgm-hmr9-lodash", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, medium.Severity);

        var low = Assert.Single(
            result.Findings, f => f.Title.Contains("CVE-2020-26137-urllib3", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, low.Severity);

        Assert.NotNull(scanExec);
        Assert.Equal("grype", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        Assert.Contains("dir:.", argv, StringComparer.Ordinal);
        var outputIndex = argv.IndexOf("-o");
        Assert.True(outputIndex >= 0 && argv[outputIndex + 1] == "sarif");
        var failOnIndex = argv.IndexOf("--fail-on");
        Assert.True(failOnIndex >= 0 && argv[failOnIndex + 1] == "negligible");
        Assert.Contains("-q", argv, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Exit0_WithSarifResults_StillReportsFindings()
    {
        // The SARIF report is the verdict, not the exit code: an
        // unknown-severity match may leave --fail-on untripped (exit 0) yet
        // must still surface from the parsed report.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, SarifWithFindings, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Findings.Count);
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

        IAuditor auditor = new GrypeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Exit1_FailedRun_IsInfrastructureFailure()
    {
        // Grype exits 1 on every run failure (bad flag, unscannable target,
        // unparseable config, database load failure) — error text, no SARIF
        // verdict — so it fails closed as infrastructure, never as findings.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "1 error occurred:\n\t* failed to catalog: no such file"));
        });

        IAuditor auditor = new GrypeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("grype", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit3_UnknownConvention_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(3, SarifWithFindings, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("grype", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
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
                  "tool": {"driver": {"name": "grype", "version": "0.119.0"}},
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
            return Task.FromResult(new SandboxExecResult(2, sarif, ""));
        });

        IAuditor auditor = new GrypeAuditor();
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
                  "tool": {"driver": {"name": "grype", "version": "0.119.0"}},
                  "results": [
                    {"ruleId": "GHSA-aaaa-bbbb-cccc-lodash", "level": "error", "message": {"text": "vendored copy"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "vendor/upstream/lodash.js"}, "region": {"startLine": 1}}}]},
                    {"ruleId": "GHSA-dddd-eeee-ffff-lodash", "level": "error", "message": {"text": "manifest match"}, "locations": [{"physicalLocation": {"artifactLocation": {"uri": "package-lock.json"}, "region": {"startLine": 1}}}]}
                  ]
                }
              ]
            }
            """;

        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, sarif, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("GHSA-dddd-eeee-ffff-lodash", finding.Title, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, ".grype.yaml\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        IAuditor auditor = new GrypeAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains(".grype.yaml", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, ".grype.yaml\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GrypeAuditor();
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
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, SarifClean, ""));
        });

        var auditor = new GrypeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/baseline/grype.yaml",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        var configIndex = argv.IndexOf("--config");
        Assert.True(configIndex >= 0 && argv[configIndex + 1] == "/baseline/grype.yaml");
    }

    [Fact]
    public async Task ScopedConfiguration_MinimumSeverity_DropsAdvisories()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsRepoFileProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, SarifWithFindings, ""));
        });

        var auditor = new GrypeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:MinimumSeverity"] = "error",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Only the two error-level matches survive the threshold.
        Assert.Equal(2, result.Findings.Count);
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
            return Task.FromResult(new SandboxExecResult(2, SarifWithFindings, ""));
        });

        var auditor = new GrypeAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExcludedRules"] = "GHSA-35jh-r3h4-6jhm-lodash,CVE-2023-44487-nghttp2",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.True(result.Passed);
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
            s => s.PluginId == GrypeAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("grype", flattened, StringComparison.Ordinal);
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
                Enabled = [GrypeAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == GrypeAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var grype = Assert.Single(tools, t => t.Binary == "grype");
        // Verify-only by design: no distro package carries a pinned grype —
        // the operator provisions the upstream release binary.
        Assert.Null(grype.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        Assert.Single(contributions.VerificationCommands);
        var verification = string.Join("\n",
            contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("grype", verification, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_grype", "true")]
    public async Task RealGrype_VulnerableFixture_ProducesFindingWithRuleIdAndLocation()
    {
        if (InstalledGrypeVersion is null || !GrypeEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedGrypeFixtureRepoAsync(vulnerable: true);

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

            var auditor = new GrypeAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledGrypeVersion,
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
            Assert.Contains("-lodash", finding.Title, StringComparison.Ordinal);
            Assert.Contains("package-lock.json", finding.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_grype", "true")]
    public async Task RealGrype_CleanFixture_Passes()
    {
        if (InstalledGrypeVersion is null || !GrypeEndToEndAvailable.Value)
            return;

        var fixtureDir = await SeedGrypeFixtureRepoAsync(vulnerable: false);

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

            var auditor = new GrypeAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledGrypeVersion,
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.GrypeAuditorPlugin.dll");
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
            PluginId: GrypeAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Grype Dependency Vulnerabilities",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "grype " + GrypeAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("grype", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "grype" && exec.Argv[1] == "--version";

    private static bool IsRepoFileProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("-e", StringComparison.Ordinal)
            && !exec.Argv[2].Contains("command -v", StringComparison.Ordinal);

    private static async Task<string> SeedGrypeFixtureRepoAsync(bool vulnerable)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-grype-fixture-" + Guid.NewGuid().ToString("N")[..8]);
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

        // A pinned npm lockfile entry: grype matches it against the
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

    private static bool ProbeGrypeEndToEnd()
    {
        // The real-binary tests need a usable vulnerability database
        // (network), not just the binary: run the exact scan the auditor
        // performs against an empty directory and require a clean verdict.
        try
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "codeybox-grype-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "grype",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("dir:" + dir);
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("sarif");
                psi.ArgumentList.Add("-q");
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
