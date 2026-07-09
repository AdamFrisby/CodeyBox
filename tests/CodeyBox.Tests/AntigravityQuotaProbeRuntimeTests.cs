using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-and-runtime tests for <see cref="AntigravityQuotaProbe"/>. The probe is
/// an authorization/liveness read of <c>:loadCodeAssist</c> (the only gateway
/// RPC that answers for our credential without spending quota): 200 ⇒ available,
/// 429 ⇒ rate-limited with the gateway reset, anything else ⇒ Unknown. Per-model
/// exhaustion is learned reactively via <see cref="AntigravityQuotaProbe.MarkExhaustedAsync"/>;
/// those overrides gate pickups until the gateway-provided reset (a 7-day weekly
/// lockout parks the member instead of churning).
/// </summary>
public sealed class AntigravityQuotaProbeRuntimeTests
{
    // A trimmed real :loadCodeAssist 200 body (AI Pro account).
    private const string TierBody =
        """{"currentTier":{"id":"standard-tier"},"paidTier":{"id":"g1-pro-tier"}}""";

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

    // ── loadCodeAssist: 200 / 429 / 403 / 5xx / transport ────────────────────

    [Fact]
    public async Task LoadCodeAssist_200_Reports100PctAvailableWithTier()
    {
        // A 200 from :loadCodeAssist means the credential is valid and the
        // subscription is active — dispatchable. The tier label flows into Notes
        // and the per-model key carries a 100% reading for the router.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(100.0, snapshot.AvailablePct);
        Assert.Contains("authorized", snapshot.Notes!);
        Assert.Contains("g1-pro-tier", snapshot.Notes!);
        Assert.Equal(100.0, snapshot.PerModel["gemini-3.5-flash-high"].AvailablePct);
        // The request hit loadCodeAssist with the GEMINI plugin type (ANTIGRAVITY
        // is rejected by the proto on this host).
        Assert.Single(handler.Requests);
        Assert.Equal(AntigravityQuotaProbe.LoadCodeAssistEndpoint, handler.Requests[0].Uri);
        Assert.Contains("GEMINI", handler.Requests[0].Body);
    }

    [Fact]
    public async Task LoadCodeAssist_200_EmptyBody_StillAvailable_NoTierNote()
    {
        // No tier fields → still authorized (200), just no tier label.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, "{}");

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(100.0, snapshot.AvailablePct);
        Assert.Equal("loadCodeAssist: authorized", snapshot.Notes);
    }

    [Fact]
    public async Task LoadCodeAssist_429_Reports0PctWithStructuredReset()
    {
        // Body carries quota_metadata.lockout_until — the 7-day lockout shape the
        // failure detector reads. The probe must surface that exact reset so the
        // work item parks WaitingForQuotaReset until then, not for a
        // Retry-After-derived "5 minutes from now".
        var lockoutUntil = "2026-06-16T12:00:00Z";
        var body = "{\"error\":{\"code\":429,\"message\":\"weekly limit reached\","
            + "\"quota_metadata\":{\"lockout_until\":\"" + lockoutUntil + "\"}}}";
        var handler = new LoadCodeAssistRouter(HttpStatusCode.TooManyRequests, body);

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member("claude-opus-4-6-thinking"), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.NotNull(snapshot.ResetAt);
        Assert.Equal(DateTimeOffset.Parse(lockoutUntil), snapshot.ResetAt);
        Assert.Contains("rate-limited", snapshot.Notes!);
        Assert.Equal(0.0, snapshot.PerModel["claude-opus-4-6-thinking"].AvailablePct);
    }

