using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Probes the ChatGPT backend usage endpoint used by Codex CLI to estimate
/// subscription quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// Fail-open: any network error, unexpected status code, or unrecognised
/// response shape returns <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1
/// so a broken endpoint never blocks work items.
///
/// Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.
/// </summary>
public sealed class CodexQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _token;
    private readonly string? _accountId;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CodexQuotaProbe> _log;

    // Single-entry cache: (snapshot, expiry). Protected by _lock.
    private (AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Codex;

    public CodexQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<CodexQuotaProbe> log,
        string? accountId = null)
    {
        _httpClientFactory = httpClientFactory;
        _token = token;
        _accountId = accountId;
        _cacheTtl = cacheTtl;
        _log = log;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_token))
            return Unknown("no token configured");

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry && DateTimeOffset.UtcNow < entry.ExpiresAt)
                return entry.Snapshot;

            var snapshot = await FetchAsync(ct);
            _cache = (snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AgentQuotaSnapshot> FetchAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");

            // Do NOT log the Authorization header — it contains the ChatGPT token.
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (!string.IsNullOrWhiteSpace(_accountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", _accountId);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Codex usage endpoint returned {StatusCode}; treating quota as unknown",
                    (int)response.StatusCode);
                return Unknown($"HTTP {(int)response.StatusCode}");
            }

            // Do NOT log the response body — it may contain account identifiers.
            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null) return Unknown("response too large");
            return ParseResponse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Codex quota probe failed; treating quota as unknown");
            return Unknown("network error");
        }
    }

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var overall = TryParseRateLimit(root.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : root);
            var perModel = ParsePerModel(root);

            if (overall is not null || perModel.Count > 0)
                return new AgentQuotaSnapshot
                {
                    AvailablePct = overall?.AvailablePct ?? -1,
                    ResetAt = overall?.ResetAt,
                    Notes = overall is null ? "overall quota unknown; parsed per-model rollups" : null,
                    PerModel = perModel,
                };

            return Unknown("unexpected response shape");
        }
        catch (JsonException)
        {
            return Unknown("invalid JSON");
        }
    }

    private static Dictionary<string, ModelQuota> ParsePerModel(JsonElement root)
    {
        var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("additional_rate_limits", out var additional) ||
            additional.ValueKind != JsonValueKind.Array)
            return perModel;

        foreach (var item in additional.EnumerateArray())
        {
            var modelId = TryGetString(item, "limit_name")
                ?? TryGetString(item, "model_id")
                ?? TryGetString(item, "model")
                ?? TryGetString(item, "limitName");
            if (string.IsNullOrWhiteSpace(modelId))
                continue;

            var quotaSource = item.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : item;
            var quota = TryParseRateLimit(quotaSource);
            if (quota is not null)
                perModel[modelId] = quota;
        }

        return perModel;
    }

    private static ModelQuota? TryParseRateLimit(JsonElement el)
    {
        var windows = new[]
        {
            ("5h-rolling", TryGetProperty(el, "primary_window")),
            ("weekly", TryGetProperty(el, "secondary_window")),
            ("overall", (JsonElement?)el),
        };

        ModelQuota? mostConstrained = null;
        foreach (var (windowName, window) in windows)
        {
            if (window is null)
                continue;

            var quota = TryParseWindow(window.Value, windowName);
            if (quota is null)
                continue;

            if (mostConstrained is null || quota.AvailablePct < mostConstrained.AvailablePct)
                mostConstrained = quota;
        }

        return mostConstrained;
    }

    private static ModelQuota? TryParseWindow(JsonElement el, string window)
    {
        if (!TryGetDoubleProperty(el, "used_percent", out var usedPct) &&
            !TryGetDoubleProperty(el, "usedPercent", out usedPct))
            return null;

        return new ModelQuota
        {
            AvailablePct = Math.Clamp(100.0 - usedPct, 0.0, 100.0),
            ResetAt = TryGetResetAt(el),
            Window = window,
        };
    }

    private static JsonElement? TryGetProperty(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var value) ? value : null;

    private static string? TryGetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetDoubleProperty(JsonElement el, string name, out double value)
    {
        value = 0;
        return el.ValueKind == JsonValueKind.Object &&
               el.TryGetProperty(name, out var prop) &&
               TryGetDouble(prop, out value);
    }

    private static bool TryGetDouble(JsonElement el, out double value)
    {
        value = 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), out value),
            _ => false,
        };
    }

    private static DateTimeOffset? TryGetResetAt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("reset_at", out var snake))
                return TryGetResetAt(snake);
            if (el.TryGetProperty("resetAt", out var camel))
                return TryGetResetAt(camel);
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var seconds) =>
                DateTimeOffset.FromUnixTimeSeconds(seconds),
            JsonValueKind.String when DateTimeOffset.TryParse(el.GetString(), out var parsed) =>
                parsed,
            _ => null,
        };
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        // Allocate one extra char so we can detect bodies that exceed the cap.
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

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };
}
