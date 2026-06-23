using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Orchestrator.Knobs;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class PlanningPipelineTests : IDisposable
{
    private readonly string _workspace;

    public PlanningPipelineTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-planning-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PlanOn_RunsPlanningReviewThenImplementation_AndPersistsPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent { TryPushDuringPlanning = true };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plan-on") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("\"approach\"", final.PlanArtifact, StringComparison.Ordinal);
        Assert.NotNull(final.PlanGeneratedAt);
        Assert.NotNull(final.PlanReviewedAt);
        Assert.Equal("Placeholder plan review approved.", final.PlanReviewSummary);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.Contains("## Reviewed planning metadata", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("Plan-declared files/areas", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("output.txt", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("make the smallest output file change", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("pipeline integration verifies final branch", agent.LastWorkPrompt, StringComparison.Ordinal);

        var events = setup.Webhooks.Events.Select(e => e.Event).ToArray();
        Assert.Contains("work_item.planning", events);
        Assert.Contains("work_item.plan_review", events);
        Assert.Contains("work_item.plan_approved", events);

        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (_, treeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "feature/plan-on", "--name-only");
        Assert.Contains("output.txt", treeOutput);
        Assert.DoesNotContain("planning-scratch.txt", treeOutput);
        Assert.DoesNotContain("planning-pushed.txt", treeOutput);

        var (_, mainTreeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "main", "--name-only");
        Assert.DoesNotContain("planning-pushed.txt", mainTreeOutput);
    }

    [Fact]
    public async Task PlanOff_Default_DoesNotRunPlanningPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plan-off");

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PlanArtifact);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.DoesNotContain("## Reviewed planning metadata", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOff_WithStaleArtifact_DoesNotInjectPlanIntoImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plan-off-stale") with
        {
            PlanArtifact = "STALE PLAN: do the old task",
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            PlanReviewSummary = "old approval",
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.DoesNotContain("## Reviewed planning metadata", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE PLAN", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectDefaultPlanOn_RunsPlanningPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            });
        var item = NewItem("feature/project-default-plan");

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Contains("\"approach\"", final.PlanArtifact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_ClaudeSessionMode_RunsPlanningAsFirstWarmTurnThenImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var sessionRunner = new PlanningSessionRunner
        {
            DirtyPlanningSandbox = true,
            TryPushDuringPlanning = true,
        };
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            projectKnobs: null,
            sessionRunner,
            enableClaudeSession: true);
        var item = NewItem("feature/session-planning") with
        {
            ModelId = "claude-opus-4-7",
            ReasoningMode = "max",
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, sessionRunner.OpenCalls);
        Assert.Equal(2, sessionRunner.SendTurns);
        Assert.Equal(1, sessionRunner.CloseCalls);
        Assert.Equal("claude-opus-4-7", sessionRunner.OpenedModelId);
        Assert.Equal("max", sessionRunner.OpenedReasoningMode);
        Assert.Contains("planning-only phase", sessionRunner.PromptsSent[0], StringComparison.Ordinal);
        Assert.Contains("Reviewed planning metadata", sessionRunner.PromptsSent[1], StringComparison.Ordinal);
        Assert.Contains("output.txt", sessionRunner.PromptsSent[1], StringComparison.Ordinal);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);

        var barePath = Path.Combine(setup.GitRoot, item.Id + ".git");
        var (_, treeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "feature/session-planning", "--name-only");
        Assert.Contains("output.txt", treeOutput);
        Assert.DoesNotContain("planning-session-scratch.txt", treeOutput);
        Assert.DoesNotContain("planning-session-pushed.txt", treeOutput);

        var (_, mainTreeOutput, _) = await TestSupport.RunGit(
            barePath, "ls-tree", "-r", "main", "--name-only");
        Assert.DoesNotContain("planning-session-pushed.txt", mainTreeOutput);
    }

    [Fact]
    public async Task PlanOn_PlanningPromptContainsNoWriteAndRequiredSections()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plan-prompt") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains("do not write implementation", agent.LastPlanningPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"files\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"testStrategy\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"risks\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"satisfiesTask\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_PlanningPromptRunsPreprocessorsWithPlanningPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var preprocessor = new RecordingPlanningPreprocessor();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            promptPreprocessors: new AgentPromptPreprocessorChain([preprocessor]));
        var item = NewItem("feature/plan-preprocessor") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains(AgentPromptPhase.Planning, preprocessor.Phases);
        Assert.Contains("planning-preprocessor-marker", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("planning-preprocessor-marker", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_PlanningLoadsProjectRulesFromRealSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await File.WriteAllTextAsync(Path.Combine(seed, "AGENTS.md"), "planning rule marker\n");
        await TestSupport.RunGit(seed, "add", "AGENTS.md");
        await TestSupport.RunGit(seed, "commit", "-m", "add agents rules");
        var agent = new PlanningAwareAgent();
        var rules = new ProjectRulesPromptPreprocessor(
            new PlanningStaticOptionsMonitor<AgentPromptPreprocessingOptions>(
                new AgentPromptPreprocessingOptions { ProjectRulesPath = "AGENTS.md" }),
            NullLogger<ProjectRulesPromptPreprocessor>.Instance);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            promptPreprocessors: new AgentPromptPreprocessorChain([rules]));
        var item = NewItem("feature/plan-rules") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(agent.PlanningReceivedSandbox);
        Assert.Contains("planning rule marker", agent.LastPlanningPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_RunnerWithoutTextOnlyCapability_PlansInSandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new SandboxOnlyPlanningAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/no-text-only-plan") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.True(agent.PlanningReceivedSandbox);
        Assert.Contains("\"approach\"", final.PlanArtifact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_NonStreamingPlanningStdoutFallbackPersistsPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent { StreamPlanningOutput = false };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/non-streaming-plan") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("\"approach\"", final.PlanArtifact, StringComparison.Ordinal);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
    }

    [Fact]
    public async Task ResumeFromPlanReview_ApprovesPlanBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/resume-plan-review") with
        {
            State = WorkItemState.PlanReview,
            PlanArtifact = """
                {
                  "approach": "resume from review.",
                  "files": ["output.txt"],
                  "testStrategy": ["pipeline integration verifies final branch"],
                  "risks": ["none"],
                  "satisfiesTask": "creates output.txt"
                }
                """,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.NotNull(final.PlanReviewedAt);
        Assert.Equal("Placeholder plan review approved.", final.PlanReviewSummary);
        Assert.Contains("## Reviewed planning metadata", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("output.txt", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("resume from review", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeFromPlanApproved_DoesNotRerunPlanningAndInjectsApprovedPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/resume-plan-approved") with
        {
            State = WorkItemState.PlanApproved,
            PlanArtifact = """
                {
                  "approach": "already approved.",
                  "files": ["output.txt"],
                  "testStrategy": ["pipeline integration verifies final branch"],
                  "risks": ["none"],
                  "satisfiesTask": "creates output.txt"
                }
                """,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            PlanReviewSummary = "approved earlier",
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.Equal("approved earlier", final.PlanReviewSummary);
        Assert.Contains("## Reviewed planning metadata", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("already approved", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentPauseResume_FromPlanningRetryClearsPlanAndReplansEndToEnd()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        using var pauses = new SqliteAgentPauseController(
            Path.Combine(_workspace, "planning-pause.db"),
            NullLogger<SqliteAgentPauseController>.Instance);
        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            setup.Store,
            queue,
            new NullGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new AgentPauseRetryScheduler(
            setup.Store,
            retrier,
            pauses,
            NullLogger<AgentPauseRetryScheduler>.Instance);
        var item = NewItem("feature/pause-resume-replan") with
        {
            State = WorkItemState.WaitingForAgentResume,
            AgentPauseTarget = AgentKind.Claude,
            AgentPauseRetryFrom = "planning",
            LastError = "waiting: agent paused",
            PlanArtifact = """
                {
                  "approach": "stale paused plan",
                  "files": ["stale.txt"],
                  "testStrategy": ["stale"],
                  "risks": ["stale"],
                  "satisfiesTask": "stale"
                }
                """,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PlanReviewSummary = "stale approval",
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);

        var retried = await scheduler.RetryWaitingItemsForTestAsync("test");

        Assert.Equal(1, retried);
        var resumed = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Null(resumed.PlanArtifact);
        Assert.Null(resumed.PlanReviewedAt);
        Assert.True(queue.Count >= 1);

        await setup.Pipeline.RunAsync(resumed, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Contains("\"approach\"", final.PlanArtifact, StringComparison.Ordinal);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_EmptyPlanOutput_FailsBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent { PlanOutput = "   " };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/empty-plan") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("without producing a PLAN artifact", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_PlanningAgentFailure_FailsBeforeImplementationWithDetails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent
        {
            PlanningResult = new AgentResult(false, "planner failed", "planner stdout", "planner stderr"),
        };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/planning-failure") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("Planning agent claude reported failure", final.LastError, StringComparison.Ordinal);
        Assert.Contains("planner stderr", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_PlanReviewRejection_FailsBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            planReviewGate: new RejectingPlanReviewGate());
        var item = NewItem("feature/rejected-plan") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("Plan review rejected the planning artifact", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_CheckAndActAndAgentControl_BypassPlanningPhase()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var pauses = new SqliteAgentPauseController(
            Path.Combine(_workspace, "agent-pauses.db"),
            NullLogger<SqliteAgentPauseController>.Instance);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            agentPauseController: pauses);
        var knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PlanKnob.KeyName] = PlanKnob.ValueOn,
        };
        var check = NewItem("feature/check-bypass") with
        {
            JobType = JobType.CheckAndAct,
            Knobs = knobs,
            Check = new CheckAndActSpec
            {
                Question = "Is the repository already compliant?",
                ActionableAnswer = true,
                OnYes = new OnYesActionSpec
                {
                    Title = "unused follow-up",
                    Prompt = "unused because the scripted answer is false",
                },
            },
        };
        var control = NewItem("feature/control-bypass") with
        {
            JobType = JobType.AgentControl,
            Knobs = knobs,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Codex.Value,
                Reason = "planning bypass test",
            },
        };

        await setup.Store.CreateAsync(check);
        await setup.Pipeline.RunAsync(check, CancellationToken.None);
        await setup.Store.CreateAsync(control);
        await setup.Pipeline.RunAsync(control, CancellationToken.None);

        var finalCheck = await setup.Store.GetAsync(check.Id);
        var finalControl = await setup.Store.GetAsync(control.Id);
        Assert.Equal(WorkItemState.Done, finalCheck!.State);
        Assert.Equal(WorkItemState.Done, finalControl!.State);
        Assert.Null(finalCheck.PlanArtifact);
        Assert.Null(finalControl.PlanArtifact);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.CheckCalls);
        Assert.NotNull(await pauses.GetAgentStateAsync(AgentKind.Codex));
    }

    [Fact]
    public async Task PlanOn_PlanningQuotaFailure_ParksForPlanningRetryWithoutImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(17);
        var agent = new PlanningAwareAgent
        {
            PlanningResult = new AgentResult(false, "planner quota", null, "planner quota exhausted"),
        };
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            quotaClassifier: new PlanningQuotaClassifier(resetAt));
        var item = NewItem("feature/planning-quota") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.Equal("planning", final.QuotaRetryFrom);
        Assert.Equal("planning", final.QuotaRetryPhase);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_PlanningTransientFailure_ParksForPlanningRetryWithoutImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent
        {
            PlanningResult = new AgentResult(false, "planner network", null, "connection reset"),
            ClassifyPlanningFailureAsTransient = true,
        };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/planning-transient") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, final!.State);
        Assert.Equal("planning", final.TransientRetryFrom);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_MalformedPlanOutput_FailsBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent { PlanOutput = "PLAN:\nApproach: free form is not structured." };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/malformed-plan") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("structured JSON PLAN artifact", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_PromptEditDuringPlanning_RequeuesWithoutPersistingStalePlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/stale-during-planning") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        agent.OnPlanningBeforeReturnAsync = async ct =>
        {
            await setup.Store.TryReplacePromptAsync(item.Id, "edited while planning", DateTimeOffset.UtcNow, ct);
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Queued, final!.State);
        Assert.Equal(2, final.PromptRevision);
        Assert.Equal("edited while planning", final.Prompt);
        Assert.Null(final.PlanArtifact);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_PromptEditDuringPlanReview_DoesNotApproveStalePlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var gate = new MutatingPlanReviewGate();
        using var setup = BuildPipeline(agent, _workspace, seed, planReviewGate: gate);
        var item = NewItem("feature/stale-during-review") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        gate.OnReviewAsync = async ct =>
        {
            await setup.Store.TryReplacePromptAsync(item.Id, "edited during review", DateTimeOffset.UtcNow, ct);
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Queued, final!.State);
        Assert.Equal(2, final.PromptRevision);
        Assert.Equal("edited during review", final.Prompt);
        Assert.Null(final.PlanArtifact);
        Assert.Null(final.PlanReviewedAt);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_PromptEditAfterPlanApproval_StopsBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/stale-after-approval") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        var edited = false;
        setup.Webhooks.OnPublishAsync = async (evt, ct) =>
        {
            if (!edited && evt.Event == "work_item.plan_approved")
            {
                edited = true;
                await setup.Store.TryReplacePromptAsync(item.Id, "edited after approval", DateTimeOffset.UtcNow, ct);
            }
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Queued, final!.State);
        Assert.Equal(2, final.PromptRevision);
        Assert.Equal("edited after approval", final.Prompt);
        Assert.Null(final.PlanArtifact);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanReviewWithoutArtifact_FailsBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/bad-plan-review") with
        {
            State = WorkItemState.PlanReview,
            PlanArtifact = null,
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("Plan review cannot run before the planning artifact exists", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanApprovedWithoutReviewedArtifact_FailsBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/bad-plan-approved") with
        {
            State = WorkItemState.PlanApproved,
            PlanArtifact = null,
            PlanReviewedAt = null,
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("PlanApproved item is missing an approved planning artifact", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_OversizedPlanArtifact_IsTruncatedWithinStructuredSchema()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent
        {
            PlanOutput = $$"""
                {
                  "approach": "{{new string('x', 70 * 1024)}}",
                  "files": ["output.txt"],
                  "testStrategy": ["pipeline integration verifies final branch"],
                  "risks": ["none"],
                  "satisfiesTask": "creates output.txt"
                }
                """,
        };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/oversized-plan") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.True(final!.State == WorkItemState.Done, final.LastError);
        Assert.NotNull(final.PlanArtifact);
        Assert.Contains(new string('x', 100), final.PlanArtifact, StringComparison.Ordinal);
        Assert.True(final.PlanArtifact!.Length < 70 * 1024);
    }

    private static PlanningPipelineSetup BuildPipeline(
        IAgentRunner agent,
        string workspace,
        string seedRepoUrl,
        IReadOnlyDictionary<string, string>? projectKnobs = null,
        ISessionAgentRunner? sessionRunner = null,
        bool enableClaudeSession = false,
        IPlanReviewGate? planReviewGate = null,
        IQuotaFailureClassifier? quotaClassifier = null,
        IWorkItemAutoRetryScheduler? retryScheduler = null,
        IAgentPauseController? agentPauseController = null,
        AgentPromptPreprocessorChain? promptPreprocessors = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var registry = new AgentRegistry([agent]);
        var webhooks = new CapturingWebhookDispatcher();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Knobs = projectKnobs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ClaudeSession = new ProjectClaudeSessionConfig { Enabled = enableClaudeSession },
        });
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            knobRegistry: new KnobRegistry([new PlanKnob()]),
            quotaClassifier: quotaClassifier,
            retryScheduler: retryScheduler,
            agentPauseController: agentPauseController,
            sessionAgentRunner: sessionRunner,
            sessionDispatchOptions: new AgentSessionDispatchOptions { Enabled = enableClaudeSession },
            planReviewGate: planReviewGate,
            promptPreprocessors: promptPreprocessors);

        return new PlanningPipelineSetup(pipeline, store, webhooks, gitRoot);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Planning test",
        Prompt = "write output.txt",
        WorkBranch = branch,
    };
}

internal sealed class PlanningPipelineSetup(
    PipelineRunner Pipeline,
    SqliteWorkItemStore Store,
    CapturingWebhookDispatcher Webhooks,
    string GitRoot) : IDisposable
{
    public PipelineRunner Pipeline { get; } = Pipeline;
    public SqliteWorkItemStore Store { get; } = Store;
    public CapturingWebhookDispatcher Webhooks { get; } = Webhooks;
    public string GitRoot { get; } = GitRoot;

    public void Dispose() => Store.Dispose();
}

internal sealed partial class PlanningAwareAgent : IAgentRunner, ITextOnlyAgentRunner
{
    private const string DefaultPlan = """
        {
          "approach": "make the smallest output file change.",
          "files": ["output.txt"],
          "testStrategy": ["pipeline integration verifies final branch."],
          "risks": ["none for this fixture."],
          "satisfiesTask": "creates output.txt."
        }
        """;

    public AgentKind Kind => AgentKind.Claude;
    public int PlanningCalls { get; private set; }
    public int CheckCalls { get; private set; }
    public int WorkCalls { get; private set; }
    public bool TryPushDuringPlanning { get; init; }
    public bool ClassifyPlanningFailureAsTransient { get; init; }
    public bool StreamPlanningOutput { get; init; } = true;
    public string PlanOutput { get; init; } = DefaultPlan;
    public AgentResult? PlanningResult { get; init; }
    public Func<CancellationToken, Task>? OnPlanningBeforeReturnAsync { get; set; }
    public string LastPlanningPrompt { get; private set; } = string.Empty;
    public string LastWorkPrompt { get; private set; } = string.Empty;
    public bool PlanningReceivedSandbox { get; private set; }

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
            return await HandleMergeAsync(sandbox, workingDirectory, prompt, ct);

        if (prompt.Contains(CheckAndActPipeline.StartSentinel, StringComparison.Ordinal))
        {
            CheckCalls++;
            var verdict = string.Join('\n',
                "some preamble",
                CheckAndActPipeline.StartSentinel,
                """{"answer": false, "evidence": "planning bypass check path", "confidence": "high"}""",
                CheckAndActPipeline.EndSentinel);
            stdoutChunkCallback?.Invoke(verdict);
            return new AgentResult(true, "checked", verdict, null);
        }

        if (prompt.Contains("planning-only phase", StringComparison.Ordinal))
        {
            PlanningCalls++;
            LastPlanningPrompt = prompt;
            PlanningReceivedSandbox = true;
            var scratch = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "echo 'discard me' > \"$1/planning-scratch.txt\"", "sh", workingDirectory],
            }, ct);
            if (!scratch.Success)
                return new AgentResult(false, "failed to write planning scratch", scratch.Stdout, scratch.Stderr);

            if (TryPushDuringPlanning)
            {
                await sandbox.ExecAsync(new SandboxExec
                {
                    Argv =
                    [
                        "sh",
                        "-c",
                        """
                        set +e
                        git -C "$1" config user.email codeybox-test@example.invalid
                        git -C "$1" config user.name CodeyBoxTest
                        echo 'should not land' > "$1/planning-pushed.txt"
                        git -C "$1" add planning-pushed.txt
                        git -C "$1" commit -m planning-push-attempt >/tmp/planning-push-attempt.out 2>&1
                        git -C "$1" push origin HEAD:main >>/tmp/planning-push-attempt.out 2>&1
                        exit 0
                        """,
                        "sh",
                        workingDirectory,
                    ],
                }, ct);
            }

            if (OnPlanningBeforeReturnAsync is not null)
                await OnPlanningBeforeReturnAsync(ct);

            if (PlanningResult is not null)
                return PlanningResult;

            if (StreamPlanningOutput)
                stdoutChunkCallback?.Invoke(PlanOutput);
            return new AgentResult(true, "planned", PlanOutput, null);
        }

        WorkCalls++;
        LastWorkPrompt = prompt;
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'implemented from plan' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        return write.Success
            ? new AgentResult(true, "worked", null, null)
            : new AgentResult(false, "failed to write output.txt", write.Stdout, write.Stderr);
    }

    public AgentFailureClassification ClassifyFailure(AgentResult result)
    {
        if (!result.Success && ClassifyPlanningFailureAsTransient)
        {
            return new AgentFailureClassification(
                AgentFailureKind.TransientNetwork,
                Reason: "test transient planning failure");
        }

        return result.Success
            ? new AgentFailureClassification(AgentFailureKind.Normal)
            : AgentFailureClassifier.Classify(result.Stderr, result.Stdout, result.Summary);
    }

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential)
    {
        _ = credential;
        return null;
    }

    public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = workingDirectory;
        PlanningReceivedSandbox = sandbox is not null;

        if (!prompt.Contains("planning-only phase", StringComparison.Ordinal))
            return new TextOnlyAgentResult(false, "unexpected text-only prompt", null, prompt);

        PlanningCalls++;
        LastPlanningPrompt = prompt;

        if (OnPlanningBeforeReturnAsync is not null)
            await OnPlanningBeforeReturnAsync(ct);

        if (PlanningResult is not null)
        {
            return new TextOnlyAgentResult(
                PlanningResult.Success,
                PlanningResult.Summary,
                PlanningResult.Stdout,
                PlanningResult.Stderr);
        }

        return new TextOnlyAgentResult(true, "planned", PlanOutput, null);
    }

    private static async Task<AgentResult> HandleMergeAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        CancellationToken ct)
    {
        var match = MergePromptShape().Match(prompt);
        if (!match.Success)
            return new AgentResult(false, "could not parse merge prompt", null, null);

        var workBranch = match.Groups[1].Value;
        var merge = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "git", "-C", workingDirectory, "merge", "--no-ff",
                "-m", $"codeybox: merge {workBranch}", $"origin/{workBranch}",
            ],
        }, ct);
        return merge.Success
            ? new AgentResult(true, "merged", null, null)
            : new AgentResult(false, "merge failed", merge.Stdout, merge.Stderr);
    }

    [GeneratedRegex(@"merge branch `([^`]+)` into branch\s+`([^`]+)`",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MergePromptShape();
}

internal sealed partial class SandboxOnlyPlanningAgent : IAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int PlanningCalls { get; private set; }
    public int WorkCalls { get; private set; }
    public bool PlanningReceivedSandbox { get; private set; }

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = captureStructuredStream;

        if (prompt.Contains("planning-only phase", StringComparison.Ordinal))
        {
            PlanningCalls++;
            PlanningReceivedSandbox = true;
            stdoutChunkCallback?.Invoke(PlanningAwareAgentJson.DefaultPlan);
            return new AgentResult(true, "planned", PlanningAwareAgentJson.DefaultPlan, null);
        }

        if (prompt.StartsWith("# Merge task", StringComparison.Ordinal))
        {
            var match = MergePromptShape().Match(prompt);
            if (!match.Success)
                return new AgentResult(false, "could not parse merge prompt", null, null);

            var merge = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "git", "-C", workingDirectory, "merge", "--no-ff",
                    "-m", $"codeybox: merge {match.Groups[1].Value}", $"origin/{match.Groups[1].Value}",
                ],
            }, ct);
            return merge.Success
                ? new AgentResult(true, "merged", null, null)
                : new AgentResult(false, "merge failed", merge.Stdout, merge.Stderr);
        }

        WorkCalls++;
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'implemented without text-only planning' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        return write.Success
            ? new AgentResult(true, "worked", null, null)
            : new AgentResult(false, "failed to write output.txt", write.Stdout, write.Stderr);
    }

    [GeneratedRegex(@"merge branch `([^`]+)` into branch\s+`([^`]+)`",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MergePromptShape();
}

internal sealed class PlanningSessionRunner : IScopedSessionAgentRunner
{
    private ISandbox? _sandbox;
    private string? _workingDirectory;

    public AgentKind Kind => AgentKind.Claude;
    public int OpenCalls { get; private set; }
    public int SendTurns { get; private set; }
    public int CloseCalls { get; private set; }
    public string? OpenedModelId { get; private set; }
    public string? OpenedReasoningMode { get; private set; }
    public bool DirtyPlanningSandbox { get; init; }
    public bool TryPushDuringPlanning { get; init; }
    public List<string> PromptsSent { get; } = [];

    public Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
        => Task.FromResult(new AgentResult(false, "session test should not use one-shot RunAsync", null, null));

    public Task<AgentSessionHandle> OpenSessionAsync(
        AgentSessionOpenRequest request,
        CancellationToken ct = default)
        => OpenSessionAsync(
            request.Sandbox,
            request.WorkingDirectory,
            request.Credential,
            request.ModelId,
            request.ReasoningMode,
            ct);

    public Task<AgentSessionHandle> OpenSessionAsync(
        ISandbox sandbox,
        string workingDirectory,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default)
    {
        OpenCalls++;
        _sandbox = sandbox;
        _workingDirectory = workingDirectory;
        OpenedModelId = modelId;
        OpenedReasoningMode = reasoningMode;
        return Task.FromResult(new AgentSessionHandle(
            Kind,
            "planning-session",
            new AgentSessionSandboxRef(sandbox.Id),
            workingDirectory,
            modelId,
            reasoningMode));
    }

    public async Task<AgentResult> SendTurnAsync(
        AgentSessionHandle sessionHandle,
        string prompt,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        SendTurns++;
        PromptsSent.Add(prompt);
        if (prompt.Contains("planning-only phase", StringComparison.Ordinal))
        {
            if (DirtyPlanningSandbox)
            {
                await _sandbox!.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", "echo 'discard session scratch' > \"$1/planning-session-scratch.txt\"", "sh", _workingDirectory!],
                }, ct);
            }

            if (TryPushDuringPlanning)
            {
                await _sandbox!.ExecAsync(new SandboxExec
                {
                    Argv =
                    [
                        "sh",
                        "-c",
                        """
                        set +e
                        git -C "$1" config user.email codeybox-test@example.invalid
                        git -C "$1" config user.name CodeyBoxTest
                        echo 'should not land from session planning' > "$1/planning-session-pushed.txt"
                        git -C "$1" add planning-session-pushed.txt
                        git -C "$1" commit -m planning-session-push-attempt >/tmp/planning-session-push-attempt.out 2>&1
                        git -C "$1" push origin HEAD:main >>/tmp/planning-session-push-attempt.out 2>&1
                        git -C "$1" push /repo HEAD:main >>/tmp/planning-session-push-attempt.out 2>&1
                        exit 0
                        """,
                        "sh",
                        _workingDirectory!,
                    ],
                }, ct);
            }

            stdoutChunkCallback?.Invoke(PlanningAwareAgentJson.DefaultPlan);
            return new AgentResult(true, "planned", BuildClaudeStreamPlan(PlanningAwareAgentJson.DefaultPlan), null);
        }

        var write = await _sandbox!.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'implemented from warm plan' > \"$0\"", $"{_workingDirectory}/output.txt"],
        }, ct);
        return write.Success
            ? new AgentResult(true, "worked", null, null)
            : new AgentResult(false, "failed to write output.txt", write.Stdout, write.Stderr);
    }

    private static string BuildClaudeStreamPlan(string plan)
    {
        var escapedPlan = JsonSerializer.Serialize(plan);
        return string.Join('\n',
            """{"type":"system","subtype":"init","session_id":"planning-session","tools":[]}""",
            "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_plan\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-7\",\"content\":[{\"type\":\"text\",\"text\":"
                + escapedPlan
                + "}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}",
            """{"type":"result","subtype":"success","duration_ms":0,"num_turns":1,"result":"Done","is_error":false,"session_id":"planning-session","usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""");
    }

    public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
    {
        CloseCalls++;
        return Task.CompletedTask;
    }
}

