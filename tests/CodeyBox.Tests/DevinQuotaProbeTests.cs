using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinQuotaProbe"/> using a fake HTTP message handler.
/// The wire shape is hand-encoded protobuf matching the live contract
/// decoded from devin 3000.11.1 traffic (2026-09-21):
/// <c>GetUserStatusResponse{1: user_status{13: plan_status{14..20}},
/// 2: plan_info{2: plan_name}}</c>.
/// </summary>
public sealed class DevinQuotaProbeTests
{
    private static readonly AgentMembership AnyMember = new()
    {
        Agent = AgentKind.Devin,
        Billing = AgentBilling.Subscription,
        ModelId = "claude-sonnet-5-medium",
        QualityScore = 90,
    };

    private const string Endpoint = "https://devin-api.example";

    /// <summary>
    /// Encodes a GetUserStatusResponse carrying the given plan_status fields
    /// (proto field numbers as on the wire).
    /// </summary>
    private static byte[] QuotaResponse(
        ulong? dailyPct = 80, ulong? weeklyPct = 40,
        ulong? dailyReset = 1800000000, ulong? weeklyReset = 1800500000,
        ulong? overageMicros = null, ulong? acuConsumed = null, ulong? acuLimit = null,
        string? planName = "Pro")
    {
        var planStatus = new DevinProtoWire.MessageWriter();
        if (dailyPct is { } dp) planStatus.Field(14, dp);
        if (weeklyPct is { } wp) planStatus.Field(15, wp);
        if (overageMicros is { } om) planStatus.Field(16, om);
        if (dailyReset is { } dr) planStatus.Field(17, dr);
        if (weeklyReset is { } wr) planStatus.Field(18, wr);
        if (acuConsumed is { } ac) planStatus.Field(19, ac);
        if (acuLimit is { } al) planStatus.Field(20, al);

        var userStatus = new DevinProtoWire.MessageWriter().Field(13, planStatus);
        var response = new DevinProtoWire.MessageWriter().Field(1, userStatus);
        if (planName is not null)
            response.Field(2, new DevinProtoWire.MessageWriter().Field(2, planName));
        return response.ToArray();
    }

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
        byte[]? body = null,
        Action<HttpRequestMessage>? capture = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        return new QuotaUrlRoutingHandler(req =>
        {
            capture?.Invoke(req);
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body ?? QuotaResponse()),
            };
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
    public async Task Probe_SendsBasicAuthAndProtoBody()
    {
        // Verified 2026-09-21 by intercepting the real CLI: Basic scheme with
        // the credentials token verbatim, application/proto body, Connect
        // protocol header, and the token echoed inside the metadata envelope
        // at metadata field 3.
        string? auth = null;
        string? contentType = null;
        string? connectVersion = null;
        byte[]? body = null;
        HttpMethod? method = null;
        var handler = UsageHandler(capture: req =>
        {
            method = req.Method;
            auth = req.Headers.Authorization?.ToString();
            contentType = req.Content?.Headers.ContentType?.ToString();
            connectVersion = req.Headers.TryGetValues("Connect-Protocol-Version", out var v)
                ? string.Join(",", v) : null;
            body = req.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        });

        var probe = BuildProbe(handler, token: "devin-session-token$test");
        await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("Basic devin-session-token$test", auth);
        Assert.Equal("application/proto", contentType);
        Assert.Equal("1", connectVersion);
        Assert.NotNull(body);
        var requestMessage = DevinProtoWire.Parse(body!);
        var metadata = requestMessage.TryGetMessage(1);
        Assert.NotNull(metadata);
        Assert.Equal("devin-session-token$test", metadata!.TryGetString(3));
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
    public async Task Parse_DailyAndWeekly_MinWindowWins()
    {
        var probe = BuildProbe(UsageHandler());
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(40, snap.AvailablePct);
        Assert.Equal(2, snap.Windows.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800500000), snap.ResetAt);
        Assert.Contains("plan Pro", snap.Notes);
        // The plan windows are the primary bucket — never mapped to
        // BalanceRemaining.
        Assert.Null(snap.BalanceRemaining);
    }

    [Fact]
    public async Task Parse_OverageBalance_SurfacedInNotes()
    {
        var probe = BuildProbe(UsageHandler(body: QuotaResponse(
            dailyPct: 100, weeklyPct: 100, overageMicros: 10_000_000, planName: null)));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(100, snap.AvailablePct);
        Assert.Contains("overage balance $10", snap.Notes);
    }

    [Fact]
    public async Task Parse_UnsetSentinelFields_TreatedAsAbsent()
    {
        // proto3 serialises unset int64 fields as -1 (ulong.MaxValue) — those
        // are absent data, not a 1.8e19 percent quota.
        var probe = BuildProbe(UsageHandler(body: QuotaResponse(
            dailyPct: 65, weeklyPct: null, dailyReset: ulong.MaxValue)));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(65, snap.AvailablePct);
        Assert.Single(snap.Windows);
        Assert.Null(snap.Windows[0].ResetAt);
    }

    [Fact]
    public async Task Parse_AcuCountersOnly_DerivesRemaining()
    {
        // Responses lacking the percent fields but carrying ACU counters get
        // a derived remaining share rather than degrading to unknown.
        var probe = BuildProbe(UsageHandler(body: QuotaResponse(
            dailyPct: null, weeklyPct: null, dailyReset: null, weeklyReset: null,
            acuConsumed: 750, acuLimit: 1000)));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);

        Assert.True(snap.IsKnown);
        Assert.Equal(25, snap.AvailablePct);
        Assert.Single(snap.Windows, w => w.Name == "acu");
    }

    [Fact]
    public async Task Parse_NotProto_UnknownNotZero()
    {
        var probe = BuildProbe(UsageHandler(body: "not proto \x01\x02\xff"u8.ToArray()));
        var snap = await probe.GetAvailabilityAsync(AnyMember, CancellationToken.None);
        Assert.False(snap.IsKnown);
        Assert.Equal(QuotaUnknownReason.Permanent, snap.Unknown);
    }

    [Fact]
    public async Task Parse_NoQuotaFields_UnknownNotZero()
    {
        // Valid proto but no plan_status payload — e.g. a response that only
        // carries unrelated user fields.
        var body = new DevinProtoWire.MessageWriter()
            .Field(1, new DevinProtoWire.MessageWriter().Field(7, "a@b.c"))
            .ToArray();
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
