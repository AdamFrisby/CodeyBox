using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the B1 <see cref="BaselineImageReaper"/>: reference-counted GC
/// for content-hashed baseline VMs. Uses an in-memory fake resolver and a
/// real SQLite work-item store so the live-ref query exercises the actual
/// SQL path.
/// </summary>
public sealed class BaselineImageReaperTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-baseline-test-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public BaselineImageReaperTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItem WorkItem(string? baselineRef, WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "x",
        Agent = AgentKind.Claude,
        State = state,
        BaselineImageRef = baselineRef,
    };

    /// <summary>
    /// Baselines referenced by an active (non-terminal) work item must survive
    /// the sweep, regardless of how long they've been on the host.
    /// </summary>
    [Fact]
    public async Task LiveRef_IsNotReaped()
    {
        await _store.CreateAsync(WorkItem("cb-baseline-aaa", WorkItemState.Working));
        var resolver = new FakeResolver();
        resolver.AddImage("cb-baseline-aaa");
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.Zero };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var reaper = new BaselineImageReaper(resolver, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(resolver.Disposed);
        var report = reaper.GetLatestReport();
        Assert.Single(report);
        Assert.True(report[0].IsLive);
    }

    /// <summary>
    /// An orphan that has been observed for less than the grace window is
    /// reported but not yet deleted. The reaper applies the window across
    /// sweeps using its in-memory first-observed clock.
    /// </summary>
    [Fact]
    public async Task Orphan_WithinGrace_IsNotReaped()
    {
        // No work item references this baseline.
        var resolver = new FakeResolver();
        resolver.AddImage("cb-baseline-orphan");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.FromHours(24) };
        var reaper = new BaselineImageReaper(resolver, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Empty(resolver.Disposed);
        var report = reaper.GetLatestReport();
        Assert.Single(report);
        Assert.False(report[0].IsLive);
        Assert.NotNull(report[0].FirstObservedOrphanAt);
    }

    /// <summary>
    /// An orphan first observed long enough ago to clear the grace window is
    /// reaped on the next sweep.
    /// </summary>
    [Fact]
    public async Task Orphan_PastGrace_IsReaped()
    {
        var resolver = new FakeResolver();
        resolver.AddImage("cb-baseline-old");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.FromHours(24) };
        var reaper = new BaselineImageReaper(resolver, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        // First sweep stamps the first-observed clock. No reap.
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(resolver.Disposed);

        // Advance past the grace window.
        time.Advance(TimeSpan.FromHours(25));

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Single(resolver.Disposed);
        Assert.Equal("cb-baseline-old", resolver.Disposed[0]);
    }

    /// <summary>
    /// When a baseline is referenced by a TERMINAL work item only (Done,
    /// Failed, etc.), it must be eligible for reaping. The live-ref query
    /// excludes terminal states.
    /// </summary>
    [Fact]
    public async Task Orphan_TerminalOnlyReferences_IsReaped()
    {
        await _store.CreateAsync(WorkItem("cb-baseline-zzz", WorkItemState.Done));
        var resolver = new FakeResolver();
        resolver.AddImage("cb-baseline-zzz");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.Zero };
        var reaper = new BaselineImageReaper(resolver, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Single(resolver.Disposed);
        Assert.Equal("cb-baseline-zzz", resolver.Disposed[0]);
    }

    /// <summary>
    /// A baseline that becomes live again between sweeps (e.g. a new work
    /// item pinned to it) must have its grace clock reset, so a subsequent
    /// brief orphaning doesn't immediately reap.
    /// </summary>
    [Fact]
    public async Task Orphan_BecomesLive_ResetsGraceClock()
    {
        var resolver = new FakeResolver();
        resolver.AddImage("cb-baseline-flap");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.FromHours(24) };
        var reaper = new BaselineImageReaper(resolver, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        // First sweep stamps clock.
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.False(reaper.GetLatestReport()[0].IsLive);

        // Operator creates a work item pinned to it.
        await _store.CreateAsync(WorkItem("cb-baseline-flap", WorkItemState.Working));
        time.Advance(TimeSpan.FromHours(12));
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.True(reaper.GetLatestReport()[0].IsLive);

        // That item finishes; baseline becomes orphan again. Clock starts fresh.
        var all = new List<WorkItem>();
        await foreach (var w in _store.ListAsync()) all.Add(w);
        var item = all[0];
        await _store.UpdateAsync(item.With(WorkItemState.Done));

        time.Advance(TimeSpan.FromHours(1));
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.False(reaper.GetLatestReport()[0].IsLive);
        // Only 1 hour into the fresh grace window — must not reap yet.
        Assert.Empty(resolver.Disposed);
    }

    /// <summary>
    /// A baseline that vanishes from the host between sweeps (operator-purged
    /// manually) must be dropped from the reaper's in-memory tracker so it
    /// doesn't keep accumulating phantom entries.
    /// </summary>
    [Fact]
    public async Task Disappeared_Baseline_IsForgottenFromTracker()
    {
        var resolver = new FakeResolver();
        resolver.AddImage("cb-baseline-ephemeral");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.FromHours(24) };
        var reaper = new BaselineImageReaper(resolver, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Single(reaper.GetLatestReport());

        // Operator manually purges; second sweep returns empty.
        resolver.RemoveImage("cb-baseline-ephemeral");
        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestReport());
    }

    /// <summary>
    /// Null Object resolver short-circuits ExecuteAsync — the reaper logs
    /// once and never sweeps. Verified indirectly: RunSweepAsync with a Null
    /// resolver still works (returns empty) without throwing.
    /// </summary>
    [Fact]
    public async Task NullResolver_SweepProducesEmptyReport()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var opts = new BaselineImageReaperOptions { GraceWindow = TimeSpan.FromHours(24) };
        var reaper = new BaselineImageReaper(
            NullBaselineImageResolver.Instance, _store, opts, NullLogger<BaselineImageReaper>.Instance, time);

        await reaper.RunSweepAsync(CancellationToken.None);
        Assert.Empty(reaper.GetLatestReport());
    }

    private sealed class FakeResolver : IBaselineImageResolver
    {
        private readonly List<BaselineImageInfo> _images = [];
        public List<string> Disposed { get; } = [];

        public void AddImage(string name) => _images.Add(new BaselineImageInfo(name, null, null));
        public void RemoveImage(string name) => _images.RemoveAll(i => i.Name == name);

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor) =>
            profileName is null ? null : $"cb-baseline-{profileName}-{flavor}";

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>(_images.ToList());

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct)
        {
            Disposed.Add(name);
            _images.RemoveAll(i => i.Name == name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
