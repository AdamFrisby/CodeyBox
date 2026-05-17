using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Fetches the list of model identifiers available to a Codex credential.
///
/// <para>Two endpoints are supported:</para>
/// <list type="bullet">
///   <item><description>ChatGPT-OAuth subscription path: <c>GET https://chatgpt.com/backend-api/wham/models</c>
///   with <c>Authorization: Bearer &lt;access_token&gt;</c>. Used when an OAuth
///   token (from <c>~/.codex/auth.json</c>) is present.</description></item>
///   <item><description>Pay-per-API path: <c>GET https://api.openai.com/v1/models</c> with
///   <c>Authorization: Bearer &lt;api_key&gt;</c>. Used when only an API key is configured.</description></item>
/// </list>
///
/// <para>Both endpoints return the OpenAI standard <c>{ "data": [ { "id": ... } ] }</c>
/// shape. Never logs the Authorization header or credential values.</para>
/// </summary>
public sealed class CodexModelListProbe : IAgentModelListProbe
{
    internal const string OAuthModelsEndpoint = "https://chatgpt.com/backend-api/wham/models";
    internal const string ApiModelsEndpoint = "https://api.openai.com/v1/models";
    private const int MaxResponseChars = 256 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<(string? OAuthToken, string? AccountId, string? ApiKey)> _credentialsProvider;
    private readonly ILogger<CodexModelListProbe> _log;

    public AgentKind Kind => AgentKind.Codex;

    public CodexModelListProbe(
        IHttpClientFactory httpClientFactory,
        Func<(string? OAuthToken, string? AccountId, string? ApiKey)> credentialsProvider,
        ILogger<CodexModelListProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _log = log;
    }

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        var (oauth, accountId, apiKey) = _credentialsProvider();
        var (endpoint, token, account) = !string.IsNullOrEmpty(oauth)
            ? (OAuthModelsEndpoint, oauth!, accountId)
            : !string.IsNullOrEmpty(apiKey)
                ? (ApiModelsEndpoint, apiKey!, (string?)null)
                : (null, null, null);

        if (endpoint is null || token is null)
            return AgentModelListResult.Failed("no credential configured");

        try
        {
            var client = _httpClientFactory.CreateClient("agent-modellist");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrWhiteSpace(account))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return AgentModelListResult.Failed($"HTTP {(int)response.StatusCode}");

            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null)
                return AgentModelListResult.Failed("response too large");
            return ParseResponse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Codex model-list probe failed; skipping model-id validation");
            return AgentModelListResult.Failed("network error");
        }
    }

    internal static AgentModelListResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // OpenAI shape: { data: [ { id: "..." } ] }
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return CollectIds(data, "id");
            // ChatGPT WHAM shape may use `models` with `slug` or `id`.
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<string>();
                foreach (var item in models.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = TryGetString(item, "slug")
                        ?? TryGetString(item, "id")
                        ?? TryGetString(item, "model");
                    if (!string.IsNullOrWhiteSpace(id))
                        ids.Add(id);
                }
                return AgentModelListResult.Success(ids);
            }
            return AgentModelListResult.Failed("unexpected response shape");
        }
        catch (JsonException)
        {
            return AgentModelListResult.Failed("invalid JSON");
        }
    }

    private static AgentModelListResult CollectIds(JsonElement array, string idField)
    {
        var ids = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetString(item, idField);
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        return AgentModelListResult.Success(ids);
    }

    private static string? TryGetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
