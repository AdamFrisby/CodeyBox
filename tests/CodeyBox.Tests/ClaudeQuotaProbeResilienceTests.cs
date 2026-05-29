using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the snapshot-retention / retry behaviour added to
/// <see cref="ClaudeQuotaProbe"/>: a one-off transient probe failure must NOT
/// discard the previously known-good quota figure (otherwise the
/// <c>MinQuotaPct</c> floor is silently bypassed under
/// <c>QuotaUnknownPolicy.UseObservedFailures</c>); only after N consecutive
/// failures OR exceeding the staleness window should the snapshot fall to
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1.
/// </summary>
public sealed class ClaudeQuotaProbeResilienceTests
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
        ClaudeQuotaProbeResilienceOptions options,
        TestTimeProvider time,
        TimeSpan? cacheTtl = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new ClaudeQuotaProbe(
            factory,
            () => new AgentQuotaCredentials("test-token"),
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<ClaudeQuotaProbe>.Instance,
            resilienceProvider: () => options,
            timeProvider: time);
    }

    // ── Retain last-known-good across a single transient failure ──────────────

    [Fact]
    public async Task SingleTransientFailure_AfterGoodReading_RetainsLastKnownGood()
    {
        // First call: 200 with 71% available. Second call: every retry returns
        // a 5xx. The snapshot served on the second call MUST be the retained
        // 71%, not AvailablePct=-1 — that's the floor-bypass bug.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29 /* used */)),    // available = 71
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 2,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 3,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time,
            cacheTtl: TimeSpan.FromSeconds(60));

        var first = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(71, first.AvailablePct, precision: 5);

        // Advance past the cache TTL so the next call refetches.
        time.Advance(TimeSpan.FromSeconds(90));

        var second = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        // Retained — same percentage, stale-marked notes.
        Assert.Equal(71, second.AvailablePct, precision: 5);
        Assert.NotNull(second.Notes);
        Assert.Contains("stale", second.Notes!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consecutiveFailures=1", second.Notes);
    }

    [Fact]
    public async Task SingleTransientFailure_RetainsValue_FloorStillApplies()
    {
        // The whole point of retaining: the MinQuotaPct=10 floor MUST keep
        // working against the stale 71%. WouldAllow must return true while the
        // stale snapshot is in play, and would return false if AvailablePct
        // had been clobbered to -1 with UnknownPolicy=FailCautious.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29)),                  // available = 71
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 2,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 3,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(90));
        var stale = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        var routerOpts = new QuotaRouterOptions
        {
            MinQuotaPct = 10,
            UnknownPolicy = QuotaUnknownPolicy.FailCautious,
        };
        Assert.True(QuotaRouter.WouldAllow(stale.AvailablePct, recentFailure: false, routerOpts),
            "floor must evaluate against retained 71%, not -1");
    }

    [Fact]
    public async Task SingleTransientFailure_StaleValueBelowFloor_FloorBlocks()
    {
        // Fail-safe check: if last-known-good was already below the floor,
        // the retained snapshot must NOT slip past it just because it's stale.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(95)),                  // available = 5 < floor 10
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 2,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 3,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(90));
        var stale = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(5, stale.AvailablePct, precision: 5);
        var routerOpts = new QuotaRouterOptions { MinQuotaPct = 10 };
        Assert.False(QuotaRouter.WouldAllow(stale.AvailablePct, recentFailure: false, routerOpts));
    }

    // ── Falling out of retention ──────────────────────────────────────────────

    [Fact]
    public async Task ConsecutiveFailures_ExceedingThreshold_FallsToUnknown()
    {
        // After the configured number of consecutive end-to-end probe failures,
        // the retained snapshot is dropped and the next call returns -1.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29)),                  // available = 71
            // Three end-to-end probes, each MaxRetries=0 → one attempt apiece.
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.InternalServerError, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 0,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 2,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time,
            cacheTtl: TimeSpan.FromMilliseconds(1));

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        var s1 = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(71, s1.AvailablePct, precision: 5);
        Assert.Contains("stale", s1.Notes!, StringComparison.OrdinalIgnoreCase);

        time.Advance(TimeSpan.FromSeconds(1));
        var s2 = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(71, s2.AvailablePct, precision: 5);

        time.Advance(TimeSpan.FromSeconds(1));
        var s3 = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(s3.AvailablePct < 0, $"expected unknown after 3 consecutive failures, got {s3.AvailablePct}");
    }

    [Fact]
    public async Task StalenessExceeded_FallsToUnknown_EvenIfFailureCountLow()
    {
        // Even with consecutiveFailures well below threshold, a snapshot older
        // than MaxStaleness must NOT be served as a stale reading.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29)),                  // available = 71
            (HttpStatusCode.InternalServerError, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 0,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 100,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time,
            cacheTtl: TimeSpan.FromMilliseconds(1));

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        // Age the snapshot beyond MaxStaleness.
        time.Advance(TimeSpan.FromMinutes(6));

        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0,
            $"expected unknown once retained snapshot exceeds MaxStaleness, got {snap.AvailablePct}");
    }

    [Fact]
    public async Task SuccessAfterTransientFailure_ResetsConsecutiveFailureCounter()
    {
        // A successful probe must zero the counter so the next single transient
        // blip doesn't tip past the threshold prematurely.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29)),                  // 71
            (HttpStatusCode.InternalServerError, ""),         // fail
            (HttpStatusCode.OK, Rollup(50)),                  // 50, resets counter
            (HttpStatusCode.InternalServerError, ""));        // fail again
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 0,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 1,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time,
            cacheTtl: TimeSpan.FromMilliseconds(1));

        // Good
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        // Transient failure 1 — still retained (counter == 1, threshold 1).
        var s1 = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(71, s1.AvailablePct, precision: 5);
        time.Advance(TimeSpan.FromSeconds(1));
        // Recovery — counter resets, lastKnownGood updates.
        var s2 = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(50, s2.AvailablePct, precision: 5);
        Assert.Null(s2.Notes);
        time.Advance(TimeSpan.FromSeconds(1));
        // Another transient failure — counter just hit 1 again, still retained.
        var s3 = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(50, s3.AvailablePct, precision: 5);
        Assert.Contains("stale", s3.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Retry behaviour ───────────────────────────────────────────────────────

    [Fact]
    public async Task SingleAttempt_Recovers_AfterTwoTransientFailures()
    {
        // First two attempts fail with 503; third succeeds. With MaxRetries=2
        // the probe should see the success and NOT report a failure.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.ServiceUnavailable, ""),
            (HttpStatusCode.ServiceUnavailable, ""),
            (HttpStatusCode.OK, Rollup(40)));                 // 60
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 2,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 3,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time);

        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(60, snap.AvailablePct, precision: 5);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PermanentFailure_NotRetried_DoesNotRetainStale()
    {
        // 401 / 404 are config or auth issues. They are NOT retried and
        // should NOT be wrapped by the stale-retain path — those would mask
        // a real auth-rotation problem.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29)),                  // 71 — recorded
            (HttpStatusCode.Unauthorized, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 5,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 3,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time,
            cacheTtl: TimeSpan.FromMilliseconds(1));

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var s = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(s.AvailablePct < 0, $"401 must fall to unknown, got {s.AvailablePct}");
        // Exactly 2 calls: no retries on a permanent 401.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task BackToBackTransientFailures_FloorRemainsEnforced()
    {
        // Reproduces the original bug shape with the fix in place:
        // a single transient blip after a 71% reading must NOT change the
        // gating outcome under FailCautious / FailOpen / UseObservedFailures.
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, Rollup(29)),                  // 71
            (HttpStatusCode.InternalServerError, ""));
        var probe = BuildProbe(
            handler,
            new ClaudeQuotaProbeResilienceOptions
            {
                MaxRetries = 0,
                RetryInitialDelay = TimeSpan.Zero,
                MaxConsecutiveFailures = 3,
                MaxStaleness = TimeSpan.FromMinutes(5),
            },
            time,
            cacheTtl: TimeSpan.FromMilliseconds(1));

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var stale = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(71, stale.AvailablePct, precision: 5);
        // Floor evaluates against the retained 71 under every policy.
        foreach (var policy in new[] {
            QuotaUnknownPolicy.FailOpen,
            QuotaUnknownPolicy.FailCautious,
            QuotaUnknownPolicy.UseObservedFailures,
        })
        {
            var opts = new QuotaRouterOptions { MinQuotaPct = 10, UnknownPolicy = policy };
            Assert.True(QuotaRouter.WouldAllow(stale.AvailablePct, recentFailure: false, opts),
                $"policy {policy}: must allow against retained 71%");
        }
    }
}

// ── Test helpers ─────────────────────────────────────────────────────────────

internal sealed class SequenceHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
    public int CallCount { get; private set; }

    public SequenceHandler(params (HttpStatusCode Status, string Body)[] responses)
    {
        _responses = new Queue<(HttpStatusCode, string)>(responses);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        if (_responses.Count == 0)
            throw new InvalidOperationException("SequenceHandler ran out of canned responses");
        var (status, body) = _responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

/// <summary>
/// Manually-advanceable clock. Backs <c>Task.Delay(_, TimeProvider, ct)</c>
/// with a real timer of 1 ms so the tests don't deadlock waiting for the
/// production retry backoff and don't depend on wall-clock alignment.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public TestTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => System.CreateTimer(callback, state, TimeSpan.FromMilliseconds(1), period);
}
