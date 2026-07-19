using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Audit.Llm;
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
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
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
        Assert.StartsWith("auditor-loop/v1: Plan approved by the deterministic task-binding policy", final.PlanReviewSummary, StringComparison.Ordinal);
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
    public async Task PlanOn_NoActivePlanAuditors_FailsClosedBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed, auditors: []);
        var item = NewItem("feature/no-plan-auditors") with
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
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("no active Plan-target auditors", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_ApprovingPlan_EmitsLinkedTestCasesFromTestStrategy()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed, wireTestCaseStore: true);
        var item = NewItem("feature/plan-emits-testcases") with
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

        var cases = new List<TestCase>();
        await foreach (var tc in setup.TestCaseStore!.ListByWorkItemAsync(item.Id.ToString()))
            cases.Add(tc);

        // The fixture plan declares one testStrategy scenario:
        // "pipeline integration verifies final branch." -> Integration.
        var only = Assert.Single(cases);
        Assert.Equal(item.Id.ToString(), only.SourceWorkItemId);
        Assert.Equal(AutomationKind.Integration, only.AutomationKind);
        Assert.Contains("pipeline integration", only.Description, StringComparison.Ordinal);
        Assert.Null(only.ExecutableArtifactJson);
    }

    [Fact]
    public async Task PlanOn_EmitPlanTestCasesOff_ApprovesPlanButEmitsZeroTestCases()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        // Store IS wired so emission is observable; only the flag gates it off.
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            wireTestCaseStore: true,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                EmitPlanTestCases = false,
            });
        var item = NewItem("feature/plan-emit-off") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        // The plan is still approved and the item completes normally...
        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.PlanReviewedAt);
        Assert.Contains("\"approach\"", final.PlanArtifact, StringComparison.Ordinal);

        // ...but with the flag OFF, the fixture plan's testStrategy scenario
        // materialises NO TestCase artifacts (guard at
        // EmitPlanTestCasesAsync: !_opts.EmitPlanTestCases short-circuits).
        var cases = new List<TestCase>();
        await foreach (var tc in setup.TestCaseStore!.ListByWorkItemAsync(item.Id.ToString()))
            cases.Add(tc);
        Assert.Empty(cases);
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
    public async Task PlanOn_WithoutKnobRegistry_DisablesPlanningAndWarnsOnce()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var logger = new CapturingLogger<PipelineRunner>();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            omitKnobRegistry: true,
            pipelineLogger: logger);

        var first = NewItem("feature/no-knob-registry-1") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        var second = NewItem("feature/no-knob-registry-2") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(first);
        await setup.Pipeline.RunAsync(first, CancellationToken.None);
        await setup.Store.CreateAsync(second);
        await setup.Pipeline.RunAsync(second, CancellationToken.None);

        // Planning is disabled when the registry is missing: items go straight
        // to implementation without producing or persisting a plan.
        var firstFinal = await setup.Store.GetAsync(first.Id);
        Assert.NotNull(firstFinal);
        Assert.Equal(WorkItemState.Done, firstFinal!.State);
        Assert.Null(firstFinal.PlanArtifact);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(2, agent.WorkCalls);

        var warnings = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("PipelineRunner has no IKnobRegistry wired", StringComparison.Ordinal))
            .ToList();
        Assert.Single(warnings);
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
    public async Task ItemPlanOff_OverridesProjectDefaultPlanOn()
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
        var item = NewItem("feature/item-plan-off") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOff,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Null(final.PlanArtifact);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_ClaudeSessionMode_RunsPlanningColdThenImplementationInWarmSession()
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
        Assert.True(
            sessionRunner.SendTurns == 1,
            "Expected only implementation to use the warm session after plan approval. Prompts: "
            + string.Join("\n---\n", sessionRunner.PromptsSent));
        Assert.Equal(1, sessionRunner.CloseCalls);
        Assert.Equal("claude-opus-4-7", sessionRunner.OpenedModelId);
        Assert.Equal("max", sessionRunner.OpenedReasoningMode);
        Assert.DoesNotContain("planning-only phase", sessionRunner.PromptsSent[0], StringComparison.Ordinal);
        Assert.Contains("Reviewed planning metadata", sessionRunner.PromptsSent[0], StringComparison.Ordinal);
        Assert.Contains("output.txt", sessionRunner.PromptsSent[0], StringComparison.Ordinal);
        Assert.Equal(1, agent.PlanningCalls);
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
    public async Task PlanOn_QueuedItemWithStalePlanArtifactClearsAndReplans()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/stale-plan-replans") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
            PlanArtifact = """
                {
                  "approach": "old stale plan",
                  "files": ["old.txt"],
                  "testStrategy": ["old tests"],
                  "risks": ["old risk"],
                  "satisfiesTask": "old task"
                }
                """,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddHours(-2),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddHours(-1),
            PlanReviewSummary = "old approval",
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.NotNull(final.PlanArtifact);
        Assert.DoesNotContain("old stale plan", final.PlanArtifact, StringComparison.Ordinal);
        Assert.Contains("make the smallest output file change", final.PlanArtifact, StringComparison.Ordinal);
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
    public async Task PlanOn_RunnerWithoutTextOnlyCapability_PlanningSandboxGetsCredentialAndAgentNetwork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new SandboxOnlyPlanningAgent();
        var recorder = new PlanningRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            credentials: new PlanningMarkerCredentialProvider(),
            sandboxProvider: recorder,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [PlanningMarkerCredentialProvider.MarkerHost],
            });
        var item = NewItem("feature/no-text-only-plan-credential") with
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
        Assert.True(agent.PlanningReceivedCredential);

        var planningSpec = Assert.Single(recorder.SpecsForPhase("planning"));
        Assert.Equal(
            PlanningMarkerCredentialProvider.MarkerValue,
            planningSpec.Environment[PlanningMarkerCredentialProvider.MarkerKey]);
        Assert.Contains(PlanningMarkerCredentialProvider.MarkerHost, planningSpec.Network.AllowedHosts);

        // Defense-in-depth: every repository host-bind on the planning spec
        // must be ReadOnly AND SnapshotForIsolation. The snapshot flag forces
        // multipass (and any provider with no kernel-level RO option) to stage
        // an isolated copy of the bare repo so a malicious plan agent that
        // breaks out of the read-only mount can't follow symlinks back into
        // the host tree. The writable workspace and credentials tmpfs are
        // exempt — they are tmpfs mounts, not bare-repo binds. A regression
        // that drops either flag from a repo mount silently weakens the
        // planning-phase isolation contract.
        var repoMounts = planningSpec.Mounts
            .Where(m => !m.Tmpfs && m.HostPath is not null)
            .ToList();
        Assert.NotEmpty(repoMounts);
        foreach (var mount in repoMounts)
        {
            Assert.True(mount.ReadOnly,
                $"Planning mount at {mount.SandboxPath} must be ReadOnly=true.");
            Assert.True(mount.SnapshotForIsolation,
                $"Planning mount at {mount.SandboxPath} must be SnapshotForIsolation=true.");
        }
    }

    [Fact]
    public async Task PlanOn_ExtractorReturnsNull_FallsBackToRawStdoutAndPersistsPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // AlwaysReturnNullFromExtractor forces NormalizePlanArtifact down the
        // null-return fallback branch that every non-Claude runner relies on.
        // Plain JSON stdout is delivered as-is; the parser still accepts it.
        var agent = new PlanningAwareAgent
        {
            StreamPlanningOutput = false,
            AlwaysReturnNullFromExtractor = true,
        };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/null-extractor-fallback") with
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

        // Confirm the fallback branch — not the extractor's parsed output — fed
        // the plan into NormalizeRaw. Every extractor invocation returned null,
        // proving the orchestrator routed the raw stdout straight to
        // PlanArtifactDocument when the runner-provided extractor declined.
        Assert.True(agent.ExtractorInvocations > 0,
            "Extractor must have been invoked at least once by the orchestrator.");
        Assert.Equal(agent.ExtractorInvocations, agent.LastExtractorNullReturns);
    }

    [Fact]
    public async Task PlanOn_StructuredStreamRunnerWithoutPlanExtractor_DoesNotCaptureEnvelopeForPlanParsing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new StructuredPlainPlanningAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/plain-structured-runner") with
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
        Assert.NotEmpty(agent.CaptureStructuredStreamCalls);
        Assert.False(agent.CaptureStructuredStreamCalls[0]);
        Assert.All(agent.CaptureStructuredStreamCalls, Assert.False);
        Assert.Equal(0, agent.StructuredStreamSupportProbeCount);
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
        Assert.StartsWith("auditor-loop/v1: Plan approved by the deterministic task-binding policy", final.PlanReviewSummary, StringComparison.Ordinal);
        Assert.Contains("## Reviewed planning metadata", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("output.txt", agent.LastWorkPrompt, StringComparison.Ordinal);
        Assert.Contains("resume from review", agent.LastWorkPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeFromLegacyPlanApproved_ReopensReviewBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([new AuditResult(true, [])]);
        using var setup = BuildPipeline(agent, _workspace, seed, auditors: [auditor]);
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
        Assert.Equal(1, auditor.Calls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.NotEqual("approved earlier", final.PlanReviewSummary);
        Assert.StartsWith("auditor-loop/v1:", final.PlanReviewSummary, StringComparison.Ordinal);
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
    public async Task PlanOn_PlanningInfrastructureFailure_FailsWithInfraMetadata()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent
        {
            PlanningResult = new AgentResult(
                Success: false,
                Summary: "agent exited 127",
                Stdout: null,
                Stderr: "env: 'claude': No such file or directory"),
        };
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/planning-infra") with
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
        Assert.Equal(WorkItemFailureKinds.Infrastructure, final.FailureKind);
        Assert.Equal(AgentKind.Claude, final.Agent);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("Planning agent claude reported failure", final.LastError, StringComparison.Ordinal);
        Assert.Contains("agent exited 127", final.LastError, StringComparison.Ordinal);

        var involvement = await setup.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Contains(
            involvement,
            row => row.AgentKind == AgentKind.Claude
                && row.Phase == "planning"
                && row.Outcome == AgentInvolvementOutcomes.FailureInfrastructure
                && row.EndedAt is not null);

        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            setup.Store,
            queue,
            new NullGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new AgentRestoreRetryScheduler(
            setup.Store,
            retrier,
            () => new AgentRestoreRetryOptions
            {
                Enabled = true,
                LookbackGrace = TimeSpan.FromMinutes(30),
                PostRestoreMargin = TimeSpan.FromMinutes(5),
                MaxCandidatesPerSweep = 10,
            },
            NullLogger<AgentRestoreRetryScheduler>.Instance,
            involvement: setup.Involvement);

        var summary = await scheduler.SweepForTestAsync(new AgentRestoredEvent(
            AgentKind.Claude,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow));

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(WorkItemState.Queued, (await setup.Store.GetAsync(item.Id))!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PlanOn_PlanningAuthFailure_FailsWithAuthMetadataAndRestoresFromAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var agent = new PlanningAwareAgent
        {
            PlanningResult = new AgentResult(
                Success: false,
                Summary: "agent exited 1",
                Stdout: null,
                Stderr: "API Error: 401 Unauthorized"),
        };
        using var setup = BuildPipeline(agent, _workspace, seed, authAvailability: registry);
        var item = NewItem("feature/planning-auth") with
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
        Assert.Equal(WorkItemFailureKinds.AuthRequired, final.FailureKind);
        Assert.Equal(WorkItemAuthFailureScope.Fleet, final.AuthFailureScope);
        Assert.Equal(AgentKind.Claude, final.Agent);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("auth required from agent output during planning", final.LastError, StringComparison.Ordinal);

        var involvement = await setup.Involvement.ListByWorkItemAsync(item.Id, CancellationToken.None);
        Assert.Contains(
            involvement,
            row => row.AgentKind == AgentKind.Claude
                && row.Phase == "planning"
                && row.Outcome == AgentInvolvementOutcomes.FailureAuth
                && row.EndedAt is not null);

        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            setup.Store,
            queue,
            new NullGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new AgentRestoreRetryScheduler(
            setup.Store,
            retrier,
            () => new AgentRestoreRetryOptions
            {
                Enabled = true,
                LookbackGrace = TimeSpan.FromMinutes(30),
                PostRestoreMargin = TimeSpan.FromMinutes(5),
                MaxCandidatesPerSweep = 10,
            },
            NullLogger<AgentRestoreRetryScheduler>.Instance,
            involvement: setup.Involvement);

        var restored = registry.Reset(AgentKind.Claude);
        Assert.NotNull(restored);
        var summary = await scheduler.SweepForTestAsync(restored!);

        Assert.Equal(1, summary.Requeued);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(WorkItemState.Queued, (await setup.Store.GetAsync(item.Id))!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PlanOn_PlanReviewAlwaysRejects_FailsAfterMaxPlanIterations()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "architecture:llm-review",
                AuditSeverity.Error,
                "test review rejection",
                "rejected by the test reviewer")]),
        ]);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            maxPlanReviewIterations: 2);
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
        // Initial plan + one rework turn = 2 planning calls (== max iterations),
        // then the still-blocked plan fails before any implementation.
        Assert.Equal(2, agent.PlanningCalls);
        Assert.Equal(2, final.PlanReviewAttempts);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("did not approve the planning artifact after 2 plan-review iteration", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReviewIterationCap_UsesHotReloadedSnapshotAtLifecycleStart()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "architecture:llm-review",
                AuditSeverity.Error,
                "revise once",
                "The first plan needs one revision.")]),
            new AuditResult(true, []),
        ]);
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            MaxPlanReviewIterations = 1,
        });
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            pipelineTuning: tuning);
        tuning.Replace(new PipelineTuningOptions { MaxPlanReviewIterations = 2 });
        var item = NewItem("feature/hot-reloaded-plan-cap") with
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
        Assert.Equal(2, final.PlanReviewAttempts);
        Assert.Equal(2, auditor.Calls);
    }

    [Fact]
    public async Task PlanOn_PlanReviewRejectsThenApproves_ReworksPlanThenImplements()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "architecture:llm-review",
                AuditSeverity.Error,
                "needs a different approach",
                "The data flow is backward.")]),
            new AuditResult(true, []),
        ]);
        using var setup = BuildPipeline(agent, _workspace, seed, auditors: [auditor]);
        var item = NewItem("feature/reworked-plan") with
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
        // First review rejected → one plan-rework turn → second review approved.
        Assert.Equal(2, agent.PlanningCalls);
        Assert.Equal(2, auditor.Calls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.NotNull(final.PlanReviewedAt);
        // The rework turn carries only trusted, enumerated metadata (category,
        // severity, stable finding id) — never the model-authored reviewer prose,
        // which must not cross into the tool-bearing planning prompt.
        Assert.Contains("was REJECTED by plan review", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("PLAN_REVIEW_REWORK_FEEDBACK_JSON", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"Category\":\"architecture\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"FindingId\":\"f-", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("different approach", agent.LastPlanningPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data flow is backward", agent.LastPlanningPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanReworkFeedback_DoesNotForwardReviewerProseToToolBearingPrompt()
    {
        const string TruncatedTail = "REVIEWER_TAIL_MUST_NOT_CROSS_BOUNDARY";
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "architecture:llm-review",
                AuditSeverity.Error,
                new string('t', 500) + TruncatedTail,
                new string('d', 5000) + TruncatedTail,
                "PLAN:" + new string('l', 500) + TruncatedTail)]),
            new AuditResult(true, []),
        ]);
        using var setup = BuildPipeline(agent, _workspace, seed, auditors: [auditor]);
        var item = NewItem("feature/bounded-plan-feedback") with
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
        // No reviewer prose — title, description, or location text — reaches the
        // planning prompt; only enumerated metadata (category + finding id) does.
        Assert.DoesNotContain(TruncatedTail, agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('d', 30), agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('t', 30), agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('l', 30), agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("PLAN_REVIEW_REWORK_FEEDBACK_JSON", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"FindingId\":\"f-", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.True(agent.LastPlanningPrompt.Length < 10_000, $"bounded prompt length was {agent.LastPlanningPrompt.Length}");
    }

    [Fact]
    public async Task PlanOn_PlanTargetAuditor_ReworksThenApproves()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "plan:approach",
                AuditSeverity.Error,
                "needs a different approach",
                "The data flow is backward.")]),
            new AuditResult(true, []),
        ]);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor]);
        var item = NewItem("feature/plan-target-auditor") with
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
        Assert.Equal(2, auditor.Calls);
        Assert.Equal(AuditTarget.Plan, auditor.LastContext?.EffectiveTarget);
        Assert.Contains("\"approach\"", auditor.LastContext?.PlanArtifact, StringComparison.Ordinal);
        Assert.Equal(2, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.Contains("PLAN_REVIEW_REWORK_FEEDBACK_JSON", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"Category\":\"review\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"FindingId\":\"f-", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("needs a different approach", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The data flow is backward", agent.LastPlanningPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOn_ReworkedPlanArtifactIsReviewedAndPersisted()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        const string RevisedMarker = "revised-marker";
        var initialPlan = """
            {
              "approach": "initial approach without the required marker.",
              "files": ["output.txt"],
              "testStrategy": ["pipeline integration verifies final branch."],
              "risks": ["none for this fixture."],
              "satisfiesTask": "creates output.txt."
            }
            """;
        var revisedPlan = $$"""
            {
              "approach": "updated approach with {{RevisedMarker}}.",
              "files": ["output.txt"],
              "testStrategy": ["pipeline integration verifies final branch."],
              "risks": ["none for this fixture."],
              "satisfiesTask": "creates output.txt with the revised plan."
            }
            """;
        var agent = new PlanningAwareAgent { PlanOutputs = [initialPlan, revisedPlan] };
        var auditor = new ArtifactMarkerPlanAuditor(RevisedMarker);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor]);
        var item = NewItem("feature/reworked-plan-artifact") with
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
        Assert.Equal(2, agent.PlanningCalls);
        Assert.Equal(2, auditor.SeenArtifacts.Count);
        Assert.DoesNotContain(RevisedMarker, auditor.SeenArtifacts[0], StringComparison.Ordinal);
        Assert.Contains(RevisedMarker, auditor.SeenArtifacts[1], StringComparison.Ordinal);
        Assert.NotNull(final.PlanArtifact);
        Assert.Contains(RevisedMarker, final.PlanArtifact!, StringComparison.Ordinal);
        Assert.Equal(1, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanReview_ExcludesCodeOnlyAuditorsDuringPlanReview()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var planAuditor = new ScriptedPlanTextAuditor([
            new AuditResult(true, []),
        ]);
        var codeAuditor = new CodeOnlyRecordingAuditor();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [planAuditor, codeAuditor]);
        var item = NewItem("feature/plan-excludes-code-only") with
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
        Assert.Equal(1, planAuditor.Calls);
        Assert.Equal(1, codeAuditor.Calls);
        Assert.All(codeAuditor.TargetsSeen, target => Assert.Equal(AuditTarget.Code, target));
    }

    [Fact]
    public async Task PlanReview_LlmReviewAuditorUsesSharedAuditPathAndBlocksBeforeImplementation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var reviewAgent = new PlanReviewTextAgent(AgentKind.Codex, [
            """
            {"passed":false,"findings":[{"severity":"error","title":"needs a different approach","description":"The data flow is backward."}]}
            """,
            """
            {"passed":true,"findings":[]}
            """,
        ]);
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = reviewAgent,
            ReviewFocus = "- Architectural fit before implementation.",
            FrameTemplate = "{{reviewFocus}}\n{{originalPrompt}}\n{{resultFile}}",
            Targets = AuditTargets.PlanOnly,
        });
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: new ProjectAudit
            {
                AuditTypes = ["scripted"],
                AuditAgent = AgentKind.Codex,
            },
            extraAgentRunners: [reviewAgent],
            credentials: new SingleAgentCredentialProvider(AgentKind.Codex));
        var item = NewItem("feature/llm-plan-review-text-only") with
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
        Assert.Equal(2, reviewAgent.TextOnlyCalls);
        Assert.Equal(0, reviewAgent.SandboxRunCalls);
        Assert.Equal(2, agent.PlanningCalls);
        Assert.Equal(1, agent.WorkCalls);
        Assert.Contains("PLAN_REVIEW_REWORK_FEEDBACK_JSON", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.Contains("\"Category\":\"architecture\"", agent.LastPlanningPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("needs a different approach", agent.LastPlanningPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_UnrunnableLlmReviewerFailsAsInfrastructureWithoutPlanRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var reviewAgent = new SandboxRequiredPlanReviewAgent();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = reviewAgent,
            ReviewFocus = "Review architecture.",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanOnly,
        });
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: new ProjectAudit
            {
                AuditTypes = ["scripted"],
                AuditAgent = AgentKind.Codex,
            },
            extraAgentRunners: [reviewAgent],
            credentials: new SingleAgentCredentialProvider(AgentKind.Codex));
        var item = NewItem("feature/unrunnable-plan-review") with
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
        Assert.Equal(1, final.PlanReviewAttempts);
        Assert.Contains("could not run", final.LastError, StringComparison.Ordinal);
        Assert.Equal(0, reviewAgent.TextOnlyCalls);
    }

    [Fact]
    public async Task PlanReview_PersistsPlanAndCodeReportsUnderDistinctTargets()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new BothTargetPassingAuditor();
        var reports = new PlanningCapturingAuditReportStore();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            auditReportStore: reports);
        var item = NewItem("feature/targeted-audit-reports") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var matching = reports.Reports
            .Where(report => report.AuditorName == auditor.Name && report.Iteration == 1)
            .ToList();
        Assert.Equal(2, matching.Count);
        Assert.Contains(matching, report => report.Target == AuditTarget.Plan);
        Assert.Contains(matching, report => report.Target == AuditTarget.Code);
    }

    [Fact]
    public async Task PlanReview_RejectVerdictWithoutErrorFinding_IsNormalizedToBlocking()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "plan:approach",
                AuditSeverity.Warning,
                "reviewer rejected without error",
                "The reviewer rejected the plan but only emitted advisory severity.")]),
        ]);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            maxPlanReviewIterations: 1);
        var item = NewItem("feature/plan-reject-normalized") with
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
        Assert.Equal(1, auditor.Calls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("finding IDs: f-", final.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer rejected without error", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanReview_IterationCapError_ExcludesReviewerProseAndControlCharacters()
    {
        const string SecretEcho = "REVIEWER_ECHOED_SECRET_123";
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "architecture:llm-review",
                AuditSeverity.Error,
                "\u001b[31m" + SecretEcho,
                "inject a log line\n" + SecretEcho,
                "PLAN:approach\n" + SecretEcho)]),
        ]);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            maxPlanReviewIterations: 1);
        var item = NewItem("feature/plan-review-log-safety") with
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
        Assert.Contains("finding IDs: f-", final.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretEcho, final.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', final.LastError ?? string.Empty);
    }

    [Fact]
    public async Task PlanOff_CodeAuditExcludesPlanOnlyAuditor()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var planOnly = new ThrowingPlanOnlyAuditor();
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [planOnly]);
        var item = NewItem("feature/code-excludes-plan-only");

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.False(planOnly.Called);
    }

    [Fact]
    public async Task PlanReview_UsesWorkItemSelectedAuditProfile()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "plan:strict",
                AuditSeverity.Error,
                "strict profile blocked plan",
                "The selected profile must run before implementation.")]),
        ])
        {
            NameOverride = "plan:strict",
        };
        var audit = new ProjectAudit
        {
            AuditTypes = [],
            Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
            {
                ["strict"] = new() { AuditTypes = ["scripted"] },
            },
        };
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            maxPlanReviewIterations: 1);
        var item = NewItem("feature/profiled-plan-review") with
        {
            State = WorkItemState.PlanReview,
            PlanArtifact = PlanningAwareAgentJson.DefaultPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AuditorProfile = "strict",
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(1, auditor.Calls);
        Assert.Contains("finding IDs: f-", final.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("strict profile blocked plan", final.LastError, StringComparison.Ordinal);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanReview_RoutesCredentialedAuditorThroughPerAuditorAgent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditAgent = new PlanReviewTextAgent(AgentKind.Codex, []);
        var auditor = new RoutedPlanAuditor();
        var audit = new ProjectAudit
        {
            AuditTypes = ["scripted"],
            PerAuditorAgent = new Dictionary<string, AgentKind>(StringComparer.OrdinalIgnoreCase)
            {
                [auditor.Name] = AgentKind.Codex,
            },
        };
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            extraAgentRunners: [auditAgent],
            credentials: new SingleAgentCredentialProvider(AgentKind.Codex));
        var item = NewItem("feature/plan-review-routing") with
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
        Assert.Equal(AgentKind.Codex, auditor.ObservedRunnerKind);
        Assert.Equal(AgentKind.Codex, auditor.ObservedCredentialKind);
    }

    [Fact]
    public async Task ResumeFromPlanReview_EnforcesPersistedPlanReviewAttempts()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([
            new AuditResult(false, [new AuditFinding(
                "architecture:llm-review",
                AuditSeverity.Error,
                "still blocked",
                "still blocked")]),
        ]);
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            auditors: [auditor],
            maxPlanReviewIterations: 2);
        var item = NewItem("feature/resumed-cap") with
        {
            State = WorkItemState.PlanReview,
            PlanArtifact = PlanningAwareAgentJson.DefaultPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PlanReviewAttempts = 1,
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(2, final.PlanReviewAttempts);
        Assert.Equal(0, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
        Assert.Contains("after 2 plan-review iteration", final.LastError, StringComparison.Ordinal);
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
    public async Task PlanOn_StateRaceDuringPlanPersistence_FailsAmbiguousPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        using var setup = BuildPipeline(agent, _workspace, seed);
        var item = NewItem("feature/state-race-during-planning") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        agent.OnPlanningBeforeReturnAsync = async ct =>
        {
            var current = await setup.Store.GetAsync(item.Id, ct)
                ?? throw new InvalidOperationException("test item disappeared");
            await setup.Store.UpdateAsync(current with
            {
                State = WorkItemState.WorkComplete,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Planning artifact persistence raced with state WorkComplete", final.LastError, StringComparison.Ordinal);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_PromptEditDuringPlanReview_DoesNotApproveStalePlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([new AuditResult(true, [])]);
        using var setup = BuildPipeline(agent, _workspace, seed, auditors: [auditor]);
        var item = NewItem("feature/stale-during-review") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        auditor.OnReviewAsync = async ct =>
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
    public async Task PlanOn_StateRaceDuringPlanReviewApproval_FailsAmbiguousPlan()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        var auditor = new ScriptedPlanTextAuditor([new AuditResult(true, [])]);
        using var setup = BuildPipeline(agent, _workspace, seed, auditors: [auditor]);
        var item = NewItem("feature/state-race-during-review") with
        {
            Knobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlanKnob.KeyName] = PlanKnob.ValueOn,
            },
        };
        auditor.OnReviewAsync = async ct =>
        {
            var current = await setup.Store.GetAsync(item.Id, ct)
                ?? throw new InvalidOperationException("test item disappeared");
            await setup.Store.UpdateAsync(current with
            {
                State = WorkItemState.WorkComplete,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
        };

        await setup.Store.CreateAsync(item);
        await setup.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await setup.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Plan review approval raced with state WorkComplete", final.LastError, StringComparison.Ordinal);
        Assert.Equal(1, agent.PlanningCalls);
        Assert.Equal(0, agent.WorkCalls);
    }

    [Fact]
    public async Task PlanOn_StateRaceDuringPlanReviewTransition_FailsStaleContinuation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var agent = new PlanningAwareAgent();
        PlanningTransitionRaceStore? raceStore = null;
        using var setup = BuildPipeline(
            agent,
            _workspace,
            seed,
            workItemStoreDecorator: store => raceStore = new PlanningTransitionRaceStore(store));
        var item = NewItem("feature/state-race-during-transition") with
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
        Assert.True(raceStore!.Raced);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Planning transition for work item", final.LastError, StringComparison.Ordinal);
        Assert.Contains("raced with state WorkComplete", final.LastError, StringComparison.Ordinal);
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
    public void ApprovedPlanSnapshotMatches_RejectsMismatchedPlanStateFields()
    {
        // TryEnterWorkFromApprovedPlanAsync uses ApprovedPlanSnapshotMatches
        // as its race-detection guard. The existing PlanOn_PromptEditAfter
        // PlanApproval_StopsBeforeImplementation test exercises the
        // prompt-revision-bump branch (because TryReplacePromptAsync clears
        // plan_* alongside bumping the revision). This direct unit test
        // pins the broader snapshot contract: ANY of the six fields
        // (State, PromptRevision, PlanReviewedAt, PlanGeneratedAt,
        // PlanArtifact, PlanReviewSummary) changing INDEPENDENTLY drives
        // the match check to false. The static helper is private, so the
        // test invokes it via reflection through the
        // [InternalsVisibleTo("CodeyBox.Tests")] surface.
        var generatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var reviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var planArtifact = """
            {
              "approach": "original plan",
              "files": ["output.txt"],
              "testStrategy": ["run"],
              "risks": ["none"],
              "satisfiesTask": "yes"
            }
            """;
        var approved = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.PlanApproved,
            PromptRevision = 1,
            PlanArtifact = planArtifact,
            PlanGeneratedAt = generatedAt,
            PlanReviewedAt = reviewedAt,
            PlanReviewSummary = "approved",
        };

        var method = typeof(PipelineRunner).GetMethod(
            "ApprovedPlanSnapshotMatches",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        bool Match(WorkItem current, WorkItem snap)
            => (bool)method!.Invoke(null, [current, snap])!;

        // Identical snapshots match.
        Assert.True(Match(approved, approved));

        // State change → mismatch (Working / Failed / Queued etc.).
        Assert.False(Match(approved with { State = WorkItemState.Working }, approved));
        Assert.False(Match(approved with { State = WorkItemState.Queued }, approved));

        // PromptRevision bump → mismatch.
        Assert.False(Match(approved with { PromptRevision = 2 }, approved));

        // PlanArtifact text mutated → mismatch (the audit's specific race
        // scenario: a concurrent writer swaps the plan body without
        // touching the prompt).
        Assert.False(Match(approved with { PlanArtifact = planArtifact + " " }, approved));
        Assert.False(Match(approved with { PlanArtifact = null }, approved));

        // PlanReviewSummary mutated → mismatch.
        Assert.False(Match(approved with { PlanReviewSummary = "tampered" }, approved));
        Assert.False(Match(approved with { PlanReviewSummary = null }, approved));

        // Plan timestamps drift → mismatch.
        Assert.False(Match(approved with { PlanGeneratedAt = generatedAt.AddSeconds(1) }, approved));
        Assert.False(Match(approved with { PlanReviewedAt = reviewedAt.AddSeconds(1) }, approved));
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
        IQuotaFailureClassifier? quotaClassifier = null,
        IWorkItemAutoRetryScheduler? retryScheduler = null,
        IAgentPauseController? agentPauseController = null,
        AgentPromptPreprocessorChain? promptPreprocessors = null,
        ICredentialProvider? credentials = null,
        ISandboxProvider? sandboxProvider = null,
        PipelineOptions? pipelineOptions = null,
        Func<SqliteWorkItemStore, IWorkItemStore>? workItemStoreDecorator = null,
        IReadOnlyList<IAuditor>? auditors = null,
        ProjectAudit? projectAudit = null,
        IReadOnlyList<IAgentRunner>? extraAgentRunners = null,
        bool omitKnobRegistry = false,
        bool wireTestCaseStore = false,
        ILogger<PipelineRunner>? pipelineLogger = null,
        IAuditReportStore? auditReportStore = null,
        PipelineTuningSnapshot? pipelineTuning = null,
        int maxPlanReviewIterations = 3,
        IAgentAuthAvailabilityRegistry? authAvailability = null)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var pipelineStore = workItemStoreDecorator?.Invoke(store) ?? store;
        var involvement = new SqliteAgentInvolvementStore(stateDb);
        // Share the same DB file so the test_cases FK to work_items resolves.
        var testCaseStore = wireTestCaseStore ? new SqliteTestCaseStore(stateDb) : null;
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = sandboxProvider
            ?? new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var runners = new List<IAgentRunner> { agent };
        if (extraAgentRunners is not null)
            runners.AddRange(extraAgentRunners);
        var registry = new AgentRegistry(runners);
        var webhooks = new CapturingWebhookDispatcher();
        var auditorList = (auditors ?? [new PassingPlanAuditor()]).ToList();
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Knobs = projectKnobs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ClaudeSession = new ProjectClaudeSessionConfig { Enabled = enableClaudeSession },
            Audit = projectAudit ?? new ProjectAudit { AuditTypes = auditTypes },
        });
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditorList));
        var terminalTransitions = TestSupport.CreateTerminalTransition(pipelineStore, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, credentials ?? new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            pipelineStore, webhooks,
            pipelineOptions ?? new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
            },
            pipelineLogger ?? NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions,
            knobRegistry: omitKnobRegistry ? null : new KnobRegistry([new PlanKnob()]),
            quotaClassifier: quotaClassifier,
            retryScheduler: retryScheduler,
            agentPauseController: agentPauseController,
            sessionAgentRunner: sessionRunner,
            sessionDispatchOptions: new AgentSessionDispatchOptions { Enabled = enableClaudeSession },
            promptPreprocessors: promptPreprocessors,
            testCaseStore: testCaseStore,
            auditReports: auditReportStore,
            pipelineTuning: pipelineTuning ?? new PipelineTuningSnapshot(new PipelineTuningOptions
            {
                MaxPlanReviewIterations = maxPlanReviewIterations,
            }),
            involvement: involvement,
            authAvailability: authAvailability);

        return new PlanningPipelineSetup(pipeline, store, involvement, webhooks, gitRoot, testCaseStore);
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
    SqliteAgentInvolvementStore Involvement,
    CapturingWebhookDispatcher Webhooks,
    string GitRoot,
    SqliteTestCaseStore? TestCaseStore = null) : IDisposable
{
    public PipelineRunner Pipeline { get; } = Pipeline;
    public SqliteWorkItemStore Store { get; } = Store;
    public SqliteAgentInvolvementStore Involvement { get; } = Involvement;
    public CapturingWebhookDispatcher Webhooks { get; } = Webhooks;
    public string GitRoot { get; } = GitRoot;
    public SqliteTestCaseStore? TestCaseStore { get; } = TestCaseStore;

    public void Dispose()
    {
        TestCaseStore?.Dispose();
        Involvement.Dispose();
        Store.Dispose();
    }
}


