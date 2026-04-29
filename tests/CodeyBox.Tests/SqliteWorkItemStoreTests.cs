using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteWorkItemStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-test-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SqliteWorkItemStoreTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
    };

    [Fact]
    public async Task RoundTrip_PreservesAllFields()
    {
        var item = Sample() with
        {
            BaseBranch = "main",
            WorkBranch = "feature/x",
            WorkTimeout = TimeSpan.FromMinutes(7),
            MergeTimeout = TimeSpan.FromMinutes(3),
            PushUpstream = false,
            UpstreamPushAttempts = 2,
        };
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(item.Title, read!.Title);
        Assert.Equal(item.BaseBranch, read.BaseBranch);
        Assert.Equal(item.WorkTimeout, read.WorkTimeout);
        Assert.Equal(item.PushUpstream, read.PushUpstream);
        Assert.Equal(item.UpstreamPushAttempts, read.UpstreamPushAttempts);
        Assert.Equal(item.Agent, read.Agent);
    }

    [Fact]
    public async Task UpdateAsync_PersistsTransitions()
    {
        var item = Sample();
        await _store.CreateAsync(item);
        await _store.UpdateAsync(item.With(WorkItemState.Working));
        await _store.UpdateAsync(item.With(WorkItemState.Failed, "broken"));
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, read!.State);
        Assert.Equal("broken", read.LastError);
    }

    [Fact]
    public async Task ListByStateAsync_FiltersCorrectly()
    {
        var working = Sample();
        var done = Sample();
        await _store.CreateAsync(working with { State = WorkItemState.Working });
        await _store.CreateAsync(done with { State = WorkItemState.Done });

        var results = new List<WorkItem>();
        await foreach (var w in _store.ListByStateAsync(WorkItemState.Working)) results.Add(w);
        Assert.Single(results);
        Assert.Equal(working.Id, results[0].Id);
    }

    [Fact]
    public async Task RoundTrip_NonEmptyDependsOn_Preserved()
    {
        var dep1 = Sample();
        var dep2 = Sample();
        await _store.CreateAsync(dep1);
        await _store.CreateAsync(dep2);

        var item = Sample() with { DependsOn = [dep1.Id, dep2.Id] };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(2, read!.DependsOn.Count);
        Assert.Contains(dep1.Id, read.DependsOn);
        Assert.Contains(dep2.Id, read.DependsOn);
    }

    [Fact]
    public async Task RoundTrip_EmptyDependsOn_Preserved()
    {
        var item = Sample() with { DependsOn = [] };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Empty(read!.DependsOn);
    }
}
