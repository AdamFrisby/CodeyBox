using System.Diagnostics;
using System.Text.RegularExpressions;
using CodeyBox.BufAuditorPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the buf auditor plugin:
/// - Missing or wrong-version binary is an infrastructure failure naming buf (never a pass or finding).
/// - Exits 0 and 100 are verdicts (findings-producing), while exit 1 and others are infrastructure.
/// - Exit 100 without a parseable JSON report fails closed as an infrastructure failure.
/// - JSON-lines output maps to findings with rule ids, locations, and mapped severity.
/// - Raw tool output carries no severity; every finding maps to Error (never passed through).
/// - Default exclusions (vendor/, third_party/, node_modules/) and options (Against, AgainstRegistry, ConfigPath).
/// - Default baseline resolves the HEAD/base-branch merge-base; conflicting baselines fail deterministically.
/// - Plugin is disabled by default, absent from baseline provisioning until enabled.
/// - Real binary execution tests under [Trait("requires_buf", "true")].
/// </summary>
public sealed class BufAuditorTests
{
    private static readonly string? InstalledBufVersion = ProbeInstalledBufVersion();

    private const string JsonWithBreakingChange = """
        {"path":"proto/user.proto","start_line":4,"start_column":3,"end_line":4,"end_column":9,"type":"FIELD_SAME_TYPE","message":"Field \"1\" with name \"id\" on message \"User\" changed type from \"int32\" to \"string\"."}
        {"start_line":1,"start_column":1,"end_line":1,"end_column":1,"type":"FILE_NO_DELETE","message":"Previously present file \"old.proto\" was deleted."}
        """;

    private const string JsonWithSeverities = """
        {"path":"a.proto","start_line":1,"type":"FIELD_SAME_TYPE","message":"type change"}
        {"path":"b.proto","start_line":2,"type":"FILE_NO_DELETE","message":"deleted file"}
        """;

    private const string JsonWithFilteredPaths = """
        {"path":"proto/user.proto","start_line":4,"type":"FIELD_SAME_TYPE","message":"Root type change"}
        {"path":"vendor/protos/user.proto","start_line":7,"type":"FIELD_SAME_TYPE","message":"Vendored type change"}
        {"path":"third_party/protos/user.proto","start_line":9,"type":"FIELD_SAME_TYPE","message":"Third-party type change"}
        {"path":"node_modules/pkg/user.proto","start_line":11,"type":"FIELD_SAME_TYPE","message":"Dependency type change"}
        {"start_line":1,"type":"FILE_NO_DELETE","message":"Previously present file \"gone.proto\" was deleted."}
        """;

