using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using CodeyBox.TartSandboxPlugin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the Tart macOS-VM sandbox provider plugin:
/// the kind is constructible and selectable by placement (shared across
/// members naming it); it is classified NotEnforced regardless of what it
/// reports; declared capabilities match the implementation and placement
/// refuses work requiring anything undeclared; service-side failures are
/// infrastructure (never diff verdicts); concurrent use never exceeds member
/// capacity and live load is visible to placement; credentials resolve from
/// the environment chain, never from config files.
/// </summary>
public sealed class TartSandboxProviderTests
{
    private static TartSandboxOptions TestOptions() => new()
    {
        Enabled = true,
        TartBinaryPath = "tart",
        DefaultImage = "ghcr.io/cirruslabs/macos-sequoia-base:latest",
        SshPassword = "test-password",
        ReadyTimeoutSeconds = 30,
        PollIntervalMilliseconds = 10,
        CliTimeoutSeconds = 5,
        SshConnectTimeoutSeconds = 2,
    };

    private static TartSandboxProvider NewProvider(
        FakeTartProcessRunner runner,
        TartSandboxOptions? opts = null,
        Func<bool>? isMacOS = null) =>
        new(
            () => opts ?? TestOptions(),
            runner,
            isMacOS ?? (() => true),
            TimeProvider.System,
            NullLogger.Instance);

    private static SandboxSpec BasicSpec() => new()
    {
        ImageReference = "ghcr.io/cirruslabs/macos-sequoia-base:latest",
        WorkingDirectory = "/work",
    };