internal sealed partial class PlanningAwareAgent : IAgentRunner, ITextOnlyAgentRunner, IPlanArtifactExtractor
{
    /// <summary>
    /// When true, the extractor unconditionally returns null regardless of
    /// stdout shape. Used to drive PipelineRunner.NormalizePlanArtifact's
    /// fallback-to-raw branch under test (the production trigger for null is
    /// "no stream envelope observed", which is also how every non-Claude
    /// runner's stdout shape arrives).
    /// </summary>
    public bool AlwaysReturnNullFromExtractor { get; init; }

    public int ExtractorInvocations { get; private set; }
    public int LastExtractorNullReturns { get; private set; }

    public string? ExtractPlanArtifactText(string rawStdout)
    {
        ExtractorInvocations++;
        if (AlwaysReturnNullFromExtractor)
        {
            LastExtractorNullReturns++;
            return null;
        }
        var extracted = ClaudePlanArtifactExtractor.Extract(rawStdout);
        if (extracted is null)
            LastExtractorNullReturns++;
        return extracted;
    }

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
    public IReadOnlyList<string>? PlanOutputs { get; init; }
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

            var planOutput = CurrentPlanOutput();
            if (StreamPlanningOutput)
                stdoutChunkCallback?.Invoke(planOutput);
            return new AgentResult(true, "planned", planOutput, null);
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

