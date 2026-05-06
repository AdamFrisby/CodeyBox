using CodeyBox.Agents.Claude;

namespace CodeyBox.Tests;

public sealed class ClaudeQuotaProbeRealShapeTests
{
    private const string CapturedShape = """
    {
      "plan_type": "max",
      "rate_limit": {
        "allowed": true,
        "limit_reached": false,
        "primary_window": {
          "used_percent": 20,
          "limit_window_seconds": 18000,
          "reset_after_seconds": 3600,
          "reset_at": 1778091218
        },
        "secondary_window": {
          "used_percent": 10,
          "limit_window_seconds": 604800,
          "reset_after_seconds": 500000,
          "reset_at": 1778605571
        }
      },
      "additional_rate_limits": [
        {
          "limit_name": "claude-sonnet-4-6",
          "metered_feature": "claude_sonnet",
          "rate_limit": {
            "allowed": true,
            "limit_reached": false,
            "primary_window": { "used_percent": 30, "limit_window_seconds": 18000, "reset_at": 1778091218 },
            "secondary_window": { "used_percent": 40, "limit_window_seconds": 604800, "reset_at": 1778605571 }
          }
        },
        {
          "limit_name": "claude-opus-4-7",
          "metered_feature": "claude_opus",
          "rate_limit": {
            "allowed": false,
            "limit_reached": true,
            "primary_window": { "used_percent": 100, "limit_window_seconds": 18000, "reset_at": 1778091218 },
            "secondary_window": { "used_percent": 95, "limit_window_seconds": 604800, "reset_at": 1778605571 }
          }
        }
      ]
    }
    """;

    [Fact]
    public void CapturedRollupShape_ParsesOverallAndPerModelLimits()
    {
        var snapshot = ClaudeQuotaProbe.ParseResponse(CapturedShape);

        Assert.Equal(80, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("claude-sonnet-4-6"));
        Assert.True(snapshot.PerModel.ContainsKey("claude-opus-4-7"));
        Assert.Equal(60, snapshot.PerModel["claude-sonnet-4-6"].AvailablePct);
        Assert.Equal(0, snapshot.PerModel["claude-opus-4-7"].AvailablePct);
        Assert.Equal("5h-rolling", snapshot.PerModel["claude-opus-4-7"].Window);
    }
}
