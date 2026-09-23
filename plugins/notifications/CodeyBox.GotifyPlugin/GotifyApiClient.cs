using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.GotifyPlugin;

/// <summary>
/// Typed transport failure from the Gotify REST API: the request never
/// completed (DNS, connection, TLS). Routine API-level outcomes (HTTP
/// status, timeouts) stay as <see cref="GotifyApiClient.PostResult"/>
/// values; only a dead transport throws, so the provider can log it as an
/// error while still swallowing it per the notification contract.
/// </summary>
internal sealed class GotifyApiException : Exception
{
    public string ErrorCode { get; }

    public GotifyApiException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thin transport over the Gotify REST API (<c>POST /message</c>). The
/// application token travels per call in the <c>X-Gotify-Key</c> header —
/// never in the URL, never stored, never logged. Routine failures surface
/// as <see cref="PostResult"/> values (the provider logs and swallows, per
/// the notification contract), a dead transport throws
/// <see cref="GotifyApiException"/>, and cancellation propagates.
/// </summary>
internal sealed class GotifyApiClient
{
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
            response = await _http.SendAsync(request, timeoutCts.Token);
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
            // Gotify error bodies are small JSON envelopes
            // ({"errorCode":…,"error":"…","errorDescription":"…"}); the read
            // stays under the same timeout so a stalled peer cannot hang
            // the notification path.
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
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
                return new PostResult(false, null, DescribeError(response, body));

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

    /// <summary>Extract Gotify's error envelope fields without letting a
    /// hostile or oversized body escape into logs — only the fixed fields
    /// are taken, each bounded.</summary>
    private static string DescribeError(HttpResponseMessage response, string body)
    {
        const int maxFieldChars = 200;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                var text = err.GetString() ?? string.Empty;
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
}
