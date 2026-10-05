using System.Diagnostics;
using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for serialized merge landings, bounded guard-triggered rework,
/// the pickup-time "no conflicts to resolve" success path, and per-item
/// merge-retry visibility.
/// </summary>
[Collection("Pipeline integration")]
public sealed class MergeLandingSerializationTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-landing-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewUpstreamItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "serialized landing",
        Prompt = "Change a file",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = true,
    };

    private static WorkItem NewLocalItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "guard rework",
        Prompt = "Implement the foo feature",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    private static async Task<WorkItem> WaitForAsync(
        SqliteWorkItemStore store,
        WorkItemId id,
        Func<WorkItem, bool> ready,
        TimeSpan timeout,
        string what)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        WorkItem? current = null;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            current = await store.GetAsync(id);
            Assert.NotNull(current);
            if (ready(current!))
                return current!;
            if (current!.State
                is WorkItemState.Failed
                or WorkItemState.AuditFailed
                or WorkItemState.MergeConflictResolutionFailed
                or WorkItemState.Cancelled
                or WorkItemState.AbandonedAfterRecoveryAttempts)
            {
                Assert.Fail($"Work item {id} terminally failed while waiting for {what}: state={current.State} error={current.LastError}");
            }

            await Task.Delay(200);
        }

        Assert.Fail($"Timed out waiting for {what}; last state={current?.State} error={current?.LastError}");
        return current!;
    }

    [Fact]
    public async Task MergeLandingGate_SerializesSharedKey()
    {
        var gate = new MergeLandingGate();
        var key = MergeLandingGate.KeyFor("github", "https://example.invalid/o/r", "main");
        var first = gate.Retain(key);
        await first.Semaphore.WaitAsync();
        try
        {
            var secondEntered = false;
            var second = gate.Retain(key);
            var waiter = Task.Run(async () =>
            {
                await second.Semaphore.WaitAsync();
                secondEntered = true;
                second.Semaphore.Release();
                gate.Release(key, second, releaseSemaphore: false);
            });
            await Task.Delay(250);
            Assert.False(secondEntered);
            first.Semaphore.Release();
            await waiter.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(secondEntered);
        }
        finally
        {
            gate.Release(key, first, releaseSemaphore: false);
        }

        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public void MergeLandingGate_KeyRequiresSharedIdentity()
    {
        Assert.Throws<ArgumentException>(() => MergeLandingGate.KeyFor("", "https://example.invalid/o/r", "main"));
        Assert.Throws<ArgumentException>(() => MergeLandingGate.KeyFor("github", "  ", "main"));
        Assert.Throws<ArgumentException>(() => MergeLandingGate.KeyFor("github", "https://example.invalid/o/r", ""));
        Assert.NotEqual(
            MergeLandingGate.KeyFor("github", "https://example.invalid/o/r", "main"),
            MergeLandingGate.KeyFor("github", "https://example.invalid/o/r", "release"));
        Assert.NotEqual(
            MergeLandingGate.KeyFor("github", "https://example.invalid/o/r", "main"),
            MergeLandingGate.KeyFor("github", "https://example.invalid/other", "main"));
    }

    /// <summary>
    /// Two concurrent landings into the same upstream base must serialize
    /// through the shared per-base queue: both land, neither terminally
    /// fails, and the forge never observes overlapping merge calls.
    /// The test holds the shared gate while both items run so they are
    /// guaranteed to contend (no timing luck), then releases and asserts
    /// ordered, non-overlapping landings. The merge-result verification
    /// observes the same queue, so contention now surfaces at the merge
    /// phase (before any forge call) rather than only at upstream push.
    /// </summary>
    [Fact]
    public async Task ConcurrentLandings_OnSameBase_LandSerializedWithoutTerminalFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-serial-landing");
        var remote = new SerialLandingRemote { SeedRepoPath = seed };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "serial-landing", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new SharedLandingFactory(remote));
        remote.BareRepoRoot = tp.GitRoot;
        // Distinct files so the local merges are conflict-free; assignment
        // order across the two items does not matter.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("serial-a.txt", "a\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("serial-b.txt", "b\n"));

        var item1 = NewUpstreamItem("codeybox/serial-one-" + WorkItemId.New().ToString()[..8]);
        var item2 = NewUpstreamItem("codeybox/serial-two-" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item1);
        await tp.Store.CreateAsync(item2);

        var gateKey = MergeLandingGate.KeyFor("serial-landing", seed, "main");
        var hold = MergeLandingGate.Shared.Retain(gateKey);
        await hold.Semaphore.WaitAsync();
        var holdReleased = false;
        void ReleaseHold()
        {
            if (holdReleased)
                return;
            holdReleased = true;
            hold.Semaphore.Release();
            MergeLandingGate.Shared.Release(gateKey, hold, releaseSemaphore: false);
        }

        Task run1 = Task.Run(() => tp.Pipeline.RunAsync(item1, CancellationToken.None));
        Task run2 = Task.CompletedTask;
        try
        {
            // Stagger the starts so the two work-phase queue dequeues cannot
            // coincide; both items still contend on the held landing gate.
            await Task.Delay(500);
            run2 = Task.Run(() => tp.Pipeline.RunAsync(item2, CancellationToken.None));
            await WaitForAsync(tp.Store, item1.Id, i => i.State == WorkItemState.Merging, TimeSpan.FromMinutes(2), "item1 merge");
            await WaitForAsync(tp.Store, item2.Id, i => i.State == WorkItemState.Merging, TimeSpan.FromMinutes(2), "item2 merge");

            // Both items are parked at the shared landing queue (merge-result
            // verification observes it before upstream push does): no forge
            // call yet.
            Assert.Equal(0, remote.CompleteCalls);
            ReleaseHold();

            await Task.WhenAll(run1, run2).WaitAsync(TimeSpan.FromMinutes(5));
        }
        finally
        {
            ReleaseHold();
        }

        var final1 = await tp.Store.GetAsync(item1.Id);
        var final2 = await tp.Store.GetAsync(item2.Id);
        Assert.Equal(WorkItemState.Done, final1!.State);
        Assert.Equal(WorkItemState.Done, final2!.State);
        Assert.Equal(2, remote.CompleteCalls);
        Assert.Equal(1, remote.MaxConcurrentLandings);
        Assert.NotNull(final1.MergeSha);
        Assert.NotNull(final2.MergeSha);
        Assert.NotEqual(final1.MergeSha, final2.MergeSha);
        // No base motion under the gate, so no landing retries were needed.
        Assert.Equal(0, final1.MergeAttempts);
        Assert.Equal(0, final2.MergeAttempts);
        Assert.Null(final1.MergeRetryReason);
    }

    /// <summary>
    /// A scope-fence guard trip (resolver edited outside the permitted
    /// hunks) routes to a conflict-rework turn — briefed with the guard
    /// reason — instead of parking, and the item lands once the rework
    /// resolves the tree.
    /// </summary>
    [Fact]
    public async Task ScopeFenceViolation_RoutesToReworkWithGuardReason_ThenSucceeds()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-scope-fence-rework");
        var auditor = new LandingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));
        // Misbehaving merge resolver: resolves the conflict but also emits
        // an unrelated file, tripping the scope fence.
        tp.Agent.ConflictResolutionPlan.Enqueue(files => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "main side\nwork side\n",
            ["UNRELATED.txt"] = "sneaky\n",
        });
        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            await LandingGit.WriteFileAsync(sandbox, workDir, "README.md", "main side\nwork side\n", ct);
            await LandingGit.RunAsync(sandbox, "git", "-C", workDir, "add", "README.md");
            await LandingGit.RunAsync(sandbox, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", null, null);
        });

        var item = NewLocalItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Single(tp.Agent.ConflictReworkPrompts);
        Assert.Contains("outside the permitted conflict hunks", tp.Agent.ConflictReworkPrompts[0], StringComparison.Ordinal);
        Assert.Contains("outside the permitted conflict hunks", final.MergeRetryReason ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// An auto-merge race against a moving base re-queues through race
    /// recovery and records the retry count plus reason on the item for
    /// operator visibility.
    /// </summary>
    [Fact]
    public async Task AutoMergeRace_RecordsRetryCountAndReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace, "seed-race-visibility");
        var remote = new RacingUpstreamRemote
        {
            SeedRepoPath = seed,
            ResponsePlan =
            {
                new RacingResponse(AutoMergeRaced: true, AdvanceSeedBeforeReturning: true),
            },
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "racing-upstream", AutoMerge = true, MergeMethod = "squash" },
            upstreamFactory: new SingleRemoteFactory(remote),
            mergeStrategy: [MergeStrategy.RealMerge, MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("race-visibility.txt", "work\n"));

        var item = NewUpstreamItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(final.MergeAttempts >= 1);
        Assert.Contains("raced", final.MergeRetryReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeRetryFields_RoundTripThroughStore()
    {
        var db = Path.Combine(_workspace, "merge-retry-rt.db");
        using var store = new SqliteWorkItemStore(db);
        var item = NewLocalItem("codeybox/rt") with
        {
            MergeAttempts = 2,
            MergeRetryReason = "base moved during landing",
        };
        await store.CreateAsync(item);

        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(2, read!.MergeAttempts);
        Assert.Equal("base moved during landing", read.MergeRetryReason);

        await store.UpdateAsync(read with { MergeAttempts = 3, MergeRetryReason = "raced again" });
        var updated = await store.GetAsync(item.Id);
        Assert.Equal(3, updated!.MergeAttempts);
        Assert.Equal("raced again", updated.MergeRetryReason);
    }

    [Fact]
    public void IsResolverGuardFailure_ClassifiesGuards()
    {
        var fence = new MergeConflictResolutionFailedException(
            "merge failed",
            new ScopeFenceViolation(["README.md:1 changed non-conflicted file"]));
        Assert.True(MergeScopeFence.IsResolverGuardFailure(fence));

        var discarded = new MergeConflictResolutionFailedException(
            "conflict-rework agent discarded prior commits (e.g. work to README.md lost); refusing to update work branch");
        Assert.True(MergeScopeFence.IsResolverGuardFailure(discarded));

        var ordinary = new MergeConflictResolutionFailedException(
            "conflict-rework agent did not produce a clean resolution: agent reported failure");
        Assert.False(MergeScopeFence.IsResolverGuardFailure(ordinary));

        var baseMovedExhaustion = new MergeConflictResolutionFailedException(
            "merge landing could not settle",
            new MergeBaseMovedException("repo", "main", "aaa", "bbb"));
        Assert.False(MergeScopeFence.IsResolverGuardFailure(baseMovedExhaustion));
    }

    [Fact]
    public void BaseMovedLanding_DoesNotFeedAgentBreaker()
    {
        // A base-moved landing is environmental motion observed after the
        // agent finished composing the merge — it must neither open nor
        // reset the agent's failure window.
        Assert.Null(PipelineRunner.ClassifyDispatchOutcome(
            new MergeBaseMovedException("repo", "main", "aaa", "bbb"),
            genuineAttemptTimeout: false));
        // Sanity: ordinary agent failures still feed the breaker.
        Assert.False(PipelineRunner.ClassifyDispatchOutcome(
            new InvalidOperationException("agent exploded"),
            genuineAttemptTimeout: false));
    }

    /// <summary>
    /// Pickup-time rebase stopped with no unmerged paths and a staged
    /// resolution: success — the clean rebase continues instead of parking
    /// with "no conflicts to resolve".
    /// </summary>
    [Fact]
    public async Task RebaseNoConflictStop_WithStagedResolution_ContinuesSuccessfully()
    {
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace, "seed-rebase-noconflict-unused"));
        var repoDir = Directory.CreateDirectory(Path.Combine(_workspace, "rebase-staged-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        await LandingGit.RunAsync(repoDir, "init", "-b", "main", ".");
        await LandingGit.RunAsync(repoDir, "config", "user.email", "test@test.com");
        await LandingGit.RunAsync(repoDir, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "base\n");
        await LandingGit.RunAsync(repoDir, "add", "README.md");
        await LandingGit.RunAsync(repoDir, "commit", "-m", "init");
        await LandingGit.RunAsync(repoDir, "checkout", "-qb", "work");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "work\n");
        await LandingGit.RunAsync(repoDir, "commit", "-am", "work change");
        await LandingGit.RunAsync(repoDir, "checkout", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "main\n");
        await LandingGit.RunAsync(repoDir, "commit", "-am", "main change");
        await LandingGit.RunAsync(repoDir, "update-ref", "refs/remotes/origin/main", "main");
        await LandingGit.RunAsync(repoDir, "checkout", "-q", "work");

        // Stage the production state: a rebase stopped mid-way whose
        // resolution is already fully staged (no unmerged paths remain).
        var (rebaseExit, _, _) = await LandingGit.RunAllowFailureAsync(repoDir, "rebase", "main");
        Assert.Equal(1, rebaseExit);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "main\nwork\n");
        await LandingGit.RunAsync(repoDir, "add", "README.md");

        var oldTip = await LandingGit.RevParseAsync(repoDir, "work");
        var sandbox = new LocalGitSandbox(repoDir, failFirstRebase: true);
        var item = NewLocalItem("work");
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "test",
            RepositoryUrl = repoDir,
        };
        var candidates = new AgenticConflictCandidatesResult(
            [new AgenticConflictResolverCandidate(ThrowingAgentRunner.Instance, null)]);

        var result = await tp.Pipeline.RebaseCheckedOutBranchWithScopeFenceAsync(
            item,
            tp.Agent,
            sandbox,
            "test-repo",
            "main",
            "work",
            "origin/main",
            oldTip,
            new MergeScopeHint("test", false),
            project,
            candidates,
            precomputedCandidateFailure: null,
            CancellationToken.None);

        Assert.Empty(result.ConflictFiles);
        Assert.Null(result.ChosenResolver);
        var head = await LandingGit.RevParseAsync(repoDir, "HEAD");
        Assert.False(string.Equals(head, oldTip, StringComparison.Ordinal));
        var readme = await File.ReadAllTextAsync(Path.Combine(repoDir, "README.md"));
        Assert.Equal("main\nwork\n", readme);
        // The resolver was never consulted: there was nothing to resolve.
        Assert.True(sandbox.InitialRebaseIntercepted);
    }

    /// <summary>
    /// Pickup-time rebase stopped on an empty commit: the empty commit is
    /// skipped and the rebase completes instead of parking.
    /// </summary>
    [Fact]
    public async Task RebaseNoConflictStop_EmptyCommit_SkipsAndCompletes()
    {
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace, "seed-rebase-empty-unused"));
        var repoDir = Directory.CreateDirectory(Path.Combine(_workspace, "rebase-empty-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        await LandingGit.RunAsync(repoDir, "init", "-b", "main", ".");
        await LandingGit.RunAsync(repoDir, "config", "user.email", "test@test.com");
        await LandingGit.RunAsync(repoDir, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "base\n");
        await LandingGit.RunAsync(repoDir, "add", "README.md");
        await LandingGit.RunAsync(repoDir, "commit", "-m", "init");
        await LandingGit.RunAsync(repoDir, "checkout", "-qb", "work");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "base\nsame\n");
        await LandingGit.RunAsync(repoDir, "commit", "-am", "work change (duplicated on main)");
        await LandingGit.RunAsync(repoDir, "checkout", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "base\nsame\n");
        await LandingGit.RunAsync(repoDir, "commit", "-am", "main change");
        await LandingGit.RunAsync(repoDir, "update-ref", "refs/remotes/origin/main", "main");
        await LandingGit.RunAsync(repoDir, "checkout", "-q", "work");

        // Force the stopped-at-empty-commit state: a commit whose application
        // is empty against the new base. --reapply-cherry-picks forces the
        // duplicate to replay and --empty=ask pauses exactly here with zero
        // unmerged paths and nothing staged.
        var (emptyExit, _, _) = await LandingGit.RunAllowFailureAsync(repoDir, "rebase", "--reapply-cherry-picks", "--empty=ask", "main");
        Assert.Equal(1, emptyExit);
        // Break the commit identity so `rebase --continue` cannot proceed:
        // this models the production fallback trigger (continue broken for
        // environmental reasons) while the stopped commit provably applied
        // no changes, so --skip is safe.
        await LandingGit.RunAsync(repoDir, "config", "user.email", "");

        var oldTip = await LandingGit.RevParseAsync(repoDir, "work");
        var mainTip = await LandingGit.RevParseAsync(repoDir, "main");
        var sandbox = new LocalGitSandbox(repoDir, failFirstRebase: true);
        var item = NewLocalItem("work");
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "test",
            RepositoryUrl = repoDir,
        };
        var candidates = new AgenticConflictCandidatesResult(
            [new AgenticConflictResolverCandidate(ThrowingAgentRunner.Instance, null)]);

        var result = await tp.Pipeline.RebaseCheckedOutBranchWithScopeFenceAsync(
            item,
            tp.Agent,
            sandbox,
            "test-repo",
            "main",
            "work",
            "origin/main",
            oldTip,
            new MergeScopeHint("test", false),
            project,
            candidates,
            precomputedCandidateFailure: null,
            CancellationToken.None);

        Assert.Empty(result.ConflictFiles);
        Assert.Null(result.ChosenResolver);
        // The provably-empty commit was dropped; the branch now tracks main.
        var head = await LandingGit.RevParseAsync(repoDir, "HEAD");
        Assert.Equal(mainTip, head);
    }

    /// <summary>
    /// Pickup-time rebase stopped with no unmerged paths on a NON-empty
    /// commit (persistent environmental failure): parks with a clear
    /// no-progress reason instead of looping or silently skipping work.
    /// </summary>
    [Fact]
    public async Task RebaseNoConflictStop_NonEmptyCommitNoProgress_ParksWithClearReason()
    {
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            await TestSupport.CreateSeedRepoAsync(_workspace, "seed-rebase-stuck-unused"));
        var repoDir = Directory.CreateDirectory(Path.Combine(_workspace, "rebase-stuck-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        await LandingGit.RunAsync(repoDir, "init", "-b", "main", ".");
        await LandingGit.RunAsync(repoDir, "config", "user.email", "test@test.com");
        await LandingGit.RunAsync(repoDir, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "base\n");
        await LandingGit.RunAsync(repoDir, "add", "README.md");
        await LandingGit.RunAsync(repoDir, "commit", "-m", "init");
        await LandingGit.RunAsync(repoDir, "checkout", "-qb", "work");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "work\n");
        await LandingGit.RunAsync(repoDir, "commit", "-am", "work change");
        await LandingGit.RunAsync(repoDir, "checkout", "-q", "main");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "main\n");
        await LandingGit.RunAsync(repoDir, "commit", "-am", "main change");
        await LandingGit.RunAsync(repoDir, "update-ref", "refs/remotes/origin/main", "main");
        // Clean tree, no rebase in progress: the initial rebase failure is
        // spurious (simulated), and neither --continue nor --skip can make
        // progress on the non-empty stopped commit.
        await LandingGit.RunAsync(repoDir, "checkout", "-q", "work");

        var oldTip = await LandingGit.RevParseAsync(repoDir, "work");
        var sandbox = new LocalGitSandbox(repoDir, failFirstRebase: true);
        var item = NewLocalItem("work");
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "test",
            RepositoryUrl = repoDir,
        };
        var candidates = new AgenticConflictCandidatesResult(
            [new AgenticConflictResolverCandidate(ThrowingAgentRunner.Instance, null)]);

        var ex = await Assert.ThrowsAsync<MergeConflictResolutionFailedException>(() =>
            tp.Pipeline.RebaseCheckedOutBranchWithScopeFenceAsync(
                item,
                tp.Agent,
                sandbox,
                "test-repo",
                "main",
                "work",
                "origin/main",
                oldTip,
                new MergeScopeHint("test", false),
                project,
                candidates,
                precomputedCandidateFailure: null,
                CancellationToken.None));

        Assert.Contains("made no progress", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no conflicts to resolve", ex.Message, StringComparison.Ordinal);
        // The work branch is untouched: nothing was skipped or continued.
        Assert.Equal(oldTip, await LandingGit.RevParseAsync(repoDir, "work"));
    }

    private sealed class LandingAuditor(string workspace, string path, string content) : IAuditor
    {
        private readonly string _workspace = workspace;
        private readonly string _path = path;
        private readonly string _content = content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
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
            return new AuditResult(true, []);
        }
    }
}

