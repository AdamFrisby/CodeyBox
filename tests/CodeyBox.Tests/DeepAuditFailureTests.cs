using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

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
}
