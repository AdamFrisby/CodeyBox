using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.ApiCompatAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Tools;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the apicompat auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming apicompat (never a pass or finding).
/// - Exits 0 and 1 are verdicts (findings-producing); any other exit is infrastructure.
/// - Exit 1 is ambiguous: CPnnnn diagnostic lines mean findings, while run failures
///   (unhandled-exception dumps, usage errors) with no diagnostic lines fail closed as infrastructure.
/// - Text output maps to findings with rule ids and mapped severity; findings carry no file location.
/// - Raw tool output carries no severity; every finding maps to Error (never passed through).
/// - Assembly mode (LeftAssembly/RightAssembly) and package mode (Package/BaselinePackage),
///   with deterministic failures for conflicting or missing baselines and for
///   suppression-file generation flags.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_apicompat", "true")].
/// </summary>
public sealed class ApiCompatAuditorTests
{
    private static readonly string? InstalledApicompatVersion = ProbeInstalledApicompatVersion();

    private const string BreakingStderr = """
        API compatibility errors between '/left/TestLib.dll' (left) and '/right/TestLib.dll' (right):
        CP0002: Member 'int TestLib.Widget.Id.get' exists on /left/TestLib.dll but not on /right/TestLib.dll
        CP0002: Member 'void TestLib.Widget.Id.set' exists on /left/TestLib.dll but not on /right/TestLib.dll
        API breaking changes found. If those are intentional, the APICompat suppression file can be updated by specifying the '--generate-suppression-file' parameter.
        """;

    private const string CleanStdout = """
        APICompat ran successfully without finding any breaking changes.
        """;

    private const string UnhandledExceptionStderr = """
        Unhandled exception: System.IO.FileNotFoundException: Could not find the provided path '/missing.dll' to load binaries from.
           at Microsoft.DotNet.ApiSymbolExtensions.AssemblySymbolLoader.LoadFromPaths(IEnumerable`1 paths, ImmutableHashSet`1 referenceAssemblyNamesToIgnore)
           at Microsoft.DotNet.ApiCompatibility.Runner.ApiCompatRunner.ExecuteWorkItems()
        """;

