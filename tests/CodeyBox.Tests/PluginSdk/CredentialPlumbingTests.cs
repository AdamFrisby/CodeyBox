using System.Net;
using System.Text;
using CodeyBox.PluginSdk.Credentials;

namespace CodeyBox.Tests.PluginSdk;

/// <summary>
/// Verification for the credential-plugin plumbing shared in PluginSdk:
/// message sanitisation flattens every control character (never only
/// CR/LF), the shared error-detail reader applies one bounded/extraction
/// policy per backend, lease-handle construction refuses segments its own
/// parser cannot round-trip, and the capped copy never buffers past
/// cap+1 bytes.
/// </summary>
public sealed class CredentialPlumbingTests
{
    [Fact]
    public void Truncate_Flattens_All_Control_Characters()
    {
        var result = CredentialMessages.Truncate(
            "a\rb\nc\td\u001be\af\u0000g", "fallback", 100);
        Assert.Equal("a b c d e f g", result);
    }

    [Fact]
    public void Truncate_Caps_And_Falls_Back()
    {
        Assert.Equal("abc", CredentialMessages.Truncate("abcdef", "fallback", 3));
        Assert.Equal("fallback", CredentialMessages.Truncate("", "fallback", 3));
        Assert.Equal("fallback", CredentialMessages.Truncate(null, "fallback", 3));
    }

    [Fact]
    public void Truncate_Never_Splits_A_Surrogate_Pair()
    {
        // The cap lands between the emoji's UTF-16 surrogate pair: the lone
        // high surrogate is dropped rather than emitted into a message that
        // strict UTF-8 encoders would mangle downstream.
        var result = CredentialMessages.Truncate("abc\U0001F600def", "fallback", 4);
        Assert.Equal("abc", result);
        Assert.DoesNotContain(result, char.IsSurrogate);
    }

