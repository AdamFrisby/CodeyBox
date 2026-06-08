using CodeyBox.Agents.Cursor;

namespace CodeyBox.Tests;

public sealed class CursorQuotaProbeRealShapeTests
{
    [Fact]
    public void CapturedDashboardUsageShape_OutOfUsage_Returns0PctAndCycleEndReset()
    {
        // Captured live 2026-06-04 from
        // DashboardService/GetCurrentPeriodUsage; account is at 100% across
        // every percent dimension and the displayMessage reads
        // "You've hit your usage limit". availablePct must be 0 (NOT -1) so
        // the router gates cursor below minQuotaPct.
        var capturedShape = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "cursor-dashboard-usage.redacted.json"));

        var snapshot = CursorQuotaProbe.ParseResponse(capturedShape);

        Assert.Equal(0.0, snapshot.AvailablePct, precision: 5);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1782444007000),
            snapshot.ResetAt);
        Assert.True(snapshot.PerModel.ContainsKey("composer-2.5"));
        Assert.Equal(0.0, snapshot.PerModel["composer-2.5"].AvailablePct, precision: 5);
        // Auto bucket is at 0% just like overall, so no cap is applied — the
        // Window string stays "auto" rather than "auto (capped by overall)".
        Assert.Contains("auto", snapshot.PerModel["composer-2.5"].Window);
        Assert.DoesNotContain("capped", snapshot.PerModel["composer-2.5"].Window);
        Assert.False(snapshot.PerModel.ContainsKey("cursor-auto"));
        Assert.False(snapshot.PerModel.ContainsKey("cursor-api"));
    }

    [Fact]
    public void SyntheticHasUsageShape_TakesMostConstrainedPercentDimension()
    {
        // No remaining/limit field on the response — the real shape doesn't
        // emit one. headline = 100 - max(total=40, auto=30, api=10) = 60.
        const string body = """
        {
          "billingCycleEnd": "1782444007000",
          "planUsage": {
            "totalSpend": 800,
            "includedSpend": 800,
            "limit": 2000,
            "remainingBonus": true,
            "autoPercentUsed": 30,
            "apiPercentUsed": 10,
            "totalPercentUsed": 40
          },
          "displayMessage": "You've used 40% of your included usage",
          "enabled": true,
          "autoBucketModels": ["composer-2.5"]
        }
        """;

        var snapshot = CursorQuotaProbe.ParseResponse(body);

        Assert.Equal(60.0, snapshot.AvailablePct, precision: 5);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1782444007000),
            snapshot.ResetAt);
        Assert.True(snapshot.PerModel.ContainsKey("composer-2.5"));
        // auto bucket (100-30=70) capped by overall 60.
        Assert.Equal(60.0, snapshot.PerModel["composer-2.5"].AvailablePct, precision: 5);
    }
}
