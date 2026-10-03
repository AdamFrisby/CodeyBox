using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.BlaxelPlugin;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the Blaxel perpetual-sandbox provider plugin:
/// the kind is constructible and selectable by placement (shared across
/// members naming it); it is classified NotEnforced regardless of what it
/// reports; declared capabilities match the implementation and placement
/// refuses work requiring anything undeclared; service-side failures are
/// infrastructure (never diff verdicts); concurrent use never exceeds member
/// capacity and live load is visible to placement; credentials resolve from
/// the environment chain, never from config files; suspend settles into
/// standby (memory, processes, and filesystem preserved) and every resume is
/// exec-probed before it counts.
/// </summary>
public sealed class BlaxelSandboxProviderTests
{
    private static BlaxelSandboxOptions TestOptions() => new()
    {
        ApiKey = "test-key",
        Workspace = "test-workspace",
        ApiBaseUrl = "https://api.blaxel.ai/v0",
        WaitForRunningTimeout = TimeSpan.FromSeconds(10),
        ExecPollInterval = TimeSpan.FromMilliseconds(10),
        SuspendWaitTimeout = TimeSpan.FromSeconds(5),
        ApiTimeout = TimeSpan.FromSeconds(5),
    };

    private static BlaxelSandboxProvider NewProvider(FakeBlaxelHandler handler, BlaxelSandboxOptions? opts = null)
    {
        var options = opts ?? TestOptions();
        return new BlaxelSandboxProvider(
            () => options,
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger.Instance);
    }

    private static SandboxSpec BasicSpec() => new()
    {
        ImageReference = "ignored",
        WorkingDirectory = "/work",
    };

    [Fact]
    public async Task CreateAsync_ProvisionsSandbox_StagesMounts_AndDeletesOnDispose()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

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

            var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v0/sandboxes");
            Assert.Equal("Bearer test-key", create.BlaxelAuthorization);
            Assert.Equal("test-workspace", create.BlaxelWorkspace);
            using (var doc = JsonDocument.Parse(create.Body))
            {
                var metadata = doc.RootElement.GetProperty("metadata");
                Assert.StartsWith("codeybox-", metadata.GetProperty("name").GetString(), StringComparison.Ordinal);
                Assert.Equal("true", metadata.GetProperty("labels").GetProperty("codeybox-managed").GetString());
                Assert.Equal("blaxel", metadata.GetProperty("labels").GetProperty("codeybox-provider").GetString());
                var runtime = doc.RootElement.GetProperty("spec").GetProperty("runtime");
                Assert.Equal("blaxel/base-image:latest", runtime.GetProperty("image").GetString());
                Assert.Equal(8192, runtime.GetProperty("memory").GetInt32());
                Assert.Equal("48h", runtime.GetProperty("ttl").GetString());
            }

            var staged = handler.Requests
                .Where(r => r.Method == HttpMethod.Post && r.Path == "/process")
                .Select(r => r.Body)
                .ToList();
            Assert.NotEmpty(staged);
            Assert.Contains(staged, b => b.Contains("/data/input.txt", StringComparison.Ordinal));
            var mountPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello-mount"));
            Assert.Contains(staged, b => b.Contains(mountPayload, StringComparison.Ordinal));

            // Every request — control plane and data plane — carries both headers.
            Assert.All(handler.Requests, r =>
            {
                Assert.Equal("Bearer test-key", r.BlaxelAuthorization);
                Assert.Equal("test-workspace", r.BlaxelWorkspace);
            });

            Assert.Equal(1, provider.ActiveSandboxCount);

            await sandbox.DisposeAsync();
            Assert.Equal(0, provider.ActiveSandboxCount);

