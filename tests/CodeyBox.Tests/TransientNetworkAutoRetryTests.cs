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

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
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

    [Fact]
    public async Task NotifyTransientFailure_AtAttemptCap_MarksTransientExhausted()
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
        var item = NewTransientItem() with { TransientRetryAttempts = 5 };
        await fixture.Store.CreateAsync(item);

        await fixture.Scheduler.NotifyTransientFailureAsync(item);

        var stored = await fixture.Store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal("transient-exhausted", stored!.FailureKind);
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
            WorkItemState.Failed,
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
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal(0, stored.TransientRetryAttempts);
        Assert.Equal(0, fixture.Queue.Count);
    }

    private SchedulerFixture BuildScheduler(
        AutoRetryOnTransientFailureOptions transientOptions,
        RecordingGitHost? gitHost = null,
        IQueueController? queueController = null,
        Func<double>? jitterRandom = null)
    {
        var store = new SqliteWorkItemStore(Path.Combine(_workspace, $"state-{Guid.NewGuid():N}.db"));
        var queue = new InMemoryTaskQueue();
        gitHost ??= new RecordingGitHost();
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Transient retry",
            RepositoryUrl = "file:///tmp/transient-retry",
            DefaultAgent = AgentKind.Claude,
        });
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
        State = WorkItemState.Failed,
        LastError = "Agent claude reported transient transport failure",
        FailureKind = "transient",
        PushUpstream = false,
    };

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
            => Task.FromResult(true);

        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(true);

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
}
