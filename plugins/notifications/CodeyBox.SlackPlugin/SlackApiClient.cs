using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.SlackPlugin;

/// <summary>
/// Typed transport failure from the Slack Web API: the request never
/// completed (DNS, connection, TLS). Routine API-level outcomes
/// (ok:false, HTTP status, timeouts) stay as <see cref="SlackApiClient.PostResult"/>
/// values; only a dead transport throws, so the provider can log it as an
/// error while still swallowing it per the notification contract.
/// </summary>
internal sealed class SlackApiException : Exception
{
    public string ErrorCode { get; }

    public SlackApiException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thin transport over the Slack Web API (<c>chat.postMessage</c> /
/// <c>chat.update</c>). The bot token travels per call and is never stored
/// or logged; routine failures surface as <see cref="SlackApiClient.PostResult"/>
/// values (the provider logs and swallows, per the notification contract),
/// a dead transport throws <see cref="SlackApiException"/>, and cancellation
/// propagates.
/// </summary>
internal sealed class SlackApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public SlackApiClient(HttpClient http)
    {
        _http = http;
    }

    public sealed record PostResult(bool Ok, string? Channel, string? Ts, string? Error);

    public async Task<PostResult> PostMessageAsync(
        string botToken,
        string channel,
        Dictionary<string, object?> payload,
        string? threadTs,
        TimeSpan timeout,
        CancellationToken ct)
    {
        payload["channel"] = channel;
        if (!string.IsNullOrWhiteSpace(threadTs))
            payload["thread_ts"] = threadTs;

        return await SendAsync(botToken, "https://slack.com/api/chat.postMessage", payload, timeout, ct);
    }

    public async Task<PostResult> UpdateMessageAsync(
        string botToken,
        string channel,
        string messageTs,
        string fallbackText,
        List<object?> blocks,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["channel"] = channel,
            ["ts"] = messageTs,
            ["text"] = fallbackText,
            ["blocks"] = blocks,
        };
        return await SendAsync(botToken, "https://slack.com/api/chat.update", payload, timeout, ct);
    }

    private async Task<PostResult> SendAsync(
        string botToken,
        string url,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
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
            return new PostResult(false, null, null, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new SlackApiException("transport", "Slack Web API request did not complete.", ex);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                    throw;
                throw new SlackApiException("timeout", "Slack Web API response read timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new SlackApiException("transport", "Slack Web API response could not be read.", ex);
            }

            if (!response.IsSuccessStatusCode)
                return new PostResult(false, null, null, $"http-{(int)response.StatusCode}");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                if (!ok)
                {
                    var error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                        ? err.GetString()
                        : "unknown_error";
                    return new PostResult(false, null, null, error);
                }
                var channel = root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
                    ? ch.GetString()
                    : null;
                var ts = root.TryGetProperty("ts", out var tsProp) && tsProp.ValueKind == JsonValueKind.String
                    ? tsProp.GetString()
                    : null;
                return new PostResult(true, channel, ts, null);
            }
            catch (JsonException)
            {
                return new PostResult(false, null, null, "malformed_response");
            }
        }
    }
}
