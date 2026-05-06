using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the deep-audit convergence loop in ReleaseService.RunDeepAuditPhaseAsync:
/// when auditors return blocking findings on iteration 1 and none on iteration 2,
/// the release transitions to Released and a remediation work item is dispatched
/// between iterations.
/// </summary>
public sealed class DeepAuditConvergenceTests : IDisposable
{
    private const string AuditorName = "test-convergence-auditor";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-conv-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();

    public DeepAuditConvergenceTests()
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
    public async Task ConvergenceLoop_ErrorsOnIter1_PassesOnIter2_TransitionsToReleased()
    {
        // Arrange: auditor fails on iter 1, passes on iter 2.
        var auditor = new ScriptedDeepAuditor(AuditorName,
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Test finding", "Must fix this")]),
            new AuditResult(true, []));

        var (svc, rel, _) = await SetupAsync(auditor, maxIterations: 3);

        // Act: trigger the convergence loop via the auto-transition path.
        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        // Wait for the background deep-audit phase to run to completion.
        var final = await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 5);

        // Assert
        Assert.Equal(ReleaseState.Released, final);
        Assert.Contains(_webhooks.Events, e => e.Event == "release.published");
    }

    [Fact]
    public async Task ConvergenceLoop_RemediationWorkItemDispatched_BetweenIterations()
    {
        // Arrange: auditor fails once then passes.
        var auditor = new ScriptedDeepAuditor(AuditorName,
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Bug", "Fix the bug")]),
            new AuditResult(true, []));

        var (svc, rel, _) = await SetupAsync(auditor, maxIterations: 3);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 5);

        // A remediation work item linked to the release must have been created.
        var allItems = new List<WorkItem>();
        await foreach (var wi in _workItemStore.ListByReleaseAsync(rel.Id, default))
            allItems.Add(wi);

        // Original seeded item + 1 remediation item from iteration 1.
        Assert.Contains(allItems, wi =>
            wi.Title.Contains("deep-audit", StringComparison.OrdinalIgnoreCase) ||
            wi.Title.Contains("remediation", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(_webhooks.Events, e => e.Event == "release.deep_audit_remediation_dispatched");
    }

    [Fact]
    public async Task ConvergenceLoop_IterationCompleteWebhookEmittedPerIteration()
    {
        var auditor = new ScriptedDeepAuditor(AuditorName,
            new AuditResult(false, [new AuditFinding(AuditorName, AuditSeverity.Error, "Issue", "Fix it")]),
            new AuditResult(true, []));

        var (svc, rel, _) = await SetupAsync(auditor, maxIterations: 3);

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 5);

        // One iteration-complete event per loop iteration (2 iterations: fail then pass).
        var iterEvents = _webhooks.Events
            .Where(e => e.Event == "release.deep_audit_iteration_complete")
            .ToList();
        Assert.True(iterEvents.Count >= 2, $"Expected ≥2 iteration-complete events, got {iterEvents.Count}");
    }

    [Fact]
    public async Task ConvergenceLoop_NoBranchName_FailsImmediately()
    {
        // Release with no branch name should fail without running auditors.
        var auditor = new ScriptedDeepAuditor(AuditorName);
        var projects = new InMemoryProjectRepository(
            ReleaseTestHelper.EnabledProjectWithDeepAuditors(AuditorName, maxIterations: 3));
        var autoCompleteQueue = new AutoCompleteTaskQueue(_workItemStore);
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore, _workItemStore, projects, _webhooks,
            deepAuditors: [auditor],
            taskQueue: autoCompleteQueue,
            sandboxes: new AlwaysSucceedSandboxProvider(),
            gitHost: new DeepAuditTestGitHost());

        // Release in Closed state, NO BranchName.
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, branchName: null);
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done);
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));

        await svc.OnWorkItemTerminalAsync(rel.Id, default);

        var final = await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 5);

        Assert.Equal(ReleaseState.Failed, final);
        Assert.Contains(_webhooks.Events, e => e.Event == "release.failed");
    }

    [Fact]
    public async Task DeepAuditNetworkCapability_AllowsNetworkWithoutAgentCredentials()
    {
        var auditor = new ScriptedDeepAuditor(
            AuditorName,
            AuditCapabilities.Network,
            new AuditResult(true, []));
        var project = ReleaseTestHelper.EnabledProjectWithDeepAuditors(AuditorName, maxIterations: 1) with
        {
            NetworkProfiles = new ProjectNetworkProfiles { AuditTool = "audit-tools" },
        };
        var projects = new InMemoryProjectRepository(project);
        var autoCompleteQueue = new AutoCompleteTaskQueue(_workItemStore);
        var sandboxes = new CapturingSandboxProvider();
        var svc = ReleaseTestHelper.BuildService(
            _releaseStore,
            _workItemStore,
            projects,
            _webhooks,
            deepAuditors: [auditor],
            taskQueue: autoCompleteQueue,
            sandboxes: sandboxes,
            gitHost: new DeepAuditTestGitHost(),
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "none",
                AgentAllowedHosts = ["api.anthropic.com"],
                AuditToolAllowedHosts = ["registry.npmjs.org"],
            });

        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed, branchName: "release/v1.0");
        await _releaseStore.CreateAsync(rel);
        var item = MakeWorkItem(rel.Id, WorkItemState.Done);
        await _workItemStore.CreateAsync(item);
        await _workItemStore.UpdateAsync(item.With(WorkItemState.Done));

        await svc.OnWorkItemTerminalAsync(rel.Id, default);
        await PollUntilAsync(rel.Id,
            s => s is ReleaseState.Released or ReleaseState.Failed,
            timeoutSeconds: 5);

        var spec = Assert.Single(sandboxes.Specs);
        Assert.Contains("registry.npmjs.org", spec.Network.AllowedHosts);
        Assert.Equal("audit-tools", spec.Network.ProfileName);
        Assert.DoesNotContain(spec.Mounts, m => m.SandboxPath == SandboxConventions.CredentialsDir);
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
        var item = MakeWorkItem(rel.Id, WorkItemState.Done);
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

    private static WorkItem MakeWorkItem(ReleaseId releaseId, WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "seed item",
        Prompt = "do work",
        Agent = AgentKind.Claude,
        ReleaseId = releaseId,
    };

    private sealed class CapturingSandboxProvider : ISandboxProvider
    {
        public List<SandboxSpec> Specs { get; } = [];
        public string Name => "capturing";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            Specs.Add(spec);
            return Task.FromResult<ISandbox>(new AlwaysSucceedSandbox());
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => Task.CompletedTask;
    }
}