    [Fact]
    public void Kind_IsConstructibleAndNamesTart()
    {
        var provider = NewProvider(new FakeTartProcessRunner());

        Assert.Equal("tart", provider.Name);
        Assert.Equal("tart", TartSandboxOptions.ProviderKind);
        Assert.Equal("codeybox.tart-sandbox", TartSandboxOptions.PluginId);
        Assert.False(new TartSandboxOptions().Enabled);
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, provider.IsolationLevel);
    }

    [Fact]
    public async Task DisabledProvider_RefusesNamingKind()
    {
        var provider = NewProvider(new FakeTartProcessRunner(), TestOptions() with { Enabled = false });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(BasicSpec(), CancellationToken.None));
        Assert.Contains("tart", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not enabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonMacOSHost_RefusesNamingKindAsInfrastructure()
    {
        var provider = NewProvider(new FakeTartProcessRunner(), isMacOS: () => false);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("tart", ex.Provider);
        Assert.Equal("unsupported-host", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
    }

    [Fact]
    public async Task CreateAsync_ProvisionsVm_StagesMounts_AndDeletesOnDispose()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);

        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        try
        {
            var hostFile = Path.Combine(hostDir, "input.txt");
            await File.WriteAllTextAsync(hostFile, "hello-mount");
            var spec = BasicSpec() with
            {
                Mounts = [new SandboxMount { SandboxPath = "/data", HostPath = hostDir }],
            };

            await using var sandbox = await provider.CreateAsync(spec, CancellationToken.None);
            Assert.StartsWith("codeybox-", sandbox.Id, StringComparison.Ordinal);

            var clone = Assert.Single(runner.Invocations, i => i.Executable == "tart" && i.Argv is ["clone", _, _]);
            Assert.Equal("ghcr.io/cirruslabs/macos-sequoia-base:latest", clone.Argv[1]);
            var set = Assert.Single(runner.Invocations, i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "set");
            Assert.Contains("--cpu", set.Argv, StringComparer.Ordinal);
            Assert.Contains("--memory", set.Argv, StringComparer.Ordinal);
            var run = Assert.Single(runner.Invocations, i => i.Executable == "tart" && i.Argv.Count > 0 && i.Argv[0] == "run");
            Assert.Contains("--no-graphics", run.Argv, StringComparer.Ordinal);

            var staged = runner.GetGuestFile(sandbox.Id, "/data/input.txt");
            Assert.NotNull(staged);
            Assert.Equal("hello-mount", Encoding.UTF8.GetString(staged));

            // The SSH password travels in the child environment, never argv.
            foreach (var invocation in runner.Invocations)
            {
                foreach (var arg in invocation.Argv)
                    Assert.DoesNotContain("test-password", arg, StringComparison.Ordinal);
            }
            var sshpasses = runner.Invocations.Where(i => i.Executable == "sshpass").ToList();
            Assert.NotEmpty(sshpasses);
            foreach (var sshpass in sshpasses)
            {
                Assert.Equal("ssh", sshpass.Argv[1]);
                Assert.True(sshpass.EnvironmentKeys.ContainsKey("SSHPASS"));
            }

            Assert.Equal(1, provider.ActiveSandboxCount);

            await sandbox.DisposeAsync();
            Assert.Equal(0, provider.ActiveSandboxCount);

            Assert.Contains(runner.Invocations, i => i.Executable == "tart" && i.Argv is ["stop", _]);
            Assert.Contains(runner.Invocations, i => i.Executable == "tart" && i.Argv is ["delete", _]);
            Assert.False(runner.VmExists(sandbox.Id));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecAsync_StreamsOutputChunks_AndReturnsGuestExit()
    {
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = _ => (3, "out-data", "err-data");
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var stdoutChunks = new List<string>();
        var stderrChunks = new List<string>();
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["echo", "hi'; rm -rf /"],
            ExtraEnvironment = new Dictionary<string, string> { ["GREETING"] = "hello world" },
            StdoutChunkCallback = chunk => stdoutChunks.Add(chunk),
            StderrChunkCallback = chunk => stderrChunks.Add(chunk),
        }, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out-data", result.Stdout);
        Assert.Equal("err-data", result.Stderr);
        Assert.Equal("out-data", string.Concat(stdoutChunks));
        Assert.Equal("err-data", string.Concat(stderrChunks));

        var userExec = runner.Invocations.First(i =>
            (i.Executable == "ssh" || i.Executable == "sshpass") && i.Argv[^1].Contains("__cb_code", StringComparison.Ordinal));
        var sentCommand = userExec.Argv[^1];
        Assert.Contains("'GREETING'", sentCommand, StringComparison.Ordinal);
        Assert.Contains("'hello world'", sentCommand, StringComparison.Ordinal);
        Assert.Contains("mkdir -p -- '/work'", sentCommand, StringComparison.Ordinal);
        Assert.Contains("'hi'\\''; rm -rf /'", sentCommand, StringComparison.Ordinal);
        // The remote command is a single argv element — never a shell string.
        Assert.Single(userExec.Argv, a => a.Contains("mkdir -p", StringComparison.Ordinal));
        Assert.True(userExec.Argv.Count >= 3);
    }

    [Fact]
    public async Task ExecAsync_GuestExit255_IsVerdict_NotUnavailable()
    {
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = _ => (255, string.Empty, "guest chose 255");
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["exit", "255"] }, CancellationToken.None);

        Assert.Equal(255, result.ExitCode);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecAsync_SshFailure_IsUnavailable_NotDiffVerdict()
    {
        var runner = new FakeTartProcessRunner { FailSshTransport = true };
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
    }

    [Fact]
    public async Task ExecAsync_Cancellation_ThrowsCancelled()
    {
        var runner = new FakeTartProcessRunner { BlockExecutions = true };
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);

        await Task.Delay(100, CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        runner.ReleaseExecutions();
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task KillActiveExecsAsync_CancelsInFlightExec()
    {
        var runner = new FakeTartProcessRunner { BlockExecutions = true };
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var tart = Assert.IsType<TartSandbox>(sandbox);
        var execTask = tart.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, CancellationToken.None);

        await Task.Delay(100, CancellationToken.None);
        await tart.KillActiveExecsAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        runner.ReleaseExecutions();
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_OutputLimitExceeded_SetsFlags()
    {
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = _ => (0, new string('x', 100), string.Empty);
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            MaxStdoutBytes = 10,
            MaxStderrBytes = 10,
        }, CancellationToken.None);

        Assert.True(result.StdoutLimitExceeded);
        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecAsync_StreamingMode_KeepsTailOnly()
    {
        var runner = new FakeTartProcessRunner();
        runner.ExecutionResponder = _ => (0, new string('y', 5000), string.Empty);
        var provider = NewProvider(runner);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            KillOnOutputLimit = false,
            StreamOutputWithoutKill = true,
            MaxRetainedStdoutBytes = 100,
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.OutputLimitExceeded);
        Assert.Equal(100, result.Stdout.Length);
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementation()
    {
        var provider = NewProvider(new FakeTartProcessRunner());

        Assert.Equal(
            [SandboxCapabilities.SuspendResume, SandboxCapabilities.Teardown],
            provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.BaselineBake, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.DiskGuard, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.CacheSeeding, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.PortPublishing, provider.DeclaredCapabilities);
    }

    [Fact]
    public async Task Placement_UndeclaredCapability_RefusedNamingCapability()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("t", "tart", capabilities: [SandboxCapabilities.BaselineBake])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.BaselineBake], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(SandboxCapabilities.BaselineBake, ex.UnmetCapability);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task Placement_DeclaredCapability_PlacesOnTart()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("t", "tart", capabilities: [SandboxCapabilities.SuspendResume])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.SuspendResume], null, null, BasicSpec()),
            CancellationToken.None);

        Assert.StartsWith("codeybox-", sandbox.Id, StringComparison.Ordinal);
        Assert.Equal(1, provider.ActiveSandboxCount);
    }

    [Fact]
    public void PluginCatalog_AcceptsKind_SharesInstance_RegardlessOfOrder()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);
        var catalog = new PluginSandboxProviderCatalog([("codeybox.tart-sandbox", provider)]);
        Assert.True(catalog.IsPluginKind("tart"));
        Assert.True(catalog.TryGetProvider("TART ", out var resolved));
        Assert.Same(provider, resolved);

        var builds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var registry = new SandboxProviderRegistry(
            kind =>
            {
                if (catalog.TryGetProvider(kind, out var pluginProvider))
                {
                    builds[kind] = builds.TryGetValue(kind, out var count) ? count + 1 : 1;
                    return pluginProvider;
                }

                throw new InvalidOperationException($"Unregistered kind '{kind}'.");
            },
            pluginKinds: catalog.Kinds);

        var first = registry.Resolve(SandboxPlacementTestMembers.Member("a", "tart"));
        var second = registry.Resolve(SandboxPlacementTestMembers.Member("b", "TART "));
        Assert.Same(provider, first);
        Assert.Same(first, second);
        Assert.Equal(1, builds["tart"]);
    }

    [Fact]
    public void PluginAttribute_DeclaresCompatibleHostApi()
    {
        var attribute = typeof(TartSandboxProvider)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();

        Assert.Equal(TartSandboxOptions.PluginId, attribute.Id);
        Assert.True(
            Version.TryParse(attribute.MinHostApiVersion, out _),
            $"MinHostApiVersion '{attribute.MinHostApiVersion}' must parse so the host gate can compare it.");
    }

    [Fact]
    public void Kind_ClassifiedNotEnforced()
    {
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("tart"));
        Assert.False(SandboxEgressPolicy.IsEnforced("tart"));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement("tart"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfiledWork_RefusedNamingKind()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("t", "tart")),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", BasicSpec()),
            CancellationToken.None));

        Assert.Equal("enforced-egress", ex.UnmetCapability);
        Assert.Contains("tart", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ProfiledSpec_RefusedAtSink()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var spec = BasicSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "llm" } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_GraphicalSpec_Refused()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var spec = BasicSpec() with { Flavor = SandboxProfileFlavor.Graphical };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RecoveryLease_RefusedExplicitly()
    {
        var runner = new FakeTartProcessRunner();
        var provider = NewProvider(runner);
        var spec = BasicSpec() with { RecoveryLease = new SandboxRecoveryLease("tart", "vm-1", "token-1") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("recovery-lease", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.EnteredClones);
    }

    [Fact]
    public async Task CreateAsync_MissingMountSource_SurfacesForOrchestratorRetry()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()) }],
        };

        var ex = await Assert.ThrowsAsync<SandboxMountSourceMissingException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains(spec.Mounts[0].HostPath!, ex.HostPath, StringComparison.Ordinal);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_CredentialTmpfs_Refused()
    {
        var provider = NewProvider(new FakeTartProcessRunner());
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/run/codeybox/creds", Tmpfs = true }],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("environment variables", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialMountPath_MatchesHostConvention()
    {
        Assert.Equal(SandboxConventions.CredentialsDir, TartSandboxProvider.CredentialMountPath);
    }

    [Fact]
    public void MightOwnSandbox_MatchesPrefixOnly()
    {
        var provider = NewProvider(new FakeTartProcessRunner());

        Assert.True(provider.MightOwnSandbox("codeybox-abc", null));
        Assert.False(provider.MightOwnSandbox("someone-else", null));
        Assert.True(provider.MightOwnSandbox(string.Empty, null));
    }
}
