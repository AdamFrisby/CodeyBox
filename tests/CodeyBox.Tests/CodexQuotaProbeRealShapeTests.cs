using CodeyBox.Agents.Codex;

namespace CodeyBox.Tests;

public sealed class CodexQuotaProbeRealShapeTests
{
    [Fact]
    public async Task CapturedWhamUsageShape_ParsesOverallAndPerModelLimits()
    {
        var capturedWhamUsageShape = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "codex-wham-usage.redacted.json"));

        var snapshot = CodexQuotaProbe.ParseResponse(capturedWhamUsageShape);

        // Overall account is at 63% available (the most-constrained window in the
        // top-level rate_limit). Per-model buckets must be capped by this — even
        // though the per-model windows alone show 100% available, the account
        // can deny calls account-wide, so per-model availability tracks overall.
        Assert.Equal(63, snapshot.AvailablePct);
        Assert.True(snapshot.PerModel.ContainsKey("GPT-5.3-Codex-Spark"));
        Assert.Equal(63, snapshot.PerModel["GPT-5.3-Codex-Spark"].AvailablePct);
        // ParseResponse maps the raw shape only; no routing-alias key is
        // synthesised at parse time (aliasing onto a configured model is a
        // member-gate concern, sourced from config).
        Assert.False(snapshot.PerModel.ContainsKey("gpt-5.5"));
        Assert.Contains("capped by overall", snapshot.PerModel["GPT-5.3-Codex-Spark"].Window);
        Assert.NotNull(snapshot.ResetAt);
    }

    [Fact]
    public async Task CapturedWhamUsageShape_CarriesResetCreditsAndRawWindowFields()
    {
        var capturedWhamUsageShape = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "codex-wham-usage.redacted.json"));

        var snapshot = CodexQuotaProbe.ParseResponse(capturedWhamUsageShape);

        // Top-level banked manual-reset count is preserved verbatim.
        Assert.Equal(3, snapshot.ResetCreditsAvailable);

        // Each window carries the untransformed used_percent and the raw epoch
        // reset_at alongside the derived AvailablePct / ResetAt.
        var fiveH = Assert.Single(snapshot.Windows, w => w.Name == "5h-rolling");
        Assert.Equal(34, fiveH.UsedPercent);
        Assert.Equal(1778091218, fiveH.ResetAtEpochSeconds);
        Assert.Equal(66, fiveH.AvailablePct);

        var weekly = Assert.Single(snapshot.Windows, w => w.Name == "weekly");
        Assert.Equal(37, weekly.UsedPercent);
        Assert.Equal(1778605571, weekly.ResetAtEpochSeconds);
        Assert.Equal(63, weekly.AvailablePct);
    }

    [Fact]
    public void ParseResponse_WithoutResetCredits_LeavesFieldNull()
    {
        // Absent rate_limit_reset_credits must not fabricate a count — the
        // reset-credit tracker distinguishes "0 banked" from "unknown".
        var snapshot = CodexQuotaProbe.ParseResponse("""
        {
          "rate_limit": { "primary_window": { "used_percent": 40, "reset_at": 1778091218 } }
        }
        """);

        Assert.Null(snapshot.ResetCreditsAvailable);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(40, window.UsedPercent);
        Assert.Equal(1778091218, window.ResetAtEpochSeconds);
    }

    [Fact]
    public void PrimaryWindowDeclaringAWeeklyLength_IsNamedWeekly_NotFiveHourly()
    {
        // Captured from the live account: the WEEKLY allowance arrives in the primary_window slot
        // (limit_window_seconds 604800) with secondary_window null. Naming it by slot reported a
        // 96%-consumed weekly window as "5h-rolling" — which reads as "recovers in hours" when it
        // recovers in days, and looks the per-window floor up under the wrong key.
        const string json = """
        {"rate_limit":{"allowed":true,"limit_reached":false,
          "primary_window":{"used_percent":96,"limit_window_seconds":604800,"reset_at":1789113119},
          "secondary_window":null}}
        """;

        var snapshot = CodexQuotaProbe.ParseResponse(json);

        Assert.True(snapshot.IsKnown);
        Assert.Equal(4.0, snapshot.AvailablePct, precision: 6);
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("weekly", window.Name);
        Assert.Equal(4.0, window.AvailablePct, precision: 6);
    }

    [Fact]
    public void PrimaryWindowDeclaringAShortLength_StaysFiveHourly()
    {
        // The per-feature limits really are 5-hourly (18000s); they must not be swept up as weekly.
        const string json = """
        {"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":18000,"reset_at":1788593271}}}
        """;

        var window = Assert.Single(CodexQuotaProbe.ParseResponse(json).Windows);

        Assert.Equal("5h-rolling", window.Name);
    }

    [Fact]
    public void WindowWithoutADeclaredLength_KeepsThePositionalName()
    {
        // Older payloads omit limit_window_seconds; fall back rather than guess.
        const string json = """
        {"rate_limit":{"primary_window":{"used_percent":25,"reset_at":1789113119},
                       "secondary_window":{"used_percent":10,"reset_at":1789113119}}}
        """;

        var names = CodexQuotaProbe.ParseResponse(json).Windows.Select(w => w.Name).ToArray();

        Assert.Contains("5h-rolling", names);
        Assert.Contains("weekly", names);
    }
}
