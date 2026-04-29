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

    // Default subscription and usage bodies that produce a valid snapshot.
    private const string DefaultSubBody = """{"hard_limit_usd":100.0}""";
    private const string DefaultUsageBody = """{"data":[],"total_usage":5000}"""; // 50 USD of 100 USD used → 50% available

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

    /// <summary>
    /// Routes requests: subscription URL → subBody/subStatus, usage URL → usageBody/usageStatus.
    /// </summary>
    private static QuotaUrlRoutingHandler DualHandler(
        string subBody = DefaultSubBody,
        string usageBody = DefaultUsageBody,
        Action<HttpRequestMessage>? capture = null,
        HttpStatusCode subStatus = HttpStatusCode.OK,
        HttpStatusCode usageStatus = HttpStatusCode.OK)
    {
        return new QuotaUrlRoutingHandler(req =>
        {
            capture?.Invoke(req);
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("subscription"))
                return new HttpResponseMessage(subStatus) { Content = new StringContent(subBody) };
            return new HttpResponseMessage(usageStatus) { Content = new StringContent(usageBody) };
        });
    }

    // ── Endpoint and Authorization header ────────────────────────────────────

    [Fact]
    public async Task Probe_CallsBothEndpoints()
    {
        var calledUris = new List<Uri?>();
        var handler = DualHandler(capture: req => calledUris.Add(req.RequestUri));

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Contains(calledUris, u => u!.AbsolutePath.Contains("subscription"));
        Assert.Contains(calledUris, u => u!.AbsolutePath.Contains("usage"));
    }

    [Fact]
    public async Task Probe_SendsBearerTokenOnBothCalls()
    {
        var capturedAuths = new List<string?>();
        var handler = DualHandler(capture: req => capturedAuths.Add(req.Headers.Authorization?.ToString()));

        var probe = BuildProbe(handler, token: "test-codex-api-key");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.All(capturedAuths, auth => Assert.Equal("Bearer test-codex-api-key", auth));
        Assert.Equal(2, capturedAuths.Count);
    }

    // ── Quota calculation ─────────────────────────────────────────────────────

    [Fact]
    public async Task FullyUsed_Returns0Pct()
    {
        // 10000 cents = $100 used of $100 limit → 0%
        var handler = DualHandler(
            subBody: """{"hard_limit_usd":100.0}""",
            usageBody: """{"data":[],"total_usage":10000}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public async Task HalfUsed_Returns50Pct()
    {
        // 5000 cents = $50 used of $100 limit → 50%
        var handler = DualHandler(
            subBody: """{"hard_limit_usd":100.0}""",
            usageBody: """{"data":[],"total_usage":5000}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(50.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public async Task NothingUsed_Returns100Pct()
    {
        // 0 cents used of $100 limit → 100%
        var handler = DualHandler(
            subBody: """{"hard_limit_usd":100.0}""",
            usageBody: """{"data":[],"total_usage":0}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(100.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public async Task OverQuota_FloorsAt0Pct()
    {
        // 20000 cents = $200 used of $100 limit → floors at 0%
        var handler = DualHandler(
            subBody: """{"hard_limit_usd":100.0}""",
            usageBody: """{"data":[],"total_usage":20000}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(0.0, snap.AvailablePct);
    }

    // ── ParseHardLimit unit tests ─────────────────────────────────────────────

    [Fact]
    public void ParseHardLimit_ValidResponse_ReturnsLimit()
    {
        var limit = CodexQuotaProbe.ParseHardLimit("""{"hard_limit_usd":100.0,"soft_limit_usd":80.0}""");
        Assert.Equal(100.0, limit, precision: 5);
    }

    [Fact]
    public void ParseHardLimit_MissingField_ReturnsNegative()
    {
        var limit = CodexQuotaProbe.ParseHardLimit("""{"soft_limit_usd":80.0}""");
        Assert.True(limit < 0);
    }

    [Fact]
    public void ParseHardLimit_InvalidJson_ReturnsNegative()
    {
        Assert.True(CodexQuotaProbe.ParseHardLimit("{bad json}") < 0);
    }

    // ── ParseTotalUsage unit tests ────────────────────────────────────────────

    [Fact]
    public void ParseTotalUsage_RealOpenAIFormat_ReturnsCents()
    {
        // {"data":[],"total_usage":12.34} is the actual OpenAI response format.
        var cents = CodexQuotaProbe.ParseTotalUsage("""{"data":[],"total_usage":12.34}""");
        Assert.Equal(12.34, cents, precision: 5);
    }

    [Fact]
    public void ParseTotalUsage_ZeroUsage_ReturnsZero()
    {
        var cents = CodexQuotaProbe.ParseTotalUsage("""{"data":[],"total_usage":0}""");
        Assert.Equal(0.0, cents, precision: 5);
    }

    [Fact]
    public void ParseTotalUsage_MissingField_ReturnsNegative()
    {
        var cents = CodexQuotaProbe.ParseTotalUsage("""{"data":[],"something_else":12.34}""");
        Assert.True(cents < 0);
    }

    [Fact]
    public void ParseTotalUsage_InvalidJson_ReturnsNegative()
    {
        Assert.True(CodexQuotaProbe.ParseTotalUsage("{bad json}") < 0);
    }

    // ── HTTP error handling ───────────────────────────────────────────────────

    [Fact]
    public async Task SubscriptionEndpoint404_ReturnsUnknown()
    {
        var handler = DualHandler(subStatus: HttpStatusCode.NotFound);
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public async Task UsageEndpoint429_ReturnsUnknown()
    {
        var handler = DualHandler(usageStatus: HttpStatusCode.TooManyRequests);
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
        var handler = DualHandler(capture: _ => callCount++);

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
        var handler = DualHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);  // 2 HTTP calls (sub + usage)
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);  // 0 HTTP calls (cache hit)

        Assert.Equal(2, callCount); // only the first fetch cycle hits the network
    }

    [Fact]
    public async Task Call_AfterTtlExpires_HitsNetworkAgain()
    {
        int callCount = 0;
        var handler = DualHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.Zero);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);  // 2 calls
        await Task.Delay(1);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);  // 2 more calls

        Assert.Equal(4, callCount);
    }
}

// ── Test helpers ─────────────────────────────────────────────────────────────

/// <summary>Routes requests to different responses based on the request URI.</summary>
internal sealed class QuotaUrlRoutingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _router;

    public QuotaUrlRoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> router) => _router = router;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => Task.FromResult(_router(req));
}
