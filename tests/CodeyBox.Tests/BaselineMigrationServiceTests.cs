using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Service-level tests for <see cref="BaselineMigrationService"/> wired to a real
/// <see cref="SqliteWorkItemStore"/>, an in-memory project repository, and a
/// fake baseline resolver whose returned ref stands in for a config change.
/// Verifies the end-to-end migration mechanism: a cleared pin causes the next
/// pickup to recompute a different ref; terminal / already-current items are
/// left alone; the reported count is correct and the operation is idempotent.
/// </summary>
public sealed class BaselineMigrationServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-migsvc-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly FakeBaselineResolver _resolver = new();
    private readonly BaselineMigrationService _service;

    private static readonly ProjectId ProjectA = new("proj-a");

    public BaselineMigrationServiceTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = ProjectA,
            DisplayName = "Project A",
            RepositoryUrl = "https://example.invalid/a",
            NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
        });
        _service = new BaselineMigrationService(
            _store,
            _resolver,
            projects,
            TimeProvider.System,
            () => new BaselineMigrationOptions { MaxItemsPerScan = 5000 },
            NullLogger<BaselineMigrationService>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private WorkItem Sample(string? baselineRef, WorkItemState state = WorkItemState.Working) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = ProjectA,
        Title = "t",
        Prompt = "x",
        Agent = AgentKind.Claude,
        State = state,
        BaselineImageRef = baselineRef,
    };

    [Fact]
    public async Task ClearingPin_MakesNextPickupRecomputeToNewRef_AfterConfigChange()
    {
        // Item pinned to the OLD baseline while the live config now resolves NEW.
        var item = Sample("cb-baseline-old");
        await _store.CreateAsync(item);
        _resolver.Current = "cb-baseline-new";

        var result = await _service.MigrateAsync(default);

        // Pin is cleared, so the next pickup recomputes from live config.
        Assert.Equal(1, result.MigratedCount);
        Assert.Null((await _store.GetAsync(item.Id))!.BaselineImageRef);

        // What it will recompute to differs from the old pin — the config change
        // is now visible: SandboxTargetResolver forwards the recomputed ref for a
        // matching work/headless target.
        var target = Assert.Single(result.RecomputeTargets);
        Assert.Equal("cb-baseline-new", target.BaselineImageRef);
        Assert.NotEqual("cb-baseline-old", target.BaselineImageRef);
    }

    [Fact]
    public async Task LeavesTerminalAndAlreadyCurrentItemsUntouched()
    {
        _resolver.Current = "cb-baseline-new";
        var stale = Sample("cb-baseline-old", WorkItemState.Working);
        var done = Sample("cb-baseline-old", WorkItemState.Done);
        var cancelled = Sample("cb-baseline-old", WorkItemState.Cancelled);
        var alreadyCurrent = Sample("cb-baseline-new", WorkItemState.Working);
        await _store.CreateAsync(stale);
        await _store.CreateAsync(done);
        await _store.CreateAsync(cancelled);
        await _store.CreateAsync(alreadyCurrent);

        var result = await _service.MigrateAsync(default);

        Assert.Equal(1, result.MigratedCount);
        Assert.Null((await _store.GetAsync(stale.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-old", (await _store.GetAsync(done.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-old", (await _store.GetAsync(cancelled.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-new", (await _store.GetAsync(alreadyCurrent.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Idempotent_SecondCallMigratesNothing()
    {
        _resolver.Current = "cb-baseline-new";
        await _store.CreateAsync(Sample("cb-baseline-old"));

        var first = await _service.MigrateAsync(default);
        var second = await _service.MigrateAsync(default);

        Assert.Equal(1, first.MigratedCount);
        Assert.Equal(0, second.MigratedCount);
        Assert.Equal(0, second.ScannedCount);
    }

    [Fact]
    public async Task RespectsBaselineRefFilter()
    {
        _resolver.Current = "cb-baseline-new";
        var oldA = Sample("cb-baseline-oldA");
        var oldB = Sample("cb-baseline-oldB");
        await _store.CreateAsync(oldA);
        await _store.CreateAsync(oldB);

        var result = await _service.MigrateAsync(new BaselineMigrationFilter(BaselineImageRef: "cb-baseline-oldA"));

        Assert.Equal(1, result.MigratedCount);
        Assert.Null((await _store.GetAsync(oldA.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-oldB", (await _store.GetAsync(oldB.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Truncates_WhenScanCapExceeded_AndReportsTruncated()
    {
        _resolver.Current = "cb-baseline-new";
        var cappedService = new BaselineMigrationService(
            _store,
            _resolver,
            new InMemoryProjectRepository(new Project
            {
                Id = ProjectA,
                DisplayName = "Project A",
                RepositoryUrl = "https://example.invalid/a",
                NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
            }),
            TimeProvider.System,
            () => new BaselineMigrationOptions { MaxItemsPerScan = 2 },
            NullLogger<BaselineMigrationService>.Instance);

        for (var i = 0; i < 5; i++)
            await _store.CreateAsync(Sample($"cb-baseline-old-{i}"));

        var result = await cappedService.MigrateAsync(default);

        Assert.True(result.Truncated);
        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(2, result.MigratedCount);

        // Idempotent re-runs drain the rest.
        var second = await cappedService.MigrateAsync(default);
        Assert.Equal(2, second.MigratedCount);
        var third = await cappedService.MigrateAsync(default);
        Assert.Equal(1, third.MigratedCount);
        Assert.False(third.Truncated);
    }

    [Fact]
    public async Task ResolverThrows_ClearsPinsToNoRecomputeTarget_WithoutAborting()
    {
        // A resolver that fails (e.g. transient config error) must not abort the
        // migration: matching stale pins still clear, treated as "no current
        // baseline", so they migrate to a null recompute target — exactly what a
        // failing pickup resolve would produce.
        _resolver.ThrowOnResolve = true;
        var item = Sample("cb-baseline-old");
        await _store.CreateAsync(item);

        var result = await _service.MigrateAsync(default);

        Assert.Equal(1, result.MigratedCount);
        Assert.Null((await _store.GetAsync(item.Id))!.BaselineImageRef);
        var target = Assert.Single(result.RecomputeTargets);
        Assert.Null(target.BaselineImageRef);
        Assert.Equal(1, target.Count);
    }

    private sealed class FakeBaselineResolver : IBaselineImageResolver
    {
        /// <summary>The ref the live config currently resolves to.</summary>
        public string? Current { get; set; }

        /// <summary>When true, <see cref="ResolveBaselineRef"/> throws to exercise
        /// the service's resolver-failure fallback.</summary>
        public bool ThrowOnResolve { get; set; }

        public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
        {
            if (ThrowOnResolve)
                throw new InvalidOperationException("resolver unavailable");
            return Current;
        }

        public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

        public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
