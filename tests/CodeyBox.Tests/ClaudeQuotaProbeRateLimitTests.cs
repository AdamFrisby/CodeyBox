using System.Net;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// A 429 from the usage endpoint means "stop asking". Retrying it spends more requests on an endpoint
/// that is already refusing, which deepens the rate limit rather than escaping it.
/// </summary>
public sealed class ClaudeQuotaProbeRateLimitTests
{
    private static AgentMembership Member() => new()
    {
        Agent = AgentKind.Claude,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
        ModelId = "claude-opus-4-8",
    };

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly TimeSpan? _retryAfter;
        private readonly DateTimeOffset? _retryAfterDate;

        public int Requests { get; private set; }

        public CountingHandler(HttpStatusCode status, TimeSpan? retryAfter = null, DateTimeOffset? retryAfterDate = null)
        {
            _status = status;
            _retryAfter = retryAfter;
            _retryAfterDate = retryAfterDate;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var response = new HttpResponseMessage(_status) { Content = new StringContent("{}") };
            if (_retryAfterDate is { } date)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
            else if (_retryAfter is { } ra)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return Task.FromResult(response);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static ClaudeQuotaProbe Probe(HttpMessageHandler handler, MutableClock clock) =>
        new(new SingleClientFactory(handler),
            _ => new AgentQuotaCredentials("token"),
            // Zero TTL so the cache never masks what the rate-limit cooldown is doing.
            TimeSpan.Zero,
            NullLogger<ClaudeQuotaProbe>.Instance,
            resilienceProvider: () => new ClaudeQuotaProbeResilienceOptions { MaxRetries = 3 },
            timeProvider: clock);

    [Fact]
    public async Task RateLimited_IssuesExactlyOneRequest_NotAWholeRetryLadder()
    {
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests);
        var clock = new MutableClock(DateTimeOffset.UtcNow);

        var snapshot = await Probe(handler, clock).GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.False(snapshot.IsKnown);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task WhileCoolingDown_NoFurtherRequestsAreMade()
    {
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var probe = Probe(handler, clock);

        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        var afterFirst = handler.Requests;

        clock.Advance(TimeSpan.FromMinutes(5));   // still inside the default 15m cooldown
        var snapshot = await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(afterFirst, handler.Requests);
        Assert.Contains("rate-limited", snapshot.Notes ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AfterTheCooldownElapses_ProbingResumes()
    {
        // The suppression must be temporary — a rate limit should not blind the router indefinitely.
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var probe = Probe(handler, clock);

        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        var afterFirst = handler.Requests;

        clock.Advance(ClaudeQuotaProbe.DefaultRateLimitCooldown + TimeSpan.FromMinutes(1));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.True(handler.Requests > afterFirst, "probing should resume once the cooldown expires");
    }

    [Fact]
    public async Task RetryAfterHeader_IsHonoured_UpToTheCap()
    {
        // A provider Retry-After within the cap is honoured in full: the probe
        // waits at least the server's value before issuing another request.
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30));
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var probe = Probe(handler, clock);

        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        var afterFirst = handler.Requests;

        clock.Advance(TimeSpan.FromSeconds(20));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(afterFirst, handler.Requests);

        clock.Advance(TimeSpan.FromSeconds(20));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.True(handler.Requests > afterFirst, "probing should resume once Retry-After elapses");
    }

    [Fact]
    public async Task RetryAfterHttpDate_IsHonoured_UpToTheCap()
    {
        // The HTTP-date form is honoured the same as delta-seconds.
        var start = DateTimeOffset.UtcNow;
        var clock = new MutableClock(start);
        var handler = new CountingHandler(
            HttpStatusCode.TooManyRequests, retryAfterDate: start.AddSeconds(30));
        var probe = Probe(handler, clock);

        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        var afterFirst = handler.Requests;

        clock.Advance(TimeSpan.FromSeconds(20));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(afterFirst, handler.Requests);

        clock.Advance(TimeSpan.FromSeconds(20));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.True(handler.Requests > afterFirst, "probing should resume once Retry-After elapses");
    }

    [Fact]
    public async Task RetryAfterHeader_LargeValue_IsCappedByMaxRetryDelay()
    {
        // A huge provider value must not wedge the probe: the 429 cooldown is
        // capped at MaxRetryDelay (default 5 minutes), so probing resumes
        // after the cap even though the server asked for 2 hours.
        var handler = new CountingHandler(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2));
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var probe = Probe(handler, clock);

        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);
        var afterFirst = handler.Requests;

        clock.Advance(TimeSpan.FromMinutes(3));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.Equal(afterFirst, handler.Requests);

        clock.Advance(TimeSpan.FromMinutes(3));
        await probe.GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.True(handler.Requests > afterFirst, "a capped cooldown must resume probing");
    }

    [Fact]
    public async Task TransientServerErrors_StillRetry()
    {
        // Only 429 means "stop asking" — a 500 is worth retrying, and that behaviour must survive.
        var handler = new CountingHandler(HttpStatusCode.InternalServerError);
        var clock = new MutableClock(DateTimeOffset.UtcNow);

        await Probe(handler, clock).GetAvailabilityAsync(Member(), CancellationToken.None);

        Assert.True(handler.Requests > 1, "5xx should still exercise the retry ladder");
    }

    /// <summary>Advanceable clock. The assembly's shared FakeTimeProvider is fixed-time, and this
    /// suite needs to move past a cooldown.</summary>
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
