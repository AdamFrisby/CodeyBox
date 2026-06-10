using CodeyBox.Agents.Antigravity;

namespace CodeyBox.Tests;

/// <summary>
/// Parser-only tests for <see cref="AntigravityQuotaProbe.ParseTier"/>. The
/// JSON is a trimmed real <c>:loadCodeAssist</c> 200 response captured against
/// agy 1.0.7 on 2026-06-10 (daily-cloudcode-pa). The tier label is display-only
/// — it feeds the snapshot Notes, never gating — so the parser must be lenient.
/// </summary>
public sealed class AntigravityQuotaProbeParserTests
{
    [Fact]
    public void ParseTier_RealLoadCodeAssistShape_PrefersPaidTier()
    {
        // Captured shape: an AI Pro account carries both a currentTier
        // (standard-tier) and a paidTier (g1-pro-tier). The paid tier is the
        // more informative label for the Notes.
        var json = """
        {
          "currentTier": {"id": "standard-tier", "name": "Gemini Code Assist"},
          "allowedTiers": [{"id": "standard-tier", "isDefault": true}],
          "cloudaicompanionProject": "example-project-id",
          "gcpManaged": false,
          "paidTier": {"id": "g1-pro-tier", "name": "Gemini Code Assist in Google One AI Pro"}
        }
        """;

        Assert.Equal("g1-pro-tier", AntigravityQuotaProbe.ParseTier(json));
    }

    [Fact]
    public void ParseTier_NoPaidTier_FallsBackToCurrentTier()
    {
        var json = """{"currentTier": {"id": "standard-tier", "name": "Gemini Code Assist"}}""";
        Assert.Equal("standard-tier", AntigravityQuotaProbe.ParseTier(json));
    }

    [Fact]
    public void ParseTier_NoTierFields_ReturnsNull()
    {
        Assert.Null(AntigravityQuotaProbe.ParseTier("""{"cloudaicompanionProject": "x"}"""));
        Assert.Null(AntigravityQuotaProbe.ParseTier("{}"));
        Assert.Null(AntigravityQuotaProbe.ParseTier("not json"));
    }
}
