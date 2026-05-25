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
        double minQuotaPct = 10.0)
    {
        var opts = new QuotaRouterOptions { MinQuotaPct = minQuotaPct, QuotaRecheckInterval = TimeSpan.FromMinutes(5) };
        return new AgentClassRouter(catalog.ToList(), probes, opts, NullLogger<AgentClassRouter>.Instance);
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

    private static InProcessQuotaHeadroomManager BuildHeadroomManager(
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        double estimatedPctCost) =>
        new(
            new FixedHeadroomEstimator(estimatedPctCost),
            probes,
            opts,
            NullLogger<InProcessQuotaHeadroomManager>.Instance);

    private static async Task<IQuotaReservationLease> ReserveAsync(
        IQuotaHeadroomManager manager,
        WorkItem item,
        AgentRoutingDecision decision)
    {
        Assert.NotNull(decision.Chosen);
        var gate = await manager.TryReserveAsync(
            new QuotaHeadroomGateRequest(
                item.ProjectId,
                decision.Chosen!,
                decision.ChosenAvailablePct ?? -1,
                decision.ChosenQuotaResetAt));
        Assert.True(gate.Allow, gate.Reason);
        return Assert.IsAssignableFrom<IQuotaReservationLease>(gate.Reservation);
    }

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

    [Fact]
    public async Task AvailableButProjectedBelowThreshold_ShouldWait()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 15.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new FixedHeadroomEstimator(10.0));

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("headroom", decision.Reason);
    }

    [Fact]
    public async Task InsufficientHeadroom_FallsBackToMemberWithMoreQuota()
    {
        var cls = FrontierClass(Sub(Claude), Sub(Codex));
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 15.0), new FakeProbe(Codex, 50.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new FixedHeadroomEstimator(10.0));

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task InsufficientHeadroom_FallsBackToPayPerApiMember()
    {
        var cls = FrontierClass(Sub(Claude), Api(Codex));
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 15.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new FixedHeadroomEstimator(10.0));

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task InsufficientHeadroom_UsesProbeResetForSuggestedRetryAt()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(2);
        var cls = FrontierClass(Sub(Claude));
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, new AgentQuotaSnapshot { AvailablePct = 15.0, ResetAt = resetAt })],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new FixedHeadroomEstimator(10.0));

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.Equal(resetAt, decision.SuggestedRetryAt);
    }

    [Fact]
    public async Task ReservedHeadroom_CapsConcurrentDispatchesAgainstCachedQuota()
    {
        var cls = FrontierClass(Sub(Claude));
        var probe = new FakeProbe(Claude, 50.0);
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var manager = BuildHeadroomManager([probe], opts, estimatedPctCost: 15.0);
        var router = new AgentClassRouter(
            [cls],
            [probe],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);

        var firstItem = MakeItem("frontier");
        var first = await router.ResolveAsync(firstItem, null, CancellationToken.None);
        var firstReservation = await ReserveAsync(manager, firstItem, first);
        var secondItem = MakeItem("frontier");
        var second = await router.ResolveAsync(secondItem, null, CancellationToken.None);
        var secondReservation = await ReserveAsync(manager, secondItem, second);
        var third = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Claude, first.Chosen!.Agent);
        Assert.Equal(Claude, second.Chosen!.Agent);
        Assert.Null(third.Chosen);
        Assert.True(third.ShouldWait);
        Assert.Contains("headroom", third.Reason);
        await firstReservation.ReleaseAsync(quotaMayHaveBeenConsumed: false);
        await secondReservation.ReleaseAsync(quotaMayHaveBeenConsumed: false);

        var afterReleaseItem = MakeItem("frontier");
        var afterRelease = await router.ResolveAsync(afterReleaseItem, null, CancellationToken.None);
        var afterReleaseReservation = await ReserveAsync(manager, afterReleaseItem, afterRelease);
        Assert.Equal(Claude, afterRelease.Chosen!.Agent);
        Assert.False(afterRelease.ShouldWait);
        await afterReleaseReservation.ReleaseAsync(quotaMayHaveBeenConsumed: false);
    }

    [Fact]
    public async Task ReservedHeadroom_RefreshFailureReleasesAfterCacheTtl()
    {
        var cls = FrontierClass(Sub(Claude));
        var probe = new ThrowingRefreshQuotaProbe(Claude, availablePct: 50.0);
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            QuotaCacheTtl = TimeSpan.FromMilliseconds(100),
        };
        var manager = BuildHeadroomManager([probe], opts, estimatedPctCost: 35.0);
        var router = new AgentClassRouter(
            [cls],
            [probe],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);

        var firstItem = MakeItem("frontier");
        var first = await router.ResolveAsync(firstItem, null, CancellationToken.None);
        var firstReservation = await ReserveAsync(manager, firstItem, first);

        await firstReservation.ReleaseAsync(quotaMayHaveBeenConsumed: true);

        var immediate = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Null(immediate.Chosen);
        Assert.True(immediate.ShouldWait);
        Assert.Contains("headroom", immediate.Reason);

        AgentRoutingDecision? afterTtl = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            afterTtl = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
            if (afterTtl.Chosen is not null)
                break;

            await Task.Delay(20);
        }

        var afterTtlItem = MakeItem("frontier");
        var afterTtlReservation = await ReserveAsync(manager, afterTtlItem, afterTtl!);
        Assert.Equal(Claude, afterTtl!.Chosen!.Agent);
        Assert.Equal(1, probe.RefreshCount);
        await afterTtlReservation.ReleaseAsync(quotaMayHaveBeenConsumed: false);
    }

    [Fact]
    public async Task ReservedHeadroom_UnknownRefreshRetainsReservationUntilCacheTtl()
    {
        var cls = FrontierClass(Sub(Claude));
        var probe = new RefreshingQuotaProbe(
            Claude,
            beforeRefreshAvailablePct: 50.0,
            afterRefreshAvailablePct: -1.0);
        var opts = new QuotaRouterOptions
        {
            MinQuotaPct = 10.0,
            QuotaCacheTtl = TimeSpan.FromMilliseconds(100),
        };
        var manager = BuildHeadroomManager([probe], opts, estimatedPctCost: 35.0);
        var router = new AgentClassRouter(
            [cls],
            [probe],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);

        var firstItem = MakeItem("frontier");
        var first = await router.ResolveAsync(firstItem, null, CancellationToken.None);
        var firstReservation = await ReserveAsync(manager, firstItem, first);

        await firstReservation.ReleaseAsync(quotaMayHaveBeenConsumed: true);

        var immediate = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
        Assert.Null(immediate.Chosen);
        Assert.True(immediate.ShouldWait);
        Assert.Contains("headroom", immediate.Reason);

        AgentRoutingDecision? afterTtl = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            afterTtl = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);
            if (afterTtl.Chosen is not null)
                break;

            await Task.Delay(20);
        }

        Assert.Equal(1, probe.RefreshCount);
        Assert.Equal(Claude, afterTtl!.Chosen!.Agent);
    }

    [Fact]
    public async Task ReservedHeadroom_IsSharedAcrossModelsForSameProjectAndAgent()
    {
        var projectId = new ProjectId("shared-project");
        var opusClass = new AgentClass
        {
            Id = "claude-opus",
            DisplayName = "Claude Opus",
            Members =
            [
                new AgentMembership
                {
                    Agent = Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    ModelId = "opus",
                },
            ],
        };
        var sonnetClass = new AgentClass
        {
            Id = "claude-sonnet",
            DisplayName = "Claude Sonnet",
            Members =
            [
                new AgentMembership
                {
                    Agent = Claude,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                    ModelId = "sonnet",
                },
            ],
        };
        var snapshot = new AgentQuotaSnapshot
        {
            AvailablePct = 60.0,
            PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                ["opus"] = new() { AvailablePct = 60.0 },
                ["sonnet"] = new() { AvailablePct = 60.0 },
            },
        };
        var probe = new FakeProbe(Claude, snapshot);
        var opts = new QuotaRouterOptions { MinQuotaPct = 10.0 };
        var manager = BuildHeadroomManager([probe], opts, estimatedPctCost: 45.0);
        var router = new AgentClassRouter(
            [opusClass, sonnetClass],
            [probe],
            opts,
            NullLogger<AgentClassRouter>.Instance,
            headroomManager: manager);

        var firstItem = MakeItem("claude-opus") with { ProjectId = projectId };
        var first = await router.ResolveAsync(
            firstItem,
            null,
            CancellationToken.None);
        var reservation = await ReserveAsync(manager, firstItem, first);

        try
        {
            var second = await router.ResolveAsync(
                MakeItem("claude-sonnet") with { ProjectId = projectId },
                null,
                CancellationToken.None);

            Assert.Equal("opus", first.Chosen!.ModelId);
            Assert.Null(second.Chosen);
            Assert.True(second.ShouldWait);
            Assert.Contains("headroom", second.Reason);
        }
        finally
        {
            await reservation.ReleaseAsync(quotaMayHaveBeenConsumed: false);
        }
    }

    [Fact]
    public async Task HeadroomEstimator_ReceivesProjectAgentAndModelScope()
    {
        var estimator = new CapturingHeadroomEstimator(estimatedPctCost: 10.0);
        var cls = FrontierClass(new AgentMembership
        {
            Agent = Claude,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
            ModelId = "claude-opus-4-7",
        });
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 50.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: estimator);

        var item = MakeItem("frontier") with { ProjectId = new ProjectId("scoped-project") };

        var decision = await router.ResolveAsync(item, null, CancellationToken.None);

        Assert.Equal(Claude, decision.Chosen!.Agent);
        var request = Assert.Single(estimator.Requests);
        Assert.Equal(new ProjectId("scoped-project"), request.ProjectId);
        Assert.Equal(Claude, request.Agent);
        Assert.Equal("claude-opus-4-7", request.ModelId);
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

    [Fact]
    public async Task PayPerApiFallback_IsNotRejectedByHeadroomEstimator()
    {
        var cls = FrontierClass(Sub(Claude), Api(Codex));
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 1.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new ThrowingHeadroomEstimator());

        var decision = await router.ResolveAsync(MakeItem("frontier"), null, CancellationToken.None);

        Assert.Equal(Codex, decision.Chosen!.Agent);
        Assert.Equal(AgentBilling.PayPerApi, decision.Chosen.Billing);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public async Task SubscriptionEstimatorException_FailsClosedToQuotaWait()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = new AgentClassRouter(
            [cls],
            [new FakeProbe(Claude, 50.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new ThrowingHeadroomEstimator());

        var decision = await router.ResolveAsync(
            MakeItem("frontier"),
            null,
            CancellationToken.None);

        Assert.Null(decision.Chosen);
        Assert.True(decision.ShouldWait);
        Assert.Contains("headroom", decision.Reason);
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
    public async Task ComputeEarliestExhaustedReset_PropagatesProbeFailures()
    {
        var cls = FrontierClass(Sub(Claude));
        var router = BuildRouter([cls], [new ThrowingQuotaProbe(Claude)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.ComputeEarliestExhaustedResetAsync(
                MakeItem("frontier"), null, CancellationToken.None));
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
    public async Task ComputeEarliestExhaustedReset_NoClassConfigured_ReturnsNull()
    {
        var router = BuildRouter([], []);
        var earliest = await router.ComputeEarliestExhaustedResetAsync(
            MakeItem(classId: null), MakeProject(defaultClass: null), CancellationToken.None);

        Assert.Null(earliest);
    }
}

internal sealed class ThrowingQuotaProbe : IAgentQuotaProbe
{
    public ThrowingQuotaProbe(AgentKind kind) => Kind = kind;

    public AgentKind Kind { get; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => throw new InvalidOperationException("quota probe failed");
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

internal sealed class FixedHeadroomEstimator : IQuotaHeadroomEstimator
{
    private readonly double _estimatedPctCost;

    public FixedHeadroomEstimator(double estimatedPctCost) => _estimatedPctCost = estimatedPctCost;

    public Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default)
        => Task.FromResult<QuotaHeadroomEstimate?>(new QuotaHeadroomEstimate(
            _estimatedPctCost,
            AverageTokensPerIteration: 100_000,
            SampledItemCount: 1,
            Source: "test",
            TrustedForEnforcement: true));
}

internal sealed class CapturingHeadroomEstimator : IQuotaHeadroomEstimator
{
    private readonly double _estimatedPctCost;

    public CapturingHeadroomEstimator(double estimatedPctCost) => _estimatedPctCost = estimatedPctCost;

    public List<QuotaHeadroomRequest> Requests { get; } = [];

    public Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult<QuotaHeadroomEstimate?>(new QuotaHeadroomEstimate(
            _estimatedPctCost,
            AverageTokensPerIteration: 100_000,
            SampledItemCount: 1,
            Source: "test",
            TrustedForEnforcement: true));
    }
}

internal sealed class ThrowingHeadroomEstimator : IQuotaHeadroomEstimator
{
    public Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default)
        => throw new InvalidOperationException("headroom estimator failed");
}