    [Fact]
    public async Task ReadServerDetail_Picks_First_Usable_Field()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"bad request here","error":"ignored"}"""),
        };
        var detail = await CredentialMessages.ReadServerDetailAsync(
            response, ["message", "error"], relayRawText: true, CancellationToken.None);
        Assert.Equal("bad request here", detail);
    }

    [Fact]
    public async Task ReadServerDetail_Falls_To_Next_Field_When_First_Is_Empty()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"   ","error":"second field"}"""),
        };
        var detail = await CredentialMessages.ReadServerDetailAsync(
            response, ["message", "error"], relayRawText: true, CancellationToken.None);
        Assert.Equal("second field", detail);
    }

    [Fact]
    public async Task ReadServerDetail_Flattens_Control_Characters_In_Server_Text()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            // ESC + BEL must not reach logs or the lease store as escapes.
            Content = new StringContent("""{"message":"up\u001bstream\nexploded\u0007"}"""),
        };
        var detail = await CredentialMessages.ReadServerDetailAsync(
            response, ["message"], relayRawText: true, CancellationToken.None);
        Assert.Equal("up stream exploded ", detail);
        Assert.DoesNotContain(detail, c => char.IsControl(c));
    }

    [Fact]
    public async Task ReadServerDetail_Relays_Raw_Body_When_No_Field_Matches()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("not json at all"),
        };
        var detail = await CredentialMessages.ReadServerDetailAsync(
            response, ["message"], relayRawText: true, CancellationToken.None);
        Assert.Equal("not json at all", detail);
    }

    [Fact]
    public async Task ReadServerDetail_Placeholder_When_Raw_Relay_Disabled()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"free text that must not be relayed"}"""),
        };
        var detail = await CredentialMessages.ReadServerDetailAsync(
            response, ["error"], relayRawText: false, CancellationToken.None);
        Assert.Equal(CredentialMessages.NoReadableDetail, detail);
    }

    [Fact]
    public async Task ReadServerDetail_Allowlist_Gates_The_Relayed_Value()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "invalid_client" };
        using var rejected = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"free text not on the list"}"""),
        };
        Assert.Equal(
            CredentialMessages.NoReadableDetail,
            await CredentialMessages.ReadServerDetailAsync(
                rejected, ["error"], relayRawText: false, CancellationToken.None, allowed));

        using var accepted = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_client"}"""),
        };
        Assert.Equal(
            "invalid_client",
            await CredentialMessages.ReadServerDetailAsync(
                accepted, ["error"], relayRawText: false, CancellationToken.None, allowed));
    }

    [Fact]
    public async Task ReadServerDetail_Unreadable_Body_Yields_Placeholder()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new FaultingStream(new IOException("boom"))),
        };
        var detail = await CredentialMessages.ReadServerDetailAsync(
            response, ["message"], relayRawText: true, CancellationToken.None);
        Assert.Equal(CredentialMessages.NoReadableDetail, detail);
    }

    [Fact]
    public async Task ReadServerDetail_Caller_Cancellation_Propagates()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new FaultingStream(new OperationCanceledException())),
        };
        // Caller cancellation is never swallowed as "no readable body".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CredentialMessages.ReadServerDetailAsync(
                response, ["message"], relayRawText: true, CancellationToken.None));
    }

    [Fact]
    public void LeaseHandle_Build_RoundTrips()
    {
        var handle = LeaseHandles.Build("acme", "s", "MY_VAR", "server-lease-1");
        Assert.Equal("acme.s.MY_VAR.server-lease-1", handle);
        Assert.True(LeaseHandles.TryParse(handle, "acme", out var kind, out var envVar, out var tail));
        Assert.Equal("s", kind);
        Assert.Equal("MY_VAR", envVar);
        Assert.Equal("server-lease-1", tail);
    }

    [Theory]
    [InlineData("has.dot")]
    [InlineData("has\nnewline")]
    [InlineData("has\u001bescape")]
    [InlineData("   ")]
    [InlineData("")]
    public void LeaseHandle_Build_Rejects_Segments_Its_Parser_Cannot_RoundTrip(string tail)
    {
        Assert.Throws<ArgumentException>(
            () => LeaseHandles.Build("acme", "s", "MY_VAR", tail));
    }

    [Fact]
    public void LeaseHandle_Build_Rejects_Overlong_Handle()
    {
        var tail = new string('x', LeaseHandles.MaxHandleLength);
        Assert.Throws<ArgumentException>(
            () => LeaseHandles.Build("acme", "s", "MY_VAR", tail));
    }

    [Theory]
    [InlineData("acme.s.MY_VAR.tail\nforged")]
    [InlineData("acme.s.MY_VAR.tail\u001bescape")]
    [InlineData("acme.s.MY_VAR.ta\u0000il")]
    [InlineData("acme.s.MY_VAR.   ")]
    [InlineData("acme..MY_VAR.tail")]
    [InlineData("other.s.MY_VAR.tail")]
    public void LeaseHandle_Parse_Rejects_Segments_Build_Would_Not_Mint(string handle)
    {
        // A parsed handle is always a shape Build could emit: control
        // characters and blank or foreign segments never parse, so a forged
        // lease id cannot smuggle escapes into provider logs.
        Assert.False(LeaseHandles.TryParse(handle, "acme", out _, out _, out _));
    }

    [Fact]
    public async Task CopyCapped_Never_Returns_More_Than_Cap_Plus_One()
    {
        var source = new MemoryStream(new byte[10 * 1024]);
        var (bytes, truncated) = await CredentialBodies.CopyCappedAsync(source, 100, CancellationToken.None);
        Assert.True(truncated);
        Assert.Equal(101, bytes.Length);
    }

    [Fact]
    public async Task CopyCapped_Passes_Under_Cap_Through()
    {
        var payload = Encoding.UTF8.GetBytes("small body");
        var (bytes, truncated) = await CredentialBodies.CopyCappedAsync(
            new MemoryStream(payload), 100, CancellationToken.None);
        Assert.False(truncated);
        Assert.Equal(payload, bytes);
    }

    [Theory]
    [InlineData(401, CredentialFailureKind.Unauthorized)]
    [InlineData(403, CredentialFailureKind.Unauthorized)]
    [InlineData(404, CredentialFailureKind.NotFound)]
    [InlineData(429, CredentialFailureKind.RateLimited)]
    [InlineData(500, CredentialFailureKind.BackendError)]
    [InlineData(503, CredentialFailureKind.BackendError)]
    [InlineData(400, CredentialFailureKind.Misconfigured)]
    [InlineData(418, CredentialFailureKind.Misconfigured)]
    [InlineData(302, CredentialFailureKind.InvalidResponse)]
    [InlineData(200, CredentialFailureKind.InvalidResponse)]
    public void KindForStatus_Maps_To_Shared_Taxonomy(int status, CredentialFailureKind expected)
        => Assert.Equal(expected, CredentialException.KindForStatus(status, null));

    [Fact]
    public void KindForStatus_503_With_RetryAfter_Is_Throttled()
        => Assert.Equal(CredentialFailureKind.Throttled, CredentialException.KindForStatus(503, 30));

    [Fact]
    public async Task Transport_Refuses_Credentials_To_NonHttps_NonLoopback_Target()
    {
        // The scheme guard sits at the sink: a plain-http non-loopback
        // target is refused before one byte is sent.
        var handler = new CountingHandler((request, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var http = new HttpClient(handler);
        var transport = CreateTransport(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://secrets.example.com/v1/x");
        var ex = await Assert.ThrowsAsync<TestCredentialException>(
            () => transport.SendAsync(request, "read secret", CancellationToken.None));
        Assert.Equal(CredentialFailureKind.Misconfigured, ex.Kind);
        Assert.False(ex.IsInfrastructure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Transport_Allows_Loopback_Http_Target()
    {
        var handler = new CountingHandler((request, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            }));
        using var http = new HttpClient(handler);
        var transport = CreateTransport(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9/v1/x");
        using var response = await transport.SendAsync(request, "read secret", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Transport_Refuses_Redirect_Without_Following()
    {
        var handler = new CountingHandler((request, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://other.example.com/landing") },
            }));
        using var http = new HttpClient(handler);
        var transport = CreateTransport(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/x");
        var ex = await Assert.ThrowsAsync<TestCredentialException>(
            () => transport.SendAsync(request, "read secret", CancellationToken.None));
        Assert.Equal(CredentialFailureKind.InvalidResponse, ex.Kind);
        Assert.True(ex.IsInfrastructure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Transport_Handler_Cancel_Without_Request_Cancel_Is_Unreachable()
    {
        // A handler-side cancellation the caller did not request is the
        // client's timeout firing — a backend fault, not caller abort.
        var handler = new CountingHandler((request, ct) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException()));
        using var http = new HttpClient(handler);
        var transport = CreateTransport(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/x");
        var ex = await Assert.ThrowsAsync<TestCredentialException>(
            () => transport.SendAsync(request, "read secret", CancellationToken.None));
        Assert.Equal(CredentialFailureKind.Unreachable, ex.Kind);
        Assert.True(ex.IsInfrastructure);
    }

    [Fact]
    public async Task Transport_Caller_Cancellation_Propagates()
    {
        var handler = new CountingHandler((request, ct) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException(ct)));
        using var http = new HttpClient(handler);
        var transport = CreateTransport(http);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/x");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync(request, "read secret", cts.Token));
    }

    [Fact]
    public async Task Transport_Classifies_Failure_Status_And_Carries_RetryAfter()
    {
        var handler = new CountingHandler((request, ct) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("""{"message":"slow\ndown"}"""),
            };
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var transport = CreateTransport(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/x");
        var ex = await Assert.ThrowsAsync<TestCredentialException>(
            () => transport.SendAsync(request, "read secret", CancellationToken.None));
        Assert.Equal(CredentialFailureKind.RateLimited, ex.Kind);
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(120, ex.RetryAfterSeconds);
        Assert.True(ex.IsInfrastructure);
        // Server text is sanitised (newline flattened) before relay.
        Assert.Contains("slow down", ex.Message, StringComparison.Ordinal);
    }

    private static CredentialTransport CreateTransport(HttpClient http)
        => new(http, "TestBackend", TestCredentialException.Create, ["message"], relayRawErrorText: true);

    private sealed class TestCredentialException : CredentialException
    {
        public TestCredentialException(
            CredentialFailureKind kind, string message, int? statusCode = null, int? retryAfterSeconds = null)
            : base("TestBackend", kind, message, statusCode, retryAfterSeconds)
        {
        }

        public static TestCredentialException Create(
            CredentialFailureKind kind, string message, Exception? inner, int? statusCode, int? retryAfterSeconds)
            => new(kind, message, statusCode, retryAfterSeconds);
    }

    private sealed class CountingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return handler(request, cancellationToken);
        }
    }

    private sealed class FaultingStream(Exception fault) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw fault;
        public override long Position
        {
            get => 0;
            set => throw fault;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw fault;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => throw fault;
        public override long Seek(long offset, SeekOrigin origin) => throw fault;
        public override void SetLength(long value) => throw fault;
        public override void Write(byte[] buffer, int offset, int count) => throw fault;
    }
}
