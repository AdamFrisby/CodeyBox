using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Fetches the list of model identifiers available to a Claude credential by
/// calling <c>GET https://api.anthropic.com/v1/models</c>.
///
/// <para>Supports both OAuth tokens (sent as <c>Authorization: Bearer</c>)
/// and raw API keys (sent as <c>x-api-key</c>), matching <see cref="ClaudeSmokeProbe"/>.</para>
///
/// <para>Never logs the Authorization header or credential values.</para>
/// </summary>
public sealed class ClaudeModelListProbe : IAgentModelListProbe
{
    internal const string ModelsEndpoint = "https://api.anthropic.com/v1/models";
    internal const string AnthropicVersion = "2023-06-01";
    private const int MaxResponseChars = 256 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<(string? OAuthToken, string? ApiKey)> _credentialsProvider;
    private readonly ILogger<ClaudeModelListProbe> _log;

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeModelListProbe(
        IHttpClientFactory httpClientFactory,
        Func<(string? OAuthToken, string? ApiKey)> credentialsProvider,
        ILogger<ClaudeModelListProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _log = log;
    }

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        var (oauth, apiKey) = _credentialsProvider();
        if (string.IsNullOrEmpty(oauth) && string.IsNullOrEmpty(apiKey))
            return AgentModelListResult.Failed("no credential configured");

        try
        {
            var client = _httpClientFactory.CreateClient("agent-modellist");
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            if (!string.IsNullOrEmpty(oauth))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth);
            else
                request.Headers.Add("x-api-key", apiKey!);
            request.Headers.Add("anthropic-version", AnthropicVersion);

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
            _log.LogDebug(ex, "Claude model-list probe failed; skipping model-id validation");
            return AgentModelListResult.Failed("network error");
        }
    }

    internal static AgentModelListResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return AgentModelListResult.Failed("unexpected response shape");

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString();
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