        return new TextOnlyAgentResult(true, "planned", CurrentPlanOutput(), null);
    }

    private string CurrentPlanOutput()
    {
        if (PlanOutputs is not { Count: > 0 })
            return PlanOutput;

        var index = Math.Clamp(PlanningCalls - 1, 0, PlanOutputs.Count - 1);
        return PlanOutputs[index];
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

internal sealed partial class SandboxOnlyPlanningAgent : IAgentRunner, IAgentCredentialEnvironmentPolicy
{
    public AgentKind Kind => AgentKind.Claude;
    public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            PlanningMarkerCredentialProvider.MarkerKey,
        };
    public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations { get; } = [];
    public int PlanningCalls { get; private set; }
    public int WorkCalls { get; private set; }
    public bool PlanningReceivedSandbox { get; private set; }
    public bool PlanningReceivedCredential { get; private set; }

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
            PlanningReceivedCredential = credential?.EnvironmentVariables.ContainsKey(PlanningMarkerCredentialProvider.MarkerKey) == true;
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

internal sealed class StructuredPlainPlanningAgent : IAgentRunner, IStructuredStreamAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public int PlanningCalls { get; private set; }
    public int WorkCalls { get; private set; }
    public int StructuredStreamSupportProbeCount { get; private set; }
    public List<bool> CaptureStructuredStreamCalls { get; } = [];

    public Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default)
    {
        _ = sandbox;
        _ = ct;
        StructuredStreamSupportProbeCount++;
        return Task.FromResult(true);
    }

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
        CaptureStructuredStreamCalls.Add(captureStructuredStream);

        if (prompt.Contains("planning-only phase", StringComparison.Ordinal))
        {
            PlanningCalls++;
            var stdout = captureStructuredStream
                ? """{"type":"assistant_delta","delta":{"text":"not a PLAN object"}}"""
                : PlanningAwareAgentJson.DefaultPlan;
            stdoutChunkCallback?.Invoke(stdout);
            return new AgentResult(true, "planned", stdout, null);
        }

        WorkCalls++;
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "echo 'implemented by structured plain agent' > \"$0\"", $"{workingDirectory}/output.txt"],
        }, ct);
        return write.Success
            ? new AgentResult(true, "worked", null, null)
            : new AgentResult(false, "failed to write output.txt", write.Stdout, write.Stderr);
    }
}

