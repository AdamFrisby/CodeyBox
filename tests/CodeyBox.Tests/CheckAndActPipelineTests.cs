using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
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

    private static string BuildVerdictStdout(bool answer, string evidence, string? confidence)
    {
        var ans = answer ? "true" : "false";
        var confSegment = confidence is null ? "" : $", \"confidence\": \"{confidence}\"";
        return $"some preamble\n{CheckAndActPipeline.StartSentinel}\n{{\"answer\": {ans}, \"evidence\": \"{evidence}\"{confSegment}}}\n{CheckAndActPipeline.EndSentinel}\n";
    }
}
