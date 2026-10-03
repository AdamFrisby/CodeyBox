using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeyBox.TeamsPlugin;

/// <summary>
/// Typed transport failure from the Bot Framework: the request never
/// completed (DNS, connection, TLS). Routine API-level outcomes (HTTP
/// status, timeouts) stay as result values; only a dead transport throws,
/// so the provider can log it as an error while still swallowing it per the
/// notification contract.
/// </summary>
internal sealed class TeamsApiException : Exception
{
    public TeamsApiException(string errorCode, string message, Exception? inner = null)
        : base($"[{errorCode}] {message}", inner)
    {
    }
}

/// <summary>
/// Acquires Bot Framework connector tokens (OAuth2 client-credentials
/// against the Microsoft login endpoint) and caches the current one until
/// shortly before expiry. Thread-safe: concurrent callers share one cached
/// token and one in-flight acquisition.
/// </summary>
internal sealed class TeamsBotTokenCache
{
    /// <summary>Microsoft login token endpoint for the Bot Framework.</summary>
    public const string TokenEndpoint = "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token";

    /// <summary>OAuth2 scope for Bot Framework connector tokens.</summary>
    public const string ConnectorScope = "https://api.botframework.com/.default";

    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public TeamsBotTokenCache(HttpClient http, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetTokenAsync(string appId, string appPassword, TimeSpan timeout, CancellationToken ct)
    {
        var cached = _token;
        if (!string.IsNullOrEmpty(cached) && _clock.GetUtcNow() < _expiresAt - ExpiryMargin)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _token;
            if (!string.IsNullOrEmpty(cached) && _clock.GetUtcNow() < _expiresAt - ExpiryMargin)
                return cached;

            var (token, expiresAt) = await AcquireAsync(appId, appPassword, timeout, ct).ConfigureAwait(false);
            _token = token;
            _expiresAt = expiresAt;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> AcquireAsync(string appId, string appPassword, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = appId,
            ["client_secret"] = appPassword,
            ["scope"] = ConnectorScope,
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                throw;
            throw new TeamsApiException("timeout", "Bot Framework token acquisition timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TeamsApiException("transport", "Bot Framework token acquisition did not complete.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new TeamsApiException($"http-{(int)response.StatusCode}", "Bot Framework token acquisition was refused.");
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                    throw;
                throw new TeamsApiException("timeout", "Bot Framework token response read timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new TeamsApiException("transport", "Bot Framework token response could not be read.", ex);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("access_token", out var tokenProp)
                    || tokenProp.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(tokenProp.GetString()))
                    throw new TeamsApiException("malformed_response", "Bot Framework token response carries no access token.");
                var lifetime = TimeSpan.FromHours(1);
                if (root.TryGetProperty("expires_in", out var expProp))
                {
                    var seconds = expProp.ValueKind == JsonValueKind.Number && expProp.TryGetInt32(out var s)
                        ? s
                        : expProp.ValueKind == JsonValueKind.String && int.TryParse(expProp.GetString(), out var ps) ? ps : 3600;
                    lifetime = TimeSpan.FromSeconds(Math.Max(60, seconds));
                }
                return (tokenProp.GetString()!, _clock.GetUtcNow().Add(lifetime));
            }
            catch (JsonException ex)
            {
                throw new TeamsApiException("malformed_response", "Bot Framework token response is not valid JSON.", ex);
            }
        }
    }
}

/// <summary>
/// Thin transport over the Bot Framework Connector
/// (<c>/v3/conversations/{id}/activities</c>). The bot token travels per
/// call and is never stored or logged; routine failures surface as result
/// values (the provider logs and swallows, per the notification contract),
/// a dead transport throws <see cref="TeamsApiException"/>, and
/// cancellation propagates. The sink carries its own guard: the service URL
/// must be an absolute https URI on every call, whatever the caller passed.
/// </summary>
internal sealed class TeamsApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public TeamsApiClient(HttpClient http)
    {
        _http = http;
    }

    public sealed record PostResult(bool Ok, string? ActivityId, string? Error);

    public sealed record UpdateResult(bool Ok, string? Error);

    /// <summary>The service URL is usable when it is an absolute https URI.</summary>
    public static bool IsUsableServiceUrl(string? serviceUrl)
    {
        return Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<PostResult> PostActivityAsync(
        string serviceUrl,
        string conversationId,
        string botToken,
        Dictionary<string, object?> activity,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!IsUsableServiceUrl(serviceUrl))
            return new PostResult(false, null, "invalid_service_url");
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 256)
            return new PostResult(false, null, "invalid_conversation");

        var url = $"{serviceUrl.TrimEnd('/')}/v3/conversations/{Uri.EscapeDataString(conversationId.Trim())}/activities";
        var outcome = await SendAsync(botToken, HttpMethod.Post, url, activity, timeout, ct).ConfigureAwait(false);
        if (!outcome.Ok)
            return new PostResult(false, null, outcome.Error);
        try
        {
            using var doc = JsonDocument.Parse(outcome.Body);
            var id = doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            return new PostResult(true, id, null);
        }
        catch (JsonException)
        {
            return new PostResult(false, null, "malformed_response");
        }
    }

    public async Task<UpdateResult> UpdateActivityAsync(
        string serviceUrl,
        string conversationId,
        string activityId,
        string botToken,
        Dictionary<string, object?> activity,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!IsUsableServiceUrl(serviceUrl))
            return new UpdateResult(false, "invalid_service_url");
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 256)
            return new UpdateResult(false, "invalid_conversation");
        if (string.IsNullOrWhiteSpace(activityId) || activityId.Length > 256)
            return new UpdateResult(false, "invalid_activity");

        var url = $"{serviceUrl.TrimEnd('/')}/v3/conversations/{Uri.EscapeDataString(conversationId.Trim())}/activities/{Uri.EscapeDataString(activityId.Trim())}";
        var outcome = await SendAsync(botToken, HttpMethod.Put, url, activity, timeout, ct).ConfigureAwait(false);
        return new UpdateResult(outcome.Ok, outcome.Error);
    }

    private async Task<(bool Ok, string Body, string? Error)> SendAsync(
        string botToken,
        HttpMethod method,
        string url,
        Dictionary<string, object?> activity,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        var json = JsonSerializer.Serialize(activity, JsonOpts);
        request.Content = new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Our own timeout is the only OCE this scope converts to a
            // result; a requested shutdown — or anyone else's cancellation
            // — propagates so work stops promptly.
            if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                throw;
            return (false, string.Empty, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TeamsApiException("transport", "Bot Framework Connector request did not complete.", ex);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!timeoutCts.IsCancellationRequested || ct.IsCancellationRequested)
                    throw;
                throw new TeamsApiException("timeout", "Bot Framework Connector response read timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new TeamsApiException("transport", "Bot Framework Connector response could not be read.", ex);
            }

            if (!response.IsSuccessStatusCode)
                return (false, string.Empty, $"http-{(int)response.StatusCode}");
            return (true, body, null);
        }
    }
}
