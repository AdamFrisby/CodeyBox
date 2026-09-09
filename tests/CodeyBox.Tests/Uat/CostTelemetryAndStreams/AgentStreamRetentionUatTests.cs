using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace CodeyBox.Tests.Uat.CostTelemetryAndStreams;

/// <summary>
/// UAT coverage for agent stream retention sweep behavior from the Cost,
/// Telemetry, And Streams section.
/// Plan anchor:
/// docs/uat/00-plan.md#agent-stream-retention-sweep---deletes-expired-agent-stream-files-and-empty-directories
/// </summary>
public sealed class AgentStreamRetentionUatTests : IDisposable
{
    private readonly CostTelemetryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Sweep_DeletesExpiredFilesAndRemovesEmptyWorkItemDirectories()
    {
        var streamRoot = _workspace.NewStreamRoot();
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = streamRoot, RetainedDays = 7 },
            NullLogger<AgentStreamStore>.Instance);
        var expiredItemId = WorkItemId.New();
        var freshItemId = WorkItemId.New();
        await using (var capture = await store.BeginCaptureAsync(expiredItemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"result\"}\n");
        await using (var capture = await store.BeginCaptureAsync(freshItemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"result\"}\n");
        var expiredFile = Assert.Single(await store.ListAsync(expiredItemId));
        var freshFile = Assert.Single(await store.ListAsync(freshItemId));
        var expiredPath = Path.Combine(streamRoot, expiredItemId.ToString(), expiredFile.FileName);
        var freshPath = Path.Combine(streamRoot, freshItemId.ToString(), freshFile.FileName);
        // Age via last-write time: the sweep reads GetLastWriteTimeUtc because
        // creation/birth time is unreliable on Linux.
        File.SetLastWriteTimeUtc(expiredPath, DateTime.Parse("2026-05-01T00:00:00Z").ToUniversalTime());
        File.SetLastWriteTimeUtc(freshPath, DateTime.Parse("2026-05-13T00:00:00Z").ToUniversalTime());

        var deleted = await store.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(expiredPath));
        Assert.False(Directory.Exists(Path.Combine(streamRoot, expiredItemId.ToString())));
        Assert.True(File.Exists(freshPath));
    }

    [Fact]
    public async Task Sweep_DisabledKeepForeverAndMissingRootAreNoOps()
    {
        var disabled = new AgentStreamStore(
            new AgentStreamsOptions { Enabled = false, Path = _workspace.NewStreamRoot(), RetainedDays = 7 },
            NullLogger<AgentStreamStore>.Instance);
        var keepForeverRoot = _workspace.NewStreamRoot();
        var keepForever = new AgentStreamStore(
            new AgentStreamsOptions { Path = keepForeverRoot, RetainedDays = 0 },
            NullLogger<AgentStreamStore>.Instance);
        var missingRoot = new AgentStreamStore(
            new AgentStreamsOptions { Path = _workspace.NewStreamRoot(), RetainedDays = 7 },
            NullLogger<AgentStreamStore>.Instance);
        var itemId = WorkItemId.New();
        await using (var capture = await keepForever.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"result\"}\n");
        var retainedFile = Assert.Single(await keepForever.ListAsync(itemId));
        var retainedPath = Path.Combine(keepForeverRoot, itemId.ToString(), retainedFile.FileName);
        File.SetLastWriteTimeUtc(retainedPath, DateTime.Parse("2026-04-01T00:00:00Z").ToUniversalTime());

        var disabledDeleted = await disabled.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        var keepForeverDeleted = await keepForever.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));
        var missingRootDeleted = await missingRoot.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        Assert.Equal(0, disabledDeleted);
        Assert.Equal(0, keepForeverDeleted);
        Assert.Equal(0, missingRootDeleted);
        Assert.True(File.Exists(retainedPath));
    }

    [Fact]
    public async Task Sweep_SizeBackstop_EvictsOldestFirstUntilUnderCap_EvenWhenRetentionDisabled()
    {
        var streamRoot = _workspace.NewStreamRoot();
        // RetainedDays = 0 disables age-based eviction; the size backstop must
        // still bound the directory. Cap = 1 MB; three ~0.6 MB files (1.8 MB
        // total) force eviction of the two oldest, leaving the newest ~0.6 MB.
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = streamRoot, RetainedDays = 0, MaxTotalSizeMb = 1 },
            NullLogger<AgentStreamStore>.Instance);
        const int payloadBytes = 600 * 1024;

        var oldest = await WriteSizedStreamAsync(store, streamRoot, payloadBytes, "2026-05-01T00:00:00Z");
        var middle = await WriteSizedStreamAsync(store, streamRoot, payloadBytes, "2026-05-02T00:00:00Z");
        var newest = await WriteSizedStreamAsync(store, streamRoot, payloadBytes, "2026-05-03T00:00:00Z");

        var deleted = await store.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(oldest), "oldest file should be evicted first");
        Assert.False(File.Exists(middle), "second-oldest file should be evicted next");
        Assert.True(File.Exists(newest), "newest file should be kept once under the cap");
    }

    [Fact]
    public async Task Sweep_SizeBackstop_UnderCap_IsNoOp()
    {
        var streamRoot = _workspace.NewStreamRoot();
        var store = new AgentStreamStore(
            new AgentStreamsOptions { Path = streamRoot, RetainedDays = 0, MaxTotalSizeMb = 8 },
            NullLogger<AgentStreamStore>.Instance);

        var only = await WriteSizedStreamAsync(store, streamRoot, 600 * 1024, "2026-05-01T00:00:00Z");

        var deleted = await store.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(only));
    }

    [Fact]
    public async Task Sweep_ReadsRetentionOptionsLive_HotReload()
    {
        var streamRoot = _workspace.NewStreamRoot();
        // Live options: the store reads the accessor on every sweep, so an edit
        // to RetainedDays takes effect without reconstructing the store.
        var live = new AgentStreamsOptions { Path = streamRoot, RetainedDays = 0 };
        var store = new AgentStreamStore(() => live, NullLogger<AgentStreamStore>.Instance);

        var itemId = WorkItemId.New();
        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk("{\"type\":\"result\"}\n");
        var file = Assert.Single(await store.ListAsync(itemId));
        var path = Path.Combine(streamRoot, itemId.ToString(), file.FileName);
        File.SetLastWriteTimeUtc(path, DateTime.Parse("2026-05-01T00:00:00Z").ToUniversalTime());

        // RetainedDays == 0 → age-based eviction disabled → old file kept.
        Assert.Equal(0, await store.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z")));
        Assert.True(File.Exists(path));

        // Reload a non-zero window → same store now evicts the aged file.
        live.RetainedDays = 7;
        Assert.Equal(1, await store.SweepAsync(DateTimeOffset.Parse("2026-05-14T00:00:00Z")));
        Assert.False(File.Exists(path));
    }

    private static async Task<string> WriteSizedStreamAsync(
        AgentStreamStore store,
        string streamRoot,
        int payloadBytes,
        string lastWriteUtc)
    {
        var itemId = WorkItemId.New();
        await using (var capture = await store.BeginCaptureAsync(itemId, "work", 1))
            capture!.WriteChunk(new string('x', payloadBytes) + "\n");
        var file = Assert.Single(await store.ListAsync(itemId));
        var path = Path.Combine(streamRoot, itemId.ToString(), file.FileName);
        File.SetLastWriteTimeUtc(path, DateTime.Parse(lastWriteUtc).ToUniversalTime());
        return path;
    }

    [Fact]
    public async Task RetentionService_LogsStoreFailuresAndContinuesStartupSweep()
    {
        using var cts = new CancellationTokenSource();
        var failingStore = new ThrowingSweepStore(cts.Cancel);
        var logger = new RecordingLogSink<AgentStreamRetentionService>();
        using var service = new AgentStreamRetentionService(failingStore, logger, TimeSpan.FromDays(1));
        var executeAsync = typeof(AgentStreamRetentionService).GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(executeAsync);
        var task = (Task)executeAsync.Invoke(service, [cts.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(1, failingStore.SweepCalls);
        Assert.Contains(logger.Warnings, warning => warning.Contains("retention sweep failed", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ThrowingSweepStore : IAgentStreamStore
    {
        private readonly Action _onSweep;

        public ThrowingSweepStore(Action onSweep) => _onSweep = onSweep;

        public int SweepCalls { get; private set; }
        public AgentStreamsOptions Options { get; } = new();

        public Task<AgentStreamCapture?> BeginCaptureAsync(
            WorkItemId workItemId,
            string phase,
            int iteration,
            CancellationToken ct = default)
            => Task.FromResult<AgentStreamCapture?>(null);

        public Task<IReadOnlyList<AgentStreamFile>> ListAsync(
            WorkItemId workItemId,
            int limit = AgentStreamStore.DefaultListLimit,
            bool includeLineCount = false,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentStreamFile>>([]);

        public Task<AgentStreamFile?> GetAsync(
            WorkItemId workItemId,
            string fileName,
            bool includeLineCount = false,
            CancellationToken ct = default)
            => Task.FromResult<AgentStreamFile?>(null);

        public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
        {
            SweepCalls++;
            _onSweep();
            throw new InvalidOperationException("planned sweep failure");
        }
    }
}
