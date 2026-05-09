using CodeyBox.Agents.Gemini;

namespace CodeyBox.Tests;

public sealed class GeminiQuotaProbeRealShapeTests
{
    [Fact]
    public async Task CapturedCodeAssistShape_TakesMostRestrictiveBucket()
    {
        // Captured live shape from POST cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota.
        // Each bucket is per-model, with `remainingFraction` 0-1. Overall = min across buckets.
        var capturedShape = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", "gemini-codeassist-usage.redacted.json"));

        var snapshot = GeminiQuotaProbe.ParseResponse(capturedShape);

        // Buckets: flash=1.0 (100%), flash-lite=0.42 (42%), pro=0.05 (5%).
        // Overall = min(100, 42, 5) = 5.
        Assert.Equal(5, snapshot.AvailablePct);

        // Per-model entries: each bucket landed under its modelId.
        Assert.Equal(100, snapshot.PerModel["gemini-2.5-flash"].AvailablePct);
        Assert.Equal(42, snapshot.PerModel["gemini-2.5-flash-lite"].AvailablePct);
        Assert.Equal(5, snapshot.PerModel["gemini-2.5-pro"].AvailablePct);

        Assert.NotNull(snapshot.ResetAt);
    }

    [Fact]
    public void EmptyBuckets_ReportsUnknown()
    {
        var snapshot = GeminiQuotaProbe.ParseResponse("{\"buckets\":[]}");
        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("no buckets", snapshot.Notes ?? "");
    }

    [Fact]
    public void MissingBucketsField_ReportsUnknown()
    {
        var snapshot = GeminiQuotaProbe.ParseResponse("{\"foo\":\"bar\"}");
        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("unexpected response shape", snapshot.Notes ?? "");
    }

    [Fact]
    public void InvalidJson_ReportsUnknown()
    {
        var snapshot = GeminiQuotaProbe.ParseResponse("not json at all");
        Assert.Equal(-1, snapshot.AvailablePct);
        Assert.Contains("invalid JSON", snapshot.Notes ?? "");
    }

    [Fact]
    public void RemainingFractionAtZero_ProducesZeroAvailability()
    {
        var json = """
            {"buckets":[
              {"modelId":"gemini-2.5-pro","remainingFraction":0.0,"resetTime":"2026-05-10T20:00:00Z","tokenType":"REQUESTS"}
            ]}
            """;
        var snapshot = GeminiQuotaProbe.ParseResponse(json);
        Assert.Equal(0, snapshot.AvailablePct);
        Assert.Equal(0, snapshot.PerModel["gemini-2.5-pro"].AvailablePct);
    }
}
