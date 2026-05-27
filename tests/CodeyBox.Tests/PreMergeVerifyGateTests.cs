using CodeyBox.Core;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the pre-merge CI gate. The gate runs after the
/// local merge phase but before the forge auto-merge API call, and refuses
/// to proceed when an <see cref="IPreMergeVerifier"/> reports failure —
/// regardless of whether the forge would have accepted the merge.
///
/// Motivation: the forge's <c>mergeable == true</c> flag only checks for
/// textual conflicts. It does not catch the case where a clean merge against
/// a freshly-moved <c>main</c> still breaks the build (a renamed helper,
/// a drifted constant) or fails previously-green tests.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PreMergeVerifyGateTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-premerge-").FullName;

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

    /// <summary>
    /// Acceptance #2 / #3: a PR whose rebase against current main passes
    /// but whose rebased build fails MUST NOT be auto-merged. The work item
    /// must park at MergeConflictResolutionFailed with a lastError prefix
    /// that distinguishes this from a textual rebase conflict.
    /// </summary>
    [Fact]
    public async Task RebasedBuildFails_AutoMergerDeclinesAndParksWithDistinctMessage()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var verifier = new StubPreMergeVerifier
        {
            Result = PreMergeVerifyResult.BuildOrTestFailed("dotnet build: CS0117 'IndexOf' not found"),
        };
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
                PreMergeVerifyArgv = ["dotnet", "build"],
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge],
            preMergeVerifier: verifier);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/premerge-build-fails");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        // The distinct "rebased build failed" prefix — separate from
        // "base didn't move", "main is being hammered", and LLM-merger failures.
        Assert.StartsWith("pre-merge verify: rebased build failed:", final.LastError);
        Assert.Contains("CS0117", final.LastError);

        // The verifier ran exactly once, and CompleteAsync was NEVER called —
        // the gate must short-circuit the forge merge call.
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, remote.CompleteCalls);
    }

    /// <summary>
    /// Acceptance #1 / #3: a textual rebase conflict must produce a distinct
    /// "rebase failed" prefix that the operator can tell apart from a clean
    /// rebase whose build broke.
    /// </summary>
    [Fact]
    public async Task RebaseTextualConflict_AutoMergerDeclinesWithRebaseFailedPrefix()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var verifier = new StubPreMergeVerifier
        {
            Result = PreMergeVerifyResult.RebaseFailed("CONFLICT (content): merge conflict in src/Foo.cs"),
        };
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
                PreMergeVerifyArgv = ["dotnet", "build"],
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge],
            preMergeVerifier: verifier);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/premerge-rebase-conflict");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.StartsWith("pre-merge verify: rebase failed:", final.LastError);
        Assert.Contains("CONFLICT", final.LastError);

        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, remote.CompleteCalls);
    }

    /// <summary>
    /// Happy path: when the verifier returns success the auto-merger proceeds
    /// to call CompleteAsync. Proves the gate is opt-in via project config
    /// and does not silently block green merges.
    /// </summary>
    [Fact]
    public async Task VerifierGreen_AutoMergerProceedsToCompleteAsync()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var verifier = new StubPreMergeVerifier { Result = PreMergeVerifyResult.Ok() };
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            // CompleteAsync needs at least one entry — default racing-false
            // means success and lets the work item reach Done.
            ResponsePlan = { new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false) },
        };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
                PreMergeVerifyArgv = ["dotnet", "build"],
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge],
            preMergeVerifier: verifier);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/premerge-green");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(1, remote.CompleteCalls);
    }

    /// <summary>
    /// Backwards-compat: a project that has NOT populated
    /// PreMergeVerifyArgv must keep its prior behaviour — the verifier is
    /// never invoked, even when one is registered with the pipeline. This
    /// keeps existing projects from a surprise gate they did not opt into.
    /// </summary>
    [Fact]
    public async Task NoArgvConfigured_VerifierIsNotInvoked()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var verifier = new StubPreMergeVerifier
        {
            Result = PreMergeVerifyResult.BuildOrTestFailed("should not be called"),
        };
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan = { new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false) },
        };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
                // PreMergeVerifyArgv intentionally omitted (defaults to []).
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge],
            preMergeVerifier: verifier);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/premerge-no-argv");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(1, remote.CompleteCalls);
    }

    /// <summary>
    /// When AutoMerge is disabled the gate has no role: the human will press
    /// the merge button on the forge after their own checks. Skipping the
    /// gate here avoids running a (potentially slow) verifier on every PR
    /// that the operator never asked to auto-merge.
    /// </summary>
    [Fact]
    public async Task AutoMergeDisabled_GateIsSkipped()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var verifier = new StubPreMergeVerifier
        {
            Result = PreMergeVerifyResult.BuildOrTestFailed("should not be called"),
        };
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan = { new RacingResponse(AutoMergeRaced: false, AdvanceSeedBeforeReturning: false) },
        };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = false,
                MergeMethod = "squash",
                PreMergeVerifyArgv = ["dotnet", "build"],
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge],
            preMergeVerifier: verifier);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/premerge-no-automerge");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, verifier.Calls);
    }

    /// <summary>
    /// If the verifier itself throws (a host I/O error, sandbox bring-up
    /// failure, etc.) we MUST park rather than silently auto-merge — the
    /// gate is a refuse-on-doubt safety net, not a best-effort hint.
    /// </summary>
    [Fact]
    public async Task VerifierThrows_ParksWithBuildOrTestFailedPrefix()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var verifier = new StubPreMergeVerifier
        {
            ThrowOnVerify = new InvalidOperationException("sandbox bring-up failed: out of disk"),
        };
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        var factory = new SingleRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
                PreMergeVerifyArgv = ["dotnet", "build"],
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge],
            preMergeVerifier: verifier);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello\n"));

        var item = NewItem("feature/premerge-throws");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.StartsWith("pre-merge verify: rebased build failed:", final.LastError);
        Assert.Contains("out of disk", final.LastError);
        Assert.Equal(0, remote.CompleteCalls);
    }
}

internal sealed class StubPreMergeVerifier : IPreMergeVerifier
{
    public PreMergeVerifyResult Result { get; set; } = PreMergeVerifyResult.Ok();
    public Exception? ThrowOnVerify { get; set; }
    public int Calls { get; private set; }
    public List<PreMergeVerifyRequest> Requests { get; } = new();

    public Task<PreMergeVerifyResult> VerifyAsync(PreMergeVerifyRequest request, CancellationToken ct)
    {
        Calls++;
        Requests.Add(request);
        if (ThrowOnVerify is not null)
            throw ThrowOnVerify;
        return Task.FromResult(Result);
    }
}
