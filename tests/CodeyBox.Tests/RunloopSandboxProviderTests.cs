using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.RunloopPlugin;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the Runloop Devboxes sandbox provider plugin:
/// the kind is constructible and selectable by placement (shared across
/// members naming it); it is classified NotEnforced regardless of what it
/// reports; declared capabilities match the implementation and placement
/// refuses work requiring anything undeclared; service-side failures are
/// infrastructure (never diff verdicts); concurrent use never exceeds member
/// capacity and live load is visible to placement; credentials resolve from
/// the environment chain, never from config files.
/// </summary>
public sealed class RunloopSandboxProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static RunloopSandboxOptions TestOptions() => new()
    {
        Token = "test-token",
        ApiBaseUrl = "https://api.runloop.ai",
        WaitForRunningTimeout = TimeSpan.FromSeconds(10),
        ExecPollInterval = TimeSpan.FromMilliseconds(10),
        ApiTimeout = TimeSpan.FromSeconds(5),
    };

    private static RunloopSandboxProvider NewProvider(FakeRunloopHandler handler, RunloopSandboxOptions? opts = null)
    {
        var options = opts ?? TestOptions();
        return new RunloopSandboxProvider(
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
    public async Task CreateAsync_ProvisionsDevbox_StagesMounts_AndDeletesOnDispose()
    {
        var handler = new FakeRunloopHandler();
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
            Assert.StartsWith("dbx_", sandbox.Id, StringComparison.Ordinal);

            var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v1/devboxes");
            Assert.Equal("Bearer", create.AuthorizationScheme);
            Assert.Equal("test-token", create.AuthorizationParameter);
            Assert.DoesNotContain("test-token", create.Path, StringComparison.Ordinal);
            using (var doc = JsonDocument.Parse(create.Body))
            {
                Assert.StartsWith("codeybox-", doc.RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);
                var metadata = doc.RootElement.GetProperty("metadata");
                Assert.Equal("true", metadata.GetProperty("codeybox-managed").GetString());
            }

            var write = Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Post && r.Path.EndsWith("/write_file_contents", StringComparison.Ordinal));
            Assert.Contains("/data/input.txt", write.Body, StringComparison.Ordinal);
            Assert.Contains("hello-mount", write.Body, StringComparison.Ordinal);

            Assert.Equal(1, provider.ActiveSandboxCount);

            await sandbox.DisposeAsync();
            Assert.Equal(0, provider.ActiveSandboxCount);

            Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Post && r.Path.EndsWith("/shutdown", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecAsync_StreamsOutputChunks_AndReturnsExitCode()
    {
        var handler = new FakeRunloopHandler();
        handler.ExecutionResponder = _ => (3, "out-data", "err-data");
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

        var started = Assert.Single(handler.Requests, r => r.Path.EndsWith("/execute_async", StringComparison.Ordinal));
        // The recorded body is JSON-encoded (quotes arrive as \u0027); decode
        // the command property before asserting on its shell contents.
        var sentCommand = JsonDocument.Parse(started.Body).RootElement.GetProperty("command").GetString();
        Assert.NotNull(sentCommand);
        Assert.Contains("GREETING=", sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", sentCommand, StringComparison.Ordinal);
        Assert.Contains("mkdir -p -- '/work'", sentCommand, StringComparison.Ordinal);
        Assert.Contains("'hi'\\''; rm -rf /'", sentCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecAsync_Cancellation_KillsExecution_AndThrowsCancelled()
    {
        var handler = new FakeRunloopHandler { BlockExecutions = true };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);

        while (handler.StartedExecutionIds.Count == 0)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        Assert.Single(handler.Requests, r => r.Path.Contains("/kill", StringComparison.Ordinal));
        handler.ReleaseExecutions();
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_OutputLimitExceeded_Kills_AndSetsFlags()
    {
        var handler = new FakeRunloopHandler();
        handler.ExecutionResponder = _ => (0, new string('x', 100), string.Empty);
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
        Assert.Single(handler.Requests, r => r.Path.Contains("/kill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuspendAsync_PreservesDevbox_AcrossDispose()
    {
        var handler = new FakeRunloopHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        await suspendable.SuspendAsync(CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Path.EndsWith("/suspend", StringComparison.Ordinal));
        await sandbox.DisposeAsync();

        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeSandboxAsync_SuspendedDevbox_ResumesToRunning()
    {
        var handler = new FakeRunloopHandler { DevboxStatus = "suspended" };
        var provider = NewProvider(handler);

        await provider.ResumeSandboxAsync("dbx_1", CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Path.EndsWith("/resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeSandboxAsync_MissingDevbox_IsNonFatal()
    {
        var handler = new FakeRunloopHandler { DevboxStatusCode = HttpStatusCode.NotFound };
        var provider = NewProvider(handler);

        await provider.ResumeSandboxAsync("dbx_gone", CancellationToken.None);
    }

    [Fact]
    public async Task CreateSnapshotAsync_ReturnsSnapshotId()
    {
        var handler = new FakeRunloopHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var runloop = Assert.IsType<RunloopSandbox>(sandbox);
        var snapshotId = await runloop.CreateSnapshotAsync("baseline", CancellationToken.None);

        Assert.Equal("snap_test", snapshotId);
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/snapshot_disk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteAndReadFile_RoundTrips()
    {
        var handler = new FakeRunloopHandler();
        handler.ReadFileBody = "file-contents";
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var runloop = Assert.IsType<RunloopSandbox>(sandbox);
        await runloop.WriteFileAsync("/work/note.txt", "file-contents", CancellationToken.None);
        Assert.Equal("file-contents", await runloop.ReadFileAsync("/work/note.txt", CancellationToken.None));
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementation()
    {
        var provider = NewProvider(new FakeRunloopHandler());

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
        var provider = NewProvider(new FakeRunloopHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("rl", "runloop", capabilities: [SandboxCapabilities.BaselineBake])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.BaselineBake], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(SandboxCapabilities.BaselineBake, ex.UnmetCapability);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task Placement_DeclaredCapability_PlacesOnRunloop()
    {
        var provider = NewProvider(new FakeRunloopHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("rl", "runloop", capabilities: [SandboxCapabilities.SuspendResume])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.SuspendResume], null, null, BasicSpec()),
            CancellationToken.None);

        Assert.StartsWith("dbx_", sandbox.Id, StringComparison.Ordinal);
        Assert.Equal(1, provider.ActiveSandboxCount);
    }

    [Fact]
    public void PluginCatalog_AcceptsKind_SharesInstance_RegardlessOfOrder()
    {
        var handler = new FakeRunloopHandler();
        var provider = NewProvider(handler);
        var catalog = new PluginSandboxProviderCatalog([("codeybox.runloop", provider)]);
        Assert.True(catalog.IsPluginKind("runloop"));
        Assert.True(catalog.TryGetProvider("RUNLOOP ", out var resolved));
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

        var first = registry.Resolve(SandboxPlacementTestMembers.Member("a", "runloop"));
        var second = registry.Resolve(SandboxPlacementTestMembers.Member("b", "RUNLOOP "));
        Assert.Same(provider, first);
        Assert.Same(first, second);
        Assert.Equal(1, builds["runloop"]);
    }

    [Fact]
    public void PluginAttribute_DeclaresCompatibleHostApi()
    {
        var attribute = typeof(RunloopSandboxProvider)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();

        Assert.Equal(RunloopSandboxOptions.PluginId, attribute.Id);
        Assert.True(
            Version.TryParse(attribute.MinHostApiVersion, out _),
            $"MinHostApiVersion '{attribute.MinHostApiVersion}' must parse so the host gate can compare it.");
    }

    [Fact]
    public void Kind_ClassifiedNotEnforced()
    {
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("runloop"));
        Assert.False(SandboxEgressPolicy.IsEnforced("runloop"));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement("runloop"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfiledWork_RefusedNamingKind()
    {
        var provider = NewProvider(new FakeRunloopHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("rl", "runloop")),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", BasicSpec()),
            CancellationToken.None));

        Assert.Equal("enforced-egress", ex.UnmetCapability);
        Assert.Contains("runloop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ProfiledSpec_RefusedAtSink()
    {
        var provider = NewProvider(new FakeRunloopHandler());
        var spec = BasicSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "llm" } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_GraphicalSpec_Refused()
    {
        var provider = NewProvider(new FakeRunloopHandler());
        var spec = BasicSpec() with { Flavor = SandboxProfileFlavor.Graphical };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_MissingMountSource_SurfacesForOrchestratorRetry()
    {
        var provider = NewProvider(new FakeRunloopHandler());
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
        var provider = NewProvider(new FakeRunloopHandler());
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
        Assert.Equal(SandboxConventions.CredentialsDir, RunloopSandboxProvider.CredentialMountPath);
    }

    [Fact]
    public void ResolveToken_MissingEnvironment_FailsClosedNamingVariable()
    {
        var provider = NewProvider(new FakeRunloopHandler());
        var opts = TestOptions() with { Token = null, TokenEnvironmentVariable = "CODEYBOX_RUNLOOP_TEST_MISSING_TOKEN" };
        Environment.SetEnvironmentVariable("CODEYBOX_RUNLOOP_TEST_MISSING_TOKEN", null);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ResolveToken(opts));
        Assert.Contains("CODEYBOX_RUNLOOP_TEST_MISSING_TOKEN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveToken_EnvironmentChain_UsedAtUseTime()
    {
        var provider = NewProvider(new FakeRunloopHandler());
        const string variable = "CODEYBOX_RUNLOOP_TEST_TOKEN";
        Environment.SetEnvironmentVariable(variable, "rotated-token");
        try
        {
            var opts = TestOptions() with { Token = null, TokenEnvironmentVariable = variable };
            Assert.Equal("rotated-token", provider.ResolveToken(opts));

            Environment.SetEnvironmentVariable(variable, "rotated-again");
            Assert.Equal("rotated-again", provider.ResolveToken(opts));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task CreateAsync_Unauthorised_DefersAsInfrastructure()
    {
        var handler = new FakeRunloopHandler { FailCreateStatus = HttpStatusCode.Unauthorized };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("runloop", ex.Provider);
        Assert.Equal("unauthorised", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_Throttled_DefersWithLongerBackoff()
    {
        var handler = new FakeRunloopHandler { FailCreateStatus = (HttpStatusCode)429 };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("throttled", ex.ErrorClass);
        Assert.True(ex.RecheckIn >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task CreateAsync_Unreachable_DefersAsInfrastructure()
    {
        var handler = new FakeRunloopHandler { ThrowOnCreate = true };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("unreachable", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ExecAsync_ServiceError_IsUnavailable_NotDiffVerdict()
    {
        var handler = new FakeRunloopHandler { FailExecStatus = HttpStatusCode.InternalServerError };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
    }

    [Fact]
    public void FailureClassification_ServiceFailures_AreInfrastructure()
    {
        Assert.True(RunloopFailureClassification.IsInfrastructure(
            new RunloopApiException(HttpStatusCode.Unauthorized, "unauthorised", "no")));
        Assert.True(RunloopFailureClassification.IsInfrastructure(new HttpRequestException("down")));
        Assert.True(RunloopFailureClassification.IsInfrastructure(new TimeoutException()));

        var throttled = RunloopFailureClassification.Classify((HttpStatusCode)429, "create-devbox");
        Assert.Equal("throttled", throttled.ErrorClass);

        var unauthorised = RunloopFailureClassification.Classify(HttpStatusCode.Unauthorized, "create-devbox");
        Assert.Equal("unauthorised", unauthorised.ErrorClass);
    }

    [Fact]
    public async Task ConcurrentAcquisitions_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var handler = new FakeRunloopHandler { GateCreates = true };
        var provider = NewProvider(handler);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("rl-a", "runloop", preferenceScore: 100, capacity: 3),
                SandboxPlacementTestMembers.Member("rl-b", "runloop", preferenceScore: 100, capacity: 5)),
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
        var provider = NewProvider(new FakeRunloopHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("rl-a", "runloop", preferenceScore: 100, capacity: 1),
                SandboxPlacementTestMembers.Member("rl-b", "runloop", preferenceScore: 100, capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None);

        // Member rl-a is full; the next acquisition must spill to rl-b instead
        // of queueing behind rl-a: it completes promptly with live load = 2.
        await using var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersToOwnedDevboxes()
    {
        var handler = new FakeRunloopHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        var entry = Assert.Single(managed);
        Assert.Equal(sandbox.Id, entry.Name);
        Assert.True(entry.IsTrackedActive);
        Assert.False(entry.IsSuspendLifecycleOrFrozen);
    }

    [Fact]
    public void ShellCommand_BuildsQuotedCommand_WithBase64Environment()
    {
        var command = RunloopShellCommand.Build(
            new Dictionary<string, string> { ["BASE"] = "1", ["DROP"] = "x" },
            new SandboxExec
            {
                Argv = ["sh", "-c", "echo hi"],
                ExtraEnvironment = new Dictionary<string, string> { ["GREETING"] = "a'b\"c$d" },
                EnvironmentVariablesToUnset = ["DROP"],
            },
            "/work",
            maxEnvironmentBytes: 1024,
            maxCommandBytes: 4096,
            maxStdinBytes: 1024);

        Assert.Contains("mkdir -p -- '/work'", command, StringComparison.Ordinal);
        Assert.Contains("unset -- DROP", command, StringComparison.Ordinal);
        Assert.DoesNotContain("a'b\"c$d", command, StringComparison.Ordinal);
        Assert.Contains("'sh' '-c' 'echo hi'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellCommand_RejectsOversizedEnvironment()
    {
        Assert.Throws<ArgumentException>(() => RunloopShellCommand.Build(
            new Dictionary<string, string> { ["BIG"] = new string('v', 100) },
            new SandboxExec { Argv = ["true"] },
            "/work",
            maxEnvironmentBytes: 10,
            maxCommandBytes: 4096,
            maxStdinBytes: 1024));
    }

    [Fact]
    public void GuestPath_Validation_RejectsEscapes()
    {
        Assert.Throws<ArgumentException>(() => RunloopGuestPath.ValidateAbsolute("relative/path"));
        Assert.Throws<ArgumentException>(() => RunloopGuestPath.ValidateAbsolute("/work/../etc"));
        Assert.Equal("sub/file", RunloopGuestPath.GetRelativePath("/work", "/work/sub/file"));
        Assert.Throws<ArgumentException>(() => RunloopGuestPath.GetRelativePath("/work", "/other/file"));
    }
}
