using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Opencode;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task SmokeHotReload_SwapsLiveFieldsButKeepsStartupCacheTtl()
    {
        var initial = new CodeyBoxOptions
        {
            Smoke = new SmokeConfig
            {
                Enabled = true,
                CacheTtlMinutes = 15,
                StartupTimeoutSeconds = 5,
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var smoke = new SmokeOptionsSnapshot(new SmokeOptions
        {
            Enabled = true,
            CacheTtlMinutes = 15,
            StartupTimeoutSeconds = 5,
        });

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            smokeOptions: smoke);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions
        {
            Smoke = new SmokeConfig
            {
                Enabled = false,
                CacheTtlMinutes = 7,
                StartupTimeoutSeconds = 3,
            },
        });

        Assert.False(smoke.Enabled);
        Assert.Equal(15, smoke.Current.CacheTtlMinutes);
        Assert.Equal(3, smoke.Current.StartupTimeoutSeconds);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AgentPausesHotReload_ReconcilesConfigOwnedPausesOnly()
    {
        var initial = new CodeyBoxOptions();
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-config-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            dbPath,
            NullLogger<SqliteAgentPauseController>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            pauses: pauses);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);

            monitor.Fire(new CodeyBoxOptions
            {
                AgentPauses = new Dictionary<string, AgentPauseConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = new() { Reason = "reserve quota" },
                },
            });

            var configPause = await pauses.GetAgentStateAsync(Claude);
            Assert.NotNull(configPause);
            Assert.Equal("reserve quota", configPause!.PausedReason);
            Assert.Equal("config", configPause.PausedBy);

            await pauses.PauseAsync(Codex, "api outage", "api");
            monitor.Fire(new CodeyBoxOptions());

            Assert.Null(await pauses.GetAgentStateAsync(Claude));
            var runtimePause = await pauses.GetAgentStateAsync(Codex);
            Assert.NotNull(runtimePause);
            Assert.Equal("api", runtimePause!.PausedBy);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task AgentPausesStartup_AppliesConfiguredPauses()
    {
        var initial = new CodeyBoxOptions
        {
            AgentPauses = new Dictionary<string, AgentPauseConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new() { Paused = true, Reason = "reserve quota at boot" },
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-startup-pauses-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            dbPath,
            NullLogger<SqliteAgentPauseController>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            pauses: pauses);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);

            var state = await pauses.GetAgentStateAsync(Claude);
            Assert.NotNull(state);
            Assert.Equal("reserve quota at boot", state!.PausedReason);
            Assert.Equal("config", state.PausedBy);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task AgentPausesHotReload_PausedFalseEntry_DoesNotPauseAgent()
    {
        var initial = new CodeyBoxOptions();
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-off-entry-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            dbPath,
            NullLogger<SqliteAgentPauseController>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            pauses: pauses);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);

            monitor.Fire(new CodeyBoxOptions
            {
                AgentPauses = new Dictionary<string, AgentPauseConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = new() { Paused = false },
                },
            });

            Assert.Null(await pauses.GetAgentStateAsync(Claude));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task AgentPausesHotReload_DoesNotOverwriteExistingRuntimePause()
    {
        var initial = new CodeyBoxOptions();
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-runtime-takeover-{Guid.NewGuid():N}.db");
        using var pauses = new SqliteAgentPauseController(
            dbPath,
            NullLogger<SqliteAgentPauseController>.Instance);

        await pauses.PauseAsync(Claude, "api pause", "api");

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            pauses: pauses);
        try
        {
            await coordinator.StartAsync(CancellationToken.None);

            monitor.Fire(new CodeyBoxOptions
            {
                AgentPauses = new Dictionary<string, AgentPauseConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = new() { Paused = true, Reason = "config wants to pause too" },
                },
            });

            var state = await pauses.GetAgentStateAsync(Claude);
            Assert.NotNull(state);
            Assert.Equal("api", state!.PausedBy);
            Assert.Equal("api pause", state.PausedReason);

            // Removing the config entry must NOT resume the still-active runtime pause.
            monitor.Fire(new CodeyBoxOptions());
            var after = await pauses.GetAgentStateAsync(Claude);
            Assert.NotNull(after);
            Assert.Equal("api", after!.PausedBy);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task SmokeHotReload_ReEnableRunsMissingProbeCoverage()
    {
        var initial = new CodeyBoxOptions
        {
            AgentClasses = [HotReloadClass("frontier", "cursor")],
            Smoke = new SmokeConfig { Enabled = false },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        var coverage = new InVmSmokeCoveragePolicy(
            [new ClaudeInVmSmokeProbe()],
            registry,
            new InVmSmokeOptions { Enabled = true },
            smokeOptions);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            coverage: coverage,
            smokeOptions: smokeOptions);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions
        {
            AgentClasses = [HotReloadClass("frontier", "cursor")],
            Smoke = new SmokeConfig { Enabled = true },
        });

        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Contains("no registered IInVmSmokeProbe", registry.GetAvailability(AgentKind.Cursor).Reason);

        await coordinator.StopAsync(CancellationToken.None);
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
    public async Task Coordinator_OnChange_BenchesNewlyAddedMemberWithoutInVmProbe()
    {
        // AC#1 must hold across hot-reloads, not just at startup: a member added
        // at runtime with no registered in-VM probe would otherwise stay
        // default-Available and fail on first dispatch. The reload must re-run
        // coverage enforcement through the gate so the new uncovered member is
        // benched immediately.
        var initial = new CodeyBoxOptions
        {
            AgentClasses = [HotReloadClass("frontier", "claude")],
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);

        // Real coverage policy: claude has a probe (covered), cursor does not.
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var coverage = new InVmSmokeCoveragePolicy(
            [new CodeyBox.Agents.Claude.ClaudeInVmSmokeProbe()],
            registry,
            new InVmSmokeOptions { Enabled = true });

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            coverage: coverage);
        await coordinator.StartAsync(CancellationToken.None);

        // Add a class naming an uncovered agent at runtime.
        var updated = new CodeyBoxOptions
        {
            AgentClasses =
            [
                HotReloadClass("frontier", "claude"),
                HotReloadClass("extra", "cursor"),
            ],
        };
        monitor.Fire(updated);

        Assert.False(registry.GetAvailability(AgentKind.Cursor).Available);
        Assert.Contains("no registered IInVmSmokeProbe", registry.GetAvailability(AgentKind.Cursor).Reason);
        Assert.True(registry.GetAvailability(AgentKind.Claude).Available);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_WhenSmokeDisabled_DoesNotBenchNewlyAddedMemberWithoutInVmProbe()
    {
        var initial = new CodeyBoxOptions
        {
            AgentClasses = [HotReloadClass("frontier", "claude")],
            Smoke = new SmokeConfig { Enabled = false },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var router = new AgentClassRouter(
            AgentClassesConfigBuilder.Build(initial.AgentClasses, NullLogger<AgentClassRouter>.Instance),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(new AgentConcurrencyOptions());
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), new AgentBurnEstimatorOptions(),
            NullLogger<AgentBurnEstimator>.Instance);

        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false });
        var coverage = new InVmSmokeCoveragePolicy(
            [new ClaudeInVmSmokeProbe()],
            registry,
            new InVmSmokeOptions { Enabled = true },
            smokeOptions);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            coverage: coverage,
            smokeOptions: smokeOptions);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions
        {
            AgentClasses =
            [
                HotReloadClass("frontier", "claude"),
                HotReloadClass("extra", "cursor"),
            ],
            Smoke = new SmokeConfig { Enabled = false },
        });

        Assert.True(registry.GetAvailability(AgentKind.Cursor).Available);

        await coordinator.StopAsync(CancellationToken.None);
    }

    private static AgentClassOptions HotReloadClass(string id, params string[] agents)
    {
        var cls = new AgentClassOptions { Id = id, DisplayName = id };
        foreach (var a in agents)
            cls.Members.Add(new AgentMembershipOptions { Agent = a, Billing = "Subscription", QualityScore = 100 });
        return cls;
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

    // ── PipelineTuning hot-reload ────────────────────────────────────────────

    [Fact]
    public async Task Coordinator_OnChange_PipelineTuningPushesToSnapshot()
    {
        // End-to-end: an edit to CodeyBox:PipelineTuning must reach the live
        // PipelineTuningSnapshot without a restart so PipelineRunner's
        // quota-fallback and merge-staging retry paths observe the new values.
        var initial = new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = TimeSpan.FromMinutes(5),
                QuotaExhaustionFallbackTtl = TimeSpan.FromHours(1),
                MaxParsedQuotaResetWindow = TimeSpan.FromHours(24),
                MergeSandboxStagingRestoreAttempts = 2,
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var snapshot = new PipelineTuningSnapshot(
            new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = initial.PipelineTuning.DefaultQuotaFailurePause,
                QuotaExhaustionFallbackTtl = initial.PipelineTuning.QuotaExhaustionFallbackTtl,
                MaxParsedQuotaResetWindow = initial.PipelineTuning.MaxParsedQuotaResetWindow,
                MergeSandboxStagingRestoreAttempts = initial.PipelineTuning.MergeSandboxStagingRestoreAttempts,
            });

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
            pipelineTuning: snapshot);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Current.DefaultQuotaFailurePause);
        Assert.Equal(2, snapshot.Current.MergeSandboxStagingRestoreAttempts);

        // Hot-reload: shorten the quota pause and bump retry attempts.
        monitor.Fire(new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = TimeSpan.FromMinutes(1),
                MergeSandboxStagingRestoreAttempts = 3,
            },
        });
        Assert.Equal(TimeSpan.FromMinutes(1), snapshot.Current.DefaultQuotaFailurePause);
        Assert.Equal(3, snapshot.Current.MergeSandboxStagingRestoreAttempts);

        // Same-value fire should be a no-op.
        monitor.Fire(new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = TimeSpan.FromMinutes(1),
                MergeSandboxStagingRestoreAttempts = 3,
            },
        });
        Assert.Equal(3, snapshot.Current.MergeSandboxStagingRestoreAttempts);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_PipelineTuningNoOpWhenSnapshotNull()
    {
        var initial = new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions(),
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

        var capturingLog = new CapturingLogger<AgentConfigHotReload>();

        // No pipelineTuning snapshot — the coordinator must not throw.
        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            capturingLog,
            pipelineTuning: null);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = TimeSpan.FromMinutes(1),
            },
        });

        await coordinator.StopAsync(CancellationToken.None);

        // Null-guard must have returned early without logging any warning or error
        // (a bug that threw or logged an error would still pass the "no-throw"
        //  contract but would indicate the guard wasn't wired correctly).
        Assert.DoesNotContain(capturingLog.Entries, e => e.Level >= LogLevel.Warning);
    }

    // ── BudgetDeferralRecheck hot-reload ─────────────────────────────────────

    [Fact]
    public async Task Coordinator_OnChange_BudgetDeferralRecheckPushesToSnapshot()
    {
        // End-to-end: an edit to CodeyBox:BudgetDeferralRecheck must reach the
        // live BudgetDeferralRecheckSnapshot without a restart so
        // OrchestratorService's budget-cap deferral paths observe the new
        // recheck intervals on the next pickup attempt.
        var initial = new CodeyBoxOptions
        {
            BudgetDeferralRecheck = new BudgetDeferralRecheckOptions
            {
                PausedProjectRecheck = TimeSpan.FromMinutes(1),
                HourlyLimitRecheck = TimeSpan.FromMinutes(5),
                DailyLimitRecheck = TimeSpan.FromHours(1),
                ConcurrentLimitRecheck = TimeSpan.FromMinutes(1),
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var snapshot = new BudgetDeferralRecheckSnapshot(
            new BudgetDeferralRecheckOptions
            {
                PausedProjectRecheck = initial.BudgetDeferralRecheck.PausedProjectRecheck,
                HourlyLimitRecheck = initial.BudgetDeferralRecheck.HourlyLimitRecheck,
                DailyLimitRecheck = initial.BudgetDeferralRecheck.DailyLimitRecheck,
                ConcurrentLimitRecheck = initial.BudgetDeferralRecheck.ConcurrentLimitRecheck,
            });

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
            budgetDeferralRecheck: snapshot);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Current.HourlyLimitRecheck);
        Assert.Equal(TimeSpan.FromMinutes(1), snapshot.Current.PausedProjectRecheck);

        // Hot-reload: shorten the hourly recheck and lengthen the paused recheck.
        monitor.Fire(new CodeyBoxOptions
        {
            BudgetDeferralRecheck = new BudgetDeferralRecheckOptions
            {
                HourlyLimitRecheck = TimeSpan.FromMinutes(2),
                PausedProjectRecheck = TimeSpan.FromMinutes(15),
            },
        });
        Assert.Equal(TimeSpan.FromMinutes(2), snapshot.Current.HourlyLimitRecheck);
        Assert.Equal(TimeSpan.FromMinutes(15), snapshot.Current.PausedProjectRecheck);

        // Same-value fire should be a no-op.
        monitor.Fire(new CodeyBoxOptions
        {
            BudgetDeferralRecheck = new BudgetDeferralRecheckOptions
            {
                HourlyLimitRecheck = TimeSpan.FromMinutes(2),
                PausedProjectRecheck = TimeSpan.FromMinutes(15),
            },
        });
        Assert.Equal(TimeSpan.FromMinutes(2), snapshot.Current.HourlyLimitRecheck);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_BudgetDeferralRecheckNoOpWhenSnapshotNull()
    {
        var initial = new CodeyBoxOptions
        {
            BudgetDeferralRecheck = new BudgetDeferralRecheckOptions(),
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

        var capturingLog = new CapturingLogger<AgentConfigHotReload>();

        // No budgetDeferralRecheck snapshot — the coordinator must not throw.
        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            capturingLog,
            budgetDeferralRecheck: null);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions
        {
            BudgetDeferralRecheck = new BudgetDeferralRecheckOptions
            {
                HourlyLimitRecheck = TimeSpan.FromMinutes(2),
            },
        });

        await coordinator.StopAsync(CancellationToken.None);

        // Null-guard must have returned early without logging any warning or error.
        Assert.DoesNotContain(capturingLog.Entries, e => e.Level >= LogLevel.Warning);
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

    [Fact]
    public async Task Coordinator_OnChange_QuotaRouterColdStartFitInWindowPropagates()
    {
        var initial = new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig
            {
                MinQuotaPct = 10.0,
                ColdStartFitInWindow = 2.0,
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var qro = new QuotaRouterOptions
        {
            MinQuotaPct = initial.QuotaRouter.MinQuotaPct,
            ColdStartFitInWindow = initial.QuotaRouter.ColdStartFitInWindow,
        };
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            qro,
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            quotaRouterOptions: qro);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(2.0, qro.ColdStartFitInWindow);

        monitor.Fire(new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig { MinQuotaPct = 10.0, ColdStartFitInWindow = 5.0 },
        });
        Assert.Equal(5.0, qro.ColdStartFitInWindow);

        monitor.Fire(new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig { MinQuotaPct = 10.0, ColdStartFitInWindow = 1.5 },
        });
        Assert.Equal(1.5, qro.ColdStartFitInWindow);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_QuotaRouterIntraKindRoutingPolicyPropagates()
    {
        var initial = new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig
            {
                MinQuotaPct = 10.0,
                IntraKindRoutingPolicy = IntraKindRoutingPolicy.MostQuotaFirst,
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var qro = new QuotaRouterOptions
        {
            MinQuotaPct = initial.QuotaRouter.MinQuotaPct,
            IntraKindRoutingPolicy = initial.QuotaRouter.IntraKindRoutingPolicy,
        };
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            qro,
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            quotaRouterOptions: qro);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(IntraKindRoutingPolicy.MostQuotaFirst, qro.IntraKindRoutingPolicy);

        monitor.Fire(new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig
            {
                MinQuotaPct = 10.0,
                IntraKindRoutingPolicy = IntraKindRoutingPolicy.RoundRobin,
            },
        });
        Assert.Equal(IntraKindRoutingPolicy.RoundRobin, qro.IntraKindRoutingPolicy);

        monitor.Fire(new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig
            {
                MinQuotaPct = 10.0,
                IntraKindRoutingPolicy = IntraKindRoutingPolicy.Sticky,
            },
        });
        Assert.Equal(IntraKindRoutingPolicy.Sticky, qro.IntraKindRoutingPolicy);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_QuotaRouterFloorByAgentPropagates()
    {
        var initial = new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig { MinQuotaPct = 10.0 },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var qro = new QuotaRouterOptions
        {
            MinQuotaPct = initial.QuotaRouter.MinQuotaPct,
            StartFloorPct = initial.QuotaRouter.StartFloorPct,
            EndFloorPct = initial.QuotaRouter.EndFloorPct,
        };
        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            qro,
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            quotaRouterOptions: qro);
        await coordinator.StartAsync(CancellationToken.None);

        monitor.Fire(new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig
            {
                MinQuotaPct = 10.0,
                FloorByAgent = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new QuotaRouterFloorConfig
                    {
                        MinQuotaPct = 1.0,
                        StartFloorPct = 1.0,
                        EndFloorPct = 0.0,
                        RampWindowSeconds = 86_400,
                    },
                },
            },
        });

        Assert.True(qro.FloorByAgent.TryGetValue("codex", out var codexFloor));
        Assert.NotNull(codexFloor);
        Assert.Equal(1.0, codexFloor.MinQuotaPct);
        Assert.Equal(1.0, codexFloor.StartFloorPct);
        Assert.Equal(0.0, codexFloor.EndFloorPct);
        Assert.Equal(TimeSpan.FromDays(1), codexFloor.RampWindow);

        monitor.Fire(new CodeyBoxOptions
        {
            QuotaRouter = new QuotaRouterConfig { MinQuotaPct = 10.0 },
        });

        Assert.Empty(qro.FloorByAgent);

        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_OnChange_PipelineTuningPushesNewFieldsToSnapshot()
    {
        var initial = new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = TimeSpan.FromMinutes(5),
                MaxQuestionsPerWorkItem = 10,
                AgentSuspendMaxRetries = 1,
            },
        };
        var monitor = new ManualOptionsMonitor<CodeyBoxOptions>(initial);
        var snapshot = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            DefaultQuotaFailurePause = initial.PipelineTuning.DefaultQuotaFailurePause,
            MaxQuestionsPerWorkItem = initial.PipelineTuning.MaxQuestionsPerWorkItem,
            AgentSuspendMaxRetries = initial.PipelineTuning.AgentSuspendMaxRetries,
        });

        var router = new AgentClassRouter(
            Array.Empty<AgentClass>(),
            Array.Empty<IAgentQuotaProbe>(),
            new QuotaRouterOptions { MinQuotaPct = 5.0 },
            NullLogger<AgentClassRouter>.Instance);
        using var orchFixture = OrchestratorFixture.Build(initial.AgentConcurrency);
        var burnEstimator = new AgentBurnEstimator(
            new InertCostStore(), initial.AgentBurnEstimator,
            NullLogger<AgentBurnEstimator>.Instance);

        // Capture the static default before the coordinator sets it.
        var originalMaxRetries = AgentSuspendResilience.MaxRetries;

        var coordinator = new AgentConfigHotReload(
            monitor, orchFixture.Orchestrator, router, burnEstimator,
            NullLogger<AgentConfigHotReload>.Instance,
            pipelineTuning: snapshot);
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(10, snapshot.Current.MaxQuestionsPerWorkItem);
        Assert.Equal(1, snapshot.Current.AgentSuspendMaxRetries);

        // SetMaxRetries is called on start; verify the static was initialised.
        Assert.Equal(1, AgentSuspendResilience.MaxRetries);

        monitor.Fire(new CodeyBoxOptions
        {
            PipelineTuning = new PipelineTuningOptions
            {
                DefaultQuotaFailurePause = TimeSpan.FromMinutes(1),
                MaxQuestionsPerWorkItem = 20,
                AgentSuspendMaxRetries = 3,
            },
        });
        Assert.Equal(20, snapshot.Current.MaxQuestionsPerWorkItem);
        Assert.Equal(3, snapshot.Current.AgentSuspendMaxRetries);
        Assert.Equal(3, AgentSuspendResilience.MaxRetries);

        // Restore original (shared static).
        AgentSuspendResilience.SetMaxRetries(originalMaxRetries);

        await coordinator.StopAsync(CancellationToken.None);
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
