using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.GotifyPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Transport-level tests for the Gotify REST client: the peer is a network
/// boundary, so its redirects are refused (the credential-bearing request is
/// never re-sent to a server-chosen host), its response body is buffered
/// under a hard byte cap, and its error text is sanitized before it can
/// reach logs. Each test drives the real client against a captured handler
/// and asserts on the returned <see cref="GotifyApiClient.PostResult"/>.
/// </summary>
public sealed class GotifyApiClientTests
{
    private const string Token = "A-test-app-token-value";
    private static readonly Uri Endpoint = new("https://gotify.example.invalid/message");

    private static Dictionary<string, object?> Payload() => new()
    {
        ["title"] = "t",
        ["message"] = "m",
        ["priority"] = 5,
    };

    private static GotifyApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new CapturingHttpHandler(responder)));

    [Fact]
    public async Task Redirect_IsNotFollowed_SurfacesAsFailure()
    {
        // A 3xx is the peer asking for the X-Gotify-Key to be re-sent to a
        // Location it chose. The plugin's own handler never follows
        // redirects; the client additionally surfaces any 3xx as a
        // fixed-vocabulary failure rather than an implicit retry target.
        var handler = new CapturingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://attacker.invalid/collect") },
        });
        var api = new GotifyApiClient(new HttpClient(handler));

        var result = await api.PostMessageAsync(Token, Endpoint, Payload(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("http-302-redirect", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedBody_DeclaredLength_ReturnsTooLarge()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', GotifyApiClient.MaxResponseBodyBytes + 1)),
        });

        var result = await api.PostMessageAsync(Token, Endpoint, Payload(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("response_too_large", result.Error);
    }

    [Fact]
    public async Task OversizedBody_UndeclaredLength_ReturnsTooLarge()
    {
        // No Content-Length: the streaming cap must still cut the body off.
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[GotifyApiClient.MaxResponseBodyBytes + 64]),
        });

        var result = await api.PostMessageAsync(Token, Endpoint, Payload(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("response_too_large", result.Error);
    }

    [Fact]
    public async Task ErrorEnvelope_ControlCharacters_AreStripped()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"errorCode":400,"error":"bad\nrequest\u001b[31m forged"}"""),
        });

        var result = await api.PostMessageAsync(Token, Endpoint, Payload(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain('\n', result.Error);
        Assert.DoesNotContain('\u001b', result.Error);
        Assert.Contains("bad request", result.Error);
    }

    [Fact]
    public async Task ErrorEnvelope_EchoedToken_IsRedacted()
    {
        // A hostile or nonstandard server could echo request material back;
        // the application token must never reach the provider's logs.
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { errorCode = 401, error = $"invalid token {Token}" })),
        });

        var result = await api.PostMessageAsync(Token, Endpoint, Payload(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(Token, result.Error);
        Assert.Contains("<redacted>", result.Error);
    }

    [Fact]
    public async Task Success_ParsesMessageId()
    {
        var api = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":42,"appid":7,"message":"m","title":"t","priority":5}"""),
        });

        var result = await api.PostMessageAsync(Token, Endpoint, Payload(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(42, result.MessageId);
    }

    /// <summary>Response body with no declared Content-Length, so the
    /// streaming byte cap — not the header pre-check — is what bounds it.</summary>
    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _data;

        public UnknownLengthContent(byte[] data) => _data = data;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
