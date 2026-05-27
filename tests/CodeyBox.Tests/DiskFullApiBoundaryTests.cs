using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the API boundary contracts the disk-guard work added:
/// (1) <c>WorkItemStoreDiskFullException</c> bubbling up from any endpoint
/// must be translated by the dedicated <c>app.Use</c> middleware into a
/// 503 with <c>Retry-After: 300</c> and a fixed JSON body (no raw stack
/// trace). (2) <c>/healthz</c> must surface the per-mount disk[] array
/// when the registered sandbox provider implements
/// <see cref="IDiskGuardedSandboxProvider"/>, so dashboards can alert
/// before SQLITE_FULL fires.
/// </summary>
public sealed class DiskFullApiBoundaryTests
{
    [Fact]
    public async Task ListWorkItems_WhenStoreReportsDiskFull_Returns503WithRetryAfterAndJsonBody()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-503-{Guid.NewGuid():N}.db");
        try
        {
            // Materialise the schema by spinning a real store against the same path
            // first. The DI graph downstream (cost store, etc.) joins against the
            // work_items table at startup and crashes if the table is absent.
            using (var real = new SqliteWorkItemStore(dbPath)) { }

            using var factory = new DiskFullApiFactory(
                store: new DiskFullStore(operation: "ListAsync"),
                sandboxProvider: new StubSandboxProvider(),
                dbPathOverride: dbPath);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/workitems/");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("300", response.Headers.GetValues("Retry-After").Single());
            Assert.Equal("application/json",
                response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("state store full", doc.RootElement.GetProperty("error").GetString());
            Assert.Contains("host disk is exhausted",
                doc.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Healthz_WhenProviderExposesDiskGuard_IncludesDiskArrayPerMount()
    {
        var sandboxProvider = new StubDiskGuardedSandboxProvider(new[]
        {
            new DiskGuardSample("/fake/mp", FreeBytes: 50L * 1024 * 1024 * 1024, ThresholdBytes: 10L * 1024 * 1024 * 1024),
            new DiskGuardSample("/var/lib/codeybox", FreeBytes: 200L * 1024 * 1024, ThresholdBytes: 10L * 1024 * 1024 * 1024),
            new DiskGuardSample("/missing/mount", FreeBytes: null, ThresholdBytes: 10L * 1024 * 1024 * 1024),
        });
        using var factory = new DiskFullApiFactory(
            store: new InMemoryNoopWorkItemStore(),
            sandboxProvider: sandboxProvider);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());

        var disk = body.GetProperty("disk");
        Assert.Equal(JsonValueKind.Array, disk.ValueKind);
        Assert.Equal(3, disk.GetArrayLength());

        var first = disk[0];
        Assert.Equal("/fake/mp", first.GetProperty("path").GetString());
        Assert.Equal(50L * 1024 * 1024 * 1024, first.GetProperty("freeBytes").GetInt64());
        Assert.Equal(10L * 1024 * 1024 * 1024, first.GetProperty("thresholdBytes").GetInt64());
        Assert.False(first.GetProperty("belowThreshold").GetBoolean());

        var second = disk[1];
        Assert.Equal("/var/lib/codeybox", second.GetProperty("path").GetString());
        Assert.True(second.GetProperty("belowThreshold").GetBoolean(),
            "200 MiB free against 10 GiB threshold must report belowThreshold=true");

        var third = disk[2];
        // FreeBytes is nullable<long> upstream. When the probe cannot resolve
        // the mount we render JSON null, NOT belowThreshold=true (inconclusive
        // != exhausted — matches DefaultDiskSpaceProbe's contract).
        Assert.Equal(JsonValueKind.Null, third.GetProperty("freeBytes").ValueKind);
        Assert.False(third.GetProperty("belowThreshold").GetBoolean());
    }

    [Fact]
    public async Task Healthz_WhenProviderDoesNotImplementDiskGuardCapability_ReturnsEmptyDiskArray()
    {
        using var factory = new DiskFullApiFactory(
            store: new InMemoryNoopWorkItemStore(),
            sandboxProvider: new StubSandboxProvider());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        var disk = body.GetProperty("disk");
        Assert.Equal(JsonValueKind.Array, disk.ValueKind);
        Assert.Equal(0, disk.GetArrayLength());
    }

    private sealed class DiskFullApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;
        private readonly bool _ownsDbPath;
        private readonly IWorkItemStore _store;
        private readonly ISandboxProvider _sandboxProvider;

        public DiskFullApiFactory(IWorkItemStore store, ISandboxProvider sandboxProvider, string? dbPathOverride = null)
        {
            _store = store;
            _sandboxProvider = sandboxProvider;
            _dbPath = dbPathOverride ?? Path.Combine(
                Path.GetTempPath(), $"codeybox-diskfull-api-{Guid.NewGuid():N}.db");
            _ownsDbPath = dbPathOverride is null;
        }

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
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton(_store);

                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton(_sandboxProvider);

                services.RemoveAll<IProjectRepository>();
                services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsDbPath)
            {
                try { File.Delete(_dbPath); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Store that throws <see cref="WorkItemStoreDiskFullException"/> from
    /// <c>ListAsync</c>. Drives the dedicated 503 middleware from any
    /// endpoint that enumerates the store (e.g. <c>GET /workitems</c>).
    /// </summary>
    private sealed class DiskFullStore : IWorkItemStore
    {
        private readonly string _operation;
        public DiskFullStore(string operation) => _operation = operation;

        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default)
            => ThrowingEnumerable(_operation);

        private static async IAsyncEnumerable<WorkItem> ThrowingEnumerable(
            string operation,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new WorkItemStoreDiskFullException(operation,
                new InvalidOperationException("simulated SQLITE_FULL"));
#pragma warning disable CS0162 // unreachable code; keeps the async-iterator method shape valid.
            yield break;
#pragma warning restore CS0162
        }

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => Task.FromResult(true);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default)
            => Task.FromResult(new PriorityUpdateResult(PriorityUpdateOutcome.NotFound, null, null));
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Empty();
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Task.FromResult(0);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => Empty();
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, int, int, string)>>([]);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, int)>>([]);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Empty();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Empty();
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default)
            => Task.FromResult(new PromptReplaceResult(PromptReplaceOutcome.NotFound, null));
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemIteration>>([]);

        private static async IAsyncEnumerable<WorkItem> Empty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// No-op work-item store for tests that only need to exercise non-store
    /// endpoints (like <c>/healthz</c>). The DI graph requires an
    /// <see cref="IWorkItemStore"/> registration regardless.
    /// </summary>
    private sealed class InMemoryNoopWorkItemStore : IWorkItemStore
    {
        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => Task.FromResult(true);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default)
            => Task.FromResult(new PriorityUpdateResult(PriorityUpdateOutcome.NotFound, null, null));
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Empty();
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Task.FromResult(0);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => Empty();
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => Task.FromResult<WorkItem?>(null);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, int, int, string)>>([]);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, int)>>([]);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => Empty();
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Empty();
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Empty();
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default)
            => Task.FromResult(new PromptReplaceResult(PromptReplaceOutcome.NotFound, null));
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemIteration>>([]);

        private static async IAsyncEnumerable<WorkItem> Empty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubSandboxProvider : ISandboxProvider
    {
        public string Name => "stub";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException("test stub does not launch sandboxes");
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubDiskGuardedSandboxProvider : ISandboxProvider, IDiskGuardedSandboxProvider
    {
        private readonly IReadOnlyList<DiskGuardSample> _samples;
        public StubDiskGuardedSandboxProvider(IEnumerable<DiskGuardSample> samples) => _samples = samples.ToArray();
        public string Name => "stub-guarded";
        public IReadOnlyList<DiskGuardSample> SampleDiskGuardState() => _samples;
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException("test stub does not launch sandboxes");
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
