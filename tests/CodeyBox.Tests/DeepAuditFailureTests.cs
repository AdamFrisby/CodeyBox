using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when deep auditors persistently return blocking Errors across all
/// iterations, ReleaseService.RunDeepAuditPhaseAsync transitions the release to
/// Failed after DeepAuditMaxIterations and populates FailedReason.
/// </summary>
public sealed class DeepAuditFailureTests : IDisposable
{
    private const string AuditorName = "test-failure-auditor";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-fail-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();

    public DeepAuditFailureTests()
    {
        _releaseStore = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _workItemStore.Dispose();
        _releaseStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PersistentErrors_TransitionsToFailed_AfterMaxIterations()
    {
        const int maxIterations = 2;

        // Auditor always returns a blocking error — more results than maxIterations to be safe.
        var alwaysError = Enumerable.Repeat(
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Persistent bug", "Not fixed")]),
            maxIterations + 2).ToArray();

        var auditor = new ScriptedDeepAuditor(AuditorName, alwaysError);
        var (svc, rel, _) = await SetupAsync(auditor, maxIterations);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        var final = await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 10);

        Assert.Equal(ReleaseState.Failed, final);
    }

    [Fact]
    public async Task PersistentErrors_FailedReasonContainsIterationCount()
    {
        const int maxIterations = 2;
        var alwaysError = Enumerable.Repeat(
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Still broken", "Won't converge")]),
            maxIterations + 2).ToArray();

        var auditor = new ScriptedDeepAuditor(AuditorName, alwaysError);
        var (svc, rel, _) = await SetupAsync(auditor, maxIterations);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 10);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.NotNull(refreshed!.FailedReason);
        Assert.Contains(maxIterations.ToString(), refreshed.FailedReason);
    }

    [Fact]
    public async Task PersistentErrors_EmitsReleaseFailedWebhook()
    {
        const int maxIterations = 2;
        var alwaysError = Enumerable.Repeat(
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Error", "Error")]),
            maxIterations + 2).ToArray();

        var auditor = new ScriptedDeepAuditor(AuditorName, alwaysError);
        var (svc, rel, _) = await SetupAsync(auditor, maxIterations);
        var releaseFailed = new TaskCompletionSource<WebhookEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _webhooks.OnPublishAsync = (evt, _) =>
        {
            if (evt.Event == "release.failed")
                releaseFailed.TrySetResult(evt);
            return Task.CompletedTask;
        };

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 10);

        var failedEvent = await releaseFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(rel.Id, failedEvent.Release?.Id);
    }

    [Fact]
    public async Task PersistentErrors_MaxIterationsRemediationItemsCreated()
    {
        // With maxIterations=2 and persistent errors, expect 1 remediation item
        // (created after iter 1; iter 2 is the last attempt so no further item).
        const int maxIterations = 2;
        var alwaysError = Enumerable.Repeat(
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Error", "Error")]),
            maxIterations + 2).ToArray();

        var auditor = new ScriptedDeepAuditor(AuditorName, alwaysError);
        var (svc, rel, _) = await SetupAsync(auditor, maxIterations);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 10);

        // Count remediation work items (exclude the original seeded item).
        var allItems = new List<WorkItem>();
        await foreach (var wi in _workItemStore.ListByReleaseAsync(rel.Id, default))
            allItems.Add(wi);

        var remediationItems = allItems.Where(wi =>
            wi.Title.Contains("deep-audit", StringComparison.OrdinalIgnoreCase) ||
            wi.Title.Contains("remediation", StringComparison.OrdinalIgnoreCase)).ToList();

        // maxIterations=2: remediation after iter 1, then iter 2 hits max → fail (no item for iter 2).
        Assert.Single(remediationItems);
    }

    [Fact]
    public async Task PersistentErrors_WithSingleIteration_FailsImmediately()
    {
        // maxIterations=1: no remediation items created; fails on the first (and only) pass.
        const int maxIterations = 1;
        var auditor = new ScriptedDeepAuditor(AuditorName,
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Error", "Error")]));

        var (svc, rel, _) = await SetupAsync(auditor, maxIterations);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        var final = await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 5);

        Assert.Equal(ReleaseState.Failed, final);

        // No remediation items when maxIterations=1.
        var allItems = new List<WorkItem>();
        await foreach (var wi in _workItemStore.ListByReleaseAsync(rel.Id, default))
            allItems.Add(wi);

        Assert.DoesNotContain(allItems, wi =>
            wi.Title.Contains("deep-audit", StringComparison.OrdinalIgnoreCase) ||
            wi.Title.Contains("remediation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnexpectedDeepAuditFailure_FirstTerminalTransitionWriteFails_RetriesToDurableFailedState()
    {
        var retryDelay = TimeSpan.FromSeconds(30);
        var timeProvider = new ControllableTimeProvider();
        var releaseStore = new FailFirstTerminalTransitionReleaseStore(_releaseStore);
        var projects = new InMemoryProjectRepository(
            ReleaseTestHelper.EnabledProjectWithDeepAuditors(AuditorName, maxIterations: 1));
        var service = ReleaseTestHelper.BuildService(
            releaseStore,
            _workItemStore,
            projects,
            _webhooks,
            deepAuditors: [new ScriptedDeepAuditor(AuditorName)],
            sandboxes: new ThrowingCreateSandboxProvider(),
            gitHost: new DeepAuditTestGitHost(),
            deepAuditFailurePersistenceOptions: () => new DeepAuditFailurePersistenceOptions
            {
                MaxAttempts = 2,
                RetryDelay = retryDelay,
            },
            timeProvider: timeProvider);

        var release = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, branchName: "release/v1.0");
        await releaseStore.CreateAsync(release);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = release.ProjectId,
            Title = "seed item",
            Prompt = "do work",
            Agent = AgentKind.Claude,
            ReleaseId = release.Id,
        };
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));

        await service.OnWorkItemTerminalAsync(release.Id, default);
        await releaseStore.FirstTerminalTransitionFailure.WaitAsync(TimeSpan.FromSeconds(5));

        Release? persisted = null;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            timeProvider.Advance(retryDelay);
            persisted = await _releaseStore.GetAsync(release.Id);
            if (persisted?.State == ReleaseState.Failed)
                break;
            // The retry runs as a background continuation off the advanced timer.
            // A bare Task.Yield() re-queues immediately and hot-spins the thread
            // pool, starving that continuation under load; a short real delay
            // yields actual wall-clock time for it to complete and persist.
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.Equal(ReleaseState.Failed, persisted?.State);
        Assert.Equal(2, releaseStore.TerminalTransitionAttempts);
        Assert.Equal(
            "deep audit phase failed due to an internal credential or sandbox error",
            persisted?.FailedReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(ReleaseService svc, Release rel, WorkItem item)> SetupAsync(
        ScriptedDeepAuditor auditor,
        int maxIterations)
    {
        var projects = new InMemoryProjectRepository(
            ReleaseTestHelper.EnabledProjectWithDeepAuditors(AuditorName, maxIterations: maxIterations));
        var autoCompleteQueue = new AutoCompleteTaskQueue(_workItemStore);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            taskQueue: autoCompleteQueue,
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost());

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(rel);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "seed item",
            Prompt = "do work",
            Agent = AgentKind.Claude,
            ReleaseId = rel.Id,
        };
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));

        return (svc, rel, item);
    }

    private async Task<ReleaseState> PollUntilAsync(
        ReleaseId id,
        Func<ReleaseState, bool> predicate,
        int timeoutSeconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var r = await _releaseStore.GetAsync(id);
            if (r is not null && predicate(r.State))
                return r.State;
            await Task.Delay(20);
        }
        var final = await _releaseStore.GetAsync(id);
        return final?.State ?? ReleaseState.Open;
    }

    private sealed class ThrowingCreateSandboxProvider : ISandboxProvider
    {
        public string Name => "throwing-create";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated sandbox creation failure");

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailFirstTerminalTransitionReleaseStore : IReleaseStore
    {
        private readonly IReleaseStore _inner;
        private readonly TaskCompletionSource _firstTerminalTransitionFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _terminalTransitionAttempts;

        public FailFirstTerminalTransitionReleaseStore(IReleaseStore inner) => _inner = inner;

        public int TerminalTransitionAttempts => Volatile.Read(ref _terminalTransitionAttempts);
        public Task FirstTerminalTransitionFailure => _firstTerminalTransitionFailure.Task;

        public Task CreateAsync(Release release, CancellationToken ct = default)
            => _inner.CreateAsync(release, ct);

        public Task UpdateAsync(Release release, CancellationToken ct = default)
            => _inner.UpdateAsync(release, ct);

        public Task<Release?> GetAsync(ReleaseId id, CancellationToken ct = default)
            => _inner.GetAsync(id, ct);

        public Task<Release?> GetByNameAsync(
            ProjectId projectId,
            string name,
            CancellationToken ct = default)
            => _inner.GetByNameAsync(projectId, name, ct);

        public Task<IReadOnlyList<Release>> ListAsync(
            ProjectId? projectId = null,
            ReleaseState? state = null,
            int? limit = null,
            int? offset = null,
            CancellationToken ct = default)
            => _inner.ListAsync(projectId, state, limit, offset, ct);

        public Task<bool> TrySetBranchAsync(
            ReleaseId id,
            string branchName,
            string baseCommitSha,
            CancellationToken ct = default)
            => _inner.TrySetBranchAsync(id, branchName, baseCommitSha, ct);

        public Task<bool> TryTransitionStateAsync(
            Release release,
            ReleaseState expectedCurrentState,
            CancellationToken ct = default)
        {
            if (expectedCurrentState == ReleaseState.InReview && release.State == ReleaseState.Failed)
            {
                var attempt = Interlocked.Increment(ref _terminalTransitionAttempts);
                if (attempt == 1)
                {
                    _firstTerminalTransitionFailure.TrySetResult();
                    throw new IOException("simulated transient terminal transition write failure");
                }
            }

            return _inner.TryTransitionStateAsync(release, expectedCurrentState, ct);
        }

        public Task SaveAuditIterationAsync(
            ReleaseAuditIteration iteration,
            CancellationToken ct = default)
            => _inner.SaveAuditIterationAsync(iteration, ct);

        public Task<IReadOnlyList<ReleaseAuditIteration>> ListAuditIterationsAsync(
            ReleaseId releaseId,
            CancellationToken ct = default)
            => _inner.ListAuditIterationsAsync(releaseId, ct);
    }
}
