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
        // The race-recovery path called FetchBaseBranchAsync exactly once.
        Assert.Equal(1, remote.FetchCalls);
        // The second CompleteAsync carried the PR number from the first to
        // skip re-creating the PR (which would 422).
        Assert.Null(remote.Requests[0].ExistingPullRequestNumber);
        Assert.Equal(1, remote.Requests[1].ExistingPullRequestNumber);
    }

    [Fact]
    public async Task AutoMergeRace_BaseDidNotMove_ParksWithDistinctMessage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Programme the remote to race, but DON'T mutate the seed — so the
        // refetch returns the same sha and the orchestrator must distinguish
        // "real race" from "different conflict" (branch protection etc.).
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: false),
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
            // Only the initial merge runs — recovery should park before re-running.
            mergeStrategy: [MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-stable");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        // The distinct "base didn't move" message — separate from the
        // "main is being hammered" / LLM-merger-gave-up paths.
        Assert.Contains("base didn't move", final.LastError);
        // Only one CompleteAsync call — orchestrator parked instead of looping.
        Assert.Equal(1, remote.CompleteCalls);
        Assert.Equal(1, remote.FetchCalls);
    }

    [Fact]
    public async Task AutoMergeRace_HitsMaxAttempts_ParksWithRaceCapMessage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Always race, always advance base — simulates a hostile writer
        // hammering main. The orchestrator should give up after
        // UpstreamPushMaxAttempts re-runs.
        const int maxAttempts = 3;
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        for (var i = 0; i < maxAttempts + 1; i++)
        {
            remote.ResponsePlan.Add(new RacingResponse(
                AutoMergeRaced: true,
                AdvanceSeedBeforeReturning: true));
        }

        var mergeStrategies = Enumerable.Repeat(MergeStrategy.RealMerge, maxAttempts + 1).ToArray();
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
            mergeStrategy: mergeStrategies,
            pipelineOptions: new CodeyBox.Orchestrator.PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = maxAttempts,
                UpstreamPushBackoff = TimeSpan.Zero,
            });
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-fix.txt", "fixes race\n"));

        var item = NewItem("feature/race-cap");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        // The distinct "main is being hammered" message — operators can tell
        // this apart from "LLM gave up" or "base didn't move".
        Assert.Contains("baseBranch likely being mutated by another writer", final.LastError);
        // CompleteAsync was called once per attempt (the cap).
        Assert.Equal(maxAttempts, remote.CompleteCalls);
    }
}

internal sealed record RacingResponse(bool AutoMergeRaced, bool AdvanceSeedBeforeReturning);

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
            var newFile = $"upstream-drift-{index}.txt";
            await File.WriteAllTextAsync(
                Path.Combine(SeedRepoPath, newFile),
                $"upstream drift commit {index}\n",
                ct);
            await TestSupport.RunGit(SeedRepoPath, "add", newFile);
            await TestSupport.RunGit(SeedRepoPath, "commit", "-m", $"drift {index}");
        }

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = "https://example.invalid/pr/1",
            PullRequestNumber = 1,
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
