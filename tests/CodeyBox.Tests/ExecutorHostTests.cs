using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ExecutorClock = Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the remote executor host item: outbound-only registration,
/// registry appearance, heartbeat liveness through the existing dead-worker
/// path, retain-for-resume disconnect behaviour, and never-selected placement
/// for zero-capacity / cordoned hosts.
/// </summary>
public sealed class ExecutorHostTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── options ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExecutorOptions_Validate_AcceptsWellFormed()
    {
        var options = ValidOptions();
        options.Validate();
        var registration = options.ToRegistration();
        Assert.Equal("exec-1", registration.HostId);
        Assert.Equal(2, registration.MaxConcurrentSandboxes);
        Assert.Equal(["restricted"], registration.AllowedNetworkProfiles);
        Assert.Equal(["claude"], registration.DeclaredCredentials);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecutorOptions_Validate_RejectsMissingHostId(string hostId)
    {
        var options = ValidOptions();
        options.HostId = hostId;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/x")]
    public void ExecutorOptions_Validate_RejectsBadOrchestratorUrl(string url)
    {
        var options = ValidOptions();
        options.OrchestratorBaseUrl = url;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ExecutorOptions_Validate_RejectsNegativeCapacity()
    {
        var options = ValidOptions();
        options.MaxConcurrentSandboxes = -1;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ExecutorOptions_Validate_RejectsUnknownProvider()
    {
        var options = ValidOptions();
        options.LocalSandboxProvider = "multipass";
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // ── tracker: disconnect policy ──────────────────────────────────────────

    [Fact]
    public void Tracker_DuplicatePhaseId_ThrowsInsteadOfLeakingHandle()
    {
        var first = new FakeSandbox("vm-1");
        var second = new FakeSandbox("vm-2");
        var tracker = new ExecutorSandboxTracker();
        tracker.Track("phase-a", "vm-1", first);
        Assert.Throws<InvalidOperationException>(() => tracker.Track("phase-a", "vm-2", second));
        Assert.Equal(1, tracker.TrackedCount);
    }

    [Fact]
    public void Tracker_ConnectionLoss_RetainsWithoutDisposing()
    {
        var sandbox = new FakeSandbox("vm-1");
        var tracker = new ExecutorSandboxTracker();
        tracker.Track("phase-a", "vm-1", sandbox);

        var retained = tracker.MarkConnectionLost();

        Assert.True(tracker.IsConnectionLost);
        Assert.Equal(["phase-a"], retained);
        Assert.False(sandbox.Disposed);
        Assert.Equal(1, tracker.TrackedCount);
    }

    [Fact]
    public async Task Tracker_Reconnect_ReclaimsUntrackedAndRetainsTracked()
    {
        await using var live = new FakeSandbox("vm-live");
        var tracker = new ExecutorSandboxTracker();
        tracker.Track("phase-a", "vm-live", live);
        tracker.MarkConnectionLost();

        var lifecycle = new FakeSandboxProvider
        {
            Inventory =
            [
                new ManagedSandboxInfo("vm-live", null, null, false),
                new ManagedSandboxInfo("vm-orphan", null, null, false),
            ],
        };

        var outcome = await tracker.ReconcileOnReconnectAsync(lifecycle);

        Assert.False(tracker.IsConnectionLost);
        Assert.Equal(1, outcome.RetainedCount);
        Assert.Equal(1, outcome.ReclaimedCount);
        Assert.True(outcome.InventoryComplete);
        Assert.Equal(["vm-orphan"], lifecycle.DisposedLeakedNames);
        Assert.False(live.Disposed);
    }

    [Fact]
    public async Task Tracker_ReconnectWithIncompleteInventory_ReclaimsNothing()
    {
        await using var live = new FakeSandbox("vm-live");
        var tracker = new ExecutorSandboxTracker();
        tracker.Track("phase-a", "vm-live", live);

        var lifecycle = new FakeSandboxProvider
        {
            Inventory =
            [
                new ManagedSandboxInfo("vm-orphan", null, null, false),
            ],
            InventoryComplete = false,
        };

        var outcome = await tracker.ReconcileOnReconnectAsync(lifecycle);

        Assert.False(outcome.InventoryComplete);
        Assert.Equal(0, outcome.ReclaimedCount);
        Assert.Empty(lifecycle.DisposedLeakedNames);
    }

    [Fact]
    public async Task Tracker_Dispose_TearsDownTracked()
    {
        await using var disposeSandbox = new FakeSandbox("vm-1");
        var tracker = new ExecutorSandboxTracker();
        tracker.Track("phase-a", "vm-1", disposeSandbox);
        await tracker.DisposeAsync();
        Assert.True(disposeSandbox.Disposed);
    }

    // ── client: outbound register + heartbeat ───────────────────────────────

    [Fact]
    public async Task Client_Register_PostsOutboundAndReturnsWorkerId()
    {
        var requests = new List<(string Method, string Path)>();
        var client = MakeClient(
            ValidOptions(),
            (req, _) =>
            {
                requests.Add((req.Method.Method, req.RequestUri!.AbsolutePath));
                return Task.FromResult(JsonResponse(new { workerId = "executor:exec-1", hostId = "exec-1" }));
            },
            out _);

        var workerId = await client.RegisterAsync();

        Assert.Equal("executor:exec-1", workerId);
        var (method, path) = Assert.Single(requests);
        Assert.Equal("POST", method);
        Assert.Equal("/executors/register", path);
    }

    [Fact]
    public async Task Client_Register_SendsBearerAuthAndCapacity()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var client = MakeClient(
            ValidOptions(),
            async (req, ct) =>
            {
                seen = req;
                body = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(new { workerId = "executor:exec-1" });
            },
            out _);

        await client.RegisterAsync();

        Assert.NotNull(seen);
        Assert.Equal("Bearer", seen!.Headers.Authorization?.Scheme);
        Assert.False(string.IsNullOrEmpty(seen.Headers.Authorization?.Parameter));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("exec-1", doc.RootElement.GetProperty("hostId").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("maxConcurrentSandboxes").GetInt32());
        Assert.Equal("claude", doc.RootElement.GetProperty("declaredCredentials").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Client_Register_WithoutApiKey_FailsFast()
    {
        var options = ValidOptions();
        options.ApiKeyEnvVar = "CODEYBOX_TEST_MISSING_KEY_" + Guid.NewGuid().ToString("N");
        var client = MakeClient(options, (_, _) => Task.FromResult(JsonResponse(new { })), out _);
        await Assert.ThrowsAsync<ExecutorTransportException>(() => client.RegisterAsync());
    }

    [Fact]
    public async Task Client_Register_OversizedResponse_IsRejectedBeforeBuffering()
    {
        var client = MakeClient(
            ValidOptions(),
            (_, _) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent('{' + new string('x', ExecutorClient.MaxResponseBytes + 1) + '}'),
                };
                return Task.FromResult(resp);
            },
            out _);
        await Assert.ThrowsAsync<ExecutorTransportException>(() => client.RegisterAsync());
    }

    [Fact]
    public async Task Client_Heartbeat_PostsToHostScopedRoute()
    {
        var paths = new List<string>();
        var client = MakeClient(
            ValidOptions(),
            (req, _) =>
            {
                paths.Add(req.RequestUri!.AbsolutePath);
                return Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal)
                    ? JsonResponse(new { workerId = "executor:exec-1" })
                    : JsonResponse(new { workerId = "executor:exec-1", hostId = "exec-1" }));
            },
            out _);

        await client.RegisterAsync();
        await client.HeartbeatAsync(null);

        Assert.Contains("/executors/exec-1/heartbeat", paths);
    }

    [Fact]
    public async Task Client_Heartbeat_ServerError_SurfacesTypedError()
    {
        var client = MakeClient(
            ValidOptions(),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            out _);
        await Assert.ThrowsAsync<ExecutorTransportException>(() => client.HeartbeatAsync(null));
    }

    [Fact]
    public async Task Client_RunAsync_HeartbeatsOnInterval_AndMarksLossThenReconciles()
    {
        var clock = new ExecutorClock(DateTimeOffset.UtcNow);
        var heartbeatCalls = 0;
        var failHeartbeats = true;
        var tracker = new ExecutorSandboxTracker();
        var lifecycle = new FakeSandboxProvider
        {
            Inventory = [new ManagedSandboxInfo("vm-orphan", null, null, false)],
        };
        var client = MakeClient(
            ValidOptions(),
            (req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
                {
                    heartbeatCalls++;
                    if (failHeartbeats)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
                }
                return Task.FromResult(JsonResponse(new { workerId = "executor:exec-1" }));
            },
            out _,
            tracker,
            lifecycle,
            clock);

        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        clock.Advance(TimeSpan.FromSeconds(16));
        await Task.Delay(200);
        Assert.True(tracker.IsConnectionLost);

        failHeartbeats = false;
        clock.Advance(TimeSpan.FromSeconds(16));
        await Task.Delay(200);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(heartbeatCalls >= 2);
        Assert.False(tracker.IsConnectionLost);
        Assert.Equal(["vm-orphan"], lifecycle.DisposedLeakedNames);
    }

    // ── client: local provisioning + phase seam ─────────────────────────────

    [Fact]
    public async Task Client_ProvisionsSandboxes_ThroughInjectedProvider()
    {
        var provider = new FakeSandboxProvider();
        var client = MakeClient(ValidOptions(), (_, _) => Task.FromResult(JsonResponse(new { })), out _, provider: provider);

        await using var sandbox = await client.ProvisionSandboxAsync(new SandboxSpec { ImageReference = "ignored" });

        Assert.Same(provider, client.SandboxProvider);
        Assert.Single(provider.CreatedSpecs);
        Assert.Equal("fake-sandbox", sandbox.Id);
    }

    [Fact]
    public async Task Client_RunPhase_WithoutRunner_FailsFast()
    {
        var client = MakeClient(ValidOptions(), (_, _) => Task.FromResult(JsonResponse(new { })), out _);
        Assert.False(client.HasPhaseRunner);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RunPhaseAsync(MakeItem(), CancellationToken.None));
    }

    [Fact]
    public async Task Client_RunPhase_WithRunner_DelegatesThroughSeam()
    {
        var runner = new FakePhaseRunner();
        var client = MakeClient(
            ValidOptions(), (_, _) => Task.FromResult(JsonResponse(new { })), out _,
            phaseRunner: runner);

        var item = MakeItem();
        await client.RunPhaseAsync(item, CancellationToken.None);

        Assert.True(client.HasPhaseRunner);
        Assert.Same(item, Assert.Single(runner.Ran));
    }

    // ── server: registration appears in the worker registry ─────────────────

    [Fact]
    public async Task Server_Register_AppearsInWorkerRegistry()
    {
        using var factory = new ExecutorApiFactory();
        using var api = factory.CreateClient();
        using var executorHttp = factory.CreateClient();
        var client = MakeClientForServer(ValidOptions(), executorHttp);

        var workerId = await client.RegisterAsync();
        await client.HeartbeatAsync(null);

        var workers = await api.GetFromJsonAsync<JsonElement>("/workers");
        var row = workers.EnumerateArray()
            .Single(w => w.GetProperty("workerId").GetString() == workerId);
        Assert.Equal("exec-1", row.GetProperty("hostName").GetString());
        Assert.Equal("exec-1", row.GetProperty("executorHostId").GetString());
        Assert.Equal(2, row.GetProperty("maxConcurrentSandboxes").GetInt32());
        Assert.Equal("claude", row.GetProperty("executorCredentials").EnumerateArray().Single().GetString());
        Assert.Equal("restricted", row.GetProperty("executorNetworkProfiles").EnumerateArray().Single().GetString());
        Assert.False(row.GetProperty("cordoned").GetBoolean());
        Assert.True(row.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Server_Register_ZeroCapacityAndCordoned_StillRegisters()
    {
        using var factory = new ExecutorApiFactory();
        using var api = factory.CreateClient();

        foreach (var (host, capacity, cordoned) in new[] { ("zero-cap", (int?)0, false), ("draining", (int?)4, true) })
        {
            var resp = await api.PostAsJsonAsync("/executors/register", new
            {
                hostId = host,
                maxConcurrentSandboxes = capacity,
                allowedNetworkProfiles = Array.Empty<string>(),
                declaredCredentials = Array.Empty<string>(),
                cordoned,
            });
            resp.EnsureSuccessStatusCode();
        }

        var workers = await api.GetFromJsonAsync<JsonElement>("/workers");
        var rows = workers.EnumerateArray()
            .Where(w => (w.GetProperty("workerId").GetString() ?? "").StartsWith("executor:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            var hostId = row.GetProperty("executorHostId").GetString();
            var ineligible = hostId == "zero-cap"
                ? !ExecutorEligibility.IsEligibleForPlacement(ToEligibility(row), 0)
                : !ExecutorEligibility.IsEligibleForPlacement(ToEligibility(row), 0);
            Assert.True(ineligible);
        }
    }

    [Fact]
    public async Task Server_Register_RejectsInvalidPayloads()
    {
        using var factory = new ExecutorApiFactory();
        using var api = factory.CreateClient();

        var missingHost = await api.PostAsJsonAsync("/executors/register", new { hostId = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, missingHost.StatusCode);

        var negativeCap = await api.PostAsJsonAsync("/executors/register", new { hostId = "h", maxConcurrentSandboxes = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negativeCap.StatusCode);

        var tooMany = await api.PostAsJsonAsync("/executors/register", new
        {
            hostId = "h",
            declaredCredentials = Enumerable.Range(0, ExecutorRegistration.MaxDeclaredEntries + 1).Select(i => "c" + i).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task Server_Heartbeat_UnknownHost_ReturnsNotFound()
    {
        using var factory = new ExecutorApiFactory();
        using var api = factory.CreateClient();
        var resp = await api.PostAsJsonAsync("/executors/ghost/heartbeat", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── registry: persistence + migration ───────────────────────────────────

    [Fact]
    public async Task Registry_RoundTripsExecutorFields()
    {
        var path = TempDbPath();
        using var registry = new SqliteWorkerRegistry(path);
        try
        {
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = "executor:exec-1",
                HostName = "exec-1",
                ProcessId = 42,
                StartedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                ExecutorHostId = "exec-1",
                MaxConcurrentSandboxes = 3,
                ExecutorNetworkProfiles = ["restricted"],
                ExecutorCredentials = ["claude", "codex"],
                Cordoned = true,
                Healthy = false,
            });

            var found = Assert.Single(await registry.ListAsync());
            Assert.Equal("exec-1", found.ExecutorHostId);
            Assert.Equal(3, found.MaxConcurrentSandboxes);
            Assert.Equal(["restricted"], found.ExecutorNetworkProfiles);
            Assert.Equal(["claude", "codex"], found.ExecutorCredentials);
            Assert.True(found.Cordoned);
            Assert.False(found.Healthy);
            Assert.True(found.IsExecutor);
        }
        finally
        {
            registry.Dispose();
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Registry_PreExistingDatabase_LoadsWithoutExecutorColumns()
    {
        var path = TempDbPath();
        try
        {
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE worker_registry (
                        worker_id            TEXT PRIMARY KEY,
                        host_name            TEXT NOT NULL,
                        process_id           INTEGER NOT NULL,
                        started_at           TEXT NOT NULL,
                        last_heartbeat_at    TEXT NOT NULL,
                        current_work_item_id TEXT
                    );
                    INSERT INTO worker_registry VALUES ('w-old', 'old-host', 7, '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', NULL);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            using var registry = new SqliteWorkerRegistry(path);
            var found = Assert.Single(await registry.ListAsync());
            Assert.Equal("w-old", found.WorkerId);
            Assert.Null(found.ExecutorHostId);
            Assert.Null(found.MaxConcurrentSandboxes);
            Assert.False(found.Cordoned);
            Assert.True(found.Healthy);
            Assert.False(found.IsExecutor);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ── reaper: existing dead-worker path reclaims executors ────────────────

    [Fact]
    public async Task Reaper_ClaimsStaleExecutor_AndKeepsHeartbeatingOne()
    {
        var path = TempDbPath();
        using var store = new SqliteWorkItemStore(path);
        using var registry = new SqliteWorkerRegistry(path);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = "executor:dead",
                HostName = "dead",
                ProcessId = 1,
                StartedAt = now.AddMinutes(-10),
                LastHeartbeatAt = now.AddMinutes(-10),
                ExecutorHostId = "dead",
                MaxConcurrentSandboxes = 2,
                ExecutorNetworkProfiles = [],
                ExecutorCredentials = ["claude"],
            });
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = "executor:live",
                HostName = "live",
                ProcessId = 2,
                StartedAt = now,
                LastHeartbeatAt = now,
                ExecutorHostId = "live",
            });

            var reaper = new DeadWorkerReaper(
                registry,
                store,
                new InMemoryTaskQueue(),
                new DeadWorkerOptions
                {
                    HeartbeatInterval = TimeSpan.FromSeconds(5),
                    DeadWorkerThreshold = TimeSpan.FromSeconds(60),
                    CheckInterval = TimeSpan.FromMinutes(60),
                },
                NullLogger<DeadWorkerReaper>.Instance);
            await reaper.RunOnceAsync(CancellationToken.None);

            var remaining = await registry.ListAsync();
            Assert.Equal(["executor:live"], remaining.Select(w => w.WorkerId).OrderBy(x => x).ToArray());
        }
        finally
        {
            store.Dispose();
            registry.Dispose();
            TryDelete(path);
        }
    }

    // ── outbound-only over a real socket ────────────────────────────────────

    [Fact]
    public async Task Executor_BindsNoInboundPort()
    {
        var before = ListeningPorts();
        using var stub = new StubOrchestrator();
        var stubPort = stub.Port;

        var options = ValidOptions();
        options.OrchestratorBaseUrl = $"http://127.0.0.1:{stubPort}/";
        options.HeartbeatInterval = TimeSpan.FromMilliseconds(100);
        using var http = new HttpClient { BaseAddress = new Uri(options.OrchestratorBaseUrl) };
        var client = new ExecutorClient(
            http,
            () => options,
            new FakeSandboxProvider(),
            new ExecutorSandboxTracker(),
            null,
            null,
            NullLogger<ExecutorClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.RegisterAsync(cts.Token);
        var run = client.RunAsync(cts.Token);
        await Task.Delay(600, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(stub.RegisterCount >= 1);
        Assert.True(stub.HeartbeatCount >= 1);

        var after = ListeningPorts();
        after.ExceptWith(before);
        Assert.Equal(stubPort, Assert.Single(after));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ExecutorOptions ValidOptions() => new()
    {
        HostId = "exec-1",
        OrchestratorBaseUrl = "http://127.0.0.1:1/",
        ApiKeyEnvVar = TestApiKey.EnvVar,
        MaxConcurrentSandboxes = 2,
        AllowedNetworkProfiles = ["restricted"],
        DeclaredCredentials = ["claude"],
    };

    private static ExecutorClient MakeClient(
        ExecutorOptions options,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        out RecordingHandler recording,
        ExecutorSandboxTracker? tracker = null,
        FakeSandboxProvider? provider = null,
        TimeProvider? clock = null,
        IPipelineRunner? phaseRunner = null)
    {
        recording = new RecordingHandler(handler);
        var http = new HttpClient(recording) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        return new ExecutorClient(
            http,
            () => options,
            (ISandboxProvider?)provider ?? new FakeSandboxProvider(),
            tracker,
            phaseRunner,
            clock,
            NullLogger<ExecutorClient>.Instance);
    }

    private static ExecutorClient MakeClientForServer(ExecutorOptions options, HttpClient http) =>
        new(
            http,
            () => options,
            new FakeSandboxProvider(),
            new ExecutorSandboxTracker(),
            null,
            null,
            NullLogger<ExecutorClient>.Instance);

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"codeybox-executor-{Guid.NewGuid():N}.db");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
    }

    private static ExecutorRegistration ToEligibility(JsonElement row) => new()
    {
        HostId = row.GetProperty("executorHostId").GetString()!,
        MaxConcurrentSandboxes = row.TryGetProperty("maxConcurrentSandboxes", out var cap) && cap.ValueKind == JsonValueKind.Number
            ? cap.GetInt32()
            : null,
        Cordoned = row.GetProperty("cordoned").GetBoolean(),
        Healthy = row.GetProperty("healthy").GetBoolean(),
    };

    private static HashSet<int> ListeningPorts()
    {
        var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
        return props.GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
    }

    private static class TestApiKey
    {
        public static readonly string EnvVar = "CODEYBOX_TEST_EXECUTOR_API_KEY";
        static TestApiKey() => Environment.SetEnvironmentVariable(EnvVar, new string('k', 40));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            handler(request, ct);
    }

    private sealed class FakeSandbox(string id) : ISandbox
    {
        public string Id { get; } = id;
        public bool Disposed { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        public string Name => "fake";
        public List<SandboxSpec> CreatedSpecs { get; } = [];
        public List<ManagedSandboxInfo> Inventory { get; set; } = [];
        public bool InventoryComplete { get; set; } = true;
        public List<string> DisposedLeakedNames { get; } = [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreatedSpecs.Add(spec);
            return Task.FromResult<ISandbox>(new FakeSandbox("fake-sandbox"));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(Inventory);

        public Task<ManagedSandboxInventory> ListManagedInventoryAsync(CancellationToken ct) =>
            Task.FromResult(new ManagedSandboxInventory(Inventory, InventoryComplete));

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
        {
            DisposedLeakedNames.Add(sandbox.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePhaseRunner : IPipelineRunner
    {
        public List<WorkItem> Ran { get; } = [];
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Ran.Add(item);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal real-socket orchestrator stub: accepts the executor's outbound
    /// register/heartbeat POSTs over loopback TCP. Exists so the no-inbound-port
    /// test exercises genuine sockets rather than an in-memory handler.
    /// </summary>
    private sealed class StubOrchestrator : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public int RegisterCount;
        public int HeartbeatCount;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public StubOrchestrator()
        {
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _ = HandleAsync(client, ct);
            }
        }

        private async Task HandleAsync(System.Net.Sockets.TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(ct);
                var contentLength = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct)))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
                        contentLength = n;
                }
                if (contentLength > 0)
                {
                    var buf = new char[contentLength];
                    var read = 0;
                    while (read < contentLength)
                    {
                        var n = await reader.ReadAsync(buf.AsMemory(read, contentLength - read), ct);
                        if (n == 0) break;
                        read += n;
                    }
                }
                if (requestLine is not null)
                {
                    if (requestLine.Contains("/executors/register", StringComparison.Ordinal))
                        Interlocked.Increment(ref RegisterCount);
                    else if (requestLine.Contains("/heartbeat", StringComparison.Ordinal))
                        Interlocked.Increment(ref HeartbeatCount);
                }
                var payload = "{\"workerId\":\"executor:exec-1\"}";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.ASCII.GetByteCount(payload)}\r\nConnection: close\r\n\r\n{payload}";
                var bytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(bytes, ct);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    public sealed class ExecutorApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-executor-endpoint-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"executor-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"executor-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"executor-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"executor-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                TryDelete(_dbPath);
            base.Dispose(disposing);
        }
    }
}