internal sealed class PlanningSessionRunner : IScopedSessionAgentRunner, IPlanArtifactExtractor
{
    private ISandbox? _sandbox;
    private string? _workingDirectory;

    public AgentKind Kind => AgentKind.Claude;

    public string? ExtractPlanArtifactText(string rawStdout)
        => ClaudePlanArtifactExtractor.Extract(rawStdout);
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

internal sealed class PlanningMarkerCredentialProvider : ICredentialProvider
{
    public const string MarkerKey = "CODEYBOX_PLANNING_CREDENTIAL_MARKER";
    public const string MarkerValue = "planning-credential-present";
    public const string MarkerHost = "planning-agent.example.invalid";

    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult<AgentCredential?>(new AgentCredential(
            agent,
            EnvironmentVariables: new Dictionary<string, string> { [MarkerKey] = MarkerValue },
            Files: new Dictionary<string, string>()));
}

internal sealed class PlanningRecordingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
{
    private readonly List<SandboxSpec> _specs = [];

    public string Name => inner.Name;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        lock (_specs)
            _specs.Add(spec);
        return inner.CreateAsync(spec, ct);
    }

    public IReadOnlyList<SandboxSpec> SpecsForPhase(string phase)
    {
        lock (_specs)
            return _specs.Where(s => s.TimingPhase == phase).ToList();
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        => inner.ListAllManagedAsync(ct);

    public Task DisposeLeakedAsync(string name, CancellationToken ct)
        => inner.DisposeLeakedAsync(name, ct);
}

internal sealed class ScriptedPlanTextAuditor(IReadOnlyList<AuditResult> results) : IAuditor
{
    public string? NameOverride { get; init; }
    public string Name => NameOverride ?? "plan:approach";
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.None;
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;
    public int Calls { get; private set; }
    public AuditContext? LastContext { get; private set; }
    public Func<CancellationToken, Task>? OnReviewAsync { get; set; }

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastContext = context;
        if (OnReviewAsync is not null)
            await OnReviewAsync(ct);
        var idx = Math.Min(Calls - 1, results.Count - 1);
        return results[idx];
    }
}

