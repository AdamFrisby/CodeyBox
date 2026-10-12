using System.Net;
using CodeyBox.MattermostPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Transport-level tests for the Mattermost REST client: the peer is a
/// network boundary, so its redirects are refused (the credential-bearing
/// request is never re-sent to a server-chosen host), its response body is
/// buffered under a hard byte cap, rate limits surface as bounded retries,
/// ambiguous 2xx-without-id bodies are flagged (never silently treated as
/// delivered), and its error text is sanitized before it can reach logs.
/// Each test drives the real client against a captured handler and asserts
/// on the returned <see cref="MattermostApiClient.PostResult"/>.
/// </summary>
public sealed class MattermostApiClientTests
{
    private const string Token = "test-mattermost-token-value";
    private const string Server = "https://mattermost.example.invalid";
    private const string Channel = "abcdefghij1234567890abcdef";

    private static Dictionary<string, object?> Payload() => new()
    {
        ["message"] = "hello",
    };

    private static MattermostApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new CapturingHttpHandler(responder)));

    private static Task<MattermostApiClient.PostResult> PostAsync(
        MattermostApiClient api,
        Dictionary<string, object?>? payload = null,
        TimeSpan? timeout = null,
        int retries = 1,
        TimeSpan? maxWait = null) =>
        api.PostMessageAsync(
            Token, Server, Channel, payload ?? Payload(), null,
            timeout ?? TimeSpan.FromSeconds(15), retries,
            maxWait ?? TimeSpan.FromSeconds(30), CancellationToken.None);

    [Fact]
    public async Task Success_ReturnsPostId()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"postid00000000000000000001","channel_id":"c"}"""),
        });

        var result = await PostAsync(api);

        Assert.True(result.Ok);
        Assert.Equal("postid00000000000000000001", result.PostId);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public async Task SuccessWithoutId_IsAmbiguous()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"channel_id":"c"}"""),
        });

        var result = await PostAsync(api);

        Assert.False(result.Ok);
        Assert.Equal("malformed_response", result.Error);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task NonJsonBody_IsAmbiguous()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("not json at all {{{"),
        });

        var result = await PostAsync(api);

        Assert.False(result.Ok);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed_SurfacesAsFailure()
    {
        // A 3xx is the peer asking for the bearer token to be re-sent to a
        // Location it chose. The plugin's own handler never follows
        // redirects; the client additionally surfaces any 3xx as a
        // fixed-vocabulary failure rather than an implicit retry target.
        var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://attacker.invalid/collect") },
        });
        var api = new MattermostApiClient(new HttpClient(handler));

        var result = await PostAsync(api);

        Assert.False(result.Ok);
        Assert.Equal("http-302-redirect", result.Error);
        Assert.False(result.Ambiguous);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedBody_DeclaredLength_ReturnsTooLarge()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(new string('x', MattermostApiClient.MaxResponseBodyBytes + 1)),
        });

        var result = await PostAsync(api);

        Assert.False(result.Ok);
        Assert.Equal("response_too_large", result.Error);
    }

    [Fact]
    public async Task Unauthorized_RedactsToken_SanitizesControls()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $"{{\"message\":\"bad token {Token}\\n\\u001b[31mred\",\"status_code\":401}}"),
        });

        var result = await PostAsync(api);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.StartsWith("http-401:", result.Error);
        Assert.DoesNotContain(Token, result.Error);
        Assert.DoesNotContain('\n', result.Error);
        Assert.DoesNotContain('\u001b', result.Error);
        Assert.False(result.Error.Contains('\n'));
        Assert.False(result.Error.Contains('\u001b'));
    }

    [Fact]
    public async Task InvalidChannel_RejectedWithoutHttpCall()
    {
        var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var api = new MattermostApiClient(new HttpClient(handler));

        var result = await api.PostMessageAsync(
            Token, Server, "no spaces allowed!", Payload(), null,
            TimeSpan.FromSeconds(15), 1, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("invalid_channel", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RateLimitThenSuccess_RetriesWithSamePayload()
    {
        var calls = 0;
        var handler = new CapturingHttpHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":"postid00000000000000000009"}"""),
            };
        });
        var api = new MattermostApiClient(new HttpClient(handler));

        var result = await PostAsync(api);

        Assert.True(result.Ok);
        Assert.Equal("postid00000000000000000009", result.PostId);
        Assert.Equal(2, calls);
        foreach (var request in handler.Requests)
            Assert.Equal($"Bearer {Token}", request.Authorization);
    }

    [Fact]
    public async Task TimeoutSurfaces_AsResult_NotThrow()
    {
        var api = new MattermostApiClient(new HttpClient(new HangingHandler()));

        var result = await PostAsync(api, timeout: TimeSpan.FromMilliseconds(100));

        Assert.False(result.Ok);
        Assert.Equal("timeout", result.Error);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var api = new MattermostApiClient(new HttpClient(new HangingHandler()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.PostMessageAsync(
                Token, Server, Channel, Payload(), null,
                TimeSpan.FromSeconds(15), 1, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task RateLimitBackoff_HonoursCallerCancellation()
    {
        var handler = new CapturingHttpHandler(_ =>
        {
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return limited;
        });
        var api = new MattermostApiClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Max wait (1h) admits the 60s ask, so the client starts the back-off
        // sleep — the caller's cancellation must abort it, not sleep it out.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.PostMessageAsync(
                Token, Server, Channel, Payload(), null,
                TimeSpan.FromSeconds(30), 1, TimeSpan.FromHours(1), cts.Token));
    }

    /// <summary>Handler that never answers until the caller's token fires,
    /// so timeout/cancellation paths execute for real.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
