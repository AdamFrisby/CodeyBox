using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Resolution-seam tests for member-key probe resolution: a probe declares which
/// members it serves via <see cref="IAgentQuotaProbe.Handles"/>, and
/// <see cref="AgentQuotaProbeCatalog.ResolveSubscriptionProbe"/> picks by
/// <see cref="AgentQuotaMemberKey"/> instead of by kind.
/// </summary>
public sealed class AgentQuotaProbeResolutionTests
{
    private static AgentMembership Member(AgentKind agent, string? modelId, int score = 50) =>
        new()
        {
            Agent = agent,
            Billing = AgentBilling.Subscription,
            ModelId = modelId,
            QualityScore = score,
        };

    private static AgentClass SoloClass(params AgentMembership[] members) =>
        new() { Id = "frontier", DisplayName = "Frontier", Members = members };

    private static WorkItem Item() =>
        new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier",
        };

    [Fact]
    public void DefaultProbe_ServesEveryMemberOfItsKind()
    {
        // Regression guard: a probe with no Handles override keeps the
        // historical per-kind behaviour — it serves every member of its kind,
        // whatever model each member routes to.
        var probe = new DefaultProbe(AgentKind.Copilot, 75);
        var probes = AgentQuotaProbeCatalog.BuildSubscriptionProbes([probe]);
        var log = NullLogger.Instance;

        var forModelA = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
            probes, Member(AgentKind.Copilot, "model-a"), log);
        var forModelB = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
            probes, Member(AgentKind.Copilot, "model-b"), log);
        var forDefaultModel = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
            probes, Member(AgentKind.Copilot, null), log);

        Assert.Same(probe, forModelA.Probe);
        Assert.Same(probe, forModelB.Probe);
        Assert.Same(probe, forDefaultModel.Probe);
        Assert.Null(forModelA.Conflict);
    }

    [Fact]
    public async Task SingleModelClaim_ServesClaimedMember_AndIsNotConsultedForOtherModel()
    {
        // The Copilot case from the design: one probe claims a single
        // (Agent, ModelId) pair; another model of the same agent falls back to
        // the per-kind probe, which must be the only probe consulted for it.
        var claimed = new ModelClaimProbe(AgentKind.Copilot, "model-a", 90);
        var perKind = new DefaultProbe(AgentKind.Copilot, 40);
        var probes = AgentQuotaProbeCatalog.BuildSubscriptionProbes([claimed, perKind]);
        var log = NullLogger.Instance;

        var claimedResolution = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
            probes, Member(AgentKind.Copilot, "model-a"), log);
        Assert.Same(claimed, claimedResolution.Probe);
        await claimedResolution.Probe!.GetAvailabilityAsync(
            Member(AgentKind.Copilot, "model-a"), CancellationToken.None);

        var otherResolution = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
            probes, Member(AgentKind.Copilot, "model-b"), log);
        Assert.Same(perKind, otherResolution.Probe);
        await otherResolution.Probe!.GetAvailabilityAsync(
            Member(AgentKind.Copilot, "model-b"), CancellationToken.None);

        Assert.Equal(1, claimed.AvailabilityCalls);
        Assert.Equal(1, perKind.AvailabilityCalls);
    }

    [Fact]
    public async Task SameAgentDifferentModels_ResolveToDifferentProbes_EndToEnd()
    {
        // Two members of the SAME agent with different models are metered by
        // different probes: model-a's probe reports exhaustion while model-b's
        // probe has headroom, so the router skips model-a and picks model-b even
        // though model-a scores higher.
        var probeA = new ModelClaimProbe(AgentKind.Copilot, "model-a", 0);
        var probeB = new ModelClaimProbe(AgentKind.Copilot, "model-b", 90);

        var router = new AgentClassRouter(
            [SoloClass(Member(AgentKind.Copilot, "model-a", score: 100), Member(AgentKind.Copilot, "model-b", score: 99))],
            [probeA, probeB],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal("model-b", decision.Chosen!.ModelId);
        Assert.Equal(1, probeA.AvailabilityCalls);
        Assert.Equal(1, probeB.AvailabilityCalls);
    }

    [Fact]
    public void SpecificClaim_WinsOverBroadClaim_RegardlessOfOrder()
    {
        var broad = new DefaultProbe(AgentKind.Copilot, 10);
        var specific = new ModelClaimProbe(AgentKind.Copilot, "model-a", 90);
        var log = NullLogger.Instance;
        var member = Member(AgentKind.Copilot, "model-a");

        foreach (var probes in new[]
                 {
                     AgentQuotaProbeCatalog.BuildSubscriptionProbes([broad, specific]),
                     AgentQuotaProbeCatalog.BuildSubscriptionProbes([specific, broad]),
                 })
        {
            var resolution = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(probes, member, log);
            Assert.Same(specific, resolution.Probe);
            Assert.Null(resolution.Conflict);
        }
    }

    [Fact]
    public void EquallySpecificClaims_ProduceErrorLogAndUnknownResult()
    {
        // Two plugins both claiming (copilot, model-a) is a configuration
        // error: resolution must be loud (Error naming both probes and the key)
        // and fail closed (Unknown) rather than pick an arbitrary winner.
        var byok = new ByokClaimProbe(AgentKind.Copilot, "model-a", 90);
        var subscription = new SubscriptionClaimProbe(AgentKind.Copilot, "model-a", 10);
        var member = Member(AgentKind.Copilot, "model-a");

        foreach (var probes in new[]
                 {
                     AgentQuotaProbeCatalog.BuildSubscriptionProbes([byok, subscription]),
                     AgentQuotaProbeCatalog.BuildSubscriptionProbes([subscription, byok]),
                 })
        {
            var log = new CapturingLogger();
            var resolution = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(probes, member, log);

            Assert.Null(resolution.Probe);
            Assert.NotNull(resolution.Conflict);
            Assert.Equal(
                [typeof(ByokClaimProbe).FullName + " (kind copilot)", typeof(SubscriptionClaimProbe).FullName + " (kind copilot)"],
                resolution.Conflict!.ProbeNames);

            var snapshot = AgentQuotaProbeCatalog.ConflictUnknownSnapshot(resolution.Conflict);
            Assert.False(snapshot.IsKnown);
            Assert.NotNull(snapshot.Unknown);

            var error = Assert.Single(log.Lines, l => l.Level == LogLevel.Error);
            Assert.Contains(typeof(ByokClaimProbe).FullName!, error.Message, StringComparison.Ordinal);
            Assert.Contains(typeof(SubscriptionClaimProbe).FullName!, error.Message, StringComparison.Ordinal);
            Assert.Contains("copilot", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("model-a", error.Message, StringComparison.Ordinal);

            // Neither contender was consulted: no reading backs the Unknown.
            Assert.Equal(0, byok.AvailabilityCalls);
            Assert.Equal(0, subscription.AvailabilityCalls);
        }
    }

    [Fact]
    public async Task ConflictedMember_FailsClosedThroughRouter()
    {
        // End to end: with two equally specific claims and a FailCautious
        // unknown policy, the conflicted member is gated (fail closed) instead
        // of being served by either contender.
        var router = new AgentClassRouter(
            [SoloClass(Member(AgentKind.Copilot, "model-a", score: 100))],
            [new ByokClaimProbe(AgentKind.Copilot, "model-a", 90), new SubscriptionClaimProbe(AgentKind.Copilot, "model-a", 90)],
            new QuotaRouterOptions { MinQuotaPct = 10, UnknownPolicy = QuotaUnknownPolicy.FailCautious },
            NullLogger<AgentClassRouter>.Instance);

        var decision = await router.ResolveAsync(Item(), null, CancellationToken.None);

        Assert.Null(decision.Chosen);
    }

    [Fact]
    public void Resolution_IsIndependentOfRegistrationOrder()
    {
        var specific = new ModelClaimProbe(AgentKind.Copilot, "model-a", 90);
        var broad = new DefaultProbe(AgentKind.Copilot, 40);
        var log = NullLogger.Instance;

        foreach (var modelId in new[] { "model-a", "model-b" })
        {
            var member = Member(AgentKind.Copilot, modelId);
            var first = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
                AgentQuotaProbeCatalog.BuildSubscriptionProbes([specific, broad]), member, log);
            var second = AgentQuotaProbeCatalog.ResolveSubscriptionProbe(
                AgentQuotaProbeCatalog.BuildSubscriptionProbes([broad, specific]), member, log);

            Assert.Same(first.Probe, second.Probe);
            Assert.Equal(first.Conflict, second.Conflict);
        }
    }

    [Fact]
    public void LastKnownGoodDecorator_PreservesInnerClaim()
    {
        // Production registers every probe wrapped in LastKnownGoodQuotaProbe;
        // the decorator must forward Handles so a narrowed inner probe keeps its
        // narrowed claim through the wrapper.
        var claimed = new ModelClaimProbe(AgentKind.Copilot, "model-a", 90);
        var wrapped = new LastKnownGoodQuotaProbe(
            claimed,
            () => new LastKnownGoodQuotaOptions(),
            log: null,
            timeProvider: TimeProvider.System);
        var perKind = new DefaultProbe(AgentKind.Copilot, 40);
        var probes = AgentQuotaProbeCatalog.BuildSubscriptionProbes([wrapped, perKind]);
        var log = NullLogger.Instance;

        Assert.Same(
            wrapped,
            AgentQuotaProbeCatalog.ResolveSubscriptionProbe(probes, Member(AgentKind.Copilot, "model-a"), log).Probe);
        Assert.Same(
            perKind,
            AgentQuotaProbeCatalog.ResolveSubscriptionProbe(probes, Member(AgentKind.Copilot, "model-b"), log).Probe);
    }

    /// <summary>Probe with no Handles override: exercises the interface default.</summary>
    private sealed class DefaultProbe(AgentKind kind, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public int AvailabilityCalls { get; private set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            AvailabilityCalls++;
            return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });
        }
    }

    /// <summary>Probe claiming exactly one (Agent, ModelId) pair.</summary>
    private sealed class ModelClaimProbe(AgentKind kind, string modelId, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public int AvailabilityCalls { get; private set; }

        public bool Handles(AgentQuotaMemberKey key) =>
            key.Agent == Kind && string.Equals(key.ModelId, modelId, StringComparison.Ordinal);

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            AvailabilityCalls++;
            return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });
        }
    }

    private sealed class ByokClaimProbe(AgentKind kind, string modelId, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public int AvailabilityCalls { get; private set; }

        public bool Handles(AgentQuotaMemberKey key) =>
            key.Agent == Kind && string.Equals(key.ModelId, modelId, StringComparison.Ordinal);

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            AvailabilityCalls++;
            return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });
        }
    }

    private sealed class SubscriptionClaimProbe(AgentKind kind, string modelId, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public int AvailabilityCalls { get; private set; }

        public bool Handles(AgentQuotaMemberKey key) =>
            key.Agent == Kind && string.Equals(key.ModelId, modelId, StringComparison.Ordinal);

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        {
            AvailabilityCalls++;
            return Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
