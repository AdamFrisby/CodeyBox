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
