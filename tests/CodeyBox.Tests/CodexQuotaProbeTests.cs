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
        QualityScore = 100,
    };

    private const string DefaultUsageBody = """
    {
      "rate_limit": {
        "primary_window": { "used_percent": 50, "reset_at": 1778091218 },
        "secondary_window": { "used_percent": 25, "reset_at": 1778605571 }
      }
    }
    """;

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

    private static QuotaUrlRoutingHandler UsageHandler(
        string usageBody = DefaultUsageBody,
        Action<HttpRequestMessage>? capture = null,
        HttpStatusCode usageStatus = HttpStatusCode.OK)
    {
        return new QuotaUrlRoutingHandler(req =>
        {
            capture?.Invoke(req);
            return new HttpResponseMessage(usageStatus) { Content = new StringContent(usageBody) };
        });
    }

    // ── Endpoint and Authorization header ────────────────────────────────────

    [Fact]
    public async Task Probe_CallsWhamUsageEndpoint()
    {
        var calledUris = new List<Uri?>();
        var handler = UsageHandler(capture: req => calledUris.Add(req.RequestUri));

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal([new Uri(CodexQuotaProbe.UsageEndpoint)], calledUris);
    }

    [Fact]
    public async Task Probe_SendsBearerTokenOnBothCalls()
    {
        var capturedAuths = new List<string?>();
        var handler = UsageHandler(capture: req => capturedAuths.Add(req.Headers.Authorization?.ToString()));

        var probe = BuildProbe(handler, token: "test-codex-api-key");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.All(capturedAuths, auth => Assert.Equal("Bearer test-codex-api-key", auth));
        Assert.Single(capturedAuths);
    }

    // ── Quota calculation ─────────────────────────────────────────────────────

    [Fact]
    public async Task FullyUsed_Returns0Pct()
    {
        var handler = UsageHandler(usageBody: """{"rate_limit":{"primary_window":{"used_percent":100}}}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public async Task HalfUsed_Returns50Pct()
    {
        var handler = UsageHandler(usageBody: """{"rate_limit":{"primary_window":{"used_percent":50}}}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(50.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public async Task NothingUsed_Returns100Pct()
    {
        var handler = UsageHandler(usageBody: """{"rate_limit":{"primary_window":{"used_percent":0}}}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(100.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public async Task OverQuota_FloorsAt0Pct()
    {
        var handler = UsageHandler(usageBody: """{"rate_limit":{"primary_window":{"used_percent":200}}}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public void ParseResponse_AddsDefaultRoutedAliasForWhamCodexBucket()
    {
        var snap = CodexQuotaProbe.ParseResponse("""
        {
          "rate_limit": { "primary_window": { "used_percent": 40 } },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "rate_limit": { "primary_window": { "used_percent": 100 } }
            }
          ]
        }
        """);

        Assert.Equal(0, snap.PerModel["GPT-5.3-Codex-Spark"].AvailablePct);
        Assert.Equal(0, snap.PerModel[CodexQuotaProbe.DefaultRoutedModelId].AvailablePct);
    }

    // ── HTTP error handling ───────────────────────────────────────────────────

    [Fact]
    public async Task UsageEndpoint429_ReturnsUnknown()
    {
        var handler = UsageHandler(usageStatus: HttpStatusCode.TooManyRequests);
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
        var handler = UsageHandler(capture: _ => callCount++);

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
        var handler = UsageHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromMinutes(5));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TokenChange_WithinTtl_HitsNetworkWithNewToken()
    {
        var token = "token-1";
        var capturedAuths = new List<string?>();
        var handler = UsageHandler(capture: req => capturedAuths.Add(req.Headers.Authorization?.ToString()));
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new CodexQuotaProbe(
            factory,
            () => new AgentQuotaCredentials(token, "account-1"),
            TimeSpan.FromMinutes(5),
            NullLogger<CodexQuotaProbe>.Instance);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        token = "token-2";
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(["Bearer token-1", "Bearer token-2"], capturedAuths);
    }

    [Fact]
    public async Task AccountChange_WithinTtl_HitsNetworkWithNewAccountHeader()
    {
        var accountId = "account-1";
        var capturedAccounts = new List<string?>();
        var handler = UsageHandler(capture: req =>
            capturedAccounts.Add(req.Headers.TryGetValues("ChatGPT-Account-Id", out var values)
                ? values.Single()
                : null));
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new CodexQuotaProbe(
            factory,
            () => new AgentQuotaCredentials("token-1", accountId),
            TimeSpan.FromMinutes(5),
            NullLogger<CodexQuotaProbe>.Instance);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        accountId = "account-2";
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(["account-1", "account-2"], capturedAccounts);
    }

    [Fact]
    public async Task Call_AfterTtlExpires_HitsNetworkAgain()
    {
        int callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.Zero);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await Task.Delay(1);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvalidateCache_ForcesRefetchOnNextCall()
    {
        // Direct unit test for the public InvalidateCache surface that
        // Program.cs wires to CodexCredentialFileSource.TokenUpdated. With
        // the same token across all calls, the probe's token-keyed cache
        // would otherwise survive, so the only way callCount can advance
        // after the second call is via InvalidateCache.
        int callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromHours(1));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(1, callCount); // long TTL: second call cached.

        probe.InvalidateCache();

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(2, callCount); // invalidation forced a refetch.

        // InvalidateCache must release its lock — a missing release would
        // deadlock the next GetAvailabilityAsync. A 2 s budget catches that.
        probe.InvalidateCache();
        var refetch = probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        var winner = await Task.WhenAny(refetch, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(refetch, winner);
        Assert.Equal(3, callCount);
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
