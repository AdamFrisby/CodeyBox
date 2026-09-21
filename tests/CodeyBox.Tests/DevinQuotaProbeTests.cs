using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinQuotaProbe"/> using a fake HTTP message handler.
/// Field shape from the devin 3000.11.1 binary's Connect-RPC proto
/// (<c>GetUserStatusResponse.user_status.plan_status</c>); protojson may emit
/// camelCase so both spellings are exercised.
/// </summary>
public sealed class DevinQuotaProbeTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Devin,
        Billing = AgentBilling.Subscription,
        ModelId = "claude-sonnet-4",
        QualityScore = 90,
    };

    private const string Endpoint = "https://devin-api.example";

    private const string SnakeCaseBody = """
    {
      "user_status": {
        "plan_status": {
          "daily_quota_remaining_percent": 80,
          "weekly_quota_remaining_percent": 40,
          "daily_quota_reset_at_unix": 1800000000,
          "weekly_quota_reset_at_unix": 1800500000,
          "available_prompt_credits": 12.5,
          "available_flex_credits": 3
        },
        "plan_info": { "plan_name": "core" }
      }
    }
    """;

    private static DevinQuotaProbe BuildProbe(
        HttpMessageHandler handler,
        string? token = "test-token",
        string? endpoint = Endpoint,
        TimeSpan? cacheTtl = null)
    {
        var factory = new QuotaFakeHttpClientFactory("agent-quota", handler);
        return new DevinQuotaProbe(
            factory,
            token,
            endpoint,
            cacheTtl ?? TimeSpan.FromSeconds(60),
            NullLogger<DevinQuotaProbe>.Instance);
    }

    private static QuotaUrlRoutingHandler UsageHandler(
        string body = SnakeCaseBody,
        Action<HttpRequestMessage>? capture = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        return new QuotaUrlRoutingHandler(req =>
        {
            capture?.Invoke(req);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        });
    }

    [Fact]
    public void Kind_IsDevin()
    {
        var probe = BuildProbe(UsageHandler());
        Assert.Equal(AgentKind.Devin, probe.Kind);
    }

    [Fact]
    public async Task Probe_PostsToLoginAssignedEndpoint()
    {
        // The API host comes from credentials.toml (api.devin.ai does not
        // serve this RPC) — pin that the request goes to the credential's
        // base URL plus the fixed method path.
        var calledUris = new List<Uri?>();
        var handler = UsageHandler(capture: req => calledUris.Add(req.RequestUri));

        var probe = BuildProbe(handler);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(
            [new Uri("https://devin-api.example/exa.seat_management_pb.SeatManagementService/GetUserStatus")],
            calledUris);
    }

    [Fact]
    public async Task Probe_SendsBearerTokenAndJsonBody()
    {
        string? auth = null;
        string? body = null;
        HttpMethod? method = null;
        var handler = UsageHandler(capture: req =>
        {
            method = req.Method;
            auth = req.Headers.Authorization?.ToString();
            body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        });

        var probe = BuildProbe(handler, token: "devin-api-key");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("Bearer devin-api-key", auth);
        Assert.Equal("{}", body);
    }

    [Fact]
    public async Task NoToken_ReturnsUnknownWithoutHttpCall()
    {
        var callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);
        var probe = BuildProbe(handler, token: null);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Equal(QuotaUnknownReason.NoCredential, snap.Unknown);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task NoEndpoint_ReturnsUnknownWithoutHttpCall()
    {
        // No usable api_server_url → fail closed. There is no safe default
        // host (api.devin.ai 404s this RPC).
        var callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);
        var probe = BuildProbe(handler, endpoint: null);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Equal(QuotaUnknownReason.NoCredential, snap.Unknown);
        Assert.Equal(0, callCount);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://evil.example")]
    [InlineData("relative/path")]
    public async Task MalformedEndpoint_NeverReachesHttp(string endpoint)
    {
        // The credentials file is operator-controlled but still crosses a
        // trust boundary into an outbound request target — only absolute
        // http(s) bases are accepted at the sink.
        var calledUris = new List<Uri?>();
        var handler = UsageHandler(capture: req => calledUris.Add(req.RequestUri));
        var probe = BuildProbe(handler, endpoint: endpoint);
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Empty(calledUris);
    }

    [Fact]
    public async Task Parse_SnakeCase_MinWindowWins()
    {
        var probe = BuildProbe(UsageHandler());
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(40, snap.AvailablePct);
        Assert.Equal(2, snap.Windows.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800500000), snap.ResetAt);
        Assert.Contains("plan core", snap.Notes);
        Assert.Contains("top-up credits", snap.Notes);
        // Credits are surfaced in notes, not BalanceRemaining — the plan
        // windows are the primary bucket.
        Assert.Null(snap.BalanceRemaining);
    }

    [Fact]
    public async Task Parse_CamelCase_SameReading()
    {
        const string body = """
        {
          "userStatus": {
            "planStatus": {
              "dailyQuotaRemainingPercent": 90,
              "weeklyQuotaRemainingPercent": 55,
              "weeklyQuotaResetAtUnix": "1800500000"
            },
            "planInfo": { "planName": "team" }
          }
        }
        """;
        var probe = BuildProbe(UsageHandler(body: body));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(55, snap.AvailablePct);
        Assert.Contains("plan team", snap.Notes);
    }

    [Fact]
    public async Task Parse_StringEncodedNumerics_Accepted()
    {
        // protojson serialises int64/float fields as strings.
        const string body = """
        {
          "user_status": {
            "plan_status": {
              "daily_quota_remaining_percent": "67.5",
              "daily_quota_reset_at_unix": "1800000000"
            }
          }
        }
        """;
        var probe = BuildProbe(UsageHandler(body: body));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(67.5, snap.AvailablePct);
    }

    [Fact]
    public async Task Parse_AcuCountersOnly_DerivesRemaining()
    {
        // Older responses may lack the percent fields but carry ACU
        // consumption; the probe derives the remaining share rather than
        // degrading to unknown.
        const string body = """
        {
          "user_status": {
            "plan_status": { "acu_consumed": 750, "acu_limit": 1000 }
          }
        }
        """;
        var probe = BuildProbe(UsageHandler(body: body));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(25, snap.AvailablePct);
        Assert.Single(snap.Windows, w => w.Name == "acu");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"user_status":{}}""")]
    [InlineData("""{"user_status":{"plan_status":{"grace_period_status":"ok"}}}""")]
    public async Task Parse_NoQuotaFields_UnknownNotZero(string body)
    {
        var probe = BuildProbe(UsageHandler(body: body));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snap.Unknown);
        Assert.Equal("unexpected response shape", snap.Notes);
    }

    [Fact]
    public async Task Http401_PermanentUnknown()
    {
        var probe = BuildProbe(UsageHandler(status: HttpStatusCode.Unauthorized));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snap.Unknown);
    }

    [Fact]
    public async Task Http503_TransientUnknown()
    {
        var probe = BuildProbe(UsageHandler(status: HttpStatusCode.ServiceUnavailable));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Equal(QuotaUnknownReason.Transient, snap.Unknown);
    }

    [Fact]
    public async Task Cache_HitsWithinTtl_InvalidatorClears()
    {
        var callCount = 0;
        var handler = UsageHandler(capture: _ => callCount++);
        var probe = BuildProbe(handler);

        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(1, callCount);

        probe.InvalidateCache();
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.Equal(2, callCount);
    }
}
