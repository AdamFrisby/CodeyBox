using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class WorkBranchRebaseOnPickupTests : IDisposable
{
    private readonly string _workspace;

    public WorkBranchRebaseOnPickupTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-work-branch-rebase-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task RebaseRetryWithFreshMain()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);

        var (oldB, oldC) = await CommitTwoWorkBranchCommitsAsync(barePath, item.WorkBranch!);
        await CommitToSeedAsync(seed, "main.txt", "main advanced\n", "main advanced");
        var advancedMain = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var rebasedC = await RevParseAsync(barePath, item.WorkBranch!);
        var rebasedB = await RevParseAsync(barePath, $"{item.WorkBranch}~1");
        var rebasedBase = await RevParseAsync(barePath, $"{item.WorkBranch}~2");
        Assert.Equal(advancedMain, rebasedBase);
        Assert.NotEqual(oldB, rebasedB);
        Assert.NotEqual(oldC, rebasedC);
        Assert.Equal("work B\n", await ShowAsync(barePath, $"{item.WorkBranch}:b.txt"));
        Assert.Equal("work C\n", await ShowAsync(barePath, $"{item.WorkBranch}:c.txt"));
    }

    [Fact]
    public async Task NoRebaseOnFirstPickup()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "first pickup\n"));

        var item = NewItem("codeybox/first-pickup");
        var baseTip = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var barePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("first pickup\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task QueuedPickup_MissingWorkBranchCreatesBranchBeforeWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/recovered-missing-branch");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");
        Assert.False(await tp.GitHost.BranchExistsAsync(repoId, item.WorkBranch!));

        var beforeWorkCalls = 0;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            beforeWorkCalls++;
            await AssertSandboxSeesResetOriginWorkBranchAsync(
                sandbox, workingDirectory, item.WorkBranch!, expectPriorFileAbsent: false, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "work after missing branch recovery\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, beforeWorkCalls);
        Assert.True(await tp.GitHost.BranchExistsAsync(repoId, item.WorkBranch!));
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("work after missing branch recovery\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task QueuedPickup_NonOwnedExistingWorkBranchIsResetBeforeWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/recovered-anomalous-branch") with { RecoveryAttempts = 1 };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "prior.txt", "prior attempt\n", "prior attempt");

        var beforeWorkCalls = 0;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            beforeWorkCalls++;
            await AssertSandboxSeesResetOriginWorkBranchAsync(
                sandbox, workingDirectory, item.WorkBranch!, expectPriorFileAbsent: true, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "work after anomalous branch recovery\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, beforeWorkCalls);
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("work after anomalous branch recovery\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
        var priorOnBranch = await TestSupport.RunGitNoThrow(barePath, "show", $"{item.WorkBranch}:prior.txt");
        Assert.NotEqual(0, priorOnBranch.code);
    }

    [Fact]
    public async Task QueuedResume_PreservesExistingWorkBranchBeforeWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/operator-resume") with
        {
            PreserveWorkBranchOnQueuedPickup = true,
        };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var preservedTip = await CommitToBareBranchAsync(
            barePath, item.WorkBranch!, "preserved.txt", "preserved work\n", "preserved work");

        string? observedHead = null;
        string? observedPreservedFile = null;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var head = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "rev-parse", "HEAD"],
            }, ct);
            observedHead = head.Stdout.Trim();

            var preserved = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", $"{workingDirectory}/preserved.txt"],
            }, ct);
            observedPreservedFile = preserved.Stdout;
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "continued work\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.False(final.PreserveWorkBranchOnQueuedPickup);
        Assert.Equal(preservedTip, observedHead);
        Assert.Equal("preserved work\n", observedPreservedFile);
        Assert.Equal(preservedTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("preserved work\n", await ShowAsync(barePath, $"{item.WorkBranch}:preserved.txt"));
        Assert.Equal("continued work\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task ItemStaleRecovery_RecoveredItem_PreservedCommitsRideThroughPickup_AndMergeAdvancesOntoCurrentMain()
    {
        // Acceptance criterion (c): recovery preserves the work branch and
        // the next pickup carries the preserved commits forward; the merge
        // phase then folds them onto current upstream main so the merge
        // commit advances past the new main tip rather than starting over
        // from scratch.
        //
        // Without this end-to-end check, a regression where recovery sets
        // PreserveWorkBranchOnQueuedPickup but the next pickup wipes the
        // branch (or skips the merge-time rebase that lands the preserved
        // commits onto current main) would pass the per-watchdog unit tests.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // Item is currently Working with two prior work-branch commits the
        // pipeline produced on top of the original main tip.
        var item = NewItem("feature/recovery-preserved-rides") with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-100),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-100),
        };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var originalMain = await RevParseAsync(barePath, "main");
        await CommitTwoWorkBranchCommitsAsync(barePath, item.WorkBranch!);
        await tp.Store.CreateAsync(item);

        // Recover via ItemStaleProgressWatchdog — same surface the operator
        // endpoint and periodic sweep use.
        var watchdogOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(60),
            CheckInterval = TimeSpan.FromMinutes(1),
            ItemStaleTimeout = TimeSpan.FromMinutes(90),
            ItemStaleCheckInterval = TimeSpan.FromMinutes(5),
            ItemStaleMaxRecoveryAttempts = 3,
        };
        watchdogOpts.Validate();
        using var registryDb = new SqliteWorkerRegistry(Path.Combine(_workspace, "registry.db"));
        var watchdog = new ItemStaleProgressWatchdog(
            tp.Store, new InMemoryTaskQueue(), registryDb,
            watchdogOpts, NullLogger<ItemStaleProgressWatchdog>.Instance);
        var result = await watchdog.RecoverItemAsync(
            (await tp.Store.GetAsync(item.Id))!,
            "test: simulated stale-updatedAt recovery",
            CancellationToken.None);
        Assert.True(result.Recovered);
        Assert.Equal(WorkItemState.Queued, result.NewState);
        Assert.True(result.BranchPreserved);

        // Advance upstream main AFTER recovery, then run the pipeline on
        // the recovered Queued item. The work branch carries forward into
        // pickup, the agent appends a new commit, the merge phase folds
        // the result onto the advanced main, and the final state is Done.
        await CommitToSeedAsync(seed, "main.txt", "main advanced after recovery\n", "main advanced after recovery");
        var advancedMain = await RevParseAsync(seed, "main");
        Assert.NotEqual(originalMain, advancedMain);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "post-recovery work\n"));
        var recovered = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(recovered!, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Preserved work commits and the new post-recovery commit must all
        // be reachable on the work branch — the pickup did not discard them.
        Assert.Equal("work B\n", await ShowAsync(barePath, $"{item.WorkBranch}:b.txt"));
        Assert.Equal("work C\n", await ShowAsync(barePath, $"{item.WorkBranch}:c.txt"));
        Assert.Equal("post-recovery work\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));

        // Main was advanced and folded in: the merge commit's ancestry
        // reaches the advanced-main tip (recovery did not restart from the
        // pre-advance base).
        Assert.Equal("main advanced after recovery\n", await ShowAsync(barePath, $"main:main.txt"));
        var advancedMainReachable = await IsAncestorAsync(barePath, advancedMain, "main");
        Assert.True(advancedMainReachable,
            "merge must fold the preserved branch onto current main; advanced main tip is unreachable from final main");
    }

    private static async Task<bool> IsAncestorAsync(string repoPath, string ancestor, string descendant)
    {
        var result = await TestSupport.RunGitNoThrow(repoPath, "merge-base", "--is-ancestor", ancestor, descendant);
        return result.code == 0;
    }

    [Fact]
    public async Task QueuedResume_MissingPreservedWorkBranchResetsAndRunsWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/operator-resume-missing") with
        {
            PreserveWorkBranchOnQueuedPickup = true,
        };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var baseTip = await RevParseAsync(barePath, "main");
        Assert.False(await tp.GitHost.BranchExistsAsync(repoId, item.WorkBranch!));

        var beforeWorkCalls = 0;
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            beforeWorkCalls++;
            await AssertSandboxSeesResetOriginWorkBranchAsync(
                sandbox, workingDirectory, item.WorkBranch!, expectPriorFileAbsent: false, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "work after missing preserved branch\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.False(final.PreserveWorkBranchOnQueuedPickup);
        Assert.Equal(1, beforeWorkCalls);
        Assert.True(await tp.GitHost.BranchExistsAsync(repoId, item.WorkBranch!));
        Assert.Equal(baseTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("work after missing preserved branch\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task QueuedExplicitNonOwnedExistingWorkBranchWithoutRecoveryIsPreserved()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/operator-managed");
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var existingTip = await CommitToBareBranchAsync(
            barePath, item.WorkBranch!, "operator.txt", "operator work\n", "operator work");

        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent continuation\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(existingTip, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("operator work\n", await ShowAsync(barePath, $"{item.WorkBranch}:operator.txt"));
        Assert.Equal("agent continuation\n", await ShowAsync(barePath, $"{item.WorkBranch}:agent.txt"));
    }

    [Fact]
    public async Task RebaseConflictCanRouteThroughScopeFenceResolution()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var originalTip = await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "README.md",
            "work branch change\n",
            "work changes readme");

        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");
        tp.Agent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            Assert.Equal("README.md", file.Path);
            Assert.Contains("<<<<<<<", file.Content);
            Assert.Contains("main branch change", file.Content);
            Assert.Contains("work branch change", file.Content);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["README.md"] = "main branch change\nwork branch change\n",
            };
        });

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(tp.Agent.ConflictResolutionPlan);
        Assert.NotEqual(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal("main branch change\nwork branch change\n", await ShowAsync(barePath, $"{item.WorkBranch}:README.md"));
    }

    [Fact]
    public async Task RebaseConflictFailureLeavesWorkBranchAtOriginalTip()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var originalTip = await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "README.md",
            "work branch change\n",
            "work changes readme");

        await CommitToSeedAsync(seed, "README.md", "main branch change\n", "main changes readme");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Contains("pickup-time rebase resolver failed", final.LastError);
        Assert.Equal(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal("work branch change\n", await ShowAsync(barePath, $"{item.WorkBranch}:README.md"));
    }

    [Fact]
    public async Task NoBaseAdvanceIsNoop()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var (_, originalTip) = await CommitTwoWorkBranchCommitsAsync(barePath, item.WorkBranch!);

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
    }

    [Fact]
    public async Task PreservesAuthorship()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "authored.txt",
            "authored work\n",
            "authored work",
            authorName: "Original Author",
            authorEmail: "original@example.com");
        await CommitToSeedAsync(seed, "main.txt", "main advanced\n", "main advanced");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var log = await GitStdoutAsync(barePath, "log", "-1", "--format=%an <%ae>%n%B", item.WorkBranch!);
        Assert.Contains("Original Author <original@example.com>", log);
        Assert.Contains(CodeyBoxTrailers.CoAuthoredBy, log);
    }

    [Fact]
    public async Task RetryAfterMainAdvancesEndToEnd()
    {
        // Retry-from-work (entry = Queued) drops the prior attempt's commits
        // and starts the agent from a pristine base, so the agent observes
        // the freshly-advanced main and stacks its new work on it. The prior
        // attempt's files do NOT carry through to main; the retry-from-work
        // contract is "start over," not "stack on top." See
        // RetryFromWorkResetTests for the dedicated coverage.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "prior.txt", "prior attempt\n", "prior attempt");
        await CommitToSeedAsync(seed, "dependency.txt", "dependency landed\n", "dependency landed");

        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            var observed = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "git -C \"$1\" log -1 --format=%s origin/main > \"$1/observed-origin-main-subject.txt\" && git -C \"$1\" merge-base --is-ancestor origin/main HEAD && test ! -e \"$1/prior.txt\"",
                    "sh",
                    workingDirectory,
                ],
            }, ct);
            if (!observed.Success)
                throw new InvalidOperationException($"agent did not see pristine fresh main: {observed.Stderr}");
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("agent.txt", "agent saw fresh main\n"));

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal("dependency landed\n", await ShowAsync(barePath, "main:observed-origin-main-subject.txt"));
        Assert.Equal("agent saw fresh main\n", await ShowAsync(barePath, "main:agent.txt"));

        // The prior attempt's file must not appear on main — retry-from-work
        // resets the work branch to base before invoking the agent.
        var priorOnMain = await TestSupport.RunGitNoThrow(barePath, "show", "main:prior.txt");
        Assert.NotEqual(0, priorOnMain.code);
    }

    [Fact]
    public async Task ExplicitExistingWorkBranchOutsidePerItemNamespaceIsNotRebased()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/not-isolated") with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "work.txt",
            "work\n",
            "work");
        var originalBase = await RevParseAsync(barePath, "main");
        var originalTip = await RevParseAsync(barePath, item.WorkBranch!);
        await CommitToSeedAsync(seed, "main.txt", "main advanced\n", "main advanced");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal(originalBase, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
        Assert.Equal("work\n", await ShowAsync(barePath, $"{item.WorkBranch}:work.txt"));
    }

    [Fact]
    public async Task MergedResumeDoesNotRebaseWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.Merged };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "work.txt",
            "work\n",
            "work");
        var originalBase = await RevParseAsync(barePath, "main");
        var originalTip = await RevParseAsync(barePath, item.WorkBranch!);
        await CommitToSeedAsync(seed, "main.txt", "main advanced\n", "main advanced");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(originalTip, await RevParseAsync(barePath, item.WorkBranch!));
        Assert.Equal(originalBase, await RevParseAsync(barePath, $"{item.WorkBranch}~1"));
    }

    [Fact]
    public async Task LargeFileWithConflictResolvesInVm()
    {
        // ~288 KB conflicted file: previously this hard-failed because the
        // text-only resolver capped each LLM payload at 128 KiB. The agentic
        // resolver now runs the agent CLI inside the same sandbox, so the
        // agent reads the file directly off disk and writes the resolution
        // back — no payload cap applies.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);

        var prefix = string.Concat(Enumerable.Repeat("prefix line\n", 12_000));
        var suffix = string.Concat(Enumerable.Repeat("suffix line\n", 12_000));
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "big.txt",
            prefix + "work side\n" + suffix,
            "work changes big");
        await CommitToSeedAsync(seed, "big.txt", prefix + "main side\n" + suffix, "main changes big");

        tp.Agent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            Assert.Equal("big.txt", file.Path);
            Assert.Contains("<<<<<<<", file.Content);
            // The full conflicted file is available to the agent — no slicing.
            Assert.True(file.Content.Length > 128 * 1024,
                $"file content should exceed legacy 128 KiB cap, was {file.Content.Length} bytes");
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["big.txt"] = prefix + "main side\nwork side\n" + suffix,
            };
        });

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(tp.Agent.ConflictResolutionPlan);
        var resolved = await ShowAsync(barePath, $"{item.WorkBranch}:big.txt");
        Assert.Equal(prefix + "main side\nwork side\n" + suffix, resolved);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(4, 2)]
    public async Task RebaseAlwaysProducesNonEmptyMergeBaseWithMain(int workCommitCount, int mainAdvanceCount)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitWorkBranchCommitsAsync(barePath, item.WorkBranch!, workCommitCount);
        for (var i = 0; i < mainAdvanceCount; i++)
            await CommitToSeedAsync(seed, $"main-{i}.txt", $"main {i}\n", $"main {i}");
        var refreshedBase = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        await TestSupport.RunGit(barePath, "merge-base", "--is-ancestor", refreshedBase, item.WorkBranch!);
        Assert.Equal(refreshedBase, await GitStdoutTrimAsync(barePath, "merge-base", refreshedBase, item.WorkBranch!));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public async Task RebaseNeverDropsCommits(int workCommitCount, int mainAdvanceCount)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var preRebaseBase = await RevParseAsync(barePath, "main");
        await CommitWorkBranchCommitsAsync(barePath, item.WorkBranch!, workCommitCount);
        var preRebaseCommits = await CommitSnapshotsAsync(barePath, $"{preRebaseBase}..{item.WorkBranch}");

        if (workCommitCount > 0)
            await CommitToSeedAsync(seed, "work-0.txt", "work 0\n", "main independently landed work 0");
        for (var i = 0; i < mainAdvanceCount; i++)
            await CommitToSeedAsync(seed, $"advance-{i}.txt", $"advance {i}\n", $"advance {i}");
        var refreshedBase = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var postRebaseCommits = await CommitSnapshotsAsync(barePath, $"{refreshedBase}..{item.WorkBranch}");
        Assert.Equal(preRebaseCommits.Count, postRebaseCommits.Count);
        foreach (var before in preRebaseCommits)
        {
            Assert.Contains(postRebaseCommits, after =>
                after.Subject == before.Subject
                && after.Body == before.Body
                && after.AuthorName == before.AuthorName
                && after.AuthorEmail == before.AuthorEmail);
        }
    }

    [Fact]
    public async Task RebaseNeverDropsAlreadyUpstreamWorkCommit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem() with { State = WorkItemState.WorkComplete };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var preRebaseBase = await RevParseAsync(barePath, "main");
        await CommitToBareBranchAsync(
            barePath,
            item.WorkBranch!,
            "duplicate.txt",
            "already upstream\n",
            "work independently implemented upstream change",
            authorName: "Original Author",
            authorEmail: "original@example.com");
        var before = Assert.Single(await CommitSnapshotsAsync(barePath, $"{preRebaseBase}..{item.WorkBranch}"));

        await CommitToSeedAsync(seed, "duplicate.txt", "already upstream\n", "main landed equivalent change");
        var refreshedBase = await RevParseAsync(seed, "main");

        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var after = Assert.Single(await CommitSnapshotsAsync(barePath, $"{refreshedBase}..{item.WorkBranch}"));
        Assert.NotEqual(before.Sha, after.Sha);
        Assert.Equal(before.Tree, after.Tree);
        Assert.Equal(before.Subject, after.Subject);
        Assert.Equal(before.Body, after.Body);
        Assert.Equal(before.AuthorName, after.AuthorName);
        Assert.Equal(before.AuthorEmail, after.AuthorEmail);
        Assert.Contains(CodeyBoxTrailers.CoAuthoredBy, after.Body);
    }

    private async Task<(string B, string C)> CommitTwoWorkBranchCommitsAsync(string barePath, string branch)
    {
        var clone = await CloneForCommitAsync(barePath);
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        await WriteAndCommitAsync(clone, "b.txt", "work B\n", "work B");
        var b = await RevParseAsync(clone, "HEAD");
        await WriteAndCommitAsync(clone, "c.txt", "work C\n", "work C");
        var c = await RevParseAsync(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return (b, c);
    }

    private async Task CommitWorkBranchCommitsAsync(string barePath, string branch, int count)
    {
        var clone = await CloneForCommitAsync(barePath);
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        for (var i = 0; i < count; i++)
            await WriteAndCommitAsync(clone, $"work-{i}.txt", $"work {i}\n", $"work {i}");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
    }

    private async Task<string> CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject,
        string authorName = "Test",
        string authorEmail = "test@test.com")
    {
        var clone = await CloneForCommitAsync(barePath);
        await TestSupport.RunGit(clone, "config", "user.email", authorEmail);
        await TestSupport.RunGit(clone, "config", "user.name", authorName);
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        await WriteAndCommitAsync(clone, fileName, contents, subject);
        var sha = await RevParseAsync(clone, "HEAD");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return sha;
    }

    private async Task<string> CloneForCommitAsync(string barePath)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        return clone;
    }

    private static async Task WriteAndCommitAsync(string repoPath, string path, string content, string subject)
    {
        var fullPath = Path.Combine(repoPath, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
    }

    private static async Task CommitToSeedAsync(string repoPath, string path, string content, string message)
    {
        await TestSupport.RunGit(repoPath, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(repoPath, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, path), content);
        await TestSupport.RunGit(repoPath, "add", path);
        await TestSupport.RunGit(repoPath, "commit", "-m", message);
    }

    private static async Task<string> ShowAsync(string repoPath, string rev)
        => await GitStdoutAsync(repoPath, "show", rev);

    private static async Task<string> RevParseAsync(string repoPath, string rev)
        => (await GitStdoutAsync(repoPath, "rev-parse", rev)).Trim();

    private static async Task<string> GitStdoutAsync(string repoPath, params string[] args)
    {
        var (_, stdout, _) = await TestSupport.RunGit(repoPath, args);
        return stdout;
    }

    private static async Task<string> GitStdoutTrimAsync(string repoPath, params string[] args)
        => (await GitStdoutAsync(repoPath, args)).Trim();

    private static async Task AssertSandboxSeesResetOriginWorkBranchAsync(
        ISandbox sandbox,
        string workingDirectory,
        string workBranch,
        bool expectPriorFileAbsent,
        CancellationToken ct)
    {
        var script = """
            set -eu
            workdir="$1"
            branch="$2"
            expect_prior_absent="$3"
            base="$(git -C "$workdir" rev-parse origin/main)"
            head="$(git -C "$workdir" rev-parse HEAD)"
            work="$(git -C "$workdir" rev-parse "origin/$branch")"
            test "$head" = "$base"
            test "$work" = "$base"
            if [ "$expect_prior_absent" = "true" ]; then
              test ! -e "$workdir/prior.txt"
            fi
            """;
        var observed = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", script, "sh", workingDirectory, workBranch, expectPriorFileAbsent ? "true" : "false"],
        }, ct);
        if (!observed.Success)
            throw new InvalidOperationException(
                $"agent did not see reset origin work branch '{workBranch}': {observed.Stdout}{observed.Stderr}");
    }

    private static async Task<IReadOnlyList<CommitSnapshot>> CommitSnapshotsAsync(string repoPath, string revisionRange)
    {
        var revs = (await GitStdoutAsync(repoPath, "rev-list", "--reverse", revisionRange))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var commits = new List<CommitSnapshot>();
        foreach (var rev in revs)
        {
            commits.Add(new CommitSnapshot(
                Sha: await RevParseAsync(repoPath, rev),
                Tree: await RevParseAsync(repoPath, $"{rev}^{{tree}}"),
                Subject: (await GitStdoutAsync(repoPath, "log", "-1", "--format=%s", rev)).TrimEnd('\n'),
                Body: await GitStdoutAsync(repoPath, "log", "-1", "--format=%B", rev),
                AuthorName: (await GitStdoutAsync(repoPath, "log", "-1", "--format=%an", rev)).TrimEnd('\n'),
                AuthorEmail: (await GitStdoutAsync(repoPath, "log", "-1", "--format=%ae", rev)).TrimEnd('\n')));
        }

        return commits;
    }

    private sealed record CommitSnapshot(
        string Sha,
        string Tree,
        string Subject,
        string Body,
        string AuthorName,
        string AuthorEmail);

    private static WorkItem NewItem(string? workBranch = null)
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "test",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = workBranch ?? $"codeybox/{id.ToString()[..8]}",
            PushUpstream = false,
        };
    }
}
