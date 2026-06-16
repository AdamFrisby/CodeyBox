using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class TransientNetworkAutoRetryTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("transient-retry");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-transient-network-").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task NotifyTransientFailure_SchedulesBackoffWithoutIncrementingAttempt()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem();
        await fixture.Store.CreateAsync(item);

        var result = await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Scheduled, result.Status);
        Assert.Equal(item.Id, result.UpdatedItem.Id);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), result.NextRetryAt);
        Assert.Equal("transient", stored!.FailureKind);
        Assert.Equal(_time.GetUtcNow(), stored.TransientRetryFirstFailedAt);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), stored.NextTransientRetryAt);
        Assert.Equal(0, stored.TransientRetryAttempts);
    }

    [Fact]
    public async Task NotifyTransientFailure_WhenRowLeftTransientState_ReturnsStateChangedWithoutScheduling()
    {
        using var fixture = BuildScheduler(EnabledRetryOptions());
        var stale = NewTransientItem();
        await fixture.Store.CreateAsync(stale);
        var alreadyRetried = stale.With(WorkItemState.Queued, "retry already resumed");
        await fixture.Store.UpdateAsync(alreadyRetried);

        var result = await fixture.Scheduler.NotifyTransientFailureAsync(stale);

        var stored = await fixture.Store.GetAsync(stale.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Skipped, result.Status);
        Assert.Equal("state-changed", result.Reason);
        Assert.Equal(WorkItemState.Queued, result.UpdatedItem.State);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Null(stored.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task NotifyTransientFailure_WhenSchedulePersistenceThrows_ReturnsSkippedWithoutArmingRetryTimer()
    {
        ThrowingOnceTryUpdateStore? failingStore = null;
        using var fixture = BuildScheduler(
            EnabledRetryOptions() with { BaseDelay = TimeSpan.FromSeconds(30) },
            storeDecorator: inner => failingStore = new ThrowingOnceTryUpdateStore(inner));
        var item = NewTransientItem();
        await fixture.Store.CreateAsync(item);

        var result = await fixture.Scheduler.NotifyTransientFailureAsync(item);

        Assert.Equal(WorkItemAutoRetryScheduleStatus.Skipped, result.Status);
        Assert.Equal("transient schedule write failed", result.Reason);
        Assert.Equal(1, failingStore!.TryUpdateCalls);
        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal("transient", stored.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Equal(0, stored.TransientRetryAttempts);

        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(1, failingStore.TryUpdateCalls);
    }

    [Fact]
    public async Task NotifyTransientFailure_AppliesFullJitterSpread()
    {
        var randoms = new Queue<double>([0.10, 0.90]);
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(100),
            MaxDelay = TimeSpan.FromSeconds(100),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.Full,
        }, jitterRandom: () => randoms.Dequeue());
        var first = NewTransientItem();
        var second = NewTransientItem();
        await fixture.Store.CreateAsync(first);
        await fixture.Store.CreateAsync(second);

        await fixture.Scheduler.NotifyTransientFailureAsync(first);
        await fixture.Scheduler.NotifyTransientFailureAsync(second);

        var storedFirst = await fixture.Store.GetAsync(first.Id);
        var storedSecond = await fixture.Store.GetAsync(second.Id);
        Assert.NotNull(storedFirst);
        Assert.NotNull(storedSecond);
        Assert.Equal(_time.GetUtcNow().AddSeconds(10), storedFirst!.NextTransientRetryAt);
        Assert.Equal(_time.GetUtcNow().AddSeconds(90), storedSecond!.NextTransientRetryAt);
        Assert.NotEqual(storedFirst.NextTransientRetryAt, storedSecond.NextTransientRetryAt);
    }

    [Theory]
    [InlineData(0.0, 30)]
    [InlineData(0.5, 60)]
    [InlineData(1.0, 90)]
    public async Task NotifyTransientFailure_AppliesDecorrelatedJitterWithinBaseAndTriplePreviousDelay(
        double random,
        int expectedDelaySeconds)
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(5),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.Decorrelated,
        }, jitterRandom: () => random);
        var item = NewTransientItem() with { TransientRetryAttempts = 1 };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(_time.GetUtcNow().AddSeconds(expectedDelaySeconds), stored!.NextTransientRetryAt);
    }

    [Fact]
    public async Task NotifyTransientFailure_AtAttemptCap_MarksTransientExhausted()
    {
        var webhooks = new CapturingWebhookDispatcher();
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        }, webhooks: webhooks);
        var item = NewTransientItem() with { TransientRetryAttempts = 5 };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal("transient-exhausted", stored!.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Contains("attempts=5; max=5", stored.LastError);
        var failed = Assert.Single(webhooks.Events, e => e.Event == "work_item.failed");
        Assert.Equal(stored.Id, failed.WorkItem?.Id);
    }

    [Fact]
    public async Task NotifyTransientFailure_AtAttemptCap_WhenFailedWebhookThrows_StillMarksTransientExhausted()
    {
        using var fixture = BuildScheduler(
            new AutoRetryOnTransientFailureOptions
            {
                Enabled = true,
                BaseDelay = TimeSpan.FromSeconds(30),
                MaxDelay = TimeSpan.FromMinutes(15),
                Multiplier = 2,
                MaxAutoRetriesPerWorkItem = 5,
                MaxElapsedTime = TimeSpan.FromHours(1),
                JitterMode = TransientRetryJitterMode.None,
            },
            webhooks: new ThrowingFailedWebhookDispatcher());
        var item = NewTransientItem() with { TransientRetryAttempts = 5 };
        await fixture.Store.CreateAsync(item);

        var result = await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemAutoRetryScheduleStatus.Exhausted, result.Status);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal("transient-exhausted", stored.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Contains("attempts=5; max=5", stored.LastError);
    }

    [Fact]
    public async Task NotifyTransientFailure_WhenNextDelayWouldExceedElapsedCap_MarksTransientExhausted()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromMinutes(2),
            MaxDelay = TimeSpan.FromMinutes(2),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem() with
        {
            TransientRetryFirstFailedAt = _time.GetUtcNow().AddMinutes(-59),
        };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal("transient-exhausted", stored!.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Contains("elapsed would exceed", stored.LastError);
    }

    [Fact]
    public async Task PeriodicSweep_RetriesOnlyTransientFailures_UsingAutoPick()
    {
        var gitHost = new RecordingGitHost { Ahead = true };
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        }, gitHost: gitHost);
        var transient = NewTransientItem() with
        {
            WorkBranch = "codeybox/prior-work",
            BaseBranch = "main",
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
        };
        var normal = NewTransientItem() with
        {
            State = WorkItemState.Failed,
            FailureKind = "other",
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
        };
        await fixture.Store.CreateAsync(transient);
        await fixture.Store.CreateAsync(normal);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(transient.Id);
        var untouched = await fixture.Store.GetAsync(normal.Id);
        Assert.NotNull(retried);
        Assert.NotNull(untouched);
        Assert.Equal(WorkItemState.WorkComplete, retried!.State);
        Assert.Equal(1, retried.TransientRetryAttempts);
        Assert.Null(retried.FailureKind);
        Assert.Null(retried.NextTransientRetryAt);
        Assert.Equal("audit", gitHost.LastActualFrom);
        Assert.Equal(WorkItemState.Failed, untouched!.State);
        Assert.Equal(0, untouched.TransientRetryAttempts);
        Assert.Equal(transient.Id, await fixture.Queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PeriodicSweep_WhenDueItemExceededElapsedCap_MarksTransientExhaustedWithoutEnqueueing()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromMinutes(10),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem() with
        {
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
            TransientRetryFirstFailedAt = _time.GetUtcNow().AddMinutes(-11),
        };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal("transient-exhausted", stored.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Contains("elapsed=", stored.LastError);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task PeriodicSweep_RetriesFailedTransientRows_UsingAutoPick()
    {
        using var fixture = BuildScheduler(EnabledRetryOptions());
        var transient = NewTransientItem() with
        {
            State = WorkItemState.Failed,
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
        };
        var normal = NewTransientItem() with
        {
            State = WorkItemState.Failed,
            FailureKind = "other",
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
        };
        await fixture.Store.CreateAsync(transient);
        await fixture.Store.CreateAsync(normal);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(transient.Id);
        var untouched = await fixture.Store.GetAsync(normal.Id);
        Assert.NotNull(retried);
        Assert.NotNull(untouched);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.TransientRetryAttempts);
        Assert.Null(retried.FailureKind);
        Assert.Null(retried.NextTransientRetryAt);
        Assert.Equal(WorkItemState.Failed, untouched!.State);
        Assert.Equal(0, untouched.TransientRetryAttempts);
        Assert.Equal(transient.Id, await fixture.Queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PeriodicSweep_TransientRetrySuccess_PublishesTransientAutoRetryWebhook()
    {
        var webhooks = new CapturingWebhookDispatcher();
        using var fixture = BuildScheduler(EnabledRetryOptions(), webhooks: webhooks);
        var item = NewTransientItem() with
        {
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
            TransientRetryFrom = "merge",
            TransientRetryAttempts = 2,
            WorkBranch = "codeybox/transient-webhook",
            BaseBranch = "main",
        };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(retried);
        Assert.Equal(WorkItemState.AuditPassed, retried!.State);
        Assert.Equal(3, retried.TransientRetryAttempts);

        var evt = Assert.Single(webhooks.Events, e => e.Event == "work_item.auto_retry");
        Assert.Equal(item.Id, evt.WorkItem?.Id);
        using var details = JsonDocument.Parse(JsonSerializer.Serialize(evt.Details));
        var root = details.RootElement;
        Assert.Equal(item.Id.ToString(), root.GetProperty("workItemId").GetString());
        Assert.Equal("transient", root.GetProperty("reason").GetString());
        Assert.Equal(3, root.GetProperty("attemptNumber").GetInt32());
        Assert.Equal("periodic", root.GetProperty("triggeredBy").GetString());
        Assert.Equal("merge", root.GetProperty("from").GetString());
        Assert.Equal("merge", root.GetProperty("actualFrom").GetString());
    }

    [Fact]
    public async Task RetryThenSecondTransientFailure_UsesOriginalElapsedCap()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromMinutes(2),
            MaxDelay = TimeSpan.FromMinutes(2),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var firstFailedAt = _time.GetUtcNow().AddMinutes(-59);
        var item = NewTransientItem() with
        {
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
            TransientRetryFirstFailedAt = firstFailedAt,
        };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var retried = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(retried);
        Assert.Equal(WorkItemState.Queued, retried!.State);
        Assert.Equal(1, retried.TransientRetryAttempts);
        Assert.Equal(firstFailedAt, retried.TransientRetryFirstFailedAt);

        var failedAgain = retried.With(
            WorkItemState.WaitingForTransientRetry,
            "Agent claude reported transient transport failure again",
            failureKind: "transient");
        await fixture.Store.UpdateAsync(failedAgain);

        await fixture.Scheduler.NotifyTransientFailureAsync(failedAgain);

        var exhausted = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(exhausted);
        Assert.Equal("transient-exhausted", exhausted!.FailureKind);
        Assert.Equal(firstFailedAt, exhausted.TransientRetryFirstFailedAt);
        Assert.Null(exhausted.NextTransientRetryAt);
        Assert.Contains("elapsed would exceed", exhausted.LastError);
    }

    [Fact]
    public async Task PeriodicSweep_DoesNotRetryFutureDueTransientFailure()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem() with { NextTransientRetryAt = _time.GetUtcNow().AddMinutes(5) };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task PeriodicSweep_WhenGlobalQueuePaused_DoesNotConsumeAttempt()
    {
        var queueController = new FakeQueueController(globalPaused: true);
        using var fixture = BuildScheduler(EnabledRetryOptions(), queueController: queueController);
        var item = NewTransientItem() with { NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1) };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task PeriodicSweep_WhenProjectQueuePaused_DoesNotConsumeAttempt()
    {
        var queueController = new FakeQueueController(projectPaused: true);
        using var fixture = BuildScheduler(EnabledRetryOptions(), queueController: queueController);
        var item = NewTransientItem() with { NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1) };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task PeriodicSweep_WhenProjectRepositoryMissing_DoesNotConsumeAttempt()
    {
        using var fixture = BuildScheduler(EnabledRetryOptions(), includeProjects: false);
        var item = NewTransientItem() with { NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1) };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task PeriodicSweep_WhenProjectMissing_DoesNotConsumeAttempt()
    {
        using var fixture = BuildScheduler(
            EnabledRetryOptions(),
            projects: new InMemoryProjectRepository());
        var item = NewTransientItem() with { NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1) };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task PeriodicSweep_WhenRetrierRejectsResume_DoesNotConsumeAttempt()
    {
        var gitHost = new RecordingGitHost { RepositoryExists = false };
        using var fixture = BuildScheduler(EnabledRetryOptions(), gitHost: gitHost);
        var item = NewTransientItem() with
        {
            WorkBranch = "codeybox/prior-work",
            TransientRetryFrom = "audit",
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
        };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task NotifyTransientFailure_WhenDisabled_LeavesTransientFailureUnscheduled()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = false,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem();
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal("transient", stored.FailureKind);
        Assert.Null(stored.NextTransientRetryAt);
        Assert.Equal(0, stored.TransientRetryAttempts);
    }

    [Fact]
    public async Task PeriodicSweep_WhenDisabled_DoesNotRetryDueTransientFailure()
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = false,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem() with { NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1) };
        await fixture.Store.CreateAsync(item);

        await RunTransientPeriodicSweepAsync(fixture.Scheduler);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(8, 300)]
    public async Task NotifyTransientFailure_UsesMultiplierAndMaxDelayCap(int priorAttempts, int expectedDelaySeconds)
    {
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(5),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 10,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        });
        var item = NewTransientItem() with { TransientRetryAttempts = priorAttempts };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(_time.GetUtcNow().AddSeconds(expectedDelaySeconds), stored!.NextTransientRetryAt);
    }

    [Fact]
    public async Task StartAsync_RearmsPersistedTransientRetryAndTargetedTimerRequeues()
    {
        var gitHost = new RecordingGitHost { Ahead = false };
        using var fixture = BuildScheduler(new AutoRetryOnTransientFailureOptions
        {
            Enabled = true,
            PeriodicCheckInterval = TimeSpan.FromMinutes(10),
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(15),
            Multiplier = 2,
            MaxAutoRetriesPerWorkItem = 5,
            MaxElapsedTime = TimeSpan.FromHours(1),
            JitterMode = TransientRetryJitterMode.None,
        }, gitHost: gitHost);
        var item = NewTransientItem() with
        {
            NextTransientRetryAt = _time.GetUtcNow().AddMinutes(1),
        };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.StartAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));

        var retried = await WaitForAsync(
            async () => (await fixture.Store.GetAsync(item.Id))?.State == WorkItemState.Queued);
        Assert.True(retried);
        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.TransientRetryAttempts);
        Assert.Equal(item.Id, await fixture.Queue.DequeueAsync(CancellationToken.None));

        await fixture.Scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_HotEnableTransientRetry_RearmsPersistedTransientRetry()
    {
        var liveOptions = EnabledRetryOptions() with
        {
            Enabled = false,
            PeriodicCheckInterval = TimeSpan.FromMinutes(10),
        };
        using var fixture = BuildScheduler(
            liveOptions,
            transientRetryOptionsAccessor: () => liveOptions);
        var item = NewTransientItem() with
        {
            NextTransientRetryAt = _time.GetUtcNow().AddSeconds(-1),
        };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(250);

        var stillParked = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stillParked!.State);
        Assert.Equal(0, stillParked.TransientRetryAttempts);

        liveOptions = liveOptions with { Enabled = true };

        var retried = await WaitForAsync(
            async () => (await fixture.Store.GetAsync(item.Id))?.State == WorkItemState.Queued);
        Assert.True(retried);
        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.TransientRetryAttempts);
        Assert.Equal(item.Id, await fixture.Queue.DequeueAsync(CancellationToken.None));

        await fixture.Scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_HotEnableTransientRetry_SchedulesParkedTransientRetryWithoutTimestamp()
    {
        var liveOptions = EnabledRetryOptions() with
        {
            Enabled = false,
            PeriodicCheckInterval = TimeSpan.FromMinutes(10),
            BaseDelay = TimeSpan.FromSeconds(30),
        };
        using var fixture = BuildScheduler(
            liveOptions,
            transientRetryOptionsAccessor: () => liveOptions);
        var item = NewTransientItem() with { NextTransientRetryAt = null };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(250);

        var stillUnscheduled = await fixture.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stillUnscheduled!.State);
        Assert.Null(stillUnscheduled.NextTransientRetryAt);

        liveOptions = liveOptions with { Enabled = true };

        var scheduled = await WaitForAsync(
            async () => (await fixture.Store.GetAsync(item.Id))?.NextTransientRetryAt is not null);
        Assert.True(scheduled);
        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), stored.NextTransientRetryAt);

        await fixture.Scheduler.StopAsync(CancellationToken.None);
    }

    private SchedulerFixture BuildScheduler(
        AutoRetryOnTransientFailureOptions transientOptions,
        RecordingGitHost? gitHost = null,
        IQueueController? queueController = null,
        Func<double>? jitterRandom = null,
        IProjectRepository? projects = null,
        bool includeProjects = true,
        IWebhookDispatcher? webhooks = null,
        Func<AutoRetryOnTransientFailureOptions>? transientRetryOptionsAccessor = null,
        Func<SqliteWorkItemStore, IWorkItemStore>? storeDecorator = null)
    {
        var sqliteStore = new SqliteWorkItemStore(Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db"));
        var store = storeDecorator?.Invoke(sqliteStore) ?? sqliteStore;
        var queue = new InMemoryTaskQueue();
        gitHost ??= new RecordingGitHost();
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        projects ??= includeProjects ? new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Transient retry",
            RepositoryUrl = "file:///tmp/transient-retry",
            DefaultAgent = AgentKind.Claude,
        }) : null;
        var opts = new OrchestratorOptions
        {
            AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions { Enabled = false },
            AutoRetryOnTransientFailure = transientOptions,
        };
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            opts,
            NullLogger<QuotaRetryScheduler>.Instance,
            projects: projects,
            queueController: queueController,
            webhooks: webhooks,
            timeProvider: _time,
            transientRetryOptionsAccessor: transientRetryOptionsAccessor ?? (() => transientOptions),
            jitterRandom: jitterRandom);

        return new SchedulerFixture(sqliteStore, queue, scheduler);
    }

    private static async Task RunTransientPeriodicSweepAsync(QuotaRetryScheduler scheduler)
    {
        var method = typeof(QuotaRetryScheduler).GetMethod(
            "RunTransientPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(scheduler, [CancellationToken.None])!;
    }

    private static WorkItem NewTransientItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "Transient retry",
        Prompt = "retry after transient transport failure",
        State = WorkItemState.WaitingForTransientRetry,
        LastError = "Agent claude reported transient transport failure",
        FailureKind = "transient",
        PushUpstream = false,
    };

    private static AutoRetryOnTransientFailureOptions EnabledRetryOptions() => new()
    {
        Enabled = true,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(15),
        Multiplier = 2,
        MaxAutoRetriesPerWorkItem = 5,
        MaxElapsedTime = TimeSpan.FromHours(1),
        JitterMode = TransientRetryJitterMode.None,
    };

    private async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return true;
            _time.Advance(TimeSpan.Zero);
            await Task.Delay(20);
        }

        return await condition();
    }

    private sealed record SchedulerFixture(
        SqliteWorkItemStore Store,
        InMemoryTaskQueue Queue,
        QuotaRetryScheduler Scheduler) : IDisposable
    {
        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class RecordingGitHost : IGitHost
    {
        public bool Ahead { get; init; }
        public bool RepositoryExists { get; init; } = true;
        public bool BranchExists { get; init; } = true;
        public string? LastActualFrom { get; private set; }

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
            => throw new NotSupportedException();

        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => Task.FromResult("main");

        public Task PushToUpstreamAsync(
            string repositoryId,
            string upstreamUrl,
            string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult(RepositoryExists);

        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(BranchExists);

        public Task<bool> BranchHasCommitsAheadAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
        {
            LastActualFrom = Ahead ? "audit" : "work";
            return Task.FromResult(Ahead);
        }

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult((string.Empty, string.Empty));
    }

    private sealed class FakeQueueController : IQueueController
    {
        private readonly bool _projectPaused;

        public FakeQueueController(bool globalPaused = false, bool projectPaused = false)
        {
            State = globalPaused ? QueueState.Paused : QueueState.Running;
            _projectPaused = projectPaused;
        }

        public QueueState State { get; }
        public DateTimeOffset? PausedAt => null;
        public string? PausedReason => null;
        public Task PauseAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default)
            => Task.FromResult<ProjectQueueState?>(new ProjectQueueState(projectId, _projectPaused, null, null));
    }

    private sealed class ThrowingFailedWebhookDispatcher : IWebhookDispatcher
    {
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct = default)
            => evt.Event == "work_item.failed"
                ? throw new InvalidOperationException("webhook failed")
                : Task.CompletedTask;
    }

    private sealed class ThrowingOnceTryUpdateStore : IWorkItemStore
    {
        private readonly SqliteWorkItemStore _inner;
        private int _remainingThrows = 1;
        private int _tryUpdateCalls;

        public ThrowingOnceTryUpdateStore(SqliteWorkItemStore inner) => _inner = inner;

        public int TryUpdateCalls => Volatile.Read(ref _tryUpdateCalls);

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => _inner.CreateAsync(item, ct);
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => _inner.UpdateAsync(item, ct);

        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _tryUpdateCalls);
            if (Interlocked.Exchange(ref _remainingThrows, 0) == 1)
                throw new InvalidOperationException("transient schedule write failed");
            return _inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        }

        public Task<PriorityUpdateResult> UpdatePriorityAsync(
            WorkItemId id,
            int priority,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => _inner.UpdatePriorityAsync(id, priority, updatedAt, ct);

        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(
            WorkItemId id,
            IReadOnlyList<WorkItemId> dependsOn,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => _inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);

        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(
            WorkItemId id,
            int? auditMaxIterations,
            string? auditComplexity,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => _inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => _inner.ListByStateAsync(state, ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => _inner.CountByStateAsync(state, ct);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => _inner.ReorderAsync(orderedIds, ct);
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => _inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => _inner.CountStartedInWindowAsync(projectId, since, ct);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => _inner.CountInFlightAsync(projectId, ct);
        public Task<(int Refactor, int Other)> CountInFlightSplitByRefactorAsync(ProjectId projectId, CancellationToken ct = default, WorkItemId? excludeId = null) => _inner.CountInFlightSplitByRefactorAsync(projectId, ct, excludeId);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => _inner.GetByExternalIdAsync(projectId, externalId, ct);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => _inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => _inner.GetFleetStateCountsAsync(ct);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => _inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => _inner.GetFleetPauseStatesAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => _inner.ListByReplaySourceAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => _inner.ListSuspendedAsync(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => _inner.GetActiveBaselineImageRefsAsync(ct);
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => _inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => _inner.OrphanReplaysAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => _inner.ListByReleaseAsync(releaseId, ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => _inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => _inner.GetIterationsAsync(workItemId, ct);
    }
}
