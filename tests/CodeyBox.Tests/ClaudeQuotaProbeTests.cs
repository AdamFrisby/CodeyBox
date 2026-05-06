using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClaudeQuotaProbe"/> using a fake HTTP message handler.
/// Verifies endpoint URL, Authorization header, JSON parsing, cache behaviour,
/// and error handling.
/// </summary>
public sealed class ClaudeQuotaProbeTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    private static string Rollup(double usedPercent, string resetAt = "1778091218") =>
        $"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":{usedPercent},\"reset_at\":{resetAt}}}}}}}";

    private static ClaudeQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string token = "test-token",
        TimeSpan? cacheTtl = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new ClaudeQuotaProbe(
            factory,
            token,
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<ClaudeQuotaProbe>.Instance);
    }

    // ── Endpoint and Authorization header ────────────────────────────────────

    [Fact]
    public async Task Probe_CallsCorrectEndpoint()
    {
        Uri? capturedUri = null;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            Rollup(10),
            req => capturedUri = req.RequestUri);

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(new Uri(ClaudeQuotaProbe.UsageEndpoint), capturedUri);
    }

    [Fact]
    public async Task Probe_SendsBearerToken()
    {
        string? capturedAuth = null;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK,
            Rollup(0),
            req => capturedAuth = req.Headers.Authorization?.ToString());

        var probe = BuildProbe(handler, token: "my-secret-token");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal("Bearer my-secret-token", capturedAuth);
    }

    // ── JSON parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseResponse_FullyUsed_Returns0Pct()
    {
        var snap = ClaudeQuotaProbe.ParseResponse(Rollup(100));
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public void ParseResponse_HalfUsed_Returns50Pct()
    {
        var snap = ClaudeQuotaProbe.ParseResponse(Rollup(50));
        Assert.Equal(50.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_UnusedWindow_Returns100Pct()
    {
        var snap = ClaudeQuotaProbe.ParseResponse(Rollup(0));
        Assert.Equal(100.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_WithResetAt_PopulatesResetAt()
    {
        var snap = ClaudeQuotaProbe.ParseResponse(Rollup(10, "\"2026-05-01T00:00:00Z\""));
        Assert.NotNull(snap.ResetAt);
        Assert.Equal(2026, snap.ResetAt!.Value.Year);
    }

    [Fact]
    public void ParseResponse_MissingFields_ReturnsUnknown()
    {
        var snap = ClaudeQuotaProbe.ParseResponse("""{"some":"other","shape":true}""");
        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public void ParseResponse_InvalidJson_ReturnsUnknown()
    {
        var snap = ClaudeQuotaProbe.ParseResponse("not-json");
        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public void ParseResponse_NeverNegative_WhenOverQuota()
    {
        // Sanity-check: over-quota usage floors at 0, not negative.
        var snap = ClaudeQuotaProbe.ParseResponse(Rollup(150));
        Assert.Equal(0.0, snap.AvailablePct);
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
    public async Task Http500_ReturnsUnknown()
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { });
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.AvailablePct < 0);
    }

    [Fact]
    public async Task NetworkException_ReturnsUnknown()
    {
        var handler = new QuotaThrowingHandler(new HttpRequestException("network failure"));
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
            Rollup(0),
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
            Rollup(10),
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
            Rollup(10),
            _ => callCount++);

        // Zero TTL → cache expires immediately.
        var probe = BuildProbe(handler, cacheTtl: TimeSpan.Zero);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await Task.Delay(1); // ensure clock advances
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(2, callCount);
    }
}

// ── Test helpers ─────────────────────────────────────────────────────────────

internal sealed class QuotaFakeHttpClientFactory : IHttpClientFactory
{
    private readonly string _clientName;
    private readonly HttpMessageHandler _handler;

    public QuotaFakeHttpClientFactory(string clientName, HttpMessageHandler handler)
    {
        _clientName = clientName;
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        if (name != _clientName)
            throw new InvalidOperationException($"Unexpected client name '{name}'; expected '{_clientName}'");
        return new HttpClient(_handler, disposeHandler: false);
    }
}

internal sealed class QuotaCapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    private readonly Action<HttpRequestMessage> _capture;

    public QuotaCapturingHandler(HttpStatusCode status, string body, Action<HttpRequestMessage> capture)
    {
        _status = status;
        _body = body;
        _capture = capture;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _capture(request);
        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        });
    }
}

internal sealed class QuotaThrowingHandler : HttpMessageHandler
{
    private readonly Exception _ex;
    public QuotaThrowingHandler(Exception ex) { _ex = ex; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromException<HttpResponseMessage>(_ex);
}
