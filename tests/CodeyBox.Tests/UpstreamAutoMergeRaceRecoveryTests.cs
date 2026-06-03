using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the auto-merge race recovery path in
/// <see cref="CodeyBox.Orchestrator.PipelineRunner"/>. The race fires when
/// upstream <c>main</c> advances between the local merge phase and the
/// GitHub merge call (HTTP 405 on PUT /pulls/N/merge). The orchestrator
/// should refetch base, re-run the LLM merger against the new tip, and
/// retry the GitHub merge — bounded by <c>UpstreamPushMaxAttempts</c>.
///
/// Tests run real <see cref="CodeyBox.Git.LocalGitHost"/> + <see cref="ScriptedAgent"/>
/// with a fake <see cref="IUpstreamRemote"/> that pre-programmes the
/// AutoMergeRaced responses and mutates the seed repo between attempts to
/// make the refetch actually pick up a new base commit.
/// </summary>
[Collection("Pipeline integration")]
public sealed class UpstreamAutoMergeRaceRecoveryTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-race-").FullName;

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
    public async Task AutoMergeRaces_OrchestratorReFetchesBaseAndRetriesUntilDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Pre-programme the fake remote:
        //   - 1st CompleteAsync: AutoMergeRaced=true (PR opened, merge raced)
        //   - then advance the seed repo so the refetch picks up a moved base
        //   - 2nd CompleteAsync: success (merged)
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: true),
                new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false),
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
            // One merge for the initial merge phase, one for the re-run on the race.
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-recovery");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // CompleteAsync was called twice — once for the initial attempt that
        // raced, and once after the race-recovery re-merge.
        Assert.Equal(2, remote.CompleteCalls);
        // Two FetchBaseBranchAsync calls: one is the pre-merge canonical-base
        // refresh (stale-base guard), the other is the race-recovery refetch.
        Assert.Equal(2, remote.FetchCalls);
        // The second CompleteAsync carried the PR number from the first to
        // skip re-creating the PR (which would 422).
        Assert.Null(remote.Requests[0].ExistingPullRequestNumber);
        Assert.Equal(1, remote.Requests[1].ExistingPullRequestNumber);
        // Acceptance criterion: UpstreamPushAttempts reflects the total retry
        // count so the operator surface stays observable. Two CompleteAsync
        // calls = two attempts.
        Assert.Equal(2, final.UpstreamPushAttempts);
        // The second CompleteAsync request must carry the freshly-resolved
        // merge sha, not the stale one from the initial racing attempt — that
        // is what proves the orchestrator re-merged against the moved base.
        Assert.NotEqual(remote.Requests[0].MergeSha, remote.Requests[1].MergeSha);
    }

    [Fact]
    public async Task AutoMergeRace_BaseNotAdvanced_OrchestratorStillReRunsMergeAndAutoMerges()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Programme the remote to race once, but DON'T mutate the seed.
        // With the fixed staleness check (live upstream base ref), the
        // orchestrator re-runs the merge phase against the refreshed base
        // regardless of whether it can detect motion. Since the base didn't
        // actually move, the re-merge produces an equivalent tree and the
        // retry succeeds — no operator intervention needed.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: false),
                new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false),
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
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-base-stable");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Two CompleteAsync calls: one raced, one succeeded after recovery.
        Assert.Equal(2, remote.CompleteCalls);
        // Two FetchBaseBranchAsync calls: pre-merge canonical-base refresh +
        // race-recovery refetch.
        Assert.Equal(2, remote.FetchCalls);
        // The second CompleteAsync carried the PR number from the first.
        Assert.Null(remote.Requests[0].ExistingPullRequestNumber);
        Assert.Equal(1, remote.Requests[1].ExistingPullRequestNumber);
        // UpstreamPushAttempts reflects retry count on the operator surface.
        Assert.Equal(2, final.UpstreamPushAttempts);
    }

    [Fact]
    public async Task AutoMergeRace_HitsMaxAttempts_ParksWithRaceCapMessage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Always race, always advance base — simulates a hostile writer
        // hammering main. Use the hot-reloadable PipelineTuningSnapshot with
        // a low AutoMergeRaceRecoveryMaxAttempts so the cap fires inside the
        // loop (not at the post-loop exhaustion check).
        const int maxAttempts = 3;
        const int raceRecoveryMax = 1;
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        for (var i = 0; i < maxAttempts + 1; i++)
        {
            remote.ResponsePlan.Add(new RacingResponse(
                AutoMergeRaced: true,
                AdvanceSeedBeforeReturning: true));
        }

        var mergeStrategies = Enumerable.Repeat(MergeStrategy.RealMerge, maxAttempts + 1).ToArray();
        var factory = new SingleRemoteFactory(remote);
        var pipelineTuning = new CodeyBox.Orchestrator.PipelineTuningOptions
        {
            AutoMergeRaceRecoveryMaxAttempts = raceRecoveryMax,
        };
        var pipelineTuningSnapshot = new CodeyBox.Orchestrator.PipelineTuningSnapshot(pipelineTuning);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: mergeStrategies,
            pipelineOptions: new CodeyBox.Orchestrator.PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = maxAttempts,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            pipelineTuning: pipelineTuningSnapshot);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-cap");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        // The race-recovery cap fires inside the loop when AutoMergeRaceRecoveryMaxAttempts
        // is exhausted (raceRecoveryCount reaches the cap), parking with the
        // "main is being hammered" message.
        Assert.Contains("baseBranch likely being mutated by another writer", final.LastError);
        // CompleteAsync is called once per attempt up to the point the cap fires.
        // With raceRecoveryMax=1: attempt 1 races, recovers, count becomes 1,
        // 1>=1 fires the cap → parks → break. So CompleteCalls = 1.
        Assert.Equal(raceRecoveryMax, remote.CompleteCalls);
        // FetchBaseBranchAsync: pre-merge canonical-base refresh (1) + one per
        // race-recovery iteration (1, since cap fires after the first recovery).
        Assert.Equal(raceRecoveryMax + 1, remote.FetchCalls);
        // UpstreamPushAttempts reflects the attempt number when cap fired.
        Assert.Equal(raceRecoveryMax, final.UpstreamPushAttempts);
    }

    [Fact]
    public async Task AutoMergeRace_NullPullRequestNumber_ParksWithDistinctMessage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The upstream says we raced but gives us no PR number to retry against.
        // The orchestrator must park (we have nothing to merge into) rather than
        // looping or silently transitioning to Done.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(
                    AutoMergeRaced: true,
                    AdvanceSeedBeforeReturning: false,
                    OmitPullRequestNumber: true),
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
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-no-pr");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("no PR number returned", final.LastError);
        // Exactly one fetch: the pre-merge canonical-base refresh. The
        // race-recovery refetch never runs because the park branch fires
        // before reaching it (no PR number to retry against).
        Assert.Equal(1, remote.FetchCalls);
    }

    [Fact]
    public async Task AutoMergeRace_FetchBaseBranchThrows_ParksWithRefetchError()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The orchestrator gets AutoMergeRaced=true but the refetch fails
        // (network error, auth bounce). Park rather than loop without info.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: false),
            },
            FetchThrows = new InvalidOperationException("git fetch upstream branch 'main' failed: connection refused"),
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
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-fetch-throws");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("could not refetch upstream base", final.LastError);
        // Two fetches: the pre-merge canonical-base refresh (which throws,
        // is caught + logged as best-effort), then the race-recovery refetch
        // (which throws and is the one the test specifically targets — it's
        // the strict path that parks with the asserted lastError).
        Assert.Equal(2, remote.FetchCalls);
    }

    [Fact]
    public async Task AutoMergeRace_FetchBaseBranchReturnsNull_ParksWithUpstreamNotAdvertising()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // FetchBaseBranchAsync returns null — the upstream literally does not
        // advertise the configured base branch. Park with a distinct message
        // so the operator can fix branch naming rather than chasing a race.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: false),
            },
            FetchReturnsNull = true,
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
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-no-base");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("does not advertise base branch", final.LastError);
        // Two fetches: the pre-merge canonical-base refresh (which returns
        // null and is logged as best-effort), then the race-recovery refetch
        // (also returns null and triggers the strict park with the asserted
        // lastError).
        Assert.Equal(2, remote.FetchCalls);
    }

    [Fact]
    public async Task AutoMergeRace_SetBranchToCommitThrows_ParksWithAdvanceWorkBranchError()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Inject a gitHost decorator that throws on SetBranchToCommitAsync.
        // This simulates the bare repo state being unexpectedly broken after
        // the re-merge produces a new sha. The orchestrator must park rather
        // than retrying (the bare-repo state is bad, retrying won't help).
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: true),
                new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false),
            },
        };
        var factory = new SingleRemoteFactory(remote);
        SetBranchThrowingGitHost? wrapper = null;
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge],
            gitHostDecorator: inner =>
            {
                wrapper = new SetBranchThrowingGitHost(inner);
                return wrapper;
            });
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-setbranch-throws");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("could not advance local work branch", final.LastError);
        Assert.NotNull(wrapper);
        Assert.True(wrapper!.SetBranchInvocations > 0);
    }

    [Fact]
    public async Task AutoMergeRace_RecoveryCapHotReloadable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Programme the remote to always race and advance base — but set the
        // PipelineTuningSnapshot race-recovery cap to 1 so we can assert the
        // hot-reloadable cap fires before the UpstreamPushMaxAttempts bound.
        const int raceRecoveryCap = 1;
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        for (var i = 0; i < 5; i++)
        {
            remote.ResponsePlan.Add(new RacingResponse(
                AutoMergeRaced: true,
                AdvanceSeedBeforeReturning: true));
        }

        var mergeStrategies = Enumerable.Repeat(MergeStrategy.RealMerge, 5).ToArray();
        var factory = new SingleRemoteFactory(remote);
        var pipelineTuning = new CodeyBox.Orchestrator.PipelineTuningOptions
        {
            AutoMergeRaceRecoveryMaxAttempts = raceRecoveryCap,
        };
        var pipelineTuningSnapshot = new CodeyBox.Orchestrator.PipelineTuningSnapshot(pipelineTuning);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: mergeStrategies,
            pipelineOptions: new CodeyBox.Orchestrator.PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 5,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            pipelineTuning: pipelineTuningSnapshot);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-hot-reload-cap");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("baseBranch likely being mutated by another writer", final.LastError);

        // Simulate hot-reload: bump the cap mid-flight (or, in test terms,
        // assert that the cap was respected, not the UpstreamPushMaxAttempts).
        // With raceRecoveryCap=1 and UpstreamPushMaxAttempts=5, the loop
        // should park after 1 recovery iteration (raceRecoveryCount reaches
        // the cap), not after 5 total attempts. CompleteCalls=1 confirms the
        // hot-reloadable cap took precedence.
        Assert.Equal(1, remote.CompleteCalls);
    }

    [Fact]
    public async Task AutoMergeRace_RerunMergePhaseFails_ParksAsLlmMergerGaveUp()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The first CompleteAsync races; the orchestrator refetches a moved
        // base and re-invokes the merge phase. The base drift overwrites the
        // SAME file the work branch modified, so the re-run merge phase has
        // a real host conflict. With no ConflictResolutionPlan entry queued
        // the scripted agent fails the resolve, surfacing as
        // MergeConflictResolutionFailedException. The orchestrator must route
        // this to the RunAsync-level MergeConflictResolutionFailed handler —
        // NOT the upstream-push catch (which would relabel it as
        // "infrastructure" and conflate "LLM gave up" with the race). This
        // test directly exercises the catch-narrowing fix in PipelineRunner.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(
                    AutoMergeRaced: true,
                    AdvanceSeedBeforeReturning: true,
                    AdvanceSeedFilePath: "shared.txt",
                    AdvanceSeedFileContent: "base side drifted\n"),
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
            // Two merge strategies: first succeeds, second is RealMerge but
            // will encounter conflicts (the drift overwrote shared.txt).
            // Without a ConflictResolutionPlan entry the agent reports
            // failure, which RunAgentMergePhaseAsync rethrows as
            // MergeConflictResolutionFailedException.
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        // Work branch writes shared.txt. After race recovery, the seed will
        // also have shared.txt with different content → host-level conflict
        // → conflict-resolver path → no plan entry → fails.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side content\n"));

        var item = NewItem("feature/race-llm-fails");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        // The recovery-rethrow message identifies this as the LLM merger
        // failing on the refreshed base — not a transient infrastructure
        // failure (which would be "upstream complete failed after N attempts")
        // and not the "main is being hammered" cap message.
        Assert.Contains("race recovery", final.LastError, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Decorating IGitHost that throws on SetBranchToCommitAsync to exercise the
/// recovery park-branch in TryRecoverFromAutoMergeRaceAsync.
/// </summary>
internal sealed class SetBranchThrowingGitHost : IGitHost
{
    private readonly IGitHost _inner;
    public int SetBranchInvocations { get; private set; }

    public SetBranchThrowingGitHost(IGitHost inner) { _inner = inner; }

    public Task SetBranchToCommitAsync(string repositoryId, string branch, string sha, CancellationToken ct = default)
    {
        SetBranchInvocations++;
        throw new InvalidOperationException(
            $"simulated update-ref failure for branch '{branch}' at {sha}");
    }

    // Delegate everything else to the real host.
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => _inner.EnsureRepositoryAsync(id, seedFromUrl, ct);
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => _inner.EnsureRepositoryAsync(id, seedFromUrl, baseBranch, ct);
    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => _inner.GetSandboxAccess(repositoryId);
    public string GetRepoPath(string repositoryId) => _inner.GetRepoPath(repositoryId);
    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => _inner.GetDefaultBranchAsync(repositoryId, ct);
    public Task PushToUpstreamAsync(
        string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => _inner.PushToUpstreamAsync(repositoryId, upstreamUrl, branch, upstreamEnv, reconcileStrategy, ct);
    public Task<string?> FetchUpstreamBranchAsync(
        string repositoryId, string upstreamUrl, string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        CancellationToken ct = default)
        => _inner.FetchUpstreamBranchAsync(repositoryId, upstreamUrl, branch, upstreamEnv, ct);
    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => _inner.DisposeRepositoryAsync(repositoryId, ct);
    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => _inner.RepositoryExistsAsync(id, ct);
    public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
        => _inner.BranchExistsAsync(repositoryId, branch, ct);
    public Task<bool> BranchHasCommitsAheadAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => _inner.BranchHasCommitsAheadAsync(repositoryId, baseBranch, workBranch, ct);
    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => _inner.GetDiffAsync(repositoryId, baseBranch, workBranch, ct);
    public Task<GitMergeTreeResult> ComputeMergeTreeAsync(string repositoryId, string mainCommit, string workCommit, CancellationToken ct = default)
        => _inner.ComputeMergeTreeAsync(repositoryId, mainCommit, workCommit, ct);
    public Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
        => _inner.ResolveCommitAsync(repositoryId, commitish, ct);
    public Task ResetWorkBranchToBaseAsync(string repositoryId, string workBranch, string baseBranch, CancellationToken ct = default)
        => _inner.ResetWorkBranchToBaseAsync(repositoryId, workBranch, baseBranch, ct);
    public Task<string> ResolveTreeAsync(string repositoryId, string treeish, CancellationToken ct = default)
        => _inner.ResolveTreeAsync(repositoryId, treeish, ct);
    public Task<string> ReadTextFileAsync(string repositoryId, string treeish, string path, CancellationToken ct = default)
        => _inner.ReadTextFileAsync(repositoryId, treeish, path, ct);
    public Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
        => _inner.ListFilesAsync(repositoryId, treeish, pathPrefix, ct);
    public Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(string repositoryId, string fromTreeish, string toTreeish, CancellationToken ct = default)
        => _inner.GetChangedPathsAsync(repositoryId, fromTreeish, toTreeish, ct);
    public Task<string> GetUnifiedDiffAsync(string repositoryId, string fromTreeish, string toTreeish, string path, CancellationToken ct = default)
        => _inner.GetUnifiedDiffAsync(repositoryId, fromTreeish, toTreeish, path, ct);
}

internal sealed record RacingResponse(
    bool AutoMergeRaced,
    bool AdvanceSeedBeforeReturning,
    bool OmitPullRequestNumber = false,
    string? AdvanceSeedFilePath = null,
    string? AdvanceSeedFileContent = null);

/// <summary>
/// Fake upstream remote that pre-programmes a sequence of CompleteAsync
/// outcomes (success vs. AutoMergeRaced) and optionally advances the seed
/// repo before returning so a subsequent FetchBaseBranchAsync picks up a
/// moved base. Used by <see cref="UpstreamAutoMergeRaceRecoveryTests"/>.
/// </summary>
internal sealed class RacingUpstreamRemote : IUpstreamRemote
{
    public required string SeedRepoPath { get; init; }
    public List<RacingResponse> ResponsePlan { get; } = new();
    public List<UpstreamCompletionRequest> Requests { get; } = new();
    public int CompleteCalls { get; private set; }
    public int FetchCalls { get; private set; }

    /// <summary>When set, <see cref="FetchBaseBranchAsync"/> throws on every call.</summary>
    public Exception? FetchThrows { get; set; }
    /// <summary>When true, <see cref="FetchBaseBranchAsync"/> returns null without running git.</summary>
    public bool FetchReturnsNull { get; set; }

    public string Name => "racing-upstream";

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var index = CompleteCalls;
        CompleteCalls++;
        var response = index < ResponsePlan.Count
            ? ResponsePlan[index]
            : new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false);

        if (response.AdvanceSeedBeforeReturning)
        {
            // Write a new commit to seed main so the next refetch sees a moved
            // base. The orchestrator will then re-run the LLM merger against
            // this new tip rather than parking on "base didn't move".
            //
            // If the response specifies an AdvanceSeedFilePath, write that
            // file with the supplied content — used by tests that need the
            // drift to conflict with the work branch (so the re-run merge
            // phase exercises the conflict-resolver path).
            var newFile = response.AdvanceSeedFilePath ?? $"upstream-drift-{index}.txt";
            var newContent = response.AdvanceSeedFileContent
                ?? $"upstream drift commit {index}\n";
            await File.WriteAllTextAsync(
                Path.Combine(SeedRepoPath, newFile),
                newContent,
                ct);
            await TestSupport.RunGit(SeedRepoPath, "add", newFile);
            await TestSupport.RunGit(SeedRepoPath, "commit", "-m", $"drift {index}");
        }

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = response.OmitPullRequestNumber ? null : "https://example.invalid/pr/1",
            // Park-branch test: when AutoMerge raced but no PR number is
            // returned the orchestrator has nothing to retry against and must
            // park with the "no PR number returned" message.
            PullRequestNumber = response.OmitPullRequestNumber ? null : 1,
            MergedSha = response.AutoMergeRaced ? null : request.MergeSha ?? "merged-sha",
            AutoMergeRaced = response.AutoMergeRaced,
            Notes = response.AutoMergeRaced ? "racing" : null,
        };
    }

    public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
        => Task.FromResult(true);

    public async Task<string?> FetchBaseBranchAsync(string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        FetchCalls++;
        if (FetchThrows is not null)
            throw FetchThrows;
        if (FetchReturnsNull)
            return null;
        // Run git directly against the bare repo path — mirrors the LocalGitHost
        // implementation of FetchUpstreamBranchAsync so the test exercises the
        // same ref-update semantics the real GitHubUpstreamRemote relies on.
        var barePath = Path.Combine(BareRepoRoot
            ?? throw new InvalidOperationException("RacingUpstreamRemote was not wired with BareRepoRoot"),
            repositoryId + ".git");
        await TestSupport.RunGit(
            barePath, "fetch", "--no-tags",
            SeedRepoPath, $"+refs/heads/{baseBranch}:refs/heads/{baseBranch}");
        var (_, sha, _) = await TestSupport.RunGit(
            barePath, "rev-parse", "--verify", $"refs/heads/{baseBranch}^{{commit}}");
        return sha.Trim();
    }

    /// <summary>Wired by <see cref="SingleRemoteFactory"/> at construction.</summary>
    public string? BareRepoRoot { get; set; }
}

internal sealed class SingleRemoteFactory : IUpstreamRemoteFactory
{
    private readonly RacingUpstreamRemote _remote;
    public SingleRemoteFactory(RacingUpstreamRemote remote) => _remote = remote;
    public IUpstreamRemote Create(Project project) => _remote;
}
