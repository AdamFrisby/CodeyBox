using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Guards the detection order agent-turn &lt; item-stale &lt; sandbox wall
/// clock at configuration load. The 2026-09-08 incident parked a healthy
/// 1h44m turn because the item-stale window no longer bounded a real turn
/// duration from below; a future edit must not re-invert the order silently.
/// </summary>
public sealed class WorkerProgressWatchdogTimeoutOrderingTests
{
    [Fact]
    public void Defaults_PreserveDetectionOrder()
    {
        // Out-of-the-box config: per-turn progress window (60m) < item-stale
        // window (75m) < sandbox wall-clock backstop (6h).
        var opts = new WorkerProgressWatchdogOptions();
        opts.Validate();
    }

    [Fact]
    public void Validate_IncidentInterimValues_Pass()
    {
        // The interim host mitigation values: 45m turn budget < 4h stale < 6h
        // wall clock. This triple is the regression anchor for the incident.
        WorkerProgressWatchdogOptions.ValidateTimeoutOrdering(
            TimeSpan.FromMinutes(45),
            TimeSpan.FromHours(4),
            TimeSpan.FromHours(6));
    }

    [Fact]
    public void Validate_ItemStaleAtOrBelowPerTurnBudget_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkerProgressWatchdogOptions.ValidateTimeoutOrdering(
                TimeSpan.FromMinutes(60),
                TimeSpan.FromMinutes(60),
                TimeSpan.FromHours(6)));
        Assert.Contains("ItemStaleTimeout", ex.Message);
        Assert.Contains("per-turn", ex.Message);
    }

    [Fact]
    public void Validate_ItemStaleAtOrAboveWallClock_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkerProgressWatchdogOptions.ValidateTimeoutOrdering(
                TimeSpan.FromMinutes(45),
                TimeSpan.FromHours(6),
                TimeSpan.FromHours(6)));
        Assert.Contains("ItemStaleTimeout", ex.Message);
        Assert.Contains("wall clock", ex.Message);
    }

    [Fact]
    public void Validate_DisabledLegs_Skipped()
    {
        // Zero is the "disable this detector" sentinel on both legs; a
        // disabled detector has no order to preserve. Null wall clock means
        // no backstop configured.
        WorkerProgressWatchdogOptions.ValidateTimeoutOrdering(
            TimeSpan.Zero, TimeSpan.FromMinutes(30), TimeSpan.FromHours(6));
        WorkerProgressWatchdogOptions.ValidateTimeoutOrdering(
            TimeSpan.FromMinutes(60), TimeSpan.Zero, TimeSpan.FromHours(6));
        WorkerProgressWatchdogOptions.ValidateTimeoutOrdering(
            TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(75), wallClock: null);
    }

    [Fact]
    public void Validate_GlobalItemStaleBelowProgressTimeout_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(60),
            ItemStaleTimeout = TimeSpan.FromMinutes(30),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ItemStaleTimeout", ex.Message);
    }

    [Fact]
    public void Validate_GlobalItemStaleAtOrAboveWallClock_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleTimeout = TimeSpan.FromHours(7),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ItemStaleTimeout", ex.Message);
        Assert.Contains("wall clock", ex.Message);
    }

    [Fact]
    public void Validate_PerAgentBothSetInverted_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(8),
                    ItemStaleTimeout = TimeSpan.FromHours(6),
                },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("crock", ex.Message);
        Assert.Contains("ItemStaleTimeout", ex.Message);
    }

    [Fact]
    public void Validate_ShippedCrockOverride_Passes()
    {
        // The shipped appsettings.json crock override (6h progress, 8h stale)
        // preserves the order; per-agent batch-latency overrides are exempt
        // from the wall-clock leg.
        var opts = new WorkerProgressWatchdogOptions
        {
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(6),
                    ItemStaleTimeout = TimeSpan.FromHours(8),
                },
            },
        };
        opts.Validate();
        Assert.Equal(TimeSpan.FromHours(6), opts.ResolveProgressTimeout(AgentKind.Crock));
        Assert.Equal(TimeSpan.FromHours(8), opts.ResolveItemStaleTimeout(AgentKind.Crock));
    }
}
