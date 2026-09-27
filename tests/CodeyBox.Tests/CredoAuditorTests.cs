using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.CredoAuditorPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Credo auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming credo (never a pass or finding).
/// - Exits 0–31 are verdicts (findings-producing bitmask); 128–130 and others are infrastructure.
/// - A findings exit without JSON output fails closed as an infrastructure failure.
/// - Credo JSON output maps to findings with rule ids, locations, and mapped severity.
/// - Raw tool categories go through the declared mapping (warning→Error, design/refactor→Warning,
///   consistency/readability→Info); advisory-only runs still pass.
/// - Default exclusions and scoped options (ExpectedVersion, ConfigPath).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_credo", "true")].
/// </summary>
public sealed class CredoAuditorTests
{
    private static readonly string? InstalledCredoVersion = ProbeInstalledCredoVersion();

    // Shape mirrors real `credo suggest --format json` (1.7.19):
    // a single {"issues": [...]} document with check/category/filename/line_no/message/priority/scope.
    private const string JsonWithFindings = """
        {
          "issues": [
            {
              "check": "Credo.Check.Readability.ModuleDoc",
              "category": "readability",
              "filename": "lib/app.ex",
              "line_no": 1,
              "column": 1,
              "column_end": 9,
              "trigger": "defmodule",
              "message": "Modules should have a @moduledoc tag.",
              "priority": 1,
              "scope": "MyApp"
            },
            {
              "check": "Credo.Check.Warning.IoInspect",
              "category": "warning",
              "filename": "lib/debug.ex",
              "line_no": 12,
              "column": 5,
              "column_end": 15,
              "trigger": "IO.inspect",
              "message": "There should be no `IO.inspect/1` calls.",
              "priority": 10,
              "scope": "MyApp.debug"
            }
          ]
        }
        """;

    private const string JsonClean = """
        {
          "issues": []
        }
        """;

    // A deps/ finding exercises the finding-level ExcludePaths mechanism.
    private const string JsonWithVendoredPaths = """
        {
          "issues": [
            {
              "check": "Credo.Check.Readability.ModuleDoc",
              "category": "readability",
              "filename": "lib/app.ex",
              "line_no": 3,
              "column": 1,
              "message": "Modules should have a @moduledoc tag.",
              "priority": 1,
              "scope": "MyApp"
            },
            {
              "check": "Credo.Check.Readability.ModuleDoc",
              "category": "readability",
              "filename": "deps/jason/lib/jason.ex",
              "line_no": 1,
              "column": 1,
              "message": "Modules should have a @moduledoc tag.",
              "priority": 1,
              "scope": "Jason"
            }
          ]
        }
        """;

    private const string JsonAdvisoryOnly = """
        {
          "issues": [
            {
              "check": "Credo.Check.Consistency.ExceptionNames",
              "category": "consistency",
              "filename": "lib/app.ex",
              "line_no": 7,
              "column": 3,
              "message": "Exception names should end with Error.",
              "priority": 1,
              "scope": "MyApp"
            }
          ]
        }
        """;

