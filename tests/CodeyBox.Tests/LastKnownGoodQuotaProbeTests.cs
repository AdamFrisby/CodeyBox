using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="LastKnownGoodQuotaProbe"/>: a transient unknown
/// serves the most recent real reading; permanent / no-credential unknowns
/// discard it; staleness and the reading's own reset bound the substitution.
/// </summary>
public sealed class LastKnownGoodQuotaProbeTests
{
    private static AgentMembership Member(string? model = null) => new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        ModelId = model,
        QualityScore = 100,
    };

    private sealed class StubProbe : IAgentQuotaProbe
    {
        public AgentKind Kind => AgentKind.Claude;
        public AgentQuotaSnapshot Next { get; set; } = new() { AvailablePct = 100 };
        public Exception? ThrowOnCall { get; set; }

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => ThrowOnCall is not null
                ? Task.FromException<AgentQuotaSnapshot>(ThrowOnCall)
                : Task.FromResult(Next);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now;
        public Clock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    private static LastKnownGoodQuotaProbe Build(StubProbe inner, Clock clock, TimeSpan? staleness = null) =>
        new(inner,
            () => new LastKnownGoodQuotaOptions { MaxStaleness = staleness ?? TimeSpan.FromMinutes(5) },
            NullLogger<LastKnownGoodQuotaProbe>.Instance,
            clock);

    [Fact]
    public async Task TransientUnknown_AfterGoodReading_ServesStale()
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 71 } };
        var lkg = Build(inner, clock);

        Assert.Equal(71, (await lkg.GetAvailabilityAsync(Member(), default)).AvailablePct);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");
        clock.Advance(TimeSpan.FromMinutes(1));
        var stale = await lkg.GetAvailabilityAsync(Member(), default);

        Assert.True(stale.IsKnown);
        Assert.Equal(71, stale.AvailablePct);
        Assert.Contains("stale", stale.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(QuotaUnknownReason.Permanent)]
    [InlineData(QuotaUnknownReason.NoCredential)]
    public async Task NonTransientUnknown_DiscardsRetained(QuotaUnknownReason reason)
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 71 } };
        var lkg = Build(inner, clock);
        await lkg.GetAvailabilityAsync(Member(), default);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(reason, "gone");
        var result = await lkg.GetAvailabilityAsync(Member(), default);

        Assert.False(result.IsKnown);
        Assert.Equal(reason, result.Unknown);

        // And it stays dropped: a subsequent transient unknown has nothing to serve.
        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");
        Assert.False((await lkg.GetAvailabilityAsync(Member(), default)).IsKnown);
    }

    [Fact]
    public async Task StalenessExceeded_FallsThroughToUnknown()
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 71 } };
        var lkg = Build(inner, clock, staleness: TimeSpan.FromMinutes(5));
        await lkg.GetAvailabilityAsync(Member(), default);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.False((await lkg.GetAvailabilityAsync(Member(), default)).IsKnown);
    }

    [Fact]
    public async Task ResetPassed_DropsRetained_SoRecoveredWindowIsNotGatedForever()
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        // A retained 0% exhaustion whose window resets in 10 minutes.
        var inner = new StubProbe
        {
            Next = new AgentQuotaSnapshot { AvailablePct = 0, ResetAt = clock.GetUtcNow().AddMinutes(10) },
        };
        var lkg = Build(inner, clock, staleness: TimeSpan.FromHours(1));
        await lkg.GetAvailabilityAsync(Member(), default);

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");

        // Before reset: still served (stale 0%).
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await lkg.GetAvailabilityAsync(Member(), default)).IsKnown);

        // After reset: dropped (don't gate a window that has reset).
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False((await lkg.GetAvailabilityAsync(Member(), default)).IsKnown);
    }

    [Fact]
    public async Task GoodReading_OverwritesRetained()
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 71 } };
        var lkg = Build(inner, clock);
        await lkg.GetAvailabilityAsync(Member(), default);

        inner.Next = new AgentQuotaSnapshot { AvailablePct = 42 };
        Assert.Equal(42, (await lkg.GetAvailabilityAsync(Member(), default)).AvailablePct);

        // The newer 42 is what gets retained, not the old 71.
        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(42, (await lkg.GetAvailabilityAsync(Member(), default)).AvailablePct);
    }

    [Fact]
    public async Task PerModel_RetentionIsIndependent()
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 71 } };
        var lkg = Build(inner, clock);
        await lkg.GetAvailabilityAsync(Member("model-a"), default);   // retains 71 for model-a only

        inner.Next = AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient, "5xx");

        Assert.Equal(71, (await lkg.GetAvailabilityAsync(Member("model-a"), default)).AvailablePct);
        Assert.False((await lkg.GetAvailabilityAsync(Member("model-b"), default)).IsKnown); // nothing retained
    }

    [Fact]
    public async Task InnerThrow_IsTreatedAsTransient_ServesStale()
    {
        var clock = new Clock(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var inner = new StubProbe { Next = new AgentQuotaSnapshot { AvailablePct = 71 } };
        var lkg = Build(inner, clock);
        await lkg.GetAvailabilityAsync(Member(), default);

        inner.ThrowOnCall = new HttpRequestException("boom");
        var stale = await lkg.GetAvailabilityAsync(Member(), default);

        Assert.True(stale.IsKnown);
        Assert.Equal(71, stale.AvailablePct);
    }
}
