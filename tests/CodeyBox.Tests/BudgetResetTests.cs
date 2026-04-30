using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that budget windows roll correctly: items started outside the
/// rolling window are excluded from the count, making deferred items eligible
/// once the window advances.
/// </summary>
public sealed class BudgetResetTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-budgetreset-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public BudgetResetTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Started(string projectId, DateTimeOffset at) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Done,
        StartedAt = at,
    };

    [Fact]
    public async Task ItemsOutsideHourlyWindow_NotCounted_AfterWindowAdvances()
    {
        var pid = new ProjectId("roll-proj");
        var now = DateTimeOffset.UtcNow;

        // 3 items started just over an hour ago — outside the rolling 1h window.
        for (var i = 0; i < 3; i++)
            await _store.CreateAsync(Started("roll-proj", now.AddHours(-1).AddSeconds(-10)));

        // Window: last hour from "now".
        var count = await _store.CountStartedInWindowAsync(pid, now.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ItemsInsideHourlyWindow_Counted()
    {
        var pid = new ProjectId("roll-proj2");
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 3; i++)
            await _store.CreateAsync(Started("roll-proj2", now.AddMinutes(-30)));

        var count = await _store.CountStartedInWindowAsync(pid, now.AddHours(-1));
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task MixedItems_OnlyInsideWindowCounted()
    {
        var pid = new ProjectId("mix-proj");
        var now = DateTimeOffset.UtcNow;

        // 2 inside window, 2 outside.
        await _store.CreateAsync(Started("mix-proj", now.AddMinutes(-30)));
        await _store.CreateAsync(Started("mix-proj", now.AddMinutes(-45)));
        await _store.CreateAsync(Started("mix-proj", now.AddHours(-2)));
        await _store.CreateAsync(Started("mix-proj", now.AddHours(-3)));

        var count = await _store.CountStartedInWindowAsync(pid, now.AddHours(-1));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task DailyWindow_RollsIndependentlyOfHourlyWindow()
    {
        var pid = new ProjectId("daily-proj");
        var now = DateTimeOffset.UtcNow;

        // 5 started 2 hours ago: inside 24h window but outside 1h window.
        for (var i = 0; i < 5; i++)
            await _store.CreateAsync(Started("daily-proj", now.AddHours(-2)));

        var hourly = await _store.CountStartedInWindowAsync(pid, now.AddHours(-1));
        var daily = await _store.CountStartedInWindowAsync(pid, now.AddHours(-24));

        Assert.Equal(0, hourly);
        Assert.Equal(5, daily);
    }
}