            var deleted = Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Delete && r.Path.StartsWith("/v0/sandboxes/", StringComparison.Ordinal));
            Assert.EndsWith(sandbox.Id, deleted.Path, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecAsync_StreamsOutputChunks_AndReturnsExitCode()
    {
        var handler = new FakeBlaxelHandler();
        handler.ExecutionResponder = (_, _) => (3, "out-data", "err-data");
        var provider = NewProvider(handler);

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

        var started = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/process" && r.Body.Contains("echo", StringComparison.Ordinal));
        // The recorded body is JSON-encoded; decode the command property before
        // asserting on its shell contents.
        var sentCommand = JsonDocument.Parse(started.Body).RootElement.GetProperty("command").GetString();
        Assert.NotNull(sentCommand);
        Assert.Contains("mkdir -p -- '/work'", sentCommand, StringComparison.Ordinal);
        Assert.Contains("'hi'\\''; rm -rf /'", sentCommand, StringComparison.Ordinal);
        // Environment travels natively in ProcessRequest.env, never in the command string.
        Assert.DoesNotContain("hello world", sentCommand, StringComparison.Ordinal);
        var env = JsonDocument.Parse(started.Body).RootElement.GetProperty("env");
        Assert.Equal("hello world", env.GetProperty("GREETING").GetString());
    }

    [Fact]
    public async Task ExecAsync_SecretEnvironment_NeverInlinesValuesInCommand()
    {
        const string secret = "s3cr3t-v4lue";
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "CODEYBOX_TEST_SECRET"],
            ExtraEnvironment = new Dictionary<string, string> { ["CODEYBOX_TEST_SECRET"] = secret },
            EnvironmentVariablesToUnset = ["STALE_VAR"],
            EnvironmentContainsSecrets = true,
        }, CancellationToken.None);

        Assert.True(result.Success, result.Stderr);

        var started = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/process" && r.Body.Contains("printenv", StringComparison.Ordinal));
        var sentCommand = JsonDocument.Parse(started.Body).RootElement.GetProperty("command").GetString();
        Assert.NotNull(sentCommand);
        Assert.DoesNotContain(secret, sentCommand, StringComparison.Ordinal);
        var env = JsonDocument.Parse(started.Body).RootElement.GetProperty("env");
        Assert.Equal(secret, env.GetProperty("CODEYBOX_TEST_SECRET").GetString());
        Assert.False(env.TryGetProperty("STALE_VAR", out _));
    }

    [Fact]
    public async Task ExecAsync_Cancellation_KillsProcess_AndThrowsCancelled()
    {
        var handler = new FakeBlaxelHandler { BlockProcesses = true };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);

        while (handler.StartedProcesses.Count == 0)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/kill", StringComparison.Ordinal));
        handler.ReleaseProcesses();
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_OutputLimitExceeded_Kills_AndSetsFlags()
    {
        var handler = new FakeBlaxelHandler();
        handler.ExecutionResponder = (_, _) => (0, new string('x', 100), string.Empty);
        var provider = NewProvider(handler);

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
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/kill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecAsync_StreamingMode_NeverKillsOnVolume()
    {
        var handler = new FakeBlaxelHandler();
        handler.ExecutionResponder = (_, _) => (0, new string('x', 100), string.Empty);
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var chunks = new List<string>();
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["yes"],
            StreamOutputWithoutKill = true,
            KillOnOutputLimit = false,
            MaxRetainedStdoutBytes = 16,
            StdoutChunkCallback = chunk => chunks.Add(chunk),
        }, CancellationToken.None);

        Assert.True(result.Success, result.Stderr);
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/kill", StringComparison.Ordinal));
        Assert.NotEmpty(chunks);
        Assert.True(result.Stdout.Length <= 16);
    }

    [Fact]
    public async Task ExecAsync_StreamingWithKillThreshold_Rejected()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["true"],
            StreamOutputWithoutKill = true,
            KillOnOutputLimit = true,
        }, CancellationToken.None));
    }

    [Fact]
    public async Task SuspendAsync_PreservesSandbox_AcrossDispose()
    {
        var handler = new FakeBlaxelHandler { StandbyAfterPolls = 1 };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        await suspendable.SuspendAsync(CancellationToken.None);

        Assert.True(sandbox is BlaxelSandbox { IsSuspended: true });
        var standbySeen = handler.Requests.Count(r => r.Method == HttpMethod.Get && r.Path.StartsWith("/v0/sandboxes/", StringComparison.Ordinal));
        Assert.True(standbySeen >= 2, $"expected standby polling, saw {standbySeen} GETs");

        await sandbox.DisposeAsync();
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete && r.Path.StartsWith("/v0/sandboxes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuspendAsync_NeverSettles_FailsAsInfrastructure()
    {
        var handler = new FakeBlaxelHandler { StandbyAfterPolls = int.MaxValue };
        var provider = NewProvider(handler, TestOptions() with { SuspendWaitTimeout = TimeSpan.FromMilliseconds(50) });

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(() => suspendable.SuspendAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResumeSandboxAsync_StandbySandbox_WakesAndProvesGuest()
    {
        var handler = new FakeBlaxelHandler { InitialState = "STANDBY" };
        handler.SeedSandbox("codeybox-standby-1");
        var provider = NewProvider(handler);

        await provider.ResumeSandboxAsync("codeybox-standby-1", CancellationToken.None);

        // A data-plane touch woke the sandbox (unknown process => 404, which is
        // itself proof the endpoint answered), then the resume probe must echo.
        var probe = handler.StartedProcesses.FirstOrDefault(p =>
            p.Command.StartsWith("printf %s 'resume-probe-", StringComparison.Ordinal));
        Assert.NotNull(probe);
        Assert.EndsWith("'", probe.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeSandboxAsync_UnverifiedGuest_RefusesResume()
    {
        var handler = new FakeBlaxelHandler { InitialState = "STANDBY", BreakResumeProbe = true };
        handler.SeedSandbox("codeybox-standby-2");
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.ResumeSandboxAsync("codeybox-standby-2", CancellationToken.None));
        Assert.Equal("blaxel", ex.Provider);
        Assert.Equal("resume-probe", ex.Operation);
    }

    [Fact]
    public async Task ResumeSandboxAsync_MissingSandbox_IsNonFatal()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

        await provider.ResumeSandboxAsync("codeybox-gone", CancellationToken.None);
    }

    [Fact]
    public async Task ResumeSandboxAsync_AlreadyRunning_SkipsProbe()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var processCount = handler.StartedProcesses.Count;
        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        Assert.Equal(processCount, handler.StartedProcesses.Count);
    }

    [Fact]
    public async Task SuspendResumeCycle_PreservesGuestFiles()
    {
        var handler = new FakeBlaxelHandler { StandbyAfterPolls = 1 };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var blaxel = Assert.IsType<BlaxelSandbox>(sandbox);
        await blaxel.WriteFileAsync("/work/live-note.txt", "live-contents", CancellationToken.None);
        await ((ISuspendableSandbox)sandbox).SuspendAsync(CancellationToken.None);
        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        Assert.Equal("live-contents", await blaxel.ReadFileAsync("/work/live-note.txt", CancellationToken.None));
    }

    [Fact]
    public async Task WritableMount_SyncedBackOnce_AtTeardown()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        try
        {
            var spec = BasicSpec() with
            {
                Mounts = [new SandboxMount { SandboxPath = "/out", HostPath = hostDir, ReadOnly = false }],
            };

            await using (var sandbox = await provider.CreateAsync(spec, CancellationToken.None))
            {
                var blaxel = Assert.IsType<BlaxelSandbox>(sandbox);
                await blaxel.WriteFileAsync("/out/result.txt", "guest-output", CancellationToken.None);
            }

            Assert.Equal("guest-output", await File.ReadAllTextAsync(Path.Combine(hostDir, "result.txt")));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementation()
    {
        var provider = NewProvider(new FakeBlaxelHandler());

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
        var provider = NewProvider(new FakeBlaxelHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("bx", "blaxel", capabilities: [SandboxCapabilities.BaselineBake])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.BaselineBake], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(SandboxCapabilities.BaselineBake, ex.UnmetCapability);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task Placement_DeclaredCapability_PlacesOnBlaxel()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("bx", "blaxel", capabilities: [SandboxCapabilities.SuspendResume])),
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
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);
        var catalog = new PluginSandboxProviderCatalog([("codeybox.blaxel", provider)]);
        Assert.True(catalog.IsPluginKind("blaxel"));
        Assert.True(catalog.TryGetProvider("BLAXEL ", out var resolved));
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

        var first = registry.Resolve(SandboxPlacementTestMembers.Member("a", "blaxel"));
        var second = registry.Resolve(SandboxPlacementTestMembers.Member("b", "BLAXEL "));
        Assert.Same(provider, first);
        Assert.Same(first, second);
        Assert.Equal(1, builds["blaxel"]);
    }

    [Fact]
    public void PluginAttribute_DeclaresCompatibleHostApi()
    {
        var attribute = typeof(BlaxelSandboxProvider)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();

        Assert.Equal(BlaxelSandboxOptions.PluginId, attribute.Id);
        Assert.True(
            Version.TryParse(attribute.MinHostApiVersion, out _),
            $"MinHostApiVersion '{attribute.MinHostApiVersion}' must parse so the host gate can compare it.");
    }

    [Fact]
    public void Kind_ClassifiedNotEnforced()
    {
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("blaxel"));
        Assert.False(SandboxEgressPolicy.IsEnforced("blaxel"));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement("blaxel"), StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_DoesNotClaimContainment()
    {
        Assert.Equal(SandboxIsolationLevel.DedicatedKernel, NewProvider(new FakeBlaxelHandler()).IsolationLevel);
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement(BlaxelSandboxOptions.ProviderKind));
    }

    [Fact]
    public async Task ProfiledWork_RefusedNamingKind()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("bx", "blaxel")),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", BasicSpec()),
            CancellationToken.None));

        Assert.Equal("enforced-egress", ex.UnmetCapability);
        Assert.Contains("blaxel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ProfiledSpec_RefusedAtSink()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);
        var spec = BasicSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "llm" } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v0/sandboxes");
    }

    [Fact]
    public async Task CreateAsync_GraphicalSpec_Refused()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        var spec = BasicSpec() with { Flavor = SandboxProfileFlavor.Graphical };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_MissingMountSource_FailsBeforeProvisioning()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()) }],
        };

        var ex = await Assert.ThrowsAsync<SandboxMountSourceMissingException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains(spec.Mounts[0].HostPath!, ex.HostPath, StringComparison.Ordinal);
        Assert.Equal(0, provider.ActiveSandboxCount);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v0/sandboxes");
    }

    [Fact]
    public async Task CreateAsync_CredentialTmpfs_RefusedBeforeProvisioning()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/run/codeybox/creds", Tmpfs = true }],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("environment variables", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v0/sandboxes");
    }

    [Fact]
    public async Task CreateAsync_NonCredentialTmpfs_NeedsExplicitDowngrade()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);
        var spec = BasicSpec() with
        {
            Mounts = [new SandboxMount { SandboxPath = "/scratch", Tmpfs = true }],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("AllowPersistentTmpfsDowngrade", ex.Message, StringComparison.Ordinal);

        var downgraded = NewProvider(handler, TestOptions() with { AllowPersistentTmpfsDowngrade = true });
        await using var sandbox = await downgraded.CreateAsync(spec, CancellationToken.None);
        Assert.StartsWith("codeybox-", sandbox.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialMountPath_MatchesHostConvention()
    {
        Assert.Equal(SandboxConventions.CredentialsDir, BlaxelSandboxProvider.CredentialMountPath);
    }

    [Fact]
    public void ResolveCredentials_MissingApiKey_FailsClosedNamingVariable()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        var opts = TestOptions() with { ApiKey = null, ApiKeyEnvironmentVariable = "CODEYBOX_BLAXEL_TEST_MISSING_KEY" };
        Environment.SetEnvironmentVariable("CODEYBOX_BLAXEL_TEST_MISSING_KEY", null);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ResolveCredentials(opts));
        Assert.Contains("CODEYBOX_BLAXEL_TEST_MISSING_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCredentials_MissingWorkspace_FailsClosedNamingVariable()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        var opts = TestOptions() with { Workspace = null, WorkspaceEnvironmentVariable = "CODEYBOX_BLAXEL_TEST_MISSING_WS" };
        Environment.SetEnvironmentVariable("CODEYBOX_BLAXEL_TEST_MISSING_WS", null);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ResolveCredentials(opts));
        Assert.Contains("CODEYBOX_BLAXEL_TEST_MISSING_WS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCredentials_EnvironmentChain_UsedAtUseTime()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        const string keyVariable = "CODEYBOX_BLAXEL_TEST_KEY";
        const string workspaceVariable = "CODEYBOX_BLAXEL_TEST_WS";
        Environment.SetEnvironmentVariable(keyVariable, "rotated-key");
        Environment.SetEnvironmentVariable(workspaceVariable, "rotated-ws");
        try
        {
            var opts = TestOptions() with { ApiKey = null, Workspace = null, ApiKeyEnvironmentVariable = keyVariable, WorkspaceEnvironmentVariable = workspaceVariable };
            var first = provider.ResolveCredentials(opts);
            Assert.Equal("rotated-key", first.ApiKey);
            Assert.Equal("rotated-ws", first.Workspace);

            Environment.SetEnvironmentVariable(keyVariable, "rotated-again");
            Assert.Equal("rotated-again", provider.ResolveCredentials(opts).ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, null);
            Environment.SetEnvironmentVariable(workspaceVariable, null);
        }
    }

    [Fact]
    public async Task CreateAsync_Unauthorised_DefersAsInfrastructure()
    {
        var handler = new FakeBlaxelHandler { FailCreateStatus = HttpStatusCode.Unauthorized };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("blaxel", ex.Provider);
        Assert.Equal("unauthorised", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_Throttled_DefersWithLongerBackoff()
    {
        var handler = new FakeBlaxelHandler { FailCreateStatus = (HttpStatusCode)429 };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("throttled", ex.ErrorClass);
        Assert.True(ex.RecheckIn >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task CreateAsync_Unreachable_DefersAsInfrastructure()
    {
        var handler = new FakeBlaxelHandler { ThrowOnCreate = true };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("unreachable", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ExecAsync_ServiceError_IsUnavailable_NotDiffVerdict()
    {
        var handler = new FakeBlaxelHandler { FailProcessStatus = HttpStatusCode.InternalServerError };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
    }

    [Fact]
    public void FailureClassification_ServiceFailures_AreInfrastructure()
    {
        Assert.True(BlaxelFailureClassification.IsInfrastructure(
            new BlaxelApiException(HttpStatusCode.Unauthorized, "unauthorised", "no")));
        Assert.True(BlaxelFailureClassification.IsInfrastructure(new HttpRequestException("down")));
        Assert.True(BlaxelFailureClassification.IsInfrastructure(new TimeoutException()));

        var throttled = BlaxelFailureClassification.Classify((HttpStatusCode)429, "create-sandbox");
        Assert.Equal("throttled", throttled.ErrorClass);

        var unauthorised = BlaxelFailureClassification.Classify(HttpStatusCode.Unauthorized, "create-sandbox");
        Assert.Equal("unauthorised", unauthorised.ErrorClass);
    }

    [Fact]
    public async Task ConcurrentAcquisitions_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var handler = new FakeBlaxelHandler { GateCreates = true };
        var provider = NewProvider(handler);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("bx-a", "blaxel", preferenceScore: 100, capacity: 3),
                SandboxPlacementTestMembers.Member("bx-b", "blaxel", preferenceScore: 100, capacity: 5)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        Assert.Equal(8, acquirer.MaxConcurrent);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
                CancellationToken.None))
            .ToList();

        using var enteredCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (handler.EnteredCreates < 8)
        {
            await Task.Delay(10, enteredCts.Token);
        }

        Assert.Equal(8, handler.MaxConcurrentCreates);
        handler.ReleaseCreates();

        var sandboxes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(8, sandboxes.Length);
        Assert.Equal(8, acquirer.InFlight);
        Assert.Equal(8, provider.ActiveSandboxCount);

        // Two more contenders with no permits left: they must not slip past the
        // member gates. Cancelling them proves the cap held under pressure.
        using var overflowCts = new CancellationTokenSource();
        var overflow = Enumerable.Range(0, 2)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
                overflowCts.Token))
            .ToList();
        await Task.Delay(500, CancellationToken.None);
        Assert.Equal(8, handler.MaxConcurrentCreates);
        overflowCts.Cancel();
        foreach (var pending in overflow)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        foreach (var sandbox in sandboxes)
        {
            await sandbox.DisposeAsync();
        }

        Assert.Equal(0, acquirer.InFlight);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task FullMember_SpillsOverToLeastLoadedMember()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("bx-a", "blaxel", preferenceScore: 100, capacity: 1),
                SandboxPlacementTestMembers.Member("bx-b", "blaxel", preferenceScore: 100, capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None);

        // Member bx-a is full; the next acquisition must spill to bx-b instead
        // of queueing behind bx-a: it completes promptly with live load = 2.
        await using var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersToOwnedSandboxes()
    {
        var handler = new FakeBlaxelHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        var entry = Assert.Single(managed);
        Assert.Equal(sandbox.Id, entry.Name);
        Assert.True(entry.IsTrackedActive);
        Assert.False(entry.IsSuspendLifecycleOrFrozen);
    }

    [Fact]
    public async Task DisposeLeakedAsync_DeletesByName_MissingIsNonFatal()
    {
        var handler = new FakeBlaxelHandler { FailDeleteStatus = HttpStatusCode.NotFound };
        var provider = NewProvider(handler);

        await provider.DisposeLeakedAsync("codeybox-gone", CancellationToken.None);
    }

    [Fact]
    public void ShellCommand_BuildsQuotedCommand_WithNativeEnvironment()
    {
        var exec = new SandboxExec
        {
            Argv = ["sh", "-c", "echo hi"],
            ExtraEnvironment = new Dictionary<string, string> { ["GREETING"] = "a'b\"c$d" },
            EnvironmentVariablesToUnset = ["DROP"],
        };
        var merged = BlaxelShellCommand.MergeEnvironment(
            new Dictionary<string, string> { ["BASE"] = "1", ["DROP"] = "x" },
            exec,
            maxEnvironmentBytes: 1024);

        Assert.Equal("1", merged["BASE"]);
        Assert.Equal("a'b\"c$d", merged["GREETING"]);
        Assert.False(merged.ContainsKey("DROP"));

        var command = BlaxelShellCommand.Build(
            exec, "/work", maxCommandBytes: 4096, maxStdinBytes: 1024);

        Assert.Contains("mkdir -p -- '/work'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("a'b\"c$d", command, StringComparison.Ordinal);
        Assert.DoesNotContain("GREETING", command, StringComparison.Ordinal);
        Assert.Contains("'sh' '-c' 'echo hi'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellCommand_RejectsOversizedPayloads()
    {
        Assert.Throws<ArgumentException>(() => BlaxelShellCommand.Build(
            new SandboxExec { Argv = ["true"], Stdin = new string('v', 100) },
            "/work",
            maxCommandBytes: 4096,
            maxStdinBytes: 10));

        Assert.Throws<ArgumentException>(() => BlaxelShellCommand.MergeEnvironment(
            new Dictionary<string, string> { ["BIG"] = new string('v', 100) },
            new SandboxExec { Argv = ["true"] },
            maxEnvironmentBytes: 10));
    }

    [Fact]
    public void GuestPath_Validation_RejectsEscapes()
    {
        Assert.Throws<ArgumentException>(() => BlaxelGuestPath.ValidateAbsolute("relative/path"));
        Assert.Throws<ArgumentException>(() => BlaxelGuestPath.ValidateAbsolute("/work/../etc"));
        Assert.Equal("sub/file", BlaxelGuestPath.GetRelativePath("/work", "/work/sub/file"));
        Assert.Throws<ArgumentException>(() => BlaxelGuestPath.GetRelativePath("/work", "/other/file"));
    }

    [Fact]
    public void ResolveSandboxUrl_PrefersReportedEndpoint()
    {
        var provider = NewProvider(new FakeBlaxelHandler());
        _ = provider;
        var view = new BlaxelSandboxView
        {
            Metadata = new BlaxelSandboxViewMetadata { Name = "sbx", Url = "https://custom.example/" },
            Spec = new BlaxelSandboxViewSpec { Region = "us-pdx-1" },
        };

        Assert.Equal("https://custom.example/", BlaxelSandboxProvider.ResolveSandboxUrl(view, "ws"));
    }

    [Fact]
    public void ResolveSandboxUrl_FallsBackToDocumentedPattern()
    {
        var view = new BlaxelSandboxView
        {
            Metadata = new BlaxelSandboxViewMetadata { Name = "sbx" },
            Spec = new BlaxelSandboxViewSpec { Region = "us-pdx-1" },
        };

        Assert.Equal(
            "https://sbx-sbx-myws.us-pdx-1.bl.run",
            BlaxelSandboxProvider.ResolveSandboxUrl(view, "myws"));
    }

    [Fact]
    public void ValidateOptions_RejectsBadShapes()
    {
        Assert.Throws<InvalidOperationException>(() =>
            BlaxelSandboxProvider.ValidateOptions(TestOptions() with { NamePrefix = "UPPER-" }));
        Assert.Throws<InvalidOperationException>(() =>
            BlaxelSandboxProvider.ValidateOptions(TestOptions() with { NamePrefix = new string('a', 27) }));
        Assert.Throws<InvalidOperationException>(() =>
            BlaxelSandboxProvider.ValidateOptions(TestOptions() with { MemoryMb = 128 }));
        Assert.Throws<InvalidOperationException>(() =>
            BlaxelSandboxProvider.ValidateBaseUrl("http://insecure.example/", allowUnsafeHttp: false));
        BlaxelSandboxProvider.ValidateBaseUrl("http://localhost:8080/", allowUnsafeHttp: true);
    }
}
