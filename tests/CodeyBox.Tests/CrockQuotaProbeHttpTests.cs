using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Crock;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for <see cref="CrockQuotaProbe"/>. The probe's whole job
/// is to validate the configured Anthropic API key against
/// <c>GET /v1/models</c> and translate the response into an
/// <see cref="AgentQuotaSnapshot"/>; a regression that broke the 200 path
/// (wrong header, wrong endpoint URL, missing version pin) would leave the
/// router permanently Unknown for every crock dispatch. None of these paths
/// can be reached through the parameter-less constructor used by the existing
/// CrockAgentRunnerTests fixture, so a dedicated HTTP-mocked test class is
/// the only place these paths land.
/// </summary>
public sealed class CrockQuotaProbeHttpTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Crock,
        Billing = AgentBilling.PayPerApi,
        QualityScore = 50,
    };

    private static CrockQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string token = "sk-ant-test",
        TimeSpan? cacheTtl = null,
        TimeProvider? timeProvider = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new CrockQuotaProbe(
            factory,
            _ => new AgentQuotaCredentials(token),
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<CrockQuotaProbe>.Instance,
            timeProvider);
    }

    // ── Headers / endpoint ──────────────────────────────────────────────────

    [Fact]
    public async Task Probe_HitsListModelsEndpoint()
    {
        Uri? capturedUri = null;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            req => capturedUri = req.RequestUri);

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(new Uri(CrockQuotaProbe.ListModelsEndpoint), capturedUri);
    }

    [Fact]
    public async Task Probe_SendsApiKeyAndVersionHeaders()
    {
        // x-api-key + anthropic-version are the wire-pinning shape the
        // Anthropic SDK uses; a regression to Bearer-auth here would break
        // every probe with a 401 against a healthy key.
        string? capturedKey = null, capturedVersion = null;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            req =>
            {
                capturedKey = req.Headers.TryGetValues("x-api-key", out var k)
                    ? string.Join(",", k) : null;
                capturedVersion = req.Headers.TryGetValues("anthropic-version", out var v)
                    ? string.Join(",", v) : null;
            });

        var probe = BuildProbe(handler, token: "my-key");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal("my-key", capturedKey);
        Assert.Equal(CrockQuotaProbe.AnthropicVersion, capturedVersion);
    }

    // ── Status-code mapping ────────────────────────────────────────────────

    [Fact]
    public async Task Probe_Http200_ReturnsFullAvailabilityWithPricingNote()
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var probe = BuildProbe(handler);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snapshot.IsKnown);
        Assert.Equal(100.0, snapshot.AvailablePct);
        // The Notes field must mention the pricing model so the operator
        // dashboard reads truthfully — "100%" for a pay-per-token agent
        // would otherwise look like a free pass.
        Assert.NotNull(snapshot.Notes);
        Assert.Contains("pay-per-token", snapshot.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_Http429_ReturnsExhaustedSnapshot()
    {
        var handler = new QuotaCapturingHandler(HttpStatusCode.TooManyRequests, "", _ => { });
        var probe = BuildProbe(handler);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.NotNull(snapshot.Notes);
        Assert.Contains("rate-limited", snapshot.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_Http429_WithRetryAfterDelta_PopulatesResetAt()
    {
        var handler = new QuotaWithRetryAfterHandler(retryAfterSeconds: 90);
        var fakeTime = new FixedTimeProvider(DateTimeOffset.Parse("2026-06-24T12:00:00Z"));
        var probe = BuildProbe(handler, timeProvider: fakeTime);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.NotNull(snapshot.ResetAt);
        // Retry-After: 90 seconds from "now" should land 90s past the fake clock.
        var expected = fakeTime.GetUtcNow().AddSeconds(90);
        Assert.True(snapshot.ResetAt!.Value >= expected.AddSeconds(-1)
            && snapshot.ResetAt.Value <= expected.AddSeconds(1));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Probe_Http401Or403_ReturnsUnknownPermanent(HttpStatusCode status)
    {
        var handler = new QuotaCapturingHandler(status, "", _ => { });
        var probe = BuildProbe(handler);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        // 401/403 is a permanent credential failure — the router's unknown
        // policy must see Permanent so a stale key cannot ride the
        // last-known-good cache forever.
        Assert.Equal(QuotaUnknownReason.Permanent, snapshot.Unknown);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Probe_5xx_ReturnsUnknownTransient(HttpStatusCode status)
    {
        var handler = new QuotaCapturingHandler(status, "", _ => { });
        var probe = BuildProbe(handler);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snapshot.Unknown);
    }

    [Fact]
    public async Task Probe_NetworkException_ReturnsUnknownTransient()
    {
        var handler = new QuotaThrowingHandler(new HttpRequestException("network failure"));
        var probe = BuildProbe(handler);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snapshot.Unknown);
    }

    // ── Cache behaviour ─────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_SecondCallWithinTtl_DoesNotHitNetwork()
    {
        int calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            _ => calls++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Probe_DifferentTokens_HitNetworkSeparately()
    {
        // The cache key is (routeKey, token). Two crock members on the same
        // route but different API keys must not share a cache entry —
        // otherwise the second key's first probe would silently return the
        // first key's result.
        int calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            _ => calls++);
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);

        string current = "key-A";
        var probe = new CrockQuotaProbe(
            factory,
            _ => new AgentQuotaCredentials(current),
            TimeSpan.FromMinutes(5),
            NullLogger<CrockQuotaProbe>.Instance);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        current = "key-B";
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Probe_InvalidateCache_ForcesFreshNetworkCall()
    {
        int calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            _ => calls++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        probe.InvalidateCache();
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    // ── No credential ───────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_NoToken_ReturnsNoCredentialWithoutHttpCall()
    {
        int calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            _ => calls++);

        var probe = BuildProbe(handler, token: "");
        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(QuotaUnknownReason.NoCredential, snapshot.Unknown);
        Assert.Equal(0, calls);
    }

    // ── MarkExhaustedAsync (runtime 429 hint path) ──────────────────────────

    [Fact]
    public async Task MarkExhaustedAsync_GatesNextPickupBeforeNetwork()
    {
        // The reactive 429 override the class doc advertises: after a real
        // dispatch returns 429 the orchestrator calls MarkExhaustedAsync so
        // the next pickup of the same member sees AvailablePct=0 immediately,
        // without waiting for the next periodic probe to fail again.
        int calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            _ => calls++);

        var probe = BuildProbe(handler);
        await probe.MarkExhaustedAsync(
            AnyMember, ttl: TimeSpan.FromMinutes(10),
            resetAt: null);

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(0, calls);
        Assert.NotNull(snapshot.Notes);
        Assert.Contains("exhausted", snapshot.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkExhaustedAsync_ResetAtSoonerThanTtl_ClampsExpiry()
    {
        // The lockout window is min(TTL, resetAt) — a runtime hint must not
        // push the parking window past the provider's actual reset moment.
        var fakeTime = new FixedTimeProvider(DateTimeOffset.Parse("2026-06-24T12:00:00Z"));
        int calls = 0;
        var handler = new QuotaCapturingHandler(HttpStatusCode.OK, "{}",
            _ => calls++);
        var probe = BuildProbe(handler, timeProvider: fakeTime);

        var earlyReset = fakeTime.GetUtcNow().AddMinutes(5);
        await probe.MarkExhaustedAsync(
            AnyMember,
            ttl: TimeSpan.FromHours(1),    // long TTL
            resetAt: earlyReset);          // but earlier reset wins

        var snapshot = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(earlyReset, snapshot.ResetAt);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MarkExhaustedAsync_PerToken_DoesNotLeakAcrossKeys()
    {
        // Two AgentMembership instances on the same route but distinct tokens
        // must be gated independently — marking one exhausted must NOT gate
        // the other.
        var factory = new QuotaFakeHttpClientFactory("agent-quota",
            new QuotaCapturingHandler(HttpStatusCode.OK, "{}", _ => { }));
        string token = "key-exhausted";

        var probe = new CrockQuotaProbe(
            factory,
            _ => new AgentQuotaCredentials(token),
            TimeSpan.FromMinutes(5),
            NullLogger<CrockQuotaProbe>.Instance);

        await probe.MarkExhaustedAsync(AnyMember, ttl: TimeSpan.FromHours(1));
        var snapshotA = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(0.0, snapshotA.AvailablePct);

        // Swap token — the second key must NOT inherit the exhaustion mark.
        token = "key-fresh";
        var snapshotB = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(100.0, snapshotB.AvailablePct);
    }

    // ── Test helpers (Anthropic-shaped) ─────────────────────────────────────

    private sealed class QuotaWithRetryAfterHandler : HttpMessageHandler
    {
        private readonly int _retryAfterSeconds;
        public QuotaWithRetryAfterHandler(int retryAfterSeconds)
        {
            _retryAfterSeconds = retryAfterSeconds;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(""),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(_retryAfterSeconds));
            return Task.FromResult(response);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
