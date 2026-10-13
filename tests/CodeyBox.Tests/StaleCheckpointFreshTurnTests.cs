using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// A durable agent-turn checkpoint is bookkeeping bound to the pre-turn
/// work-branch tip. When the branch legitimately moves between dispatches —
/// a pickup-time rebase onto a fresh base or a restart-recovery recomposition —
/// the checkpoint no longer descends from the current tip and is unusable, but
/// that says nothing about the work. The pipeline must discard the stale
/// checkpoint and run a normal fresh turn from the current tip (branch
/// preserved, no terminal failure). A retained-sandbox recovery lease is
/// provider-owned state and must still be refused explicitly, never silently
/// discarded. Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class StaleCheckpointFreshTurnTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-stale-checkpoint-").FullName;

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
    public async Task StaleCheckpoint_AfterBranchRebase_DiscardedAndFreshTurnRuns()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
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
            else
            {
                var write = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "echo 'fresh change' > \"$0\"", $"{workingDirectory}/fresh.txt"],
                }, ct);
                if (!write.Success)
                    throw new InvalidOperationException($"failed to write fresh.txt: {write.Stderr}");
            }
        };
        agent.WorkResults.Enqueue(new AgentResult(true, "fresh ok", "fresh ok", null));

        var logger = new CapturingLogger<PipelineRunner>();
        var seedPath = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            seedPath,
            agentOverride: agent,
            logger: logger);

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        // Simulate a pickup-time rebase onto a fresh base plus a
        // restart-recovery recomposition: the base advances and the work
        // branch is recomposed at the fresh tip. The checkpoint still
        // descends from the old base, not from this tip.
        var freshBaseTip = await RecomposeBranchOnFreshBaseAsync(setup, item, seedPath);
        var barePath = BarePathFor(setup, item);
        var (staleCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", await TipOfAsync(barePath, item.WorkBranch!), await CheckpointCommitOfAsync(setup, item));
        Assert.Equal(1, staleCode);

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.FailureKind);
        Assert.Equal(0, final.TerminalFailureCount);

        // The stale bookkeeping is gone and the fresh turn ran: two agent
        // dispatches (interrupted + fresh), and the fresh prompt is a normal
        // work prompt rather than a checkpoint-resume prompt.
        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Null(final.AgentTurnRecoveryLease);
        Assert.Equal(2, agent.WorkPrompts.Count);
        Assert.DoesNotContain("codeybox/preempt", agent.WorkPrompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            logger.Entries,
            e => e.Message.Contains(
                "Discarding durable agent-turn resume checkpoint",
                StringComparison.OrdinalIgnoreCase));

        // The branch was preserved, not reset: the final tip still descends
        // from the recomposed fresh base, carries the fresh turn's output,
        // and never restored the discarded checkpoint's tree.
        var (ancestorCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", freshBaseTip, item.WorkBranch!);
        Assert.Equal(0, ancestorCode);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("fresh.txt", tree);
        Assert.Contains("base2.txt", tree);
        Assert.DoesNotContain("output.txt", tree);
    }

    [Fact]
    public async Task StaleCheckpoint_WithAdvancedTipAfterRecompose_DiscardedAndFreshTurnStagesAtCurrentTip()
    {
        // Combination of a stale checkout tip (an outside writer lands a
        // commit on the recomposed branch, as in the same-branch-advance
        // recovery) plus a stale checkpoint (the base moved under it): the
        // tip advance must not resurrect the stale resume. The checkpoint is
        // still discarded and the fresh turn stages at the CURRENT tip,
        // preserving the recomposed base, the landed commit, and the fresh
        // turn's output — never the discarded checkpoint's tree.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
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
            else
            {
                var write = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "echo 'fresh change' > \"$0\"", $"{workingDirectory}/fresh.txt"],
                }, ct);
                if (!write.Success)
                    throw new InvalidOperationException($"failed to write fresh.txt: {write.Stderr}");
            }
        };
        agent.WorkResults.Enqueue(new AgentResult(true, "fresh ok", "fresh ok", null));

        var logger = new CapturingLogger<PipelineRunner>();
        var seedPath = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            seedPath,
            agentOverride: agent,
            logger: logger);

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        var freshBaseTip = await RecomposeBranchOnFreshBaseAsync(setup, item, seedPath);
        var rivalTip = await AdvanceBranchAsync(setup, item, "rival.txt", "rival change\n", "rival advance");
        var barePath = BarePathFor(setup, item);
        var (staleCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", await TipOfAsync(barePath, item.WorkBranch!), await CheckpointCommitOfAsync(setup, item));
        Assert.Equal(1, staleCode);

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.FailureKind);
        Assert.Equal(0, final.TerminalFailureCount);

        Assert.Null(final.PreemptCheckpoint);
        Assert.Null(final.AgentTurnResumeCheckpoint);
        Assert.Null(final.AgentTurnRecoveryLease);
        Assert.Equal(2, agent.WorkPrompts.Count);
        Assert.DoesNotContain("codeybox/preempt", agent.WorkPrompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            logger.Entries,
            e => e.Message.Contains(
                "Discarding durable agent-turn resume checkpoint",
                StringComparison.OrdinalIgnoreCase));

        // The fresh turn staged at the moved tip (not at the recomposed base
        // alone): the tip advance survived the discard alongside the base.
        Assert.Contains(
            logger.Entries,
            e => e.Message.Contains(
                "at current bare-repo tip",
                StringComparison.OrdinalIgnoreCase));
        var (baseAncestorCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", freshBaseTip, item.WorkBranch!);
        Assert.Equal(0, baseAncestorCode);
        var (rivalAncestorCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", rivalTip, item.WorkBranch!);
        Assert.Equal(0, rivalAncestorCode);
        var (_, tree, _) = await TestSupport.RunGit(barePath, "ls-tree", "-r", item.WorkBranch!, "--name-only");
        Assert.Contains("fresh.txt", tree);
        Assert.Contains("base2.txt", tree);
        Assert.Contains("rival.txt", tree);
        Assert.DoesNotContain("output.txt", tree);
    }

    [Fact]
    public async Task StaleBranchMove_WithRetainedSandboxLease_RefusedWithExplicitMessage()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = AgentKind.Claude };
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
            else
            {
                var write = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "echo 'fresh change' > \"$0\"", $"{workingDirectory}/fresh.txt"],
                }, ct);
                if (!write.Success)
                    throw new InvalidOperationException($"failed to write fresh.txt: {write.Stderr}");
            }
        };
        agent.WorkResults.Enqueue(new AgentResult(true, "fresh ok", "fresh ok", null));

        var sandboxHook = new LeaseInjectingSandboxProvider(
            new ProcessSandboxProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessSandboxProvider>.Instance));
        var logger = new CapturingLogger<PipelineRunner>();
        var seedPath = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            seedPath,
            agentOverride: agent,
            logger: logger,
            sandboxProvider: sandboxHook);
        sandboxHook.Store = setup.Store;

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        // Same situation as the discard test: fresh base plus a recomposed
        // work branch, so the durable checkpoint no longer descends from the
        // current tip. A concurrent provider-side retain publishes a
        // retained-sandbox recovery lease inside the stale-check window; the
        // discard must refuse explicitly rather than silently drop
        // provider-owned state.
        var barePath = BarePathFor(setup, item);
        await RecomposeBranchOnFreshBaseAsync(setup, item, seedPath);
        var staleCheckpointCommit = await CheckpointCommitOfAsync(setup, item);
        var (staleCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", await TipOfAsync(barePath, item.WorkBranch!), staleCheckpointCommit);
        Assert.Equal(1, staleCode);

        var lease = new SandboxRecoveryLease(
            "incus",
            "codeybox-retained-stale",
            "retained-stale-token");
        sandboxHook.TargetId = item.Id;
        sandboxHook.LeaseToInject = lease;

        var resumeEntry = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared after preempt");
        await setup.Pipeline.RunAsync(resumeEntry, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains(
            "Cannot discard a retained-sandbox recovery lease",
            final.LastError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no longer descends", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lease, final.AgentTurnRecoveryLease);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
        Assert.Null(final.PreemptCheckpoint);

        // The stale bookkeeping was entered but refused, not silently
        // discarded: the branch tip still does not descend to the checkpoint
        // commit that was stale at dispatch.
        var (stillStaleCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", await TipOfAsync(barePath, item.WorkBranch!), staleCheckpointCommit);
        Assert.Equal(1, stillStaleCode);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Test stale checkpoint",
        Prompt = "write output.txt",
        WorkBranch = "feature/stale-checkpoint-" + Guid.NewGuid().ToString("N")[..8],
    };

    private static string BarePathFor(TestPipeline setup, WorkItem item) =>
        Path.Combine(setup.GitRoot, item.Id + ".git");

    /// <summary>
    /// Runs the first turn until the agent holds its tree, preempts via host
    /// shutdown, and asserts a typed checkpoint was recorded.
    /// </summary>
    private static async Task PreemptFirstTurnAsync(
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
    }

    /// <summary>
    /// Advances the base branch with a fresh commit and recomposes the work
    /// branch at the fresh tip, returning the fresh base tip SHA. The fresh
    /// commit lands in the UPSTREAM seed repo first: the pipeline refreshes
    /// the bare base branch from upstream on every entry, so a base advance
    /// pushed only to the bare repo would be reverted before the resume runs
    /// and the recomposed tip would not survive to the stale check.
    /// </summary>
    private async Task<string> RecomposeBranchOnFreshBaseAsync(TestPipeline setup, WorkItem item, string seedPath)
    {
        await File.WriteAllTextAsync(Path.Combine(seedPath, "base2.txt"), "fresh base\n");
        await TestSupport.RunGit(seedPath, "add", "base2.txt");
        await TestSupport.RunGit(seedPath, "commit", "-q", "-m", "fresh base commit");
        var (_, freshBaseTipOut, _) = await TestSupport.RunGit(seedPath, "rev-parse", "HEAD");
        var freshBaseTip = freshBaseTipOut.Trim();

        var barePath = BarePathFor(setup, item);
        var clone = Path.Combine(_workspace, "rebase-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", "-q", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "rebase@example.invalid");
        await TestSupport.RunGit(clone, "config", "user.name", "Rebase Writer");
        // Carry the fresh upstream base into the bare repo (fast-forward, so
        // the entry refresh below is a no-op) and recompose the work branch
        // at the fresh tip.
        await TestSupport.RunGit(clone, "fetch", "-q", seedPath, "main");
        await TestSupport.RunGit(clone, "checkout", "-q", "-B", item.WorkBranch!, "FETCH_HEAD");
        await TestSupport.RunGit(clone, "push", "-q", "origin", "HEAD:main");
        await TestSupport.RunGit(clone, "push", "-q", "origin", $"HEAD:{item.WorkBranch}");
        return freshBaseTip;
    }

    /// <summary>
    /// Lands one outside-writer commit on top of the existing work branch,
    /// returning the new tip SHA.
    /// </summary>
    private async Task<string> AdvanceBranchAsync(
        TestPipeline setup, WorkItem item, string file, string content, string message)
    {
        var barePath = BarePathFor(setup, item);
        var clone = Path.Combine(_workspace, "advance-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", "-q", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "advance@example.invalid");
        await TestSupport.RunGit(clone, "config", "user.name", "Advance Writer");
        await TestSupport.RunGit(clone, "fetch", "-q", "origin");
        await TestSupport.RunGit(clone, "checkout", "-q", "-B", item.WorkBranch!, $"origin/{item.WorkBranch}");
        await File.WriteAllTextAsync(Path.Combine(clone, file), content);
        await TestSupport.RunGit(clone, "add", file);
        await TestSupport.RunGit(clone, "commit", "-q", "-m", message);
        await TestSupport.RunGit(clone, "push", "-q", "origin", $"HEAD:{item.WorkBranch}");
        var (_, tip, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
        return tip.Trim();
    }

    private static async Task<string> TipOfAsync(string barePath, string branch)
    {
        var (_, sha, _) = await TestSupport.RunGit(barePath, "rev-parse", branch);
        return sha.Trim();
    }

    private static async Task<string> CheckpointCommitOfAsync(TestPipeline setup, WorkItem item)
    {
        var stored = await setup.Store.GetAsync(item.Id)
            ?? throw new InvalidOperationException("work item disappeared");
        var checkpointRef = AgentTurnCheckpointRef.Parse(stored.PreemptCheckpoint!);
        return checkpointRef.SourceCommitSha;
    }

    /// <summary>
    /// Simulates a concurrent provider-side retain landing inside the
    /// stale-check window: on the first fetch of the durable preempt ref (the
    /// stale check's fetch, after pickup validation has passed), the
    /// lease is published to the real store via a normal update, so the
    /// discard's re-read observes provider-owned state and must refuse.
    /// Capability resolution unwraps through <see cref="ISandboxDecorator"/>,
    /// so the inner process sandbox's capabilities stay intact.
    /// </summary>
    private sealed class LeaseInjectingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
    {
        public CodeyBox.Orchestrator.SqliteWorkItemStore? Store { get; set; }

        public WorkItemId TargetId { get; set; }

        public SandboxRecoveryLease? LeaseToInject { get; set; }

        private int _injected;

        public string Name => inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var sandbox = await inner.CreateAsync(spec, ct).ConfigureAwait(false);
            return new LeaseInjectingSandbox(sandbox, this);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
            inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            inner.DisposeLeakedAsync(name, ct);

        internal async Task MaybeInjectAsync(IReadOnlyList<string> argv)
        {
            if (LeaseToInject is null || Store is null)
                return;
            if (!argv.Contains("fetch"))
                return;
            if (!argv.Any(a => a.Contains("codeybox/preempt", StringComparison.Ordinal)))
                return;
            if (Interlocked.Exchange(ref _injected, 1) == 1)
                return;
            var current = await Store.GetAsync(TargetId).ConfigureAwait(false);
            if (current is null || current.AgentTurnRecoveryLease is not null)
                return;
            await Store.UpdateAsync(
                current with
                {
                    AgentTurnRecoveryLease = LeaseToInject,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ConfigureAwait(false);
        }
    }

    private sealed class LeaseInjectingSandbox(ISandbox inner, LeaseInjectingSandboxProvider owner) : ISandboxDecorator
    {
        public ISandbox InnerSandbox => inner;

        public string Id => inner.Id;

        public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv is not null)
                await owner.MaybeInjectAsync(exec.Argv).ConfigureAwait(false);
            return await inner.ExecAsync(exec, ct).ConfigureAwait(false);
        }

        public Task SyncStateToHostAsync(CancellationToken ct = default) =>
            inner.SyncStateToHostAsync(ct);

        public Task KillActiveExecsAsync(CancellationToken ct = default) =>
            inner.KillActiveExecsAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
