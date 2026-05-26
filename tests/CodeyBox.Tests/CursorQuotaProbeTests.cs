using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CursorQuotaProbe"/>. The probe is deliberately
/// minimal — Cursor exposes no documented usage endpoint — and must always
/// report Unknown so the router's UnknownPolicy=UseObservedFailures applies
/// reactive back-pressure via <see cref="CursorQuotaFailureDetector"/>.
/// </summary>
public sealed class CursorQuotaProbeTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Cursor,
        Billing = AgentBilling.Subscription,
        ModelId = "composer-2.5",
        QualityScore = 98,
    };

    [Fact]
    public void Kind_IsCursor()
    {
        var probe = new CursorQuotaProbe();
        Assert.Equal(AgentKind.Cursor, probe.Kind);
    }

    [Fact]
    public async Task GetAvailabilityAsync_AlwaysReturnsUnknown()
    {
        var probe = new CursorQuotaProbe();
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0, "Cursor probe must report Unknown so router falls onto UnknownPolicy");
        Assert.Equal("no probe endpoint", snap.Notes);
    }

    [Fact]
    public async Task GetAvailabilityAsync_HasEmptyPerModelMap()
    {
        var probe = new CursorQuotaProbe();
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Empty(snap.PerModel);
    }
}
