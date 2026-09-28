using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.ApiCompatAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the apicompat auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming apicompat (never a pass or finding).
/// - Exits 0 and 1 are the only verdict exits; 1 requires CP####/PKV### diagnostics on stderr —
///   a diagnostic-free 1 (usage error, crash, missing operand) is infrastructure, like 2/126/127.
/// - Diagnostics map to findings with the CP/PKV rule id and the compared artifact path;
///   stderr diagnostics are Error, stdout diagnostics Warning, `info` prefixed Info.
/// - Tool channel levels are mapped through the declared severity mapping (never passed through).
/// - Operand wiring: Left/Right (assembly mode) or Package/BaselinePackage (package mode),
///   one channel each; missing, half-configured, or doubly-configured operands fail deterministically.
/// - Repository-relative suppression files require TrustRepositorySuppression;
///   --generate-suppression-file is refused outright (it hides findings in a file).
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_apicompat", "true")] build fixture
///   assemblies with dotnet and need apicompat on PATH, but no network.
/// </summary>
public sealed class ApiCompatAuditorTests
{
    private static readonly string? InstalledToolVersion = ProbeInstalledToolVersion();
    private static readonly bool HasDotnet = ProbeBinary("dotnet", "--version");

    // Captured verbatim from `apicompat -l <baseline> -r <current>` on
    // Microsoft.DotNet.ApiCompat.Tool 10.0.401: diagnostics ride stderr.
    private const string ReportWithFindings = """
        API compatibility errors between 'baseline/out/Lib.dll' (left) and 'current/out/Lib.dll' (right):
        CP0002: Member 'int FixtureLib.Widget.Removed()' exists on baseline/out/Lib.dll but not on current/out/Lib.dll
        CP0006: Cannot add interface member 'string FixtureLib.ISpeak.Sound' to current/out/Lib.dll because it does not exist on baseline/out/Lib.dll
        API breaking changes found. If those are intentional, the APICompat suppression file can be updated by specifying the '--generate-suppression-file' parameter.
        """;

    private const string ReportWarningsOnly = """
        CP1003: Could not find the following assemblies in the supplied directories.
        """;

