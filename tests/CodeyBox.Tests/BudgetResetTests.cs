using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

// Framework FakeTimeProvider (CreateTimer fires on Advance); aliased to avoid
// the namespace-local FakeTimeProvider in AgentClassRouterScoreTests.cs.
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that budget windows roll correctly: items started outside the
/// rolling window are excluded from the count, making deferred items eligible
/// once the window advances.
/// </summary>
[Collection("Background service timing")]
public sealed class BudgetResetTests : IDisposable
{
    private static readonly TimeSpan DispatchObservationTimeout = TimeSpan.FromSeconds(15);

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

    // ── OrchestratorService integration: deferred items become eligible ────────

    /// <summary>
    /// Validates the ScheduleDeferredRequeue path: an item is initially deferred
    /// because the hourly cap is reached. When the items that consumed the cap are
    /// aged past the rolling window (StartedAt moved to 90 minutes ago), re-enqueueing
    /// the deferred item causes OrchestratorService to pick it up successfully.
    /// </summary>
    [Fact]
    public async Task DeferredItem_PickedUp_AfterWindowClears()
    {
        var pid = new ProjectId("defer-retry");
        var projectRepo = new InMemoryProjectRepository(new Project
        {
            Id = pid,
            DisplayName = "Defer Retry",
            RepositoryUrl = "https://github.com/test/repo",
            Budget = new ProjectBudget { MaxItemsPerHour = 1 },
        });

        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(
            _store, onRun: () => Interlocked.Increment(ref pickupCount));
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var hourlyRecheck = TimeSpan.FromSeconds(3);
        var budgetRecheck = new BudgetDeferralRecheckSnapshot(new BudgetDeferralRecheckOptions
        {
            HourlyLimitRecheck = hourlyRecheck,
        });
        // Inject a controllable clock: both the rolling budget-window cutoff
        // (CheckBudgetAsync) and the deferral re-pickup timer (ScheduleDeferredRequeue)
        // now read it, so the window-clears → re-pickup sequence is driven by
        // Advance() instead of a real 3s wall-clock wait that starves under load.
        var fakeTime = new ControllableTimeProvider();
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            projects: projectRepo,
            budgetDeferralRecheck: budgetRecheck,
            timeProvider: fakeTime);

        // Pre-seed 1 item whose StartedAt is inside the 1-hour window → cap is reached.
        var blocking = Started("defer-retry", fakeTime.GetUtcNow().AddMinutes(-30));
        await _store.CreateAsync(blocking);

        // Enqueue 1 new item — the orchestrator should defer it (cap=1, already 1 started).
        var newItem = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = "deferred",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(newItem);
        await queue.EnqueueAsync(newItem.Id);

        await svc.StartAsync(CancellationToken.None);

        // Generous timeout: under parallel test stress the dispatcher can take
        // multiple seconds to even pick up the item — a 2 s wall was flaky in
        // the audit sandbox. The check exits early once the item is deferred,
        // so happy-path runs are unaffected.
        var observedDeferred = await WaitUntilAsync(
            () => svc.IsDeferredForTest(newItem.Id),
            DispatchObservationTimeout);
        Assert.True(observedDeferred);
        Assert.Equal(0, pickupCount);

        // Simulate the rolling window advancing: age the blocking item out of the window.
        await _store.UpdateAsync(blocking with { StartedAt = fakeTime.GetUtcNow().AddHours(-2) });

        // Now the cap is not reached. Drive the deferral re-pickup timer on the
        // injected clock (HourlyLimitRecheck) until the item is picked up. The
        // fake clock — not the wall clock — fires the deferral; the 30s backstop
        // only guards against a genuine non-firing regression.
        var pickedUp = await AdvanceUntilAsync(
            fakeTime,
            hourlyRecheck,
            () => Volatile.Read(ref pickupCount) >= 1);

        await svc.StopAsync(CancellationToken.None);

        Assert.True(pickedUp, "deferred item should be picked up after the window clears and the recheck timer fires");
        Assert.Equal(1, Volatile.Read(ref pickupCount));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    /// <summary>
    /// Advances the injected fake clock by <paramref name="step"/> in a loop,
    /// yielding between advances so the deferral-timer continuation and the
    /// re-pickup dispatch can run, until <paramref name="predicate"/> trips. The
    /// fake clock fires the deferral timer; the 30s wall-clock backstop only
    /// guards against a genuine non-firing regression and never fires the timer
    /// itself, so it does not reintroduce wall-clock flakiness.
    /// </summary>
    private static async Task<bool> AdvanceUntilAsync(
        ControllableTimeProvider fakeTime,
        TimeSpan step,
        Func<bool> predicate)
    {
        var backstop = DateTime.UtcNow.AddSeconds(30);
        while (!predicate())
        {
            fakeTime.Advance(step);
            await Task.Yield();
            if (predicate())
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(2));
            if (DateTime.UtcNow > backstop)
                return predicate();
        }
        return true;
    }
}
