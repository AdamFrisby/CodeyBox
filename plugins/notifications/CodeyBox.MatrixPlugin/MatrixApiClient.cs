using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.MatrixPlugin;

/// <summary>
/// Typed transport failure talking to the Matrix homeserver: the request never
/// completed (DNS, connection, TLS), or the response body stalled past the
/// call timeout. Routine API-level outcomes (HTTP status, Matrix error
/// bodies, send-phase timeouts) stay as result values; only a dead transport
/// or a stalled read throws, so the provider can log it as an error while
/// still swallowing it per the notification contract.
/// </summary>
internal sealed class MatrixApiException : Exception
{
    public MatrixApiException(string errorCode, string message, Exception? inner = null)
        : base($"[{errorCode}] {message}", inner)
    {
    }
}

/// <summary>Room security mode as observed from the homeserver.</summary>
internal enum RoomSecurityState
{
    /// <summary>No <c>m.room.encryption</c> state event: the room is sent in
    /// the clear and this plugin may post to it.</summary>
    Unencrypted,

    /// <summary>An <c>m.room.encryption</c> state event is present: the room
    /// is end-to-end encrypted and this plugin must never post to it.</summary>
    Encrypted,

    /// <summary>The mode could not be established (auth failure, rate limit,
    /// server error, malformed body, oversized body). Fails closed: treated
    /// exactly like <see cref="Encrypted"/> — never posted to.</summary>
    Unknown,
}

/// <summary>
/// Thin transport over the Matrix Client-Server API (pinned to the
/// <c>/_matrix/client/v3</c> paths): room encryption-state reads and
/// <c>m.room.message</c> event sends. The access token travels per call in
/// the <c>Authorization</c> header — never in the URL, never stored, never
/// logged. Routine failures surface as result values (the provider logs and
/// swallows, per the notification contract), a dead transport or stalled
/// response read throws <see cref="MatrixApiException"/>, and cancellation
/// propagates.
/// </summary>
internal sealed class MatrixApiClient
{
    /// <summary>Hard cap on the buffered response body. Matrix envelopes are
    /// small JSON (<c>{"event_id":…}</c> on success,
    /// <c>{"errcode","error","retry_after_ms"}</c> on failure); the cap stops
    /// a hostile or malfunctioning peer from streaming an unbounded body into
    /// host memory. The per-call timeout bounds duration; this bounds size.</summary>
    internal const int MaxResponseBodyBytes = 64 * 1024;

    /// <summary>Event type sent for every notification.</summary>
    public const string MessageEventType = "m.room.message";

    /// <summary>State event type whose presence marks a room encrypted.</summary>
    public const string EncryptionStateType = "m.room.encryption";

    /// <summary>Rate-limit errcode: the send may be retried with the same
    /// transaction ID after <c>retry_after_ms</c>.</summary>
    public const string LimitExceededErrcode = "M_LIMIT_EXCEEDED";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public MatrixApiClient(HttpClient http)
    {
        _http = http;
    }

    public sealed record SendResult(bool Ok, string? EventId, string? Error, bool RateLimited, int? RetryAfterMs);

    public sealed record SecurityResult(RoomSecurityState State, string? Error);

    public static Uri SendUri(Uri homeserverBase, string roomId, string txnId)
    {
        var baseUrl = homeserverBase.AbsoluteUri.TrimEnd('/');
        return new Uri(
            $"{baseUrl}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}" +
            $"/send/{Uri.EscapeDataString(MessageEventType)}/{Uri.EscapeDataString(txnId)}");
    }

    public static Uri EncryptionStateUri(Uri homeserverBase, string roomId)
    {
        var baseUrl = homeserverBase.AbsoluteUri.TrimEnd('/');
        return new Uri(
            $"{baseUrl}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}" +
            $"/state/{Uri.EscapeDataString(EncryptionStateType)}/");
    }

