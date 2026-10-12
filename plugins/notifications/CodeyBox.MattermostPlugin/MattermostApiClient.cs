using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.MattermostPlugin;

/// <summary>
/// Typed transport failure from the Mattermost REST API: the request never
/// completed (DNS, connection, TLS). Routine API-level outcomes (HTTP status,
/// timeouts, rate limits) stay as <see cref="MattermostApiClient.PostResult"/>
/// values; only a dead transport throws, so the provider can log it as an
/// error while still swallowing it per the notification contract.
/// </summary>
internal sealed class MattermostApiException : Exception
{
    public MattermostApiException(string errorCode, string message, Exception? inner = null)
        : base($"[{errorCode}] {message}", inner)
    {
    }
}

/// <summary>
/// Thin transport over the Mattermost REST API v4 (<c>POST /api/v4/posts</c>;
/// pinned stable resource path for Mattermost Server v9/v10). The
/// notification token travels per call in the <c>Authorization</c> header —
/// never in the URL, never stored, never logged. Routine failures surface as
/// <see cref="PostResult"/> values (the provider logs and swallows, per the
/// notification contract), a dead transport throws
/// <see cref="MattermostApiException"/>, and cancellation propagates.
/// The sink carries its own guard: the base URL must be a clean absolute
/// http(s) origin on every call and the channel ID must match the platform
/// alphabet, whatever the caller passed. Rate limits (HTTP 429) are honoured
/// with a bounded retry: at most <c>maxRateLimitRetries</c> extra attempts,
/// each waiting the server's <c>Retry-After</c> capped by
/// <c>maxRateLimitWait</c> — never an unbounded sleep or an open retry loop.
/// </summary>
internal sealed class MattermostApiClient
{
    /// <summary>Pinned API version path for posts (stable since Mattermost 4.x).</summary>
    public const string ApiVersionPath = "api/v4";

    /// <summary>Hard cap on the buffered response body. Post envelopes are
    /// small JSON (<c>{id,…}</c> on success, <c>{message,…}</c> on failure);
    /// the cap stops a hostile or malfunctioning peer — or a LAN MITM under
    /// <c>AllowPlainHttp</c> — from streaming an unbounded body into host
    /// memory. The per-call timeout bounds duration; this bounds size.</summary>
    internal const int MaxResponseBodyBytes = 64 * 1024;

    /// <summary>Default back-off when a 429 carries no parsable Retry-After.</summary>
    internal static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public MattermostApiClient(HttpClient http)
    {
        _http = http;
    }

    public sealed record PostResult(bool Ok, string? PostId, string? Error, bool Ambiguous = false);

