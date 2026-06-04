using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

/// <summary>
/// Pipeline-level coverage for the prompt-revision wiring inside
/// <see cref="PipelineRunner"/>. The store-level primitives are covered in
/// <see cref="PromptRevisionStoreTests"/>; this file pins the orchestrator's
/// use of them — the env-var injected into the sandbox, the dispatch-row
/// recording call sites, the rework re-read, the commit-trailer forwarding,
/// and <see cref="PipelineRunner.BuildTerminalRevisionAsync"/>'s branches —
/// so a regression in any of these load-bearing wires would surface here
/// rather than only at the wire-shape level.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerPromptRevisionTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-pr-promptrev-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Prompt revision test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    // ── RecordIterationDispatchAsync call sites ────────────────────────────

    [Fact]
    public async Task WorkPhase_RecordsIterationDispatch_AtRevisionOne()
    {
        // PipelineRunner.cs:347 records the work-phase dispatch row BEFORE the
        // Working transition. A regression that skips the call (or records it
        // after the agent finished, or with item.PromptRevision read from a
        // stale snapshot) would silently break the dispatch ledger and the
        // trailer auditor would degrade to the legacy/no-revision branch.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "hello\n"));

        var item = NewItem("feature/dispatch-row-work");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var rows = await tp.Store.GetIterationsAsync(item.Id);
        // At minimum the work-phase row must exist with revision 1.
        var row1 = Assert.Single(rows, r => r.Iteration == 1);
        Assert.Equal(1, row1.PromptRevisionAtDispatch);
        Assert.Equal(item.Id, row1.WorkItemId);
    }

    [Fact]
    public async Task ReworkPhase_RecordsIterationDispatch_AtFreshRevision()
    {
        // PipelineRunner.cs:2126-2134 re-reads the work item before recording
        // the rework dispatch row so a PUT /workitems/{id}/prompt landing
        // between the work commit and the rework dispatch lands at the new
        // revision rather than the orchestrator's stale snapshot. This test
        // simulates that edit via the auditor (the auditor runs between the
        // work iteration and the rework dispatch, so a TryReplacePromptAsync
        // call from inside the auditor mimics the operator's mid-flight PUT).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Capture the store so we can mutate the prompt from inside an auditor
        // — TestSupport.BuildPipeline builds the store internally, but the
        // returned TestPipeline exposes the same instance.
        SqliteWorkItemStore? storeRef = null;
        var auditor = new PromptMutatingAuditor(() => storeRef!);

        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            auditors: [auditor], maxAuditIterations: 2);
        storeRef = tp.Store;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2\n"));

        var item = NewItem("feature/dispatch-row-rework");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Iteration 1 dispatched at revision 1 (PutPrompt happens INSIDE the
        // audit step between the work iteration and the rework dispatch).
        // Iteration 2 must dispatch at revision 2 — proving the re-read was
        // honoured. Without it, iteration 2 would record revision 1 because
        // item.PromptRevision in the orchestrator's local variable was 1
        // when the work phase started.
        var rows = await tp.Store.GetIterationsAsync(item.Id);
        var iter1 = Assert.Single(rows, r => r.Iteration == 1);
        var iter2 = Assert.Single(rows, r => r.Iteration == 2);
        Assert.Equal(1, iter1.PromptRevisionAtDispatch);
        Assert.Equal(2, iter2.PromptRevisionAtDispatch);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(2, final!.PromptRevision);
        Assert.Equal("edited mid-flight", final.Prompt);
    }

    // ── CODEYBOX_PROMPT_REVISION env-var injection ─────────────────────────

    [Fact]
    public async Task SandboxSpec_CarriesPromptRevisionEnvVar_ForWorkAndReworkPhases()
    {
        // PipelineRunner.cs:1404-1423 builds the agent sandbox spec with the
        // CODEYBOX_PROMPT_REVISION env var set to the iteration's dispatched
        // revision. This is the channel by which the agent can echo the value
        // back as a commit trailer — without it, the trailer auditor would
        // always fail. A regression that drops the dictionary, mis-keys it,
        // or writes to a credential-shadowed slot would not be caught by any
        // existing wire-shape or store-level test.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var sandboxes = new CapturingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        SqliteWorkItemStore? storeRef = null;
        var auditor = new PromptMutatingAuditor(() => storeRef!);

        using var tp = TestSupport.BuildPipeline(_workspace, seed,
            sandboxProvider: sandboxes,
            auditors: [auditor], maxAuditIterations: 2);
        storeRef = tp.Store;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "v2\n"));

        var item = NewItem("feature/env-var-injection");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var workSpec = Assert.Single(sandboxes.Specs, s => s.TimingPhase == "work");
        Assert.True(workSpec.Environment.ContainsKey(CodeyBoxTrailers.PromptRevisionEnvVar),
            $"work-phase sandbox spec missing {CodeyBoxTrailers.PromptRevisionEnvVar}");
        Assert.Equal("1", workSpec.Environment[CodeyBoxTrailers.PromptRevisionEnvVar]);
        Assert.Equal(item.Id.ToString(),
            workSpec.Environment[SandboxConventions.WorkItemIdEnvironmentVariable]);

        var reworkSpec = Assert.Single(sandboxes.Specs, s => s.TimingPhase == "rework");
        Assert.True(reworkSpec.Environment.ContainsKey(CodeyBoxTrailers.PromptRevisionEnvVar),
            $"rework-phase sandbox spec missing {CodeyBoxTrailers.PromptRevisionEnvVar}");
        // Rework must see the new revision — confirms the rework-phase env-var
        // is sourced from the freshly-read store row, not the orchestrator's
        // pickup-time snapshot.
        Assert.Equal("2", reworkSpec.Environment[CodeyBoxTrailers.PromptRevisionEnvVar]);
        Assert.Equal(item.Id.ToString(),
            reworkSpec.Environment[SandboxConventions.WorkItemIdEnvironmentVariable]);
    }

    // ── Commit-trailer wiring ──────────────────────────────────────────────

    [Fact]
    public async Task CommitTrailerBlock_IncludesPromptRevisionTrailer_OnAgentCommit()
    {
        // PipelineRunner.cs:1692-1693 forwards promptRevisionAtDispatch into
        // ComposeCommitTrailerBlockAsync at commit-creation time. The
        // CodeyBoxTrailers.Compose primitive is unit-tested in
        // PromptRevisionTrailerTests, but no test exercises the runner-level
        // wiring — a regression that forgets to pass the revision through (or
        // passes the work item's CURRENT revision instead of the DISPATCHED
        // one) would emit commits with the wrong trailer.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("trailer-probe.txt", "x\n"));

        var item = NewItem("feature/trailer-wired");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Read the most-recent agent commit on the work branch (the merge
        // commit added on main has its own message, so we look at the parent).
        var bareRepo = tp.GitHost.GetRepoPath(
            await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));
        var (code, log, _) = await TestSupport.RunGit(bareRepo,
            "log", "-1", $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            item.WorkBranch!);
        Assert.Equal(0, code);
        Assert.Equal("1", log.Trim());
    }

    [Fact]
    public async Task CommitTrailerBlock_UsesDispatchedRevision_NotCurrentRevision()
    {
        // The trailer must carry the value of CODEYBOX_PROMPT_REVISION that
        // was active when the iteration was DISPATCHED — not the work item's
        // current value. Mid-iteration edit: work-phase commit must still
        // carry revision 1 (the dispatch value), even though by the time the
        // commit lands the work item is at revision 2.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        // Inject the prompt edit during the work-phase agent invocation via
        // BeforeWorkAsync — a moment that runs after RecordIterationDispatch
        // has fired but before the orchestrator composes the commit trailer.
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("trailer-dispatched.txt", "x\n"));
        WorkItemId? editTarget = null;
        tp.Agent.BeforeWorkAsync = async (_, _, _) =>
        {
            if (editTarget is null) return;
            await tp.Store.TryReplacePromptAsync(editTarget.Value, "edited", DateTimeOffset.UtcNow);
        };

        var item = NewItem("feature/trailer-dispatched");
        editTarget = item.Id;
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(2, final!.PromptRevision); // bumped mid-iteration
        Assert.Equal("edited", final.Prompt);

        // Trailer must still read 1 — the dispatched revision, not the current.
        var bareRepo = tp.GitHost.GetRepoPath(
            await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));
        var (code, log, _) = await TestSupport.RunGit(bareRepo,
            "log", "-1", $"--pretty=format:%(trailers:key={CodeyBoxTrailers.PromptRevisionTrailerKey},valueonly=true,unfold=true)",
            item.WorkBranch!);
        Assert.Equal(0, code);
        Assert.Equal("1", log.Trim());
    }

    // ── BuildTerminalRevisionAsync ─────────────────────────────────────────

    [Fact]
    public async Task BuildTerminalRevisionAsync_NonTerminalState_ReturnsNull()
    {
        // Non-terminal transitions return null so the existing webhook payload
        // shape is unchanged for working/auditing/reworking/etc. Returning a
        // populated record here would surface revision-attribution fields on
        // every intermediate transition, polluting the public webhook contract.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/non-terminal-revision") with { State = WorkItemState.Working };
        await tp.Store.CreateAsync(item);

        var details = await tp.Pipeline.BuildTerminalRevisionAsync(item, CancellationToken.None);
        Assert.Null(details);
    }

    [Fact]
    public async Task BuildTerminalRevisionAsync_TerminalNoIterations_RevisionMatchesIsNull()
    {
        // RevisionMatches=null distinguishes "agent finished an older revision"
        // (false) from "no iteration was ever dispatched" (null). The Boolean
        // false reading would tell JobTrack to prompt a re-run for an item
        // that never actually ran — null surfaces the missing dispatch row
        // honestly.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/terminal-no-iter") with { State = WorkItemState.Failed };
        await tp.Store.CreateAsync(item);

        var details = await tp.Pipeline.BuildTerminalRevisionAsync(item, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Equal(1, details!.PromptRevision);
        Assert.Null(details.RevisionAtCompletion);
        Assert.Null(details.RevisionMatches);
    }

    [Fact]
    public async Task BuildTerminalRevisionAsync_TerminalWithIterations_RevisionAttributesLargestIteration()
    {
        // The lookup picks the row with the LARGEST iteration number — i.e.
        // the last iteration that actually ran. Using .Max(i => i.PromptRevisionAtDispatch)
        // only agrees when iterations are monotonic; this test seeds an
        // out-of-order revision pattern so a regression that switched to .Max
        // would diverge from "the revision attributed to the LAST iteration."
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/largest-iter") with
        {
            State = WorkItemState.Done,
            PromptRevision = 5,
        };
        await tp.Store.CreateAsync(item);
        // Iteration 1 = revision 7 (highest revision), iteration 2 = revision 5,
        // iteration 3 = revision 3 (lowest revision but largest iteration). A
        // .Max-based regression would report 7; the correct LAST-iteration
        // lookup returns 3.
        await tp.Store.RecordIterationDispatchAsync(item.Id, iteration: 1, promptRevisionAtDispatch: 7, DateTimeOffset.UtcNow);
        await tp.Store.RecordIterationDispatchAsync(item.Id, iteration: 2, promptRevisionAtDispatch: 5, DateTimeOffset.UtcNow);
        await tp.Store.RecordIterationDispatchAsync(item.Id, iteration: 3, promptRevisionAtDispatch: 3, DateTimeOffset.UtcNow);

        var details = await tp.Pipeline.BuildTerminalRevisionAsync(item, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Equal(5, details!.PromptRevision);
        Assert.Equal(3, details.RevisionAtCompletion);
        // Item revision (5) != last dispatched (3) → RevisionMatches is false.
        Assert.Equal(false, details.RevisionMatches);
    }

    [Fact]
    public async Task BuildTerminalRevisionAsync_TerminalMatchingRevision_RevisionMatchesIsTrue()
    {
        // Happy-path attribution: agent finished against the current prompt.
        // The webhook payload's revisionMatches=true tells JobTrack no re-run
        // is needed.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        var item = NewItem("feature/matching-rev") with
        {
            State = WorkItemState.Done,
            PromptRevision = 4,
        };
        await tp.Store.CreateAsync(item);
        await tp.Store.RecordIterationDispatchAsync(item.Id, iteration: 1, promptRevisionAtDispatch: 4, DateTimeOffset.UtcNow);

        var details = await tp.Pipeline.BuildTerminalRevisionAsync(item, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Equal(4, details!.PromptRevision);
        Assert.Equal(4, details.RevisionAtCompletion);
        Assert.Equal(true, details.RevisionMatches);
    }

    // ── Webhook payload integration ────────────────────────────────────────

    [Fact]
    public async Task WebhookEvent_DonePayload_CarriesPopulatedRevisionFields()
    {
        // End-to-end: a successful happy-path pipeline emits work_item.done
        // with promptRevision/revisionAtCompletion/revisionMatches populated
        // from BuildTerminalRevisionAsync. This guards the path from the
        // helper through Transition() to the published WebhookEvent.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipelineWithDispatcher(seed, webhooks);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("done-rev.txt", "x\n"));

        var item = NewItem("feature/done-rev");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var done = webhooks.Events.First(e => e.Event == "work_item.done");
        Assert.Equal(1, done.PromptRevision);
        Assert.Equal(1, done.RevisionAtCompletion);
        Assert.Equal(true, done.RevisionMatches);

        // A non-terminal intermediate event (work_item.working) must NOT
        // carry the fields — the helper short-circuits on non-terminal
        // states and Transition() passes null through.
        var working = webhooks.Events.First(e => e.Event == "work_item.working");
        Assert.Null(working.PromptRevision);
        Assert.Null(working.RevisionAtCompletion);
        Assert.Null(working.RevisionMatches);
    }

    // ── Test helpers ───────────────────────────────────────────────────────

    private TestPipeline BuildPipelineWithDispatcher(
        string seed, IWebhookDispatcher webhooks)
    {
        // TestSupport.BuildPipeline always wires NullWebhookDispatcher; this
        // helper builds a parallel pipeline using a custom dispatcher so we
        // can inspect the published event payloads.
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new CodeyBox.Git.LocalGitHost(
            new CodeyBox.Git.LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<CodeyBox.Git.LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new CodeyBox.Git.InMemoryPullRequestService();
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var registry = new CodeyBox.Agents.AgentRegistry([agent]);

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = Array.Empty<string>() },
        });

        var composer = new CodeyBox.Projects.ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
    }

    /// <summary>
    /// Auditor that fails the first audit iteration (forcing rework) and
    /// invokes <see cref="IWorkItemStore.TryReplacePromptAsync"/> on its way
    /// out, simulating a PUT /workitems/{id}/prompt landing while the audit
    /// is running — i.e. between the work-phase commit and the rework
    /// dispatch. The second iteration passes.
    /// </summary>
    private sealed class PromptMutatingAuditor : IAuditor
    {
        private readonly Func<IWorkItemStore> _getStore;
        private int _calls;

        public PromptMutatingAuditor(Func<IWorkItemStore> getStore) => _getStore = getStore;

        public string Name => "test:prompt-mutating-auditor";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
        {
            _calls++;
            if (_calls == 1)
            {
                // Edit the prompt before reporting failure so the rework
                // re-read picks it up. This mimics the operator window the
                // task spec calls out: "Update a prompt via PUT
                // /workitems/{id}/prompt while the item is Working."
                await _getStore().TryReplacePromptAsync(
                    context.WorkItemId, "edited mid-flight", DateTimeOffset.UtcNow, ct);
                return new AuditResult(false,
                [
                    new AuditFinding(Name, AuditSeverity.Error, "force rework", "first iter always fails"),
                ]);
            }
            return new AuditResult(true, []);
        }
    }
}