    /// <summary>Read the room's security mode. A present
    /// <c>m.room.encryption</c> state event means <see
    /// cref="RoomSecurityState.Encrypted"/>; <c>M_NOT_FOUND</c> means <see
    /// cref="RoomSecurityState.Unencrypted"/>; anything else — including a
    /// malformed or oversized success body — is <see
    /// cref="RoomSecurityState.Unknown"/> and fails closed downstream.</summary>
    public async Task<SecurityResult> GetRoomSecurityAsync(
        string accessToken,
        Uri homeserverBase,
        string roomId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(homeserverBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, EncryptionStateUri(homeserverBase, roomId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                throw;
            return new SecurityResult(RoomSecurityState.Unknown, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MatrixApiException("transport", "Matrix state request did not complete.", ex);
        }

        using (response)
        {
            // The plugin's handler never follows redirects, so a 3xx here is
            // the peer asking for the bearer token to be re-sent to a
            // Location it chose — refused as unknown (fail closed), never
            // followed.
            if (MatrixHttpClients.IsRedirect(response.StatusCode))
                return new SecurityResult(RoomSecurityState.Unknown, $"http-{(int)response.StatusCode}-redirect");

            var (body, tooLarge) = await ReadBodyBoundedAsync(response, timeoutCts, ct);
            if (tooLarge)
                return new SecurityResult(RoomSecurityState.Unknown, "response_too_large");

            if (response.IsSuccessStatusCode)
            {
                // Any well-formed state event here marks the room encrypted:
                // the event's presence is the signal, not any one field.
                // A malformed body fails closed rather than assuming clear.
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        return new SecurityResult(RoomSecurityState.Encrypted, null);
                }
                catch (JsonException)
                {
                }
                return new SecurityResult(RoomSecurityState.Unknown, "malformed_response");
            }

            if (IsNotFound(body))
                return new SecurityResult(RoomSecurityState.Unencrypted, null);

            return new SecurityResult(RoomSecurityState.Unknown, DescribeError(response, body, accessToken));
        }
    }

    /// <summary>Send one <c>m.room.message</c> event idempotently under
    /// <paramref name="txnId"/>. Re-PUTting the same transaction ID is the
    /// retry mechanism: the server returns the original <c>event_id</c>, so
    /// callers must reuse the ID across retries of one send.</summary>
    public async Task<SendResult> SendMessageAsync(
        string accessToken,
        Uri homeserverBase,
        string roomId,
        string txnId,
        Dictionary<string, object?> content,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(homeserverBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(txnId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Put, SendUri(homeserverBase, roomId, txnId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var json = JsonSerializer.Serialize(content, JsonOpts);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                throw;
            return new SendResult(false, null, "timeout", false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MatrixApiException("transport", "Matrix send request did not complete.", ex);
        }

        using (response)
        {
            if (MatrixHttpClients.IsRedirect(response.StatusCode))
                return new SendResult(false, null, $"http-{(int)response.StatusCode}-redirect", false, null);

            var (body, tooLarge) = await ReadBodyBoundedAsync(response, timeoutCts, ct);
            if (tooLarge)
                return new SendResult(false, null, "response_too_large", false, null);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("event_id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                        return new SendResult(true, id.GetString(), null, false, null);
                }
                catch (JsonException)
                {
                }
                return new SendResult(false, null, "malformed_response", false, null);
            }

            var (errcode, retryAfterMs) = ParseError(body);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || string.Equals(errcode, LimitExceededErrcode, StringComparison.Ordinal))
                return new SendResult(false, null, DescribeError(response, body, accessToken), true, retryAfterMs);

            return new SendResult(false, null, DescribeError(response, body, accessToken), false, null);
        }
    }

    /// <summary>Whether an error body is the state's "no such event" answer —
    /// the unencrypted signal. Matches the fixed <c>M_NOT_FOUND</c> errcode
    /// exactly, never by substring.</summary>
    private static bool IsNotFound(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("errcode", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "M_NOT_FOUND", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (string? Errcode, int? RetryAfterMs) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? errcode = null;
            int? retryAfter = null;
            if (root.TryGetProperty("errcode", out var code) && code.ValueKind == JsonValueKind.String)
                errcode = code.GetString();
            if (root.TryGetProperty("retry_after_ms", out var retry) && retry.ValueKind == JsonValueKind.Number
                && retry.TryGetInt32(out var ms) && ms >= 0)
                retryAfter = ms;
            return (errcode, retryAfter);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Read the response body under a hard byte cap — enforced
    /// before buffering, on the declared length and again while streaming —
    /// so an unbounded body is cut off rather than materialised. A stalled
    /// read past the call timeout surfaces as a transport exception; a
    /// caller-requested shutdown propagates.</summary>
    private static async Task<(string Body, bool TooLarge)> ReadBodyBoundedAsync(
        HttpResponseMessage response,
        CancellationTokenSource timeoutCts,
        CancellationToken callerCt)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBodyBytes)
            return (string.Empty, true);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var buffer = new byte[MaxResponseBodyBytes + 1];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), timeoutCts.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                totalRead += read;
            }
            return totalRead > MaxResponseBodyBytes
                ? (string.Empty, true)
                : (Encoding.UTF8.GetString(buffer, 0, totalRead), false);
        }
        catch (OperationCanceledException)
        {
            if (!timeoutCts.IsCancellationRequested || callerCt.IsCancellationRequested)
                throw;
            throw new MatrixApiException("timeout", "Matrix response read timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new MatrixApiException("transport", "Matrix response could not be read.", ex);
        }
    }

    /// <summary>Extract the fixed Matrix error fields without letting a
    /// hostile or oversized body escape into logs — only the fixed
    /// <c>error</c> field is taken, control and format characters that could
    /// forge log lines are stripped, the access token is redacted if the
    /// peer echoes it back, and the result is bounded.</summary>
    private static string DescribeError(HttpResponseMessage response, string body, string accessToken)
    {
        const int maxFieldChars = 200;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                var text = SanitizeForLog(err.GetString() ?? string.Empty);
                if (!string.IsNullOrEmpty(accessToken) && text.Contains(accessToken, StringComparison.Ordinal))
                    text = text.Replace(accessToken, "<redacted>", StringComparison.Ordinal);
                if (text.Length > maxFieldChars)
                    text = text[..maxFieldChars];
                var code = doc.RootElement.TryGetProperty("errcode", out var errcode)
                    && errcode.ValueKind == JsonValueKind.String
                    ? errcode.GetString()
                    : null;
                return string.IsNullOrWhiteSpace(code)
                    ? $"http-{(int)response.StatusCode}:{text}"
                    : $"http-{(int)response.StatusCode}:{code} {text}".TrimEnd();
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
