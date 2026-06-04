using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Per-window absolute quota floors: dispatch is gated on every provider
/// window (e.g. claude <c>five_hour</c>, <c>seven_day</c>) being at or above
/// its own configured floor — block if any window is below. The bug being
/// fixed: a single overall <see cref="QuotaRouterOptions.MinQuotaPct"/>
/// applied to the aggregated MIN-across-windows treats 10 % of the smaller
/// 5h window the same as 10 % of the much larger 7d window, but 5h has far
/// less absolute headroom for in-flight + cache-staleness overshoot during a
/// burst. Per-window floors let the operator demand a higher fraction
/// remaining on the burst-binding window.
///
/// <para>
/// These tests pin the gate behaviour (above / below / mixed / unlisted
/// fallback / hot-reload) and one end-to-end pass through <see
/// cref="AgentClassRouter.ResolveAsync"/>. Time-ramp floor and per-window
/// floor are orthogonal — these tests fix the ramp at the start-of-window
/// floor so the ramp never overrides the per-window decision.
/// </para>
/// </summary>
public sealed class QuotaRouterPerWindowFloorTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    private static readonly DateTimeOffset Now =
        new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    private static QuotaRouterOptions Opts(
        Dictionary<string, double>? windowFloors = null,
        double minQuotaPct = 10.0) => new()
        {
            MinQuotaPct = minQuotaPct,
            MinQuotaPctByWindow = windowFloors ?? new(StringComparer.OrdinalIgnoreCase),
            // Pin the ramp endpoints to the same value as MinQuotaPct so the
            // ramp doesn't interfere — these tests only assert the per-window
            // floor path; QuotaRouterRampedFloorTests covers the ramp itself.
            StartFloorPct = minQuotaPct,
            EndFloorPct = minQuotaPct,
            RampWindow = TimeSpan.FromDays(7),
        };

    private static AgentClass ClaudeOnlyClass(string? modelId = null) => new()
    {
        Id = "x",
        DisplayName = "x",
        Members = [
            new AgentMembership
            {
                Agent = Claude,
                Billing = AgentBilling.Subscription,
                ModelId = modelId,
                QualityScore = 100,
            },
        ],
    };

    private static WorkItem Item() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "x",
    };

    [Fact]
    public async Task FiveHourBelowItsFloor_ButSevenDayHigh_DispatchBlocked()
    {
        // The five-hour window is just under its 25 % floor. The aggregated
        // AvailablePct = MIN(20, 80) = 20, which is above the legacy 10 %
        // overall floor — under the old gate the dispatch went through and
        // the in-flight + cache-staleness overshoot blew through to 0. With
        // per-window floors, the 5h window's own 25 % gate fires.
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 20,
            Windows = [
                new WindowQuota { Name = "five_hour", AvailablePct = 20 },
                new WindowQuota { Name = "seven_day", AvailablePct = 80 },
            ],
        };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
                ["seven_day"] = 10.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public async Task BothWindowsAboveTheirFloors_DispatchProceeds()
    {
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 30,
            Windows = [
                new WindowQuota { Name = "five_hour", AvailablePct = 30 },
                new WindowQuota { Name = "seven_day", AvailablePct = 90 },
            ],
        };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
                ["seven_day"] = 10.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task FiveHourJustAboveFloor_DispatchProceeds()
    {
        // Pin: 25.0 == the floor passes (>= comparison, matches MinQuotaPct).
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 25,
            Windows = [
                new WindowQuota { Name = "five_hour", AvailablePct = 25 },
                new WindowQuota { Name = "seven_day", AvailablePct = 60 },
            ],
        };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
                ["seven_day"] = 10.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
    }

    [Fact]
    public async Task SevenDayBelowItsFloor_ButFiveHourHigh_DispatchBlocked()
    {
        // The 7d window is below its 10 % floor; 5h is healthy. The dispatch
        // gate must block because per-window means ANY-below blocks.
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 5,
            Windows = [
                new WindowQuota { Name = "five_hour", AvailablePct = 80 },
                new WindowQuota { Name = "seven_day", AvailablePct = 5 },
            ],
        };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
                ["seven_day"] = 10.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public void UnlistedWindow_FallsBackToMinQuotaPct()
    {
        var opts = Opts(
            new(StringComparer.OrdinalIgnoreCase) { ["five_hour"] = 25.0 },
            minQuotaPct: 12.5);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: Array.Empty<IAgentQuotaProbe>(),
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance);

        Assert.Equal(25.0, router.ResolveWindowFloorPct("five_hour"));
        Assert.Equal(12.5, router.ResolveWindowFloorPct("seven_day"));
        Assert.Equal(12.5, router.ResolveWindowFloorPct("anything-else"));
    }

    [Fact]
    public void AgentMinOverride_BeatsGlobalWindowFloorForThatAgentOnly()
    {
        var opts = Opts(
            new(StringComparer.OrdinalIgnoreCase) { ["five_hour"] = 25.0 },
            minQuotaPct: 10.0);
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            MinQuotaPct = 1.0,
        };
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: Array.Empty<IAgentQuotaProbe>(),
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance);

        Assert.Equal(1.0, router.ResolveWindowFloorPct(Codex, "five_hour"));
        Assert.Equal(25.0, router.ResolveWindowFloorPct(Claude, "five_hour"));
        Assert.Equal(1.0, router.ResolveWindowFloorPct(Codex, "unlisted-window"));
        Assert.Equal(10.0, router.ResolveWindowFloorPct(Claude, "unlisted-window"));
    }

    [Fact]
    public void PartialAgentOverrideWithoutMin_StillUsesGlobalWindowFloor()
    {
        var opts = Opts(
            new(StringComparer.OrdinalIgnoreCase) { ["five_hour"] = 25.0 },
            minQuotaPct: 10.0);
        opts.FloorByAgent[Codex.Value] = new QuotaFloorOverrideOptions
        {
            StartFloorPct = 1.0,
            EndFloorPct = 0.0,
        };
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: Array.Empty<IAgentQuotaProbe>(),
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance);

        Assert.Equal(25.0, router.ResolveWindowFloorPct(Codex, "five_hour"));
        Assert.Equal(10.0, router.ResolveWindowFloorPct(Codex, "unlisted-window"));
    }

    [Fact]
    public void DefaultOptions_HasNoPerWindowFloors_SoLegacyBehaviourPreserved()
    {
        // Constructing QuotaRouterOptions directly (the test path) must not
        // pick up an implicit per-window floor — Program.cs is the only thing
        // that wires the {five_hour: 25} default, so tests that synthesise
        // snapshots-with-windows aren't suddenly gated.
        var opts = new QuotaRouterOptions();
        Assert.Empty(opts.MinQuotaPctByWindow);
    }

    [Fact]
    public async Task SnapshotWithNoWindows_NotAffectedByPerWindowFloors()
    {
        // Legacy path: probes that don't surface per-window data shouldn't
        // be gated by per-window floors at all — the aggregated check is the
        // only check.
        var snapshot = new AgentQuotaSnapshot { AvailablePct = 20 };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
    }

    [Fact]
    public async Task UnknownWindowAvailability_NotBlockedByPerWindowFloor()
    {
        // A window reading of -1 (unknown) shouldn't drag dispatch down —
        // the aggregated availablePct check already handles unknowns via
        // QuotaUnknownPolicy. The per-window floor only fires on a positive
        // reading.
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 40,
            Windows = [
                new WindowQuota { Name = "five_hour", AvailablePct = -1 },
                new WindowQuota { Name = "seven_day", AvailablePct = 40 },
            ],
        };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
                ["seven_day"] = 10.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
    }

    [Fact]
    public async Task PerModelWindows_UsedWhenAvailable()
    {
        // When a member specifies a ModelId and the probe surfaces per-model
        // windows, the gate uses the per-model window readings (not the
        // overall snapshot windows). Pins that the Windows field on
        // ModelQuota threads through ResolveMemberQuota.
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 70,
            Windows = [
                new WindowQuota { Name = "five_hour", AvailablePct = 70 },
            ],
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-opus-4-7"] = new ModelQuota
                {
                    AvailablePct = 20,
                    Window = "five_hour",
                    Windows = [
                        new WindowQuota { Name = "five_hour", AvailablePct = 20 },
                        new WindowQuota { Name = "seven_day", AvailablePct = 90 },
                    ],
                },
            },
        };
        var probe = new FakeProbe(Claude, snapshot);
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass(modelId: "claude-opus-4-7")],
            probes: [probe],
            opts: Opts(new(StringComparer.OrdinalIgnoreCase)
            {
                ["five_hour"] = 25.0,
                ["seven_day"] = 10.0,
            }),
            log: NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), project: null, CancellationToken.None);

        // The per-model 5h reading (20%) is below the 25% floor — block.
        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
    }

    [Fact]
    public void HotReloadOfWindowFloors_TakesEffectOnNextCall()
    {
        // Router holds the QuotaRouterOptions singleton by reference and
        // reads .MinQuotaPctByWindow on every gate decision; mutating the
        // dictionary is how AgentConfigHotReload propagates config edits.
        var opts = Opts(new(StringComparer.OrdinalIgnoreCase) { ["five_hour"] = 25.0 });
        var router = new AgentClassRouter(
            catalog: [ClaudeOnlyClass()],
            probes: Array.Empty<IAgentQuotaProbe>(),
            opts: opts,
            log: NullLogger<AgentClassRouter>.Instance);

        Assert.Equal(25.0, router.ResolveWindowFloorPct("five_hour"));

        opts.MinQuotaPctByWindow = new(StringComparer.OrdinalIgnoreCase)
        {
            ["five_hour"] = 40.0,
        };

        Assert.Equal(40.0, router.ResolveWindowFloorPct("five_hour"));
    }
}