internal sealed class PassingPlanAuditor : IAuditor
{
    public string Name => "plan:contract-pass";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AuditResult(
            context.EffectiveTarget == AuditTarget.Plan && !string.IsNullOrWhiteSpace(context.PlanArtifact),
            []));
    }
}

internal sealed class BothTargetPassingAuditor : IAuditor
{
    public string Name => "shared:both-targets";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanAndCode;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = context;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AuditResult(true, [], RawOutput: "passed"));
    }
}

internal sealed class PlanningCapturingAuditReportStore : IAuditReportStore
{
    public List<AuditReport> Reports { get; } = [];

    public Task CreateAsync(AuditReport report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Reports.Add(report);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(
        string workItemId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditReport>>(
            Reports.Where(report => report.WorkItemId == workItemId).ToList());

    public Task<string?> GetRawOutputAsync(
        string workItemId,
        AuditTarget target,
        int iteration,
        string auditorName,
        CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => Task.FromResult(0);
}

internal sealed class ArtifactMarkerPlanAuditor(string requiredMarker) : IAuditor
{
    public string Name => "plan:artifact-marker";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;
    public List<string> SeenArtifacts { get; } = [];

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        var artifact = context.PlanArtifact ?? string.Empty;
        SeenArtifacts.Add(artifact);
        if (artifact.Contains(requiredMarker, StringComparison.Ordinal))
            return Task.FromResult(new AuditResult(true, []));

        return Task.FromResult(new AuditResult(false, [new AuditFinding(
            Name,
            AuditSeverity.Error,
            "revised plan marker missing",
            $"The plan must include {requiredMarker} before implementation.")]));
    }
}

internal sealed class ThrowingPlanOnlyAuditor : IAuditor
{
    public string Name => "plan:must-not-run-during-code-audit";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;
    public bool Called { get; private set; }

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = context;
        _ = ct;
        Called = true;
        throw new InvalidOperationException("plan-only auditor must not run during code audit");
    }
}

internal sealed class RoutedPlanAuditor : IAuditor
{
    public string Name => "plan:routed";
    public string Kind => "llm";
    public AuditCapabilities Required => AuditCapabilities.AgentCredentials;
    public IReadOnlySet<AuditTarget> Targets => AuditTargets.PlanOnly;
    public AgentKind? ObservedRunnerKind { get; private set; }
    public AgentKind? ObservedCredentialKind { get; private set; }

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        ObservedRunnerKind = context.AuditRunner?.Kind;
        ObservedCredentialKind = context.AuditCredential?.Agent;
        return Task.FromResult(new AuditResult(true, []));
    }
}