internal static class PlanningAwareAgentJson
{
    public const string DefaultPlan = """
        {
          "approach": "make the smallest output file change.",
          "files": ["output.txt"],
          "testStrategy": ["pipeline integration verifies final branch."],
          "risks": ["none for this fixture."],
          "satisfiesTask": "creates output.txt."
        }
        """;
}

internal sealed class RejectingPlanReviewGate : IPlanReviewGate
{
    public ValueTask<PlanReviewDecision> ReviewAsync(
        WorkItem item,
        string planArtifact,
        CancellationToken ct = default)
    {
        _ = item;
        _ = planArtifact;
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PlanReviewDecision(
            Approved: false,
            Summary: "Rejected by test gate.",
            RejectionReason: "test review rejection"));
    }
}

internal sealed class MutatingPlanReviewGate : IPlanReviewGate
{
    public Func<CancellationToken, Task>? OnReviewAsync { get; set; }

    public async ValueTask<PlanReviewDecision> ReviewAsync(
        WorkItem item,
        string planArtifact,
        CancellationToken ct = default)
    {
        _ = item;
        _ = PlanArtifactDocument.ParseCanonical(planArtifact);
        if (OnReviewAsync is not null)
            await OnReviewAsync(ct);
        return new PlanReviewDecision(true, "mutating test review approved");
    }
}

internal sealed class RecordingPlanningPreprocessor : IAgentPromptPreprocessor
{
    public int Order => AgentPromptPreprocessorOrder.BuiltInFirst;

    public List<AgentPromptPhase> Phases { get; } = [];

    public Task<string> ProcessAsync(PromptContext ctx, string prompt, CancellationToken ct = default)
    {
        Phases.Add(ctx.Phase);
        if (ctx.Phase == AgentPromptPhase.Planning)
            prompt += "\nplanning-preprocessor-marker";
        return Task.FromResult(prompt);
    }
}

internal sealed class PlanningQuotaClassifier(DateTimeOffset resetAt) : IQuotaFailureClassifier
{
    private readonly QuotaDetection _detection = new(QuotaFailureKind.RateLimitExceeded, resetAt);

    public QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout)
        => Detect(agent, stderr, stdout) is { } detection
            ? QuotaFailureClassification.Quota(detection)
            : QuotaFailureClassification.None;

    public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
    {
        if (agent != AgentKind.Claude)
            return null;
        var combined = string.Concat(stderr, "\n", stdout);
        return combined.Contains("planner quota", StringComparison.OrdinalIgnoreCase)
            ? _detection
            : null;
    }
}

internal sealed class PlanningStaticOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
