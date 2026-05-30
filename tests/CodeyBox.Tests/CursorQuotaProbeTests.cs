using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CursorQuotaProbe"/> using a fake HTTP message handler.
/// </summary>
public sealed class CursorQuotaProbeTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Cursor,
        Billing = AgentBilling.Subscription,
        ModelId = "composer-2.5",
        QualityScore = 98,
    };

    private const string DefaultUsageBody = """
    {
      "billingCycleEnd": "1782444007000",
      "planUsage": {
        "remaining": 70,
        "limit": 100,
        "autoPercentUsed": 25,
        "apiPercentUsed": 10,
        "totalPercentUsed": 30
      },
      "autoBucketModels": ["composer-2.5"]
    }
    """;

    private static CursorQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string? token = "test-token",
        TimeSpan? cacheTtl = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new CursorQuotaProbe(
            factory,
            token,
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<CursorQuotaProbe>.Instance);
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

    [Fact]
    public void Kind_IsCursor()
    {
        var probe = BuildProbe(UsageHandler());
        Assert.Equal(AgentKind.Cursor, probe.Kind);
    }

    [Fact]
    public async Task Probe_CallsDashboardUsageEndpoint()
    {
        var calledUris = new List<Uri?>();
        var handler = UsageHandler(capture: req => calledUris.Add(req.RequestUri));

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal([new Uri(CursorQuotaProbe.UsageEndpoint)], calledUris);
    }

    [Fact]
    public async Task Probe_SendsBearerTokenAndJsonBody()
    {
        string? auth = null;
        string? contentType = null;
        string? body = null;
        HttpMethod? method = null;
        var handler = UsageHandler(capture: req =>
        {
            method = req.Method;
            auth = req.Headers.Authorization?.ToString();
            contentType = req.Content?.Headers.ContentType?.ToString();
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        var probe = BuildProbe(handler, token: "cursor-access-token");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("Bearer cursor-access-token", auth);
        Assert.Equal("application/json; charset=utf-8", contentType);
        Assert.Equal("{}", body);
    }

    [Fact]
    public async Task NoToken_ReturnsUnknownWithoutHttpCall()
    {
        int callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);
        var probe = BuildProbe(handler, token: null);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("no token configured", snap.Notes);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task EmptyToken_ReturnsUnknownWithoutHttpCall()
    {
        int callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);
        var probe = BuildProbe(handler, token: "");
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("no token configured", snap.Notes);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void ParseResponse_FullyUsed_Returns0Pct()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"planUsage":{"remaining":0,"limit":100}}""");
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public void ParseResponse_PartiallyUsed_ReturnsRemainingPct()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"planUsage":{"remaining":93.27,"limit":100}}""");
        Assert.Equal(93.27, snap.AvailablePct, precision: 2);
    }

    [Fact]
    public void ParseResponse_NeverNegative_WhenOverQuota()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"planUsage":{"remaining":-50,"limit":100}}""");
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public async Task PartiallyUsed_WithoutPerModelBuckets_ConfiguredModel_ReturnsUnknown()
    {
        var handler = UsageHandler("""{"planUsage":{"remaining":93.27,"limit":100}}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(-1, snap.AvailablePct);
        Assert.Contains("composer-2.5", snap.Notes ?? "");
        Assert.Contains("not in quota response", snap.Notes ?? "");
    }

    [Fact]
    public async Task PartiallyUsed_WithoutPerModelBuckets_NoModelId_PreservesOverall()
    {
        var handler = UsageHandler("""{"planUsage":{"remaining":93.27,"limit":100}}""");
        var probe = BuildProbe(handler);
        var member = AnyMember with { ModelId = null };
        var snap = await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(93.27, snap.AvailablePct, precision: 2);
        Assert.Null(snap.Notes);
    }

    [Fact]
    public async Task HttpError_ReturnsUnknown()
    {
        var probe = BuildProbe(UsageHandler(usageStatus: HttpStatusCode.Unauthorized));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("HTTP 401", snap.Notes);
    }

    [Fact]
    public async Task UnexpectedShape_ReturnsUnknown()
    {
        var probe = BuildProbe(UsageHandler("""{"unexpected":true}"""));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("unexpected response shape", snap.Notes);
    }

    [Fact]
    public void ParseResponse_InvalidJson_ReturnsUnknown()
    {
        var snap = CursorQuotaProbe.ParseResponse("not-json");
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("invalid JSON", snap.Notes);
    }

    [Fact]
    public async Task NetworkException_ReturnsUnknown()
    {
        var handler = new QuotaThrowingHandler(new HttpRequestException("network failure"));
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("HTTP error", snap.Notes);
    }

    [Fact]
    public async Task RequestTimeout_ReturnsUnknown()
    {
        var handler = new QuotaThrowingHandler(new TaskCanceledException("simulated HTTP timeout"));
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("request timeout", snap.Notes);
    }

    [Fact]
    public async Task UnexpectedException_ReturnsUnknown()
    {
        var handler = new QuotaThrowingHandler(new InvalidOperationException("handler fault"));
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("unexpected error", snap.Notes);
    }

    [Fact]
    public async Task ResponseTooLarge_ReturnsUnknown()
    {
        var oversized = "{\"planUsage\":{\"remaining\":0,\"limit\":100}}" + new string(' ', 70 * 1024);
        var probe = BuildProbe(UsageHandler(oversized));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("response too large", snap.Notes);
    }

    [Fact]
    public void ParseResponse_CapsPerModelByOverall()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "remaining": 10,
            "limit": 100,
            "autoPercentUsed": 25,
            "apiPercentUsed": 10
          },
          "autoBucketModels": ["composer-2.5"]
        }
        """);
        Assert.Equal(10.0, snap.AvailablePct, precision: 5);
        Assert.Equal(10.0, snap.PerModel["composer-2.5"].AvailablePct, precision: 5);
        Assert.Contains("capped by overall", snap.PerModel["composer-2.5"].Window);
        Assert.False(snap.PerModel.ContainsKey("cursor-auto"));
        Assert.False(snap.PerModel.ContainsKey("cursor-api"));
    }

    [Fact]
    public async Task CacheHit_DoesNotCallNetworkTwice()
    {
        int callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ConfiguredModelMissingFromBuckets_ReturnsUnknown()
    {
        var handler = UsageHandler("""
        {
          "planUsage": { "remaining": 90, "limit": 100, "autoPercentUsed": 10, "apiPercentUsed": 0 },
          "autoBucketModels": ["composer-2"]
        }
        """);
        var probe = BuildProbe(handler);
        var member = AnyMember with { ModelId = "composer-99-unknown" };
        var snap = await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Contains("composer-99-unknown", snap.Notes);
    }

    [Fact]
    public void ParseResponse_PopulatesAutoBucketModels()
    {
        var snap = CursorQuotaProbe.ParseResponse(DefaultUsageBody);
        Assert.Equal(70, snap.AvailablePct, precision: 5);
        Assert.Equal(70, snap.PerModel["composer-2.5"].AvailablePct, precision: 5);
        Assert.False(snap.PerModel.ContainsKey("cursor-auto"));
        Assert.False(snap.PerModel.ContainsKey("cursor-api"));
    }

    [Fact]
    public void ParseResponse_UsesFallbackAutoBucketModels_WhenArrayMissing()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "remaining": 50,
            "limit": 100,
            "autoPercentUsed": 20
          }
        }
        """);
        Assert.Equal(50, snap.AvailablePct, precision: 5);
        Assert.True(snap.PerModel.ContainsKey(CursorQuotaProbe.DefaultRoutedModelId));
        Assert.Equal(50, snap.PerModel[CursorQuotaProbe.DefaultRoutedModelId].AvailablePct, precision: 5);
    }

    [Fact]
    public async Task TokenChange_WithinTtl_HitsNetworkWithNewToken()
    {
        var token = "token-1";
        var capturedAuths = new List<string?>();
        var handler = UsageHandler(capture: req =>
            capturedAuths.Add(req.Headers.Authorization?.ToString()));
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var probe = new CursorQuotaProbe(
            factory,
            () => new AgentQuotaCredentials(token),
            TimeSpan.FromMinutes(5),
            NullLogger<CursorQuotaProbe>.Instance);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        token = "token-2";
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(["Bearer token-1", "Bearer token-2"], capturedAuths);
    }

    [Fact]
    public async Task InvalidateCache_ForcesRefetchOnNextCall()
    {
        int callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);

        var probe = BuildProbe(handler, cacheTtl: TimeSpan.FromHours(1));
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(1, callCount);

        probe.InvalidateCache();

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(2, callCount);

        probe.InvalidateCache();
        var refetch = probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        var winner = await Task.WhenAny(refetch, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(refetch, winner);
        Assert.Equal(3, callCount);
    }
}