internal sealed class CodeOnlyRecordingAuditor : IAuditor
{
    public string Name => "code:recording";
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;
    public int Calls { get; private set; }
    public List<AuditTarget> TargetsSeen { get; } = [];

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        Calls++;
        TargetsSeen.Add(context.EffectiveTarget);
        if (context.EffectiveTarget == AuditTarget.Plan)
            throw new InvalidOperationException("code-only auditor ran during plan review");
        return Task.FromResult(new AuditResult(true, []));
    }
}

internal sealed class PlanReviewTextAgent(AgentKind kind, IReadOnlyList<string> outputs) :
    IAgentRunner,
    ITextOnlyAgentRunner,
    IAgentCredentialEnvironmentPolicy
{
    private readonly Queue<string> _outputs = new(outputs);
    public AgentKind Kind { get; } = kind;
    public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "TEST_AGENT_CREDENTIAL" };
    public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations { get; } = [];
    public int TextOnlyCalls { get; private set; }
    public int SandboxRunCalls { get; private set; }
    public bool SupportsSeparateSystemPrompt => true;

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
    {
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        _ = captureStructuredStream;
        ct.ThrowIfCancellationRequested();
        SandboxRunCalls++;
        if (!prompt.Contains("reviewing a proposed implementation PLAN", StringComparison.Ordinal) ||
            !prompt.Contains("audit/result.json", StringComparison.Ordinal))
        {
            return Task.FromResult(new AgentResult(false, "unexpected prompt", null, prompt));
        }

        var output = _outputs.Count > 0 ? _outputs.Dequeue() : """{"passed":true,"findings":[]}""";
        return WriteResultAsync(sandbox, workingDirectory, output, ct);
    }

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
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
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        TextOnlyCalls++;
        if (!prompt.Contains("reviewing a proposed implementation PLAN", StringComparison.Ordinal))
            return Task.FromResult(new TextOnlyAgentResult(false, "unexpected prompt", null, prompt));
        var output = _outputs.Count > 0 ? _outputs.Dequeue() : """{"passed":true,"findings":[]}""";
        return Task.FromResult(new TextOnlyAgentResult(true, "reviewed", output, null));
    }

    public Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptAsync(
        string systemPrompt,
        string userPrompt,
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
        _ = sandbox;
        _ = workingDirectory;
        ct.ThrowIfCancellationRequested();
        TextOnlyCalls++;
        if (!systemPrompt.Contains("reviewing a proposed implementation PLAN", StringComparison.Ordinal)
            || !userPrompt.Contains("planArtifact", StringComparison.Ordinal))
        {
            return Task.FromResult(new TextOnlyAgentResult(
                false,
                "unexpected separated prompts",
                null,
                systemPrompt));
        }
        var output = _outputs.Count > 0 ? _outputs.Dequeue() : """{"passed":true,"findings":[]}""";
        return Task.FromResult(new TextOnlyAgentResult(true, "reviewed", output, null));
    }

    private static async Task<AgentResult> WriteResultAsync(
        ISandbox sandbox,
        string workingDirectory,
        string output,
        CancellationToken ct)
    {
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > audit/result.json"],
            WorkingDirectory = workingDirectory,
            Stdin = output,
        }, ct);

        return write.Success
            ? new AgentResult(true, "reviewed", "reviewed", null)
            : new AgentResult(false, "write failed", write.Stdout, write.Stderr);
    }
}

