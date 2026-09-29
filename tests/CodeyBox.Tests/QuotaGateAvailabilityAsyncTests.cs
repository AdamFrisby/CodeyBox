using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins QuotaGateAvailability.EvaluateAsync's failure-store branch to the same
/// semantics AgentClassRouter.EvaluateGateAsync uses: observed failures gate
/// only when the current quota is unknown AND UnknownPolicy is
/// UseObservedFailures. A previous version always consulted the failure store,
/// so a stale recent runtime failure could deny an otherwise-healthy member —
/// diverging from the dispatch path that consumers like
/// AllQuotasExhaustedCondition are wired through.
/// </summary>
public sealed class QuotaGateAvailabilityAsyncTests
{
    private static readonly AgentKind Claude = new("claude");

    private static AgentMembership Member() => new()
    {
        Agent = Claude,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    private static QuotaGateAvailability Build(
        QuotaUnknownPolicy unknownPolicy,
        IQuotaFailureStore? failureStore,
        TimeSpan? observedFailureWindow = null) =>
        new QuotaGateAvailability(
            new QuotaGatePolicy(new QuotaRouterOptions
            {
                // Zero floors so the unknown branch reaches UnknownPolicy:
                // these tests pin the failure-store consultation semantics,
                // not the reserve floor (a non-zero floor fails closed first).
                MinQuotaPct = 0,
                StartFloorPct = 0,
                EndFloorPct = 0,
                UnknownPolicy = unknownPolicy,
            }),
            failureStore,
            observedFailureWindow ?? TimeSpan.FromMinutes(10));

    [Fact]
    public async Task EvaluateAsync_KnownHealthyQuota_IgnoresRecentObservedFailure()
    {
        // A recent observed failure exists for the member, BUT the live snapshot
        // shows a known-healthy 50% available. The dispatch path doesn't consult
        // the failure store when quota is known — the abstraction must match.
        var now = DateTimeOffset.UtcNow;
        var failures = new InMemoryQuotaFailureStore();
        await failures.RecordAsync(Claude, modelId: null, QuotaFailureKind.LimitReached, now);
        var gate = Build(QuotaUnknownPolicy.UseObservedFailures, failures);

        var snapshot = new AgentQuotaSnapshot { AvailablePct = 50 };
        Assert.True((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_UnknownQuota_RecentFailureUnderUseObservedFailures_Denies()
    {
        // Live snapshot is unknown (-1) AND UnknownPolicy=UseObservedFailures AND
        // a recent failure exists — dispatch denies, so the gate must too.
        var now = DateTimeOffset.UtcNow;
        var failures = new InMemoryQuotaFailureStore();
        await failures.RecordAsync(Claude, modelId: null, QuotaFailureKind.LimitReached, now);
        var gate = Build(QuotaUnknownPolicy.UseObservedFailures, failures);

        var snapshot = new AgentQuotaSnapshot { AvailablePct = -1 };
        Assert.False((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_UnknownQuota_NoRecentFailure_AllowsUnderUseObservedFailures()
    {
        // No recent failure recorded — UseObservedFailures falls through to allow.
        var now = DateTimeOffset.UtcNow;
        var failures = new InMemoryQuotaFailureStore();
        var gate = Build(QuotaUnknownPolicy.UseObservedFailures, failures);

        var snapshot = new AgentQuotaSnapshot { AvailablePct = -1 };
        Assert.True((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_FailOpenPolicy_RecentFailureNotConsultedEvenWhenUnknown()
    {
        // UnknownPolicy=FailOpen means dispatch never consults the failure store.
        // A recent failure must NOT change the outcome — fail-open allows.
        var now = DateTimeOffset.UtcNow;
        var failures = new InMemoryQuotaFailureStore();
        await failures.RecordAsync(Claude, modelId: null, QuotaFailureKind.LimitReached, now);
        var gate = Build(QuotaUnknownPolicy.FailOpen, failures);

        var snapshot = new AgentQuotaSnapshot { AvailablePct = -1 };
        Assert.True((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_FailCautiousPolicy_DeniesWithoutConsultingStore()
    {
        // UnknownPolicy=FailCautious denies unknown regardless of failure store —
        // the store branch is not taken because the policy isn't UseObservedFailures.
        var now = DateTimeOffset.UtcNow;
        var failures = new ThrowingFailureStore();
        var gate = Build(QuotaUnknownPolicy.FailCautious, failures);

        var snapshot = new AgentQuotaSnapshot { AvailablePct = -1 };
        // ThrowingFailureStore would throw if EvaluateAsync touched it.
        Assert.False((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_NoFailureStoreWired_FallsBackToPolicyOnly()
    {
        // The default constructor (no failure store) should never deny on
        // observed failures — it has nothing to consult. Unknown + UseObservedFailures
        // with no store available must allow (same as "no recent failure").
        var now = DateTimeOffset.UtcNow;
        var gate = new QuotaGateAvailability(new QuotaGatePolicy(new QuotaRouterOptions
        {
            // Zero floors so the unknown branch reaches UnknownPolicy (see above).
            MinQuotaPct = 0,
            StartFloorPct = 0,
            EndFloorPct = 0,
            UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
        }));

        var snapshot = new AgentQuotaSnapshot { AvailablePct = -1 };
        Assert.True((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_ZeroWindow_SkipsFailureStoreConsult()
    {
        // An observedFailureWindow of zero is the disable signal — the gate must
        // not even consult the store. ThrowingFailureStore proves it's untouched.
        var now = DateTimeOffset.UtcNow;
        var gate = new QuotaGateAvailability(
            new QuotaGatePolicy(new QuotaRouterOptions
            {
                // Zero floors so the unknown branch reaches UnknownPolicy (see above).
                MinQuotaPct = 0,
                StartFloorPct = 0,
                EndFloorPct = 0,
                UnknownPolicy = QuotaUnknownPolicy.UseObservedFailures,
            }),
            new ThrowingFailureStore(),
            observedFailureWindow: TimeSpan.Zero);

        var snapshot = new AgentQuotaSnapshot { AvailablePct = -1 };
        Assert.True((await gate.EvaluateAsync(Member(), snapshot, now)).Allow);
    }

    [Fact]
    public async Task EvaluateAsync_ItemsFloorSeesMeasuredBurn()
    {
        // With a work-denominated floor configured, the advisory gate must see
        // the same measured burn the dispatch path supplies — otherwise it
        // reports allowed for a member commit-time dispatch refuses.
        var now = DateTimeOffset.UtcNow;
        var options = new QuotaRouterOptions
        {
            MinQuotaPct = 1.0,
            StartFloorPct = 1.0,
            EndFloorPct = 1.0,
            MinQuotaItems = 10.0,
            DispatchReservationBurnMinSamples = 3,
        };
        var burn = new AgentBurnEstimate
        {
            AvgBurnPctPerItem = 0.6,
            SampleCount = 8,
            Status = AgentBurnEstimateStatus.Measured,
        };
        var snapshot = new AgentQuotaSnapshot { AvailablePct = 3 };

        var noEstimator = new QuotaGateAvailability(new QuotaGatePolicy(options));
        Assert.True((await noEstimator.EvaluateAsync(Member(), snapshot, now, false, null)).Allow);

        var withEstimator = new QuotaGateAvailability(
            new QuotaGatePolicy(options), failureStore: null, TimeSpan.Zero,
            burnEstimator: new StubBurnEstimator(burn));
        var verdict = await withEstimator.EvaluateAsync(Member(), snapshot, now, false, null);
        Assert.False(verdict.Allow);
        // 10 items x 0.6%/item = 6.0-point floor vs 3.0% available.
        var refusalReason = Assert.IsType<string>(verdict.RefusalReason);
        Assert.Contains("3.0% < 6.0%", refusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_CancellationPropagatesFromBurnEstimator()
    {
        var gate = new QuotaGateAvailability(
            new QuotaGatePolicy(new QuotaRouterOptions()),
            failureStore: null, TimeSpan.Zero,
            burnEstimator: new CancellingBurnEstimator());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.EvaluateAsync(Member(), new AgentQuotaSnapshot { AvailablePct = 50 }, DateTimeOffset.UtcNow, false, null, cts.Token));
    }

    private sealed class StubBurnEstimator(AgentBurnEstimate estimate) : IAgentBurnEstimator
    {
        public Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default) =>
            Task.FromResult(estimate);
    }

    private sealed class CancellingBurnEstimator : IAgentBurnEstimator
    {
        public Task<AgentBurnEstimate> GetEstimateAsync(AgentKind agent, CancellationToken ct = default) =>
            Task.FromCanceled<AgentBurnEstimate>(ct);
    }

    private sealed class InMemoryQuotaFailureStore : IQuotaFailureStore
    {
        private readonly List<QuotaFailureObservation> _observations = [];

        public Task RecordAsync(
            AgentKind agent,
            string? modelId,
            QuotaFailureKind kind,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt));
            return Task.CompletedTask;
        }

        public Task RecordForProjectAsync(
            AgentKind agent,
            string? modelId,
            ProjectId projectId,
            QuotaFailureKind kind,
            DateTimeOffset observedAt,
            CancellationToken ct = default)
        {
            _observations.Add(new QuotaFailureObservation(agent, modelId, kind, observedAt, projectId));
            return Task.CompletedTask;
        }

        public async Task<bool> HasRecentAsync(
            AgentKind agent,
            string? modelId,
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            await GetMostRecentAsync(agent, modelId, window, now, ct) is not null;

        public Task<DateTimeOffset?> GetMostRecentAsync(
            AgentKind agent,
            string? modelId,
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            var latest = _observations
                .Where(o => o.Agent == agent
                    && string.Equals(o.ModelId, modelId, StringComparison.Ordinal)
                    && o.ObservedAt <= now
                    && now - o.ObservedAt <= window)
                .Select(o => (DateTimeOffset?)o.ObservedAt)
                .Max();
            return Task.FromResult(latest);
        }

        public Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(
            TimeSpan window,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuotaFailureObservation>>(_observations);

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingFailureStore : IQuotaFailureStore
    {
        public Task RecordAsync(AgentKind agent, string? modelId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordForProjectAsync(AgentKind agent, string? modelId, ProjectId projectId, QuotaFailureKind kind, DateTimeOffset observedAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> HasRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default) =>
            throw new InvalidOperationException("failure store consulted when it should not have been");

        public Task<DateTimeOffset?> GetMostRecentAsync(AgentKind agent, string? modelId, TimeSpan window, DateTimeOffset now, CancellationToken ct = default) =>
            throw new InvalidOperationException("failure store consulted when it should not have been");

        public Task<IReadOnlyList<QuotaFailureObservation>> ListRecentAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuotaFailureObservation>>([]);

        public Task PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
