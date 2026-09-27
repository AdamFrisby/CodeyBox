using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PyrightAuditorPlugin;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the pyright auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming pyright (never a pass or finding).
/// - Exit codes 0 and 1 are verdicts (findings-producing); 2 (fatal), 3 (config parse),
///   4 (illegal parameters) and others are infrastructure.
/// - A findings-producing exit without a JSON report fails closed as infrastructure.
/// - pyright JSON output maps to findings with rule ids, locations, and mapped severity;
///   absolute file paths are relativized against the scan root; range lines are 0-based.
/// - Raw tool severities ("error"/"warning"/"information") go through the declared mapping.
/// - Default exclusions (vendored, interpreter-environment, generated trees) and
///   scoped options (ExpectedVersion, ProjectPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_pyright", "true")].
/// </summary>
public sealed class PyrightAuditorTests
{
    private static readonly string? InstalledPyrightVersion = ProbeInstalledPyrightVersion();

    private const string JsonWithTypeIssues = """
        {
          "version": "1.1.414",
          "time": "1759094400000",
          "generalDiagnostics": [
            {
              "file": "/work/src/app.py",
              "severity": "error",
              "message": "Type \"str\" is not assignable to declared type \"int\"",
              "range": { "start": { "line": 0, "character": 8 }, "end": { "line": 0, "character": 15 } },
              "rule": "reportAssignmentType"
            },
            {
              "file": "/work/src/app.py",
              "severity": "warning",
              "message": "\"helper\" is not accessed",
              "range": { "start": { "line": 4, "character": 4 }, "end": { "line": 4, "character": 10 } },
              "rule": "reportUnusedVariable"
            },
            {
              "file": "/work/src/util.py",
              "severity": "information",
              "message": "Type of \"cache\" is \"dict[str, int]\"",
              "range": { "start": { "line": 9, "character": 0 }, "end": { "line": 9, "character": 5 } },
              "rule": "reportGeneralTypeIssues"
            }
          ],
          "summary": { "filesAnalyzed": 7, "errorCount": 1, "warningCount": 1, "informationCount": 1, "timeInSec": 0.42 }
        }
        """;

    private const string JsonClean = """
        {
          "version": "1.1.414",
          "time": "1759094400000",
          "generalDiagnostics": [],
          "summary": { "filesAnalyzed": 7, "errorCount": 0, "warningCount": 0, "informationCount": 0, "timeInSec": 0.31 }
        }
        """;

    private const string JsonWithFilteredPathsAndNoRange = """
        {
          "version": "1.1.414",
          "time": "1759094400000",
          "generalDiagnostics": [
            {
              "file": "/work/src/broken.py",
              "severity": "error",
              "message": "Expected expression"
            },
            {
              "file": "/work/vendor/lib.py",
              "severity": "error",
              "message": "\"x\" is not defined",
              "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 1 } },
              "rule": "reportUndefinedVariable"
            },
            {
              "file": "/work/.venv/lib/python3.12/site-packages/pkg/mod.py",
              "severity": "error",
              "message": "\"y\" is not defined",
              "range": { "start": { "line": 1, "character": 0 }, "end": { "line": 1, "character": 1 } },
              "rule": "reportUndefinedVariable"
            },
            {
              "file": "/work/dist/generated.py",
              "severity": "error",
              "message": "\"z\" is not defined",
              "range": { "start": { "line": 2, "character": 0 }, "end": { "line": 2, "character": 1 } },
              "rule": "reportUndefinedVariable"
            },
            {
              "file": "/opt/typeshed/stdlib/builtins.pyi",
              "severity": "error",
              "message": "Stub outside the audited tree",
              "range": { "start": { "line": 6, "character": 0 }, "end": { "line": 6, "character": 4 } },
              "rule": "reportGeneralTypeIssues"
            }
          ],
          "summary": { "filesAnalyzed": 12, "errorCount": 5, "warningCount": 0, "informationCount": 0, "timeInSec": 0.9 }
        }
        """;

