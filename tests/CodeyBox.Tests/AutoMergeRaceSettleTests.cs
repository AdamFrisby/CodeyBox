using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Classification of <c>AutoMergeRaced</c> outcomes by the forge's own
/// mergeability verdict plus a fresh base comparison (the foligen#12
/// post-mortem: a push followed ~1 s later by the merge call races GitHub's
/// asynchronous mergeability computation, producing a spurious 405 against
/// the previous head while the base never moved).
///
/// <para>Drives the full <c>RunAsync</c> pipeline with the shared
/// <see cref="RacingUpstreamRemote"/> scripted with outcome + live-PR
/// mergeability, through real wiring (real <see cref="SqliteWorkItemStore"/>,
/// real local git host):</para>
/// <list type="bullet">
/// <item>base unchanged + mergeable unknown → same-tree merge-call retry
/// that does NOT re-run the merge phase and does NOT consume the
/// race-recovery budget;</item>
/// <item>base unchanged + mergeable=false → genuine conflict routed to the
/// existing conflict path, outside the cap, never blaming another
/// writer.</item>
/// </list>
/// </summary>
[Collection("Pipeline integration")]
public sealed class AutoMergeRaceSettleTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-settle-").FullName;

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

    private static TestPipeline BuildSettlePipeline(
        string workspace,
        string seed,
        RacingUpstreamRemote remote,
        int raceRecoveryCap,
        StalePullRequestSweeperOptions? staleBaseReworkOptions = null)
    {
        var tp = TestSupport.BuildPipeline(
            workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: new SingleRemoteFactory(remote),
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 5,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            pipelineTuning: new PipelineTuningSnapshot(new PipelineTuningOptions
            {
                AutoMergeRaceRecoveryMaxAttempts = raceRecoveryCap,
            }),
            staleBaseReworkOptions: staleBaseReworkOptions,
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("settle-fix.txt", "fixes settle\n"));
        return tp;
    }

    [Fact]
    public async Task RaceWithUnchangedBaseAndUnknownMergeable_RetriesSameTreeWithoutConsumingRecovery()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The 405 raced the forge's mergeability computation: base never
        // moves, mergeability stays unknown. The orchestrator must retry the
        // merge call with the identical tree — no merge-phase re-run, no
        // race-recovery budget spent. With the cap at 1, the old behaviour
        // (every 405 burns a re-merge) would park after the first recovery;
        // success here proves the retry did not consume it.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: false),
                new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false),
            },
        };
        using var tp = BuildSettlePipeline(_workspace, seed, remote, raceRecoveryCap: 1);

        var item = NewItem("codeybox/settle-unknown");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(2, remote.CompleteCalls);
        // Same tree on the retry: the merge phase ran once, not twice.
        Assert.Equal(remote.Requests[0].MergeSha, remote.Requests[1].MergeSha);
        Assert.Equal(1, remote.Requests[1].ExistingPullRequestNumber);
        Assert.Equal(2, final.UpstreamPushAttempts);
        // The orchestrator read the live PR for classification (unknown
        // here) and passed its hot-reloadable settle knobs to the remote.
        Assert.Equal(1, remote.PullReads);
        Assert.Equal(TimeSpan.FromSeconds(60), remote.Requests[0].MergeSettleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), remote.Requests[0].MergeSettlePollInterval);
    }

    [Fact]
    public async Task RaceWithUnchangedBaseAndSettledFalse_RoutesToConflictRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // The forge settled the PR as conflicting against an unchanged base:
        // re-running the merger cannot help. The item must enter the existing
        // conflict path with no further merge attempt and no race-recovery
        // spent — and the base must never be blamed on another writer.
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(
                    AutoMergeRaced: true,
                    AdvanceSeedBeforeReturning: false,
                    Mergeable: false,
                    MergeableState: "dirty"),
            },
        };
        remote.PullStates.Enqueue(new UpstreamPullRequestState(
            1, "https://example.invalid/pr/1", PullRequestStatus.Open, null,
            HeadSha: null, Mergeable: false, MergeableState: "dirty"));
        using var tp = BuildSettlePipeline(
            _workspace, seed, remote, raceRecoveryCap: 3,
            staleBaseReworkOptions: new StalePullRequestSweeperOptions
            {
                RouteToConflictRework = true,
                MaxReworkAttempts = 2,
            });

        var item = NewItem("codeybox/settle-conflict");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Equal(1, remote.CompleteCalls);
        Assert.Equal(1, final.UpstreamPushAttempts);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RaceWithUnchangedBaseAndSettledFalse_RouterDisabled_ParksWithMergeableVerdict()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(
                    AutoMergeRaced: true,
                    AdvanceSeedBeforeReturning: false,
                    Mergeable: false,
                    MergeableState: "dirty"),
            },
        };
        remote.PullStates.Enqueue(new UpstreamPullRequestState(
            1, "https://example.invalid/pr/1", PullRequestStatus.Open, null,
            HeadSha: null, Mergeable: false, MergeableState: "dirty"));
        using var tp = BuildSettlePipeline(_workspace, seed, remote, raceRecoveryCap: 3);

        var item = NewItem("codeybox/settle-conflict-park");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(1, remote.CompleteCalls);
        // The park message carries the forge's actual verdict — never the
        // another-writer story, which would be false: the base did not move.
        Assert.Contains("mergeable=false", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dirty", final.LastError, StringComparison.Ordinal);
        Assert.Contains("unchanged", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("another writer", final.LastError, StringComparison.OrdinalIgnoreCase);
    }
}