internal sealed class SandboxRequiredPlanReviewAgent :
    IAgentRunner,
    ITextOnlyAgentRunner,
    IAgentCredentialEnvironmentPolicy
{
    public AgentKind Kind => AgentKind.Codex;
    public IReadOnlySet<string> DirectCredentialEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "TEST_AGENT_CREDENTIAL" };
    public IReadOnlySet<string> FileBackedCredentialEnvironmentVariables { get; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<AgentCredentialFileDestination> CredentialFileDestinations { get; } = [];
    public bool TextOnlyRequiresSandbox => true;
    public bool SupportsSeparateSystemPrompt => true;
    public int TextOnlyCalls { get; private set; }

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
        => throw new InvalidOperationException("The unrunnable Plan reviewer must fail capability validation before invocation.");

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        TextOnlyCalls++;
        throw new InvalidOperationException("The unrunnable Plan reviewer must fail capability validation before invocation.");
    }

    public Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptAsync(
        string systemPrompt,
        string userPrompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        TextOnlyCalls++;
        throw new InvalidOperationException("The unrunnable Plan reviewer must fail capability validation before invocation.");
    }
}

internal sealed class NoopKindAgent(AgentKind kind) : IAgentRunner
{
    public AgentKind Kind { get; } = kind;

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
    {
        _ = sandbox;
        _ = workingDirectory;
        _ = prompt;
        _ = credential;
        _ = modelId;
        _ = reasoningMode;
        _ = stdoutChunkCallback;
        _ = captureStructuredStream;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentResult(true, "ok", null, null));
    }
}

