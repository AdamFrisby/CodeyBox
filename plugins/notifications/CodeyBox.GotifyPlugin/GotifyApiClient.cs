using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Typed transport failure from the Gotify REST API: the request never
/// completed (DNS, connection, TLS), or the response body stalled past the
/// call timeout. Routine API-level outcomes (HTTP status, send-phase
/// timeouts) stay as <see cref="GotifyApiClient.PostResult"/> values; only
/// a dead transport or a stalled read throws, so the provider can log it as
/// an error while still swallowing it per the notification contract.
/// </summary>
internal sealed class GotifyApiException : Exception
{
    public GotifyApiException(string errorCode, string message, Exception? inner = null)
        : base($"[{errorCode}] {message}", inner)
    {
    }
}

/// <summary>
/// Thin transport over the Gotify REST API (<c>POST /message</c>). The
/// application token travels per call in the <c>X-Gotify-Key</c> header —
/// never in the URL, never stored, never logged. Routine failures surface
/// as <see cref="PostResult"/> values (the provider logs and swallows, per
/// the notification contract), a dead transport or stalled response read
/// throws <see cref="GotifyApiException"/>, and cancellation propagates.
/// </summary>
internal sealed class GotifyApiClient
{
    /// <summary>Hard cap on the buffered response body. Gotify envelopes are
    /// small JSON (<c>{id,…}</c> on success, <c>{errorCode,error,…}</c> on
    /// failure); the cap stops a hostile or malfunctioning peer — or a LAN
    /// MITM under <c>AllowPlainHttp</c> — from streaming an unbounded body
    /// into host memory. The per-call timeout bounds duration; this bounds
    /// size.</summary>
    internal const int MaxResponseBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public GotifyApiClient(HttpClient http)
    {
        _http = http;
    }

    public sealed record PostResult(bool Ok, long? MessageId, string? Error);

    public async Task<PostResult> PostMessageAsync(
        string appToken,
        Uri messageEndpoint,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appToken);
        ArgumentNullException.ThrowIfNull(messageEndpoint);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, messageEndpoint);
        request.Headers.Add("X-Gotify-Key", appToken);
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        HttpResponseMessage response;
        try
        {
            // Headers-only completion: the body is streamed under
            // MaxResponseBodyBytes below rather than buffered unbounded.
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Our own timeout is the only OCE this scope converts to a
            // result; a requested shutdown — or anyone else's cancellation
            // — propagates so work stops promptly.
            if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                throw;
            return new PostResult(false, null, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new GotifyApiException("transport", "Gotify request did not complete.", ex);
        }

        using (response)
        {
            // The plugin's handler never follows redirects, so a 3xx here is
            // the peer asking for the token to be re-sent to a Location it
            // chose — refused as a fixed-vocabulary failure, never retried.
            if (GotifyHttpClients.IsRedirect(response.StatusCode))
                return new PostResult(false, null, $"http-{(int)response.StatusCode}-redirect");

            string body;
            try
            {
                var (content, tooLarge) = await ReadBodyBoundedAsync(response, timeoutCts.Token);
                if (tooLarge)
                    return new PostResult(false, null, "response_too_large");
                body = content ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
                if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                    throw;
                throw new GotifyApiException("timeout", "Gotify response read timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new GotifyApiException("transport", "Gotify response could not be read.", ex);
            }

            if (!response.IsSuccessStatusCode)
                return new PostResult(false, null, DescribeError(response, body, appToken));

            try
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.Number
                    && idProp.TryGetInt64(out var parsed)
                    ? parsed
                    : (long?)null;
                return new PostResult(true, id, null);
            }
            catch (JsonException)
            {
                return new PostResult(false, null, "malformed_response");
            }
        }
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

    /// <summary>Extract Gotify's error envelope fields without letting a
    /// hostile or oversized body escape into logs — only the fixed field is
    /// taken, control and format characters that could forge log lines are
    /// stripped, the application token is redacted if the peer echoes it
    /// back, and the result is bounded.</summary>
    private static string DescribeError(HttpResponseMessage response, string body, string appToken)
    {
        const int maxFieldChars = 200;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                var text = SanitizeForLog(err.GetString() ?? string.Empty);
                if (!string.IsNullOrEmpty(appToken) && text.Contains(appToken, StringComparison.Ordinal))
                    text = text.Replace(appToken, "<redacted>", StringComparison.Ordinal);
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
