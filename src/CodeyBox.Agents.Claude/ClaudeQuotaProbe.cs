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
    private readonly Func<AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<ClaudeQuotaProbe> _log;

    // Single-entry cache: (token, snapshot, expiry). Protected by _lock.
    private (string AccessToken, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Claude;

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
        : this(httpClientFactory, () => new AgentQuotaCredentials(token), cacheTtl, log)
    {
    }

    public ClaudeQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<ClaudeQuotaProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _cacheTtl = cacheTtl;
        _log = log;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var credentials = _credentialsProvider();
        var token = credentials.AccessToken;
        if (string.IsNullOrEmpty(token))
            return Unknown("no token configured");

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry
                && string.Equals(entry.AccessToken, token, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < entry.ExpiresAt)
                return entry.Snapshot;

            var snapshot = await FetchAsync(token, ct);
            _cache = (token, snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Drops the in-process snapshot so the next
    /// <see cref="GetAvailabilityAsync"/> call refetches against the upstream
    /// usage endpoint. Wire to <see cref="CodeyBox.Orchestrator.CredentialFileSource.TokenUpdated"/>
    /// so an out-of-band host token rotation (operator running the CLI, child
    /// sandbox writeback, scripted refresh) doesn't leave a stale 401 pinned
    /// for the full cache TTL.
    /// </summary>
    public void InvalidateCache()
    {
        _lock.Wait();
        try { _cache = null; }
        finally { _lock.Release(); }
    }

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private async Task<AgentQuotaSnapshot> FetchAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            // Do NOT log the Authorization header — it contains the OAuth token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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

    // Claude's OAuth usage endpoint returns a flat object with named buckets
    // like `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet`.
    // Each bucket has `utilization` (0-100, where 100 means capped) and
    // `resets_at`. Global buckets (no `_<model>` suffix beyond the window
    // name) constrain ALL models; `_<model>` suffixes constrain only that
    // family. Effective availability is min(global) globally, and
    // min(global, model-specific) per model.
    private static readonly string[] GlobalBuckets = ["five_hour", "seven_day"];

    // Maps a model bucket suffix to model-id substrings it constrains.
    // E.g. `seven_day_opus` constrains any model id containing "opus".
    private static readonly (string Suffix, string ModelMatch)[] ModelSuffixes =
    [
        ("seven_day_opus", "opus"),
        ("seven_day_sonnet", "sonnet"),
        ("seven_day_haiku", "haiku"),
    ];

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try the new flat-bucket shape first.
            var flat = TryParseFlatShape(root);
            if (flat is not null) return flat;

            // Fallback: the older `rate_limit` + `additional_rate_limits` shape
            // (kept for backwards compatibility / future-proofing).
            var overall = TryParseRateLimit(root.TryGetProperty("rate_limit", out var rateLimit) ? rateLimit : root);
            var perModel = ParsePerModel(root);

            if (overall is not null && perModel.Count > 0)
            {
                var capPct = overall.AvailablePct;
                foreach (var key in perModel.Keys.ToList())
                {
                    var v = perModel[key];
                    if (v.AvailablePct > capPct)
                        perModel[key] = new ModelQuota { AvailablePct = capPct, ResetAt = overall.ResetAt ?? v.ResetAt, Window = $"{v.Window} (capped by overall)" };
                }
            }

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

    private static AgentQuotaSnapshot? TryParseFlatShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Detect flat shape: at least one global bucket present with `utilization`.
        ModelQuota? overall = null;
        var allBuckets = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in GlobalBuckets)
        {
            if (!root.TryGetProperty(bucket, out var el) || el.ValueKind != JsonValueKind.Object) continue;
            var quota = ParseFlatBucket(el, bucket);
            if (quota is null) continue;
            allBuckets[bucket] = quota;
            if (overall is null || quota.AvailablePct < overall.AvailablePct)
                overall = quota with { Window = bucket };
        }

        if (overall is null) return null; // not the flat shape

        // Collect model-specific buckets.
        var modelBuckets = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        foreach (var (suffix, _) in ModelSuffixes)
        {
            if (!root.TryGetProperty(suffix, out var el) || el.ValueKind != JsonValueKind.Object) continue;
            var quota = ParseFlatBucket(el, suffix);
            if (quota is not null)
                modelBuckets[suffix] = quota;
        }

        // Build per-model dict: every model_id matched by a suffix gets
        // min(overall, model-specific). Keep the suffix as a synthetic key
        // too so callers that route by suffix still work.
        var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        foreach (var (suffix, _) in ModelSuffixes)
        {
            if (!modelBuckets.TryGetValue(suffix, out var bucket)) continue;
            var capped = bucket.AvailablePct < overall.AvailablePct
                ? bucket with { Window = suffix }
                : new ModelQuota { AvailablePct = overall.AvailablePct, ResetAt = overall.ResetAt ?? bucket.ResetAt, Window = $"{suffix} (capped by overall)" };
            perModel[suffix] = capped;
        }

        // Map any client-known model ids by substring match.
        var configuredModels = new[] { "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5" };
        foreach (var modelId in configuredModels)
        {
            ModelQuota? best = overall; // default to global cap
            foreach (var (suffix, modelMatch) in ModelSuffixes)
            {
                if (!modelId.Contains(modelMatch, StringComparison.OrdinalIgnoreCase)) continue;
                if (!modelBuckets.TryGetValue(suffix, out var modelBucket)) continue;
                var effective = modelBucket.AvailablePct < overall.AvailablePct
                    ? modelBucket
                    : overall;
                if (best is null || effective.AvailablePct < best.AvailablePct)
                    best = effective with { Window = suffix };
            }
            if (best is not null)
                perModel[modelId] = best;
        }

        return new AgentQuotaSnapshot
        {
            AvailablePct = overall.AvailablePct,
            ResetAt = overall.ResetAt,
            Notes = null,
            PerModel = perModel,
        };
    }

    private static ModelQuota? ParseFlatBucket(JsonElement el, string window)
    {
        if (!TryGetDoubleProperty(el, "utilization", out var utilPct)) return null;
        return new ModelQuota
        {
            AvailablePct = ClampAvailable(100.0 - utilPct),
            ResetAt = TryGetResetAt(el),
            Window = window,
        };
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
        // Explicit deny flags trump usage percentages.
        var explicitDeny = (TryGetBoolProperty(el, "allowed", out var allowed) && !allowed)
                        || (TryGetBoolProperty(el, "limit_reached", out var lim) && lim);

        if (!TryGetDoubleProperty(el, "used_percent", out var usedPct) &&
            !TryGetDoubleProperty(el, "usedPercent", out usedPct))
        {
            if (TryGetDoubleProperty(el, "available_percent", out var availablePct) ||
                TryGetDoubleProperty(el, "availablePercent", out availablePct))
            {
                var pct = ClampAvailable(availablePct);
                if (explicitDeny) pct = 0;
                return new ModelQuota
                {
                    AvailablePct = pct,
                    ResetAt = TryGetResetAt(el),
                    Window = window,
                };
            }

            if (TryGetDoubleProperty(el, "used", out var used) &&
                TryGetDoubleProperty(el, "limit", out var limit) &&
                limit > 0)
            {
                var pct = ClampAvailable(100.0 * (1.0 - used / limit));
                if (explicitDeny) pct = 0;
                return new ModelQuota
                {
                    AvailablePct = pct,
                    ResetAt = TryGetResetAt(el),
                    Window = window,
                };
            }

            if (explicitDeny)
                return new ModelQuota { AvailablePct = 0, ResetAt = TryGetResetAt(el), Window = window };
            return null;
        }

        var availPct = ClampAvailable(100.0 - usedPct);
        if (explicitDeny) availPct = 0;
        return new ModelQuota
        {
            AvailablePct = availPct,
            ResetAt = TryGetResetAt(el),
            Window = window,
        };
    }

    private static bool TryGetBoolProperty(JsonElement el, string name, out bool value)
    {
        value = false;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
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
            if (el.TryGetProperty("resets_at", out var snakeS))
                return TryGetResetAt(snakeS);
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