    [Fact]
    public async Task LoadCodeAssist_429_PrefersRetryAfterOverStructuredReset()
    {
        // When the gateway sends both a Retry-After header AND a body field, the
        // header wins (matches the probe's source order).
        var retryAfterDelta = TimeSpan.FromMinutes(7);
        var handler = new LoadCodeAssistRouter(
            HttpStatusCode.TooManyRequests,
            """{"error":{"quota_metadata":{"lockout_until":"2030-01-01T00:00:00Z"}}}""",
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
    public async Task LoadCodeAssist_403_ReportsUnknown()
    {
        // PERMISSION_DENIED is not a definitive "available" or "exhausted"
        // signal — the probe must surface Unknown (-1) and let the router's
        // QuotaUnknownPolicy decide, not falsely gate the member forever.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.Forbidden, "{}");

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(-1.0, snapshot.AvailablePct);
        Assert.Contains("HTTP 403", snapshot.Notes!);
        Assert.Empty(snapshot.PerModel);
    }

    [Fact]
    public async Task LoadCodeAssist_5xx_ReportsUnknown()
    {
        var handler = new LoadCodeAssistRouter(HttpStatusCode.InternalServerError, "");

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(-1.0, snapshot.AvailablePct);
        Assert.Contains("HTTP 500", snapshot.Notes!);
    }

    [Fact]
    public async Task LoadCodeAssist_TransportError_ReportsUnknown()
    {
        // Network failure (DNS / TLS / connection reset) is transient, NOT a
        // quota signal — Unknown, not a false available/exhausted.
        var handler = new LoadCodeAssistRouter(
            throwOnSend: new HttpRequestException("simulated transport failure"));

        var probe = BuildProbe(handler);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(-1.0, snapshot.AvailablePct);
        Assert.Contains("transient error", snapshot.Notes!);
    }

    [Fact]
    public async Task GetAvailability_NoToken_ReportsUnknown_DoesNotIssueHttp()
    {
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var probe = BuildProbe(handler, token: "");

        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(-1.0, snapshot.AvailablePct);
        Assert.Contains("no token", snapshot.Notes!);
        Assert.Empty(handler.Requests);
    }

    // ── MarkExhaustedAsync + GetAvailabilityAsync gating ─────────────────────

    [Fact]
    public async Task MarkExhausted_SubsequentGetAvailability_ReturnsZeroWithResetAt()
    {
        // After MarkExhaustedAsync the next call returns a synthetic 0% snapshot
        // without issuing any HTTP traffic — the in-process override gates the
        // pickup immediately rather than waiting for the next periodic probe.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var resetAt = DateTimeOffset.UtcNow.AddHours(6);

        var probe = BuildProbe(handler);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(10), resetAt);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(resetAt, snapshot.ResetAt);
        Assert.Contains("exhausted", snapshot.Notes!);
        // No HTTP must have flowed — the override short-circuits the probe.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MarkExhausted_ResetEarlierThanTtl_CapsParkingAtReset()
    {
        // The gateway-provided reset is sooner than the caller-supplied TTL.
        // Snapshot.ResetAt must surface the reset (so the work item resumes
        // promptly), not the further-out expiry.
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
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
        // When the gateway reset is BEYOND the TTL, the override expires at TTL
        // but the ResetAt the router sees is still the gateway value (so a
        // re-probe after expiry doesn't lose the long-window reset).
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var farReset = now.AddDays(7);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(10), farReset);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(farReset, snapshot.ResetAt);
    }

    [Fact]
    public async Task MarkExhausted_PastResetHintDoesNotClearRuntimeGate()
    {
        // Reset hints can originate in runtime stderr/stdout parsing. A past
        // hint must be ignored instead of shortening the synthetic 429 gate to
        // "already expired".
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var probe = BuildProbe(handler, time: time);

        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(10), now.AddMinutes(-1));
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(now.AddMinutes(10), snapshot.ResetAt);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MarkExhausted_NonPositiveTtlClearsRuntimeGate()
    {
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.Zero, resetAt: null);
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(100.0, snapshot.AvailablePct);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MarkExhausted_PerModelKey_DoesNotClobberOtherModels()
    {
        // Two members on the same account+token but different ModelIds must have
        // independent exhaustion overrides; gating opus must not gate flash. This
        // pins the (RouteKey, Token, ModelId) cache discriminator.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
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
    public async Task MarkExhausted_TokenRotationDoesNotInheritPriorRuntimeGate()
    {
        // The agy probe's live quota read is route+token+model scoped. Learned
        // runtime 429 gates must use the same credential boundary so an operator
        // rotating to a different account under the same route is not stranded.
        var token = "agy-old-token";
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new AntigravityQuotaProbe(
            factory,
            _ => new AgentQuotaCredentials(token),
            TimeSpan.FromMinutes(5),
            NullLogger<AntigravityQuotaProbe>.Instance);

        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow.AddMinutes(30));
        token = "agy-new-token";
        probe.InvalidateCredentialState();

        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(100.0, snapshot.AvailablePct);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MarkExhausted_NoToken_IsNoOpAndDoesNotGate()
    {
        // Without a token the probe can't talk to the gateway anyway — the
        // override must not be recorded (and a later credential populates the
        // path normally). Pins the early-return at the top of MarkExhaustedAsync.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
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
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);

        var probe = BuildProbe(handler, time: time);
        await probe.MarkExhaustedAsync(Member(), TimeSpan.FromMinutes(5), now.AddMinutes(5));

        // Still gated within the window.
        var gated = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(0.0, gated.AvailablePct);

        // Advance past the expiry; override is dropped and live probe runs.
        time.Advance(TimeSpan.FromMinutes(10));
        var fresh = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(100.0, fresh.AvailablePct);
        Assert.NotEmpty(handler.Requests);
    }

    // ── TTL cache ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAvailability_TwiceWithinTtl_DoesNotReIssueHttp()
    {
        // The probe caches successful snapshots by (RouteKey, Token, ModelId) for
        // the configured TTL. A second call inside the window serves the cached
        // value, not another loadCodeAssist request.
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5), time: time);

