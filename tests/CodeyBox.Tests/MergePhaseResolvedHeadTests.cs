using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the merge-phase → upstream-push contract fixed for
/// the Foligen #43 / Landgen #9 incidents:
///
/// <list type="bullet">
/// <item>A conflict-resolved merge must publish the RESOLVED merge commit as
/// the work-branch tip — never the pre-merge work tip — so the PR head GitHub
/// merges is the tree the merge phase verified (and it contains the base).</item>
/// <item>A <see cref="MergeScopeFence"/> rejection must consume the resolver's
/// remaining attempt/escalation budget inside
/// <see cref="AgenticConflictResolver"/> instead of only firing after the
/// resolver already reported success.</item>
/// <item>An auto-merge race-recovery re-merge that fails semantically
/// (resolver exhaustion / scope fence) must route into the bounded
/// <see cref="WorkItemState.ReworkingForConflict"/> machine rather than
/// parking terminally — the re-merge runs outside the merge section's own
/// guard-rework loop.</item>
/// </list>
///
/// <para>Tests run real <see cref="CodeyBox.Git.LocalGitHost"/> +
/// <see cref="ScriptedAgent"/> + process sandboxes through
/// <see cref="PipelineRunner.RunAsync"/>.</para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class MergePhaseResolvedHeadTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-merge-head-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch, bool pushUpstream = false, int conflictAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "merge resolved head",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = pushUpstream,
        ConflictReworkAttempts = conflictAttempts,
    };

    /// <summary>
    /// Seed whose shared.txt has enough lines that a line-5 conflict leaves
    /// line 25 far outside the conflict hunk + buffer — editing it is a
    /// deterministic scope-fence violation.
    /// </summary>
    private async Task<string> CreateSeedRepoAsync()
    {
        var seed = Path.Combine(_workspace, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await TestSupport.RunGit(seed, "init", "-b", "main");
        await TestSupport.RunGit(seed, "config", "user.email", "t@l");
        await TestSupport.RunGit(seed, "config", "user.name", "T");
        // Lets MainAdvancingAuditor push the base advance into this repo —
        // it plays the forge's main for upstream fetches.
        await TestSupport.RunGit(seed, "config", "receive.denyCurrentBranch", "ignore");
        await File.WriteAllTextAsync(
            Path.Combine(seed, "shared.txt"),
            SharedFileContent("line5"));
        await TestSupport.RunGit(seed, "add", "shared.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "initial");
        return seed;
    }

    private static string SharedFileContent(string line5) => string.Join('\n',
        Enumerable.Range(1, 30).Select(i => i == 5 ? line5 : $"line{i}")) + "\n";

    private static Dictionary<string, string> ResolveShared(string line5, string? tamperLine25 = null)
    {
        var lines = Enumerable.Range(1, 30)
            .Select(i => i == 5 ? line5 : i == 25 && tamperLine25 is not null ? tamperLine25 : $"line{i}")
            .ToList();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shared.txt"] = string.Join('\n', lines) + "\n",
        };
    }

    /// <summary>
    /// Regression for the Foligen #43 shape: the merge phase resolves a real
    /// conflict, and the work-branch tip pushed upstream must BE that resolved
    /// merge commit — carrying the base as ancestry — not the pre-merge work
    /// tip the push used to publish.
    /// </summary>
    [Fact]
    public async Task ConflictedMerge_PushesResolvedMergeCommitContainingBase()
    {
        var seed = await CreateSeedRepoAsync();
        // The auditor's advance must reach BOTH the item's bare mirror and
        // the seed (the simulated forge): the pre-merge canonical-base
        // refresh re-fetches baseBranch from the upstream remote's seed, so
        // advancing only the bare repo would be reverted before the merge —
        // silently turning the intended conflict into a clean merge.
        var auditor = new MainAdvancingAuditor(
            _workspace, "shared.txt", SharedFileContent("main-line5"), additionalPushTarget: seed);
        var remote = new RacingUpstreamRemote { SeedRepoPath = seed };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: new SingleRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge]);
        auditor.GitRoot = tp.GitRoot;
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", SharedFileContent("work-line5")));
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => ResolveShared("resolved-line5"));

        var workBranch = "codeybox/merge-resolved-head";
        var item = NewItem(workBranch, pushUpstream: true);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.LocalSquashSha);

        // The durable work branch must point at the merge commit itself —
        // that is the tip PushToUpstreamAsync publishes as the PR head.
        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (revExit, branchTip, _) = await TestSupport.RunGitNoThrow(
            barePath, "rev-parse", "--verify", $"{workBranch}^{{commit}}");
        Assert.Equal(0, revExit);
        Assert.Equal(final.LocalSquashSha, branchTip.Trim());

        // And that head must contain the current base — the property the
        // GitHub-side ancestry assertion relies on before PUT /pulls/merge.
        var (ancExit, _, ancErr) = await TestSupport.RunGitNoThrow(
            barePath, "merge-base", "--is-ancestor", "main", workBranch);
        Assert.True(ancExit == 0, $"expected base to be ancestor of pushed head: {ancErr}");

        // The upstream request carried the same merge sha as MergeSha, so a
        // host that asserts pushed-head/base ancestry evaluates the real
        // pushed revision rather than a plain work tip.
        var request = Assert.Single(remote.Requests);
        Assert.Equal(final.LocalSquashSha, request.MergeSha);
    }

    /// <summary>
    /// Landgen #9 shape: the resolver's first candidate produces a
    /// syntactically clean resolution that the scope fence rejects (edits
    /// outside the permitted conflict hunks). The rejection must consume
    /// that attempt — feeding the violation back as verification feedback —
    /// so the SAME candidate's remaining attempt (or a later candidate) can
    /// repair it, instead of the fence only firing after the resolver
    /// reported success and discarding the whole attempt budget.
    /// </summary>
    [Fact]
    public async Task ConflictedMerge_ScopeFenceRejection_RetriesWithFeedback()
    {
        var seed = await CreateSeedRepoAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", SharedFileContent("main-line5"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            mergeStrategy: [MergeStrategy.RealMerge]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", SharedFileContent("work-line5")));

        // Attempt 1: resolves the conflict but also rewrites line 25 —
        // outside the conflict hunk and its buffer → scope-fence violation.
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => ResolveShared("resolved-line5", tamperLine25: "tampered-line25"));
        // Attempt 2: clean in-scope resolution.
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => ResolveShared("resolved-line5"));

        var workBranch = "codeybox/fence-retry";
        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(
            final!.State == WorkItemState.Done,
            $"state={final.State} err={final.LastError} " +
            $"agenticInvocations={tp.Agent.AgenticConflictInvocations.Count} " +
            $"planRemaining={tp.Agent.ConflictResolutionPlan.Count} " +
            $"reworkPrompts={tp.Agent.ConflictReworkPrompts.Count}");
        Assert.Equal(0, final.ConflictReworkAttempts);

        // The fence rejection burned attempt 1/2 and the retry prompt carried
        // the violation as feedback — proof the check ran INSIDE the resolver
        // loop rather than after it reported success.
        Assert.Equal(2, tp.Agent.AgenticConflictInvocations.Count);
        Assert.Contains(
            "permitted conflict hunks",
            tp.Agent.AgenticConflictInvocations[1],
            StringComparison.Ordinal);

        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (revExit, branchTip, _) = await TestSupport.RunGitNoThrow(
            barePath, "rev-parse", "--verify", $"{workBranch}^{{commit}}");
        Assert.Equal(0, revExit);
        Assert.Equal(final.LocalSquashSha, branchTip.Trim());
    }

    /// <summary>
    /// Landgen #9 trigger: auto-merge race recovery re-runs the merge phase
    /// against a moved base OUTSIDE the merge section's guard-rework loop.
    /// When that re-merge fails semantically (here: a real conflict the
    /// resolver cannot settle), the item must enter the bounded
    /// ReworkingForConflict machine — not park terminally.
    /// </summary>
    [Fact]
    public async Task AutoMergeRaceRerunMergeConflict_RoutesIntoConflictRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // First CompleteAsync races and drifts shared.txt onto main, so the
        // race-recovery re-merge hits a genuine add/add conflict. With no
        // ConflictResolutionPlan entry the resolver exhausts and throws
        // MergeConflictResolutionFailedException — the exact Landgen shape.
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
            _workspace,
            seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge],
            staleBaseReworkOptions: new StalePullRequestSweeperOptions
            {
                RouteToConflictRework = true,
                MaxReworkAttempts = 2,
            });
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side content\n"));

        var item = NewItem("feature/race-remerge-conflict", pushUpstream: true);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Equal(1, remote.CompleteCalls);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    /// <summary>
    /// Bounded fallback: the same race-recovery re-merge failure must still
    /// park at MergeConflictResolutionFailed once the configured conflict-rework
    /// attempt cap is spent — routing is bounded, never an unbounded loop.
    /// </summary>
    [Fact]
    public async Task AutoMergeRaceRerunMergeConflict_ReworkCapExhausted_ParksAtMergeConflictResolutionFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

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
            _workspace,
            seed,
            upstream: new ProjectUpstream
            {
                Kind = "racing-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge],
            staleBaseReworkOptions: new StalePullRequestSweeperOptions
            {
                RouteToConflictRework = true,
                MaxReworkAttempts = 2,
            });
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side content\n"));

        // Already at the cap: routing must decline and the item parks.
        var item = NewItem("feature/race-remerge-capped", pushUpstream: true, conflictAttempts: 2);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(2, final.ConflictReworkAttempts);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(0, queue.Count);
    }

    /// <summary>
    /// Test auditor that advances <c>main</c> in the bare repo to a fixed
    /// content for a known path during the audit phase — the same shape the
    /// conflict-rework suite uses to create "main moved while work was in
    /// flight".
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;
        private readonly string? _additionalPushTarget;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(
            string workspace, string path, string content, string? additionalPushTarget = null)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
            _additionalPushTarget = additionalPushTarget;
        }

        public async Task<AuditResult> RunAsync(
            ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");
            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            if (_additionalPushTarget is not null)
                await TestSupport.RunGit(clone, "push", _additionalPushTarget, $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }
}
