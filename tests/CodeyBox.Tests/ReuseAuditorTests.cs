using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using CodeyBox.ReuseAuditorPlugin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the reuse auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming reuse (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); exit 2 and others are infrastructure.
///   Exit 0 means compliant; exit 1 means at least one criterion non-empty.
/// - A verdict-class exit without reuse JSON output fails closed as an infrastructure failure.
/// - Unknown non_compliant criterion keys fail closed as infrastructure (never a silent verdict shrink).
/// - Reuse JSON output maps non_compliant entries to findings with rule ids
///   (reuse/&lt;criterion&gt;), locations, and mapped severity; per-file
///   invalid SPDX expressions become findings on that file.
/// - Raw tool criteria go through the declared mapping.
/// - Default scope (lint --json, ExcludePaths finding backstop) and scoped options
///   (ExpectedVersion, IncludedRules, ExcludePaths, ExtraArguments).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_reuse", "true")].
/// </summary>
public sealed class ReuseAuditorTests
{
    private static readonly string? InstalledReuseVersion = ProbeInstalledReuseVersion();

    private const string JsonWithIssues = """
        {
          "lint_version": "test",
          "reuse_spec_version": "3.3",
          "reuse_tool_version": "6.2.0",
          "non_compliant": {
            "bad_licenses": ["Foo-1.0"],
            "deprecated_licenses": ["GPL-2.0"],
            "licenses_without_extension": ["MIT"],
            "missing_licenses": ["Apache-2.0"],
            "unused_licenses": ["CC0-1.0"],
            "read_errors": [],
            "missing_copyright_info": ["src/nocopy.py"],
            "missing_licensing_info": ["src/nocopy.py", "src/nolicense.py"]
          },
          "files": [
            {
              "path": "src/nocopy.py",
              "copyrights": [],
              "spdx_expressions": []
            },
            {
              "path": "src/badexpr.py",
              "copyrights": [{"value": "2026 Jane", "source": "src/badexpr.py", "source_type": null}],
              "spdx_expressions": [
                {"value": "MIT AND", "is_valid": false, "source": "src/badexpr.py", "source_type": null},
                {"value": "MIT", "is_valid": true, "source": "src/badexpr.py", "source_type": null}
              ]
            }
          ],
          "summary": {
            "used_licenses": ["MIT"],
            "files_total": 3,
            "files_with_copyright_info": 2,
            "files_with_licensing_info": 1,
            "compliant": false
          },
          "recommendations": []
        }
        """;

    private const string JsonClean = """
        {
          "non_compliant": {
            "bad_licenses": [],
            "deprecated_licenses": [],
            "licenses_without_extension": [],
            "missing_licenses": [],
            "unused_licenses": [],
            "read_errors": [],
            "missing_copyright_info": [],
            "missing_licensing_info": []
          },
          "files": [],
          "summary": {
            "used_licenses": ["MIT"],
            "files_total": 1,
            "files_with_copyright_info": 1,
            "files_with_licensing_info": 1,
            "compliant": true
          }
        }
        """;

