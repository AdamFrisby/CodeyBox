using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Store-level tests for the two methods backing operator baseline migration:
/// <see cref="IWorkItemStore.ListNonTerminalBaselinePinnedAsync"/> (candidate
/// query) and <see cref="IWorkItemStore.ClearBaselinePinsAsync"/> (the atomic,
/// idempotent, terminal-guarded clear).
/// </summary>
public sealed class SqliteWorkItemStoreClearBaselinePinsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-clearpins-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SqliteWorkItemStoreClearBaselinePinsTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItem Sample(
        string? baselineRef,
        WorkItemState state = WorkItemState.Working,
        string project = "p") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(project),
        Title = "t",
        Prompt = "x",
        Agent = AgentKind.Claude,
        State = state,
        BaselineImageRef = baselineRef,
    };

    [Fact]
    public async Task List_ReturnsOnlyNonTerminalPinnedItems()
    {
        var live = Sample("cb-baseline-a", WorkItemState.Working);
        await _store.CreateAsync(live);
        await _store.CreateAsync(Sample("cb-baseline-b", WorkItemState.Done));
        await _store.CreateAsync(Sample("cb-baseline-c", WorkItemState.Cancelled));
        await _store.CreateAsync(Sample(baselineRef: null, state: WorkItemState.Working));

        var candidates = await _store.ListNonTerminalBaselinePinnedAsync(null, null, 100);

        var item = Assert.Single(candidates);
        Assert.Equal(live.Id, item.Id);
        Assert.Equal("cb-baseline-a", item.BaselineImageRef);
    }

    [Fact]
    public async Task List_RespectsProjectAndRefFilters()
    {
        await _store.CreateAsync(Sample("cb-baseline-x", WorkItemState.Working, project: "alpha"));
        await _store.CreateAsync(Sample("cb-baseline-y", WorkItemState.Working, project: "alpha"));
        await _store.CreateAsync(Sample("cb-baseline-x", WorkItemState.Working, project: "beta"));

        var byProject = await _store.ListNonTerminalBaselinePinnedAsync(new ProjectId("alpha"), null, 100);
        Assert.Equal(2, byProject.Count);
        Assert.All(byProject, c => Assert.Equal("alpha", c.ProjectId.Value));

        var byRef = await _store.ListNonTerminalBaselinePinnedAsync(null, "cb-baseline-x", 100);
        Assert.Equal(2, byRef.Count);
        Assert.All(byRef, c => Assert.Equal("cb-baseline-x", c.BaselineImageRef));

        var byBoth = await _store.ListNonTerminalBaselinePinnedAsync(new ProjectId("alpha"), "cb-baseline-x", 100);
        Assert.Single(byBoth);
    }

    [Fact]
    public async Task List_HonoursLimit()
    {
        for (var i = 0; i < 5; i++)
            await _store.CreateAsync(Sample($"cb-baseline-{i}", WorkItemState.Working));

        var limited = await _store.ListNonTerminalBaselinePinnedAsync(null, null, 3);

        Assert.Equal(3, limited.Count);
    }

    [Fact]
    public async Task Clear_SetsPinNull_ForNonTerminalItems_AndReturnsCount()
    {
        var a = Sample("cb-baseline-a", WorkItemState.Working);
        var b = Sample("cb-baseline-b", WorkItemState.Auditing);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);

        var cleared = await _store.ClearBaselinePinsAsync([a.Id, b.Id], DateTimeOffset.UtcNow);

        Assert.Equal(2, cleared);
        Assert.Null((await _store.GetAsync(a.Id))!.BaselineImageRef);
        Assert.Null((await _store.GetAsync(b.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Clear_SkipsTerminalItems_LeavingPinIntact()
    {
        var done = Sample("cb-baseline-done", WorkItemState.Done);
        await _store.CreateAsync(done);

        var cleared = await _store.ClearBaselinePinsAsync([done.Id], DateTimeOffset.UtcNow);

        Assert.Equal(0, cleared);
        Assert.Equal("cb-baseline-done", (await _store.GetAsync(done.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Clear_IsIdempotent_SecondCallReturnsZero()
    {
        var a = Sample("cb-baseline-a", WorkItemState.Working);
        await _store.CreateAsync(a);

        Assert.Equal(1, await _store.ClearBaselinePinsAsync([a.Id], DateTimeOffset.UtcNow));
        Assert.Equal(0, await _store.ClearBaselinePinsAsync([a.Id], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Clear_LeavesUnlistedItemsUntouched()
    {
        var target = Sample("cb-baseline-target", WorkItemState.Working);
        var bystander = Sample("cb-baseline-bystander", WorkItemState.Working);
        await _store.CreateAsync(target);
        await _store.CreateAsync(bystander);

        await _store.ClearBaselinePinsAsync([target.Id], DateTimeOffset.UtcNow);

        Assert.Null((await _store.GetAsync(target.Id))!.BaselineImageRef);
        Assert.Equal("cb-baseline-bystander", (await _store.GetAsync(bystander.Id))!.BaselineImageRef);
    }

    [Fact]
    public async Task Clear_EmptyList_ReturnsZero_NoThrow()
    {
        Assert.Equal(0, await _store.ClearBaselinePinsAsync([], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Clear_LargeBatch_ExceedsChunkSize_ClearsAll()
    {
        // More than one chunk (ClearBaselineBatchSize = 500) to exercise the
        // multi-statement single transaction path.
        var ids = new List<WorkItemId>();
        for (var i = 0; i < 600; i++)
        {
            var item = Sample($"cb-baseline-{i}", WorkItemState.Working);
            await _store.CreateAsync(item);
            ids.Add(item.Id);
        }

        var cleared = await _store.ClearBaselinePinsAsync(ids, DateTimeOffset.UtcNow);

        Assert.Equal(600, cleared);
        var remaining = await _store.ListNonTerminalBaselinePinnedAsync(null, null, 1000);
        Assert.Empty(remaining);
    }
}
