using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

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

    private static string BuildVerdictStdout(bool answer, string evidence, string? confidence)
    {
        var ans = answer ? "true" : "false";
        var confSegment = confidence is null ? "" : $", \"confidence\": \"{confidence}\"";
        return $"some preamble\n{CheckAndActPipeline.StartSentinel}\n{{\"answer\": {ans}, \"evidence\": \"{evidence}\"{confSegment}}}\n{CheckAndActPipeline.EndSentinel}\n";
    }
}
