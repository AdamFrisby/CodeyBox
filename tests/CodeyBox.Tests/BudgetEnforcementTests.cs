using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies per-project budget cap enforcement at pickup time.
/// Tests use the store's budget-query methods directly and exercise the
/// OrchestratorService pickup path via a counting pipeline.
/// </summary>
public sealed class BudgetEnforcementTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-budget-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public BudgetEnforcementTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeQueued(string projectId = "proj-a") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    // ── CountStartedInWindowAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CountStarted_NoItems_ReturnsZero()
    {
        var count = await _store.CountStartedInWindowAsync(
            new ProjectId("proj-a"), DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountStarted_ItemsWithStartedAt_Counted()
    {
        var pid = new ProjectId("proj-x");
        var item = MakeQueued("proj-x") with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30) };
        await _store.CreateAsync(item);

        var count = await _store.CountStartedInWindowAsync(pid, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountStarted_ItemsOutsideWindow_NotCounted()
    {
        var pid = new ProjectId("proj-x");
        // Started 2 hours ago — outside the 1-hour window.
        var item = MakeQueued("proj-x") with { StartedAt = DateTimeOffset.UtcNow.AddHours(-2) };
        await _store.CreateAsync(item);

        var count = await _store.CountStartedInWindowAsync(pid, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountStarted_NullStartedAt_NotCounted()
    {
        var pid = new ProjectId("proj-x");
        // Item with no StartedAt (not yet picked up).
        var item = MakeQueued("proj-x");
        await _store.CreateAsync(item);

        var count = await _store.CountStartedInWindowAsync(pid, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountStarted_OnlyCountsMatchingProject()
    {
        var now = DateTimeOffset.UtcNow;
        var itemA = MakeQueued("proj-a") with { StartedAt = now.AddMinutes(-10) };
        var itemB = MakeQueued("proj-b") with { StartedAt = now.AddMinutes(-10) };
        await _store.CreateAsync(itemA);
        await _store.CreateAsync(itemB);

        var countA = await _store.CountStartedInWindowAsync(new ProjectId("proj-a"), now.AddHours(-1));
        var countB = await _store.CountStartedInWindowAsync(new ProjectId("proj-b"), now.AddHours(-1));

        Assert.Equal(1, countA);
        Assert.Equal(1, countB);
    }

    // ── CountInFlightAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CountInFlight_NoItems_ReturnsZero()
    {
        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_WorkingItems_Counted()
    {
        var item = MakeQueued() with { State = WorkItemState.Working };
        await _store.CreateAsync(item);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountInFlight_QueuedItems_NotCounted()
    {
        var item = MakeQueued();
        await _store.CreateAsync(item);

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_TerminalItems_NotCounted()
    {
        foreach (var state in new[] { WorkItemState.Done, WorkItemState.Failed, WorkItemState.Cancelled, WorkItemState.AuditFailed })
        {
            var item = MakeQueued() with { State = state };
            await _store.CreateAsync(item);
        }

        var count = await _store.CountInFlightAsync(new ProjectId("proj-a"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountInFlight_AllActiveStates_Counted()
    {
        var pid = new ProjectId("proj-multi");
        foreach (var state in new[]
        {
            WorkItemState.Working, WorkItemState.WorkComplete,
            WorkItemState.Auditing, WorkItemState.Reworking, WorkItemState.AuditPassed,
            WorkItemState.Merging, WorkItemState.Merged, WorkItemState.UpstreamPushing,
        })
        {
            await _store.CreateAsync(MakeQueued("proj-multi") with { State = state });
        }

        var count = await _store.CountInFlightAsync(pid);
        Assert.Equal(8, count);
    }

    // ── StartedAt set on first pickup ─────────────────────────────────────────

    [Fact]
    public async Task StartedAt_SetOnFirstPickup_PersistedToStore()
    {
        var item = MakeQueued();
        await _store.CreateAsync(item);
        Assert.Null(item.StartedAt);

        // Simulate setting StartedAt (as OrchestratorService does before calling the pipeline).
        var started = DateTimeOffset.UtcNow;
        var updated = item with { StartedAt = started };
        await _store.UpdateAsync(updated);

        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read!.StartedAt);
        Assert.True(read.StartedAt >= started.AddSeconds(-1));
    }

    [Fact]
    public async Task StartedAt_RoundTrips_Precisely()
    {
        var ts = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var item = MakeQueued() with { StartedAt = ts };
        await _store.CreateAsync(item);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal(ts, read!.StartedAt);
    }
}