    private const string JsonWithCategories = """
        {
          "issues": [
            {
              "check": "rule-warning",
              "category": "warning",
              "filename": "a.ex",
              "line_no": 1,
              "message": "Warning-category issue."
            },
            {
              "check": "rule-design",
              "category": "design",
              "filename": "a.ex",
              "line_no": 2,
              "message": "Design-category issue."
            },
            {
              "check": "rule-refactor",
              "category": "refactor",
              "filename": "a.ex",
              "line_no": 3,
              "message": "Refactor-category issue."
            },
            {
              "check": "rule-consistency",
              "category": "consistency",
              "filename": "a.ex",
              "line_no": 4,
              "message": "Consistency-category issue."
            },
            {
              "check": "rule-readability",
              "category": "readability",
              "filename": "a.ex",
              "line_no": 5,
              "message": "Readability-category issue."
            },
            {
              "check": "rule-unknown",
              "category": "blocker",
              "filename": "a.ex",
              "line_no": 6,
              "message": "Unrecognised category."
            },
            {
              "check": "rule-nocategory",
              "filename": "a.ex",
              "line_no": 7,
              "message": "No category field."
            }
          ]
        }
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingCredo_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "credo: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("credo", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingCredo()
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

        IAuditor auditor = new CredoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("credo", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(Ok(exec));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "1.7.10\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("credo", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithFindings_YieldsFindings_WithRuleIdAndLocation()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            // warning(16) | readability(4) = 20: a mid-range bitmask value.
            return Task.FromResult(new SandboxExecResult(20, JsonWithFindings, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The warning-category finding fails the audit; the readability one is advisory.
        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var inspect = Assert.Single(result.Findings, f => f.Title.Contains("Credo.Check.Warning.IoInspect", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, inspect.Severity);
        Assert.EndsWith("lib/debug.ex:12", inspect.Location, StringComparison.Ordinal);

        var moduledoc = Assert.Single(result.Findings, f => f.Title.Contains("Credo.Check.Readability.ModuleDoc", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, moduledoc.Severity);
        Assert.EndsWith("lib/app.ex:1", moduledoc.Location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvisoryOnlyFixture_Passes_WithFindingsReported()
    {
        // Non-warning categories are advisory: reported, but the audit still passes.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, JsonAdvisoryOnly, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Contains("Credo.Check.Consistency.ExceptionNames", finding.Title, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
        Assert.EndsWith("lib/app.ex:7", finding.Location, StringComparison.Ordinal);
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

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode0_WithFindings_StillReported_ExitStatusFlagCannotSilenceGate()
    {
        // An operator --mute-exit-status turns credo's exit to 0 but the JSON
        // report still carries the issues: findings are still reported and
        // warning-category ones still fail the audit.
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, JsonWithFindings, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(31)]
    public async Task CategoryBitmaskExits_AreFindingsProducingVerdicts(int exitCode)
    {
        // Every value in 1–31 is a combination of category bits
        // (consistency:1, design:2, readability:4, refactor:8, warning:16).
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, JsonAdvisoryOnly, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(130)]
    public async Task ConfigAndGenericErrorExits_AreInfrastructureFailures(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(exitCode, "", "credo failed before analysis"));
        });

        IAuditor auditor = new CredoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("credo", ex.Message, StringComparison.Ordinal);
        Assert.Contains(exitCode.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsExit_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(16, "", "some usage text, not a report"));
        });

        IAuditor auditor = new CredoAuditor();
        await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "credo: command not found"));
        });

        IAuditor auditor = new CredoAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("credo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_MapsCategoriesCorrectly_NoRawStringsPassedThrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(31, JsonWithCategories, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var findings = result.Findings;
        Assert.Equal(7, findings.Count);

        var warning = Assert.Single(findings, f => f.Title.Contains("rule-warning", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Error, warning.Severity);

        var design = Assert.Single(findings, f => f.Title.Contains("rule-design", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, design.Severity);

        var refactor = Assert.Single(findings, f => f.Title.Contains("rule-refactor", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, refactor.Severity);

        var consistency = Assert.Single(findings, f => f.Title.Contains("rule-consistency", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, consistency.Severity);

        var readability = Assert.Single(findings, f => f.Title.Contains("rule-readability", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Info, readability.Severity);

        var unknown = Assert.Single(findings, f => f.Title.Contains("rule-unknown", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, unknown.Severity); // declared fallback default

        var missing = Assert.Single(findings, f => f.Title.Contains("rule-nocategory", StringComparison.Ordinal));
        Assert.Equal(AuditSeverity.Warning, missing.Severity); // absent category -> declared default
    }

    [Fact]
    public async Task DefaultArguments_RunSuggestJson()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        IAuditor auditor = new CredoAuditor();
        await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        Assert.Equal("credo", argv[0]);
        Assert.Contains("suggest", argv);
        var formatIndex = argv.ToList().IndexOf("--format");
        Assert.True(formatIndex >= 0 && formatIndex + 1 < argv.Count);
        Assert.Equal("json", argv[formatIndex + 1]);
        Assert.Equal(".", argv[^1]);
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
            s => s.PluginId == CredoAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("credo", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresCredoRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [CredoAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == CredoAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("credo", tool.Binary);
        // Verify-only by design: credo ships as a Mix escript (plus an Elixir
        // runtime); no distro apt package carries a version pin.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verification = Assert.Single(contributions.VerificationCommands);
        Assert.Contains("credo", string.Join(" ", verification.Argv), StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.7.10\n", ""));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new CredoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "1.7.10",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, JsonClean, ""));
        });

        var auditor = new CredoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "/opt/codeybox/credo.operator.exs",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config-file");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("/opt/codeybox/credo.operator.exs", argv[configIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(5, JsonWithVendoredPaths, ""));
        });

        IAuditor auditor = new CredoAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // The deps/ finding is dropped by the default ExcludePaths; the
        // lib/ finding survives.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("lib/app.ex:3", finding.Location);
        Assert.Equal(AuditSeverity.Info, finding.Severity);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(20, JsonWithFindings, ""));
        });

        var auditor = new CredoAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "Credo.Check.Warning.IoInspect",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("Credo.Check.Warning.IoInspect", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires_credo", "true")]
    public async Task RealCredo_DirtyFixture_YieldsFindings_WithRuleIdAndLine()
    {
        var installed = InstalledCredoVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCredoFixtureRepoAsync(clean: false);

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

            var auditor = new CredoAuditor();
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

            var inspect = Assert.Single(result.Findings, f => f.Title.Contains("Credo.Check.Warning.IoInspect", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, inspect.Severity);
            Assert.EndsWith("bad.ex:3", inspect.Location, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_credo", "true")]
    public async Task RealCredo_CleanFixture_Passes()
    {
        var installed = InstalledCredoVersion;
        if (installed is null)
            return;

        var fixtureDir = await SeedCredoFixtureRepoAsync(clean: true);

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

            var auditor = new CredoAuditor();
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.CredoAuditorPlugin.dll");
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
            PluginId: CredoAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Credo Elixir Linter",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, "credo " + CredoAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("credo", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "credo" && exec.Argv[1] == "--version";

    private static async Task<string> SeedCredoFixtureRepoAsync(bool clean)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-credo-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "lib"));

        // A minimal Mix project so `credo suggest` runs against real project layout.
        await File.WriteAllTextAsync(
            Path.Combine(dir, "mix.exs"),
            """
            defmodule Fixture.MixProject do
              use Mix.Project

              def project do
                [app: :fixture, version: "0.1.0", elixir: "~> 1.14"]
              end
            end
            """);

        if (clean)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "lib", "good.ex"),
                """
                defmodule Fixture.Good do
                  @moduledoc "A clean module."

                  @doc "Greets."
                  @spec greet(String.t()) :: String.t()
                  def greet(name), do: "hello, " <> name
                end
                """);
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "lib", "bad.ex"),
                """
                defmodule Fixture.Bad do
                  def debug(value) do
                    IO.inspect(value)
                  end
                end
                """);
        }

        return dir;
    }

    private static string? ProbeInstalledCredoVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "credo",
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