        var first = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        var second = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(100.0, first.AvailablePct);
        Assert.Equal(100.0, second.AvailablePct);
        Assert.Single(handler.Requests);

        // After TTL elapses the cache entry is dropped; the next call must
        // re-issue the probe rather than hand back a stale snapshot forever.
        time.Advance(TimeSpan.FromMinutes(6));
        var refreshed = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(100.0, refreshed.AvailablePct);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidateCache_AfterPrime_ForcesFreshHttpOnNextCall()
    {
        // Response-cache invalidation forces a fresh authorization read without
        // clearing runtime 429 overrides; credential-state invalidation is the
        // wider token-rotation boundary.
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));

        _ = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Single(handler.Requests);

        probe.InvalidateCache();

        _ = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MarkExhaustedThenInvalidateCache_PreservesRuntimeGateWithoutHttp()
    {
        // LastKnownGoodQuotaProbe.MarkExhaustedAsync invalidates the wrapped
        // response cache after recording the runtime override. That invalidation
        // must not erase Antigravity's synthetic 0% gate.
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var inner = BuildProbe(handler, time: time);
        var wrapped = new LastKnownGoodQuotaProbe(
            inner,
            () => new LastKnownGoodQuotaOptions { MaxStaleness = TimeSpan.FromMinutes(5) },
            NullLogger<LastKnownGoodQuotaProbe>.Instance,
            time);
        var member = Member();

        await wrapped.MarkExhaustedAsync(member, TimeSpan.FromMinutes(10), now.AddMinutes(10));
        var snapshot = await wrapped.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(0.0, snapshot.AvailablePct);
        Assert.Equal(now.AddMinutes(10), snapshot.ResetAt);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RecoveryStateInvalidation_PreservesRuntimeGateUntilExpiry()
    {
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedClock(now);
        var handler = new LoadCodeAssistRouter(HttpStatusCode.OK, TierBody);
        var probe = BuildProbe(handler, time: time);
        var member = Member();

        await probe.MarkExhaustedAsync(member, TimeSpan.FromHours(6), now.AddHours(6));
        var gated = await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(0.0, gated.AvailablePct);
        Assert.Empty(handler.Requests);

        ((IAgentQuotaRecoveryStateInvalidator)probe).InvalidateRecoveryState(member);
        var stillGated = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(0.0, stillGated.AvailablePct);
        Assert.Equal(now.AddHours(6), stillGated.ResetAt);
        Assert.Empty(handler.Requests);

        time.Advance(TimeSpan.FromHours(7));
        var recovered = await probe.GetAvailabilityAsync(member, CancellationToken.None);

        Assert.Equal(100.0, recovered.AvailablePct);
        Assert.Single(handler.Requests);
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
    /// Answers <see cref="AntigravityQuotaProbe.LoadCodeAssistEndpoint"/> with a
    /// canned status/body (optionally a Retry-After header) and records each
    /// request's URI + body so tests can assert the gateway surface and the
    /// GEMINI plugin payload. Any other URL is a bug — the probe must not call a
    /// second endpoint.
    /// </summary>
    private sealed class LoadCodeAssistRouter : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly TimeSpan? _retryAfter;
        private readonly Exception? _throwOnSend;

        public List<(string Uri, string Body)> Requests { get; } = new();

        public LoadCodeAssistRouter(
            HttpStatusCode status = HttpStatusCode.OK,
            string body = "{}",
            TimeSpan? retryAfter = null,
            Exception? throwOnSend = null)
        {
            _status = status;
            _body = body;
            _retryAfter = retryAfter;
            _throwOnSend = throwOnSend;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((uri, body));

            if (_throwOnSend is not null) throw _throwOnSend;
            if (uri != AntigravityQuotaProbe.LoadCodeAssistEndpoint)
                throw new InvalidOperationException($"Unexpected endpoint {uri}");

            var response = new HttpResponseMessage(_status) { Content = new StringContent(_body) };
            if (_retryAfter is { } ra)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return response;
        }
    }
}
