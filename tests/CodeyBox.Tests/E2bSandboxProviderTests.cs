using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.E2bSandboxPlugin;
using CodeyBox.Orchestrator;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the E2B sandbox provider plugin: the kind is
/// constructible and selectable by placement (shared across members naming
/// it); it is classified NotEnforced regardless of what it reports; declared
/// capabilities match the implementation and placement refuses work requiring
/// anything undeclared; service-side failures are infrastructure (never diff
/// verdicts); concurrent use never exceeds member capacity and live load is
/// visible to placement; credentials resolve from the environment chain,
/// never from config files; preview URLs are declared but never enabled by
/// default.
/// </summary>
public sealed class E2bSandboxProviderTests
{
    private static E2bSandboxOptions TestOptions() => new()
    {
        ApiKey = "test-key",
        ApiBaseUrl = "https://api.e2b.dev",
        WaitForRunningTimeout = TimeSpan.FromSeconds(10),
        StatusPollInterval = TimeSpan.FromMilliseconds(10),
        ApiTimeout = TimeSpan.FromSeconds(5),
    };

    private static E2bSandboxProvider NewProvider(FakeE2bHandler handler, E2bSandboxOptions? opts = null)
    {
        var options = opts ?? TestOptions();
        return new E2bSandboxProvider(
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
        var handler = new FakeE2bHandler();
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
            Assert.StartsWith("sb_test_", sandbox.Id, StringComparison.Ordinal);

            var create = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v2/sandboxes");
            Assert.Equal("api.e2b.dev", create.Host);
            Assert.Equal("test-key", create.ApiKey);
            Assert.Null(create.AccessToken);
            using (var doc = JsonDocument.Parse(create.Body))
            {
                Assert.Equal("base", doc.RootElement.GetProperty("templateID").GetString());
                var metadata = doc.RootElement.GetProperty("metadata");
                Assert.Equal("true", metadata.GetProperty("codeybox-managed").GetString());
                Assert.Equal("e2b", metadata.GetProperty("codeybox-provider").GetString());
            }

            Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get && r.Path == $"/sandboxes/{sandbox.Id}");
            var health = Assert.Single(handler.Requests, r => r.Path == "/health");
            Assert.Equal($"49983-{sandbox.Id}.e2b.app", health.Host);
            Assert.Equal($"envd_token_{sandbox.Id}", health.AccessToken);
            Assert.Null(health.ApiKey);

            var staged = Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Post && r.Path == "/files");
            Assert.Equal($"49983-{sandbox.Id}.e2b.app", staged.Host);
            Assert.Contains("/data/input.txt", Uri.UnescapeDataString(staged.FullPath), StringComparison.Ordinal);
            Assert.Contains(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("hello-mount")), staged.Body, StringComparison.Ordinal);

            Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/timeout", StringComparison.Ordinal));
            Assert.Equal(1, provider.ActiveSandboxCount);

            await sandbox.DisposeAsync();
            Assert.Equal(0, provider.ActiveSandboxCount);

            Assert.Single(
                handler.Requests,
                r => r.Method == HttpMethod.Delete && r.Path == $"/sandboxes/{sandbox.Id}");
        }
        finally
        {
            Directory.Delete(hostDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecAsync_ReturnsExitCode_AndInvokesChunkCallbacks()
    {
        var handler = new FakeE2bHandler();
        handler.CommandResponder = _ => (3, "out-data", "err-data");
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

        var command = Assert.Single(handler.Requests, r => r.Path == "/commands");
        Assert.Equal($"envd_token_{sandbox.Id}", command.AccessToken);
        var sentCommand = JsonDocument.Parse(command.Body).RootElement.GetProperty("command").GetString();
        Assert.NotNull(sentCommand);
        Assert.Contains("GREETING=", sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", sentCommand, StringComparison.Ordinal);
        Assert.Contains("mkdir -p -- '/work'", sentCommand, StringComparison.Ordinal);
        Assert.Contains("'hi'\\''; rm -rf /'", sentCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecAsync_Cancellation_AbortsWait_AndThrowsCancelled()
    {
        var handler = new FakeE2bHandler { BlockCommands = true };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, cts.Token);

        using var enteredCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (handler.EnteredCommands == 0)
        {
            await Task.Delay(10, enteredCts.Token);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        handler.ReleaseCommands();
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task KillActiveExecsAsync_CancelsTrackedWaits()
    {
        var handler = new FakeE2bHandler { BlockCommands = true };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var execTask = sandbox.ExecAsync(new SandboxExec { Argv = ["sleep", "60"] }, CancellationToken.None);

        using var enteredCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (handler.EnteredCommands == 0)
        {
            await Task.Delay(10, enteredCts.Token);
        }

        await sandbox.KillActiveExecsAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask);
        handler.ReleaseCommands();
        await sandbox.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_OutputLimitExceeded_Truncates_AndSetsFlags()
    {
        var handler = new FakeE2bHandler();
        handler.CommandResponder = _ => (0, new string('x', 100), string.Empty);
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
        Assert.Equal(10, result.Stdout.Length);
    }

    [Fact]
    public async Task SuspendAsync_PreservesSandbox_AcrossDispose()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var suspendable = Assert.IsAssignableFrom<ISuspendableSandbox>(sandbox);
        await suspendable.SuspendAsync(CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Path.EndsWith("/pause", StringComparison.Ordinal));
        await sandbox.DisposeAsync();

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ResumeSandboxAsync_PausedSandbox_ConnectsToRunning_AndRefreshesToken()
    {
        var handler = new FakeE2bHandler();
        handler.CommandResponder = _ => (0, "after-resume", string.Empty);
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        handler.SetState(sandbox.Id, "paused");

        await provider.ResumeSandboxAsync(sandbox.Id, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Path.EndsWith("/connect", StringComparison.Ordinal));

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None);
        Assert.Equal("after-resume", result.Stdout);
        var command = Assert.Single(handler.Requests, r => r.Path == "/commands");
        Assert.Equal($"envd_token_{sandbox.Id}_resumed", command.AccessToken);
    }

    [Fact]
    public async Task ResumeSandboxAsync_MissingSandbox_IsNonFatal()
    {
        var handler = new FakeE2bHandler { SandboxStatusCode = HttpStatusCode.NotFound };
        var provider = NewProvider(handler);

        await provider.ResumeSandboxAsync("sb_gone", CancellationToken.None);
    }

    [Fact]
    public async Task CreateSnapshotAsync_ReturnsSnapshotId()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var e2b = Assert.IsType<E2bSandbox>(sandbox);
        var snapshotId = await e2b.CreateSnapshotAsync("baseline", CancellationToken.None);

        Assert.Equal("snap_test", snapshotId);
        Assert.Single(handler.Requests, r => r.Path.EndsWith("/snapshots", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteAndReadFile_RoundTrips()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var e2b = Assert.IsType<E2bSandbox>(sandbox);
        await e2b.WriteFileAsync("/work/note.txt", "file-contents", CancellationToken.None);
        Assert.Equal("file-contents", await e2b.ReadFileAsync("/work/note.txt", CancellationToken.None));

        var binary = new byte[] { 0x00, 0xFF, 0x01, 0xFE };
        await e2b.WriteFileBytesAsync("/work/blob.bin", binary, CancellationToken.None);
        Assert.Equal(binary, await e2b.ReadFileBytesAsync("/work/blob.bin", CancellationToken.None));
    }

    [Fact]
    public void DeclaredCapabilities_MatchImplementation()
    {
        var provider = NewProvider(new FakeE2bHandler());

        Assert.Equal(
            [SandboxCapabilities.SuspendResume, SandboxCapabilities.Teardown, SandboxCapabilities.PortPublishing],
            provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.BaselineBake, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.DiskGuard, provider.DeclaredCapabilities);
        Assert.DoesNotContain(SandboxCapabilities.CacheSeeding, provider.DeclaredCapabilities);
    }

    [Fact]
    public async Task Placement_RequiresPortPublishing_RoutesToE2b()
    {
        var provider = NewProvider(new FakeE2bHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("e2b", "e2b", capabilities: [SandboxCapabilities.PortPublishing])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var sandbox = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.PortPublishing], null, null, BasicSpec()),
            CancellationToken.None);

        Assert.StartsWith("sb_test_", sandbox.Id, StringComparison.Ordinal);
        Assert.Equal(1, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task Placement_UndeclaredCapability_RefusedNamingCapability()
    {
        var provider = NewProvider(new FakeE2bHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("e2b", "e2b", capabilities: [SandboxCapabilities.BaselineBake])),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(
                WorkItemId.New(), "work", [SandboxCapabilities.BaselineBake], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(SandboxCapabilities.BaselineBake, ex.UnmetCapability);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public void PluginCatalog_AcceptsKind_SharesInstance_RegardlessOfOrder()
    {
        var handler = new FakeE2bHandler();
        var provider = NewProvider(handler);
        var catalog = new PluginSandboxProviderCatalog([("codeybox.e2b-sandbox", provider)]);
        Assert.True(catalog.IsPluginKind("e2b"));
        Assert.True(catalog.TryGetProvider("E2B ", out var resolved));
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

        var first = registry.Resolve(SandboxPlacementTestMembers.Member("a", "e2b"));
        var second = registry.Resolve(SandboxPlacementTestMembers.Member("b", "E2B "));
        Assert.Same(provider, first);
        Assert.Same(first, second);
        Assert.Equal(1, builds["e2b"]);
    }

    [Fact]
    public void PluginAttribute_DeclaresCompatibleHostApi()
    {
        var attribute = typeof(E2bSandboxProvider)
            .GetCustomAttributes(typeof(CodeyBoxPluginAttribute), inherit: false)
            .OfType<CodeyBoxPluginAttribute>()
            .Single();

        Assert.Equal(E2bSandboxOptions.PluginId, attribute.Id);
        Assert.True(
            Version.TryParse(attribute.MinHostApiVersion, out _),
            $"MinHostApiVersion '{attribute.MinHostApiVersion}' must parse so the host gate can compare it.");
    }

    [Fact]
    public void Kind_ClassifiedNotEnforced()
    {
        Assert.Equal(EgressEnforcementLocation.NotEnforced, HostPlatformSupport.GetEgressEnforcement("e2b"));
        Assert.False(SandboxEgressPolicy.IsEnforced("e2b"));
        Assert.Contains("NOT enforced", SandboxEgressPolicy.DescribeEgressEnforcement("e2b"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfiledWork_RefusedNamingKind()
    {
        var provider = NewProvider(new FakeE2bHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("e2b", "e2b")),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var ex = await Assert.ThrowsAsync<SandboxPlacementUnplaceableException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, "llm", BasicSpec()),
            CancellationToken.None));

        Assert.Equal("enforced-egress", ex.UnmetCapability);
        Assert.Contains("e2b", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ProfiledSpec_RefusedAtSink()
    {
        var provider = NewProvider(new FakeE2bHandler());
        var spec = BasicSpec() with { Network = new SandboxNetworkPolicy { ProfileName = "llm" } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(spec, CancellationToken.None));
        Assert.Contains("NotEnforced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_GraphicalSpec_Refused()
    {
        var provider = NewProvider(new FakeE2bHandler());
        var spec = BasicSpec() with { Flavor = SandboxProfileFlavor.Graphical };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_MissingMountSource_SurfacesForOrchestratorRetry()
    {
        var provider = NewProvider(new FakeE2bHandler());
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
        var provider = NewProvider(new FakeE2bHandler());
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
        Assert.Equal(SandboxConventions.CredentialsDir, E2bSandboxProvider.CredentialMountPath);
    }

    [Fact]
    public void Sandbox_RejectsFileBackedAgentCredentials()
    {
        var reason = ((IRejectsFileBackedAgentCredentials)new E2bSandbox(
            "sb_probe",
            "token",
            new E2bApiClient(new HttpClient(new FakeE2bHandler())),
            TestOptions,
            () => "test-key",
            BasicSpec(),
            TimeProvider.System,
            NullLogger.Instance,
            _ => { })).FileBackedAgentCredentialsUnsupportedReason;
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ResolveApiKey_MissingEnvironment_FailsClosedNamingVariable()
    {
        var provider = NewProvider(new FakeE2bHandler());
        var opts = TestOptions() with { ApiKey = null, ApiKeyEnvVar = "CODEYBOX_E2B_TEST_MISSING_KEY" };
        Environment.SetEnvironmentVariable("CODEYBOX_E2B_TEST_MISSING_KEY", null);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ResolveApiKey(opts));
        Assert.Contains("CODEYBOX_E2B_TEST_MISSING_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveApiKey_EnvironmentChain_UsedAtUseTime()
    {
        var provider = NewProvider(new FakeE2bHandler());
        const string variable = "CODEYBOX_E2B_TEST_KEY";
        Environment.SetEnvironmentVariable(variable, "rotated-key");
        try
        {
            var opts = TestOptions() with { ApiKey = null, ApiKeyEnvVar = variable };
            Assert.Equal("rotated-key", provider.ResolveApiKey(opts));

            Environment.SetEnvironmentVariable(variable, "rotated-again");
            Assert.Equal("rotated-again", provider.ResolveApiKey(opts));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task CreateAsync_Unauthorised_DefersAsInfrastructure()
    {
        var handler = new FakeE2bHandler { FailCreateStatus = HttpStatusCode.Unauthorized };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("e2b", ex.Provider);
        Assert.Equal("unauthorised", ex.ErrorClass);
        Assert.True(SandboxDeferralGuard.IsDeferral(ex));
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task CreateAsync_Throttled_DefersWithLongerBackoff()
    {
        var handler = new FakeE2bHandler { FailCreateStatus = (HttpStatusCode)429 };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("throttled", ex.ErrorClass);
        Assert.True(ex.RecheckIn >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task CreateAsync_Unreachable_DefersAsInfrastructure()
    {
        var handler = new FakeE2bHandler { ThrowOnCreate = true };
        var provider = NewProvider(handler);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => provider.CreateAsync(BasicSpec(), CancellationToken.None));

        Assert.Equal("unreachable", ex.ErrorClass);
        Assert.Equal(0, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ExecAsync_ServiceError_IsUnavailable_NotDiffVerdict()
    {
        var handler = new FakeE2bHandler { FailCommandStatus = HttpStatusCode.InternalServerError };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec { Argv = ["true"] }, CancellationToken.None));
    }

    [Fact]
    public void FailureClassification_ServiceFailures_AreInfrastructure()
    {
        Assert.True(E2bFailureClassification.IsInfrastructure(
            new E2bApiException(HttpStatusCode.Unauthorized, "unauthorised", "no")));
        Assert.True(E2bFailureClassification.IsInfrastructure(new HttpRequestException("down")));
        Assert.True(E2bFailureClassification.IsInfrastructure(new TimeoutException()));

        var throttled = E2bFailureClassification.Classify((HttpStatusCode)429, "create-sandbox");
        Assert.Equal("throttled", throttled.ErrorClass);

        var unauthorised = E2bFailureClassification.Classify(HttpStatusCode.Unauthorized, "create-sandbox");
        Assert.Equal("unauthorised", unauthorised.ErrorClass);
    }

    [Fact]
    public async Task ConcurrentAcquisitions_NeverExceedMemberCapacity_LiveLoadReachesPlacement()
    {
        var handler = new FakeE2bHandler { GateCreates = true };
        var provider = NewProvider(handler);
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("e2b-a", "e2b", preferenceScore: 100, capacity: 3),
                SandboxPlacementTestMembers.Member("e2b-b", "e2b", preferenceScore: 100, capacity: 5)),
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
        var provider = NewProvider(new FakeE2bHandler());
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("e2b-a", "e2b", preferenceScore: 100, capacity: 1),
                SandboxPlacementTestMembers.Member("e2b-b", "e2b", preferenceScore: 100, capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        await using var first = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None);

        // Member e2b-a is full; the next acquisition must spill to e2b-b instead
        // of queueing behind e2b-a: it completes promptly with live load = 2.
        await using var second = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, BasicSpec()),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, acquirer.InFlight);
        Assert.Equal(2, provider.ActiveSandboxCount);
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersToOwnedSandboxes()
    {
        var handler = new FakeE2bHandler();
        handler.AddUnmanaged("sb_foreign");
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        var entry = Assert.Single(managed);
        Assert.Equal(sandbox.Id, entry.Name);
        Assert.True(entry.IsTrackedActive);
        Assert.False(entry.IsSuspendLifecycleOrFrozen);

        handler.SetState(sandbox.Id, "paused");
        var paused = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.True(Assert.Single(paused).IsSuspendLifecycleOrFrozen);
    }

    [Fact]
    public void ShellCommand_BuildsQuotedCommand_WithBase64Environment()
    {
        var command = E2bShellCommand.Build(
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
    public async Task ExecAsync_SecretEnvironment_StagesFile_NeverInlinesValues()
    {
        const string secret = "s3cr3t-v4lue";
        var secretBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        var handler = new FakeE2bHandler();
        handler.CommandResponder = command =>
        {
            Assert.DoesNotContain(secret, command, StringComparison.Ordinal);
            Assert.DoesNotContain(secretBase64, command, StringComparison.Ordinal);
            return (0, "ok", string.Empty);
        };
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

        var staged = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/files");
        var stagedPath = Uri.UnescapeDataString(staged.FullPath.Split("?path=", StringSplitOptions.None)[1]);
        Assert.StartsWith("/tmp/.codeybox-exec-env/env-", stagedPath, StringComparison.Ordinal);
        // The staged body is base64(file content); decode once and assert on
        // the env script: values travel base64-encoded, never as bare literals.
        var stagedContent = JsonDocument.Parse(staged.Body).RootElement.GetProperty("content").GetString();
        Assert.NotNull(stagedContent);
        var envScript = Encoding.UTF8.GetString(Convert.FromBase64String(stagedContent));
        Assert.Contains(secretBase64, envScript, StringComparison.Ordinal);
        Assert.Contains("CODEYBOX_TEST_SECRET", envScript, StringComparison.Ordinal);
        Assert.Contains("unset -- STALE_VAR", envScript, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, envScript.Replace(secretBase64, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var sent = Assert.Single(handler.Requests, r => r.Path == "/commands");
        var sentCommand = JsonDocument.Parse(sent.Body).RootElement.GetProperty("command").GetString();
        Assert.NotNull(sentCommand);
        Assert.Contains(". '/tmp/.codeybox-exec-env/env-", sentCommand, StringComparison.Ordinal);
        Assert.Contains("rm -f -- '/tmp/.codeybox-exec-env/env-", sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("export CODEYBOX_TEST_SECRET=", sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBase64, sentCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecAsync_SecretEnvironment_StagingFailure_NeverFallsBackToInline()
    {
        var handler = new FakeE2bHandler { FailWriteStatus = HttpStatusCode.TooManyRequests };
        var provider = NewProvider(handler);

        await using var sandbox = await provider.CreateAsync(BasicSpec(), CancellationToken.None);
        await Assert.ThrowsAsync<SandboxExecutionUnavailableException>(
            () => sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["true"],
                ExtraEnvironment = new Dictionary<string, string> { ["CODEYBOX_TEST_SECRET"] = "s3cr3t-v4lue" },
                EnvironmentContainsSecrets = true,
            }, CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, r => r.Path == "/commands");
    }

    [Fact]
    public void ShellCommand_RejectsOversizedEnvironment()
    {
        Assert.Throws<ArgumentException>(() => E2bShellCommand.Build(
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
        Assert.Throws<ArgumentException>(() => E2bGuestPath.ValidateAbsolute("relative/path"));
        Assert.Throws<ArgumentException>(() => E2bGuestPath.ValidateAbsolute("/work/../etc"));
        Assert.Equal("sub/file", E2bGuestPath.GetRelativePath("/work", "/work/sub/file"));
        Assert.Throws<ArgumentException>(() => E2bGuestPath.GetRelativePath("/work", "/other/file"));
    }
}