    private const string JsonWithSeverities = """
        {
          "version": "1.1.414",
          "time": "1759094400000",
          "generalDiagnostics": [
            { "file": "/work/src/a.py", "severity": "error", "message": "Error-level diagnostic.", "rule": "rule-error", "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 1 } } },
            { "file": "/work/src/a.py", "severity": "warning", "message": "Warn-level diagnostic.", "rule": "rule-warn", "range": { "start": { "line": 1, "character": 0 }, "end": { "line": 1, "character": 1 } } },
            { "file": "/work/src/a.py", "severity": "information", "message": "Info-level diagnostic.", "rule": "rule-info", "range": { "start": { "line": 2, "character": 0 }, "end": { "line": 2, "character": 1 } } },
            { "file": "/work/src/a.py", "severity": "unspecified-future-level", "message": "Unrecognised level.", "rule": "rule-unknown", "range": { "start": { "line": 3, "character": 0 }, "end": { "line": 3, "character": 1 } } },
            { "file": "/work/src/a.py", "message": "No severity field.", "rule": "rule-noseverity", "range": { "start": { "line": 4, "character": 0 }, "end": { "line": 4, "character": 1 } } }
          ],
          "summary": { "filesAnalyzed": 1, "errorCount": 1, "warningCount": 1, "informationCount": 3, "timeInSec": 0.2 }
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingPyright_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "pyright: command not found"));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pyright", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingPyright()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pyright", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "pyright 1.1.99\n", ""));
            if (IsScanRootProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "/work\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pyright", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.1.99", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PyrightAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PyrightAuditor();
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
    public async Task Fixture_WithTypeIssues_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, JsonWithTypeIssues, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Findings.Count);

        var errorFinding = Assert.Single(result.Findings, f => f.Title.Contains("reportAssignmentType", StringComparison.Ordinal));
        Assert.Equal("codeybox:pyright", errorFinding.AuditorName);
        Assert.Equal(AuditSeverity.Error, errorFinding.Severity);
        // pyright reports absolute file paths and 0-based range lines; the
        // auditor relativizes to the repo root and converts to 1-based.
        Assert.Equal("src/app.py:1", errorFinding.Location);

        var warnFinding = Assert.Single(result.Findings, f => f.Title.Contains("reportUnusedVariable", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warnFinding.Severity);
        Assert.Equal("src/app.py:5", warnFinding.Location);

        var infoFinding = Assert.Single(result.Findings, f => f.Title.Contains("reportGeneralTypeIssues", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, infoFinding.Severity);
        Assert.Equal("src/util.py:10", infoFinding.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("pyright", scanExec!.Argv[0]);
        Assert.Contains("--outputjson", scanExec.Argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NonZeroFindingsExit_Code1_WithJson_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithTypeIssues, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Theory]
    [InlineData(2)] // fatal error, no diagnostics reported
    [InlineData(3)] // configuration file could not be read or parsed
    [InlineData(4)] // illegal command-line parameters
    public async Task CouldNotRunExitCodes_AreInfrastructureFailures(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // pyright could not run at all — text on stderr, not a JSON report.
            return Task.FromResult(new SandboxExecResult(exitCode, "", "pyright failed to run"));
        });

        IAuditor auditor = new PyrightAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pyright", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"exit {exitCode}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            // Exit 1 with text rather than a JSON report fails closed as infrastructure.
            return Task.FromResult(new SandboxExecResult(1, "", "No project root found."));
        });

        IAuditor auditor = new PyrightAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pyright", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "pyright: command not found"));
        });

        IAuditor auditor = new PyrightAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("pyright", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsLevelsCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithSeverities, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(5, findings.Count);

        var error = Assert.Single(findings, f => f.Title.Contains("rule-error", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, error.Severity);

        var warn = Assert.Single(findings, f => f.Title.Contains("rule-warn", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, warn.Severity);

        var info = Assert.Single(findings, f => f.Title.Contains("rule-info", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, info.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-noseverity", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent level -> default
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
            s => s.PluginId == PyrightAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("pyright", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresPyrightRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [PyrightAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == PyrightAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("pyright", tool.Binary);
        // Verify-only by design: pyright ships via npm (or the pip wrapper over
        // the same release); no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("pyright", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "pyright 1.1.99\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PyrightAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.1.99",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ProjectPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new PyrightAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ProjectPath"] = "/opt/codeybox/pyrightconfig.json",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var projectIndex = argv.ToList().IndexOf("--project");
        Assert.True(projectIndex >= 0 && projectIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/pyrightconfig.json", argv[projectIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredAndGeneratedFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithFilteredPathsAndNoRange, ""));
        });

        IAuditor auditor = new PyrightAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, .venv/, dist/ findings are dropped by the default
        // ExcludePaths; the src/ error and the diagnostic outside the scan
        // root (kept verbatim, normalized) survive.
        Assert.Equal(2, result.Findings.Count);

        // No range in the report -> file-only location (0-based lines need no
        // conversion when the tool supplies none).
        var srcFinding = Assert.Single(result.Findings, f => f.Location == "src/broken.py");
        Assert.Equal(AuditSeverity.Error, srcFinding.Severity);

        var outsideFinding = Assert.Single(result.Findings, f => f.Title.Contains("Stub outside", StringComparison.Ordinal));
        Assert.Equal("opt/typeshed/stdlib/builtins.pyi:7", outsideFinding.Location);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec) || IsScanRootProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonWithTypeIssues, ""));
        });

        var auditor = new PyrightAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "reportUnusedVariable",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("reportUnusedVariable", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_pyright", "true")]
    public async Task RealPyright_TypeErrorFixture_YieldsFindings()
    {
        var installed = InstalledPyrightVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPyrightFixtureRepoAsync(clean: false);

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

            var auditor = new PyrightAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.NotEmpty(result.Findings);

            var finding = Assert.Single(result.Findings, f => f.Location == "bad.py:1");
            Assert.Equal(AuditSeverity.Error, finding.Severity);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_pyright", "true")]
    public async Task RealPyright_CleanFixture_Passes()
    {
        var installed = InstalledPyrightVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedPyrightFixtureRepoAsync(clean: true);

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

            var auditor = new PyrightAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.True(result.Passed);
            Assert.DoesNotContain(result.Findings, f => f.Severity == AuditSeverity.Error);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.PyrightAuditorPlugin.dll");
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
            PluginId: PyrightAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Pyright Python Type Analysis",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(0, "pyright " + PyrightAuditor.DefaultExpectedVersion + "\n", "");
        if (IsScanRootProbe(exec))
            return new SandboxExecResult(0, "/work\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsScanRootProbe(SandboxExec exec)
        => exec.Argv.Count == 4
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2] == "pwd";

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("pyright", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "pyright" && exec.Argv[1] == "--version";

    private static async Task<string> SeedPyrightFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-pyright-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        if (clean)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "clean.py"), "answer: int = 42\n");
        }
        else
        {
            // A real assignment-type error at line 1 under every type-checking mode.
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.py"), "answer: int = \"hello\"\n");
        }

        return dir;
    }

    private static string? ProbeInstalledPyrightVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pyright",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");
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
