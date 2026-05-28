using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// B1: round-trip and aggregation tests for the new
/// <see cref="WorkItem.BaselineImageRef"/> column and the two
/// <see cref="IWorkItemStore"/> methods that support the reaper /baselines
/// endpoint: <see cref="IWorkItemStore.GetActiveBaselineImageRefsAsync"/> and
/// <see cref="IWorkItemStore.ListWorkItemsForBaselineAsync"/>.
/// </summary>
public sealed class SqliteWorkItemStoreBaselineImageRefTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-baseline-ref-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SqliteWorkItemStoreBaselineImageRefTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Sample(string? baselineRef = null, WorkItemState state = WorkItemState.Queued) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "x",
        Agent = AgentKind.Claude,
        State = state,
        BaselineImageRef = baselineRef,
    };

    [Fact]
    public async Task RoundTrip_BaselineImageRef_Preserved()
    {
        var item = Sample(baselineRef: "cb-baseline-abc123");
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal("cb-baseline-abc123", read!.BaselineImageRef);
    }

    [Fact]
    public async Task LegacyRow_NullBaselineImageRef_RoundTripsAsNull()
    {
        var item = Sample(baselineRef: null);
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Null(read!.BaselineImageRef);
    }

    [Fact]
    public async Task UpdateAsync_PersistsBaselineImageRefChange()
    {
        var item = Sample();
        await _store.CreateAsync(item);
        await _store.UpdateAsync(item with { BaselineImageRef = "cb-baseline-stamped" });
        var read = await _store.GetAsync(item.Id);
        Assert.Equal("cb-baseline-stamped", read!.BaselineImageRef);
    }

    [Fact]
    public async Task GetActiveBaselineImageRefs_OnlyIncludesNonTerminal()
    {
        await _store.CreateAsync(Sample("cb-baseline-aaa", WorkItemState.Working));
        await _store.CreateAsync(Sample("cb-baseline-bbb", WorkItemState.Auditing));
        await _store.CreateAsync(Sample("cb-baseline-ccc", WorkItemState.Done));
        await _store.CreateAsync(Sample("cb-baseline-ddd", WorkItemState.Failed));
        await _store.CreateAsync(Sample("cb-baseline-eee", WorkItemState.Cancelled));
        // Null baseline must not appear.
        await _store.CreateAsync(Sample(baselineRef: null, state: WorkItemState.Working));

        var active = await _store.GetActiveBaselineImageRefsAsync();

        Assert.Contains("cb-baseline-aaa", active);
        Assert.Contains("cb-baseline-bbb", active);
        Assert.DoesNotContain("cb-baseline-ccc", active);
        Assert.DoesNotContain("cb-baseline-ddd", active);
        Assert.DoesNotContain("cb-baseline-eee", active);
        Assert.Equal(2, active.Count);
    }

    [Fact]
    public async Task GetActiveBaselineImageRefs_Distinct()
    {
        await _store.CreateAsync(Sample("cb-baseline-shared", WorkItemState.Working));
        await _store.CreateAsync(Sample("cb-baseline-shared", WorkItemState.Working));

        var active = await _store.GetActiveBaselineImageRefsAsync();

        Assert.Single(active);
        Assert.Contains("cb-baseline-shared", active);
    }

    [Fact]
    public async Task ListWorkItemsForBaseline_ReturnsExpectedItems()
    {
        var w1 = Sample("cb-baseline-x", WorkItemState.Working);
        var w2 = Sample("cb-baseline-x", WorkItemState.Done);
        var w3 = Sample("cb-baseline-y", WorkItemState.Working);

        await _store.CreateAsync(w1);
        await _store.CreateAsync(w2);
        await _store.CreateAsync(w3);

        var matches = await _store.ListWorkItemsForBaselineAsync("cb-baseline-x");

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Id == w1.Id);
        Assert.Contains(matches, m => m.Id == w2.Id);
        Assert.DoesNotContain(matches, m => m.Id == w3.Id);
    }
}