    private const string InfoDifferenceLine = """
        info CP0023: Member 'FixtureLib.Widget.Beta()' was marked experimental on the left but is stable on the right.
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingTool_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task WrongVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "9.0.305+abc\n", ""));
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.0.305", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ApiCompatAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChange_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(1, "", ReportWithFindings));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var removed = Assert.Single(
            result.Findings,
            f => f.Location == "current/out/Lib.dll"
                && f.Title.StartsWith("CP0002:", StringComparison.Ordinal));
        Assert.Equal("codeybox:apicompat", removed.AuditorName);
        Assert.Equal(AuditSeverity.Error, removed.Severity);
        Assert.Contains("CP0002", removed.Title, StringComparison.Ordinal);
        Assert.Contains("FixtureLib.Widget.Removed", removed.Description, StringComparison.Ordinal);

        Assert.NotNull(scanExec);
        Assert.Equal("apicompat", scanExec!.Argv[0]);
        var argv = scanExec.Argv;
        var leftIndex = argv.ToList().IndexOf("--left");
        Assert.True(leftIndex >= 0 && argv[leftIndex + 1] == "baseline/out");
        var rightIndex = argv.ToList().IndexOf("--right");
        Assert.True(rightIndex >= 0 && argv[rightIndex + 1] == "current/out");
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(0, "APICompat ran successfully without finding any breaking changes.\n", "")
                : Ok(exec)));

        var auditor = await InitializedAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithDiagnostics_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(1, "", ReportWithFindings)
                : Ok(exec)));

        var auditor = await InitializedAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutDiagnostics_IsInfrastructure()
    {
        // Exit 1 is shared between "findings" and every failure — a usage
        // error or a missing-operand crash produces no CP/PKV lines, so the
        // run is "could not run", never a verdict.
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(1, "", "Unrecognized command or argument '--bogus'.")
                : Ok(exec)));

        var auditor = await InitializedAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(126)]
    [InlineData(134)]
    public async Task NonVerdictExitCodes_AreInfrastructureFailures(int exitCode)
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(exitCode, "", "error: boom")
                : Ok(exec)));

        var auditor = await InitializedAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_ErrorToError_WarningToWarning_InfoToInfo_NoRawPassthrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
            Task.FromResult(IsScanExec(exec)
                ? new SandboxExecResult(1, ReportWarningsOnly + "\n" + InfoDifferenceLine, ReportWithFindings)
                : Ok(exec)));

        var auditor = await InitializedAuditorAsync();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count(f => f.Severity == AuditSeverity.Error));
        var warning = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Warning);
        Assert.StartsWith("CP1003:", warning.Title, StringComparison.Ordinal);
        var info = Assert.Single(result.Findings, f => f.Severity == AuditSeverity.Info);
        Assert.Contains("CP0023", info.Title, StringComparison.Ordinal);
        // The raw tool channel is preserved in the description (tool
        // severity line), proving the value flowed through the mapping.
        var error = Assert.Single(
            result.Findings,
            f => f.Severity == AuditSeverity.Error
                && f.Title.StartsWith("CP0002:", StringComparison.Ordinal));
        Assert.Contains("Severity (tool): error", error.Description, StringComparison.Ordinal);
        Assert.Contains("Severity (tool): warning", warning.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOperandsConfigured_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>());
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("Left", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Right", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task OnlyLeftConfigured_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingOperandMember_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsOperandPresenceProbe(exec))
            {
                // Only the left operand exists; the right is a typo'd path.
                var echoed = string.Join(
                    "\n", exec.Argv.Skip(3).Where(a => a == "baseline/out"));
                return Task.FromResult(new SandboxExecResult(0, echoed + "\n", ""));
            }
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("current/out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ExtraArgumentsLeftRight_ReachScanVerbatim()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:ExtraArguments"] = "--left,baseline/out,--right,current/out",
        });
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Equal(
            ["apicompat", "--left", "baseline/out", "--right", "current/out"],
            scanExec!.Argv);
    }

    [Fact]
    public async Task MixedOperandChannels_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
            ["Scoped:ExtraArguments"] = "--left,other/baseline",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task PackageMode_EmitsSubcommandAndBaseline()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Package"] = "artifacts/pkg/New.1.1.0.nupkg",
            ["Scoped:BaselinePackage"] = "artifacts/pkg/New.1.0.0.nupkg",
        });
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Equal(
            ["apicompat", "package", "artifacts/pkg/New.1.1.0.nupkg",
                "--baseline-package", "artifacts/pkg/New.1.0.0.nupkg"],
            scanExec!.Argv);
    }

    [Fact]
    public async Task PackagePlusAssemblyOperands_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Package"] = "pkg.nupkg",
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RelativeSuppressionFile_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
            ["Scoped:SuppressionFile"] = "eng/CompatibilitySuppressions.xml",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("TrustRepositorySuppression", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task RelativeSuppressionFile_WithTrust_ReachesScan()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
            ["Scoped:SuppressionFile"] = "eng/CompatibilitySuppressions.xml",
            ["Scoped:TrustRepositorySuppression"] = "true",
        });
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var index = argv.ToList().IndexOf("--suppression-file");
        Assert.True(index >= 0 && argv[index + 1] == "eng/CompatibilitySuppressions.xml");
    }

    [Fact]
    public async Task AbsoluteSuppressionFile_AllowedWithoutTrust()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
            {
                scanExec = exec;
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            }
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
            ["Scoped:SuppressionFile"] = "/opt/suppressions/CompatSuppressions.xml",
        });
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        Assert.Contains("/opt/suppressions/CompatSuppressions.xml", scanExec!.Argv);
    }

    [Fact]
    public async Task GenerateSuppressionFile_InExtraArguments_IsDeterministicInfrastructure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsScanExec(exec))
                scanExecs++;
            return Task.FromResult(Ok(exec));
        });

        var auditor = await InitializedAuditorAsync(new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
            ["Scoped:ExtraArguments"] = "--generate-suppression-file",
        });
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("generate-suppression-file", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
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
            s => s.PluginId == ApiCompatAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("apicompat", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresToolRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [ApiCompatAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == ApiCompatAuditor.PluginId);

        var tool = Assert.Single(loader.GetEnabledPluginTools());
        Assert.Equal("apicompat", tool.Binary);
        // Verify-only by design: no distro package carries apicompat — it is
        // provisioned via dotnet tool install.
        Assert.Null(tool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var verificationArgv = string.Join(
            "\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("apicompat", verificationArgv, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_apicompat", "true")]
    public async Task RealTool_BreakingChangeFixture_ProducesFinding()
    {
        if (InstalledToolVersion is null || !HasDotnet)
            return;

        var fixtureDir = await SeedFixtureRepoAsync(breaking: true);
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

            var auditor = new ApiCompatAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledToolVersion,
                    ["Scoped:Left"] = "baseline/out/Lib.dll",
                    ["Scoped:Right"] = "current/out/Lib.dll",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("CP0002", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("current/out/Lib.dll", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_apicompat", "true")]
    public async Task RealTool_CleanFixture_Passes()
    {
        if (InstalledToolVersion is null || !HasDotnet)
            return;

        var fixtureDir = await SeedFixtureRepoAsync(breaking: false);
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

            var auditor = new ApiCompatAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = InstalledToolVersion,
                    ["Scoped:Left"] = "baseline/out/Lib.dll",
                    ["Scoped:Right"] = "current/out/Lib.dll",
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
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ApiCompatAuditorPlugin.dll");
        Assert.True(File.Exists(path), $"Plugin assembly not found at '{path}'.");
        return path;
    }

    private static async Task<ApiCompatAuditor> InitializedAuditorAsync(
        IReadOnlyDictionary<string, string?>? scopedValues = null)
    {
        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(scopedValues ?? DefaultScopedValues),
            CancellationToken.None);
        return auditor;
    }

    private static readonly IReadOnlyDictionary<string, string?> DefaultScopedValues =
        new Dictionary<string, string?>
        {
            ["Scoped:Left"] = "baseline/out",
            ["Scoped:Right"] = "current/out",
        };

    private static PluginContext BuildPluginContext(IReadOnlyDictionary<string, string?> scopedValues)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(scopedValues)
            .Build();
        return new PluginContext(
            HostApiVersion: "1.0",
            PluginId: ApiCompatAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ApiCompat .NET API Compatibility",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
    {
        if (IsVersionProbe(exec))
            return new SandboxExecResult(
                0, ApiCompatAuditor.DefaultExpectedVersion + "+e34a38d2ae1fc26406a317517196e55c68ff83ab\n", "");
        if (IsOperandPresenceProbe(exec))
            return new SandboxExecResult(0, string.Join("\n", exec.Argv.Skip(3)) + "\n", "");
        return new SandboxExecResult(0, "", "");
    }

    private static bool IsScanExec(SandboxExec exec)
        => exec.Argv.Count >= 2
            && exec.Argv[0] == "apicompat"
            && exec.Argv[1] != "--version";

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("apicompat", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "apicompat" && exec.Argv[1] == "--version";

    private static bool IsOperandPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("for f in", StringComparison.Ordinal);

    private static async Task<string> SeedFixtureRepoAsync(bool breaking)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "codeybox-apicompat-fixture-" + Guid.NewGuid().ToString("N")[..8]);

        async Task BuildLibraryAsync(string name, bool withRemovedMember)
        {
            var projectDir = Path.Combine(dir, name, "src");
            Directory.CreateDirectory(projectDir);
            var source = withRemovedMember
                ? "namespace FixtureLib;\npublic class Widget\n{\n    public string Name => \"w\";\n    public int Removed() => 1;\n}\n"
                : "namespace FixtureLib;\npublic class Widget\n{\n    public string Name => \"w\";\n}\n";
            await File.WriteAllTextAsync(
                Path.Combine(projectDir, "Lib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n"
                + "    <TargetFramework>net8.0</TargetFramework>\n"
                + "    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
            await File.WriteAllTextAsync(Path.Combine(projectDir, "Lib.cs"), source);
            await RunProcessAsync(
                dir,
                "dotnet",
                "build", Path.Combine(name, "src", "Lib.csproj"),
                "-c", "Release", "-o", Path.Combine(dir, name, "out"));
        }

        await BuildLibraryAsync("baseline", withRemovedMember: true);
        await BuildLibraryAsync("current", withRemovedMember: !breaking);
        return dir;
    }

    private static async Task RunProcessAsync(string workingDirectory, string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{fileName} {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }

    private static string? ProbeInstalledToolVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "apicompat",
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

    private static bool ProbeBinary(string binary, string arg)
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
            psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            return process.WaitForExit(milliseconds: 10_000) && process.ExitCode == 0;
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
