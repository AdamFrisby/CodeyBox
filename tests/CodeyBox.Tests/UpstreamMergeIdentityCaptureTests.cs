using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Regression coverage for the merge-identity capture path in
/// <see cref="CodeyBox.Orchestrator.PipelineRunner"/>. The merge phase
/// produces a LOCAL bare-repo merge sha that does NOT match the squash
/// commit GitHub mints at auto-merge time; the orchestrator must persist
/// the GitHub-side sha (plus the PR number / URL) on the work item so
/// monitoring / audit code can resolve it on the forge commits API.
///
/// Reuses <see cref="RacingUpstreamRemote"/> from the race-recovery suite,
/// extended with explicit override fields that let the fixture return a
/// MergedSha distinct from the request's local sha (the legacy fixture
/// echoed the local sha back, which masked this exact bug).
/// </summary>
[Collection("Pipeline integration")]
public sealed class UpstreamMergeIdentityCaptureTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-merge-id-").FullName;

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
    public async Task AutoMerge_PersistsGitHubMergedShaAndPrIdentifiers()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The forge returns a sha distinct from the local merge sha — same
        // shape as GitHub's squash-merge response, which mints its own
        // commit on the base branch and never reuses the local merge sha
        // the orchestrator pushed.
        const string forgeMergedSha = "abc123def4567890abc123def4567890abc12345";
        const int forgePrNumber = 42;
        const string forgePrUrl = "https://example.invalid/owner/repo/pull/42";

        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(
                    AutoMergeRaced: false,
                    AdvanceSeedBeforeReturning: false,
                    MergedShaOverride: forgeMergedSha,
                    PullRequestNumberOverride: forgePrNumber,
                    PullRequestUrlOverride: forgePrUrl),
            },
        };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("capture.txt", "captured\n"));

        var item = NewItem("feature/capture-merge-identity");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The CompleteAsync request carried the local merge sha (the value
        // the merge-phase agent produced). That value lives on
        // LocalSquashSha for race-recovery diagnostics.
        var requestMergeSha = remote.Requests[0].MergeSha;
        Assert.False(string.IsNullOrEmpty(requestMergeSha));
        Assert.Equal(requestMergeSha, final.LocalSquashSha);

        // The persisted MergeSha must be the FORGE response sha, not the
        // local sha the orchestrator pushed. This is the exact regression
        // the work item exists to catch: a monitoring tool looking up
        // MergeSha against GET /repos/{owner}/{repo}/commits/{sha} needs
        // a value that actually resolves there.
        Assert.Equal(forgeMergedSha, final.MergeSha);
        Assert.NotEqual(final.LocalSquashSha, final.MergeSha);

        // PR identifiers travel alongside the GitHub sha so dashboards can
        // link straight to the merged PR without having to reassemble the
        // URL from the owner/repo/number triple.
        Assert.Equal(forgePrNumber, final.MergedPrNumber);
        Assert.Equal(forgePrUrl, final.MergedPrUrl);
    }

    [Fact]
    public async Task AutoMergeOffShape_PrOpenedButNoForgeSha_LeavesMergeShaNullPrCapturesPersist()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Models the AutoMerge=false / graceful-soft-fail outcome shape:
        // a PR was opened on the forge (number + URL returned) but no
        // merge commit exists yet — a human will merge later. MergeSha
        // must stay null in that case (so monitoring code doesn't see a
        // stale local sha and try to resolve it on GitHub), but the PR
        // identifiers should still be persisted.
        const int forgePrNumber = 7;
        const string forgePrUrl = "https://example.invalid/owner/repo/pull/7";

        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(
                    AutoMergeRaced: false,
                    AdvanceSeedBeforeReturning: false,
                    PullRequestNumberOverride: forgePrNumber,
                    PullRequestUrlOverride: forgePrUrl,
                    ForceMergedShaNull: true),
            },
        };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = false,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("capture.txt", "captured\n"));

        var item = NewItem("feature/capture-no-automerge");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Local sha was captured for diagnostics.
        Assert.False(string.IsNullOrEmpty(final.LocalSquashSha));
        // No forge merge happened yet — MergeSha stays null so callers
        // don't mistake a local sha for a GitHub ref.
        Assert.Null(final.MergeSha);
        // PR identifiers still landed so the operator surface can link
        // back to the open PR.
        Assert.Equal(forgePrNumber, final.MergedPrNumber);
        Assert.Equal(forgePrUrl, final.MergedPrUrl);
    }
}
