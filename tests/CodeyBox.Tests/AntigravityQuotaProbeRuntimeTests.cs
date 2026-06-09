using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-and-runtime tests for <see cref="AntigravityQuotaProbe"/>. The parser
/// shapes are pinned by <see cref="AntigravityQuotaProbeParserTests"/>; this
/// suite covers the live-ping fallback (200/429/other) and the
/// exhaustion-override path that gates pickups until the gateway-provided
/// reset moment instead of churning during a 7-day weekly lockout.
/// </summary>
public sealed class AntigravityQuotaProbeRuntimeTests
{
    private static AgentMembership Member(string modelId = "gemini-3.5-flash-high") => new()
    {
        Agent = AgentKind.Antigravity,
        Billing = AgentBilling.Subscription,
        ModelId = modelId,
        QualityScore = 80,
    };

    private static AntigravityQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string token = "agy-test-token",
        TimeSpan? cacheTtl = null,
        TimeProvider? time = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new AntigravityQuotaProbe(
            factory,
            _ => new AgentQuotaCredentials(token),
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<AntigravityQuotaProbe>.Instance,
            time);
    }

    // ── LivePingAsync: per-model 200 / 429 / non-definitive ──────────────────

    [Fact]
    public async Task LivePing_OkResponse_Reports100PctAvailable()
    {
        // Both summary/legacy endpoints return an empty bucket → no per-model
        // reading; probe must fall back to a live :generateContent ping and
        // a 200 means "this model can take traffic right now".
        var handler = new AntigravityProbeRouter(
            summaryStatus: HttpStatusCode.OK,
            summaryBody: "{}",
            legacyStatus: HttpStatusCode.OK,
            legacyBody: "{}",
            liveStatus: _ => HttpStatusCode.OK);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(100.0, snapshot.AvailablePct);
        Assert.Contains("live probe", snapshot.Notes!);
        Assert.True(snapshot.PerModel.ContainsKey("gemini-3.5-flash-high"));
        Assert.Equal(100.0, snapshot.PerModel["gemini-3.5-flash-high"].AvailablePct);
        Assert.Single(handler.LiveRequests);
        // The model id flows into the request body's models/<id> path.
        Assert.Contains("models/gemini-3.5-flash-high", handler.LiveRequests[0]);
    }

    [Fact]
    public async Task LivePing_TooManyRequests_Reports0PctWithStructuredReset()
    {
        // Body carries quota_metadata.lockout_until — the 7-day lockout shape
        // the failure detector also reads. The probe must surface that exact
        // reset so the work item parks WaitingForQuotaReset until then,
        // not for a Retry-After-derived "5 minutes from now".
        var lockoutUntil = "2026-06-16T12:00:00Z";
        var body = "{\"error\":{\"code\":429,\"message\":\"weekly limit reached\","
            + "\"quota_metadata\":{\"lockout_until\":\"" + lockoutUntil + "\"}}}";
        var handler = new AntigravityProbeRouter(
            liveStatus: _ => HttpStatusCode.TooManyRequests,
            liveBody: _ => body);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member("claude-opus-4-6-thinking"), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.NotNull(snapshot.ResetAt);
        Assert.Equal(DateTimeOffset.Parse(lockoutUntil), snapshot.ResetAt);
        Assert.Contains("rate-limited", snapshot.Notes!);
        Assert.Equal(0.0, snapshot.PerModel["claude-opus-4-6-thinking"].AvailablePct);
    }

    [Fact]
    public async Task LivePing_TooManyRequests_PreferRetryAfterOverStructuredReset()
    {
        // When the gateway sends both a Retry-After header AND a body field,
        // the header wins (matches the LivePingAsync source order).
        var retryAfterDelta = TimeSpan.FromMinutes(7);
        var handler = new AntigravityProbeRouter(
            liveStatus: _ => HttpStatusCode.TooManyRequests,
            liveBody: _ => """{"error":{"quota_metadata":{"lockout_until":"2030-01-01T00:00:00Z"}}}""",
            retryAfter: retryAfterDelta);

        var now = DateTimeOffset.UtcNow;
        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.NotNull(snapshot.ResetAt);
        // Within a few seconds of now + 7m (header delta), NOT 2030 (body).
        var skew = (snapshot.ResetAt!.Value - (now + retryAfterDelta)).Duration();
        Assert.True(skew < TimeSpan.FromSeconds(5),
            $"expected reset near {now + retryAfterDelta:o}, got {snapshot.ResetAt:o}");
    }

    [Fact]
    public async Task LivePing_5xx_ReportsUnknown()
    {
        // A 500 is not a definitive "available" or "exhausted" signal — the
        // probe must NOT treat it as either, so AvailablePct stays -1
        // (Unknown) and the unknown policy in the router decides what to do.
        var handler = new AntigravityProbeRouter(
            liveStatus: _ => HttpStatusCode.InternalServerError);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(-1.0, snapshot.AvailablePct);
        Assert.Contains("HTTP 500", snapshot.Notes!);
        Assert.Empty(snapshot.PerModel);
    }

    [Fact]
    public async Task LivePing_TransportError_ReportsUnknown()
    {
        // Network failure on the live probe (DNS / TLS / connection reset) is
        // a transient condition, NOT a quota signal — must surface Unknown,
        // not falsely flag the model as available or exhausted.
        var handler = new AntigravityProbeRouter(
            liveStatus: _ => throw new HttpRequestException("simulated transport failure"));

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(-1.0, snapshot.AvailablePct);
        Assert.Contains("transient error", snapshot.Notes!);
    }

    [Fact]
    public async Task SummaryHasPerModel_DoesNotInvokeLivePing()
    {
        // When :retrieveUserQuotaSummary already carries the requested model,
        // the probe must NOT spend a live :generateContent call — that costs a
        // request slot from the very quota we're trying to gauge.
        var summary = """{"perModel":[{"modelId":"gemini-3.5-flash-high","remainingFraction":0.3,"resetTime":"2026-06-16T12:00:00Z"}]}""";
        var handler = new AntigravityProbeRouter(
            summaryStatus: HttpStatusCode.OK,
            summaryBody: summary,
            liveStatus: _ => HttpStatusCode.OK);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(30.0, snapshot.AvailablePct, 1);
        Assert.Empty(handler.LiveRequests);
    }

    // ── MarkExhaustedAsync + GetAvailabilityAsync gating ─────────────────────

    [Fact]
    public async Task MarkExhausted_SubsequentGetAvailability_ReturnsZeroWithResetAt()
    {
        // After MarkExhaustedAsync the next call returns a synthetic 0% snapshot
        // without issuing any HTTP traffic — the in-process override gates the
        // pickup immediately rather than waiting for the next periodic probe.
        var handler = new AntigravityProbeRouter(
            liveStatus: _ => HttpStatusCode.OK);
        var resetAt = DateTimeOffset.UtcNow.AddHours(6);

        var probe = BuildProbe(handler);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(10), resetAt);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(resetAt, snapshot.ResetAt);
        Assert.Contains("exhausted", snapshot.Notes!);
        // No HTTP must have flowed — the override short-circuits the probe.
        Assert.Empty(handler.LiveRequests);
        Assert.Equal(0, handler.SummaryRequests);
        Assert.Equal(0, handler.LegacyRequests);
    }

    [Fact]
    public async Task MarkExhausted_ResetEarlierThanTtl_CapsParkingAtReset()
    {
        // The gateway-provided reset is sooner than the caller-supplied TTL.
        // Snapshot.ResetAt must surface the reset (so the work item resumes
        // promptly), not the further-out expiry.
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new AntigravityProbeRouter(liveStatus: _ => HttpStatusCode.OK);
        var resetAt = now.AddMinutes(5);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromHours(1), resetAt);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(resetAt, snapshot.ResetAt);
    }

    [Fact]
    public async Task MarkExhausted_ResetLaterThanTtl_SnapshotResetIsTheGatewayReset()
    {
        // When the gateway reset is BEYOND the TTL, the override expires at
        // TTL but the ResetAt the router sees is still the gateway value (so a
        // re-probe after expiry doesn't lose the long-window reset).
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new AntigravityProbeRouter(liveStatus: _ => HttpStatusCode.OK);
        var farReset = now.AddDays(7);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(10), farReset);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(farReset, snapshot.ResetAt);
    }

    [Fact]
    public async Task MarkExhausted_DefaultsToOneMinute_WhenTtlIsZero()
    {
        // TimeSpan.Zero (or negative) TTL is a probe-pipeline tripwire; the
        // implementation falls back to a 1-minute window so the override is
        // still meaningful instead of being instantly expired.
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new AntigravityProbeRouter(liveStatus: _ => HttpStatusCode.OK);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.Zero, resetAt: null);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        // Still gating now (override is in the 1-minute fallback window).
        Assert.Equal(0.0, snapshot.AvailablePct);

        // Advance just past the 1-minute fallback; the override should expire
        // and a fresh probe must flow.
        time.Advance(TimeSpan.FromMinutes(2));
        var freshSnapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(100.0, freshSnapshot.AvailablePct);
    }

    [Fact]
    public async Task MarkExhausted_PerModelKey_DoesNotClobberOtherModels()
    {
        // Two members on the same account+token but different ModelIds must
        // have independent exhaustion overrides; gating opus must not gate
        // flash. This pins the (RouteKey, Token, ModelId) cache discriminator.
        var handler = new AntigravityProbeRouter(liveStatus: _ => HttpStatusCode.OK);
        var probe = BuildProbe(handler);

        await probe.MarkExhaustedAsync(Member("claude-opus-4-6-thinking"),
            TimeSpan.FromMinutes(30),
            DateTimeOffset.UtcNow.AddMinutes(30));

        var opusSnap = await probe.GetAvailabilityAsync(Member("claude-opus-4-6-thinking"), CancellationToken.None);
        var flashSnap = await probe.GetAvailabilityAsync(Member("gemini-3.5-flash-high"), CancellationToken.None);

        Assert.Equal(0.0, opusSnap.AvailablePct);
        Assert.Equal(100.0, flashSnap.AvailablePct);
    }

    [Fact]
    public async Task MarkExhausted_NoToken_IsNoOpAndDoesNotGate()
    {
        // Without a token the probe can't talk to the gateway anyway — the
        // override must not be recorded (and a later credential populates the
        // path normally). Pins the early-return at the top of MarkExhaustedAsync.
        var handler = new AntigravityProbeRouter(liveStatus: _ => HttpStatusCode.OK);
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new AntigravityQuotaProbe(
            factory,
            _ => new AgentQuotaCredentials(null),
            TimeSpan.FromSeconds(60),
            NullLogger<AntigravityQuotaProbe>.Instance);

        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(10),
            DateTimeOffset.UtcNow.AddMinutes(10));
        var snap = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        // No token → Unknown, NOT a synthetic 0% from a phantom override.
        Assert.Equal(-1.0, snap.AvailablePct);
        Assert.Contains("no token", snap.Notes!);
    }

    [Fact]
    public async Task MarkExhausted_ExpiredOverride_FallsThroughToLiveProbe()
    {
        // Once the override's expiry passes, the next GetAvailability must
        // re-issue the probe and let the live state through (here: 200 → 100%).
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new AntigravityProbeRouter(liveStatus: _ => HttpStatusCode.OK);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(5),
            now.AddMinutes(5));

        // Still gated within the window.
        var gated = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(0.0, gated.AvailablePct);

        // Advance past the expiry; override is dropped and live probe runs.
        time.Advance(TimeSpan.FromMinutes(10));
        var fresh = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(100.0, fresh.AvailablePct);
        Assert.NotEmpty(handler.LiveRequests);
    }

    // ── Test helpers ─────────────────────────────────────────────────────────

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now = _now.Add(d);
    }

    /// <summary>
    /// Routes incoming requests by the AntigravityQuotaProbe endpoint constant
    /// they target and returns canned bodies/statuses. Captures the
    /// :generateContent request bodies so the test can assert per-model
    /// substitution into the live-ping payload.
    /// </summary>
    private sealed class AntigravityProbeRouter : HttpMessageHandler
    {
        private readonly HttpStatusCode _summaryStatus;
        private readonly string _summaryBody;
        private readonly HttpStatusCode _legacyStatus;
        private readonly string _legacyBody;
        private readonly Func<string, HttpStatusCode> _liveStatus;
        private readonly Func<string, string>? _liveBody;
        private readonly TimeSpan? _retryAfter;

        public List<string> LiveRequests { get; } = new();
        public int SummaryRequests { get; private set; }
        public int LegacyRequests { get; private set; }

        public AntigravityProbeRouter(
            HttpStatusCode summaryStatus = HttpStatusCode.NotFound,
            string summaryBody = "",
            HttpStatusCode legacyStatus = HttpStatusCode.NotFound,
            string legacyBody = "",
            Func<string, HttpStatusCode>? liveStatus = null,
            Func<string, string>? liveBody = null,
            TimeSpan? retryAfter = null)
        {
            _summaryStatus = summaryStatus;
            _summaryBody = summaryBody;
            _legacyStatus = legacyStatus;
            _legacyBody = legacyBody;
            _liveStatus = liveStatus ?? (_ => HttpStatusCode.OK);
            _liveBody = liveBody;
            _retryAfter = retryAfter;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            if (uri == AntigravityQuotaProbe.QuotaSummaryEndpoint)
            {
                SummaryRequests++;
                return new HttpResponseMessage(_summaryStatus) { Content = new StringContent(_summaryBody) };
            }
            if (uri == AntigravityQuotaProbe.QuotaEndpoint)
            {
                LegacyRequests++;
                return new HttpResponseMessage(_legacyStatus) { Content = new StringContent(_legacyBody) };
            }
            if (uri == AntigravityQuotaProbe.GenerateContentEndpoint)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                LiveRequests.Add(body);
                var modelId = ParseModelId(body);
                var status = _liveStatus(modelId);
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(_liveBody?.Invoke(modelId) ?? "{}"),
                };
                if (_retryAfter is { } ra)
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
                return response;
            }
            throw new InvalidOperationException($"Unexpected endpoint {uri}");
        }

        private static string ParseModelId(string body)
        {
            const string needle = "\"model\":\"models/";
            var idx = body.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return "";
            var start = idx + needle.Length;
            var end = body.IndexOf('"', start);
            return end < 0 ? "" : body[start..end];
        }
    }
}
