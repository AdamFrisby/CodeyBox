using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="AgentClassRouter"/>.
/// Uses a programmable fake probe to verify member preference, threshold
/// gate, wait-for-subscription behaviour, and PayPerApi fallthrough.
/// </summary>
public sealed class AgentClassRouterTests
{
    private static readonly AgentKind Claude = AgentKind.Claude;
    private static readonly AgentKind Codex = AgentKind.Codex;

    private static AgentClassRouter BuildRouter(
        IEnumerable<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        double minQuotaPct = 10.0,
        IAgentDispatchAvailability? dispatchAvailability = null)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = minQuotaPct, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(
            catalog.ToList(),
            probes,
            opts,
            NullLogger<AgentClassRouter>.Instance,
            dispatchAvailability: dispatchAvailability);
    }

    private static WorkItem MakeItem(string? classId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = classId,
    };

    private static Project MakeProject(string? defaultClass = null) => new()
    {
        Id = new ProjectId("proj"),
        DisplayName = "Test",
        RepositoryUrl = "https://git.example.com/repo",
        DefaultAgentClass = defaultClass,
    };

    private static AgentClass FrontierClass(params AgentMembership[] members) => new()
    {
        Id = "frontier",
        DisplayName = "Frontier",
        Members = members,
    };

    private static AgentMembership Sub(AgentKind kind) =>
        new() { Agent = kind, Billing = AgentBilling.Subscription, QualityScore = 100 };

    private static AgentMembership Api(AgentKind kind) =>
        new() { Agent = kind, Billing = AgentBilling.PayPerApi, QualityScore = 100 };

    // ── No class configured ──────────────────────────────────────────────────

    [Fact]
    public async Task NoClassId_ReturnsNoChosen_NoWait()
    {
        var router = BuildRouter([], []);
        var decision = await router.ResolveAsync(MakeItem(classId: null), MakeProject(), CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task NoClassId_ButProjectDefault_PicksFromClass()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0)]);
        var decision = await router.ResolveAsync(MakeItem(classId: null), MakeProject("frontier"), CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    // ── Member preference ────────────────────────────────────────────────────

    [Fact]
    public async Task FirstMember_AvailablePct_HighEnough_Chosen()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 50.0), new FakeProbe(Codex, 50.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task SubscriptionMember_InvokesRegisteredQuotaProbe()
    {
        var probe = new FakeProbe(Claude, 50.0);
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [probe]);

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task FirstMember_Exhausted_FallsBackToSecond()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 5.0), new FakeProbe(Codex, 60.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task UnknownAvailablePct_TreatedAsAvailable_FailOpen()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new FakeProbe(Claude, -1.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    // ── Threshold gate ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExactlyAtThreshold_Chosen()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 10.0)], minQuotaPct: 10.0);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task JustBelowThreshold_Skipped()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 9.9), new FakeProbe(Codex, 15.0)], minQuotaPct: 10.0);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
    }

    // ── Wait for subscription ─────────────────────────────────────────────────

    [Fact]
    public async Task AllSubscriptionMembersExhausted_ShouldWait()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 2.0), new FakeProbe(Codex, 3.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.True(decision.SuggestedRecheckIn > TimeSpan.Zero);
    }

    // ── PayPerApi fallthrough ─────────────────────────────────────────────────

    [Fact]
    public async Task PayPerApiMember_NeverWaits_AlwaysFires()
    {
        var cls = FrontierClass(Api(Claude));
        var router = BuildRouter([cls], []);   // no probe needed for PayPerApi
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task SubscriptionExhausted_FallsBackToPayPerApi()
    {
        var cls = FrontierClass(Sub(Claude), Api(Codex));
        var router = BuildRouter([cls], [new FakeProbe(Claude, 1.0)]);
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
    }

    // ── No probe registered → unknown policy ────────────────────────────────

    [Fact]
    public async Task NoProbeRegistered_ForSubscriptionMember_TreatedAsAvailable()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], []);   // no Claude probe registered
        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    // ── Unknown class id ──────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownClassId_FallsThrough_NoChosen()
    {
        var router = BuildRouter([], []);
        var decision = await router.ResolveAsync(MakeItem("does-not-exist"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.False(decision.ShouldWait);
    }

    // ── ComputeEarliestExhaustedResetAsync ───────────────────────────────────

    [Fact]
    public async Task ComputeEarliestExhaustedReset_TakesMinAcrossExhaustedMembers()
    {
        // Three exhausted members with very different reset times: claude (~5h),
        // gemini (~24h), codex (~1h). MIN must be the codex reset, NOT the
        // last-tried agent's reset.
        var now = DateTimeOffset.UtcNow;
        var claudeReset = now.AddHours(5);
        var geminiReset = now.AddHours(24);
        var codexReset = now.AddHours(1);

        var cls = FrontierClass(Sub(Claude), Sub(Codex), Sub(AgentKind.Gemini));
        var router = BuildRouter([cls],
        [
            new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = claudeReset }),
            new FakeProbe(Codex, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = codexReset }),
            new FakeProbe(AgentKind.Gemini, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = geminiReset }),
        ]);

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(codexReset, earliest);
    }

    [Fact]
    public async Task ComputeEarliestExhaustedReset_IgnoresAvailableMembers()
    {
        // One available (50%), one exhausted with known reset. Only the
        // exhausted one constrains the park time.
        var now = DateTimeOffset.UtcNow;
        var geminiReset = now.AddHours(10);

        var cls = FrontierClass(Sub(Claude), Sub(AgentKind.Gemini));
        var router = BuildRouter([cls],
        [
            new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 50, ResetAt = now.AddHours(2) }),
            new FakeProbe(AgentKind.Gemini, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = geminiReset }),
        ]);

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(geminiReset, earliest);
    }

    [Fact]
    public async Task ComputeEarliestExhaustedReset_SkipsUnknownAndNoResetAt()
    {
        // Mixed bag: unknown probe (-1), exhausted but no ResetAt, exhausted with reset.
        // Only the last contributes.
        var now = DateTimeOffset.UtcNow;
        var claudeReset = now.AddHours(3);

        var cls = FrontierClass(Sub(Claude), Sub(Codex), Sub(AgentKind.Gemini));
        var router = BuildRouter([cls],
        [
            new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = claudeReset }),
            new FakeProbe(Codex, new AgentQuotaSnapshot { AvailablePct = -1 }), // unknown
            new FakeProbe(AgentKind.Gemini, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = null }), // exhausted but no reset
        ]);

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(claudeReset, earliest);
    }

    [Fact]
    public async Task ComputeEarliestExhaustedReset_IgnoresPayPerApiMembers()
    {
        // PayPerApi never parks on quota — exclude it even if a custom probe
        // reports it exhausted.
        var now = DateTimeOffset.UtcNow;
        var claudeReset = now.AddHours(2);

        var cls = FrontierClass(Sub(Claude), Api(Codex));
        var router = BuildRouter([cls],
        [
            new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = claudeReset }),
        ]);

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(claudeReset, earliest);
    }

    [Fact]
    public async Task ComputeEarliestExhaustedReset_SkipsPausedMembers()
    {
        var now = DateTimeOffset.UtcNow;
        var pausedReset = now.AddMinutes(10);
        var activeReset = now.AddHours(2);
        using var pauses = new SqliteAgentPauseController(
            Path.Combine(Path.GetTempPath(), $"codeybox-router-pauses-{Guid.NewGuid():N}.db"),
            NullLogger<SqliteAgentPauseController>.Instance);
        await pauses.PauseAsync(Claude, "reserve for oversight", "test");

        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = BuildRouter(
            [cls],
            [
                new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = pausedReset }),
                new FakeProbe(Codex, new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = activeReset }),
            ],
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(activeReset, earliest);
    }

    [Fact]
    public async Task ComputeEarliestExhaustedReset_NoClassConfigured_ReturnsNull()
    {
        var router = BuildRouter([], []);
        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem(classId: null), MakeProject(defaultClass: null), CancellationToken.None);

        Assert.Null(earliest);
    }
}

/// <summary>Fake probe that always returns a fixed AvailablePct.</summary>
internal sealed class FakeProbe : IAgentQuotaProbe
{
    private readonly AgentQuotaSnapshot _snapshot;

    public FakeProbe(AgentKind kind, double availablePct)
        : this(kind, new AgentQuotaSnapshot { AvailablePct = availablePct })
    {
    }

    public FakeProbe(AgentKind kind, AgentQuotaSnapshot snapshot)
    {
        Kind = kind;
        _snapshot = snapshot;
    }

    public AgentKind Kind { get; }

    public int CallCount { get; private set; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_snapshot);
    }
}
