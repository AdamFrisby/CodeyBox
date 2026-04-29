using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CodexQuotaProbe"/> using a fake HTTP message handler.
/// Mirrors <see cref="ClaudeQuotaProbeTests"/> — same shape, different probe.
/// </summary>
public sealed class CodexQuotaProbeTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Codex,
        Billing = AgentBilling.Subscription,
    };

    private static CodexQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string token = "test-token",
        TimeSpan? cacheTtl = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new CodexQuotaProbe(
            factory,
            token,
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<CodexQuotaProbe>.Instance);
    }

    // ── Endpoint and Authorization header ────────────────────────────────────

    [Fact]
    public async Task Probe_CallsCorrectEndpoint()
    {
        Uri? capturedUri = null;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            """{"usedTokens":100000,"quotaTokens":1000000}""",
            req => capturedUri = req.RequestUri);

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(new Uri(CodexQuotaProbe.UsageEndpoint), capturedUri);
    }

    [Fact]
    public async Task Probe_SendsBearerToken()
    {
        string? capturedAuth = null;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            """{"usedTokens":0,"quotaTokens":1000000}""",
            req => capturedAuth = req.Headers.Authorization?.ToString());

        var probe = BuildProbe(handler, token: "sk-openai-key");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal("Bearer sk-openai-key", capturedAuth);
    }

    // ── JSON parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseResponse_FullyUsed_Returns0Pct()
    {
        var snap = CodexQuotaProbe.ParseResponse(
            """{"usedTokens":1000000,"quotaTokens":1000000}""");
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public void ParseResponse_HalfUsed_Returns50Pct()
    {
        var snap = CodexQuotaProbe.ParseResponse(
            """{"usedTokens":500000,"quotaTokens":1000000}""");
        Assert.Equal(50.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_UnusedTokens_Returns100Pct()
    {
        var snap = CodexQuotaProbe.ParseResponse(
            """{"usedTokens":0,"quotaTokens":1000000}""");
        Assert.Equal(100.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_MissingFields_ReturnsUnknown()
    {
        var snap = CodexQuotaProbe.ParseResponse("""{"data":[],"total_usage":12.34}""");
        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public void ParseResponse_InvalidJson_ReturnsUnknown()
    {
        var snap = CodexQuotaProbe.ParseResponse("{bad json}");
        Assert.True(snap.AvailablePct < 0);
    }

    // ── HTTP error handling ───────────────────────────────────────────────────

    [Fact]
    public async Task Http404_ReturnsUnknown()
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.NotFound, "", _ => { });
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public async Task Http429_ReturnsUnknown()
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.TooManyRequests, "", _ => { });
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public async Task NetworkException_ReturnsUnknown()
    {
        var handler = new QuotaThrowingHandler(new HttpRequestException("timeout"));
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.AvailablePct < 0);
    }

    // ── No token configured ───────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_ReturnsUnknownWithoutHttpCall()
    {
        int callCount = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            """{"usedTokens":0,"quotaTokens":1000000}""",
            _ => callCount++);

        var probe = BuildProbe(handler, token: "");
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.AvailablePct < 0);
        Assert.Equal(0, callCount);
    }

    // ── Cache behaviour ───────────────────────────────────────────────────────

    [Fact]
    public async Task SecondCall_WithinTtl_DoesNotHitNetwork()
    {
        int callCount = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            """{"usedTokens":100000,"quotaTokens":1000000}""",
            _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Call_AfterTtlExpires_HitsNetworkAgain()
    {
        int callCount = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            """{"usedTokens":100000,"quotaTokens":1000000}""",
            _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.Zero);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await Task.Delay(1);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(2, callCount);
    }
}