internal sealed class SingleAgentCredentialProvider(AgentKind credentialKind) : ICredentialProvider
{
    public Task<AgentCredential?> GetAsync(AgentKind agent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<AgentCredential?>(agent == credentialKind
            ? new AgentCredential(agent, new Dictionary<string, string> { ["TEST_AGENT_CREDENTIAL"] = "1" }, new Dictionary<string, string>())
            : null);
    }
}

internal sealed class PlanningTransitionRaceStore(SqliteWorkItemStore inner) : PlanningForwardingWorkItemStore(inner)
{
    public bool Raced { get; private set; }

    public override async Task<bool> TryUpdateIfStateAndUpdatedAtAsync(
        WorkItem item,
        WorkItemState onlyIfState,
        DateTimeOffset onlyIfUpdatedAt,
        CancellationToken ct = default)
    {
        if (!Raced
            && onlyIfState == WorkItemState.Planning
            && item.State == WorkItemState.PlanReview
            && !string.IsNullOrWhiteSpace(item.PlanArtifact))
        {
            Raced = true;
            var current = await Inner.GetAsync(item.Id, ct)
                ?? throw new InvalidOperationException("test item disappeared");
            await Inner.UpdateAsync(current with
            {
                State = WorkItemState.WorkComplete,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
        }

        return await Inner.TryUpdateIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
    }
}

internal abstract class PlanningForwardingWorkItemStore(SqliteWorkItemStore inner) : IWorkItemStore
{
    protected SqliteWorkItemStore Inner { get; } = inner;

    public virtual Task CreateAsync(WorkItem item, CancellationToken ct = default) => Inner.CreateAsync(item, ct);
    public virtual Task UpdateAsync(WorkItem item, CancellationToken ct = default) => Inner.UpdateAsync(item, ct);
    public virtual Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
        Inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
    public virtual Task<bool> TryUpdateIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
        Inner.TryUpdateIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
    public virtual Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
    public virtual Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, WorkItemState onlyIfState, CancellationToken ct = default) =>
        Inner.UpdatePriorityIfStateAsync(id, priority, updatedAt, onlyIfState, ct);
    public virtual Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
    public virtual Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
    public virtual Task<bool> TryReplaceKnobsIfStateAndUpdatedAtAsync(WorkItemId id, IReadOnlyDictionary<string, string> knobs, DateTimeOffset updatedAt, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
        Inner.TryReplaceKnobsIfStateAndUpdatedAtAsync(id, knobs, updatedAt, onlyIfState, onlyIfUpdatedAt, ct);
    public virtual Task<bool> TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(WorkItem item, WorkItemState onlyIfState, DateTimeOffset onlyIfUpdatedAt, CancellationToken ct = default) =>
        Inner.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(item, onlyIfState, onlyIfUpdatedAt, ct);
    public virtual Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => Inner.GetAsync(id, ct);
    public virtual IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => Inner.ListAsync(ct);
    public virtual IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.ListByStateAsync(state, ct);
    public virtual Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) => Inner.CountByStateAsync(state, ct);
    public virtual Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) => Inner.ReorderAsync(orderedIds, ct);
    public virtual IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default) =>
        Inner.ListDispatchEligibleByPriorityAsync(skipIds, ct);
    public virtual Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
        Inner.CountStartedInWindowAsync(projectId, since, ct);
    public virtual Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) => Inner.CountInFlightAsync(projectId, ct);
    public virtual Task<(int Refactor, int Other)> CountInFlightSplitByRefactorAsync(ProjectId projectId, CancellationToken ct = default, WorkItemId? excludeId = null) =>
        Inner.CountInFlightSplitByRefactorAsync(projectId, ct, excludeId);
    public virtual Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
        Inner.GetByExternalIdAsync(projectId, externalId, ct);
    public virtual Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
        Inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
    public virtual Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
    public virtual Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
        Inner.GetFleetStateCountsAsync(ct);
    public virtual Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
        Inner.GetFleetRecentOutcomesAsync(perProject, ct);
    public virtual Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
        Inner.GetFleetPauseStatesAsync(ct);
    public virtual IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
        Inner.ListByReplaySourceAsync(sourceId, ct);
    public virtual IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => Inner.ListSuspendedAsync(ct);
    public virtual Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) =>
        Inner.GetActiveBaselineImageRefsAsync(ct);
    public virtual Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
        Inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
    public virtual Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => Inner.OrphanReplaysAsync(sourceId, ct);
    public virtual IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) => Inner.ListByReleaseAsync(releaseId, ct);
    public virtual Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        Inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
    public virtual Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
        Inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
    public virtual Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
        Inner.GetIterationsAsync(workItemId, ct);
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