/// <summary>
/// Fake upstream remote for the landing-serialization test: every landing
/// succeeds, but each call sleeps inside the critical section while tracking
/// the maximum observed concurrency. A serialized queue keeps the maximum
/// at one; overlapping landings would push it to two.
/// </summary>
internal sealed class SerialLandingRemote : IUpstreamRemote
{
    public required string SeedRepoPath { get; init; }
    public string? BareRepoRoot { get; set; }
    public TimeSpan LandingDelay { get; init; } = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private int _running;
    private int _completeCalls;

    public List<UpstreamCompletionRequest> Requests { get; } = [];
    public int CompleteCalls => Volatile.Read(ref _completeCalls);
    public int MaxConcurrentLandings { get; private set; }

    public string Name => "serial-landing";

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        lock (_gate)
            Requests.Add(request);
        var running = Interlocked.Increment(ref _running);
        lock (_gate)
            MaxConcurrentLandings = Math.Max(MaxConcurrentLandings, running);
        try
        {
            await Task.Delay(LandingDelay, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        Interlocked.Increment(ref _completeCalls);
        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = "https://example.invalid/pr/1",
            PullRequestNumber = 1,
            MergedSha = request.MergeSha ?? "merged-sha",
            AutoMergeRaced = false,
        };
    }

    public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
        => Task.FromResult(true);

