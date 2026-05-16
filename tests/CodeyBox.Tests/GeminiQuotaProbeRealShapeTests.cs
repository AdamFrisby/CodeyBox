using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

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

    [Fact]
    public async Task InvalidateCache_ForcesRefetchOnNextCall()
    {
        // Direct unit test for the public InvalidateCache surface that
        // Program.cs wires to GeminiOAuthCredentialFileSource.TokenUpdated.
        // The supplied token is stable across calls, so the probe's
        // token-keyed cache would survive otherwise — the only path that
        // can advance callCount after the second call is InvalidateCache.
        int callCount = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            "{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":1.0,\"tokenType\":\"REQUESTS\"}]}",
            _ => callCount++);
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new GeminiQuotaProbe(
            factory,
            () => new AgentQuotaCredentials("stable-token"),
            TimeSpan.FromHours(1),
            NullLogger<GeminiQuotaProbe>.Instance);

        var member = new AgentMembership
        {
            Agent = AgentKind.Gemini,
            Billing = AgentBilling.Subscription,
            QualityScore = 100,
        };

        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(1, callCount); // long TTL: second call cached.

        probe.InvalidateCache();

        await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(2, callCount); // invalidation forced a refetch.

        // InvalidateCache must release its lock — a missing release would
        // deadlock the next GetAvailabilityAsync. A 2 s budget catches that.
        probe.InvalidateCache();
        var refetch = probe.GetAvailabilityAsync(member, CancellationToken.None);
        var winner = await Task.WhenAny(refetch, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(refetch, winner);
        Assert.Equal(3, callCount);
    }
}
