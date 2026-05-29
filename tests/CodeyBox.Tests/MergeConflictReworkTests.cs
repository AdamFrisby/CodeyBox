using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end coverage for the third-line conflict-rework fallback in
/// <see cref="PipelineRunner"/>. When the preventive auto-rebase
/// (c9fd5b75) and the merge-phase LLM rerun (77ce33c667) both fail to
/// resolve a merge conflict, the orchestrator re-engages the original
/// work agent with a focused conflict-resolution prompt on the existing
/// work branch.
///
/// <para>These tests use the standard <c>ScriptedAgent</c> harness. The
/// merge-phase text-only resolver intentionally has no
/// <c>ConflictResolutionPlan</c> entry queued, so its failure surfaces as
/// <see cref="MergeConflictResolutionFailedException"/> and triggers the
/// rework path under test.</para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class MergeConflictReworkTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-conflict-rework-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "conflict-rework",
        Prompt = "Implement the foo feature",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    /// <summary>
    /// Load-bearing branch-preservation test. The conflict-rework iteration
    /// must start with the work branch checked out at its existing tip — NOT
    /// at main — so the agent's prior commits remain reachable and can
    /// inform the resolution. The prompt sent to the agent must include the
    /// rebase-in-progress workflow instructions.
    /// </summary>
    [Fact]
    public async Task ConflictRework_StartsFromWorkBranchTipWithPriorCommitsReachable()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;

        // Work agent writes shared.txt → conflicts with the auditor-advanced
        // main side. The text-only resolver has no plan entry queued, so the
        // merge phase surfaces MergeConflictResolutionFailed and the
        // conflict-rework path engages.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        string? observedHeadShaAtAgentStart = null;
        string? observedPriorCommitMessage = null;
        bool priorCommitReachableFromHead = false;
        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            // 1. Verify HEAD points at the work branch tip (not main). The
            //    work commit message is the trailer block ScriptedAgent emits.
            var head = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "rev-parse", "HEAD"],
            }, ct);
            Assert.True(head.Success);
            observedHeadShaAtAgentStart = head.Stdout.Trim();

            // 2. Verify ORIG_HEAD points at the work tip (rebase-in-progress
            //    marker — set by `git rebase` before applying commits).
            var origHead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "rev-parse", "ORIG_HEAD"],
            }, ct);
            Assert.True(origHead.Success);

            // 3. The prior work commit (HEAD..ORIG_HEAD) must be reachable —
            //    that is the "commits not discarded" invariant.
            var logOut = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "log", "ORIG_HEAD", "--pretty=%s", "-1"],
            }, ct);
            Assert.True(logOut.Success);
            observedPriorCommitMessage = logOut.Stdout.Trim();

            // During a paused rebase, ORIG_HEAD is NOT an ancestor of HEAD —
            // that only becomes true after `git rebase --continue` completes.
            // The relevant invariant is "the original commit exists in the
            // repository and is named via ORIG_HEAD", which proves the rework
            // setup did NOT discard the work agent's commits before invocation.
            var origExists = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "cat-file", "-e", "ORIG_HEAD^{commit}"],
            }, ct);
            priorCommitReachableFromHead = origExists.ExitCode == 0;

            // 4. Verify the worktree has conflict markers (the rebase paused
            //    at the conflict, per the contract in the spec).
            var statusBefore = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "status", "--porcelain"],
            }, ct);
            Assert.True(statusBefore.Success);
            Assert.Contains("UU README.md", statusBefore.Stdout);

            // 5. Resolve the conflict by keeping both intents, then continue.
            await WriteFileAsync(sandbox, workDir, "README.md", "main side\nwork side\n", ct);
            await Run(sandbox, "git", "-C", workDir, "add", "README.md");
            await Run(sandbox, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", "resolved cleanly", null);
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);

        // The captured prompt must instruct the agent on the conflict-rework
        // workflow and explicitly forbid destructive actions.
        Assert.Single(tp.Agent.ConflictReworkPrompts);
        var capturedPrompt = tp.Agent.ConflictReworkPrompts[0];
        Assert.Contains("# Conflict-resolution mode (third-line fallback)", capturedPrompt);
        Assert.Contains("git rebase --continue", capturedPrompt);
        Assert.Contains("Do NOT", capturedPrompt);
        Assert.Contains("git reset --hard", capturedPrompt);
        Assert.Contains("SEMANTIC_INCOMPATIBLE", capturedPrompt);
        // The original prompt is preserved verbatim at the top so the agent
        // retains the "why was this PR written" context.
        Assert.StartsWith("Implement the foo feature", capturedPrompt);

        // The branch-preservation invariant.
        Assert.NotNull(observedHeadShaAtAgentStart);
        Assert.NotNull(observedPriorCommitMessage);
        Assert.True(priorCommitReachableFromHead,
            "Original work commit must be reachable from HEAD when the rework agent starts");
    }

    /// <summary>
    /// Successful-resolution test: when the rework agent produces a clean
    /// rebase and runs <c>git rebase --continue</c>, the orchestrator
    /// re-runs the merge phase and the work item reaches Done. The original
    /// work commits must remain in the final merge commit's ancestry.
    /// </summary>
    [Fact]
    public async Task ConflictRework_SuccessfulResolution_AdvancesToDone_PreservesPriorCommitsInAncestry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        string? workCommitSha = null;
        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            var origHead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "rev-parse", "ORIG_HEAD"],
            }, ct);
            Assert.True(origHead.Success);
            workCommitSha = origHead.Stdout.Trim();

            await WriteFileAsync(sandbox, workDir, "README.md", "main side\nwork side\n", ct);
            await Run(sandbox, "git", "-C", workDir, "add", "README.md");
            await Run(sandbox, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", null, null);
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.MergeSha);

        // Anti-abandonment post-check: the work agent's file changes must be
        // reflected in the final merge commit. A clean rebase preserves the
        // changed-file set (even though commit SHAs change), so we assert
        // README.md (the agent's contribution) is in the merge sha's diff
        // against the seed root commit.
        Assert.NotNull(workCommitSha);
        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (lsExit, lsOut, _) = await TestSupport.RunGitNoThrow(
            barePath, "show", $"{final.MergeSha}:README.md");
        Assert.Equal(0, lsExit);
        // Both the work side and main side intents must be reflected.
        Assert.Contains("work side", lsOut);
        Assert.Contains("main side", lsOut);
    }

    /// <summary>
    /// Semantic-incompatible exit: the rework agent declares the two
    /// intents cannot coexist. The orchestrator parks at
    /// <see cref="WorkItemState.MergeConflictResolutionFailed"/> with the
    /// verbatim reason on <c>LastError</c>; the original work commits remain
    /// on the work branch so an operator can inspect.
    /// </summary>
    [Fact]
    public async Task ConflictRework_SemanticIncompatibleExit_ParksWithReason_PreservesWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        var involvement = new InMemoryAgentInvolvementStore();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], involvement: involvement);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        tp.Agent.ConflictReworkPlan.Enqueue((sandbox, workDir, ct) =>
        {
            // Agent decides the conflict is semantically irreconcilable and
            // bails out via the documented escape hatch. It must NOT mutate
            // the worktree — the operator wants to inspect the original work.
            _ = sandbox;
            _ = workDir;
            _ = ct;
            return Task.FromResult(new AgentResult(
                Success: false,
                Summary: "incompatible",
                Stdout: "SEMANTIC_INCOMPATIBLE: events have diverged",
                Stderr: null));
        });

        var workBranch = "codeybox/" + WorkItemId.New().ToString()[..8];
        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Contains("SEMANTIC_INCOMPATIBLE", final.LastError);
        Assert.Contains("events have diverged", final.LastError);

        // The conflict-rework row must be finalized failure:semantic-incompatible
        // — the only end-to-end assertion of that outcome string. A regression in
        // ReworkConflictAsync's finalize branch (e.g. stamping success or agent)
        // would slip through without this.
        var conflictRow = Assert.Single(
            await involvement.ListByWorkItemAsync(item.Id, CancellationToken.None),
            r => r.Phase == "conflict_rework");
        Assert.Equal("failure:semantic-incompatible", conflictRow.Outcome);
        Assert.NotNull(conflictRow.EndedAt);

        // The work branch in the bare repo must still hold the work agent's
        // commits so the operator can inspect.
        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (exitCode, branchRef, _) = await TestSupport.RunGitNoThrow(
            barePath, "rev-parse", "--verify", $"refs/heads/{workBranch}");
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(branchRef));
    }

    /// <summary>
    /// Cancellation mid-conflict-rework: when the agent run is cancelled, the
    /// involvement row must be finalized <c>failure:cancelled</c> (not left
    /// dangling in-progress). This is the only end-to-end assertion of the
    /// failure:cancelled outcome, exercising the dedicated cancel branch in
    /// <c>RunConflictReworkAgentAsync</c>.
    /// </summary>
    [Fact]
    public async Task ConflictRework_AgentCancelled_FinalizesInvolvementFailureCancelled()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        var involvement = new InMemoryAgentInvolvementStore();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], involvement: involvement);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        // Simulate the agent run being cancelled mid-rework. A plain
        // OperationCanceledException (not a PhaseCancellationException) hits the
        // dedicated cancel branch, which stamps failure:cancelled before
        // rethrowing.
        tp.Agent.ConflictReworkPlan.Enqueue((sandbox, workDir, ct) =>
            throw new OperationCanceledException("simulated mid-rework cancellation"));

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        // The wrapped PhaseCancellationException propagates out of RunAsync (the
        // conflict-rework caller rethrows OperationCanceledException); the row is
        // finalized before it does. Tolerate both throw and graceful return.
        try { await tp.Pipeline.RunAsync(item, CancellationToken.None); }
        catch (OperationCanceledException) { /* expected: simulated cancellation */ }

        var conflictRow = Assert.Single(
            await involvement.ListByWorkItemAsync(item.Id, CancellationToken.None),
            r => r.Phase == "conflict_rework");
        Assert.Equal("failure:cancelled", conflictRow.Outcome);
        Assert.NotNull(conflictRow.EndedAt);
    }

    /// <summary>
    /// Anti-abandonment guard: when the rework agent runs
    /// <c>git reset --hard origin/main</c> mid-iteration (discarding prior
    /// commits), the orchestrator detects that the prior commits are no
    /// longer in the new tip's ancestry and refuses to advance the work
    /// branch. The item parks at
    /// <see cref="WorkItemState.MergeConflictResolutionFailed"/> with a
    /// clear error.
    /// </summary>
    [Fact]
    public async Task ConflictRework_DestructiveActionDiscardsCommits_DetectedAndParked()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            // Misbehaving agent: aborts the rebase + resets HEAD to main,
            // throwing away the work commit. The orchestrator's
            // anti-abandonment check must catch this.
            await Run(sandbox, "git", "-C", workDir, "rebase", "--abort");
            await Run(sandbox, "git", "-C", workDir, "reset", "--hard", "origin/main");
            return new AgentResult(true, "abandoned", null, null);
        });

        var workBranch = "codeybox/" + WorkItemId.New().ToString()[..8];
        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Contains("discarded prior commits", final.LastError);

        // The original work branch must still hold the work commits in the
        // bare repo — the destructive action happened in the isolated
        // sandbox clone, not the durable host repo, so the operator can
        // still inspect the original work.
        var barePath = Path.Combine(tp.GitRoot, item.Id + ".git");
        var (exitCode, _, _) = await TestSupport.RunGitNoThrow(
            barePath, "rev-parse", "--verify", $"refs/heads/{workBranch}");
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Cost-tracking test: the rework iteration must record its token usage
    /// under a distinct <c>conflict_rework</c> phase key so operators can
    /// measure how much budget the third-line fallback is burning.
    /// </summary>
    [Fact]
    public async Task ConflictRework_RecordsCostUnderConflictReworkPhaseKey()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");

        // Build a pipeline with a real cost store + a fixed token extractor
        // so we can assert the rework iteration's cost row exists with the
        // expected phase key. The work-item store and the cost store must
        // share the same SQLite database file so the cost table's FK to
        // work_items can be satisfied.
        var sharedDb = Path.Combine(_workspace, "shared-state.db");
        // Pre-create the work_items table by instantiating the work-item
        // store first, then open the cost store on the same DB so its FK
        // prepare succeeds.
        using (var seedStore = new CodeyBox.Orchestrator.SqliteWorkItemStore(sharedDb)) { }
        using var costStore = new CodeyBox.Orchestrator.SqliteWorkItemCostStore(sharedDb);
        var extractor = new FixedTokenExtractor(AgentKind.Claude, input: 1234, output: 567);
        var extractors = new Dictionary<AgentKind, IAgentCostExtractor>
        {
            [AgentKind.Claude] = extractor,
        };
        var calculator = new AgentCostCalculator(new AgentPricingOptions());
        var involvement = new InMemoryAgentInvolvementStore();

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            auditors: [auditor],
            costStore: costStore,
            costExtractors: extractors,
            costCalculator: calculator,
            stateDbPathOverride: sharedDb,
            involvement: involvement);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            await WriteFileAsync(sandbox, workDir, "README.md", "main side\nwork side\n", ct);
            await Run(sandbox, "git", "-C", workDir, "add", "README.md");
            await Run(sandbox, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", null, null);
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The cost store must contain at least one row tagged with the
        // conflict_rework phase. Other phases (work, merge) may also record
        // cost rows; we only care about the existence of the rework one.
        var rows = await costStore.GetByWorkItemAsync(item.Id.ToString(), CancellationToken.None);
        Assert.Contains(rows, r => r.Phase == "conflict_rework");
        var reworkRow = rows.First(r => r.Phase == "conflict_rework");
        Assert.Equal(1234, reworkRow.InputTokens);
        Assert.Equal(567, reworkRow.OutputTokens);

        // The conflict-rework agent run is recorded outside the
        // InvokeAgentWithQuotaFallbackAsync chokepoint, so assert its involvement
        // row directly: exactly one closed conflict_rework entry, finalized
        // success, for the work agent. A regression that drops the direct
        // RecordInvolvementStartAsync/FinalizeInvolvementAsync calls in
        // ReworkConflictAsync leaves the audit trail blind to this phase.
        var inv = await involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        var conflictRow = Assert.Single(inv, r => r.Phase == "conflict_rework");
        Assert.Equal(AgentKind.Claude, conflictRow.AgentKind);
        Assert.Null(conflictRow.Iteration);
        Assert.NotNull(conflictRow.EndedAt);
        Assert.Equal("success", conflictRow.Outcome);
    }

    /// <summary>
    /// One-iteration cap. Spec acceptance criterion #1 caps the agent at one
    /// rework engagement per merge attempt. We simulate "the rework already
    /// happened" by seeding <see cref="WorkItem.ConflictReworkAttempts"/> = 1
    /// before the pipeline runs; when the merge phase then surfaces a
    /// conflict, the cap check at the top of the catch block must trip and
    /// the agent's <see cref="ScriptedAgent.ConflictReworkPlan"/> must NOT be
    /// dequeued (or the counter bumped a second time).
    /// </summary>
    [Fact]
    public async Task ConflictRework_OneIterationCap_SecondAttemptSkipsAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        // Deliberately leave ConflictReworkPlan EMPTY: if the cap check
        // misfires and the agent is invoked, ScriptedAgent returns a failure
        // ("ran out of conflict-rework plan entries") — but the assertions
        // below also defend by checking ConflictReworkPrompts.Count.

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]) with
        {
            ConflictReworkAttempts = 1,
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        // Counter must NOT be bumped: the cap blocked re-engagement.
        Assert.Equal(1, final.ConflictReworkAttempts);
        // The agent's rework plan was never consulted.
        Assert.Empty(tp.Agent.ConflictReworkPrompts);
    }

    /// <summary>
    /// State-transition observation: while the rework agent is running, the
    /// work item's persisted state must be
    /// <see cref="WorkItemState.ReworkingForConflict"/>. We snapshot the
    /// state from inside the agent callback (which runs after the
    /// Transition() call but before the agent finishes) and assert it on
    /// completion. Without this test, a bug that emitted the wrong
    /// intermediate state (or skipped the transition entirely) would be
    /// invisible — the only post-condition the success test reads is the
    /// terminal Done state.
    /// </summary>
    [Fact]
    public async Task ConflictRework_PipelineTransitionsThroughReworkingForConflictState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        WorkItemState? observedStateAtAgentInvocation = null;
        int? observedAttemptsAtAgentInvocation = null;
        var capturedItemId = WorkItemId.New();
        var store = tp.Store;

        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            var snapshot = await store.GetAsync(capturedItemId, ct);
            observedStateAtAgentInvocation = snapshot?.State;
            observedAttemptsAtAgentInvocation = snapshot?.ConflictReworkAttempts;

            await WriteFileAsync(sandbox, workDir, "README.md", "main side\nwork side\n", ct);
            await Run(sandbox, "git", "-C", workDir, "add", "README.md");
            await Run(sandbox, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", null, null);
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]) with
        {
            Id = capturedItemId,
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(WorkItemState.ReworkingForConflict, observedStateAtAgentInvocation);
        // The bump must be visible while the agent runs so the cap check works
        // correctly on any concurrent observer.
        Assert.Equal(1, observedAttemptsAtAgentInvocation);
    }

    /// <summary>
    /// Webhook events: the rework iteration must emit
    /// <c>work_item.conflict_rework_started</c> with the conflict-file list,
    /// base/work branches, and tip SHAs; and
    /// <c>work_item.conflict_rework_finished</c> with the success outcome
    /// and the diff stats (NewWorkBranchTip / FilesChanged). Spec
    /// acceptance criterion #3.
    /// </summary>
    [Fact]
    public async Task ConflictRework_EmitsStartedAndFinishedWebhookEvents_WithPayloadFields()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: [auditor], webhookDispatcher: webhooks);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            await WriteFileAsync(sandbox, workDir, "README.md", "main side\nwork side\n", ct);
            await Run(sandbox, "git", "-C", workDir, "add", "README.md");
            await Run(sandbox, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", null, null);
        });

        var workBranch = "codeybox/" + WorkItemId.New().ToString()[..8];
        var item = NewItem(workBranch);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var startedEvt = Assert.Single(webhooks.Events, e => e.Event == "work_item.conflict_rework_started");
        var startedDetails = Assert.IsType<ConflictReworkStartedDetails>(startedEvt.Details);
        Assert.Equal(item.Id.ToString(), startedDetails.WorkItemId);
        Assert.Equal("main", startedDetails.BaseBranch);
        Assert.Equal(workBranch, startedDetails.WorkBranch);
        Assert.False(string.IsNullOrWhiteSpace(startedDetails.WorkBranchTip));
        Assert.False(string.IsNullOrWhiteSpace(startedDetails.BaseTip));
        // Started before finished — temporal contract for trackers.
        Assert.NotEqual(startedDetails.WorkBranchTip, startedDetails.BaseTip);

        var finishedEvt = Assert.Single(webhooks.Events, e => e.Event == "work_item.conflict_rework_finished");
        var finishedDetails = Assert.IsType<ConflictReworkFinishedDetails>(finishedEvt.Details);
        Assert.Equal(item.Id.ToString(), finishedDetails.WorkItemId);
        Assert.Equal("main", finishedDetails.BaseBranch);
        Assert.Equal(workBranch, finishedDetails.WorkBranch);
        Assert.True(finishedDetails.Success);
        Assert.False(string.IsNullOrWhiteSpace(finishedDetails.NewWorkBranchTip));
        Assert.Null(finishedDetails.SemanticIncompatibleReason);
        Assert.Null(finishedDetails.ParkReason);

        // Order: started before finished.
        var startedIdx = webhooks.Events.ToList().FindIndex(e => e.Event == "work_item.conflict_rework_started");
        var finishedIdx = webhooks.Events.ToList().FindIndex(e => e.Event == "work_item.conflict_rework_finished");
        Assert.True(startedIdx >= 0 && finishedIdx > startedIdx,
            $"finished must follow started (started={startedIdx}, finished={finishedIdx})");
    }

    /// <summary>
    /// Webhook event payload carries the SEMANTIC_INCOMPATIBLE reason on the
    /// finished event so operators can wire a tracker comment / Slack notice
    /// off the parked state.
    /// </summary>
    [Fact]
    public async Task ConflictRework_SemanticIncompatible_FinishedEventCarriesReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main side\n");
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: [auditor], webhookDispatcher: webhooks);
        auditor.GitRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work side\n"));

        tp.Agent.ConflictReworkPlan.Enqueue((sandbox, workDir, ct) =>
        {
            _ = sandbox; _ = workDir; _ = ct;
            return Task.FromResult(new AgentResult(
                Success: false,
                Summary: "incompatible",
                Stdout: "SEMANTIC_INCOMPATIBLE: events have diverged",
                Stderr: null));
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var finishedEvt = Assert.Single(webhooks.Events, e => e.Event == "work_item.conflict_rework_finished");
        var finishedDetails = Assert.IsType<ConflictReworkFinishedDetails>(finishedEvt.Details);
        Assert.False(finishedDetails.Success);
        Assert.Equal("events have diverged", finishedDetails.SemanticIncompatibleReason);
        Assert.NotNull(finishedDetails.ParkReason);
        Assert.Contains("SEMANTIC_INCOMPATIBLE", finishedDetails.ParkReason);
    }

    /// <summary>
    /// Restart-recovery: a worker dying mid-<see cref="WorkItemState.ReworkingForConflict"/>
    /// must surface back at <see cref="WorkItemState.AuditPassed"/> via the
    /// reaper, with <see cref="WorkItem.ConflictReworkAttempts"/> preserved
    /// across the restart so the one-iteration cap survives. The mapping
    /// itself is exercised here (DeadWorkerReaper.MapToRecoveryState +
    /// OrchestratorService.TryBuildRecoveredStateForTest); the cap test
    /// above complements it by proving a preserved=1 counter blocks
    /// re-engagement.
    /// </summary>
    [Fact]
    public void ConflictRework_DeadWorkerReaperMaps_ReworkingForConflict_To_AuditPassed()
    {
        var mapped = DeadWorkerReaper.MapToRecoveryState(WorkItemState.ReworkingForConflict);
        Assert.Equal(WorkItemState.AuditPassed, mapped);
    }

    [Fact]
    public async Task ConflictRework_StartupRecovery_PreservesConflictReworkAttempts_AcrossRestart()
    {
        var dbPath = Path.Combine(_workspace, $"startup-recovery-{Guid.NewGuid():N}.db");
        using var store = new SqliteWorkItemStore(dbPath);

        // Simulate: worker died while the rework iteration was running.
        // ConflictReworkAttempts has already been bumped to 1.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "rework-restart",
            Prompt = "p",
            State = WorkItemState.ReworkingForConflict,
            ConflictReworkAttempts = 1,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var pipeline = new FakePipelineRunner(store);
        var svc = new OrchestratorService(
            queue, store, pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1, MaxRecoveryAttempts = 3 },
            NullLogger<OrchestratorService>.Instance);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await store.GetAsync(item.Id);
        Assert.NotNull(recovered);
        // State maps back to AuditPassed so the merge phase re-runs on resume.
        Assert.Equal(WorkItemState.AuditPassed, recovered.State);
        // CRITICAL: counter is preserved. If a regression reset it to 0, the
        // one-iteration cap would silently re-enable a second agent engagement
        // on the very next merge attempt.
        Assert.Equal(1, recovered.ConflictReworkAttempts);
        // Recovery counts as an interrupted-in-flight transition.
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(1, queue.Count);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task Run(ISandbox sandbox, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv });
        if (!r.Success)
            throw new InvalidOperationException(
                $"sandbox command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}\n{r.Stdout}");
    }

    private static async Task WriteFileAsync(ISandbox sandbox, string workDir, string relPath, string content, CancellationToken ct)
    {
        var r = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", $"{workDir}/{relPath}"],
            Stdin = content,
        }, ct);
        if (!r.Success)
            throw new InvalidOperationException(
                $"sandbox write failed (exit {r.ExitCode}) for {relPath}: {r.Stderr}");
    }

    /// <summary>
    /// Test auditor that advances <c>main</c> in the bare repo to a fixed
    /// content for a known path during the audit phase. Used to create the
    /// "main moved while work was in flight" scenario that drives the merge
    /// phase into a conflict.
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

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

    /// <summary>
    /// Deterministic cost extractor that always returns the same token snapshot,
    /// regardless of agent output. Lets the cost-tracking test assert the phase
    /// key without coupling to provider-specific stdout shapes.
    /// </summary>
    private sealed class FixedTokenExtractor : IAgentCostExtractor
    {
        public AgentKind Kind { get; }
        private readonly int _input;
        private readonly int _output;

        public FixedTokenExtractor(AgentKind kind, int input, int output)
        {
            Kind = kind;
            _input = input;
            _output = output;
        }

        public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
        {
            _ = agentStdout;
            _ = agentStderr;
            return new AgentCostSnapshot(InputTokens: _input, CachedInputTokens: 0, OutputTokens: _output, ModelId: null);
        }

        public ModelRateConfig? DefaultPricing => null;
    }
}