    public async Task<string?> FetchBaseBranchAsync(string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        var barePath = Path.Combine(
            BareRepoRoot ?? throw new InvalidOperationException("SerialLandingRemote was not wired with BareRepoRoot"),
            repositoryId + ".git");
        await TestSupport.RunGit(
            barePath, "fetch", "--no-tags",
            SeedRepoPath, $"+refs/heads/{baseBranch}:refs/heads/{baseBranch}");
        var (_, sha, _) = await TestSupport.RunGit(
            barePath, "rev-parse", "--verify", $"refs/heads/{baseBranch}^{{commit}}");
        return sha.Trim();
    }
}

internal sealed class SharedLandingFactory(SerialLandingRemote remote) : IUpstreamRemoteFactory
{
    public IUpstreamRemote Create(Project project) => remote;
}

/// <summary>
/// Agent stub that must never run: the no-conflict rebase tests assert the
/// resolver short-circuits before invoking any candidate, so an invocation
/// is a loud failure.
/// </summary>
internal sealed class ThrowingAgentRunner : IAgentRunner
{
    public static ThrowingAgentRunner Instance { get; } = new();
    public AgentKind Kind => AgentKind.Claude;
    public Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false) =>
        throw new InvalidOperationException("ThrowingAgentRunner must not be invoked when no conflicts exist.");
}