    /// <summary>True when the channel ID is usable: 1–64 chars from the
    /// platform alphabet (server-issued IDs are 26 lowercase alphanumerics;
    /// the bound admits operator-pasted values without opening the sink to
    /// path injection — the value is always sent as JSON, never as a URL).</summary>
    public static bool IsUsableChannelId(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || channelId.Length > 64)
            return false;
        foreach (var c in channelId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                return false;
        }
        return true;
    }

    /// <summary>True when the server URL is a clean absolute http(s) base
    /// (no query, fragment, or user-info, which would corrupt the derived
    /// posts endpoint or smuggle credentials into the URL).</summary>
    public static bool IsUsableBaseUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return false;
        if (!Uri.TryCreate(serverUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            return false;
        if (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
            return false;
        return string.IsNullOrEmpty(baseUri.Query)
            && string.IsNullOrEmpty(baseUri.Fragment)
            && string.IsNullOrEmpty(baseUri.UserInfo);
    }

    public static string PostsUrl(string serverUrl) => $"{serverUrl.TrimEnd('/')}/{ApiVersionPath}/posts";

    public async Task<PostResult> PostMessageAsync(
        string token,
        string serverUrl,
        string channelId,
        Dictionary<string, object?> payload,
        string? rootId,
        TimeSpan timeout,
        int maxRateLimitRetries,
        TimeSpan maxRateLimitWait,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!IsUsableBaseUrl(serverUrl))
            return new PostResult(false, null, "invalid_server_url");
        if (!IsUsableChannelId(channelId))
            return new PostResult(false, null, "invalid_channel");
        if (!string.IsNullOrWhiteSpace(rootId) && !IsUsableChannelId(rootId))
            return new PostResult(false, null, "invalid_root");

        payload["channel_id"] = channelId.Trim();
        if (!string.IsNullOrWhiteSpace(rootId))
            payload["root_id"] = rootId.Trim();

        var url = PostsUrl(serverUrl);
        var attempts = Math.Clamp(maxRateLimitRetries, 0, 3) + 1;
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await SendOnceAsync(token, url, payload, timeout, ct).ConfigureAwait(false);
            if (outcome is { RateLimited: true } && attempt < attempts)
            {
                var wait = RateLimitWait(outcome.RetryAfter, maxRateLimitWait);
                if (wait is null)
                    return new PostResult(false, null, "rate_limited");
                try
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    delayCts.CancelAfter(timeout);
                    await Task.Delay(wait.Value, delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        throw;
                    return new PostResult(false, null, "timeout");
                }
                continue;
            }
            if (outcome is { RateLimited: true })
                return new PostResult(false, null, "rate_limited");
            return outcome.Result!;
        }
    }

    private sealed record SendOutcome(PostResult? Result, bool RateLimited, TimeSpan? RetryAfter);

    private async Task<SendOutcome> SendOnceAsync(
        string token,
        string url,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        HttpResponseMessage response;
        try
        {
            // Headers-only completion: the body is streamed under
            // MaxResponseBodyBytes below rather than buffered unbounded.
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Our own timeout is the only OCE this scope converts to a
            // result; a requested shutdown — or anyone else's cancellation
            // — propagates so work stops promptly.
            if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                throw;
            return new SendOutcome(new PostResult(false, null, "timeout"), false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MattermostApiException("transport", "Mattermost request did not complete.", ex);
        }

        using (response)
        {
            // The plugin's handler never follows redirects, so a 3xx here is
            // the peer asking for the token to be re-sent to a Location it
            // chose — refused as a fixed-vocabulary failure, never retried.
            if (MattermostHttpClients.IsRedirect(response.StatusCode))
                return new SendOutcome(new PostResult(false, null, $"http-{(int)response.StatusCode}-redirect"), false, null);

            if ((int)response.StatusCode == 429)
                return new SendOutcome(null, true, ParseRetryAfter(response));

            string body;
            try
            {
                var (content, tooLarge) = await ReadBodyBoundedAsync(response, timeoutCts.Token).ConfigureAwait(false);
                if (tooLarge)
                    // The status is already known to be non-429 here; when
                    // it is success the post may exist server-side, so this
                    // outcome is ambiguous — the caller must not blindly
                    // repost under the same correlation token.
                    return new SendOutcome(
                        new PostResult(false, null, "response_too_large", Ambiguous: response.IsSuccessStatusCode),
                        false, null);
                body = content ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                    throw;
                throw new MattermostApiException("timeout", "Mattermost response read timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new MattermostApiException("transport", "Mattermost response could not be read.", ex);
            }

            if (!response.IsSuccessStatusCode)
                return new SendOutcome(new PostResult(false, null, DescribeError(response, body, token)), false, null);

            // A 2xx without a usable post ID is ambiguous: the server very
            // likely created the post, so retrying could duplicate it. The
            // caller records a tombstone instead of reposting.
            try
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;
                return string.IsNullOrEmpty(id)
                    ? new SendOutcome(new PostResult(false, null, "malformed_response", Ambiguous: true), false, null)
                    : new SendOutcome(new PostResult(true, id, null), false, null);
            }
            catch (JsonException)
            {
                return new SendOutcome(new PostResult(false, null, "malformed_response", Ambiguous: true), false, null);
            }
        }
    }

    /// <summary>Resolve the back-off for a 429: the server's Retry-After when
    /// parsable and within the operator cap, else the default wait when it
    /// fits the cap, else null (retry budget exceeded — drop, don't sleep).</summary>
    internal static TimeSpan? RateLimitWait(TimeSpan? retryAfter, TimeSpan maxWait)
    {
        if (maxWait <= TimeSpan.Zero)
            return null;
        var wait = retryAfter ?? DefaultRateLimitWait;
        if (wait <= TimeSpan.Zero)
            wait = DefaultRateLimitWait;
        return wait <= maxWait ? wait : null;
    }

    /// <summary>Parse Retry-After (delta-seconds or HTTP-date) into a wait.
    /// Null when absent or unparsable — the caller falls back to the default.</summary>
    internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var values = response.Headers.RetryAfter;
        if (values is null)
            return null;
        if (values.Delta is { } delta)
            return delta < TimeSpan.Zero ? null : delta;
        if (values.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    /// <summary>Read the response body under a hard byte cap — enforced
    /// before buffering, on the declared length and again while streaming —
    /// so an unbounded body is cut off rather than materialised.</summary>
    private static async Task<(string? Body, bool TooLarge)> ReadBodyBoundedAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBodyBytes)
            return (null, true);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBodyBytes + 1];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        return totalRead > MaxResponseBodyBytes
            ? (null, true)
            : (Encoding.UTF8.GetString(buffer, 0, totalRead), false);
    }

    /// <summary>Extract Mattermost's error envelope text without letting a
    /// hostile or oversized body escape into logs — only the fixed message
    /// field is taken, control and format characters that could forge log
    /// lines are stripped, the bearer token is redacted if the peer echoes
    /// it back, and the result is bounded.</summary>
    private static string DescribeError(HttpResponseMessage response, string body, string token)
    {
        const int maxFieldChars = 200;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                var text = SanitizeForLog(msg.GetString() ?? string.Empty);
                if (!string.IsNullOrEmpty(token) && text.Contains(token, StringComparison.Ordinal))
                    text = text.Replace(token, "<redacted>", StringComparison.Ordinal);
                if (text.Length > maxFieldChars)
                    text = text[..maxFieldChars];
                return $"http-{(int)response.StatusCode}:{text}";
            }
        }
        catch (JsonException)
        {
        }
        return $"http-{(int)response.StatusCode}";
    }

    /// <summary>Replace characters that could forge log structure — line
    /// breaks, ANSI escapes, bidi overrides and other control/format code
    /// points — with spaces.</summary>
    private static string SanitizeForLog(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(
                char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format
                    ? ' '
                    : c);
        }
        return sb.ToString();
    }
}
