using System.Net;
using System.Text;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Proves every OAuth refresh response body flows through the single shared
/// bounded reader (<see cref="OauthCredentialFileRefresher.SendBoundedRefreshAsync"/>)
/// with the cap enforced <i>before</i> buffering, and that CLI child-process
/// output captured by the shared refresher helper is truncated rather than
/// accumulated without limit.
/// </summary>
[Collection("Process environment")]
public sealed class OauthRefreshBoundsTests : IDisposable
{
    private readonly string _dir;

    public OauthRefreshBoundsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-refresh-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteCreds(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static string ClaudeCreds(string refresh)
        => $$"""{ "claudeAiOauth": { "accessToken": "old", "refreshToken": "{{refresh}}", "expiresAt": {{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}} } }""";

    private static string GeminiCreds(string refresh)
        => $$"""{"access_token":"old","refresh_token":"{{refresh}}","client_id":"c","client_secret":"s","expiry_date":{{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()}}}""";

    private static string CodexCreds(string refresh)
        => $$"""{"tokens":{"id_token":"id-1","access_token":"{{CodexAccessJwt(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds())}}","refresh_token":"{{refresh}}","account_id":"acct-1"} }""";

    private static string CodexAccessJwt(long expSeconds)
    {
        static string Encode(string json)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode($$"""{"exp":{{expSeconds}}}""")}.";
    }

    private static string PadToExactBytes(string json, int size)
    {
        var padding = size - Encoding.UTF8.GetByteCount(json);
        Assert.True(padding >= 0, $"Fixture JSON ({json.Length} chars) already exceeds {size} bytes.");
        return json + new string(' ', padding);
    }

    // ── Oversize bodies: rejected without full buffering ────────────────────

    [Fact]
    public async Task Claude_Refresh_OversizeBody_RejectedWithoutBuffering()
    {
        var cap = OauthCredentialRefresherBounds.MaxRefreshBodyBytes;
        var handler = new StreamingRefreshHandler(OversizeJson(64 * 1024), contentLength: null);
        var path = WriteCreds("claude.json", ClaudeCreds("rt-claude"));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        Assert.Null(await refresher.GetAccessTokenAsync());
        // Exactly cap+1 bytes are pulled (the reader's one-byte over-read
        // probe) — never the full 64 KiB body.
        Assert.Equal(cap + 1, handler.BytesRead);
    }

    [Fact]
    public async Task Codex_Refresh_OversizeBody_RejectedWithoutBuffering()
    {
        var cap = OauthCredentialRefresherBounds.MaxRefreshBodyBytes;
        var handler = new StreamingRefreshHandler(OversizeJson(64 * 1024), contentLength: null);
        var path = WriteCreds("codex.json", CodexCreds("rt-codex"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var (token, _) = await refresher.GetTokensAsync();
        Assert.Null(token);
        // Exactly cap+1 bytes are pulled (the reader's one-byte over-read
        // probe) — never the full 64 KiB body.
        Assert.Equal(cap + 1, handler.BytesRead);
    }

    [Fact]
    public async Task Gemini_Refresh_OversizeBody_RejectedWithoutBuffering()
    {
        var cap = OauthCredentialRefresherBounds.MaxRefreshBodyBytes;
        var handler = new StreamingRefreshHandler(OversizeJson(64 * 1024), contentLength: null);
        var path = WriteCreds("gemini.json", GeminiCreds("rt-gemini"));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        Assert.Null(await refresher.GetAccessTokenAsync());
        // Exactly cap+1 bytes are pulled (the reader's one-byte over-read
        // probe) — never the full 64 KiB body.
        Assert.Equal(cap + 1, handler.BytesRead);
    }

    [Fact]
    public async Task Refresh_OversizeContentLength_RejectedBeforeReadingBody()
    {
        // A lying-or-huge Content-Length must short-circuit before a single
        // body byte is pulled off the wire.
        var handler = new StreamingRefreshHandler(OversizeJson(64 * 1024), contentLength: 1024 * 1024);
        var path = WriteCreds("claude-len.json", ClaudeCreds("rt-claude"));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        Assert.Null(await refresher.GetAccessTokenAsync());
        Assert.Equal(0, handler.BytesRead);
    }

    private static string OversizeJson(int size)
        => $$"""{"access_token":"{{new string('x', size)}}","expires_in":3600}""";

    // ── Boundary: at the cap succeeds, one byte over fails ───────────────────

    [Fact]
    public async Task Claude_Refresh_BodyAtCap_Succeeds_OneOver_Fails()
    {
        var cap = OauthCredentialRefresherBounds.MaxRefreshBodyBytes;
        var okBody = PadToExactBytes("""{"access_token":"tok-exact","expires_in":28800}""", cap);
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(okBody));
        var overBody = okBody + " ";

        var okPath = WriteCreds("claude-b-ok.json", ClaudeCreds("rt-claude"));
        using (var source = new ClaudeCredentialFileSource(okPath, watch: false))
        using (var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(okBody, contentLength: null)),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance))
        {
            Assert.Equal("tok-exact", await refresher.GetAccessTokenAsync());
        }