/// <summary>
/// Minimal <see cref="ISandbox"/> that executes <c>git</c> argv against a
/// host temp repo, translating the sandbox work-dir prefix. Non-git commands
/// fail loudly. The first pipeline-shaped <c>git rebase</c> (without
/// <c>--continue</c>/<c>--skip</c>) can be forced to fail once to reproduce
/// the production "rebase failed but nothing is unmerged" state on top of a
/// real, staged sequencer state.
/// </summary>
internal sealed class LocalGitSandbox(string repoDir, bool failFirstRebase) : ISandbox
{
    private bool _interceptArmed = failFirstRebase;

    public string Id => "local-git-sandbox";
    public bool InitialRebaseIntercepted { get; private set; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        var argv = exec.Argv.Select(a => Translate(a)).ToList();
        if (argv.Count > 0
            && string.Equals(argv[0], "git", StringComparison.Ordinal)
            && argv.Any(a => string.Equals(a, "rebase", StringComparison.Ordinal))
            && argv.All(a => !string.Equals(a, "--continue", StringComparison.Ordinal)
                && !string.Equals(a, "--skip", StringComparison.Ordinal)
                && !string.Equals(a, "--abort", StringComparison.Ordinal))
            && _interceptArmed)
        {
            _interceptArmed = false;
            InitialRebaseIntercepted = true;
            return new SandboxExecResult(1, "", "simulated rebase failure with nothing unmerged");
        }

        if (argv.Count == 0 || !string.Equals(argv[0], "git", StringComparison.Ordinal))
            throw new InvalidOperationException($"LocalGitSandbox only executes git argv, got: {string.Join(' ', exec.Argv)}");

        var psi = new ProcessStartInfo(argv[0], argv.Skip(1).Select(Quote).Aggregate("", static (a, b) => a.Length == 0 ? b : a + " " + b))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (k, v) in exec.ExtraEnvironment ?? new Dictionary<string, string>())
            psi.Environment[k] = v;
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new SandboxExecResult(proc.ExitCode, stdout, stderr);

