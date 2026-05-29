using CodeyBox.Agents.Cursor;

namespace CodeyBox.Tests;

public sealed class CursorQuotaProbeRealShapeTests
{
    [Fact]
    public void CapturedDashboardUsageShape_ParsesOverallAndPerModelLimits()
    {
        var capturedShape = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "cursor-dashboard-usage.redacted.json"));

        var snapshot = CursorQuotaProbe.ParseResponse(capturedShape);

        // remaining/limit = 687/2000 = 34.35%, matching the response's
        // displayMessage ("used 66% of your included usage"). NOT 100-totalPercentUsed.
        Assert.Equal(34.35, snapshot.AvailablePct, precision: 2);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1782444007000),
            snapshot.ResetAt);
        Assert.True(snapshot.PerModel.ContainsKey("composer-2.5"));
        // auto bucket (100-autoPercentUsed=91.25) is capped by the overall 34.35.
        Assert.Equal(34.35, snapshot.PerModel["composer-2.5"].AvailablePct, precision: 2);
        Assert.Contains("auto", snapshot.PerModel["composer-2.5"].Window);
        Assert.False(snapshot.PerModel.ContainsKey("cursor-auto"));
        Assert.False(snapshot.PerModel.ContainsKey("cursor-api"));
    }
}
