using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Fetches the list of Gemini model identifiers available to the configured
/// credential.
///
/// <para>Two endpoints are supported:</para>
/// <list type="bullet">
///   <item><description>OAuth subscription path (Code Assist Individual / AI Pro / AI Ultra):
///   <c>POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota</c> —
///   the same endpoint <see cref="GeminiQuotaProbe"/> uses. The bucket list
///   names every model the OAuth tier exposes.</description></item>
///   <item><description>Pay-per-API path: <c>GET https://generativelanguage.googleapis.com/v1beta/models?key=&lt;key&gt;</c>.
///   Used when an API key is configured but no OAuth token.</description></item>
/// </list>
///
/// <para>Never logs the Authorization header, API key, or response bodies that
/// may carry account identifiers.</para>
/// </summary>
public sealed class GeminiModelListProbe : IAgentModelListProbe
{
    internal const string OAuthQuotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    internal const string ApiKeyModelsEndpoint = "https://generativelanguage.googleapis.com/v1beta/models";
    private const int MaxResponseChars = 256 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<(string? OAuthToken, string? ApiKey)> _credentialsProvider;
    private readonly ILogger<GeminiModelListProbe> _log;

    public AgentKind Kind => AgentKind.Gemini;

    public GeminiModelListProbe(
        IHttpClientFactory httpClientFactory,
        Func<(string? OAuthToken, string? ApiKey)> credentialsProvider,
        ILogger<GeminiModelListProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _log = log;
    }

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        var (oauth, apiKey) = _credentialsProvider();
        if (!string.IsNullOrEmpty(oauth))
            return await FetchOAuthAsync(oauth!, ct);
        if (!string.IsNullOrEmpty(apiKey))
            return await FetchApiKeyAsync(apiKey!, ct);
        return AgentModelListResult.Failed("no credential configured");
    }

    private async Task<AgentModelListResult> FetchOAuthAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-modellist");
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthQuotaEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return AgentModelListResult.Failed($"HTTP {(int)response.StatusCode}");

            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null)
                return AgentModelListResult.Failed("response too large");
            return ParseQuotaBuckets(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Gemini OAuth model-list probe failed; skipping model-id validation");
            return AgentModelListResult.Failed("network error");
        }
    }

    private async Task<AgentModelListResult> FetchApiKeyAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-modellist");
            // The API key is in the URL query string per Google's documented
            // contract; not exposed via headers we explicitly log.
            var uri = $"{ApiKeyModelsEndpoint}?key={Uri.EscapeDataString(apiKey)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return AgentModelListResult.Failed($"HTTP {(int)response.StatusCode}");

            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null)
                return AgentModelListResult.Failed("response too large");
            return ParseModelsListing(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Gemini API-key model-list probe failed; skipping model-id validation");
            return AgentModelListResult.Failed("network error");
        }
    }

    internal static AgentModelListResult ParseQuotaBuckets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
                return AgentModelListResult.Failed("unexpected response shape");

            var ids = new List<string>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                if (!bucket.TryGetProperty("modelId", out var modelEl) || modelEl.ValueKind != JsonValueKind.String) continue;
                var id = modelEl.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
            return AgentModelListResult.Success(ids);
        }
        catch (JsonException)
        {
            return AgentModelListResult.Failed("invalid JSON");
        }
    }

    internal static AgentModelListResult ParseModelsListing(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
                return AgentModelListResult.Failed("unexpected response shape");

            var ids = new List<string>();
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Response uses fully-qualified resource names like "models/gemini-2.0-flash";
                // strip the leading "models/" prefix to match how operators write ModelId.
                var slash = name.LastIndexOf('/');
                var id = slash >= 0 ? name[(slash + 1)..] : name;
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
            return AgentModelListResult.Success(ids);
        }
        catch (JsonException)
        {
            return AgentModelListResult.Failed("invalid JSON");
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseChars + 1];
        int totalRead = 0, chunk;
        do
        {
            chunk = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            totalRead += chunk;
        }
        while (chunk > 0 && totalRead < buffer.Length);
        if (totalRead > MaxResponseChars) return null;
        return new string(buffer, 0, totalRead);
    }
}
