using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the pre-merge canonical-base refresh — the
/// stale-base guard added in this work item. The per-work-item bare repo
/// is created at dispatch time and its local base ref is a snapshot of
/// upstream main at THAT moment; sibling work items that landed commits
/// since then have moved the canonical upstream tip. Without the refresh,
/// the merge phase agent composes a merge against the stale fork-point,
/// producing a mergeSha whose first-parent ancestry omits everything
/// sibling work landed — which then silently reverts that work when
/// the merge commit is published to upstream.
///
/// The test exercises the happy path described in the work item:
///   (a) bare repo forked from main@A,
///   (b) a sibling work item lands B onto upstream main,
///   (c) the agent's work branch then enters the merge phase.
/// Acceptance criterion: the resulting merge either fast-forwards over B
/// or refuses — never silently reverts B.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PreMergeCanonicalBaseRefreshTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-prerefresh-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = true,
    };

    [Fact]
    public async Task StaleBareRepoMain_IsRefreshedFromCanonical_AndMergeIncludesSiblingCommit()
    {
        // (a) Seed has only A. The bare repo for this work item will be
        // cloned from the seed at dispatch time → local main = A.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The fake upstream remote refreshes the bare repo's local main
        // from the seed when FetchBaseBranchAsync is called. The pre-merge
        // refresh path will trigger this fetch with the seed's tip-at-that-
        // moment — which by then includes the sibling commit B.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            // No race responses programmed — merge phase succeeds first
            // try, so the only fetch is the pre-merge canonical refresh.
        };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "merge",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;

        // (b) A sibling work item lands B onto upstream main BETWEEN bare
        // repo creation (during EnsureRepositoryAsync at the top of
        // RunAsync) and the merge phase. Hook in via BeforeWorkAsync: the
        // sibling commit is staged on the seed when the work agent runs,
        // but the bare repo's local main still points at A. The pre-merge
        // refresh — running between work phase and merge phase — fetches B
        // from the seed and overwrites the bare repo's local main.
        tp.Agent.BeforeWorkAsync = async (_, _, ct) =>
        {
            await File.WriteAllTextAsync(Path.Combine(seed, "sibling.txt"),
                "B was landed by a sibling work item\n", ct);
            await TestSupport.RunGit(seed, "add", "sibling.txt");
            await TestSupport.RunGit(seed, "commit", "-m", "sibling B");
        };

        // (c) Work agent commits a file that doesn't touch sibling.txt, so
        // the merge against refreshed main is conflict-free.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work change\n"));

        var item = NewItem("feature/stale-base-guard");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.MergeSha);

        // Pre-merge canonical-base refresh ran exactly once: there was no
        // race response programmed, so the race-recovery refetch never
        // fires. A regression that skipped the refresh would leave this at 0.
        Assert.Equal(1, remote.FetchCalls);

        // The merge sha's tree must contain BOTH the sibling commit's file
        // (proves we fast-forwarded over B) AND the work branch's file
        // (proves we still applied the agent's work). A stale-base merge
        // would have produced a tree without sibling.txt — exactly the
        // silent-revert bug class this guard exists to prevent.
        var barePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        var (siblingExit, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "cat-file", "-e", $"{final.MergeSha}:sibling.txt");
        Assert.Equal(0, siblingExit);
        var (workExit, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "cat-file", "-e", $"{final.MergeSha}:work.txt");
        Assert.Equal(0, workExit);

        // The refreshed seed tip must be an ancestor of the merge commit —
        // i.e. the merge composed against the post-refresh main rather than
        // the original fork-point. Without this ancestry the push to
        // upstream would reject as non-fast-forward (or, worse with auto-
        // merge, silently revert).
        var (seedTipExit, seedTip, _) = await TestSupport.RunGit(
            seed, "rev-parse", "HEAD");
        Assert.Equal(0, seedTipExit);
        var (ancestryExit, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", seedTip.Trim(), final.MergeSha);
        Assert.Equal(0, ancestryExit);
    }

    [Fact]
    public async Task NoopUpstream_DoesNotInvokeCanonicalRefresh()
    {
        // Inverse coverage for the noop exemption documented in the work
        // item: noop projects (jobtrack-cli style) intentionally keep the
        // independent-attempt model. The pre-merge refresh must not fire
        // when project.Upstream.Kind == "noop"; otherwise this test would
        // see a FetchCalls bump on a remote that has no canonical tip.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
        };
        // Even though we wire a factory that returns the racing remote,
        // the Kind="noop" check inside the pipeline gates the refresh
        // away before reaching the upstream. A regression that removed
        // that guard would call FetchBaseBranchAsync against this remote
        // and bump FetchCalls.
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: ProjectUpstream.Noop,
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));

        var item = NewItem("feature/noop-no-refresh") with { PushUpstream = false };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, remote.FetchCalls);
    }
}