    private const string JsonWithVendoredPaths = """
        {
          "non_compliant": {
            "bad_licenses": [],
            "deprecated_licenses": [],
            "licenses_without_extension": [],
            "missing_licenses": [],
            "unused_licenses": [],
            "read_errors": [],
            "missing_copyright_info": [],
            "missing_licensing_info": ["vendor/lib/legacy.py", "src/owned.py"]
          },
          "files": [],
          "summary": {"compliant": false}
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingReuse_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "reuse: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingReuse()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "reuse, version 0.99.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.99.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ReuseAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
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

        var auditor = new ReuseAuditor();
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
    public async Task Fixture_WithIssues_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(9, result.Findings.Count);

        var bad = Assert.Single(result.Findings, f => f.Title.Contains("Foo-1.0", StringComparison.Ordinal));
        Assert.Equal("codeybox:reuse", bad.AuditorName);
        Assert.Equal(AuditSeverity.Error, bad.Severity);
        Assert.Contains("reuse/bad-licenses", bad.Title, StringComparison.Ordinal);

        var missingLicense = Assert.Single(result.Findings, f => f.Title.Contains("Apache-2.0", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, missingLicense.Severity);
        Assert.Contains("reuse/missing-licenses", missingLicense.Title, StringComparison.Ordinal);

        var noCopyright = Assert.Single(
            result.Findings, f => f.Title.Contains("reuse/missing-copyright-info", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, noCopyright.Severity);
        Assert.Equal("src/nocopy.py", noCopyright.Location);

        var noLicense = result.Findings
            .Where(f => f.Title.Contains("reuse/missing-licensing-info", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, noLicense.Count);
        Assert.Contains(noLicense, f => f.Location == "src/nocopy.py");
        Assert.Contains(noLicense, f => f.Location == "src/nolicense.py");

        var invalid = Assert.Single(
            result.Findings, f => f.Title.Contains("reuse/invalid-spdx-expression", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, invalid.Severity);
        Assert.Equal("src/badexpr.py", invalid.Location);
        Assert.Contains("MIT AND", invalid.Title, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("reuse", scanExec!.Argv[0]);
        Assert.Contains("lint", scanExec.Argv);
        Assert.Contains("--json", scanExec.Argv);
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

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task AdvisoryOnlyFixture_Passes_WithFindings()
    {
        // Only an unused license text: exit 1 from the tool, but every
        // finding maps below Error, so the audit passes with advisory output.
        const string advisoryOnly = """
            {
              "non_compliant": {
                "bad_licenses": [],
                "deprecated_licenses": [],
                "licenses_without_extension": [],
                "missing_licenses": [],
                "unused_licenses": ["CC0-1.0"],
                "read_errors": [],
                "missing_copyright_info": [],
                "missing_licensing_info": []
              },
              "files": [],
              "summary": {"compliant": false}
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, advisoryOnly, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.Contains("reuse/unused-licenses", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_IsTheFoundSomethingExit_ReportsFindings_NotInfrastructure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode2_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Click exits 2 on usage errors: the tool could not run.
            return Task.FromResult(new SandboxExecResult(2, "", "Error: No such option: --bogus"));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerdictExit_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 with no JSON report fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(1, "", "internal error"));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCriterionKey_IsInfrastructureFailure_NotASilentVerdictShrink()
    {
        // A report-shape change (a criterion the parser does not know) must
        // fail closed: dropping it would silently shrink the verdict.
        const string unknownCriterion = """
            {
              "non_compliant": {
                "bad_licenses": [],
                "deprecated_licenses": [],
                "licenses_without_extension": [],
                "missing_licenses": [],
                "unused_licenses": [],
                "read_errors": [],
                "missing_copyright_info": [],
                "missing_licensing_info": [],
                "future_criterion": ["something"]
              },
              "files": [],
              "summary": {"compliant": false}
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, unknownCriterion, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "reuse: command not found"));
        });

        IAuditor auditor = new ReuseAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("reuse", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsCriteriaCorrectly_NoRawPassthrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // Compliance-breaking criteria -> Error; deprecation/hygiene ->
        // Warning/Info. Raw criterion tokens never reach the finding
        // severity — they stay visible only in the description's
        // "Severity (tool):" line.
        Assert.Equal(
            AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("reuse/bad-licenses", StringComparison.Ordinal)).Severity);
        Assert.Equal(
            AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("reuse/missing-copyright-info", StringComparison.Ordinal)).Severity);
        Assert.Equal(
            AuditSeverity.Error,
            Assert.Single(result.Findings, f => f.Title.Contains("reuse/invalid-spdx-expression", StringComparison.Ordinal)).Severity);
        var deprecated = Assert.Single(
            result.Findings, f => f.Title.Contains("reuse/deprecated-licenses", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, deprecated.Severity);
        Assert.Contains("Severity (tool): deprecated-licenses", deprecated.Description, StringComparison.Ordinal);
        var noExtension = Assert.Single(
            result.Findings, f => f.Title.Contains("reuse/licenses-without-extension", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, noExtension.Severity);
        var unused = Assert.Single(
            result.Findings, f => f.Title.Contains("reuse/unused-licenses", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, unused.Severity);
        Assert.Contains("Severity (tool): unused-licenses", unused.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsoluteReportPaths_RelativizeAgainstWorkingDirectory()
    {
        const string absolutePaths = """
            {
              "non_compliant": {
                "bad_licenses": [],
                "deprecated_licenses": [],
                "licenses_without_extension": [],
                "missing_licenses": [],
                "unused_licenses": [],
                "read_errors": [],
                "missing_copyright_info": [],
                "missing_licensing_info": ["/work/src/owned.py"]
              },
              "files": [],
              "summary": {"compliant": false}
            }
            """;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, absolutePaths, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/owned.py", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithVendoredPaths, ""));
        });

        IAuditor auditor = new ReuseAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("src/owned.py", finding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_SelectsCriteria()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithIssues, ""));
        });

        var auditor = new ReuseAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "reuse/bad-licenses",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("reuse/bad-licenses", finding.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--plain")]
    [InlineData("--lines")]
    public async Task ExtraArguments_OutputFlag_SuppressesBuiltinJson(string extraArg)
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new ReuseAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExtraArguments"] = extraArg,
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        // The operator's own output flag wins; the auditor must not emit a
        // second one that would conflict with or silently override it.
        Assert.DoesNotContain("--json", scanExec!.Argv);
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
            s => s.PluginId == ReuseAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);
    }

    [Fact]
    public void EnabledPlugin_DeclaresReuseRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ReuseAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ReuseAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("reuse", tool.Binary);
        // Verify-only by design: no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("reuse", string.Join(" ", verification.Argv), StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_reuse", "true")]
    public async Task RealReuse_NonCompliantFixture_YieldsFinding_WithRuleIdAndLocation()
    {
        var installed = InstalledReuseVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedReuseFixtureRepoAsync(clean: false);

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

            var auditor = new ReuseAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.Findings,
                f => f.Title.Contains("reuse/missing-licensing-info", StringComparison.Ordinal)
                    && f.Location == "bad.py");
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_reuse", "true")]
    public async Task RealReuse_CleanFixture_Passes()
    {
        var installed = InstalledReuseVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedReuseFixtureRepoAsync(clean: true);

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

            var auditor = new ReuseAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ReuseAuditorPlugin.dll");
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
            PluginId: ReuseAuditor.PluginId,
            PluginDisplayName: "CodeyBox: REUSE Licence Compliance",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "reuse, version " + ReuseAuditor.DefaultExpectedVersion + "\n", "")
            // Presence probes: exit 0 with empty stdout = probed files absent.
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("reuse", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "reuse" && exec.Argv[1] == "--version";

    private static async Task<string> SeedReuseFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-reuse-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            Directory.CreateDirectory(Path.Combine(dir, "LICENSES"));
            await File.WriteAllTextAsync(
                Path.Combine(dir, "LICENSES", "MIT.txt"),
                "MIT License\n\nPermission is hereby granted, free of charge.\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "ok.py"),
                "# SPDX-FileCopyrightText: 2026 Example Author <author@example.com>\n"
                + "#\n"
                + "# SPDX-License-Identifier: MIT\n"
                + "\nprint('hello')\n");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.py"), "print('hello')\n");
        }

        return dir;
    }

    private static string? ProbeInstalledReuseVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reuse",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
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
