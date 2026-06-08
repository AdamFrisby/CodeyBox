using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CursorQuotaProbe"/> using a fake HTTP message handler.
/// Shape source: live <c>DashboardService.GetCurrentPeriodUsage</c> capture
/// 2026-06-04 — percent-used fields are the headline, NOT a non-existent
/// <c>planUsage.remaining</c>. See <see cref="CursorQuotaProbe"/> remarks.
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
        "totalSpend": 600,
        "includedSpend": 600,
        "limit": 2000,
        "remainingBonus": true,
        "autoPercentUsed": 25,
        "apiPercentUsed": 10,
        "totalPercentUsed": 30
      },
      "enabled": true,
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
    public void ParseResponse_FullyUsedAllDimensions_Returns0Pct()
    {
        var snap = CursorQuotaProbe.ParseResponse(
            """{"planUsage":{"totalPercentUsed":100,"autoPercentUsed":100,"apiPercentUsed":100}}""");
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public void ParseResponse_PartiallyUsed_ReturnsMostConstrainedDimension()
    {
        // max(7,12,3) = 12 -> 88% available.
        var snap = CursorQuotaProbe.ParseResponse(
            """{"planUsage":{"totalPercentUsed":7,"autoPercentUsed":12,"apiPercentUsed":3}}""");
        Assert.Equal(88.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_NeverNegative_WhenOverQuota()
    {
        // Stray over-100 percent (e.g. small floating-point overshoot from
        // includedSpend == limit + bonus) clamps to 0% rather than going negative.
        var snap = CursorQuotaProbe.ParseResponse(
            """{"planUsage":{"totalPercentUsed":150,"autoPercentUsed":150,"apiPercentUsed":150}}""");
        Assert.Equal(0.0, snap.AvailablePct);
    }

    [Fact]
    public void ParseResponse_OnlyTotalPercentUsed_IsSufficient()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"planUsage":{"totalPercentUsed":40}}""");
        Assert.Equal(60.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_OnlyAutoPercentUsed_IsSufficient()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"planUsage":{"autoPercentUsed":30}}""");
        Assert.Equal(70.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_OnlyApiPercentUsed_IsSufficient()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"planUsage":{"apiPercentUsed":25}}""");
        Assert.Equal(75.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_PlanUsageAbsent_ReturnsUnknown()
    {
        var snap = CursorQuotaProbe.ParseResponse("""{"billingCycleEnd":"1782444007000"}""");
        Assert.Equal(-1, snap.AvailablePct);
        Assert.Equal("unexpected response shape", snap.Notes);
    }

    [Fact]
    public void ParseResponse_PlanUsagePresentWithoutPercentFields_ReturnsUnknown()
    {
        var snap = CursorQuotaProbe.ParseResponse(
            """{"planUsage":{"totalSpend":100,"limit":2000}}""");
        Assert.Equal(-1, snap.AvailablePct);
        Assert.Equal("unexpected response shape", snap.Notes);
    }

    [Fact]
    public void ParseResponse_DisplayMessageOutOfUsage_Forces0Pct_EvenIfPercentMissing()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalPercentUsed": 80,
            "remainingBonus": true
          },
          "displayMessage": "You've hit your usage limit"
        }
        """);
        Assert.Equal(0.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_DisplayMessageOutOfUsage_MatchesCaseInsensitively()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": { "totalPercentUsed": 50 },
          "displayMessage": "YOU'VE HIT YOUR INCLUDED USAGE LIMIT"
        }
        """);
        Assert.Equal(0.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_NonOutOfUsageDisplayMessage_DoesNotForce0Pct()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": { "totalPercentUsed": 40 },
          "displayMessage": "You've used 40% of your included usage"
        }
        """);
        Assert.Equal(60.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_BonusExhaustedAndSpendOverLimit_Forces0Pct()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalSpend": 19903,
            "limit": 2000,
            "remainingBonus": false,
            "totalPercentUsed": 90
          }
        }
        """);
        Assert.Equal(0.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_BonusExhaustedAndSpendEqualsLimit_Forces0Pct()
    {
        // Boundary: IsExplicitlyOutOfUsage uses totalSpend >= limit. Pin the
        // equality case so a future flip to `>` doesn't silently regress.
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalSpend": 2000,
            "limit": 2000,
            "remainingBonus": false,
            "totalPercentUsed": 90
          }
        }
        """);
        Assert.Equal(0.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_BonusExhaustedButSpendBelowLimit_DoesNotForce0Pct()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalSpend": 800,
            "limit": 2000,
            "remainingBonus": false,
            "totalPercentUsed": 30
          }
        }
        """);
        Assert.Equal(70.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_EnabledFalse_Forces0Pct()
    {
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": { "totalPercentUsed": 20 },
          "enabled": false
        }
        """);
        Assert.Equal(0.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_StringNumberPercents_ParsedDefensively()
    {
        // Defensive parsing: even if upstream emits a percent as a numeric string,
        // we should still read it rather than punting to Unknown.
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalPercentUsed": "55",
            "autoPercentUsed": "75",
            "apiPercentUsed": "10"
          }
        }
        """);
        Assert.Equal(25.0, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public void ParseResponse_MalformedPercentFields_DoNotThrow()
    {
        // Garbage in -> Unknown rather than an unhandled exception leaking out
        // of ParseResponse and being swallowed by FetchAsync's catch-all.
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalPercentUsed": "not-a-number",
            "autoPercentUsed": null,
            "apiPercentUsed": []
          }
        }
        """);
        Assert.Equal(-1, snap.AvailablePct);
        Assert.Equal("unexpected response shape", snap.Notes);
    }

    [Fact]
    public async Task PartiallyUsed_WithoutPerModelBuckets_ConfiguredModel_ReturnsUnknown()
    {
        // Only totalPercentUsed -> overall is computed, but no autoPercentUsed
        // means no perModel entries; the configured ModelId falls through to
        // the unknown path so the router applies its unknown policy.
        var handler = UsageHandler("""{"planUsage":{"totalPercentUsed":7}}""");
        var probe = BuildProbe(handler);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(-1, snap.AvailablePct);
        Assert.Contains("composer-2.5", snap.Notes ?? "");
        Assert.Contains("not in quota response", snap.Notes ?? "");
    }

    [Fact]
    public async Task PartiallyUsed_WithoutPerModelBuckets_NoModelId_PreservesOverall()
    {
        var handler = UsageHandler("""{"planUsage":{"totalPercentUsed":7}}""");
        var probe = BuildProbe(handler);
        var member = AnyMember with { ModelId = null };
        var snap = await probe.GetAvailabilityAsync(member, CancellationToken.None);
        Assert.Equal(93.0, snap.AvailablePct, precision: 5);
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
        var oversized = "{\"planUsage\":{\"totalPercentUsed\":0}}" + new string(' ', 70 * 1024);
        var probe = BuildProbe(UsageHandler(oversized));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.True(snap.AvailablePct < 0);
        Assert.Equal("response too large", snap.Notes);
    }

    [Fact]
    public void ParseResponse_CapsPerModelByOverall()
    {
        // total=90 -> overall 10% available. auto=25 -> bucket 75% available,
        // but capped to overall 10%.
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalPercentUsed": 90,
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
          "planUsage": { "totalPercentUsed": 10, "autoPercentUsed": 10, "apiPercentUsed": 0 },
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
        // max(total=30, auto=25, api=10) = 30 -> 70% available.
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
            "totalPercentUsed": 50,
            "autoPercentUsed": 20
          }
        }
        """);
        // max(total=50, auto=20) = 50 -> 50% available.
        Assert.Equal(50, snap.AvailablePct, precision: 5);
        Assert.True(snap.PerModel.ContainsKey(CursorQuotaProbe.DefaultRoutedModelId));
        Assert.Equal(50, snap.PerModel[CursorQuotaProbe.DefaultRoutedModelId].AvailablePct, precision: 5);
    }

    [Fact]
    public void RedactAndCap_StripsTokenLikeFieldValues()
    {
        var input = """{"accessToken":"abc.def","sessionId":"xyz","totalPercentUsed":50}""";
        var redacted = CursorQuotaProbe.RedactAndCap(input, 1024);
        Assert.DoesNotContain("abc.def", redacted);
        Assert.DoesNotContain("xyz", redacted);
        Assert.Contains("<redacted>", redacted);
        Assert.Contains("50", redacted);
    }

    [Theory]
    [InlineData("accessToken")]
    [InlineData("apiKey")]
    [InlineData("clientSecret")]
    [InlineData("userPassword")]
    [InlineData("authHeader")]
    [InlineData("sessionId")]
    [InlineData("setCookie")]
    [InlineData("bearerToken")]
    [InlineData("access-token")] // kebab-case must also be caught
    public void RedactAndCap_RedactsEveryTokenLikeKeyword(string fieldName)
    {
        var input = $$"""{"{{fieldName}}":"super-secret-value"}""";
        var redacted = CursorQuotaProbe.RedactAndCap(input, 1024);
        Assert.DoesNotContain("super-secret-value", redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void RedactAndCap_HandlesJsonEscapedQuotesInValue()
    {
        // JSON value contains an escaped quote — the regex value class must
        // match across `\"` so the suffix after the escape doesn't leak.
        var input = """{"sessionToken":"abc\"def","totalPercentUsed":50}""";
        var redacted = CursorQuotaProbe.RedactAndCap(input, 1024);
        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("def", redacted);
        Assert.Contains("<redacted>", redacted);
        Assert.Contains("50", redacted);
    }

    [Fact]
    public void RedactAndCap_TruncatesOversizedBodies()
    {
        var body = new string('x', 5000);
        var redacted = CursorQuotaProbe.RedactAndCap(body, 100);
        Assert.True(redacted.Length <= 100 + 16);
        Assert.EndsWith("[truncated]", redacted);
    }

    [Fact]
    public void ParseResponse_DecimalStringPercents_ParsedInvariantCulture()
    {
        // TryGetDouble is pinned to NumberStyles.Float + invariant culture so
        // a decimal-point fraction parses identically regardless of the runtime
        // locale (some locales would otherwise expect a comma separator).
        var snap = CursorQuotaProbe.ParseResponse("""
        {
          "planUsage": {
            "totalPercentUsed": "55.5",
            "autoPercentUsed": "30.25",
            "apiPercentUsed": "10.0"
          }
        }
        """);
        Assert.Equal(44.5, snap.AvailablePct, precision: 5);
    }

    [Fact]
    public async Task FetchAsync_UnexpectedShape_LogsRawBodyAtDebugRedacted()
    {
        // Diagnosability guarantee: when ParseResponse falls through to
        // Unknown("unexpected response shape"), FetchAsync must log the raw
        // body at Debug so the next shape drift isn't another silent guess.
        // Asserting at the wiring layer guards the LogDebug call itself
        // (removing it or swapping the Notes comparison would not be caught
        // by the ParseResponse unit tests).
        const string unexpectedBody = """{"sessionToken":"leaked-bearer","unrelated":"keep-me"}""";
        var handler = UsageHandler(usageBody: unexpectedBody);
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        var logger = new CapturingLogger<CursorQuotaProbe>();
        var probe = new CursorQuotaProbe(
            factory,
            "test-token",
            TimeSpan.FromSeconds(60),
            logger);

        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(-1, snap.AvailablePct);

        var debugEntries = logger.Entries.Where(e => e.Level == LogLevel.Debug).ToArray();
        Assert.Contains(debugEntries, e =>
            e.Message.Contains("unexpected response shape", StringComparison.Ordinal) &&
            e.Message.Contains("<redacted>", StringComparison.Ordinal) &&
            e.Message.Contains("keep-me", StringComparison.Ordinal) &&
            !e.Message.Contains("leaked-bearer", StringComparison.Ordinal));
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
