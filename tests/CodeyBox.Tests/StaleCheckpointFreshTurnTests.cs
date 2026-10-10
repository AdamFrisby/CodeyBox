using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

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
        using var setup = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace),
            agentOverride: agent,
            logger: logger);

        var item = NewItem();
        await setup.Store.CreateAsync(item);
        await PreemptFirstTurnAsync(setup, item, ready);

        // Simulate a pickup-time rebase onto a fresh base plus a
        // restart-recovery recomposition: the base advances and the work
        // branch is recomposed at the fresh tip. The checkpoint still
        // descends from the old base, not from this tip.
        var freshBaseTip = await RecomposeBranchOnFreshBaseAsync(setup, item);
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
    public async Task StaleBranchMove_WithRetainedSandboxLease_RefusedWithExplicitMessage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var setup = TestSupport.BuildPipeline(_workspace, seed);
        const int promptRevision = 7;
        var lease = new SandboxRecoveryLease(
            "incus",
            "codeybox-retained-stale",
            "retained-stale-token");
        var checkpoint = new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/acct-a",
            modelId: null,
            reasoningMode: null,
            nativeSessionId: null,
            WorkItemState.Working,
            AgentTurnResumePhase.Work,
            iteration: null,
            promptRevision: promptRevision,
            DateTimeOffset.UtcNow);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "stale lease",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = "feature/stale-lease-" + Guid.NewGuid().ToString("N")[..8],
            Agent = AgentKind.Claude,
            AgentInstanceId = "claude/acct-a",
            PromptRevision = promptRevision,
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            PreemptedAt = DateTimeOffset.UtcNow,
            AgentTurnResumeCheckpoint = checkpoint,
            AgentTurnRecoveryLease = lease,
        };
        await setup.Store.CreateAsync(item);
        await setup.GitHost.EnsureRepositoryAsync(item.Id, seed);

        // Same situation as the discard test: fresh base plus a recomposed
        // work branch. The lease must still be refused explicitly rather
        // than silently discarded as stale bookkeeping.
        await RecomposeBranchOnFreshBaseAsync(setup, item);

        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("retained-sandbox", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no longer descends", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lease, final.AgentTurnRecoveryLease);
        Assert.NotNull(final.AgentTurnResumeCheckpoint);
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
    /// branch at the fresh tip, returning the fresh base tip SHA.
    /// </summary>
    private async Task<string> RecomposeBranchOnFreshBaseAsync(TestPipeline setup, WorkItem item)
    {
        var barePath = BarePathFor(setup, item);
        var clone = Path.Combine(_workspace, "rebase-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", "-q", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "rebase@example.invalid");
        await TestSupport.RunGit(clone, "config", "user.name", "Rebase Writer");
        await TestSupport.RunGit(clone, "checkout", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(clone, "base2.txt"), "fresh base\n");
        await TestSupport.RunGit(clone, "add", "base2.txt");
        await TestSupport.RunGit(clone, "commit", "-q", "-m", "fresh base commit");
        await TestSupport.RunGit(clone, "push", "-q", "origin", "HEAD:main");
        var (_, freshBaseTip, _) = await TestSupport.RunGit(clone, "rev-parse", "HEAD");
        await TestSupport.RunGit(clone, "checkout", "-q", "-B", item.WorkBranch!, "main");
        await TestSupport.RunGit(clone, "push", "-q", "origin", $"HEAD:{item.WorkBranch}");
        return freshBaseTip.Trim();
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
}
