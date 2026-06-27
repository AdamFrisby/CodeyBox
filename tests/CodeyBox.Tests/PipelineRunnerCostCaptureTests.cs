using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that PipelineRunner writes cost rows via IWorkItemCostStore when
/// an IAgentCostExtractor returns a snapshot, cannot extract tokens, or no
/// extractor is registered for the completed agent invocation.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerCostCaptureTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerCostCaptureTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-cost-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task SuccessfulRun_WritesWorkPhaseCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-test.txt", "cost\n"));

        var item = NewItem("feature/cost-work");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRows = costStore.Recorded.Where(r => r.Phase == "work").ToList();
        Assert.Single(workRows);
    }

    [Fact]
    public async Task SuccessfulRun_WritesMergePhaseCostRow()
    {
        // A clean (non-conflicting) merge is completed entirely host-side with
        // no agent, so it emits no merge cost row. To assert the merge-phase
        // cost-capture call site, drive the agentic conflict resolver: the work
        // phase writes README, the auditor advances main's README during audit
        // (→ merge conflict), and the resolver runs the agent in the merge
        // sandbox — the only merge path that produces a "merge" cost row.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var auditor = new MainAdvancingAuditor(_workspace, "README.md", "main\n");
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore, auditors: [auditor]);
        auditor.GitRoot = tp.GitRoot;

        tp.Agent.WorkPlan.Enqueue(new FileWrite("README.md", "work\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(_ => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "main\nwork\n",
        });

        var item = NewItem("feature/cost-merge");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var mergeRows = costStore.Recorded.Where(r => r.Phase == "merge").ToList();
        Assert.Single(mergeRows);
        // Exactly two extractor-backed rows: the work phase and the merge
        // resolver. The tool auditor (AuditCapabilities.None) produces none.
        Assert.Equal(2, costStore.Recorded.Count);
    }

    [Fact]
    public async Task MissingExtractor_WritesElapsedFallbackCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            registerExtractor: false,
            usageStore: usageStore,
            agentKind: AgentKind.Opencode);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("no-extractor.txt", "x\n"));

        var item = NewItem("feature/no-extractor") with
        {
            Agent = AgentKind.Opencode,
            ModelId = "opencode-default-model",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal("opencode", workRow.AgentKind);
        Assert.Equal("opencode-default-model", workRow.ModelId);
        Assert.Equal(0, workRow.InputTokens);
        Assert.Equal(0, workRow.CachedInputTokens);
        Assert.Equal(0, workRow.OutputTokens);
        Assert.False(workRow.HasExtractedTokenUsage);

        var usage = Assert.Single(usageStore.Recorded, e => e.TimeUtc == workRow.EndedAt);
        Assert.Equal("opencode", usage.AgentKind);
        Assert.Equal("opencode-default-model", usage.ModelId);
        Assert.Equal(workRow.StartedAt, usage.StartedUtc);
        Assert.Equal(workRow.EndedAt, usage.EndedUtc);
        Assert.Equal((long)(workRow.EndedAt - workRow.StartedAt).TotalMilliseconds, usage.ElapsedMs);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
    }

    [Fact]
    public async Task ToolAuditors_DoNotProduceCostRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        // Configure a tool-only auditor (no AgentCredentials) that always passes.
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new PassingToolAuditor()], maxAuditIterations: 1);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-tool.txt", "a\n"));

        var item = NewItem("feature/tool-audit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Tool auditors (AuditCapabilities.None, no LLM) must not produce cost rows.
        var auditRows = costStore.Recorded.Where(r => r.Phase == "audit").ToList();
        Assert.Empty(auditRows);
    }

    [Fact]
    public async Task ReworkPhase_WritesReworkCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        // Auditor that fails on iteration 1, passes on iteration 2 → triggers one rework.
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new OnceFailingAuditor()], maxAuditIterations: 2);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-rework-work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-rework-rework.txt", "rework\n"));

        var item = NewItem("feature/cost-rework");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var reworkRows = costStore.Recorded.Where(r => r.Phase == "rework").ToList();
        Assert.Single(reworkRows);
        // The rework following audit iteration N is dispatched as iteration N+1
        // (the input the next audit pass will evaluate); the cost row therefore
        // records iteration=2 for the first rework — matching the
        // work_item_iterations dispatch row that drives the prompt-revision
        // trailer check.
        Assert.Equal(2, reworkRows[0].Iteration);
    }

    [Fact]
    public async Task SuccessfulRun_WritesUsageEvent_MatchingCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore, usageStore: usageStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("usage-test.txt", "usage\n"));

        var item = NewItem("feature/usage-work");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // One usage event per cost row, with the extractor's token fields. The
        // usage row's ModelId is the DISPATCHED model id (null here — no explicit
        // item.ModelId and ScriptedAgent has no default), NOT the parsed
        // snapshot.ModelId "fake-model"; the cost row keeps the parsed model for
        // display. See BuildUsageEvent — keying usage by dispatch model is what
        // keeps the budget gate's SUM in the same bucket spend lands in.
        Assert.Equal(costStore.Recorded.Count, usageStore.Recorded.Count);
        var ev = usageStore.Recorded[0];
        Assert.Equal("claude", ev.AgentKind);
        Assert.Null(ev.ModelId);
        Assert.Equal("fake-model", costStore.Recorded[0].ModelId);
        Assert.Equal(1000, ev.InputTokens);
        Assert.Equal(100, ev.CachedInputTokens);
        Assert.Equal(200, ev.OutputTokens);
        Assert.Equal(item.Id.ToString(), ev.WorkItemId);
        Assert.Equal("work", ev.Phase);
        Assert.Equal(costStore.Recorded[0].StartedAt, ev.StartedUtc);
        Assert.Equal(costStore.Recorded[0].EndedAt, ev.EndedUtc);
        Assert.True(ev.ElapsedMs > 0);

        // The legacy cost unit and timestamp must be derived from the same recorded
        // cost row (not a different field or scale): CostMicroCents is the cost
        // row's USD run through UsdToMicroCents, and TimeUtc is its EndedAt.
        var costRow = costStore.Recorded[0];
        Assert.Equal(AgentUsageEvent.UsdToMicroCents((decimal)costRow.EstimatedUsd), ev.CostMicroCents);
        Assert.Equal(costRow.EndedAt, ev.TimeUtc);
    }

    [Fact]
    public async Task SuccessfulRun_CostRowUsesDispatchModelWhenExtractorOmitsModel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace,
            seed,
            costStore,
            usageStore: usageStore,
            agentKind: AgentKind.Codex,
            extractorSnapshot: new AgentCostSnapshot(
                InputTokens: 1000,
                CachedInputTokens: 100,
                OutputTokens: 200,
                ModelId: null),
            pricingOptions: CodexGpt55Pricing());

        tp.Agent.WorkPlan.Enqueue(new FileWrite("codex-dispatch-model.txt", "usage\n"));

        var item = NewItem("feature/codex-dispatch-model") with
        {
            Agent = AgentKind.Codex,
            ModelId = "gpt-5.5",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal("codex", workRow.AgentKind);
        Assert.Equal("gpt-5.5", workRow.ModelId);
        Assert.Equal(1000, workRow.InputTokens);
        Assert.Equal(100, workRow.CachedInputTokens);
        Assert.Equal(200, workRow.OutputTokens);
        Assert.True(workRow.HasExtractedTokenUsage);
        Assert.Equal(0.01105, workRow.EstimatedUsd, precision: 6);

        var usage = Assert.Single(usageStore.Recorded, e => e.TimeUtc == workRow.EndedAt);
        Assert.Equal("gpt-5.5", usage.ModelId);
        Assert.Equal(AgentUsageEvent.UsdToMicroCents((decimal)workRow.EstimatedUsd), usage.CostMicroCents);
    }

    [Theory]
    [MemberData(nameof(BuiltInAgentKinds))]
    public async Task SuccessfulRun_WritesCostAndUsageRows_ForEachAgentKind(string agentKindValue)
    {
        var agentKind = new AgentKind(agentKindValue);
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore, usageStore: usageStore, agentKind: agentKind);

        tp.Agent.WorkPlan.Enqueue(new FileWrite($"{agentKindValue}-usage.txt", "usage\n"));

        var item = NewItem($"feature/{agentKindValue}-usage") with { Agent = agentKind };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal(agentKindValue, workRow.AgentKind);
        Assert.Equal(1000, workRow.InputTokens);
        Assert.Equal(100, workRow.CachedInputTokens);
        Assert.Equal(200, workRow.OutputTokens);
        Assert.True(workRow.HasExtractedTokenUsage);
        Assert.DoesNotContain("elapsed_fallback", workRow.RawMetadataJson);
        Assert.DoesNotContain("extractor_null_elapsed_fallback", workRow.RawMetadataJson);

        Assert.Equal(costStore.Recorded.Count, usageStore.Recorded.Count);
        Assert.Contains(usageStore.Recorded, e =>
            e.AgentKind == agentKindValue
            && e.WorkItemId == item.Id.ToString()
            && e.InputTokens == 1000
            && e.OutputTokens == 200);
    }

    [Fact]
    public async Task RegisteredExtractorReturningNull_WritesElapsedFallbackCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            agentKind: AgentKind.Cursor,
            extractorReturnsNull: true);

        tp.Agent.BeforeWorkAsync = async (_, _, ct) => await Task.Delay(25, ct);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("cursor-fallback.txt", "fallback\n"));

        var item = NewItem("feature/cursor-fallback") with
        {
            Agent = AgentKind.Cursor,
            ModelId = "cursor-default-model",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal("cursor", workRow.AgentKind);
        Assert.Equal("cursor-default-model", workRow.ModelId);
        Assert.Equal(0, workRow.InputTokens);
        Assert.Equal(0, workRow.CachedInputTokens);
        Assert.Equal(0, workRow.OutputTokens);
        Assert.Equal(0.0, workRow.EstimatedUsd);
        Assert.True(workRow.EndedAt > workRow.StartedAt);
        Assert.False(workRow.HasExtractedTokenUsage);

        Assert.Equal(costStore.Recorded.Count, usageStore.Recorded.Count);
        var usage = Assert.Single(usageStore.Recorded, e => e.TimeUtc == workRow.EndedAt);
        Assert.Equal("cursor", usage.AgentKind);
        Assert.Equal("cursor-default-model", usage.ModelId);
        Assert.Equal("work", usage.Phase);
        Assert.Equal(workRow.StartedAt, usage.StartedUtc);
        Assert.Equal(workRow.EndedAt, usage.EndedUtc);
        Assert.Equal((long)(workRow.EndedAt - workRow.StartedAt).TotalMilliseconds, usage.ElapsedMs);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.CostMicroCents);
        Assert.Equal(item.Id.ToString(), usage.WorkItemId);
    }

    [Fact]
    public async Task RegisteredExtractorThrowing_WritesElapsedFallbackCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            agentKind: AgentKind.Gemini,
            extractorThrows: true);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("throwing-extractor.txt", "fallback\n"));

        var item = NewItem("feature/throwing-extractor") with
        {
            Agent = AgentKind.Gemini,
            ModelId = "gemini-default-model",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal("gemini", workRow.AgentKind);
        Assert.Equal("gemini-default-model", workRow.ModelId);
        Assert.Equal(0, workRow.InputTokens);
        Assert.Equal(0, workRow.CachedInputTokens);
        Assert.Equal(0, workRow.OutputTokens);
        Assert.Equal(0.0, workRow.EstimatedUsd);
        Assert.False(workRow.HasExtractedTokenUsage);
        Assert.Contains("elapsed_fallback", workRow.RawMetadataJson);

        var usage = Assert.Single(usageStore.Recorded, e => e.TimeUtc == workRow.EndedAt);
        Assert.Equal("gemini", usage.AgentKind);
        Assert.Equal("gemini-default-model", usage.ModelId);
        Assert.Equal("work", usage.Phase);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.CostMicroCents);
    }

    [Fact]
    public async Task ExtractedNegativeSnapshot_WritesNonNegativeCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        var pricing = new AgentPricingOptions
        {
            DefaultRates =
            {
                ["claude"] = new ModelRateConfig
                {
                    InputPerMillion = 1,
                    CachedInputPerMillion = 1,
                    OutputPerMillion = 1,
                },
            },
        };
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            extractorSnapshot: new AgentCostSnapshot(
                InputTokens: -100,
                CachedInputTokens: -50,
                OutputTokens: -200,
                ModelId: "fake-model"),
            pricingOptions: pricing);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("negative-extracted.txt", "x\n"));

        var item = NewItem("feature/negative-extracted");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal(0, workRow.InputTokens);
        Assert.Equal(0, workRow.CachedInputTokens);
        Assert.Equal(0, workRow.OutputTokens);
        Assert.Equal(0.0, workRow.EstimatedUsd);
        Assert.True(workRow.HasExtractedTokenUsage);

        var usage = Assert.Single(usageStore.Recorded, e => e.TimeUtc == workRow.EndedAt);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.CachedInputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.CostMicroCents);
    }

    [Fact]
    public async Task CalculatorFailure_WritesExtractedTokenRowsWithZeroEstimatedCost()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            calculatorDefaultPricingThrows: true);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("calculator-fallback.txt", "x\n"));

        var item = NewItem("feature/calculator-fallback");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var workRow = Assert.Single(costStore.Recorded, r => r.Phase == "work");
        Assert.Equal("claude", workRow.AgentKind);
        Assert.Equal(1000, workRow.InputTokens);
        Assert.Equal(100, workRow.CachedInputTokens);
        Assert.Equal(200, workRow.OutputTokens);
        Assert.Equal(0.0, workRow.EstimatedUsd);
        Assert.True(workRow.HasExtractedTokenUsage);
        Assert.Equal("{}", workRow.RawMetadataJson);

        var usage = Assert.Single(usageStore.Recorded, e => e.TimeUtc == workRow.EndedAt);
        Assert.Equal("claude", usage.AgentKind);
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(100, usage.CachedInputTokens);
        Assert.Equal(200, usage.OutputTokens);
        Assert.Equal(0, usage.CostMicroCents);
    }

    [Fact]
    public async Task CheckAndActCheckPhase_WritesElapsedFallbackCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            agentKind: AgentKind.Cursor,
            extractorReturnsNull: true);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "nothing actionable"));

        var check = NewCheckAndActItem("feature/check-cost") with
        {
            Agent = AgentKind.Cursor,
            ModelId = "cursor-default-model",
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var checkRow = Assert.Single(costStore.Recorded, r => r.Phase == "check");
        Assert.Equal("cursor", checkRow.AgentKind);
        Assert.Equal("cursor-default-model", checkRow.ModelId);
        Assert.Equal(0, checkRow.InputTokens);
        Assert.Equal(0, checkRow.CachedInputTokens);
        Assert.Equal(0, checkRow.OutputTokens);
        Assert.False(checkRow.HasExtractedTokenUsage);
        Assert.Contains("elapsed_fallback", checkRow.RawMetadataJson);

        var usage = Assert.Single(usageStore.Recorded, e => e.Phase == "check");
        Assert.Equal(check.Id.ToString(), usage.WorkItemId);
        Assert.Equal(checkRow.EndedAt, usage.TimeUtc);
        Assert.Equal("cursor", usage.AgentKind);
        Assert.Equal("cursor-default-model", usage.ModelId);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
    }

    [Fact]
    public async Task CompletionModeCheckPhase_WritesCompletionCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        var completion = new ScriptedCompletionRunner(new CheckAndActCompletionResult(
            CheckAndActCompletionProviders.GeminiOAuth,
            AgentKind.Gemini,
            "gemini-2.5-pro",
            BuildVerdictStdout(false, "completion inspected README.md"),
            new CheckAndActCompletionUsage(
                InputTokens: 17,
                CachedInputTokens: 83,
                OutputTokens: 5,
                CacheHit: true)));
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            checkCompletionRunner: completion);

        var baseCheck = NewCheckAndActItem("feature/check-completion-cost");
        var check = baseCheck with
        {
            Check = baseCheck.Check! with { Mode = CheckAndActModes.Completion },
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var final = await tp.Store.GetAsync(check.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var checkRow = Assert.Single(costStore.Recorded, r => r.Phase == "check");
        Assert.Equal("gemini", checkRow.AgentKind);
        Assert.Equal("gemini-2.5-pro", checkRow.ModelId);
        Assert.Equal(17, checkRow.InputTokens);
        Assert.Equal(83, checkRow.CachedInputTokens);
        Assert.Equal(5, checkRow.OutputTokens);
        Assert.True(checkRow.HasExtractedTokenUsage);
        Assert.Contains("check_and_act_completion", checkRow.RawMetadataJson);
        Assert.Contains("\"cacheHit\":true", checkRow.RawMetadataJson);

        var usage = Assert.Single(usageStore.Recorded, e => e.Phase == "check");
        Assert.Equal(check.Id.ToString(), usage.WorkItemId);
        Assert.Equal("gemini", usage.AgentKind);
        Assert.Equal("gemini-2.5-pro", usage.ModelId);
        Assert.Equal(17, usage.InputTokens);
        Assert.Equal(83, usage.CachedInputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Single(completion.Requests);
        Assert.Empty(tp.Agent.CheckInvocations);
    }

    [Fact]
    public async Task PostActReCheckPhase_WritesElapsedFallbackCostAndUsageRows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(
            _workspace, seed, costStore,
            usageStore: usageStore,
            agentKind: AgentKind.Cursor,
            extractorReturnsNull: true);

        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(true, "initially actionable"));
        var check = NewCheckAndActItem("feature/post-act-check-cost") with
        {
            Agent = AgentKind.Cursor,
            ModelId = "cursor-default-model",
        };
        await tp.Store.CreateAsync(check);
        await tp.Pipeline.RunAsync(check, CancellationToken.None);

        var allItems = new List<WorkItem>();
        await foreach (var it in tp.Store.ListAsync()) allItems.Add(it);
        var followup = Assert.Single(allItems, i => i.OriginCheckWorkItemId == check.Id);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("remediation.txt", "fixed\n"));
        tp.Agent.CheckPlan.Enqueue(BuildVerdictStdout(false, "remediation satisfied the check"));
        await tp.Pipeline.RunAsync(followup, CancellationToken.None);

        var finalFollowup = await tp.Store.GetAsync(followup.Id);
        Assert.Equal(WorkItemState.Done, finalFollowup!.State);

        var recheckRow = Assert.Single(costStore.Recorded, r => r.Phase == "post-act-recheck");
        Assert.Equal("cursor", recheckRow.AgentKind);
        Assert.Null(recheckRow.ModelId);
        Assert.Equal(1, recheckRow.Iteration);
        Assert.Equal(0, recheckRow.InputTokens);
        Assert.Equal(0, recheckRow.CachedInputTokens);
        Assert.Equal(0, recheckRow.OutputTokens);
        Assert.False(recheckRow.HasExtractedTokenUsage);
        Assert.Contains("elapsed_fallback", recheckRow.RawMetadataJson);

        var usage = Assert.Single(usageStore.Recorded, e => e.Phase == "post-act-recheck");
        Assert.Equal(followup.Id.ToString(), usage.WorkItemId);
        Assert.Equal(recheckRow.EndedAt, usage.TimeUtc);
        Assert.Equal("cursor", usage.AgentKind);
        Assert.Null(usage.ModelId);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
    }

    [Fact]
    public async Task SuccessfulRun_EmitsTokenAndCostCounters()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("cost-otel.txt", "cost\n"));

        using var metrics = new MetricCapture("codeybox.agent.tokens", "codeybox.agent.cost_usd");

        var item = NewItem("feature/cost-otel");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Counters are emitted from the same cost-capture path that writes the DB
        // rows (one emit per row, model tagged from the extractor snapshot).
        Assert.True(metrics.Any("codeybox.agent.tokens",
            ("agent.kind", "claude"), ("model", "fake-model"), ("token_type", "input")));
        Assert.True(metrics.Any("codeybox.agent.tokens",
            ("agent.kind", "claude"), ("token_type", "output")));
        Assert.Contains(metrics.Items, m => m.Instrument == "codeybox.agent.cost_usd"
            && m.Tags.Any(t => t.Key == "agent.kind" && t.Value?.ToString() == "claude"));
    }

    [Fact]
    public async Task UsageStoreFailure_DoesNotAbortPipeline()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore, usageStore: new ThrowingUsageStore());

        tp.Agent.WorkPlan.Enqueue(new FileWrite("usage-fail-soft.txt", "x\n"));

        var item = NewItem("feature/usage-fail-soft");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Cost capture still succeeded even though usage persistence threw.
        Assert.NotEmpty(costStore.Recorded);
    }

    [Fact]
    public void BuildUsageEvent_ClampsNegativeTokensAndCost_ToZero()
    {
        // Hostile/malformed extractor output: negative tokens and a negative USD.
        // If the Math.Max clamps were removed, these would persist as negative
        // values, deflate the budget window SUM, and fail-open the spend cap.
        var snapshot = new AgentCostSnapshot(
            InputTokens: -1000, CachedInputTokens: -50, OutputTokens: -999_999_999, ModelId: "ignored");
        var ev = PipelineRunner.BuildUsageEvent(
            AgentKind.Opencode, "opencode-go/deepseek-v4-pro", snapshot,
            usd: -42.5m, new WorkItemId(Guid.NewGuid()), DateTimeOffset.UtcNow);

        Assert.Equal(0, ev.InputTokens);
        Assert.Equal(0, ev.CachedInputTokens);
        Assert.Equal(0, ev.OutputTokens);
        Assert.Equal(0L, ev.CostMicroCents);
    }

    [Fact]
    public void BuildUsageEvent_ClampsNegativeElapsed_ToZero()
    {
        var ended = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        var started = ended.AddSeconds(5);
        var snapshot = new AgentCostSnapshot(
            InputTokens: 0, CachedInputTokens: 0, OutputTokens: 0, ModelId: null);

        var ev = PipelineRunner.BuildUsageEvent(
            AgentKind.Cursor, "cursor-model", snapshot,
            usd: 0m, new WorkItemId(Guid.NewGuid()), ended, phase: "work", startedAt: started);

        Assert.Equal(0, ev.ElapsedMs);
    }

    [Fact]
    public void BuildUsageEvent_PreservesNonNegativeValues()
    {
        var snapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 100, OutputTokens: 200, ModelId: "parsed-model");
        var ev = PipelineRunner.BuildUsageEvent(
            AgentKind.Opencode, "dispatch-model", snapshot,
            usd: 1.5m, new WorkItemId(Guid.NewGuid()), DateTimeOffset.UtcNow);

        Assert.Equal(1000, ev.InputTokens);
        Assert.Equal(100, ev.CachedInputTokens);
        Assert.Equal(200, ev.OutputTokens);
        Assert.Equal(AgentUsageEvent.UsdToMicroCents(1.5m), ev.CostMicroCents);
    }

    [Fact]
    public void BuildUsageEvent_KeysByDispatchModel_NotParsedSnapshotModel()
    {
        // The budget gate sums spend on member.ModelId == the dispatched model id.
        // Usage rows MUST be keyed by that dispatch model, never the model parsed
        // from agent output, or spend lands in a different/NULL bucket than the one
        // being gated (fail-open on the cap).
        var snapshot = new AgentCostSnapshot(
            InputTokens: 10, CachedInputTokens: 0, OutputTokens: 10, ModelId: "provider-supplied-model");
        var ev = PipelineRunner.BuildUsageEvent(
            AgentKind.Opencode, "opencode-go/deepseek-v4-pro", snapshot,
            usd: 0.1m, new WorkItemId(Guid.NewGuid()), DateTimeOffset.UtcNow);

        Assert.Equal("opencode-go/deepseek-v4-pro", ev.ModelId);
    }

    [Fact]
    public void BuildUsageEvent_NullDispatchModel_PersistsNull_NotParsedModel()
    {
        // A footer that emits no model id (snapshot.ModelId set, dispatch null):
        // the row still keys on the dispatch bucket (null = default-model bucket),
        // matching how the gate queries when member.ModelId is unset.
        var snapshot = new AgentCostSnapshot(
            InputTokens: 10, CachedInputTokens: 0, OutputTokens: 10, ModelId: "some-parsed-model");
        var ev = PipelineRunner.BuildUsageEvent(
            AgentKind.Claude, dispatchModelId: null, snapshot,
            usd: 0.1m, new WorkItemId(Guid.NewGuid()), DateTimeOffset.UtcNow);

        Assert.Null(ev.ModelId);
    }

    [Fact]
    public void ResolveAuditUsageModelId_SameKind_KeepsContextModel()
    {
        // PostProcessAuditorRunAsync records audit spend under the model the
        // auditor actually dispatched on. ExecAuditorAsync keeps ctx.ModelId for a
        // same-kind auditor, so usage must bucket on ctx.ModelId — the same window
        // EvaluateAuditCandidateQuotaAsync gates. Returning null here would
        // understate the gated window and fail-open its cap.
        var auditRunner = new ClaudeAgentRunner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["claude"] = "claude-opus-4-8" }));
        var resolved = PipelineRunner.ResolveAuditUsageModelId(
            auditRunner, workRunnerKind: AgentKind.Claude, ctxModelId: "claude-opus-4-7");

        Assert.Equal("claude-opus-4-7", resolved);
    }

    [Fact]
    public void ResolveAuditUsageModelId_CrossKind_FallsBackToRunnerDefault()
    {
        // Cross-review (audit runner kind != work runner kind): ExecAuditorAsync
        // drops the vendor-specific work model (ModelId = null), so usage must
        // bucket on the audit runner's DefaultModelId — never the work item's
        // model, which the audit runner never dispatched on.
        var auditRunner = new CodexAgentRunner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["codex"] = "gpt-5.5" }));
        var resolved = PipelineRunner.ResolveAuditUsageModelId(
            auditRunner, workRunnerKind: AgentKind.Claude, ctxModelId: "claude-opus-4-7");

        Assert.Equal("gpt-5.5", resolved);
    }

    [Fact]
    public void ResolveAuditUsageModelId_SameKind_NullContextModel_FallsBackToRunnerDefault()
    {
        // Same-kind with no explicit work model: ResolveObservedModelId falls
        // through to the runner default rather than null, so spend still lands in
        // a concrete bucket instead of the NULL/default bucket by accident.
        var auditRunner = new ClaudeAgentRunner(new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["claude"] = "claude-opus-4-8" }));
        var resolved = PipelineRunner.ResolveAuditUsageModelId(
            auditRunner, workRunnerKind: AgentKind.Claude, ctxModelId: null);

        Assert.Equal("claude-opus-4-8", resolved);
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Cost capture test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    private static WorkItem NewCheckAndActItem(string branch) => NewItem(branch) with
    {
        Title = "Check cost capture test",
        Prompt = "evaluate the repo",
        BaseBranch = "main",
        PushUpstream = false,
        JobType = JobType.CheckAndAct,
        Check = new CheckAndActSpec
        {
            Question = "Is this repository still actionable?",
            ActionableAnswer = true,
            OnYes = new OnYesActionSpec
            {
                Title = "Remediate actionable check",
                Prompt = "make the check non-actionable",
            },
        },
    };

    private static string BuildVerdictStdout(bool answer, string evidence, string confidence = "high")
    {
        var ans = answer ? "true" : "false";
        return $"preamble\n{CheckAndActPipeline.StartSentinel}\n{{\"answer\": {ans}, \"evidence\": \"{evidence}\", \"confidence\": \"{confidence}\"}}\n{CheckAndActPipeline.EndSentinel}\n";
    }

    private static AgentPricingOptions CodexGpt55Pricing() => new()
    {
        Rates = new()
        {
            ["codex"] = new()
            {
                ["gpt-5.5"] = new()
                {
                    InputPerMillion = 5.0,
                    CachedInputPerMillion = 0.5,
                    OutputPerMillion = 30.0,
                },
            },
        },
    };

    public static TheoryData<string> BuiltInAgentKinds() => new()
    {
        AgentKind.Claude.Value,
        AgentKind.Codex.Value,
        AgentKind.Gemini.Value,
        AgentKind.Cursor.Value,
        AgentKind.Opencode.Value,
        AgentKind.Copilot.Value,
    };

    [Fact]
    public async Task LlmAuditor_ProducesAuditPhaseCostRow()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new FakeLlmAuditor()], maxAuditIterations: 1);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("llm-audit.txt", "a\n"));

        var item = NewItem("feature/llm-audit");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        var auditRows = costStore.Recorded.Where(r => r.Phase == "audit").ToList();
        Assert.Single(auditRows);
    }

    [Fact]
    public async Task LlmAuditor_ProducesAuditPhaseUsageEvent_KeyedByDispatchModel()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var costStore = new RecordingCostStore();
        var usageStore = new RecordingUsageStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, costStore,
            auditors: [new FakeLlmAuditor()], maxAuditIterations: 1, usageStore: usageStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("llm-audit-usage.txt", "a\n"));

        // Explicit work model so the audit usage row's dispatch bucket is a concrete,
        // assertable value: a same-kind auditor keeps ctx.ModelId per
        // ResolveAuditUsageModelId, so the audit usage event must key on it — not the
        // parsed snapshot model ("fake-model"), not null.
        var item = NewItem("feature/llm-audit-usage") with { ModelId = "claude-opus-4-7" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The audit phase records both a cost row and a usage event: a regression
        // that injects no usage store, skips RecordAsync on the audit path, or
        // buckets the audit usage under the wrong model would fail one of these.
        var auditRow = Assert.Single(costStore.Recorded, r => r.Phase == "audit");

        // One usage event per completed invocation, including the audit run.
        Assert.Equal(costStore.Recorded.Count, usageStore.Recorded.Count);

        // Correlate the audit usage event to its cost row by EndedAt → TimeUtc, then
        // assert it is bucketed under the dispatched audit model so it lands in the
        // same window EvaluateAuditCandidateQuotaAsync gates on.
        var auditUsage = Assert.Single(
            usageStore.Recorded, e => e.TimeUtc == auditRow.EndedAt);
        Assert.Equal("claude", auditUsage.AgentKind);
        Assert.Equal("claude-opus-4-7", auditUsage.ModelId);
        Assert.Equal("fake-model", auditRow.ModelId);
        Assert.Equal(
            AgentUsageEvent.UsdToMicroCents((decimal)auditRow.EstimatedUsd),
            auditUsage.CostMicroCents);
        Assert.Equal(item.Id.ToString(), auditUsage.WorkItemId);
    }

    [Fact]
    public async Task CostStoreFailure_DoesNotAbortPipeline()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var throwingStore = new ThrowingCostStore();
        using var tp = BuildPipelineWithCosts(_workspace, seed, throwingStore);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("fail-soft.txt", "x\n"));

        var item = NewItem("feature/fail-soft");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TestPipeline BuildPipelineWithCosts(
        string workspace,
        string seedRepoUrl,
        IWorkItemCostStore costStore,
        bool registerExtractor = true,
        IReadOnlyList<IAuditor>? auditors = null,
        int maxAuditIterations = 1,
        IAgentUsageStore? usageStore = null,
        AgentKind? agentKind = null,
        bool extractorReturnsNull = false,
        bool extractorThrows = false,
        AgentCostSnapshot? extractorSnapshot = null,
        AgentPricingOptions? pricingOptions = null,
        bool calculatorDefaultPricingThrows = false,
        ICheckAndActCompletionRunner? checkCompletionRunner = null)
    {
        var resolvedAgentKind = agentKind ?? AgentKind.Claude;
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]) { Kind = resolvedAgentKind };
        var registry = new AgentRegistry([agent]);

        var auditorList = auditors ?? [];
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = resolvedAgentKind,
            Audit = new ProjectAudit { MaxIterations = maxAuditIterations, AuditTypes = auditTypes },
        });

        var effectiveAuditors = auditorList.Any(RequiresBuildTestGate)
            ? TestAuditGates.WithPassedBuildAndTest(auditorList)
            : auditorList;
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(effectiveAuditors));
        var upstreamFactory = new TestUpstreamFactory();
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        IReadOnlyDictionary<AgentKind, IAgentCostExtractor>? extractors = null;
        if (registerExtractor)
        {
            var fake = new FakeCostExtractor
            {
                Kind = resolvedAgentKind,
                ReturnNull = extractorReturnsNull,
                Throw = extractorThrows,
                Snapshot = extractorSnapshot,
                DefaultPricingThrows = calculatorDefaultPricingThrows,
            };
            extractors = new Dictionary<AgentKind, IAgentCostExtractor> { [resolvedAgentKind] = fake };
        }
        var calculator = new AgentCostCalculator(pricingOptions ?? new AgentPricingOptions(), extractors);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            timingStore: null,
            costStore: costStore,
            costExtractors: extractors,
            costCalculator: calculator,
            usageStore: usageStore,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            checkCompletionRunner: checkCompletionRunner,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
    }

    private static bool RequiresBuildTestGate(IAuditor auditor)
        => string.Equals(auditor.Kind, "llm", StringComparison.OrdinalIgnoreCase)
            || (auditor.Required & AuditCapabilities.AgentCredentials) != 0;

    // ── Scripted auditors ─────────────────────────────────────────────────────

    private sealed class PassingToolAuditor : IAuditor
    {
        public string Name => "passing-tool";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, []));
    }

    /// <summary>
    /// Tool auditor that advances <c>main</c>'s copy of <paramref name="path"/>
    /// during the audit phase, so a work branch that wrote the same file merges
    /// with a conflict — forcing the merge phase down the agentic conflict
    /// resolver path (which runs an agent and emits a merge cost row) instead of
    /// the agent-less host-side clean merge.
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

    private sealed class OnceFailingAuditor : IAuditor
    {
        private int _callCount;
        public string Name => "once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
        {
            _callCount++;
            if (_callCount == 1)
                return Task.FromResult(new AuditResult(false, [
                    new AuditFinding("once-failing", AuditSeverity.Error, "Test failure", "First audit always fails"),
                ]));
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    // ── Fake extractor ────────────────────────────────────────────────────────

    private sealed class FakeCostExtractor : IAgentCostExtractor
    {
        public AgentKind Kind { get; init; }
        public bool ReturnNull { get; init; }
        public bool Throw { get; init; }
        public bool DefaultPricingThrows { get; init; }
        public AgentCostSnapshot? Snapshot { get; init; }

        public AgentCostSnapshot? TryExtract(string? stdout, string? stderr)
        {
            if (Throw)
                throw new InvalidOperationException("injected extractor failure");
            if (ReturnNull)
                return null;
            return Snapshot ?? new AgentCostSnapshot(
                InputTokens: 1000,
                CachedInputTokens: 100,
                OutputTokens: 200,
                ModelId: "fake-model");
        }

        public ModelRateConfig? DefaultPricing => DefaultPricingThrows
            ? throw new InvalidOperationException("injected calculator pricing failure")
            : null;
    }

    // ── Recording cost store ──────────────────────────────────────────────────

    internal sealed class RecordingCostStore : IWorkItemCostStore
    {
        private readonly List<WorkItemCost> _recorded = [];

        public IReadOnlyList<WorkItemCost> Recorded
        {
            get { lock (_recorded) return [.. _recorded]; }
        }

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
        {
            lock (_recorded) _recorded.Add(cost);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>(
                Recorded.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, double)>>([]);

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult(0m);
    }

    // ── Recording / throwing usage stores ─────────────────────────────────────

    internal sealed class RecordingUsageStore : IAgentUsageStore
    {
        private readonly List<AgentUsageEvent> _recorded = [];

        public IReadOnlyList<AgentUsageEvent> Recorded
        {
            get { lock (_recorded) return [.. _recorded]; }
        }

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default)
        {
            lock (_recorded) _recorded.Add(usage);
            return Task.CompletedTask;
        }

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult(new AgentUsageWindowAggregate(0, null, 0));

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ThrowingUsageStore : IAgentUsageStore
    {
        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default)
            => throw new InvalidOperationException("injected usage store failure");

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult(new AgentUsageWindowAggregate(0, null, 0));

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ScriptedCompletionRunner : ICheckAndActCompletionRunner
    {
        private readonly Queue<CheckAndActCompletionResult?> _results;

        public ScriptedCompletionRunner(params CheckAndActCompletionResult?[] results)
            => _results = new Queue<CheckAndActCompletionResult?>(results);

        public List<CheckAndActCompletionRequest> Requests { get; } = [];

        public Task<CheckAndActCompletionResult?> TryCompleteAsync(
            CheckAndActCompletionRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            if (_results.Count == 0)
                throw new InvalidOperationException("ScriptedCompletionRunner: ran out of results");
            return Task.FromResult(_results.Dequeue());
        }
    }

    // ── Fake LLM auditor (needsCreds=true path) ───────────────────────────────

    private sealed class FakeLlmAuditor : IAuditor
    {
        public string Name => "fake-llm";
        public string Kind => "llm";
        public AuditCapabilities Required => AuditCapabilities.AgentCredentials;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, [], RawOutput: "fake llm output"));
    }

    // ── Throwing cost store (fail-soft test) ──────────────────────────────────

    private sealed class ThrowingCostStore : IWorkItemCostStore
    {
        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default)
            => throw new InvalidOperationException("injected cost store failure");

        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, double)>>([]);

        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<decimal> SumEstimatedUsdAsync(
            string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => Task.FromResult(0m);
    }
}
