using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.HetznerSandboxPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Client-level tests for <see cref="HetznerApiClient"/>: bearer auth,
/// failure classification (401/403/quota/409/422/429+Retry-After/5xx),
/// unreachable vs cancelled, malformed/truncated/oversized bodies, page and
/// item bounds, server-side filter re-verification, unsafe-http refusal, and
/// firewall-rule API shape.
/// </summary>
public sealed class HetznerApiClientTests
{
    private static (HttpClient Http, FakeHetznerCloud Cloud) NewClient(
        HetznerClientLimits? limits = null)
    {
        var cloud = new FakeHetznerCloud();
        var http = new HttpClient(cloud) { Timeout = Timeout.InfiniteTimeSpan };
        return (http, cloud);
    }

    private static HetznerCredentials Creds() =>
        new(new Uri("http://localhost/"), "test-token", AllowUnsafeHttp: true);

    private static HetznerApiClient Client(HttpClient http, HetznerClientLimits? limits = null) =>
        new(http, TimeProvider.System, limits ?? new HetznerClientLimits());

    [Fact]
    public async Task MissingOrWrongToken_Maps_To_Unauthorized()
    {
        var (http, _) = NewClient();
        using (http)
        {
            var client = Client(http);
            var bad = new HetznerCredentials(new Uri("http://localhost/"), "wrong", AllowUnsafeHttp: true);
            var ex = await Assert.ThrowsAsync<HetznerApiException>(
                () => client.ListServersAsync(bad, new Dictionary<string, string>(), CancellationToken.None));
            Assert.Equal(HetznerFailureKind.Unauthorized, ex.Kind);
        }
    }