    private const string UsageErrorOutput = """
        Option '--right-assembly' is required.

        Description:
          Microsoft.DotNet.ApiCompat v10.0.401+e34a38d2ae1fc26406a317517196e55c68ff83ab
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingApicompat_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "apicompat: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingApicompat()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "9.0.100-preview-unused\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9.0.100", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ApiCompatAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, ApiCompatAuditor.DefaultExpectedVersion + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
                ["Scoped:LeftAssembly"] = "/left/TestLib.dll",
                ["Scoped:RightAssembly"] = "/right/TestLib.dll",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChange_YieldsFindings_WithRuleIdAndNoLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(1, "", BreakingStderr));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        foreach (var finding in result.Findings)
        {
            Assert.Equal("codeybox:apicompat", finding.AuditorName);
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Null(finding.Location);
        }

        Assert.All(
            result.Findings,
            f => Assert.Contains("CP0002", f.Title, StringComparison.Ordinal));

        Assert.NotNull(scanExec);
        Assert.Equal("apicompat", scanExec!.Argv[0]);
        var argv = scanExec.Argv.ToList();
        var leftIndex = argv.IndexOf("--left-assembly");
        Assert.True(leftIndex >= 0 && leftIndex + 1 < argv.Count);
        Assert.Equal("/left/TestLib.dll", argv[leftIndex + 1]);
        var rightIndex = argv.IndexOf("--right-assembly");
        Assert.True(rightIndex >= 0 && rightIndex + 1 < argv.Count);
        Assert.Equal("/right/TestLib.dll", argv[rightIndex + 1]);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(0, CleanStdout, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsExit_ReportsFindings_AndFails()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", BreakingStderr));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_WithoutDiagnostics_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", UnhandledExceptionStderr));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode1_WithUsageError_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, UsageErrorOutput, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExitCode_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(2, "", "boom"));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("apicompat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_EveryFindingIsError_NoRawSeverityPassthrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", BreakingStderr));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(BuildPluginContext(AssemblyScopedConfig()), CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.All(
            result.Findings,
            f => Assert.Contains("Rule: CP0002", f.Description, StringComparison.Ordinal));
        Assert.All(
            result.Findings,
            f => Assert.Contains("Severity (tool): error", f.Description, StringComparison.Ordinal));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            return Task.FromResult(new SandboxExecResult(1, "", BreakingStderr));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:LeftAssembly"] = "/left/TestLib.dll",
                ["Scoped:RightAssembly"] = "/right/TestLib.dll",
                ["Scoped:IncludedRules"] = "CP0001",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task MissingBaseline_IsDeterministicFailure_WithoutScan()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, CleanStdout, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>()),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Contains("no baseline", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task ConflictingModes_AreDeterministicFailure_WithoutScan()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, CleanStdout, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:LeftAssembly"] = "/left/TestLib.dll",
                ["Scoped:RightAssembly"] = "/right/TestLib.dll",
                ["Scoped:Package"] = "/pkgs/current.nupkg",
                ["Scoped:BaselinePackage"] = "/pkgs/baseline.nupkg",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task GenerateSuppressionFile_InExtraArguments_IsDeterministicFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, CleanStdout, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:LeftAssembly"] = "/left/TestLib.dll",
                ["Scoped:RightAssembly"] = "/right/TestLib.dll",
                ["Scoped:ExtraArguments"] = "--generate-suppression-file",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task PackageMode_BuildsPackageArgv()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, CleanStdout, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Package"] = "/pkgs/current.nupkg",
                ["Scoped:BaselinePackage"] = "/pkgs/baseline.nupkg",
            }),
            CancellationToken.None);
        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        Assert.Equal("apicompat", argv[0]);
        Assert.Equal("package", argv[1]);
        Assert.Equal("/pkgs/current.nupkg", argv[2]);
        var baselineIndex = argv.IndexOf("--baseline-package");
        Assert.True(baselineIndex >= 0 && baselineIndex + 1 < argv.Count);
        Assert.Equal("/pkgs/baseline.nupkg", argv[baselineIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_StrictMode_AppendedToAssemblyArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, CleanStdout, ""));
        });

        var auditor = new ApiCompatAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:LeftAssembly"] = "/left/TestLib.dll",
                ["Scoped:RightAssembly"] = "/right/TestLib.dll",
                ["Scoped:StrictMode"] = "true",
                ["Scoped:NoWarn"] = "CP0017",
                ["Scoped:SuppressionFile"] = "/ops/suppressions.xml",
            }),
            CancellationToken.None);
        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv.ToList();
        Assert.Contains("--strict-mode", argv);
        var noWarnIndex = argv.IndexOf("--noWarn");
        Assert.True(noWarnIndex >= 0 && noWarnIndex + 1 < argv.Count);
        Assert.Equal("CP0017", argv[noWarnIndex + 1]);
        var suppressionIndex = argv.IndexOf("--suppression-file");
        Assert.True(suppressionIndex >= 0 && suppressionIndex + 1 < argv.Count);
        Assert.Equal("/ops/suppressions.xml", argv[suppressionIndex + 1]);
    }

    [Fact]
    public void Parser_MapsDiagnosticLines_WithRuleId()
    {
        var parser = new ApiCompatReportParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "apicompat", "", BreakingStderr, 1));

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal("CP0002", f.RuleId));
        Assert.All(findings, f => Assert.Equal("error", f.SeverityLevel));
        Assert.All(findings, f => Assert.Null(f.Path));
        Assert.Contains(findings, f => f.Message.Contains("Id.get", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_CleanMarker_YieldsNoFindings()
    {
        var parser = new ApiCompatReportParser();
        var findings = parser.Parse(new ExternalToolParseInput(
            "apicompat", CleanStdout, "", 0));

        Assert.Empty(findings);
    }

    [Fact]
    public void Parser_EmptyReport_ThrowsParseException()
    {
        var parser = new ApiCompatReportParser();
        Assert.Throws<ExternalToolParseException>(() =>
            parser.Parse(new ExternalToolParseInput("apicompat", "", "", 1)));
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
    public void EnabledPlugin_DeclaresApicompatRequirement_VerifyOnly()
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

        var tools = loader.GetEnabledPluginTools();
        var apicompatTool = Assert.Single(tools, t => t.Binary == "apicompat");
        // Verify-only by design: apicompat has no distro apt package, so no package is specified
        Assert.Null(apicompatTool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var flattened = string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("apicompat", flattened, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_apicompat", "true")]
    public async Task RealApicompat_BreakingFixture_ReportsRuleId()
    {
        var installed = InstalledApicompatVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(Path.GetTempPath(), "codeybox-apicompat-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var baselineDll = BuildFixtureAssembly(fixtureDir, "base", baseline: true);
            var currentDll = BuildFixtureAssembly(fixtureDir, "current", baseline: false);

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts =
                    [
                        new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir },
                    ],
                },
                CancellationToken.None);

            var auditor = new ApiCompatAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:LeftAssembly"] = "/work/base/TestLib.dll",
                    ["Scoped:RightAssembly"] = "/work/current/TestLib.dll",
                }),
                CancellationToken.None);

            Assert.True(File.Exists(baselineDll));
            Assert.True(File.Exists(currentDll));
            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Equal(2, result.Findings.Count);
            Assert.All(
                result.Findings,
                f => Assert.Contains("CP0002", f.Title, StringComparison.Ordinal));
            Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
            Assert.All(result.Findings, f => Assert.Null(f.Location));
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    [Trait("requires_apicompat", "true")]
    public async Task RealApicompat_CleanFixture_Passes()
    {
        var installed = InstalledApicompatVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(Path.GetTempPath(), "codeybox-apicompat-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            BuildFixtureAssembly(fixtureDir, "base", baseline: true);
            BuildFixtureAssembly(fixtureDir, "current", baseline: true);

            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts =
                    [
                        new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir },
                    ],
                },
                CancellationToken.None);

            var auditor = new ApiCompatAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:LeftAssembly"] = "/work/base/TestLib.dll",
                    ["Scoped:RightAssembly"] = "/work/current/TestLib.dll",
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

    private static Dictionary<string, string?> AssemblyScopedConfig() => new()
    {
        ["Scoped:LeftAssembly"] = "/left/TestLib.dll",
        ["Scoped:RightAssembly"] = "/right/TestLib.dll",
    };

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.ApiCompatAuditorPlugin.dll");
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
            PluginId: ApiCompatAuditor.PluginId,
            PluginDisplayName: "CodeyBox: ApiCompat .NET API Compatibility",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, ApiCompatAuditor.DefaultExpectedVersion + "+e34a38d2ae1fc26406a317517196e55c68ff83ab\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("apicompat", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "apicompat" && exec.Argv[1] == "--version";

    private static string BuildFixtureAssembly(string fixtureDir, string name, bool baseline)
    {
        var projectDir = Path.Combine(fixtureDir, "src-" + name);
        var outputDir = Path.Combine(fixtureDir, name);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(
            Path.Combine(projectDir, "fixture.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>TestLib</AssemblyName>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(projectDir, "Lib.cs"),
            """
            namespace TestLib;
            public class Widget
            {
                __ID__
                public string Name { get; set; } = "";
            }
            """.Replace(
                "__ID__",
                baseline ? "public int Id { get; set; }" : string.Empty,
                StringComparison.Ordinal));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectDir,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(milliseconds: 180_000))
        {
            try { process.Kill(); } catch { /* best-effort fixture teardown */ }
            throw new InvalidOperationException($"dotnet build timed out for fixture '{name}'.");
        }
        var dll = Path.Combine(outputDir, "TestLib.dll");
        Assert.True(
            process.ExitCode == 0 && File.Exists(dll),
            $"dotnet build failed for fixture '{name}': {stdout}\n{stderr}");
        return dll;
    }

    private static string? ProbeInstalledApicompatVersion()
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
