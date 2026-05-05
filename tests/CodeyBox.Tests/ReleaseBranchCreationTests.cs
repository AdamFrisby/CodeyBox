using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests the SETIFNULL atomic branch-assignment in SqliteReleaseStore.
/// Two concurrent callers both attempt TrySetBranchAsync; exactly one wins.
/// </summary>
public sealed class ReleaseBranchCreationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-branch-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _store;

    public ReleaseBranchCreationTests()
    {
        _store = new SqliteReleaseStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task TrySetBranchAsync_FirstCaller_Wins()
    {
        var rel = await SeedAsync();

        var won = await _store.TrySetBranchAsync(rel.Id, "release/v1.0", "abc123");

        Assert.True(won);
        var refreshed = await _store.GetAsync(rel.Id);
        Assert.Equal("release/v1.0", refreshed!.BranchName);
        Assert.Equal("abc123", refreshed.BaseCommitSha);
    }

    [Fact]
    public async Task TrySetBranchAsync_SecondCaller_LosesRace()
    {
        var rel = await SeedAsync();

        var first = await _store.TrySetBranchAsync(rel.Id, "release/v1.0", "sha-first");
        var second = await _store.TrySetBranchAsync(rel.Id, "release/v1.0-other", "sha-second");

        Assert.True(first);
        Assert.False(second);

        // The store keeps the first writer's values.
        var refreshed = await _store.GetAsync(rel.Id);
        Assert.Equal("release/v1.0", refreshed!.BranchName);
        Assert.Equal("sha-first", refreshed.BaseCommitSha);
    }

    [Fact]
    public async Task TrySetBranchAsync_ConcurrentCallers_ExactlyOneWins()
    {
        var rel = await SeedAsync();

        var tasks = Enumerable.Range(0, 8).Select(i =>
            _store.TrySetBranchAsync(rel.Id, $"release/v{i}", $"sha{i}"));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        var rel = await SeedAsync("duplicate-name");
        var duplicate = ReleaseTestHelper.SeedRelease(ReleaseState.Open) with
        {
            Name = rel.Name,
        };

        await Assert.ThrowsAnyAsync<Exception>(() => _store.CreateAsync(duplicate));
    }

    [Fact]
    public async Task ListAsync_FilterByState_ReturnsMatchingOnly()
    {
        var open1 = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        var open2 = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        var closed = ReleaseTestHelper.SeedRelease(ReleaseState.Closed);
        await _store.CreateAsync(open1);
        await _store.CreateAsync(open2);
        await _store.CreateAsync(closed);

        var openList = await _store.ListAsync(state: ReleaseState.Open);
        var closedList = await _store.ListAsync(state: ReleaseState.Closed);

        Assert.Equal(2, openList.Count);
        Assert.Single(closedList);
    }

    [Fact]
    public async Task GetByNameAsync_ExistingName_Returns()
    {
        var rel = await SeedAsync("my-release");

        var found = await _store.GetByNameAsync(rel.ProjectId, rel.Name);

        Assert.NotNull(found);
        Assert.Equal(rel.Id, found!.Id);
    }

    [Fact]
    public async Task GetByNameAsync_DifferentProject_ReturnsNull()
    {
        var rel = await SeedAsync("shared-name");

        var found = await _store.GetByNameAsync(new ProjectId("other-project"), rel.Name);

        Assert.Null(found);
    }

    private async Task<Release> SeedAsync(string? name = null)
    {
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        if (name is not null)
            rel = rel with { Name = name };
        await _store.CreateAsync(rel);
        return rel;
    }
}
