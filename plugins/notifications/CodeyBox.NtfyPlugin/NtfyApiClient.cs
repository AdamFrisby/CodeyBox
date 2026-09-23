using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.NtfyPlugin;

/// <summary>
/// Typed transport failure talking to the ntfy server: the request never
/// completed (DNS, connection, TLS). Routine API-level outcomes (HTTP status,
/// ntfy error bodies, timeouts) stay as <see cref="NtfyApiClient.PostResult"/>
/// values; only a dead transport throws, so the provider can log it as an
/// error while still swallowing it per the notification contract.
/// </summary>
internal sealed class NtfyApiException : Exception
{
    public string ErrorCode { get; }

    public NtfyApiException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thin transport over ntfy's publish-as-JSON endpoint (<c>POST {baseUrl}/</c>
/// with the topic carried in the body). The access token travels per call and
/// is never stored or logged; routine failures surface as
/// <see cref="NtfyApiClient.PostResult"/> values (the provider logs and
/// swallows, per the notification contract), a dead transport throws
/// <see cref="NtfyApiException"/>, and cancellation propagates.
/// </summary>
internal sealed class NtfyApiClient
{
    /// <summary>Response body bytes read when extracting an error reason.</summary>
    private const int MaxErrorBodyBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _publishUrl;

    public NtfyApiClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _publishUrl = $"{baseUrl.TrimEnd('/')}/";
    }

    public sealed record PostResult(bool Ok, string? Error);

    /// <summary>Publish a message. <paramref name="accessToken"/> may be null
    /// for open topics; when present it is sent as
    /// <c>Authorization: Bearer …</c>.</summary>
    public async Task<PostResult> PublishAsync(
        string? accessToken,
        Dictionary<string, object?> payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, _publishUrl);
        if (!string.IsNullOrEmpty(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
            return new PostResult(false, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new NtfyApiException("transport", "ntfy publish request did not complete.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return new PostResult(true, null);

            // ntfy answers errors as {"code":…,"http":…,"error":"…"} — surface
            // the fixed reason field, bounded, never the raw body.
            try
            {
                var body = await ReadBoundedAsync(response, timeoutCts.Token);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(err.GetString()))
                {
                    var reason = err.GetString()!;
                    return new PostResult(false,
                        $"http-{(int)response.StatusCode}: {reason[..Math.Min(reason.Length, 200)]}");
                }
            }
            catch (OperationCanceledException)
            {
                if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                    throw;
                return new PostResult(false, "timeout");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                // Fall through to the bare status below.
            }
            return new PostResult(false, $"http-{(int)response.StatusCode}");
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxErrorBodyBytes + 1];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
            if (total > MaxErrorBodyBytes)
                break;
        }
        return Encoding.UTF8.GetString(buffer, 0, Math.Min(total, MaxErrorBodyBytes));
    }
}
