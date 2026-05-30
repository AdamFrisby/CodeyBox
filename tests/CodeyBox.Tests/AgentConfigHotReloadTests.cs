using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Opencode;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies hot-reload of <c>CodeyBox:AgentConcurrency</c>,
/// <c>CodeyBox:AgentClasses</c>, <c>CodeyBox:AgentBurnEstimator</c>, and
/// <c>CodeyBox:AgentPricing</c>: edits to these blocks of the layered config
/// land in the running router / orchestrator / burn estimator / cost
/// calculator without a restart, and in-flight items already past the
/// dispatch gate keep the snapshot they started on.
/// </summary>
public sealed class AgentConfigHotReloadTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    [Fact]
    public void Constructor_WithCalculatorButWithoutPricingState_Throws()
    {
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(new CodeyBoxOptions());
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var calculator = new AgentCostCalculator(new AgentPricingOptions());

        var ex = Assert.Throws<ArgumentException>(() =>
            new AgentConfigHotReload(
                monitor, orchFixture.Orchestrator, router, burnEstimator,
                NullLogger<AgentConfigHotReload>.Instance,
                costCalculator: calculator,
                pricingState: null));

        Assert.Contains("AgentPricingState", ex.Message);
        Assert.Equal("pricingState", ex.ParamName);
    }

    // ── AgentClassRouter.ApplyConfigReload ──────────────────────────────────

    [Fact]
    public async Task Router_ApplyConfigReload_SwapsCatalog_NewClassResolvable()
    {
        var initial = MakeClass("frontier", Claude);
        var router = new AgentClassRouter(
            new[] { initial },
            new[] { (IAgentQuotaProbe)new FakeProbe(Claude, 80.0), new FakeProbe(Codex, 80.0) },
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);

        Assert.Contains("frontier", router.ClassIds);
        Assert.DoesNotContain("bulk", router.ClassIds);

        var bulk = MakeClass("bulk", Codex);
        router.ApplyConfigReload(new[] { bulk }, Array.Empty<ParsedTodModifier>());

        Assert.DoesNotContain("frontier", router.ClassIds);
        Assert.Contains("bulk", router.ClassIds);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "bulk",
        };
        var decision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Router_ApplyConfigReload_SwapsTodModifiers_AffectsSubsequentScoring()
    {
        // Two members tied on QualityScore=100; the TOD modifier breaks the tie.
        // Initially Claude has +3 — Claude is picked. After reload swaps the
        // modifier to +3 on Codex, Codex is picked.
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        var router = new AgentClassRouter(
            new[] { cls },
            new[] { (IAgentQuotaProbe)new FakeProbe(Claude, 80.0), new FakeProbe(Codex, 80.0) },
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance,
            todModifiers: new[]
            {
                new ParsedTodModifier(Claude, 3, new[] { AllHoursWindow() }),
            });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };
        var before = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Claude, before.Chosen!.Agent);

        router.ApplyConfigReload(
            new[] { cls },
            new[] { new ParsedTodModifier(Codex, 3, new[] { AllHoursWindow() }) });

        var after = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Codex, after.Chosen!.Agent);
    }

    // ── OrchestratorService.ApplyAgentConcurrencyReload ─────────────────────

    [Fact]
    public void Orchestrator_ApplyAgentConcurrencyReload_SwapsCaps_VisibleInState()
    {
        using var fixture = OrchestratorFixture.Build(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        });
        var before = fixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(1, before.PerAgentCaps["claude"]);
        Assert.DoesNotContain("codex", before.PerAgentCaps);

        fixture.Orchestrator.ApplyAgentConcurrencyReload(new AgentConcurrencyOptions
        {
            Members =
            {
                ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 3 },
                ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 2 },
            },
        });

        var after = fixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(3, after.PerAgentCaps["claude"]);
        Assert.Equal(2, after.PerAgentCaps["codex"]);
    }

    [Fact]
    public void Orchestrator_ApplyAgentConcurrencyReload_DoesNotRetroactivelyKillInFlight()
    {
        // Reserve a claude slot under the initial cap=1, then reload to remove
        // the entry entirely (the supported way to express "uncapped") mid-flight.
        // The already-reserved running count must remain — the new cap only
        // gates *new* dispatches; lowering or removing it must not snap the
        // running call.
        using var fixture = OrchestratorFixture.Build(new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        });
        Assert.True(fixture.Orchestrator.TryReserveAgentSlotForTest(Claude));
        Assert.Equal(1, fixture.Orchestrator.GetRunning(Claude));

        fixture.Orchestrator.ApplyAgentConcurrencyReload(new AgentConcurrencyOptions());

        // The reservation already past the gate keeps its slot.
        Assert.Equal(1, fixture.Orchestrator.GetRunning(Claude));

        // The contract here is that removing the cap does not yank in-flight work.
        fixture.Orchestrator.ReleaseAgentSlotForTest(Claude);
        Assert.Equal(0, fixture.Orchestrator.GetRunning(Claude));
    }

    // ── AgentBurnEstimator.ApplyConfigReload ────────────────────────────────

    [Fact]
    public async Task BurnEstimator_ApplyConfigReload_SwapsDefaults_AndClearsCache()
    {
        var costs = new InertCostStore();
        var initial = new AgentBurnEstimatorOptions
        {
            DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 4.0 },
        };
        var estimator = new AgentBurnEstimator(
            costs, initial, NullLogger<AgentBurnEstimator>.Instance);

        var first = await estimator.GetEstimateAsync(Claude);
        Assert.Equal(4.0, first.AvgBurnPctPerItem);
        Assert.Equal(0, first.SampleCount);

        // Swap to a different default. Without ApplyConfigReload clearing the
        // in-process cache, the next read would serve the stale 4.0 for up to
        // CacheTtl.
        var updated = new AgentBurnEstimatorOptions
        {
            DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 25.0 },
        };
        estimator.ApplyConfigReload(updated);

        var second = await estimator.GetEstimateAsync(Claude);
        Assert.Equal(25.0, second.AvgBurnPctPerItem);
    }

    // ── AgentConfigHotReload coordinator ────────────────────────────────────

    [Fact]
    public async Task Coordinator_OnChange_PushesUpdatesIntoEachConsumer()
    {
        var initial = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
            },
            AgentBurnEstimator = new AgentBurnEstimatorOptions
            {
                DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 4.0 },
            },
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "frontier",
                    Members =
                    [
                        new AgentMembershipOptions
                        {
                            Agent = "claude",
                            Billing = "Subscription",
                            QualityScore = 100,
                        },
                    ],
                },
            ],
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);

        // Build downstream consumers from the same initial snapshot so the
        // coordinator's StartAsync captures an already-applied baseline.
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        // Fire OnChange with a new value that mutates all three blocks.
        var updated = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members =
                {
                    ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 3 },
                    ["codex"]  = new AgentConcurrencyEntry { MaxConcurrent = 1 },
                },
            },
            AgentBurnEstimator = new AgentBurnEstimatorOptions
            {
                DefaultBurnPercentPerItem = new(StringComparer.OrdinalIgnoreCase) { ["claude"] = 7.5 },
            },
            AgentClasses =
            [
                new AgentClassOptions
                {
                    Id = "bulk",
                    Members =
                    [
                        new AgentMembershipOptions
                        {
                            Agent = "codex",
                            Billing = "Subscription",
                            QualityScore = 100,
                        },
                    ],
                },
            ],
        };
        monitor.Fire(updated);

        // Concurrency caps reflect the new value.
        var concurrencyState = orchFixture.Orchestrator.GetConcurrencyState();
        Assert.Equal(3, concurrencyState.PerAgentCaps["claude"]);
        Assert.Equal(1, concurrencyState.PerAgentCaps["codex"]);

        // Router catalog reflects the new value.
        Assert.Contains("bulk", router.ClassIds);
        Assert.DoesNotContain("frontier", router.ClassIds);

        // Burn estimator default reflects the new value.
        var est = await burnEstimator.GetEstimateAsync(Claude);
        Assert.Equal(7.5, est.AvgBurnPctPerItem);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_InvalidAgentClassesPayload_KeepsPriorSnapshot_AndAllowsFollowupValidEdit()
    {
        // Acceptance criterion: "a bad edit can't break a running orchestrator"
        // (AgentClassesConfigBuilder.Build summary). When AgentClasses fails
        // validation mid-reload, the coordinator must keep the prior router
        // catalog AND keep its _lastRouter baseline at the pre-edit value so a
        // later valid edit is still detected as a change. Otherwise an
        // operator who fixes the bad edit would see the second OnChange
        // silently skipped because _lastRouter had been advanced to the
        // rejected serialised form.
        var validInitial = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                [
                    new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 },
                ],
            },
        };
        var initial = new CodeyBoxOptions { AgentClasses = validInitial };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(validInitial, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        // Fire OnChange with an invalid payload: Gemini at QualityScore=95
        // without ReasoningMode="high" is explicitly rejected by the builder.
        var invalid = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new()
                {
                    Id = "frontier",
                    Members =
                    [
                        new() { Agent = "gemini", Billing = "Subscription", QualityScore = 95 },
                    ],
                },
            ],
        };
        monitor.Fire(invalid);

        // Router catalog must NOT have been touched.
        Assert.Contains("frontier", router.ClassIds);
        Assert.Single(router.ClassIds);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };
        var decision = await router.ResolveAsync(item, project: null, CancellationToken.None);
        Assert.Equal(Claude, decision.Chosen!.Agent);

        // Now fire a follow-up valid edit. If _lastRouter had been advanced to
        // the rejected serialised form, this would be detected as no-change
        // against the rejected payload and the new "bulk" class would never
        // make it into the router.
        var validFollowup = new CodeyBoxOptions
        {
            AgentClasses =
            [
                new()
                {
                    Id = "bulk",
                    Members =
                    [
                        new() { Agent = "codex", Billing = "Subscription", QualityScore = 100 },
                    ],
                },
            ],
        };
        monitor.Fire(validFollowup);

        Assert.DoesNotContain("frontier", router.ClassIds);
        Assert.Contains("bulk", router.ClassIds);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_PushesPricingUpdateIntoCostCalculator()
    {
        // End-to-end: edits to CodeyBox:AgentPricing flow through OnChange ->
        // ApplyPricingIfChanged -> AgentCostCalculator.ApplyConfigReload and
        // subsequent Calculate() calls reflect the new rates. Catches:
        //   - costCalculator parameter not wired in DI / ctor (silently no-ops)
        //   - SerializePricing returning a constant (changes never detected)
        //   - ApplyPricingIfChanged passing the wrong section to the calculator
        var initialPricing = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 15.0,
                        CachedInputPerMillion = 1.50,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        var initial = new CodeyBoxOptions { AgentPricing = initialPricing };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var calculator = new AgentCostCalculator(initialPricing);
        var snapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-opus-4-7");
        Assert.Equal(0.090000m, calculator.Calculate(snapshot, Claude));

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var emptyBaseline = new AgentPricingOptions();
        var pricingState = new AgentPricingState(
            new AgentPricingDefaultsSnapshot { Baseline = emptyBaseline },
            AgentPricingMerge.Merge(emptyBaseline, initialPricing));
        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            costCalculator: calculator,
            pricingState: pricingState);
        await coordinator.StartAsync(CancellationToken.None);

        // Fire OnChange with doubled pricing.
        monitor.Fire(new CodeyBoxOptions
        {
            AgentPricing = new AgentPricingOptions
            {
                Rates = new()
                {
                    ["claude"] = new()
                    {
                        ["claude-opus-4-7"] = new ModelRateConfig
                        {
                            InputPerMillion = 30.0,
                            CachedInputPerMillion = 3.00,
                            OutputPerMillion = 150.0,
                        },
                    },
                },
            },
        });

        // Calculator must see the doubled rate via the live calculator instance.
        Assert.Equal(0.180000m, calculator.Calculate(snapshot, Claude));
        Assert.Equal(1, pricingState.LastMerge.OperatorRateCount);
        Assert.Equal(30.0, pricingState.LastMerge.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_PricingHotReload_RemergesAgainstBundledDefaults()
    {
        // Regression for the bundledPricing branch of ApplyPricingIfChanged:
        // a hot edit to operator AgentPricing must be merged with the bundled
        // defaults (operator wins per (agent, model); bundled rates for keys
        // the operator didn't touch are preserved). Without the merge, the
        // calculator would lose the bundled rate for any non-overridden pair
        // the moment the operator edited any other entry. Plausible bugs not
        // caught without this test:
        //   - operator overwrites bundled instead of merging (bundled rate
        //     for an un-overridden model vanishes after a hot-reload)
        //   - merge order reversed so bundled silently wins over operator
        //   - bundledPricing parameter never plumbed; merge branch never runs
        var bundledBaseline = new AgentPricingOptions
        {
            Rates = new(StringComparer.Ordinal)
            {
                ["claude"] = new(StringComparer.Ordinal)
                {
                    ["claude-haiku-4-5"] = new ModelRateConfig
                    {
                        InputPerMillion = 1.0,
                        CachedInputPerMillion = 0.10,
                        OutputPerMillion = 5.0,
                    },
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 5.0,
                        CachedInputPerMillion = 0.50,
                        OutputPerMillion = 25.0,
                    },
                },
            },
        };
        var initialOperator = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 15.0,
                        CachedInputPerMillion = 1.50,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        var initialMerged = AgentPricingMerge.Merge(bundledBaseline, initialOperator);
        var pricingState = new AgentPricingState(
            new AgentPricingDefaultsSnapshot { Baseline = bundledBaseline },
            initialMerged);
        var initial = new CodeyBoxOptions { AgentPricing = initialOperator };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var calculator = new AgentCostCalculator(initialMerged.Options);

        var opusSnapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-opus-4-7");
        var haikuSnapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-haiku-4-5");

        Assert.Equal(0.090000m, calculator.Calculate(opusSnapshot, Claude));
        Assert.Equal(0.006000m, calculator.Calculate(haikuSnapshot, Claude));

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            costCalculator: calculator,
            pricingState: pricingState);
        await coordinator.StartAsync(CancellationToken.None);

        // Hot-reload: operator doubles its own opus override. The bundled
        // haiku rate must NOT disappear — Merge has to re-apply bundled rates
        // for keys the operator didn't override.
        monitor.Fire(new CodeyBoxOptions
        {
            AgentPricing = new AgentPricingOptions
            {
                Rates = new()
                {
                    ["claude"] = new()
                    {
                        ["claude-opus-4-7"] = new ModelRateConfig
                        {
                            InputPerMillion = 30.0,
                            CachedInputPerMillion = 3.00,
                            OutputPerMillion = 150.0,
                        },
                    },
                },
            },
        });

        // Opus reflects the doubled operator rate.
        Assert.Equal(0.180000m, calculator.Calculate(opusSnapshot, Claude));
        // Haiku still gets the bundled rate — the merge branch ran instead of
        // overwriting the calculator with operator-only config.
        Assert.Equal(0.006000m, calculator.Calculate(haikuSnapshot, Claude));
        Assert.Equal(2, pricingState.LastMerge.BundledRateCount);
        Assert.Equal(1, pricingState.LastMerge.OperatorRateCount);
        Assert.Equal(1, pricingState.LastMerge.OverlapCount);
        Assert.Equal(2, pricingState.LastMerge.TotalRateCount);
        Assert.Equal(30.0, pricingState.LastMerge.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);
        Assert.Equal(1.0, pricingState.LastMerge.Options.Rates["claude"]["claude-haiku-4-5"].InputPerMillion);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_InvalidAgentPricingPayload_KeepsPriorSnapshot_AndAllowsFollowupValidEdit()
    {
        // Mirror of the AgentClasses follow-up-valid-edit regression test: if
        // _lastPricing were advanced inside the catch, the operator's
        // follow-up fix would be detected as no-change against the rejected
        // payload and silently skipped.
        var initialPricing = new AgentPricingOptions
        {
            Rates = new()
            {
                ["claude"] = new()
                {
                    ["claude-opus-4-7"] = new ModelRateConfig
                    {
                        InputPerMillion = 15.0,
                        CachedInputPerMillion = 1.50,
                        OutputPerMillion = 75.0,
                    },
                },
            },
        };
        var bundledBaseline = new AgentPricingOptions
        {
            Rates = new(StringComparer.Ordinal)
            {
                ["claude"] = new(StringComparer.Ordinal)
                {
                    ["claude-haiku-4-5"] = new ModelRateConfig
                    {
                        InputPerMillion = 1.0,
                        CachedInputPerMillion = 0.10,
                        OutputPerMillion = 5.0,
                    },
                },
            },
        };
        var initialMerged = AgentPricingMerge.Merge(bundledBaseline, initialPricing);
        var pricingState = new AgentPricingState(
            new AgentPricingDefaultsSnapshot { Baseline = bundledBaseline },
            initialMerged);
        var initial = new CodeyBoxOptions { AgentPricing = initialPricing };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var calculator = new AgentCostCalculator(initialMerged.Options);
        var snapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-opus-4-7");
        var priorCost = calculator.Calculate(snapshot, Claude);
        var priorMerge = pricingState.LastMerge;

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            costCalculator: calculator,
            pricingState: pricingState);
        await coordinator.StartAsync(CancellationToken.None);

        // Invalid: negative rate is rejected by AgentCostCalculator.ApplyConfigReload.
        monitor.Fire(new CodeyBoxOptions
        {
            AgentPricing = new AgentPricingOptions
            {
                Rates = new()
                {
                    ["claude"] = new()
                    {
                        ["claude-opus-4-7"] = new ModelRateConfig
                        {
                            InputPerMillion = -1.0,
                            CachedInputPerMillion = 0,
                            OutputPerMillion = 75.0,
                        },
                    },
                },
            },
        });

        // Prior pricing snapshot still in effect after the rejected reload.
        Assert.Equal(priorCost, calculator.Calculate(snapshot, Claude));
        Assert.Equal(priorMerge.OperatorRateCount, pricingState.LastMerge.OperatorRateCount);
        Assert.Equal(priorMerge.TotalRateCount, pricingState.LastMerge.TotalRateCount);
        Assert.Equal(
            priorMerge.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion,
            pricingState.LastMerge.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);
        var haikuSnapshot = new AgentCostSnapshot(
            InputTokens: 1000, CachedInputTokens: 0, OutputTokens: 1000, ModelId: "claude-haiku-4-5");
        Assert.Equal(0.006000m, calculator.Calculate(haikuSnapshot, Claude));

        // Follow-up valid edit (doubled rate) must still be detected as a
        // change against the original baseline, not the rejected payload.
        monitor.Fire(new CodeyBoxOptions
        {
            AgentPricing = new AgentPricingOptions
            {
                Rates = new()
                {
                    ["claude"] = new()
                    {
                        ["claude-opus-4-7"] = new ModelRateConfig
                        {
                            InputPerMillion = 30.0,
                            CachedInputPerMillion = 3.00,
                            OutputPerMillion = 150.0,
                        },
                    },
                },
            },
        });

        Assert.Equal(0.180000m, calculator.Calculate(snapshot, Claude));
        Assert.Equal(30.0, pricingState.LastMerge.Options.Rates["claude"]["claude-opus-4-7"].InputPerMillion);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_HotReloadConcurrency_IsObservableByPipelineRunner()
    {
        // Regression: PipelineRunner.GetCapSafe (used by the pickup-time
        // rebase-resolver's cap-aware routing) used to capture the
        // AgentConcurrencyOptions instance at construction. Hot-reload then
        // only swapped OrchestratorService's reference and PipelineRunner kept
        // gating against the pre-reload caps until process restart. After
        // wiring both consumers through the shared AgentConcurrencySnapshot,
        // a reload here must be visible to PipelineRunner.IsAtAgentCap.
        var initialCaps = new AgentConcurrencyOptions
        {
            Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 5 } },
        };
        var sharedSnapshot = new AgentConcurrencySnapshot(initialCaps);

        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(new CodeyBoxOptions
        {
            AgentConcurrency = initialCaps,
        });
        using var orchFixture = OrchestratorFixture.BuildWithSnapshot(sharedSnapshot);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        // Sanity: the shared snapshot starts at the original caps.
        Assert.Equal(5, sharedSnapshot.Current.Members["claude"].MaxConcurrent);

        // Fire a reload that lowers the claude cap to 1.
        monitor.Fire(new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
            },
        });

        // The shared snapshot — observed by both OrchestratorService.GetAgentCap
        // and PipelineRunner.GetCapSafe — now reflects the new cap. Before the
        // fix, sharedSnapshot.Current would still hold the pre-reload reference
        // because OrchestratorService.ApplyAgentConcurrencyReload would have
        // swapped only its own field.
        Assert.Equal(1, sharedSnapshot.Current.Members["claude"].MaxConcurrent);
        Assert.Equal(1, orchFixture.Orchestrator.GetConcurrencyState().PerAgentCaps["claude"]);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_OnlyMutatedBlocksAreReapplied()
    {
        // Confirms that an edit which touches only one block does not also
        // reapply the others — verifies the coordinator's per-block JSON-diff
        // gate. Tests behaviour indirectly via observable state, since the
        // audit log is global Serilog state.
        var classes = new List<AgentClassOptions>
        {
            new()
            {
                Id = "frontier",
                Members =
                [
                    new() { Agent = "claude", Billing = "Subscription", QualityScore = 100 },
                ],
            },
        };
        var initial = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
            },
            AgentClasses = classes,
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(classes, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        // Only change AgentConcurrency. The router catalog and burn defaults
        // are byte-for-byte identical to the baseline.
        var updated = new CodeyBoxOptions
        {
            AgentConcurrency = new AgentConcurrencyOptions
            {
                Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 5 } },
            },
            AgentClasses = classes,
        };
        monitor.Fire(updated);

        Assert.Equal(5, orchFixture.Orchestrator.GetConcurrencyState().PerAgentCaps["claude"]);
        // Router catalog unchanged.
        Assert.Contains("frontier", router.ClassIds);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_PushesBudgetUpdateIntoCalculator()
    {
        // End-to-end: an edit to CodeyBox:AgentBudgets must reach the live
        // AgentBudgetCalculator (new limit applied, snapshot cache dropped)
        // without a restart. Spend is fixed at 100 cents; halving the limit from
        // 200c to 100c turns a 50%-remaining budget into a 0%-remaining one.
        var store = new FixedSumUsageStore(1_000_000); // 100 cents spent
        var initialBudgets = MakeBudgets(limitCents: 200);
        var calculator = new AgentBudgetCalculator(
            store, initialBudgets, NullLogger<AgentBudgetCalculator>.Instance);

        var initial = new CodeyBoxOptions { AgentBudgets = initialBudgets };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            budgetReloader: calculator);
        await coordinator.StartAsync(CancellationToken.None);

        var before = await calculator.GetBudgetSnapshotAsync(AgentKind.Opencode, "m1");
        Assert.Equal(50.0, before!.AvailablePct, precision: 6);

        monitor.Fire(new CodeyBoxOptions { AgentBudgets = MakeBudgets(limitCents: 100) });

        var after = await calculator.GetBudgetSnapshotAsync(AgentKind.Opencode, "m1");
        Assert.Equal(0.0, after!.AvailablePct, precision: 6);

        await coordinator.StopAsync(CancellationToken.None);
    }

    private static AgentBudgetOptions MakeBudgets(double limitCents)
    {
        var opts = new AgentBudgetOptions();
        opts.Members["opencode"] = new AgentBudgetMemberOptions
        {
            Models =
            {
                ["m1"] = new AgentBudgetModelOptions
                {
                    Windows = [new AgentBudgetWindowOptions { Kind = BudgetWindowKind.Rolling, Hours = 5, LimitCents = limitCents }],
                },
            },
        };
        return opts;
    }

    private sealed class FixedSumUsageStore : IAgentUsageStore
    {
        private readonly long _sum;
        public FixedSumUsageStore(long sumMicroCents) { _sum = sumMicroCents; }

        public Task RecordAsync(AgentUsageEvent usage, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AgentUsageWindowAggregate> SumWindowAsync(
            string agentKind, string? modelId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult(new AgentUsageWindowAggregate(_sum, null, _sum > 0 ? 1 : 0));

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) => Task.FromResult(0);
    }

    // ── AgentDefaults hot-reload ────────────────────────────────────────────

    [Fact]
    public async Task AgentDefaults_HotReload_ChangesModelFlagOnNextRun()
    {
        var initialDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "claude-opus-4-7",
        };
        var snapshot = new AgentDefaultsSnapshot(initialDefaults);
        var runner = new ClaudeAgentRunner(snapshot);

        var sandbox = new CapturingSandbox();
        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("claude-opus-4-7", argv[modelIdx + 1]);

        // Hot-reload: swap default model to a lighter variant.
        var updatedDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "claude-haiku-4-5",
        };
        snapshot.Replace(updatedDefaults);

        var sandbox2 = new CapturingSandbox();
        await runner.RunAsync(sandbox2, "/work", "prompt2", credential: null);

        var argv2 = sandbox2.CapturedExec!.Argv.ToList();
        var modelIdx2 = argv2.IndexOf("--model");
        Assert.True(modelIdx2 >= 0);
        Assert.Equal("claude-haiku-4-5", argv2[modelIdx2 + 1]);
    }

    [Fact]
    public async Task AgentDefaults_HotReload_ChangesModelFlagOnNextRun_Codex()
    {
        var initialDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = "gpt-5.5",
        };
        var snapshot = new AgentDefaultsSnapshot(initialDefaults);
        var runner = new CodexAgentRunner(snapshot);

        var sandbox = new CapturingSandbox();
        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("gpt-5.5", argv[modelIdx + 1]);

        var updatedDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = "gpt-4o-mini",
        };
        snapshot.Replace(updatedDefaults);

        var sandbox2 = new CapturingSandbox();
        await runner.RunAsync(sandbox2, "/work", "prompt2", credential: null);

        var argv2 = sandbox2.CapturedExec!.Argv.ToList();
        var modelIdx2 = argv2.IndexOf("--model");
        Assert.True(modelIdx2 >= 0);
        Assert.Equal("gpt-4o-mini", argv2[modelIdx2 + 1]);
    }

    [Fact]
    public async Task AgentDefaults_HotReload_ChangesModelFlagOnNextRun_Cursor()
    {
        var initialDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cursor"] = "composer-2.5",
        };
        var snapshot = new AgentDefaultsSnapshot(initialDefaults);
        var runner = new CursorAgentRunner(snapshot);

        var sandbox = new CapturingSandbox();
        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("composer-2.5", argv[modelIdx + 1]);

        var updatedDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cursor"] = "composer-3-preview",
        };
        snapshot.Replace(updatedDefaults);

        var sandbox2 = new CapturingSandbox();
        await runner.RunAsync(sandbox2, "/work", "prompt2", credential: null);

        var argv2 = sandbox2.CapturedExec!.Argv.ToList();
        var modelIdx2 = argv2.IndexOf("--model");
        Assert.True(modelIdx2 >= 0);
        Assert.Equal("composer-3-preview", argv2[modelIdx2 + 1]);
    }

    [Fact]
    public async Task AgentDefaults_HotReload_ChangesModelFlagOnNextRun_Opencode()
    {
        var initialDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["opencode"] = "deepseek/deepseek-coder",
        };
        var snapshot = new AgentDefaultsSnapshot(initialDefaults);
        var runner = new OpencodeAgentRunner(snapshot);

        var sandbox = new CapturingSandbox();
        await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        var argv = sandbox.CapturedExec!.Argv.ToList();
        var modelIdx = argv.IndexOf("--model");
        Assert.True(modelIdx >= 0);
        Assert.Equal("deepseek/deepseek-coder", argv[modelIdx + 1]);

        var updatedDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["opencode"] = "anthropic/claude-sonnet-4-6",
        };
        snapshot.Replace(updatedDefaults);

        var sandbox2 = new CapturingSandbox();
        await runner.RunAsync(sandbox2, "/work", "prompt2", credential: null);

        var argv2 = sandbox2.CapturedExec!.Argv.ToList();
        var modelIdx2 = argv2.IndexOf("--model");
        Assert.True(modelIdx2 >= 0);
        Assert.Equal("anthropic/claude-sonnet-4-6", argv2[modelIdx2 + 1]);
    }

    [Fact]
    public async Task Coordinator_OnChange_AgentDefaultsPushesToSnapshot()
    {
        var initial = new CodeyBoxOptions
        {
            AgentDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude-opus-4-7",
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var snapshot = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(initial.AgentDefaults, initial.AgentDefaults.Comparer));

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            defaults: snapshot);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal("claude-opus-4-7", snapshot.GetDefault("claude"));

        monitor.Fire(new CodeyBoxOptions
        {
            AgentDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "claude-haiku-4-5",
            },
        });

        Assert.Equal("claude-haiku-4-5", snapshot.GetDefault("claude"));

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_IncrementalRebasePushesToSnapshot()
    {
        // End-to-end: an edit to CodeyBox:IncrementalRebase must reach the
        // live IncrementalRebaseSnapshot without a restart, so PipelineRunner
        // — which reads through the same snapshot reference — observes the
        // new Enabled value on the next between-iteration check. Catches
        // plausible coordinator bugs:
        //   - snapshot parameter not threaded through DI / ctor (silently no-ops)
        //   - SerializeIncrementalRebase emitting a constant (no change ever detected)
        //   - _lastIncrementalRebase not advanced (second valid edit silently skipped)
        var initial = new CodeyBoxOptions
        {
            IncrementalRebase = new IncrementalRebaseOptions { Enabled = false },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var snapshot = new IncrementalRebaseSnapshot(
            new IncrementalRebaseOptions { Enabled = initial.IncrementalRebase.Enabled });

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            incrementalRebase: snapshot);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.False(snapshot.Current.Enabled);

        // Flip the flag on via hot-reload.
        monitor.Fire(new CodeyBoxOptions
        {
            IncrementalRebase = new IncrementalRebaseOptions { Enabled = true },
        });
        Assert.True(snapshot.Current.Enabled);

        // Flip it back off — confirms _lastIncrementalRebase was advanced
        // after the first apply, so the second edit is detected as a change
        // rather than as a redundant repeat of the prior payload.
        monitor.Fire(new CodeyBoxOptions
        {
            IncrementalRebase = new IncrementalRebaseOptions { Enabled = false },
        });
        Assert.False(snapshot.Current.Enabled);

        await coordinator.StopAsync(CancellationToken.None);
    }

    // ── ClaudeThinkingBlockSanitizer hot-reload ──────────────────────────────

    [Fact]
    public async Task Coordinator_OnChange_SanitizerTogglePushesToConfig()
    {
        var initial = new CodeyBoxOptions
        {
            ClaudeThinkingBlockSanitizer = new ClaudeThinkingBlockSanitizerOptions { Enabled = true },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var sanitizerConfig = new ClaudeThinkingBlockSanitizerConfig { Enabled = initial.ClaudeThinkingBlockSanitizer.Enabled };

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            sanitizerConfig: sanitizerConfig);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.True(sanitizerConfig.Enabled);

        // Toggle disabled via config change.
        monitor.Fire(new CodeyBoxOptions
        {
            ClaudeThinkingBlockSanitizer = new ClaudeThinkingBlockSanitizerOptions { Enabled = false },
        });

        Assert.False(sanitizerConfig.Enabled);

        // Toggle back to enabled.
        monitor.Fire(new CodeyBoxOptions
        {
            ClaudeThinkingBlockSanitizer = new ClaudeThinkingBlockSanitizerOptions { Enabled = true },
        });

        Assert.True(sanitizerConfig.Enabled);

        // Same-value fire should be a no-op (no crash, no change).
        monitor.Fire(new CodeyBoxOptions
        {
            ClaudeThinkingBlockSanitizer = new ClaudeThinkingBlockSanitizerOptions { Enabled = true },
        });

        Assert.True(sanitizerConfig.Enabled);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_SanitizerNoOpWhenConfigNull()
    {
        // When no sanitizerConfig is registered, the coordinator should not
        // crash — ApplySanitizerIfChanged returns early.
        var initial = new CodeyBoxOptions
        {
            ClaudeThinkingBlockSanitizer = new ClaudeThinkingBlockSanitizerOptions { Enabled = true },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        // No sanitizerConfig — the coordinator handles null gracefully.
        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            sanitizerConfig: null);
        await coordinator.StartAsync(CancellationToken.None);

        // Fire a config change — must not throw.
        monitor.Fire(new CodeyBoxOptions
        {
            ClaudeThinkingBlockSanitizer = new ClaudeThinkingBlockSanitizerOptions { Enabled = false },
        });

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SanitizerConfig_Disabled_SuppressesReactiveRetryViaHotReload()
    {
        // Start with sanitizer enabled.
        var sanitizerConfig = new ClaudeThinkingBlockSanitizerConfig { Enabled = true };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, sanitizerConfig);

        // First run with enabled config — retry fires on thinking-block 400.
        var sandbox1 = new ThinkingBlockRetrySandbox(
            initialFailures: 1, sanitizerExitsZero: true);
        var result1 = await runner.RunAsync(sandbox1, "/work", "prompt", credential: null);
        Assert.True(result1.Success);
        Assert.True(sandbox1.AllExecs.Count(e => e.Argv.Count > 0 && e.Argv[0] == "claude") >= 2);

        // Hot-reload: disable the sanitizer via the shared config object.
        sanitizerConfig.Enabled = false;

        // Second run with disabled config — no retry.
        var sandbox2 = new ThinkingBlockRetrySandbox(
            initialFailures: 1, sanitizerExitsZero: true);
        var result2 = await runner.RunAsync(sandbox2, "/work", "prompt2", credential: null);
        Assert.False(result2.Success);
        Assert.Equal(1, sandbox2.AllExecs.Count(e => e.Argv.Count > 0 && e.Argv[0] == "claude"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentClass MakeClass(string id, AgentKind agent) => new()
    {
        Id = id,
        DisplayName = id,
        Members =
        [
            new AgentMembership { Agent = agent, Billing = AgentBilling.Subscription, QualityScore = 100 },
        ],
    };

    private static ParsedTimeWindow AllHoursWindow() => new(
        new HashSet<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        },
        TimeSpan.Zero,
        TimeSpan.FromHours(24));

    private sealed class OrchestratorFixture : IDisposable
    {
        public OrchestratorService Orchestrator { get; private init; } = null!;
        private SqliteWorkItemStore? _store;
        private string? _dbPath;

        public static OrchestratorFixture Build(AgentConcurrencyOptions concurrency)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"cb-hotreload-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var orch = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new NoopPipelineRunner(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxConcurrentWorkers = 4 },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrency: concurrency);
            return new OrchestratorFixture { Orchestrator = orch, _store = store, _dbPath = dbPath };
        }

        public static OrchestratorFixture BuildWithSnapshot(AgentConcurrencySnapshot snapshot)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"cb-hotreload-{Guid.NewGuid():N}.db");
            var store = new SqliteWorkItemStore(dbPath);
            var orch = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new NoopPipelineRunner(),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxConcurrentWorkers = 4 },
                NullLogger<OrchestratorService>.Instance,
                agentConcurrencySnapshot: snapshot);
            return new OrchestratorFixture { Orchestrator = orch, _store = store, _dbPath = dbPath };
        }

        public void Dispose()
        {
            _store?.Dispose();
            if (_dbPath is not null) { try { File.Delete(_dbPath); } catch { } }
        }
    }

    private sealed class NoopPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken phaseCt, CancellationToken hostCt) =>
            Task.CompletedTask;
    }

    private sealed class InertCostStore : IWorkItemCostStore, IRecentCostsByAgentQueryable
    {
        public Task<(long AvgTokens, int Samples)> GetAvgTokensPerItemAsync(
            string agentKind, int limit, CancellationToken ct = default) =>
            Task.FromResult<(long, int)>((0L, 0));

        public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());
        public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemCost>>(Array.Empty<WorkItemCost>());
        public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, double)>>(Array.Empty<(string, double)>());
        public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal> SumEstimatedUsdAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult(0m);
    }

    private sealed class ManualOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly Lock _gate = new();

        public ManualOptionsMonitor(T initial) { _value = initial; }

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }
}
