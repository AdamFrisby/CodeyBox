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

    private SchedulerFixture BuildScheduler(
        AutoRetryOnTransientFailureOptions transientOptions,
        RecordingGitHost? gitHost = null,
        IQueueController? queueController = null,
        Func<double>? jitterRandom = null,
        IProjectRepository? projects = null,
        bool includeProjects = true,
        IWebhookDispatcher? webhooks = null)
    {
        var store = new SqliteWorkItemStore(Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db"));
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
            transientRetryOptionsAccessor: () => transientOptions,
            jitterRandom: jitterRandom);

        return new SchedulerFixture(store, queue, scheduler);
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
}
