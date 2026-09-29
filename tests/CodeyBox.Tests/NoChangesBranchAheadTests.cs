using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for retried work preserved on the branch:
/// <list type="bullet">
/// <item>A work turn that commits nothing new while the work branch already
/// carries commits ahead of base advances to WorkComplete/audit as a normal
/// completion instead of failing with "Agent produced no changes to
/// commit".</item>
/// <item>The same rule covers rework turns: a no-commit rework re-audits the
/// existing branch instead of failing.</item>
/// <item>An early-ended turn (success, no commit, no completion summary) gets
/// one bounded "continue and finish" nudge in the same session before the
/// normal no-changes handling applies.</item>
/// </list>
/// </summary>
[Collection("Pipeline integration")]
public sealed class NoChangesBranchAheadTests : IDisposable
{
    private readonly string _workspace;

    public NoChangesBranchAheadTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-nochanges-ahead-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkTurn_NoNewCommit_BranchAhead_ProceedsToAudit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // Simulate a preserved branch from an earlier turn: the bare repo
        // already carries finished work ahead of base, and this pickup is an
        // explicit preserve (retry-from-work with branch preservation).
        var item = NewItem() with { PreserveWorkBranchOnQueuedPickup = true };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "prior.txt", "prior work\n", "prior work");

        // The agent verifies the existing work and exits cleanly without new
        // changes, WITH a completion summary (so no continue-nudge fires).
        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok", "verified: prior work already complete", null));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain("produced no changes", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(tp.Agent.WorkPrompts);
        Assert.Equal("prior work\n", await ShowAsync(barePath, $"{item.WorkBranch}:prior.txt"));
    }

    [Fact]
    public async Task WorkTurn_NoNewCommit_BranchEqual_FailsAsBefore()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // Fresh branch with no prior work: an empty turn is still a failure.
        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok", "did nothing, but with a summary", null));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final!.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(tp.Agent.WorkPrompts);
    }

    [Fact]
    public async Task EarlyEndedTurn_NudgedOnce_CompletesWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // First turn ends early: success, no diff, no completion summary.
        // The nudged continuation turn writes the file via the work plan.
        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("nudged.txt", "nudge completed\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(2, tp.Agent.WorkPrompts.Count);
        Assert.Contains("Continue and finish", tp.Agent.WorkPrompts[1], StringComparison.Ordinal);

        var barePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        Assert.Equal("nudge completed\n", await ShowAsync(barePath, $"{item.WorkBranch}:nudged.txt"));
    }

    [Fact]
    public async Task EarlyEndedTurn_NudgesDisabled_FailsAsNoProgress()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EarlyEndedTurnMaxNudges = 0,
        });
        using var tp = TestSupport.BuildPipeline(_workspace, seed, pipelineTuning: tuning);

        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final!.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(tp.Agent.WorkPrompts);
    }

    [Fact]
    public async Task ReworkTurn_NoNewCommit_BranchAhead_ReauditsExistingBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new OnceFailingAuditor();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2);
        tp.Agent.ResultStdout = "verified";
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        // Rework writes identical content: no new commit on an already-ahead
        // branch. The loop must re-audit (iteration 2 passes) instead of
        // failing or parking on the empty rework.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain("produced no changes", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, auditor.Calls);
    }

    private WorkItem NewItem()
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "no-changes branch-ahead",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = $"codeybox/{id.ToString()[..8]}",
            PushUpstream = false,
        };
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
        await TestSupport.RunGit(clone, "fetch", "origin");
        var refsOutput = (await TestSupport.RunGit(clone, "branch", "-r")).stdout;
        var baseRef = refsOutput.Contains($"origin/{branch}", StringComparison.Ordinal)
            ? $"origin/{branch}"
            : "origin/main";
        await TestSupport.RunGit(clone, "checkout", "-B", branch, baseRef);
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", subject);
        var sha = (await TestSupport.RunGit(clone, "rev-parse", "HEAD")).stdout.Trim();
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return sha;
    }

    private static async Task<string> ShowAsync(string repoPath, string rev)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, "show", rev);
        return stdout;
    }
}