    [Fact]
    public async Task MissingBinary_IsInfrastructureFailure_NamingBuf_NeverAPass()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec))
                return Task.FromResult(new SandboxExecResult(1, "", ""));
            if (IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(127, "", "buf: command not found"));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new BufAuditor();
        await ((BufAuditor)auditor).InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Against"] = "/baseline",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task VersionProbeFailed_IsInfrastructureFailure_NamingBuf()
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

        IAuditor auditor = new BufAuditor();
        await ((BufAuditor)auditor).InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Against"] = "/baseline",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
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
                return Task.FromResult(new SandboxExecResult(0, "1.72.0\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new BufAuditor();
        await ((BufAuditor)auditor).InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Against"] = "/baseline",
            }),
            CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.72.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains(BufAuditor.DefaultExpectedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task UnparseableExpectedVersion_IsInfrastructureFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(new SandboxExecResult(0, BufAuditor.DefaultExpectedVersion + "\n", ""));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new BufAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ExpectedVersion"] = "not-a-valid-version-string",
                ["Scoped:Against"] = "/baseline",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("unparseable ExpectedVersion", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task Fixture_WithBreakingChange_YieldsFindings_WithRuleIdAndLocation()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(100, JsonWithBreakingChange, ""));
        });

        IAuditor auditor = new BufAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Findings.Count);

        var typeChange = Assert.Single(
            result.Findings, f => f.Title.Contains("FIELD_SAME_TYPE", StringComparison.Ordinal));
        Assert.Equal("codeybox:buf", typeChange.AuditorName);
        Assert.Equal(AuditSeverity.Error, typeChange.Severity);
        Assert.Equal("proto/user.proto:4", typeChange.Location);

        var deleted = Assert.Single(
            result.Findings, f => f.Title.Contains("FILE_NO_DELETE", StringComparison.Ordinal));
        Assert.Equal("codeybox:buf", deleted.AuditorName);
        Assert.Equal(AuditSeverity.Error, deleted.Severity);
        Assert.Null(deleted.Location);

        Assert.NotNull(scanExec);
        Assert.Equal("buf", scanExec!.Argv[0]);
        Assert.Equal("breaking", scanExec.Argv[1]);
        Assert.Contains("--error-format", scanExec.Argv);
        Assert.Contains("json", scanExec.Argv);
        Assert.Contains("--against", scanExec.Argv);
    }

    [Fact]
    public async Task CleanFixture_YieldsZeroFindings_AndPasses()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new BufAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

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
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(100, JsonWithBreakingChange, ""));
        });

        IAuditor auditor = new BufAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public async Task ExitCode1_IsInfrastructureFailure_ThrowsAuditUnavailableException()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(1, "", "Failure: Module \"path: \"proto\"\" had no .proto files"));
        });

        IAuditor auditor = new BufAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode100_WithoutJson_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(100, "", ""));
        });

        IAuditor auditor = new BufAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode100_WithGarbageOutput_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(100, "Failure: something went wrong\n", ""));
        });

        IAuditor auditor = new BufAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode127_IsInfrastructureFailure()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(127, "", "buf: command not found"));
        });

        IAuditor auditor = new BufAuditor();
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.Contains("buf", ex.Message, StringComparison.Ordinal);
        Assert.Contains("127", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeverityMapping_EveryFindingIsError_NoRawSeverityPassthrough()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(100, JsonWithSeverities, ""));
        });

        IAuditor auditor = new BufAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(AuditSeverity.Error, f.Severity));
        Assert.All(
            result.Findings,
            f => Assert.Contains("Rule:", f.Description, StringComparison.Ordinal));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScopedConfiguration_ExcludePaths_FiltersDefaultVendoredFindings()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(100, JsonWithFilteredPaths, ""));
        });

        IAuditor auditor = new BufAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        // vendor/, third_party/, and node_modules/ findings are excluded by default;
        // the root finding and the pathless FILE_NO_DELETE finding still surface.
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Location == "proto/user.proto:4");
        Assert.Contains(result.Findings, f => f.Title.Contains("FILE_NO_DELETE", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Location?.StartsWith("vendor/", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Findings, f => f.Location?.StartsWith("third_party/", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Findings, f => f.Location?.StartsWith("node_modules/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ScopedConfiguration_IncludedRules_FiltersOtherRules()
    {
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            return Task.FromResult(new SandboxExecResult(100, JsonWithFilteredPaths, ""));
        });

        var auditor = new BufAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:IncludedRules"] = "FILE_NO_DELETE",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("FILE_NO_DELETE", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedConfiguration_Against_OverridesDefaultBaseline_WithoutGitProbes()
    {
        var gitExecs = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
            {
                gitExecs++;
                return Task.FromResult(GitProbeResult(exec));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new BufAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Against"] = "/baseline",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, gitExecs);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var againstIndex = argv.ToList().IndexOf("--against");
        Assert.True(againstIndex >= 0 && againstIndex + 1 < argv.Count);
        Assert.Equal("/baseline", argv[againstIndex + 1]);
    }

    [Fact]
    public async Task ScopedConfiguration_AgainstRegistry_UsesFlag_WithoutGitProbes()
    {
        var gitExecs = 0;
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
            {
                gitExecs++;
                return Task.FromResult(GitProbeResult(exec));
            }
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new BufAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:AgainstRegistry"] = "true",
            }),
            CancellationToken.None);

        var result = await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(0, gitExecs);
        Assert.NotNull(scanExec);
        Assert.Contains("--against-registry", scanExec!.Argv);
        Assert.DoesNotContain("--against", scanExec.Argv);
    }

    [Fact]
    public async Task ScopedConfiguration_ConfigPath_AppendedToArguments()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new BufAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:ConfigPath"] = "ops/buf.yaml",
            }),
            CancellationToken.None);

        await ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var configIndex = argv.ToList().IndexOf("--config");
        Assert.True(configIndex >= 0 && configIndex + 1 < argv.Count);
        Assert.Equal("ops/buf.yaml", argv[configIndex + 1]);
    }

    [Fact]
    public async Task DefaultBaseline_ResolvesMergeBaseRef()
    {
        SandboxExec? scanExec = null;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            if (IsGitProbe(exec))
                return Task.FromResult(GitProbeResult(exec));
            scanExec = exec;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new BufAuditor();
        var result = await auditor.RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.NotNull(scanExec);
        var argv = scanExec!.Argv;
        var againstIndex = argv.ToList().IndexOf("--against");
        Assert.True(againstIndex >= 0 && againstIndex + 1 < argv.Count);
        Assert.Equal(".git#ref=" + FakeMergeBaseSha, argv[againstIndex + 1]);
    }

    [Fact]
    public async Task ConflictingBaselines_AreDeterministicFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        var auditor = new BufAuditor();
        await auditor.InitializeAsync(
            BuildPluginContext(new Dictionary<string, string?>
            {
                ["Scoped:Against"] = "/baseline",
                ["Scoped:AgainstRegistry"] = "true",
            }),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => ((IAuditor)auditor).RunAsync(sandbox, "/work", FakeContext(), CancellationToken.None));

        Assert.True(ex.IsDeterministic);
        Assert.Equal(0, scanExecs);
    }

    [Fact]
    public async Task MissingBaseBranch_WithoutAgainst_IsDeterministicFailure()
    {
        var scanExecs = 0;
        var sandbox = new FakeSandbox((exec, _) =>
        {
            if (IsPresenceProbe(exec) || IsVersionProbe(exec))
                return Task.FromResult(Ok(exec));
            scanExecs++;
            return Task.FromResult(new SandboxExecResult(0, "", ""));
        });

        IAuditor auditor = new BufAuditor();
        var context = FakeContext() with { BaseBranch = "" };
        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(
            () => auditor.RunAsync(sandbox, "/work", context, CancellationToken.None));

        Assert.True(ex.IsDeterministic);
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
            s => s.PluginId == BufAuditor.PluginId);
        Assert.Equal(PluginSkipReason.Disabled, status.SkipReason);

        var tools = loader.GetEnabledPluginTools();
        Assert.Empty(tools);

        var contributions = PluginBaselineProvisioning.BuildContributions(tools);
        var flattened = string.Join("\n", contributions.InstallCommands)
            + "\n" + string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.DoesNotContain("buf", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledPlugin_DeclaresBufRequirement_VerifyOnly()
    {
        var assemblyPath = PluginAssemblyPath();
        var loader = new PluginLoader(
            new PluginOptions
            {
                AssemblyPaths = [assemblyPath],
                Allowlist = ["*"],
                Enabled = [BufAuditor.PluginId],
            },
            new ConfigurationBuilder().Build(),
            NullLogger<PluginLoader>.Instance);

        var plugins = loader.DiscoverPlugins();
        Assert.Contains(plugins, p => p.PluginId == BufAuditor.PluginId);

        var tools = loader.GetEnabledPluginTools();
        var bufTool = Assert.Single(tools, t => t.Binary == "buf");
        // Verify-only by design: buf has no distro apt package, so no package is specified
        Assert.Null(bufTool.AptPackage);

        var contributions = PluginBaselineProvisioning.BuildContributions(loader.GetEnabledPluginTools());
        var flattened = string.Join("\n", contributions.VerificationCommands.SelectMany(static v => v.Argv));
        Assert.Contains("buf", flattened, StringComparison.Ordinal);
        Assert.Empty(contributions.InstallCommands);
    }

    [Fact]
    [Trait("requires_buf", "true")]
    public async Task RealBuf_BreakingFixture_ReportsRuleIdAndLocation()
    {
        var installed = InstalledBufVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(Path.GetTempPath(), "codeybox-buf-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var baselineDir = Path.Combine(Path.GetTempPath(), "codeybox-buf-baseline-" + Guid.NewGuid().ToString("N")[..8]);
        SeedBufFixture(baselineDir, baseline: true);
        SeedBufFixture(fixtureDir, baseline: false);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts =
                    [
                        new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir },
                        new SandboxMount { SandboxPath = "/baseline", HostPath = baselineDir, ReadOnly = true },
                    ],
                },
                CancellationToken.None);

            var auditor = new BufAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Against"] = "/baseline",
                }),
                CancellationToken.None);

            var result = await ((IAuditor)auditor).RunAsync(
                sandbox, "/work", FakeContext(), CancellationToken.None);

            Assert.False(result.Passed);
            var finding = Assert.Single(
                result.Findings, f => f.Title.Contains("FIELD_SAME_TYPE", StringComparison.Ordinal));
            Assert.Equal(AuditSeverity.Error, finding.Severity);
            Assert.Equal("proto/user.proto:4", finding.Location);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
            TryDeleteDirectory(baselineDir);
        }
    }

    [Fact]
    [Trait("requires_buf", "true")]
    public async Task RealBuf_CleanFixture_Passes()
    {
        var installed = InstalledBufVersion;
        if (installed is null)
            return;

        var fixtureDir = Path.Combine(Path.GetTempPath(), "codeybox-buf-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        var baselineDir = Path.Combine(Path.GetTempPath(), "codeybox-buf-baseline-" + Guid.NewGuid().ToString("N")[..8]);
        SeedBufFixture(baselineDir, baseline: true);
        SeedBufFixture(fixtureDir, baseline: true);

        try
        {
            var provider = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
            await using var sandbox = await provider.CreateAsync(
                new SandboxSpec
                {
                    ImageReference = "ignored",
                    WorkingDirectory = "/work",
                    Mounts =
                    [
                        new SandboxMount { SandboxPath = "/work", HostPath = fixtureDir },
                        new SandboxMount { SandboxPath = "/baseline", HostPath = baselineDir, ReadOnly = true },
                    ],
                },
                CancellationToken.None);

            var auditor = new BufAuditor();
            await auditor.InitializeAsync(
                BuildPluginContext(new Dictionary<string, string?>
                {
                    ["Scoped:ExpectedVersion"] = installed,
                    ["Scoped:Against"] = "/baseline",
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
            TryDeleteDirectory(baselineDir);
        }
    }

    private static string PluginAssemblyPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodeyBox.BufAuditorPlugin.dll");
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
            PluginId: BufAuditor.PluginId,
            PluginDisplayName: "CodeyBox: Buf Protobuf Breaking Changes",
            Host: new TestPluginHost(config.GetSection("Scoped")));
    }

    private static SandboxExecResult Ok(SandboxExec exec)
        => IsVersionProbe(exec)
            ? new SandboxExecResult(0, BufAuditor.DefaultExpectedVersion + "\n", "")
            : new SandboxExecResult(0, "", "");

    private static bool IsPresenceProbe(SandboxExec exec)
        => exec.Argv.Count >= 3
            && exec.Argv[0] == "sh"
            && exec.Argv[1] == "-c"
            && exec.Argv[2].Contains("command -v", StringComparison.Ordinal)
            && exec.Argv.Contains("buf", StringComparer.Ordinal);

    private static bool IsVersionProbe(SandboxExec exec)
        => exec.Argv.Count == 2 && exec.Argv[0] == "buf" && exec.Argv[1] == "--version";

    private static bool IsGitProbe(SandboxExec exec)
        => exec.Argv.Count > 0 && exec.Argv[0] == "git";

    private const string FakeBaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FakeMergeBaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static SandboxExecResult GitProbeResult(SandboxExec exec)
    {
        if (exec.Argv.Contains("merge-base", StringComparer.Ordinal))
            return new SandboxExecResult(0, FakeMergeBaseSha + "\n", "");
        return new SandboxExecResult(0, FakeBaseSha + "\n", "");
    }

    private static void SeedBufFixture(string dir, bool baseline)
    {
        Directory.CreateDirectory(Path.Combine(dir, "proto"));
        File.WriteAllText(
            Path.Combine(dir, "buf.yaml"),
            """
            version: v2
            modules:
              - path: proto
            breaking:
              use:
                - FILE
            """);
        File.WriteAllText(
            Path.Combine(dir, "proto", "user.proto"),
            """
            syntax = "proto3";
            package test.v1;
            message User {
              __TYPE__ id = 1;
              string name = 2;
            }
            """.Replace("__TYPE__", baseline ? "int32" : "string", StringComparison.Ordinal));
    }

    private static string? ProbeInstalledBufVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "buf",
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