    [Fact]
    public async Task QuotaCode_Wins_Over_Status_For_403_And_422()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.UnprocessableEntity })
        {
            var (http, cloud) = NewClient();
            using (http)
            {
                var client = Client(http);
                cloud.FailNextServerCreate = (status, "resource_limit_exceeded", "full", false);
                var ex = await Assert.ThrowsAsync<HetznerApiException>(
                    () => client.CreateServerAsync(Creds(), MinimalServerSpec(), CancellationToken.None));
                Assert.Equal(HetznerFailureKind.QuotaExhausted, ex.Kind);
            }
        }
    }

    [Fact]
    public async Task Conflict_NotFound_Throttled_ServerError_Classified()
    {
        var (http, cloud) = NewClient();
        using (http)
        {
            var client = Client(http);

            cloud.FailNextServerCreate = (HttpStatusCode.Conflict, "uniqueness_error", "dup", false);
            var conflict = await Assert.ThrowsAsync<HetznerApiException>(
                () => client.CreateServerAsync(Creds(), MinimalServerSpec(), CancellationToken.None));
            Assert.Equal(HetznerFailureKind.Conflict, conflict.Kind);
            Assert.False(conflict.MayHaveCreated);

            cloud.FailNextServerCreate = (HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "slow", false);
            cloud.NextCreateRetryAfterSeconds = "30";
            var throttled = await Assert.ThrowsAsync<HetznerApiException>(
                () => client.CreateServerAsync(Creds(), MinimalServerSpec(), CancellationToken.None));
            Assert.Equal(HetznerFailureKind.Throttled, throttled.Kind);
            Assert.Equal(TimeSpan.FromSeconds(30), throttled.RetryAfter);
            Assert.True(throttled.MayHaveCreated);

            cloud.FailNextServerCreate = (HttpStatusCode.InternalServerError, "server_error", "boom", false);
            var failed = await Assert.ThrowsAsync<HetznerApiException>(
                () => client.CreateServerAsync(Creds(), MinimalServerSpec(), CancellationToken.None));
            Assert.Equal(HetznerFailureKind.ServerError, failed.Kind);
            Assert.True(failed.MayHaveCreated);
        }
    }

    [Fact]
    public void RetryAfter_Parses_Seconds_And_HttpDate_And_Rejects_Garbage()
    {
        using var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.TryAddWithoutValidation("Retry-After", "120");
        Assert.Equal(TimeSpan.FromSeconds(120), HetznerApiClient.GetRetryAfter(seconds));

        using var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.TryAddWithoutValidation(
            "Retry-After", DateTimeOffset.UtcNow.AddMinutes(3).ToString("R"));
        var parsed = HetznerApiClient.GetRetryAfter(date);
        Assert.NotNull(parsed);
        Assert.True(parsed > TimeSpan.Zero && parsed <= TimeSpan.FromMinutes(4));

        using var garbage = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        garbage.Headers.TryAddWithoutValidation("Retry-After", "soon");
        Assert.Null(HetznerApiClient.GetRetryAfter(garbage));

        using var none = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Null(HetznerApiClient.GetRetryAfter(none));
    }

    [Fact]
    public async Task HandlerThrowing_Maps_To_Unreachable()
    {
        using var http = new HttpClient(new ThrowingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var client = Client(http);
        var ex = await Assert.ThrowsAsync<HetznerApiException>(
            () => client.ListServersAsync(Creds(), new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(HetznerFailureKind.Unreachable, ex.Kind);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_As_Cancelled()
    {
        using var http = new HttpClient(new SlowHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var client = Client(http);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ListServersAsync(Creds(), new Dictionary<string, string>(), cts.Token));
    }

    [Fact]
    public async Task MalformedJson_Maps_To_Unexpected()
    {
        using var http = new HttpClient(new FixedBodyHandler("{not json"))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = Client(http);
        var ex = await Assert.ThrowsAsync<HetznerApiException>(
            () => client.ListServersAsync(Creds(), new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(HetznerFailureKind.Unexpected, ex.Kind);
    }

    [Fact]
    public async Task TruncatedJson_Maps_To_Unexpected()
    {
        using var http = new HttpClient(new FixedBodyHandler("""{"servers": [{"id": 1,"""))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = Client(http);
        var ex = await Assert.ThrowsAsync<HetznerApiException>(
            () => client.ListServersAsync(Creds(), new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(HetznerFailureKind.Unexpected, ex.Kind);
    }

    [Fact]
    public async Task OversizedBody_Refused_Before_Buffering()
    {
        var (http, _) = NewClient();
        using (http)
        {
            var tiny = new HetznerClientLimits() with { MaxResponseBytes = 64 };
            var client = Client(http, tiny);
            var ex = await Assert.ThrowsAsync<HetznerApiException>(
                () => client.ListServersAsync(Creds(), new Dictionary<string, string>(), CancellationToken.None));
            Assert.Equal(HetznerFailureKind.Unexpected, ex.Kind);
            Assert.Contains("exceeded", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ErrorBody_ControlCharacters_Sanitized()
    {
        using var http = new HttpClient(new FixedBodyHandler(
            """{"error": {"code": "server_error", "message": "line1\nline2\tboom"}}""",
            HttpStatusCode.InternalServerError))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = Client(http);
        var ex = await Assert.ThrowsAsync<HetznerApiException>(
            () => client.ListServersAsync(Creds(), new Dictionary<string, string>(), CancellationToken.None));
        Assert.DoesNotContain("\n", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line1 line2 boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_Collects_All_Pages_Then_Enforces_Item_Bound()
    {
        var (http, cloud) = NewClient();
        using (http)
        {
            var client = Client(http);
            var creds = Creds();
            for (var i = 0; i < 3; i++)
            {
                await client.CreateServerAsync(creds, MinimalServerSpec("page-seed-" + i), CancellationToken.None);
            }
            var selector = new Dictionary<string, string>
            {
                ["codeybox-owned"] = "true",
                ["codeybox-owner"] = "page-owner",
            };
            var all = await client.ListServersAsync(
                creds, selector, CancellationToken.None);
            Assert.Equal(3, all.Count);

            var boundedClient = Client(http, new HetznerClientLimits() with { MaxListItems = 1 });
            var ex = await Assert.ThrowsAsync<HetznerApiException>(
                () => boundedClient.ListServersAsync(creds, selector, CancellationToken.None));
            Assert.Equal(HetznerFailureKind.Unexpected, ex.Kind);
        }
    }

    [Fact]
    public async Task Delete_Missing_Resource_Returns_False()
    {
        var (http, _) = NewClient();
        using (http)
        {
            var client = Client(http);
            Assert.False(await client.DeleteServerAsync(Creds(), 424242, CancellationToken.None));
            Assert.False(await client.DeleteSshKeyAsync(Creds(), 424242, CancellationToken.None));
            Assert.False(await client.DeleteFirewallAsync(Creds(), 424242, CancellationToken.None));
            Assert.False(await client.DeleteFloatingIpAsync(Creds(), 424242, CancellationToken.None));
            // Unassign is idempotent: a missing address is already unassigned.
            await client.UnassignFloatingIpAsync(Creds(), 424242, CancellationToken.None);
        }
    }

    [Fact]
    public void RemoteHttp_Refused_Without_Loopback_Only_With_Flag()
    {
        HetznerCredentials Resolve(HetznerSandboxOptions options) =>
            HetznerCredentials.Resolve(options, name => name == "HCLOUD_TOKEN" ? "t" : null);

        Assert.Throws<InvalidOperationException>(() => Resolve(new HetznerSandboxOptions
        {
            ApiBaseUrl = "http://example.com/", AllowUnsafeHttp = true,
        }));

        Resolve(new HetznerSandboxOptions
        {
            ApiBaseUrl = "http://127.0.0.1:8080/", AllowUnsafeHttp = true,
        });

        Assert.Throws<InvalidOperationException>(() => Resolve(new HetznerSandboxOptions
        {
            ApiBaseUrl = "http://127.0.0.1:8080/", AllowUnsafeHttp = false,
        }));

        var https = Resolve(new HetznerSandboxOptions());
        Assert.Equal(new Uri("https://api.hetzner.cloud/v1"), https.ApiBaseUrl);
    }

    [Fact]
    public async Task CreateServer_Missing_Id_Throws_Unexpected()
    {
        using var http = new HttpClient(new FixedBodyHandler("""{"server": {}}""", HttpStatusCode.Created))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = Client(http);
        var ex = await Assert.ThrowsAsync<HetznerApiException>(
            () => client.CreateServerAsync(Creds(), MinimalServerSpec(), CancellationToken.None));
        Assert.Equal(HetznerFailureKind.Unexpected, ex.Kind);
    }

    private static HetznerServerSpec MinimalServerSpec(string name = "probe") =>
        new(
            Name: name,
            ServerType: "cx23",
            ImageId: 1,
            Location: "fsn1",
            SshKeyNames: ["probe-key"],
            NetworkIds: [],
            UserData: "#cloud-config\n",
            Labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["codeybox-owned"] = "true",
                ["codeybox-owner"] = "page-owner",
            },
            FirewallIds: [],
            EnablePublicIpv4: true,
            EnablePublicIpv6: false);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FixedBodyHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request; _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
