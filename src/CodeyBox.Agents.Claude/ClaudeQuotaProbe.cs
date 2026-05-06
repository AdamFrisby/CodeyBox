using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Probes the Anthropic OAuth usage endpoint to estimate available Claude
/// subscription quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// Any network error, unexpected status code, or unrecognised response shape
/// returns <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1; the router's
/// unknown policy decides whether that blocks pickup.
///
/// Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.
/// </summary>
public sealed class ClaudeQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _token;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<ClaudeQuotaProbe> _log;

    // Single-entry cache: (snapshot, expiry). Protected by _lock.
    private (AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _token = token;
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

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private async Task<AgentQuotaSnapshot> FetchAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            // Do NOT log the Authorization header — it contains the OAuth token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Claude quota endpoint returned {StatusCode}; treating quota as unknown",
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
            _log.LogDebug(ex, "Claude quota probe failed; treating quota as unknown");
            return Unknown("network error");
        }
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

        if (root.TryGetProperty("additional_rate_limits", out var additional) &&
            additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in additional.EnumerateArray())
                AddModelQuota(perModel, item);
        }

        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in models.EnumerateObject())
            {
                var quota = TryParseRateLimit(prop.Value.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : prop.Value);
                if (quota is not null)
                    perModel[prop.Name] = quota;
            }
        }

        if (root.TryGetProperty("model_rate_limits", out var modelLimits) &&
            modelLimits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelLimits.EnumerateArray())
                AddModelQuota(perModel, item);
        }

        return perModel;
    }

    private static void AddModelQuota(Dictionary<string, ModelQuota> perModel, JsonElement item)
    {
        var modelId = TryGetString(item, "model_id")
            ?? TryGetString(item, "model")
            ?? TryGetString(item, "limit_name")
            ?? TryGetString(item, "limitName");
        if (string.IsNullOrWhiteSpace(modelId))
            return;

        var quotaSource = item.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : item;
        var quota = TryParseRateLimit(quotaSource);
        if (quota is not null)
            perModel[modelId] = quota;
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
        {
            if (TryGetDoubleProperty(el, "available_percent", out var availablePct) ||
                TryGetDoubleProperty(el, "availablePercent", out availablePct))
            {
                return new ModelQuota
                {
                    AvailablePct = ClampAvailable(availablePct),
                    ResetAt = TryGetResetAt(el),
                    Window = window,
                };
            }

            if (TryGetDoubleProperty(el, "used", out var used) &&
                TryGetDoubleProperty(el, "limit", out var limit) &&
                limit > 0)
            {
                return new ModelQuota
                {
                    AvailablePct = ClampAvailable(100.0 * (1.0 - used / limit)),
                    ResetAt = TryGetResetAt(el),
                    Window = window,
                };
            }

            return null;
        }

        return new ModelQuota
        {
            AvailablePct = ClampAvailable(100.0 - usedPct),
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

    private static double ClampAvailable(double pct) => Math.Clamp(pct, 0.0, 100.0);

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };
}
