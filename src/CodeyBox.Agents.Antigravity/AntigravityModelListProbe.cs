using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Fetches the list of gateway model ids available to the Antigravity (<c>agy</c>)
/// CLI's configured credential.
///
/// <para>Endpoint preference, in order:</para>
/// <list type="number">
///   <item><description><c>POST :retrieveUserQuotaSummary</c> — the same family
///   <see cref="AntigravityQuotaProbe"/> uses; the <c>perModel</c> array names
///   every model the active subscription exposes.</description></item>
///   <item><description><c>POST :retrieveUserQuota</c> — fallback when summary
///   is absent; same shape as the Gemini probe.</description></item>
/// </list>
///
/// <para>The CLI's own <c>agy models</c> subcommand prints human display names
/// (Gemini 3.5 Flash (High), …) rather than canonical <c>--model</c> ids; the
/// gateway endpoints emit canonical ids, so we read those directly.</para>
///
/// <para>Never logs the Authorization header or response bodies (which may
/// carry account identifiers).</para>
/// </summary>
public sealed class AntigravityModelListProbe : IAgentModelListProbe
{
    internal const string SummaryEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    internal const string QuotaEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const int MaxResponseChars = 256 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<string?> _tokenProvider;
    private readonly ILogger<AntigravityModelListProbe> _log;

    public AgentKind Kind => AgentKind.Antigravity;

    public AntigravityModelListProbe(
        IHttpClientFactory httpClientFactory,
        Func<string?> tokenProvider,
        ILogger<AntigravityModelListProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _log = log;
    }

    public async Task<AgentModelListResult> GetModelListAsync(CancellationToken ct)
    {
        var token = _tokenProvider();
        if (string.IsNullOrEmpty(token))
            return AgentModelListResult.Failed("no credential configured");

        var summary = await FetchAsync(SummaryEndpoint, token, ct).ConfigureAwait(false);
        if (summary.FailureReason is null) return summary;
        var legacy = await FetchAsync(QuotaEndpoint, token, ct).ConfigureAwait(false);
        return legacy.FailureReason is null ? legacy : summary;
    }

    private async Task<AgentModelListResult> FetchAsync(string endpoint, string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-modellist");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return AgentModelListResult.Failed($"HTTP {(int)response.StatusCode}");

            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (body is null) return AgentModelListResult.Failed("response too large");
            var ids = ExtractModelIds(body);
            return ids.Count == 0
                ? AgentModelListResult.Failed("no models in response")
                : AgentModelListResult.Success(ids);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentModelListResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Antigravity model-list probe failed for {Endpoint}", endpoint);
            return AgentModelListResult.Failed("network error");
        }
    }

    internal static IReadOnlyList<string> ExtractModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ids;

            if (root.TryGetProperty("perModel", out var models) && models.ValueKind == JsonValueKind.Array)
                CollectIds(models, ids);
            if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
                CollectIds(buckets, ids);
        }
        catch (JsonException)
        {
        }
        return ids;
    }

    private static void CollectIds(JsonElement array, List<string> ids)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? id = null;
            if (item.TryGetProperty("modelId", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                id = modelEl.GetString();
            else if (item.TryGetProperty("model", out var alt) && alt.ValueKind == JsonValueKind.String)
                id = alt.GetString();
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id))
                ids.Add(id);
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxResponseChars + 1];
        int totalRead = 0, chunk;
        do
        {
            chunk = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
            totalRead += chunk;
        }
        while (chunk > 0 && totalRead < buffer.Length);
        if (totalRead > MaxResponseChars) return null;
        return new string(buffer, 0, totalRead);
    }
}
