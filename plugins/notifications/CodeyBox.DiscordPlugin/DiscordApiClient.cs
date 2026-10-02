using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.DiscordPlugin;

/// <summary>
/// Typed transport failure from the Discord REST API: the request never
/// completed (DNS, connection, TLS). Routine API-level outcomes
/// (error status, malformed bodies, timeouts) stay as result values; only
/// a dead transport throws, so the provider can log it as an error while
/// still swallowing it per the notification contract.
/// </summary>
internal sealed class DiscordApiException : Exception
{
    public DiscordApiException(string errorCode, string message, Exception? inner = null)
        : base($"[{errorCode}] {message}", inner)
    {
    }
}

/// <summary>
/// Thin transport over the Discord REST API (v10): create a message in a
/// channel or thread, start a thread from a message, and edit a message.
/// The bot token travels per call and is never stored or logged; routine
/// failures surface as result values (the provider logs and swallows, per
/// the notification contract), a dead transport throws
/// <see cref="DiscordApiException"/>, and cancellation propagates.
/// </summary>
internal sealed class DiscordApiClient
{
    public const string BaseUrl = "https://discord.com/api/v10";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public DiscordApiClient(HttpClient http)
    {
        _http = http;
    }

    public sealed record MessageResult(bool Ok, string? ChannelId, string? MessageId, string? Error);

    public sealed record ThreadResult(bool Ok, string? ThreadId, string? Error);

    public sealed record EditResult(bool Ok, string? Error);

    public async Task<MessageResult> CreateMessageAsync(
        string botToken,
        string channelId,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/channels/{Uri.EscapeDataString(channelId)}/messages";
        var outcome = await SendAsync(botToken, HttpMethod.Post, url, payload, timeout, ct);
        if (!outcome.Ok)
            return new MessageResult(false, null, null, outcome.Error);
        try
        {
            using var doc = JsonDocument.Parse(outcome.Body);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            var channel = root.TryGetProperty("channel_id", out var chProp) && chProp.ValueKind == JsonValueKind.String
                ? chProp.GetString()
                : channelId;
            return string.IsNullOrEmpty(id)
                ? new MessageResult(false, null, null, "malformed_response")
                : new MessageResult(true, channel, id, null);
        }
        catch (JsonException)
        {
            return new MessageResult(false, null, null, "malformed_response");
        }
    }

    public async Task<ThreadResult> CreateThreadFromMessageAsync(
        string botToken,
        string channelId,
        string messageId,
        string name,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}/threads";
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["auto_archive_duration"] = 1440,
        };
        var outcome = await SendAsync(botToken, HttpMethod.Post, url, payload, timeout, ct);
        if (!outcome.Ok)
            return new ThreadResult(false, null, outcome.Error);
        try
        {
            using var doc = JsonDocument.Parse(outcome.Body);
            var id = doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            return string.IsNullOrEmpty(id)
                ? new ThreadResult(false, null, "malformed_response")
                : new ThreadResult(true, id, null);
        }
        catch (JsonException)
        {
            return new ThreadResult(false, null, "malformed_response");
        }
    }

    public async Task<EditResult> EditMessageAsync(
        string botToken,
        string channelId,
        string messageId,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}";
        var outcome = await SendAsync(botToken, HttpMethod.Patch, url, payload, timeout, ct);
        return new EditResult(outcome.Ok, outcome.Error);
    }

    private sealed record SendOutcome(bool Ok, string Body, string? Error);

    private async Task<SendOutcome> SendAsync(
        string botToken,
        HttpMethod method,
        string url,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
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
            return new SendOutcome(false, string.Empty, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new DiscordApiException("transport", "Discord REST request did not complete.", ex);
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
                throw new DiscordApiException("timeout", "Discord REST response read timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new DiscordApiException("transport", "Discord REST response could not be read.", ex);
            }

            if (!response.IsSuccessStatusCode)
                return new SendOutcome(false, string.Empty, $"http-{(int)response.StatusCode}");
            return new SendOutcome(true, body, null);
        }
    }
}
