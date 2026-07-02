using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

/// <summary>
/// Probes the ChatGPT backend usage endpoint used by Codex CLI to estimate
/// subscription quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// Any network error, unexpected status code, or unrecognised response shape
/// returns <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1; the router's
/// unknown policy decides whether that blocks pickup.
///
/// Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.
/// </summary>
public sealed class CodexQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    internal const string DefaultRoutedModelId = "gpt-5.5";

    private const int MaxResponseChars = 64 * 1024; // 64 KiB
    private static readonly IReadOnlyDictionary<string, string[]> RoutedModelAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Captured WHAM usage names the Codex subscription bucket by its
            // product/display limit, while the CLI route configured in the
            // default frontier class is gpt-5.5.
            ["GPT-5.3-Codex-Spark"] = [DefaultRoutedModelId],
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CodexQuotaProbe> _log;

    // Single-entry cache: (route key, token, account, snapshot, expiry). Protected by _lock.
    private (string RouteKey, string AccessToken, string? AccountId, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Dedupes the Information log line that fires when a configured model is
    // absent from the probe response. Keyed by (token, modelId).
    private readonly HashSet<(string Token, string ModelId)> _loggedMissingModels = new();
    private readonly object _loggedMissingModelsLock = new();

    public AgentKind Kind => AgentKind.Codex;

    public CodexQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<CodexQuotaProbe> log,
        string? accountId = null)
        : this(httpClientFactory, () => new AgentQuotaCredentials(token, accountId), cacheTtl, log)
    {
    }

    public CodexQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<CodexQuotaProbe> log)
        : this(httpClientFactory, _ => credentialsProvider(), cacheTtl, log)
    {
    }

    public CodexQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<CodexQuotaProbe> log)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _cacheTtl = cacheTtl;
        _log = log;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var credentials = _credentialsProvider(member);
        var token = credentials.AccessToken;
        if (string.IsNullOrEmpty(token))
            return Unknown(QuotaUnknownReason.NoCredential, "no token configured");
        var routeKey = member.RouteKey;

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry
                && string.Equals(entry.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.AccessToken, token, StringComparison.Ordinal)
                && string.Equals(entry.AccountId, credentials.AccountId, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                snapshot = entry.Snapshot;
            }
            else
            {
                snapshot = await FetchAsync(token, credentials.AccountId, ct);
                _cache = (routeKey, token, credentials.AccountId, snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            }
        }
        finally
        {
            _lock.Release();
        }

        return ApplyMemberGate(snapshot, member, token);
    }

    /// <summary>
    /// When the configured <see cref="AgentMembership.ModelId"/> is not present
    /// in the parsed response's per-model buckets, return
    /// <c>AvailablePct = -1</c> so the router falls onto its
    /// <c>QuotaUnknownPolicy</c> rather than fail-opening on the global
    /// availability. Logs once per (token, modelId) so operators can spot
    /// typos in configured model ids without grepping for log lines.
    /// </summary>
    private AgentQuotaSnapshot ApplyMemberGate(AgentQuotaSnapshot snapshot, AgentMembership member, string token)
    {
        if (snapshot.AvailablePct < 0) return snapshot;
        if (string.IsNullOrWhiteSpace(member.ModelId)) return snapshot;
        if (snapshot.PerModel.ContainsKey(member.ModelId)) return snapshot;

        var modelList = snapshot.PerModel.Count == 0
            ? "(none)"
            : string.Join(", ", snapshot.PerModel.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var notes = $"configured model '{member.ModelId}' not in quota response (have: {modelList})";

        bool firstTime;
        lock (_loggedMissingModelsLock)
            firstTime = _loggedMissingModels.Add((token, member.ModelId));
        if (firstTime)
        {
            _log.LogInformation(
                "Codex quota probe: configured model {ModelId} not in response buckets ({BucketList}); reporting unknown so the router can apply its unknown policy",
                member.ModelId, modelList);
        }

        return new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            Unknown = QuotaUnknownReason.Permanent,
            ResetAt = snapshot.ResetAt,
            Notes = notes,
            PerModel = snapshot.PerModel,
        };
    }

    /// <summary>
    /// Drops the in-process snapshot so the next
    /// <see cref="GetAvailabilityAsync"/> call refetches against the WHAM
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

    private async Task<AgentQuotaSnapshot> FetchAsync(string token, string? accountId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");

            // Do NOT log the Authorization header — it contains the ChatGPT token.
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrWhiteSpace(accountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Codex usage endpoint returned {StatusCode}; treating quota as unknown",
                    (int)response.StatusCode);
                return Unknown(QuotaUnknownReasons.FromHttpStatus(response.StatusCode), $"HTTP {(int)response.StatusCode}");
            }

            // Do NOT log the response body — it may contain account identifiers.
            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null) return Unknown(QuotaUnknownReason.Permanent, "response too large");
            return ParseResponse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Codex quota probe failed; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "network error");
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
            var resetCredits = TryGetResetCreditsAvailable(root);

            // Cap per-model availability by the overall account quota. The
            // WHAM endpoint exposes a per-model bucket alongside an overall
            // bucket; even when the per-model bucket reports plenty of room,
            // the account-wide rate_limit can deny calls (allowed=false /
            // limit_reached=true). Per-model must respect the overall cap.
            if (overall is not null && perModel.Count > 0)
            {
                var capPct = overall.AvailablePct;
                foreach (var key in perModel.Keys.ToList())
                {
                    var v = perModel[key];
                    if (v.AvailablePct > capPct)
                    {
                        perModel[key] = new ModelQuota
                        {
                            AvailablePct = capPct,
                            ResetAt = overall.ResetAt ?? v.ResetAt,
                            Window = $"{v.Window} (capped by overall)",
                            Windows = v.Windows,
                        };
                    }
                }
            }

            if (overall is not null || perModel.Count > 0)
                return new AgentQuotaSnapshot
                {
                    AvailablePct = overall?.AvailablePct ?? -1,
                    ResetAt = overall?.ResetAt,
                    Notes = overall is null ? "overall quota unknown; parsed per-model rollups" : null,
                    PerModel = perModel,
                    Windows = overall?.Windows ?? Array.Empty<WindowQuota>(),
                    ResetCreditsAvailable = resetCredits,
                };

            return Unknown(QuotaUnknownReason.Permanent, "unexpected response shape");
        }
        catch (JsonException)
        {
            return Unknown(QuotaUnknownReason.Permanent, "invalid JSON");
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
            if (quota is null)
                continue;

            perModel[modelId] = quota;
            if (RoutedModelAliases.TryGetValue(modelId, out var aliases))
            {
                foreach (var alias in aliases)
                    perModel.TryAdd(alias, quota);
            }
        }

        return perModel;
    }

    private static ModelQuota? TryParseRateLimit(JsonElement el)
    {
        var windowSources = new[]
        {
            ("5h-rolling", TryGetProperty(el, "primary_window")),
            ("weekly", TryGetProperty(el, "secondary_window")),
            ("overall", (JsonElement?)el),
        };

        // Two passes: first collect every named window, then pick the min.
        // The collected list is preserved on the returned ModelQuota so
        // operators can see (via /quota) which window is the actual gate.
        var windows = new List<WindowQuota>(windowSources.Length);
        ModelQuota? mostConstrained = null;
        foreach (var (windowName, window) in windowSources)
        {
            if (window is null)
                continue;

            var quota = TryParseWindow(window.Value, windowName);
            if (quota is null)
                continue;

            // Skip the synthetic "overall" entry from the window list when it
            // duplicates the named windows — it carries no extra signal and
            // would only confuse the per-window breakdown.
            if (windowName != "overall")
                windows.Add(new WindowQuota
                {
                    Name = windowName,
                    AvailablePct = quota.AvailablePct,
                    ResetAt = quota.ResetAt,
                    UsedPercent = TryGetRawUsedPercent(window.Value),
                    ResetAtEpochSeconds = TryGetRawResetEpochSeconds(window.Value),
                });

            if (mostConstrained is null || quota.AvailablePct < mostConstrained.AvailablePct)
                mostConstrained = quota;
        }

        return mostConstrained is null
            ? null
            : mostConstrained with { Windows = windows };
    }

    private static ModelQuota? TryParseWindow(JsonElement el, string window)
    {
        // Explicit deny flags trump used_percent. The WHAM endpoint sets
        // `allowed: false` / `limit_reached: true` when a window is exhausted.
        var explicitDeny = (TryGetBoolProperty(el, "allowed", out var allowed) && !allowed)
                        || (TryGetBoolProperty(el, "limit_reached", out var lim) && lim);

        if (!TryGetDoubleProperty(el, "used_percent", out var usedPct) &&
            !TryGetDoubleProperty(el, "usedPercent", out usedPct))
        {
            // No used_percent — but if a deny flag is set, still report 0%.
            if (explicitDeny)
                return new ModelQuota { AvailablePct = 0, ResetAt = TryGetResetAt(el), Window = window };
            return null;
        }

        var pct = Math.Clamp(100.0 - usedPct, 0.0, 100.0);
        if (explicitDeny) pct = 0;
        return new ModelQuota
        {
            AvailablePct = pct,
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

    /// <summary>
    /// Reads the untransformed <c>used_percent</c> (or camelCase
    /// <c>usedPercent</c>) from a window element, without the invert/clamp
    /// applied to <see cref="ModelQuota.AvailablePct"/>. Null when absent.
    /// </summary>
    private static double? TryGetRawUsedPercent(JsonElement el) =>
        TryGetDoubleProperty(el, "used_percent", out var pct) ||
        TryGetDoubleProperty(el, "usedPercent", out pct)
            ? pct
            : null;

    /// <summary>
    /// Reads the raw <c>reset_at</c> (or camelCase <c>resetAt</c>) as Unix epoch
    /// seconds when the provider expresses it numerically. Null when the field
    /// is absent or non-numeric (e.g. an ISO-8601 string).
    /// </summary>
    private static long? TryGetRawResetEpochSeconds(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        var reset = TryGetProperty(el, "reset_at") ?? TryGetProperty(el, "resetAt");
        return reset is { ValueKind: JsonValueKind.Number } num && num.TryGetInt64(out var seconds)
            ? seconds
            : null;
    }

    /// <summary>
    /// Reads the top-level <c>rate_limit_reset_credits.available_count</c> — the
    /// number of banked manual quota resets the account can still spend. Null
    /// when the object or field is absent.
    /// </summary>
    private static int? TryGetResetCreditsAvailable(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rate_limit_reset_credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Object)
            return null;
        return credits.TryGetProperty("available_count", out var count) &&
               count.ValueKind == JsonValueKind.Number &&
               count.TryGetInt32(out var value)
            ? value
            : null;
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

    private static AgentQuotaSnapshot Unknown(QuotaUnknownReason reason, string notes) =>
        AgentQuotaSnapshot.UnknownSnapshot(reason, notes);
}
