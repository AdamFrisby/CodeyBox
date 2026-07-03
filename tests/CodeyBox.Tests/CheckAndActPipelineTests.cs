using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Orchestrator.Knobs;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end tests for the check-and-act work-item type. Exercises:
///   - "yes" verdict → follow-up Normal item is created + queued, parented to the check
///   - "no" verdict → no follow-up enqueue, verdict persisted, check finishes Done
///   - malformed verdict → check transitions to Failed (failureKind="other")
///
/// Uses the Process sandbox + a scripted agent that emits a configured
/// verdict-block on stdout when the check prompt arrives.
/// </summary>
[Collection("Pipeline integration")]
public sealed class CheckAndActPipelineTests : IDisposable
{
    private readonly string _workspace;
    public CheckAndActPipelineTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-checkact-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task YesVerdict_EnqueuesParentedFollowupAgainstSameProject()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "src/Foo.cs L42 builds SQL via interpolation", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate the repo",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact1",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation / interpolation (SQL-injection risk)?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix all SQL injection vulnerabilities and verify none remain",
                    Prompt = "Remediate all SQL string interpolation. Replace with parameterised queries.",
                    Priority = 200,
                    MinModelScore = 50,
                },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        // Check item completes Done with verdict + evidence recorded.
        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.Verdict);
        Assert.True(final.Verdict!.Answer);
        Assert.Contains("Foo.cs", final.Verdict.Evidence);
        Assert.Equal("high", final.Verdict.Confidence);

        // Exactly one follow-up Normal item was created, parented to the check.
        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followups = allItems.Where(i => i.OriginCheckWorkItemId == check.Id).ToList();
        Assert.Single(followups, i => i.OriginCheckWorkItemId == check.Id);
        var followup = followups[0];
        Assert.Equal(JobType.Normal, followup.JobType);
        Assert.Equal(check.ProjectId, followup.ProjectId);
        Assert.Equal("Fix all SQL injection vulnerabilities and verify none remain", followup.Title);
        Assert.Equal("Remediate all SQL string interpolation. Replace with parameterised queries.", followup.Prompt);
        Assert.Equal(200, followup.Priority);
        Assert.Equal(50, followup.MinModelScore);
        Assert.Equal(WorkItemState.Queued, followup.State);

        // Follow-up is also kicked on the dispatch queue.
        Assert.True(tp.Queue.Count >= 1, "follow-up should have been kicked on the task queue");
    }

    [Fact]
    public async Task YesVerdict_ExistingOriginFollowup_IsReusedInsteadOfDuplicated()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "src/Foo.cs still needs remediation", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate the repo",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-idempotent",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "remediate",
                },
            },
        };
        var existingFollowup = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = check.ProjectId,
            Title = "Fix it",
            Prompt = "remediate",
            BaseBranch = check.BaseBranch,
            PushUpstream = check.PushUpstream,
            OriginCheckWorkItemId = check.Id,
            JobType = JobType.Normal,
        };
        await tp.Store.CreateAsync(check);
        await tp.Store.CreateAsync(existingFollowup);

        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followups = allItems.Where(i => i.OriginCheckWorkItemId == check.Id).ToList();
        var followup = Assert.Single(followups);
        Assert.Equal(existingFollowup.Id, followup.Id);
    }

    [Fact]
    public async Task YesVerdict_RacingDuplicateOriginCreate_ReusesExistingFollowup()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        RacingFollowupCreateStore? racingStore = null;
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            workItemStoreDecorator: store => racingStore = new RacingFollowupCreateStore(store));

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "src/Foo.cs still needs remediation", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate the repo",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-race",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "remediate",
                },
            },
        };
        await tp.Store.CreateAsync(check);

        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(racingStore?.RacedFollowupId);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
        Assert.Equal(racingStore!.RacedFollowupId, followup.Id);
        Assert.Equal(1, tp.Queue.Count);
    }

    [Fact]
    public async Task NoVerdict_NoFollowupEnqueued_VerdictRecorded_CheckDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "no string interpolation found in src/**/*.cs", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate the repo",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-no",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation / interpolation?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "do remediation",
                },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.Verdict);
        Assert.False(final.Verdict!.Answer);
        Assert.Contains("no string interpolation", final.Verdict.Evidence);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        Assert.DoesNotContain(allItems, i => i.OriginCheckWorkItemId == check.Id);
    }

    [Fact]
    public async Task ActionableAnswerFalse_EnqueuesFollowupWhenAgentReturnsFalse()
    {
        // Inverse-shape check: ActionableAnswer=false means "act when the
        // agent answers no" — e.g. "are there integration tests covering X?"
        // → if no, enqueue a write-tests follow-up.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "no integration tests in tests/**", null));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for integration tests",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-inv",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Are there integration tests covering the auth flow?",
                ActionableAnswer = false,
                OnYes = new OnYesActionSpec { Title = "Write tests", Prompt = "add coverage" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
    }

    [Fact]
    public async Task MalformedVerdict_TransitionsCheckToFailed_NoFollowupEnqueued()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // Agent emits text WITHOUT the sentinels. The parser must refuse to
        // guess a yes/no out of free text.
        tp.Agent.CheckPlan.Enqueue("I think the answer is probably yes, but no JSON envelope here.");

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Bad-agent check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-bad",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is the code vulnerable?",
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("other", final.FailureKind);
        Assert.Contains("verdict", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(final.Verdict);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        Assert.DoesNotContain(allItems, i => i.OriginCheckWorkItemId == check.Id);
    }

    [Fact]
    public async Task MissingCheckSpec_OnCheckAndActItem_TransitionsFailed()
    {
        // Defensive: an item with JobType=CheckAndAct but no Check spec is a
        // configuration bug. The pipeline must fail fast and not crash.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Broken check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-empty",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = null,
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("other", final.FailureKind);
        Assert.Contains("check spec", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentFailureDuringCheck_TransitionsCheckToFailed_FailureKindOther_NoFollowupEnqueued()
    {
        // RunCheckAndActAgentAsync throws InvalidOperationException when the
        // scripted agent returns Success=false; the outer catch in
        // RunCheckAndActAsync must convert that into TransitionFailed with
        // failureKind="other" and the agent stderr surfaced in LastError —
        // without persisting a verdict and without enqueuing the on-yes
        // follow-up. The scripted agent has CheckPlan empty here so its
        // HandleCheckAsync returns AgentResult(false, ...).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // CheckPlan intentionally empty — scripted agent returns Success=false.
        Assert.Empty(tp.Agent.CheckPlan);

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Failing-agent check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-agentfail",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is the code vulnerable?",
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("other", final.FailureKind);
        Assert.False(string.IsNullOrEmpty(final.LastError));
        // The wrapper exception in RunCheckAndActAgentAsync begins with
        // "check-and-act agent failed" — pin that so a regression that
        // swallows the agent's failure summary or stderr is caught.
        Assert.Contains("check-and-act agent failed", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(final.Verdict);

        // No follow-up was enqueued.
        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        Assert.DoesNotContain(allItems, i => i.OriginCheckWorkItemId == check.Id);
    }

    [Fact]
    public async Task TransientAgentFailureDuringCheck_ParksWaitingForTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);
        tp.Agent.CheckResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "check transport failed",
            Stdout: null,
            Stderr: "request timed out while reading check stream"));

        var check = NewCheckItem("codeybox/checkact-transient-check");
        await tp.Store.CreateAsync(check);

        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("transient", final.FailureKind);
        Assert.Null(final.TransientRetryFrom);
        Assert.Equal(time.GetUtcNow(), final.TransientRetryFirstFailedAt);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), final.NextTransientRetryAt);
        Assert.Equal(0, final.TransientRetryAttempts);
    }

    [Fact]
    public async Task AuthPromptDuringCheck_FailsItemWithoutFleetBench()
    {
        // A stdout-only auth prompt during the check phase, with in-VM smoke
        // unavailable (no gate wired), fails the item (AuthRequired) but must
        // NOT globally bench the agent: the irreversible fleet-wide bench fails
        // CLOSED on uncorroborated, model-controllable stdout evidence.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var registry = NewAvailabilityRegistry();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            availabilityRegistry: registry);

        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        tp.Agent.CheckPlan.Enqueue(transcript);

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Auth check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-auth",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is the code vulnerable?",
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(WorkItemFailureKinds.AuthRequired, final.FailureKind);
        Assert.Contains("auth required from agent output", final.LastError);
        Assert.Contains("check", final.LastError);
        Assert.Contains("item-level failure only, no fleet-wide bench", final.LastError);
        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task YesVerdict_PublishesCheckFollowupEnqueuedWebhook()
    {
        // The orchestrator publishes work_item.check_followup_enqueued whenever
        // an on-yes follow-up is created. Pin: name, target work item, and the
        // origin/follow-up linkage carried in Details.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, webhookDispatcher: webhooks);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "src/Foo.cs uses interpolation", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate the repo",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-webhook",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix it", Prompt = "remediate" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var enqueueEvt = webhooks.Events
            .FirstOrDefault(e => e.Event == "work_item.check_followup_enqueued");
        Assert.NotNull(enqueueEvt);
        Assert.NotNull(enqueueEvt!.WorkItem);

        // The event's WorkItem points at the follow-up (the new normal item),
        // not the check item, and the follow-up's OriginCheckWorkItemId
        // back-links to the check.
        Assert.Equal(check.Id, enqueueEvt.WorkItem!.OriginCheckWorkItemId);
        Assert.NotEqual(check.Id, enqueueEvt.WorkItem.Id);

        // Details payload contains both ids as strings so downstream consumers
        // can correlate the verdict to its remediation item without re-reading.
        Assert.NotNull(enqueueEvt.Details);
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(enqueueEvt.Details);
        var doc = System.Text.Json.JsonDocument.Parse(detailsJson).RootElement;
        Assert.Equal(check.Id.ToString(), doc.GetProperty("originCheckWorkItemId").GetString());
        Assert.Equal(enqueueEvt.WorkItem.Id.ToString(), doc.GetProperty("followupWorkItemId").GetString());
    }

    [Fact]
    public async Task NoVerdict_DoesNotPublishCheckFollowupEnqueuedWebhook()
    {
        // Sanity-pin: the event must NOT fire when the verdict does not match
        // the actionable condition. A regression that hoisted the publish
        // outside the conditional would create a misleading observable signal
        // for "no" verdicts.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, webhookDispatcher: webhooks);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "no interpolation found", null));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate the repo",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-no-webhook",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix it", Prompt = "remediate" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.check_followup_enqueued");
    }

    [Fact]
    public async Task CheckPrompt_BuiltByCheckAndActPipeline_NotTheWorkPhasePrompt()
    {
        // Bridge the unit test (BuildPrompt) and the pipeline test (verdict
        // round-trip): the orchestrator MUST use CheckAndActPipeline.BuildPrompt
        // for the check phase. A regression that sent the work-phase prompt
        // instead would still let the scripted agent emit a verdict and pass
        // the other tests — so assert directly on what the agent received.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "evidence", "high"));

        var spec = new CheckAndActSpec
        {
            Question = "Does the repo contain a unique-string-Q9X2K7?",
            ActionableAnswer = true,
            OnYes = new OnYesActionSpec { Title = "Fix it", Prompt = "remediate" },
        };
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Prompt-shape check",
            Prompt = "ignored for check-and-act",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-prompt",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = spec,
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        Assert.Single(tp.Agent.CheckInvocations);
        var sentPrompt = tp.Agent.CheckInvocations[0];
        Assert.Contains(spec.Question, sentPrompt);
        Assert.Contains(CheckAndActPipeline.StartSentinel, sentPrompt);
        Assert.Contains(CheckAndActPipeline.EndSentinel, sentPrompt);
        // The check-and-act prompt begins with the Check-and-Act task header;
        // the work-phase prompt does not.
        Assert.StartsWith("# Check-and-Act task", sentPrompt);
    }

    [Fact]
    public async Task CheckAndAct_GraphicalProject_SmokeGateUsesHeadlessWorkProfile()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new RecordingInVmSmokeGate();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            graphicalSandbox: true,
            networkProfiles: new ProjectNetworkProfiles { Work = "headless-work" },
            inVmSmokeGate: gate);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "clean", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check graphical target",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-smoke-target",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            BaselineImageRef = "headless-work-baseline",
            Check = new CheckAndActSpec
            {
                Question = "Is remediation required?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var target = Assert.Single(gate.Targets);
        Assert.Equal("headless-work", target.NetworkProfile);
        Assert.Equal(SandboxProfileFlavor.Headless, target.Flavor);
        Assert.Equal("headless-work-baseline", target.BaselineRef);
    }

    [Fact]
    public async Task CompletionMode_UsesCompletionRunner_NoSandboxAgentCheckInvocation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new RecordingInVmSmokeGate();
        var completion = new ScriptedCompletionRunner();
        completion.Results.Enqueue(BuildCompletionResult(true, "README.md shows seed content", cacheHit: false));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            inVmSmokeGate: gate,
            checkCompletionRunner: completion);

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Completion check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-completion",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Mode = CheckAndActModes.Completion,
                Question = "Does README mention seed content?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix README", Prompt = "update docs" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(final.Verdict!.Answer);
        Assert.Empty(tp.Agent.CheckInvocations);
        Assert.Empty(gate.Targets);

        var request = Assert.Single(completion.Requests);
        Assert.Equal("check", request.Phase);
        Assert.Null(request.Iteration);
        Assert.Contains("[1: fixed generic system prompt]", request.Blocks.Render());
        Assert.Contains("[2: the code/diff under review]", request.Blocks.Render());
        Assert.Contains("[3: the specific check question]", request.Blocks.Render());
        Assert.Contains("README.md", request.Blocks.ReviewBlock);
        Assert.Contains(check.Check!.Question, request.Blocks.QuestionBlock);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
    }

    [Fact]
    public async Task CompletionMode_NoSafeProviderFallsBackToAgenticCheckPath()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new RecordingInVmSmokeGate();
        var completion = new ScriptedCompletionRunner();
        completion.Results.Enqueue(null);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            inVmSmokeGate: gate,
            checkCompletionRunner: completion);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "agentic fallback inspected the repo", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Completion fallback check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-completion-fallback",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Mode = CheckAndActModes.Completion,
                Question = "Is remediation required?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.False(final.Verdict!.Answer);
        Assert.Single(completion.Requests);
        Assert.Single(tp.Agent.CheckInvocations);
        Assert.Single(gate.Targets);
    }

    [Fact]
    public async Task YesVerdict_FollowupInheritsBoundaryPriorityAndMinModelScore()
    {
        // EnqueueOnYesFollowupAsync clamps priority to [-1000, 1000] and
        // minModelScore to [0, 200]. Use boundary values so any off-by-one
        // in the clamp would surface (e.g. Math.Clamp(p, -999, 1000) would
        // turn -1000 into -999).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "evidence", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Clamp boundaries",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-clamp",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "remediate",
                    Priority = 1000,
                    MinModelScore = 200,
                    Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ChangeScopeKnob.KeyName] = ChangeScopeKnob.ValueRefactor,
                    },
                },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
        Assert.Equal(1000, followup.Priority);
        Assert.Equal(200, followup.MinModelScore);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, followup.Knobs[ChangeScopeKnob.KeyName]);
    }

    [Fact]
    public async Task OnYesDependsOn_GuidIsPreserved_BareExternalIdIsResolved_UnknownIsDropped()
    {
        // Exercise the orchestrator-side resolver via the public side-effect:
        // the persisted follow-up's DependsOn list. Coverage:
        //   - a real GUID → kept verbatim
        //   - a bare externalId that uniquely matches one item → resolved to its id
        //   - an unknown bare externalId → silently dropped
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // Pre-create a dep with a known bare externalId in the same project.
        var depByExternalId = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "dep with external id",
            Prompt = "x",
            ExternalIds = new Dictionary<string, string> { ["ticket"] = "JIRA-42" },
        };
        await tp.Store.CreateAsync(depByExternalId);

        // A second dep referenced by GUID directly.
        var depByGuid = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "dep referenced by guid",
            Prompt = "x",
        };
        await tp.Store.CreateAsync(depByGuid);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "evidence", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check with dependsOn follow-up",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-depsresolve",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "remediate",
                    DependsOn =
                    [
                        depByGuid.Id.ToString(),
                        "JIRA-42",
                        "DOES-NOT-EXIST",
                    ],
                },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        Assert.Equal(2, followup.DependsOn.Count);
        Assert.Contains(depByGuid.Id, followup.DependsOn);
        Assert.Contains(depByExternalId.Id, followup.DependsOn);
        Assert.DoesNotContain(followup.DependsOn,
            id => id != depByGuid.Id && id != depByExternalId.Id);
        // Both resolved dependencies are still Queued, so the follow-up must
        // be persisted without being kicked onto the dispatch queue.
        Assert.Equal(0, tp.Queue.Count);
    }

    [Fact]
    public async Task OnYesDependsOn_NamespacedExternalIdIsResolved()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var dep = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "ns dep",
            Prompt = "x",
            ExternalIds = new Dictionary<string, string> { ["github"] = "PR-7" },
        };
        await tp.Store.CreateAsync(dep);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "evidence", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check with namespaced dep",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-nsdep",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "remediate",
                    DependsOn = ["github:PR-7"],
                },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
        Assert.Single(followup.DependsOn);
        Assert.Equal(dep.Id, followup.DependsOn[0]);
    }

    [Fact]
    public async Task OnYesDependsOn_AmbiguousBareExternalId_SilentlyDropped()
    {
        // Two items in the same project carry the same bare externalId value
        // under different namespaces. ResolveOnYesDependsOnAsync must NOT
        // pick one arbitrarily — the bare-id branch silently drops on >1
        // match, treating the follow-up as having no dependency for that
        // entry. See PipelineRunner.cs:2440 for the documented rationale.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var dep1 = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "dep one",
            Prompt = "x",
            ExternalIds = new Dictionary<string, string> { ["jira"] = "DUP-1" },
        };
        var dep2 = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "dep two",
            Prompt = "x",
            ExternalIds = new Dictionary<string, string> { ["github"] = "DUP-1" },
        };
        await tp.Store.CreateAsync(dep1);
        await tp.Store.CreateAsync(dep2);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "evidence", "high"));

        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "ambiguous dep",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-ambig",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "Fix it",
                    Prompt = "remediate",
                    DependsOn = ["DUP-1"],
                },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
        Assert.Empty(followup.DependsOn);
    }

    [Fact]
    public async Task PostActReValidation_NonActionableReCheck_AcceptsFollowupAsDone_WithBothVerdictsRecorded()
    {
        // Acceptance path (1): check==yes → act applies the fix → post-act
        // re-check==no → the follow-up reaches Done. Pins:
        //   - the follow-up's ReCheckVerdicts records the single
        //     non-actionable verdict (this is the post-act re-validation
        //     trace),
        //   - the originating check's initial verdict remains on the check
        //     item (both verdicts on the timeline),
        //   - the post-act re-check used the same in-VM execution path as
        //     the initial check (BuildPrompt scaffold) — assert on the
        //     prompt the agent received.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        // 1) The originating check returns "yes" (actionable) → enqueues
        // the on-yes follow-up.
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "src/Foo.cs L42 uses interpolation", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for SQL injection",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-acc1",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation (SQLi risk)?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix SQL injection", Prompt = "remediate" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        // The check enqueued exactly one follow-up parented to itself.
        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);
        Assert.Empty(followup.ReCheckVerdicts); // none recorded yet — the act hasn't run

        // 2) Run the follow-up's pipeline. The agent applies a remediation
        // in the work phase, then the post-act re-check returns "no" —
        // the remediation closed the gap.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fix.cs", "parameterised"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "no interpolation remains in src/**", "high"));

        var checkInvocationsBefore = tp.Agent.CheckInvocations.Count;
        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.Done, finalFollowup!.State);

        // The post-act re-check fired exactly once and used the SAME prompt
        // shape as the original check (same sentinels, same question).
        Assert.Equal(checkInvocationsBefore + 1, tp.Agent.CheckInvocations.Count);
        var reCheckPrompt = tp.Agent.CheckInvocations[^1];
        Assert.StartsWith("# Check-and-Act task", reCheckPrompt);
        Assert.Contains(check.Check!.Question, reCheckPrompt);
        Assert.Contains(CheckAndActPipeline.StartSentinel, reCheckPrompt);

        // Re-check verdict recorded on the follow-up's history (the
        // post-act trace). One non-actionable entry.
        var verdict = Assert.Single(finalFollowup.ReCheckVerdicts);
        Assert.False(verdict.Answer);
        Assert.Contains("no interpolation", verdict.Evidence);

        // The originating check still carries its initial verdict —
        // both verdicts on the item-chain timeline.
        var finalCheck = await tp.Store.GetAsync(check.Id);
        Assert.NotNull(finalCheck!.Verdict);
        Assert.True(finalCheck.Verdict!.Answer);
        Assert.Equal(check.Id, finalFollowup.OriginCheckWorkItemId);
    }

    [Fact]
    public async Task PostActReValidation_TransientReCheckFailure_ParksWaitingForTransientRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var time = new ManualTimeProvider();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            transientRetryOptions: TransientRetryOptions(),
            retryTimeProvider: time);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "initial issue present", "high"));
        var check = NewCheckItem("codeybox/checkact-transient-post");
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("fix.cs", "parameterised"));
        tp.Agent.CheckResults.Enqueue(new AgentResult(
            Success: false,
            Summary: "post-act check transport failed",
            Stdout: null,
            Stderr: "Transport channel closed"));

        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var final = await tp.Store.GetAsync(followup.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("transient", final.FailureKind);
        Assert.Equal("merge", final.TransientRetryFrom);
        Assert.Equal(time.GetUtcNow(), final.TransientRetryFirstFailedAt);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), final.NextTransientRetryAt);
        Assert.Equal(0, final.TransientRetryAttempts);
    }

    [Fact]
    public async Task AuthPromptDuringPostActRecheck_FailsItemWithoutFleetBench()
    {
        // Stdout-only auth during the post-act recheck with in-VM smoke
        // unavailable: item fails (AuthRequired) but the agent is NOT globally
        // benched — the fleet-wide bench fails CLOSED without corroboration.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var registry = NewAvailabilityRegistry();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            availabilityRegistry: registry);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "issue remains", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for issue",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-post-auth",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is remediation required?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        var transcript = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Auth", "agy-login-prompt.redacted.txt"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("fix.cs", "attempted"));
        tp.Agent.CheckPlan.Enqueue(transcript);

        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.Failed, finalFollowup!.State);
        Assert.Equal(WorkItemFailureKinds.AuthRequired, finalFollowup.FailureKind);
        Assert.Contains("auth required from agent output", finalFollowup.LastError);
        Assert.Contains("post-act-recheck", finalFollowup.LastError);
        Assert.Contains("item-level failure only, no fleet-wide bench", finalFollowup.LastError);
        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task PostActReValidation_AgentPausedAfterWork_ParksWithoutDispatchingRecheck()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gate = new TogglePauseInVmSmokeGate();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, inVmSmokeGate: gate);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "issue remains", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check for issue",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-pause-recheck",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is remediation required?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("fix.cs", "parameterised"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "clean", "high"));
        tp.Agent.BeforeWorkAsync = (_, _, _) =>
        {
            gate.Paused = true;
            return Task.CompletedTask;
        };
        var checkInvocationsBefore = tp.Agent.CheckInvocations.Count;

        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var parked = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, parked!.State);
        Assert.Equal("audit", parked.AgentPauseRetryFrom);
        Assert.Null(parked.QuotaRetryFrom);
        Assert.Contains("paused by operator", parked.LastError);
        Assert.Equal(checkInvocationsBefore, tp.Agent.CheckInvocations.Count);
        Assert.Single(tp.Agent.CheckPlan);
    }

    [Fact]
    public async Task PostActReValidation_StillActionableAfterCap_FailsWithRemediationDidNotSatisfy()
    {
        // Acceptance path (2): check==yes → act does NOT close the gap →
        // post-act re-check==yes → rework → re-check==yes → cap exhausted
        // → Failed with a clear "remediation did not satisfy the check
        // after N attempts" reason; every re-check verdict is recorded on
        // the follow-up's history so the operator can see the failure
        // trace.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, maxAuditIterations: 2);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "SQLi present", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-acc2",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is any user-facing SQL built via string concatenation?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        // Cap = 2. Sequence is: work writes v1 → re-check #1=yes → rework
        // writes v2 → re-check #2=yes → cap exhausted → Failed.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.cs", "v1"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "iter1 still present", "high"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.cs", "v2"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "iter2 still present", "high"));

        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.Failed, finalFollowup!.State);
        Assert.Equal("other", finalFollowup.FailureKind);
        Assert.Contains("remediation did not satisfy", finalFollowup.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", finalFollowup.LastError!); // surfaces the attempt count

        // Both re-check verdicts recorded on the follow-up's history.
        Assert.Equal(2, finalFollowup.ReCheckVerdicts.Count);
        Assert.All(finalFollowup.ReCheckVerdicts, v => Assert.True(v.Answer));
        Assert.Contains("iter1", finalFollowup.ReCheckVerdicts[0].Evidence);
        Assert.Contains("iter2", finalFollowup.ReCheckVerdicts[1].Evidence);

        // The originating check's initial verdict is preserved on the
        // check item — both verdicts traceable end-to-end.
        var finalCheck = await tp.Store.GetAsync(check.Id);
        Assert.NotNull(finalCheck!.Verdict);
        Assert.True(finalCheck.Verdict!.Answer);
    }

    [Fact]
    public async Task PostActReValidation_StillActionableThenFlipsAfterRework_FollowupReachesDone()
    {
        // Mid-cap convergence: re-check #1 fails (actionable) → rework →
        // re-check #2 passes (non-actionable). The follow-up reaches Done
        // with two recorded verdicts (yes, then no) and the cap is not
        // exhausted. Pins the iterative rework-then-revalidate loop.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, maxAuditIterations: 3);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "initial yes", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-acc3",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Vulnerable?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        // Sequence: work writes v1 → re-check #1 = yes → rework writes v2
        // → re-check #2 = no → Done.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.cs", "v1"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "still vulnerable", "high"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.cs", "v2"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "now clean", "high"));

        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.Done, finalFollowup!.State);

        // Both verdicts recorded in order: actionable, then non-actionable.
        Assert.Equal(2, finalFollowup.ReCheckVerdicts.Count);
        Assert.True(finalFollowup.ReCheckVerdicts[0].Answer);
        Assert.False(finalFollowup.ReCheckVerdicts[1].Answer);
        Assert.Contains("still vulnerable", finalFollowup.ReCheckVerdicts[0].Evidence);
        Assert.Contains("now clean", finalFollowup.ReCheckVerdicts[1].Evidence);
    }

    [Fact]
    public async Task PostActReValidation_PostActReworkCompleted_ResetsRecoveryAttemptsBeforeNextRecheckFailure()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, maxAuditIterations: 3);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "initial yes", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-recovery-reset",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Vulnerable?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        var seededRecoveryAttempts = false;
        tp.Agent.BeforeWorkAsync = async (_, _, ct) =>
        {
            var current = await tp.Store.GetAsync(followup.Id, ct);
            if (!seededRecoveryAttempts && current?.State == WorkItemState.Reworking)
            {
                await tp.Store.UpdateAsync(current with { RecoveryAttempts = 2 }, ct);
                seededRecoveryAttempts = true;
            }
        };

        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.cs", "v1"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "still vulnerable", "high"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.cs", "v2"));
        tp.Agent.CheckPlan.Enqueue("not a parseable check verdict");

        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.True(seededRecoveryAttempts);
        Assert.Equal(WorkItemState.Failed, finalFollowup!.State);
        Assert.Contains("post-act re-check verdict parse failure", finalFollowup.LastError);
        Assert.Equal(0, finalFollowup.RecoveryAttempts);
        Assert.Single(finalFollowup.ReCheckVerdicts);
    }

    [Fact]
    public async Task PostActReValidation_PostActReworkBreaksRequiredBuild_FailsBuildBeforeNextRecheck()
    {
        // Post-act rework is followed by another check verdict, not by the
        // audit loop. A required-build failure produced here must therefore
        // terminal-fail with failureKind=build at the actual pipeline call
        // site. If this path accidentally used DeferToAuditLoop, the next
        // check verdict below would be consumed and the broken branch could
        // continue toward merge.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var requiredBuild = new SequencedRequiredBuildVerifier(
            RequiredBuildVerificationResult.Passed(0, "work build ok"),
            RequiredBuildVerificationResult.Passed(0, "audit build ok"),
            RequiredBuildVerificationResult.Failed(
                1,
                "src/Broken.cs(1,1): error CS1061: 'Broken' does not contain a definition"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            maxAuditIterations: 3,
            requiredBuildVerifier: requiredBuild);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "initial yes", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-postact-build",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Vulnerable?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("fix.cs", "v1"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "still vulnerable", "high"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("build.fail", "broken\n"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "would pass if rechecked", "high"));

        var checkInvocationsBefore = tp.Agent.CheckInvocations.Count;
        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.Failed, finalFollowup!.State);
        Assert.Equal("build", finalFollowup.FailureKind);
        Assert.Contains("rework left the branch non-compiling", finalFollowup.LastError!);
        Assert.Contains("error CS1061", finalFollowup.LastError!);
        Assert.Equal(checkInvocationsBefore + 1, tp.Agent.CheckInvocations.Count);
        Assert.Single(finalFollowup.ReCheckVerdicts);
        Assert.Single(tp.Agent.CheckPlan);
        Assert.Equal(3, requiredBuild.VerifyCalls);
    }

    [Fact]
    public async Task PostActReValidation_RegularItemWithoutOrigin_GatesSkipped_NoCheckInvocation()
    {
        // Sanity-pin: items without OriginCheckWorkItemId never trigger
        // the re-validation gate. The pipeline must not invoke the check
        // agent on plain work items.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var regular = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "regular work item",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = "codeybox/regular-noorigin",
            PushUpstream = false,
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        await tp.Store.CreateAsync(regular);
        await tp.Pipeline.RunAsync(regular, CancellationToken.None);

        var finalReg = await tp.Store.GetAsync(regular.Id);
        Assert.Equal(WorkItemState.Done, finalReg!.State);
        Assert.Empty(finalReg.ReCheckVerdicts);
        Assert.Empty(tp.Agent.CheckInvocations);
    }

    [Fact]
    public async Task PostActReValidation_OrphanedFollowup_OriginCheckMissing_GateSkipped()
    {
        // Resilience: a follow-up whose originating check item no longer
        // exists (deleted) or has no Check spec must still complete the
        // normal pipeline. The re-validation gate logs a warning and
        // returns without raising — losing the check item shouldn't
        // strand the follow-up forever.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);

        var orphan = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "orphaned follow-up",
            Prompt = "fix the thing",
            BaseBranch = "main",
            WorkBranch = "codeybox/orphan-followup",
            PushUpstream = false,
            // OriginCheckWorkItemId points at an id that was never created in the store.
            OriginCheckWorkItemId = WorkItemId.New(),
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        await tp.Store.CreateAsync(orphan);
        await tp.Pipeline.RunAsync(orphan, CancellationToken.None);

        var final = await tp.Store.GetAsync(orphan.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Empty(final.ReCheckVerdicts);
        // The check agent was never invoked because the gate found no
        // originating check spec.
        Assert.Empty(tp.Agent.CheckInvocations);
    }

    [Fact]
    public async Task PostActReValidation_NonActionableReCheck_PublishesCompletionWebhook()
    {
        // The post-act re-validation gate emits one
        // work_item.post_act_recheck_completed webhook per iteration with
        // the verdict outcome and the originating check id, so operators
        // can build a timeline of "what the re-check decided" without
        // re-reading the verdict history.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(_workspace, seed, webhookDispatcher: webhooks);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "initial yes", "high"));
        var check = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Check",
            Prompt = "evaluate",
            BaseBranch = "main",
            WorkBranch = "codeybox/checkact-webhook-recheck",
            PushUpstream = false,
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Vulnerable?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
            },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("fix.cs", "parameterised"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "clean", "high"));
        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var reCheckEvt = Assert.Single(webhooks.Events,
            e => e.Event == "work_item.post_act_recheck_completed");
        Assert.NotNull(reCheckEvt.WorkItem);
        Assert.Equal(followup.Id, reCheckEvt.WorkItem!.Id);
        Assert.NotNull(reCheckEvt.Details);
        var detailsJson = System.Text.Json.JsonSerializer.Serialize(reCheckEvt.Details);
        var doc = System.Text.Json.JsonDocument.Parse(detailsJson).RootElement;
        Assert.Equal(1, doc.GetProperty("iteration").GetInt32());
        Assert.False(doc.GetProperty("answer").GetBoolean());
        Assert.False(doc.GetProperty("actionable").GetBoolean());
        Assert.Equal(check.Id.ToString(), doc.GetProperty("originCheckWorkItemId").GetString());
    }

    private static string BuildVerdictStdout(bool answer, string evidence, string? confidence)
    {
        var ans = answer ? "true" : "false";
        var confSegment = confidence is null ? "" : $", \"confidence\": \"{confidence}\"";
        return $"some preamble\n{CheckAndActPipeline.StartSentinel}\n{{\"answer\": {ans}, \"evidence\": \"{evidence}\"{confSegment}}}\n{CheckAndActPipeline.EndSentinel}\n";
    }

    private static CheckAndActCompletionResult BuildCompletionResult(
        bool answer,
        string evidence,
        bool cacheHit)
    {
        var ans = answer ? "true" : "false";
        return new CheckAndActCompletionResult(
            CheckAndActCompletionProviders.GeminiOAuth,
            AgentKind.Gemini,
            "gemini-2.5-pro",
            $"completion output\n{CheckAndActPipeline.StartSentinel}\n{{\"answer\": {ans}, \"evidence\": \"{evidence}\", \"confidence\": \"high\"}}\n{CheckAndActPipeline.EndSentinel}\n",
            new CheckAndActCompletionUsage(20, cacheHit ? 100 : 0, 4, cacheHit));
    }

    private static AutoRetryOnTransientFailureOptions TransientRetryOptions() => new()
    {
        Enabled = true,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(15),
        Multiplier = 2,
        MaxAutoRetriesPerWorkItem = 5,
        MaxElapsedTime = TimeSpan.FromHours(1),
        JitterMode = TransientRetryJitterMode.None,
    };

    private static WorkItem NewCheckItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Check for SQL injection",
        Prompt = "evaluate the repo",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
        JobType = JobType.CheckAndAct,
        Check = new CheckAndActSpec
        {
            Question = "Is any user-facing SQL built via string concatenation?",
            ActionableAnswer = true,
            OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "go" },
        },
    };

    private sealed class ScriptedCompletionRunner : ICheckAndActCompletionRunner
    {
        public Queue<CheckAndActCompletionResult?> Results { get; } = new();
        public List<CheckAndActCompletionRequest> Requests { get; } = [];

        public Task<CheckAndActCompletionResult?> TryCompleteAsync(
            CheckAndActCompletionRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            if (Results.Count == 0)
                throw new InvalidOperationException("ScriptedCompletionRunner: ran out of completion results");
            return Task.FromResult(Results.Dequeue());
        }
    }

    private sealed class SequencedRequiredBuildVerifier : IRequiredBuildVerifier
    {
        private readonly Queue<RequiredBuildVerificationResult> _results;

        public SequencedRequiredBuildVerifier(params RequiredBuildVerificationResult[] results)
            => _results = new Queue<RequiredBuildVerificationResult>(results);

        public int VerifyCalls { get; private set; }

        public Task<RequiredBuildProbeResult> ProbeAsync(
            RequiredBuildProbeRequest request,
            CancellationToken ct)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(RequiredBuildProbeResult.Applies);
        }

        public Task<RequiredBuildVerificationResult> VerifyAsync(
            RequiredBuildVerificationRequest request,
            CancellationToken ct)
        {
            _ = request;
            _ = ct;
            VerifyCalls++;
            if (_results.Count == 0)
                return Task.FromResult(RequiredBuildVerificationResult.Passed(0, "ok"));
            return Task.FromResult(_results.Dequeue());
        }
    }

    private static AgentAvailabilityRegistry NewAvailabilityRegistry() =>
        new(new AvailabilityOptions(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentAvailabilityRegistry>.Instance);

    private sealed class RacingFollowupCreateStore : IWorkItemStore
    {
        private readonly SqliteWorkItemStore _inner;
        private int _hasRaced;

        public RacingFollowupCreateStore(SqliteWorkItemStore inner)
        {
            _inner = inner;
        }

        public WorkItemId? RacedFollowupId { get; private set; }

        public async Task CreateAsync(WorkItem item, CancellationToken ct = default)
        {
            if (item.OriginCheckWorkItemId is not null && Interlocked.Exchange(ref _hasRaced, 1) == 0)
            {
                var raced = item with
                {
                    Id = WorkItemId.New(),
                    Title = item.Title + " (race winner)",
                };
                RacedFollowupId = raced.Id;
                await _inner.CreateAsync(raced, ct);
            }

            await _inner.CreateAsync(item, ct);
        }

        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => _inner.UpdateAsync(item, ct);
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) => _inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => _inner.ListByStateAsync(state, ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => _inner.CountByStateAsync(state, ct);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => _inner.ReorderAsync(orderedIds, ct);
        public IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) => _inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) => _inner.CountStartedInWindowAsync(projectId, since, ct);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => _inner.CountInFlightAsync(projectId, ct);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) => _inner.GetByExternalIdAsync(projectId, externalId, ct);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) => _inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) => _inner.GetFleetStateCountsAsync(ct);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) => _inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) => _inner.GetFleetPauseStatesAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) => _inner.ListByReplaySourceAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => _inner.ListSuspendedAsync(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) => _inner.GetActiveBaselineImageRefsAsync(ct);
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) => _inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => _inner.OrphanReplaysAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => _inner.ListByReleaseAsync(releaseId, ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) => _inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) => _inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) => _inner.GetIterationsAsync(workItemId, ct);
    }

    private sealed class RecordingInVmSmokeGate : IInVmSmokeGate
    {
        public bool Enabled => true;
        public List<InVmSmokeSandboxTarget> Targets { get; } = [];

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            Targets.Add(target);
            return Task.FromResult(new AgentAvailability(true, null, null));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
    }

    private sealed class TogglePauseInVmSmokeGate : IInVmSmokeGate
    {
        public bool Enabled => true;
        public bool Paused { get; set; }

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct)
        {
            _ = kind;
            _ = target;
            _ = ct;
            return Task.FromResult(Paused
                ? new AgentAvailability(
                    false,
                    "paused by operator: maintenance",
                    null,
                    AgentAvailabilityCause.OperatorPaused)
                : new AgentAvailability(true, null, null));
        }

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct) =>
            Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));
    }
}