        static string Quote(string a) => a.Length == 0 ? "\"\"" : a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Translate(string arg)
    {
        const string workDir = "/work";
        if (string.Equals(arg, workDir, StringComparison.Ordinal))
            return repoDir;
        if (arg.StartsWith(workDir + "/", StringComparison.Ordinal))
            return repoDir + arg[workDir.Length..];
        return arg;
    }
}

internal static class LandingGit
{
    public static async Task RunAsync(string repoDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git", args.Select(Quote).Aggregate("", static (a, b) => a.Length == 0 ? b : a + " " + b))
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr} {stdout}");

        static string Quote(string a) => a.Length == 0 ? "\"\"" : a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAllowFailureAsync(string repoDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git", args.Select(Quote).Aggregate("", static (a, b) => a.Length == 0 ? b : a + " " + b))
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);

        static string Quote(string a) => a.Length == 0 ? "\"\"" : a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
    }

    public static async Task<string> RevParseAsync(string repoDir, string rev)
    {
        var psi = new ProcessStartInfo("git", $"rev-parse --verify {rev}")
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse {rev} failed");
        return stdout.Trim();
    }

    public static async Task WriteFileAsync(CodeyBox.Core.ISandbox sandbox, string workDir, string path, string contents, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", $"{workDir}/{path}"],
            Stdin = contents,
        }, ct);
        if (!result.Success)
            throw new InvalidOperationException($"failed to write {path}: {result.Stderr}");
    }

    public static async Task RunAsync(CodeyBox.Core.ISandbox sandbox, params string[] args)
    {
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = args }, CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException($"sandbox git failed: {string.Join(' ', args)}: {result.Stderr} {result.Stdout}");
    }
}
