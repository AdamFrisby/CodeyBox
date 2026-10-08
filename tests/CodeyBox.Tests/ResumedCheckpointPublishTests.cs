using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the resumed meaningful-checkpoint/no-new-change
/// publication path (<c>PipelineRunner.WorkPhase</c>'s
/// <c>resumingPreempt && checkpointContainedMeaningfulChanges</c> branch):
/// a real preemption (host shutdown while the agent holds the tree) followed
/// by a resume whose agent produces no new commits. The rival advance lands
/// mid-turn — after pre-turn resolution but before publication — which is the
/// only shape that reaches publication with a non-fast-forward target while
/// keeping checkpoint ancestry valid. Proves the reconciled push, checkpoint
/// clearing only after confirmed durability, typed conflict propagation, and
/// checkpoint retention on failure or sync loss. Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class ResumedCheckpointPublishTests : IDisposable
{
    private readonly string _workspace;

    public ResumedCheckpointPublishTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-resume-publish-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best-effort test teardown.
        }
    }

    [Fact]
    public async Task ResumedCheckpoint_FastForward_PublishesAndClearsCheckpoint()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = NewResumeAgent(ready, midTurnAdvance: null);
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent);

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);

        var barePath = BarePathFor(setup, item);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("output.txt", tree);
    }

    [Fact]
    public async Task ResumedCheckpoint_DivergedTarget_ReconcilesAndPreservesBothSides()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rivalTip = null;
        var agent = NewResumeAgent(
            ready,
            midTurnAdvance: async () =>
            {
                rivalTip = await AdvanceBareBranchAsync(
                    _workspace, _rivalScope.BareGitRoot, _rivalScope.ItemId, _rivalScope.Branch,
                    "rival.txt", "rival change\n", "rival advance");
            });
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent);

        var item = NewItem();
        _rivalScope = (setup.GitRoot, item.Id, item.WorkBranch!);
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        Assert.NotNull(rivalTip);
        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);

        var barePath = BarePathFor(setup, item);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("output.txt", tree);
        Assert.Contains("rival.txt", tree);
        // Target history retained: the rival tip is an ancestor of the
        // reconciled branch tip (a force-push/clobber would still show both
        // files but would not preserve this ancestry... and the publisher
        // never force-pushes by construction).
        var (code, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", rivalTip!, item.WorkBranch!);
        Assert.Equal(0, code);

        // The reconciled tip went through the normal gates after publication:
        // it was merged to the base branch with both sides' content.
        var (_, mainTree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", "main", "--name-only");
        Assert.Contains("output.txt", mainTree);
        Assert.Contains("rival.txt", mainTree);
    }

    [Fact]
    public async Task ResumedCheckpoint_ConflictingTarget_FailsTypedAndRetainsCheckpoint()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rivalTip = null;
        var agent = NewResumeAgent(
            ready,
            midTurnAdvance: async () =>
            {
                rivalTip = await AdvanceBareBranchAsync(
                    _workspace, _rivalScope.BareGitRoot, _rivalScope.ItemId, _rivalScope.Branch,
                    "output.txt", "rival change\n", "rival advance");
            });
        var logger = new CapturingLogger<PipelineRunner>();
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: tracker,
            agentOverride: agent,
            logger: logger);

        var item = NewItem();
        _rivalScope = (setup.GitRoot, item.Id, item.WorkBranch!);
        await setup.Store.CreateAsync(item);
        var checkpointRef = await PreemptFirstTurnAsync(setup, item, ready);

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        Assert.NotNull(rivalTip);
        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final.FailureKind);
        Assert.Contains("conflict", final.LastError, StringComparison.OrdinalIgnoreCase);

        // The checkpoint is retained for operator retry: the ref still
        // resolves and still carries the agent's completed output.
        Assert.False(string.IsNullOrWhiteSpace(final.PreemptCheckpoint));
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        var barePath = BarePathFor(setup, item);
        var (revCode, revOut, _) = await TestSupport.RunGitNoThrow(
            barePath, "rev-parse", "--verify", checkpointRef);
        Assert.Equal(0, revCode);
        Assert.False(string.IsNullOrWhiteSpace(revOut));
        var (_, checkpointTree, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", checkpointRef, "--name-only");
        Assert.Contains("output.txt", checkpointTree);

        // The rival target is untouched: the branch tip is still the rival tip.
        var (_, branchTip, _) = await TestSupport.RunGit(barePath, "rev-parse", item.WorkBranch!);
        Assert.Equal(rivalTip!.Trim(), branchTip.Trim());
    }

    [Fact]
    public async Task ResumedCheckpoint_SyncFailure_RetainsCheckpoint()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = NewResumeAgent(ready, midTurnAdvance: null);
        await using var tracker = TrackingSandboxProvider.ForProcessSandbox();
        var syncFailer = new SyncFailingSandboxProvider(tracker);
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            sandboxProvider: syncFailer,
            agentOverride: agent);

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        // The push itself succeeds; only the post-push host sync is lost, so
        // publication is uncertain and the checkpoint must be retained.
        syncFailer.FailSync = true;
        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final.FailureKind);
        Assert.Contains("sync", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(final.PreemptCheckpoint));
        Assert.NotNull(final.AgentTurnResumeCheckpoint);

        // The pushed tip is intact on the branch (nothing was rolled back or
        // half-committed), while the checkpoint stays available for a
        // deterministic retry once sync is healthy.
        var barePath = BarePathFor(setup, item);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("output.txt", tree);
    }

    // Set before the first run whenever the mid-turn hook needs bare-repo
    // coordinates (the setup's GitRoot is only known after BuildPipeline).
    private (string BareGitRoot, WorkItemId ItemId, string Branch) _rivalScope;

    private static ScriptedAgent NewResumeAgent(
        TaskCompletionSource ready,
        Func<Task>? midTurnAdvance)
    {
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var calls = 0;
        agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                var write = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "echo 'real change' > \"$0\"", $"{workingDirectory}/output.txt"],
                }, ct);
                if (!write.Success)
                    throw new InvalidOperationException($"failed to write output.txt: {write.Stderr}");
                ready.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            else if (midTurnAdvance is not null)
            {
                await midTurnAdvance();
            }
        };
        agent.WorkResults.Enqueue(new AgentResult(true, "resumed ok", "resumed ok", null));
        return agent;
    }

    /// <summary>
    /// Runs the first turn until the agent holds its tree, preempts via host
    /// shutdown, and returns the recorded checkpoint ref.
    /// </summary>
    private static async Task<string> PreemptFirstTurnAsync(
        TestPipeline setup, WorkItem item, TaskCompletionSource ready)
    {
        using var hostShutdown = new CancellationTokenSource();
        var runTask = setup.Pipeline.RunAsync(item, CancellationToken.None, hostShutdown.Token);

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await hostShutdown.CancelAsync();

        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }

        var afterPreempt = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared during preempt");
        Assert.False(
            string.IsNullOrWhiteSpace(afterPreempt.PreemptCheckpoint),
            "expected a preempt checkpoint ref to be recorded");
        Assert.NotNull(afterPreempt.AgentTurnResumeCheckpoint);
        return AgentTurnCheckpointRef.Parse(afterPreempt.PreemptCheckpoint!).Value;
    }

    private static async Task<string> AdvanceBareBranchAsync(
        string workspace,
        string gitRoot,
        WorkItemId itemId,
        string branch,
        string file,
        string content,
        string message)
    {
        var barePath = Path.Combine(gitRoot, itemId + ".git");
        var clone = Path.Combine(
            workspace, "rival-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(workspace, "clone", "-q", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "rival@example.invalid");
        await TestSupport.RunGit(clone, "config", "user.name", "Rival Writer");
        // The branch does not exist yet at resume entry (the interrupted turn
        // never published), so start it at the base tip — the same pre-turn
        // commit the resume will resolve — then advance it mid-turn.
        await TestSupport.RunGit(clone, "checkout", "-q", "-B", branch, "main");
        await File.WriteAllTextAsync(Path.Combine(clone, file), content);
        await TestSupport.RunGit(clone, "add", file);
        await TestSupport.RunGit(clone, "commit", "-q", "-m", message);
        await TestSupport.RunGit(clone, "push", "-q", "origin", $"HEAD:{branch}");
        var (_, sha, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
        return sha.Trim();
    }

    private static string BarePathFor(TestPipeline setup, WorkItem item) =>
        Path.Combine(setup.GitRoot, item.Id + ".git");

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Test resumed publication",
        Prompt = "write output.txt",
        WorkBranch = "feature/resume-publish-" + Guid.NewGuid().ToString("N")[..8],
    };

    private sealed class SyncFailingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
    {
        public bool FailSync { get; set; }

        public string Name => inner.Name;
        public SandboxIsolationLevel IsolationLevel => inner.IsolationLevel;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => inner.BatchLaunchMode;
        public IReadOnlyList<string> DeclaredCapabilities => inner.DeclaredCapabilities;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            new SyncFailingSandbox(await inner.CreateAsync(spec, ct), () => FailSync);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class SyncFailingSandbox(ISandbox inner, Func<bool> failSync)
        : ISandbox, ISandboxDecorator
    {
        public ISandbox InnerSandbox => inner;
        public string Id => inner.Id;

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            inner.ExecAsync(exec, ct);

        public Task SyncStateToHostAsync(CancellationToken ct = default) =>
            failSync()
                ? throw new InvalidOperationException("simulated SyncStateToHost failure for resumed-publication test")
                : inner.SyncStateToHostAsync(ct);
    }
}
