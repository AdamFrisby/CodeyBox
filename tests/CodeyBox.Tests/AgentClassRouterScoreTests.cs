using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentClassRouter"/> score-based routing.
/// Uses programmable fakes for probes and <see cref="TimeProvider"/> to
/// exercise the floor filter, effective-score sort, TOD modifiers, and
/// ROUTING_NO_ELIGIBLE fast-fail.
/// </summary>
public sealed class AgentClassRouterScoreTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;
    private static readonly AgentKind Gemini = AgentKind.Gemini;

    // Monday 15:00 UTC — inside Mon-Fri 14:00-22:00 peak window.
    private static readonly DateTimeOffset PeakTime =
        new(2025, 5, 5, 15, 0, 0, TimeSpan.Zero); // Monday

    // Monday 08:00 UTC — outside peak window.
    private static readonly DateTimeOffset OffPeakTime =
        new(2025, 5, 5, 8, 0, 0, TimeSpan.Zero);

    private static AgentClassRouter BuildRouter(
        IEnumerable<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        double minQuotaPct = 10.0,
        TimeProvider? timeProvider = null,
        IReadOnlyList<ParsedTodModifier>? todModifiers = null)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = minQuotaPct, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(
            catalog.ToList(), probes, opts,
            NullLogger<AgentClassRouter>.Instance,
            timeProvider, todModifiers);
    }

    private static WorkItem MakeItem(string? classId = "frontier", int minModelScore = 95) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
        MinModelScore = minModelScore,
    };

    private static AgentMembership Sub(AgentKind kind, int score, string? modelId = null) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = score, ModelId = modelId };

    private static AgentMembership Api(AgentKind kind, int score) =>
        new() { Agent = kind, Billing = AgentBilling.PayPerApi, QualityScore = score };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    /// <summary>
    /// Mon-Fri 14:00–22:00 UTC modifier of -1 for Claude — the canonical
    /// Anthropic peak-hours tiebreaker used in the design doc.
    /// </summary>
    private static ParsedTodModifier PeakClaudeMinus1() =>
        new(Claude, -1, [
            new ParsedTimeWindow(
                new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                TimeSpan.FromHours(14),
                TimeSpan.FromHours(22))]);

    // ── Stored Agent preference is ignored during class routing ───────────────

    [Fact]
    public async Task StoredAgentPreference_IsIgnored_HigherScoringMemberWins()
    {
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 90));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = Codex,
            AgentClassId = "frontier",
            MinModelScore = 80,
        };

        var decision = await router.ResolveAsync(item, null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.Equal(Codex, item.Agent);
    }

    // ── Floor filter ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MinModelScore95_AdmitsOpus100_Codex100_Gemini95()
    {
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100), Sub(Gemini, 95));
        var router = BuildRouter([cls], [
            new FakeProbe(Claude, 50.0),
            new FakeProbe(Codex, 50.0),
            new FakeProbe(Gemini, 50.0),
        ]);
        var decision = await router.ResolveAsync(MakeItem(minModelScore: 95), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.False(decision.NoEligibleMembers);
    }

    [Fact]
    public async Task MinModelScore95_RejectsSonnet80()
    {
        var cls = FrontierClass(Sub(Claude, 80));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 100.0)]);
        var decision = await router.ResolveAsync(MakeItem(minModelScore: 95), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.NoEligibleMembers);
        Assert.Contains("ROUTING_NO_ELIGIBLE", decision.Reason);
    }

    [Fact]
    public async Task AllMembersBelowFloor_ReturnsNoEligible_DoesNotWait()
    {
        var cls = FrontierClass(Sub(Claude, 80), Sub(Codex, 70));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 100.0), new FakeProbe(Codex, 100.0)]);
        var decision = await router.ResolveAsync(MakeItem(minModelScore: 95), null, CancellationToken.None);

        Assert.True(decision.NoEligibleMembers);
        Assert.False(decision.ShouldWait);
    }

    // ── Config-order tiebreaker when effective scores are equal ───────────────

    [Fact]
    public async Task AllTiedAt100_NoTodModifier_PicksFirstInConfigOrder()
    {
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100), Sub(Gemini, 100));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0), new FakeProbe(Gemini, 50.0)],
            timeProvider: new FakeTimeProvider(OffPeakTime));

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    // ── TOD modifier changes preference among tied models ────────────────────

    [Fact]
    public async Task ClaudeTodModifierMinus1_DuringPeak_PrefersCodexOverClaude()
    {
        // Claude eff=99, Codex eff=100 → Codex wins.
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)],
            timeProvider: new FakeTimeProvider(PeakTime),
            todModifiers: [PeakClaudeMinus1()]);

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task ClaudeTodActive_CodexExhausted_StillPicksClaudeAboveFloor()
    {
        // Codex quota exhausted (1% < 10% threshold). Claude eff=99 ≥ floor=95 → Claude routed.
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 1.0)],
            timeProvider: new FakeTimeProvider(PeakTime),
            todModifiers: [PeakClaudeMinus1()]);

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task ClaudeTodActive_GeminiStillLast()
    {
        // Sort: Codex(eff=100) > Claude(eff=99) > Gemini(eff=95). Codex wins.
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100), Sub(Gemini, 95));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0), new FakeProbe(Gemini, 50.0)],
            timeProvider: new FakeTimeProvider(PeakTime),
            todModifiers: [PeakClaudeMinus1()]);

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    // ── Wrap-around TOD window (22:00–02:00) ─────────────────────────────────

    [Theory]
    [InlineData(23, 30, DayOfWeek.Sunday)]   // 23:30 Sun is inside Sun+Mon window
    [InlineData(1, 0, DayOfWeek.Monday)]     // 01:00 Mon is inside Sun+Mon window
    public async Task WrapAroundWindow_InsideWindow_ModifierApplied(int hour, int minute, DayOfWeek day)
    {
        // Window: Sun-Mon 22:00–02:00 UTC (wrap-around).
        var fakeTime = new FakeTimeProvider(MakeUtcDay(day, hour, minute));
        var wrapWindow = new ParsedTimeWindow(
            new HashSet<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Monday },
            TimeSpan.FromHours(22),
            TimeSpan.FromHours(2));
        var modifier = new ParsedTodModifier(Claude, -1, [wrapWindow]);

        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)],
            timeProvider: fakeTime,
            todModifiers: [modifier]);

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        // Modifier active → Codex(100) > Claude(99).
        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task WrapAroundWindow_OutsideWindow_ModifierNotApplied()
    {
        // Monday 12:00 UTC is outside 22:00–02:00 → no modifier → Claude wins by config order.
        var fakeTime = new FakeTimeProvider(MakeUtcDay(DayOfWeek.Monday, 12, 0));
        var wrapWindow = new ParsedTimeWindow(
            new HashSet<DayOfWeek> { DayOfWeek.Monday },
            TimeSpan.FromHours(22),
            TimeSpan.FromHours(2));
        var modifier = new ParsedTodModifier(Claude, -1, [wrapWindow]);

        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)],
            timeProvider: fakeTime,
            todModifiers: [modifier]);

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    // ── Billing tiebreaker: Subscription before PayPerApi when scores equal ───

    [Fact]
    public async Task EqualScore_SubscriptionBeforePayPerApi()
    {
        // Codex PayPerApi listed first, Claude Subscription listed second — same score.
        var cls = FrontierClass(Api(Codex, 100), Sub(Claude, 100));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0)]);

        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.Subscription, decision.Chosen.Billing);
    }

    // ── ShouldWait when all eligible subscription members are exhausted ───────

    [Fact]
    public async Task AllEligibleSubscriptionExhausted_ShouldWait()
    {
        var cls = FrontierClass(Sub(Claude, 100), Sub(Codex, 100));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 2.0), new FakeProbe(Codex, 3.0)]);
        var decision = await router.ResolveAsync(MakeItem(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.SuggestedRecheckIn > TimeSpan.Zero);
    }

    // ── TOD modifier does not affect the eligibility floor check ─────────────

    [Fact]
    public async Task TodModifier_DoesNotAffectFloorCheck_BaseScoredMemberStillEligible()
    {
        // Claude base=95 passes floor=95. TOD applies -1 → eff=94.
        // Effective score below floor is OK: TOD is preference-shaping, not gating.
        var cls = FrontierClass(Sub(Claude, 95));
        var router = BuildRouter(
            [cls],
            [new FakeProbe(Claude, 50.0)],
            timeProvider: new FakeTimeProvider(PeakTime),
            todModifiers: [PeakClaudeMinus1()]);

        var decision = await router.ResolveAsync(MakeItem(minModelScore: 95), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.NoEligibleMembers);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns a UTC DateTimeOffset for the next occurrence of 'day' at the given time.
    private static DateTimeOffset MakeUtcDay(DayOfWeek day, int hour, int minute)
    {
        // Use a fixed base date (2025-05-04 = Sunday) and offset by day index.
        var baseSunday = new DateTimeOffset(2025, 5, 4, hour, minute, 0, TimeSpan.Zero);
        var daysOffset = ((int)day - (int)DayOfWeek.Sunday + 7) % 7;
        return baseSunday.AddDays(daysOffset);
    }
}

/// <summary>
/// Fixed-time <see cref="TimeProvider"/> for router tests. Returns the same
/// instant on every call — no wall-clock dependency.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
