using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// The retry-from-work flow must reset the work branch in the bare repo back
/// to the base tip before re-invoking the agent. Without this reset, the
/// sandbox clone carries over the previous attempt's commits, the agent
/// observes "the work is already done", and the pipeline fails with
/// "Agent produced no changes to commit" — a fail-loud signal of a
/// fail-quiet state-leak bug.
/// </summary>
[Collection("Pipeline integration")]
public sealed class RetryFromWorkResetTests : IDisposable
{
    private readonly string _workspace;

    public RetryFromWorkResetTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-retry-from-work-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResetsWorkBranchToBaseSoAgentObservesPristineState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // Simulate a prior failed attempt: WI is now back in Queued, but the
        // bare repo still has the previous attempt's commits on its work branch.
        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");
        var staleAttemptSha = await CommitToBareBranchAsync(
            barePath, item.WorkBranch!, "stale-attempt.txt", "prior failed attempt\n", "prior attempt");
        Assert.NotEqual(baseTip, staleAttemptSha);

        // Before running, assert the bare repo really does have the stale work-branch tip.
        Assert.Equal(staleAttemptSha, await RevParseAsync(barePath, item.WorkBranch!));

        string? observedHeadAtAgentInvocation = null;
        string? observedStalePresent = null;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var head = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "rev-parse", "HEAD"],
            }, ct);
            observedHeadAtAgentInvocation = head.Stdout.Trim();

            var stale = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "test -f \"$1/stale-attempt.txt\" && echo yes || echo no", "sh", workingDirectory],
            }, ct);
            observedStalePresent = stale.Stdout.Trim();
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fresh.txt", "fresh agent output\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The agent saw a pristine base — no stale file, HEAD at base tip.
        Assert.Equal("no", observedStalePresent);
        Assert.Equal(baseTip, observedHeadAtAgentInvocation);

        // The new work-branch tip is built on base, not on the stale attempt.
        var newTip = await RevParseAsync(barePath, item.WorkBranch!);
        Assert.NotEqual(staleAttemptSha, newTip);
        // The first parent of the work tip must be the base tip (not the stale attempt).
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("fresh agent output\n", await ShowAsync(barePath, $"{item.WorkBranch}:fresh.txt"));
    }

    [Fact]
    public async Task FreshWorkItemWithoutExistingWorkBranchIsUnaffected()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("new.txt", "new item\n"));

        var item = NewItem();
        var baseTip = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("new item\n", await ShowAsync(barePath, $"{item.WorkBranch}:new.txt"));
    }

    [Fact]
    public async Task ConsecutiveRetriesEachResetIndependently()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");

        // Two distinct prior-attempt states, each retried back to Queued.
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "attempt-1.txt", "attempt 1\n", "attempt 1");
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "attempt-2.txt", "attempt 2\n", "attempt 2");
        var preRunTip = await RevParseAsync(barePath, item.WorkBranch!);
        Assert.NotEqual(baseTip, preRunTip);

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            // Agent must not observe either of the prior attempts.
            foreach (var stale in new[] { "attempt-1.txt", "attempt-2.txt" })
            {
                var probe = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["test", "-f", $"{workingDirectory}/{stale}"],
                }, ct);
                if (probe.Success)
                    throw new InvalidOperationException($"agent saw stale file '{stale}' after retry-from-work reset");
            }
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("final.txt", "final attempt\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("final attempt\n", await ShowAsync(barePath, $"{item.WorkBranch}:final.txt"));
    }

    [Fact]
    public async Task ResumeFromAuditPhasePreservesWorkBranch()
    {
        // Retry-from-audit (entry = WorkComplete, not Queued) must NOT reset
        // the work branch. The existing rebase keeps prior phase commits.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var priorTip = await CommitToBareBranchAsync(
            barePath, item.WorkBranch!, "work.txt", "work complete\n", "work commit");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(priorTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal("work complete\n", await ShowAsync(barePath, $"{item.WorkBranch}:work.txt"));
    }

    [Fact]
    public async Task ExplicitNonOwnedWorkBranchIsResetOnQueuedEntry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var item = NewItem("feature/operator-managed");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");
        var existingTip = await CommitToBareBranchAsync(
            barePath, item.WorkBranch!, "operator.txt", "operator content\n", "operator setup");

        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent addition\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotEqual(existingTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.NotEqual(0, (await TestSupport.RunGitNoThrow(barePath, "show", $"{item.WorkBranch}:operator.txt")).code);
        Assert.NotEqual(0, (await TestSupport.RunGitNoThrow(barePath, "show", "main:operator.txt")).code);
        Assert.Equal("agent addition\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task MissingWorkBranchResetFailureFailsItemInsteadOfLeavingQueued()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var item = NewItem("feature/missing-reset-failure");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var lockPath = Path.Combine(barePath, "refs", "heads", "feature", "missing-reset-failure.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await File.WriteAllTextAsync(lockPath, "stale lock\n");

        var agentInvoked = false;
        tp.Agent.BeforeWorkAsync = (_, _, _) =>
        {
            agentInvoked = true;
            return Task.CompletedTask;
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("should-not-run.txt", "should not run\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("git update-ref to create", final.LastError);
        Assert.Contains(item.WorkBranch!, final.LastError);
        Assert.False(agentInvoked);
    }

    private async Task<string> CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        // `clone` already fetched every branch ref under origin/*, so if
        // <branch> exists on origin we can branch off origin/<branch>; if
        // not, branch off main.
        await TestSupport.RunGit(clone, "fetch", "origin");
        var refsOutput = (await TestSupport.RunGit(clone, "branch", "-r")).stdout;
        var baseRef = refsOutput.Contains($"origin/{branch}", StringComparison.Ordinal)
            ? $"origin/{branch}"
            : "origin/main";
        await TestSupport.RunGit(clone, "checkout", "-B", branch, baseRef);
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        var sha = (await TestSupport.RunGit(clone, "rev-parse", "HEAD")).stdout.Trim();
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return sha;
    }

    private static async Task<string> ShowAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "show", rev);
        return stdout;
    }

    private static async Task<string> RevParseAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "rev-parse", rev);
        return stdout.Trim();
    }

    private static WorkItem NewItem(string? workBranch = null)
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "retry-test",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = workBranch ?? $"codeybox/{id.ToString()[..8]}",
            PushUpstream = false,
        };
    }
}