        var overPath = WriteCreds("claude-b-over.json", ClaudeCreds("rt-claude"));
        using (var source = new ClaudeCredentialFileSource(overPath, watch: false))
        using (var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(overBody, contentLength: null)),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance))
        {
            Assert.Null(await refresher.GetAccessTokenAsync());
        }
    }

    [Fact]
    public async Task Codex_Refresh_BodyAtCap_Succeeds_OneOver_Fails()
    {
        var cap = OauthCredentialRefresherBounds.MaxRefreshBodyBytes;
        var okBody = PadToExactBytes("""{"access_token":"tok-exact","expires_in":3600}""", cap);
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(okBody));
        var overBody = okBody + " ";

        var okPath = WriteCreds("codex-b-ok.json", CodexCreds("rt-codex"));
        using (var source = new CodexCredentialFileSource(okPath, watch: false))
        using (var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(okBody, contentLength: null)),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance))
        {
            var (token, _) = await refresher.GetTokensAsync();
            Assert.Equal("tok-exact", token);
        }

        var overPath = WriteCreds("codex-b-over.json", CodexCreds("rt-codex"));
        using (var source = new CodexCredentialFileSource(overPath, watch: false))
        using (var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(overBody, contentLength: null)),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance))
        {
            var (token, _) = await refresher.GetTokensAsync();
            Assert.Null(token);
        }
    }

    [Fact]
    public async Task Gemini_Refresh_BodyAtCap_Succeeds_OneOver_Fails()
    {
        var cap = OauthCredentialRefresherBounds.MaxRefreshBodyBytes;
        var okBody = PadToExactBytes("""{"access_token":"tok-exact","expires_in":3600}""", cap);
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(okBody));
        var overBody = okBody + " ";

        var okPath = WriteCreds("gemini-b-ok.json", GeminiCreds("rt-gemini"));
        using (var source = new GeminiOAuthCredentialFileSource(okPath, watch: false))
        using (var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(okBody, contentLength: null)),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance))
        {
            Assert.Equal("tok-exact", await refresher.GetAccessTokenAsync());
        }

        var overPath = WriteCreds("gemini-b-over.json", GeminiCreds("rt-gemini"));
        using (var source = new GeminiOAuthCredentialFileSource(overPath, watch: false))
        using (var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(overBody, contentLength: null)),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance))
        {
            Assert.Null(await refresher.GetAccessTokenAsync());
        }
    }

    // ── One code path: a single shared cap governs all three ─────────────────

    [Fact]
    public async Task AllThreeRefreshers_HonorSharedCap()
    {
        // Lower the ONE shared knob; every refresher must follow it. If a
        // future provider bypassed SendBoundedRefreshAsync with its own read,
        // its oversize body would still parse and this test would go red.
        // (1024 is the minimum accepted cap; the body sits above it.)
        OauthCredentialRefresherBounds.SetMaxRefreshBodyBytes(1024);
        try
        {
            var body = PadToExactBytes("""{"access_token":"tok-shared","expires_in":3600}""", 1200);

            var claudePath = WriteCreds("shared-claude.json", ClaudeCreds("rt"));
            using (var source = new ClaudeCredentialFileSource(claudePath, watch: false))
            using (var refresher = new ClaudeOauthCredentialFileRefresher(
                source,
                new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(body, contentLength: null)),
                NullLogger<ClaudeOauthCredentialFileRefresher>.Instance))
            {
                Assert.Null(await refresher.GetAccessTokenAsync());
            }

            var codexPath = WriteCreds("shared-codex.json", CodexCreds("rt"));
            using (var source = new CodexCredentialFileSource(codexPath, watch: false))
            using (var refresher = new CodexOauthCredentialFileRefresher(
                source,
                new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(body, contentLength: null)),
                NullLogger<CodexOauthCredentialFileRefresher>.Instance))
            {
                var (token, _) = await refresher.GetTokensAsync();
                Assert.Null(token);
            }

            var geminiPath = WriteCreds("shared-gemini.json", GeminiCreds("rt"));
            using (var source = new GeminiOAuthCredentialFileSource(geminiPath, watch: false))
            using (var refresher = new GeminiOauthCredentialFileRefresher(
                source,
                new RefresherFakeHttpClientFactory("agent-quota", new StreamingRefreshHandler(body, contentLength: null)),
                NullLogger<GeminiOauthCredentialFileRefresher>.Instance))
            {
                Assert.Null(await refresher.GetAccessTokenAsync());
            }
        }
        finally
        {
            OauthCredentialRefresherBounds.SetMaxRefreshBodyBytes(
                OauthCredentialRefresherBounds.DefaultMaxRefreshBodyBytes);
        }
    }

    [Fact]
    public void RefreshBounds_DefaultsAndClamping()
    {
        Assert.Equal(8192, OauthCredentialRefresherBounds.DefaultMaxRefreshBodyBytes);
        Assert.Equal(8192, OauthCredentialRefresherBounds.MaxRefreshBodyBytes);

        OauthCredentialRefresherBounds.SetMaxRefreshBodyBytes(0);
        try
        {
            Assert.Equal(OauthCredentialRefresherBounds.MinMaxRefreshBodyBytes,
                OauthCredentialRefresherBounds.MaxRefreshBodyBytes);
        }
        finally
        {
            OauthCredentialRefresherBounds.SetMaxRefreshBodyBytes(
                OauthCredentialRefresherBounds.DefaultMaxRefreshBodyBytes);
        }

        OauthCredentialRefresherBounds.SetMaxCliOutputChars(int.MaxValue);
        try
        {
            Assert.Equal(OauthCredentialRefresherBounds.MaxMaxCliOutputChars,
                OauthCredentialRefresherBounds.MaxCliOutputChars);
        }
        finally
        {
            OauthCredentialRefresherBounds.SetMaxCliOutputChars(
                OauthCredentialRefresherBounds.DefaultMaxCliOutputChars);
        }
    }

    // ── Subprocess output: truncated, still drained ───────────────────────────

    [Fact]
    public async Task BoundedTextReader_TruncatesBeyondCap_AndPassesThroughBelowCap()
    {
        const int cap = 4096;
        var huge = new string('a', cap) + "TAILMARK" + new string('b', cap * 3);
        using var hugeReader = new StringReader(huge);
        var truncated = await OauthCredentialFileRefresher.ReadBoundedTextAsync(hugeReader, cap, CancellationToken.None);
        Assert.Equal(cap, truncated.Length);
        Assert.Equal(huge[..cap], truncated);
        Assert.DoesNotContain("TAILMARK", truncated);

        using var smallReader = new StringReader("hello");
        Assert.Equal("hello", await OauthCredentialFileRefresher.ReadBoundedTextAsync(smallReader, cap, CancellationToken.None));

        using var exactReader = new StringReader(new string('c', cap));
        Assert.Equal(new string('c', cap), await OauthCredentialFileRefresher.ReadBoundedTextAsync(exactReader, cap, CancellationToken.None));
    }

    [Fact]
    public async Task CliRefresh_LargeChildOutput_DoesNotFailTheRefreshDecision()
    {
        // End-to-end through the shared helper: a child emitting ~3 MiB on
        // stdout (over the 1 MiB default per-stream cap) must still have its
        // exit code observed instead of ballooning memory or hanging.
        if (OperatingSystem.IsWindows())
            return;
        if (!File.Exists("/bin/sh"))
            return;

        var ok = await OauthCredentialFileRefresher.ExecuteCliProcessAsync(
            "/bin/sh",
            ["-c", "head -c 3000000 /dev/zero; exit 0"],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.True(ok);

        var failed = await OauthCredentialFileRefresher.ExecuteCliProcessAsync(
            "/bin/sh",
            ["-c", "head -c 3000000 /dev/zero; exit 1"],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.False(failed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StreamingRefreshHandler(string body, long? contentLength) : HttpMessageHandler
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);
        public int BytesRead { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var stream = new CountingStream(_bytes, b => BytesRead += b);
            var content = new StreamContent(stream);
            if (contentLength.HasValue)
                content.Headers.ContentLength = contentLength.Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private sealed class CountingStream(byte[] bytes, Action<int> onRead) : Stream
        {
            private readonly MemoryStream _inner = new(bytes, writable: false);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                onRead(read);
                return read;
            }
            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                onRead(read);
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
