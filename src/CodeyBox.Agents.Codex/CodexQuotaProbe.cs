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
public sealed class CodexQuotaProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator
{
    internal const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    /// <summary>
    /// Provider-side WHAM display-bucket name under which a Codex subscription's
    /// usage is reported. This is an identifier in the upstream response, NOT a
    /// routing default: the model the CLI actually routes to is sourced from
    /// config (<see cref="AgentDefaultsSnapshot"/> / the class member's ModelId),
    /// and <see cref="ApplyMemberGate"/> aliases this bucket's reading onto that
    /// configured model. (Deferred follow-up: fold this into a config-driven
    /// known-bucket list alongside the other provider model lists.)
    /// </summary>
    internal const string SubscriptionUsageBucketName = "GPT-5.3-Codex-Spark";

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentDefaultsSnapshot? _defaults;
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
        ILogger<CodexQuotaProbe> log,
        AgentDefaultsSnapshot? defaults = null)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _cacheTtl = cacheTtl;
        _log = log;
        _defaults = defaults;
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
    /// in the parsed response's per-model buckets, fall back to the overall
    /// account reading before degrading to unknown. The WHAM usage endpoint caps
    /// every per-model bucket by the account-wide 5h/weekly windows ("capped by
    /// overall" in <see cref="ParseResponse"/>), so those windows ARE the binding
    /// quota for any configured model — including a newly released one the backend
    /// has not yet minted a dedicated bucket for (e.g. gpt-5.6-sol on launch day,
    /// where the response only lists gpt-5.5 / GPT-5.3-Codex-Spark). Resolving to
    /// the overall reading keeps the snapshot KNOWN (with the reading added to
    /// <see cref="AgentQuotaSnapshot.PerModel"/> under the configured id so
    /// <c>QuotaGatePolicy.ResolveMemberQuota</c> finds it) rather than reporting
    /// -1, which would drop the class below its known-quota peers under
    /// MostQuotaFirst and starve it despite ample headroom. Only degrade to
    /// Unknown when there is no overall reading at all (guarded above). Logs once
    /// per (token, modelId) so operators can still spot typos in configured ids.
    /// Mirrors <c>ClaudeQuotaProbe.ApplyMemberGate</c>; codex has no family-bucket
    /// layer (OpenAI rate limits are account-wide windows, not per-model), so the
    /// overall reading is the only resolution step.
    /// </summary>
    private AgentQuotaSnapshot ApplyMemberGate(AgentQuotaSnapshot snapshot, AgentMembership member, string token)
    {
        if (snapshot.AvailablePct < 0) return snapshot;
        if (string.IsNullOrWhiteSpace(member.ModelId)) return snapshot;
        if (snapshot.PerModel.ContainsKey(member.ModelId)) return snapshot;

        // Config-driven subscription-bucket alias (replaces the former hardcoded
        // gpt-5.5 alias target). The WHAM response reports a Codex subscription's
        // usage under a provider display-bucket name (SubscriptionUsageBucketName),
        // not the model id the CLI routes to. When the member being gated IS the
        // configured codex routed-default model (CodeyBox:AgentDefaults[codex]) and
        // that display bucket is present, alias its already-overall-capped reading
        // onto the member so the floor is enforced on the subscription's own bucket
        // rather than the looser account-wide overall. Sourcing the routed-model
        // identity from config (not a source literal) makes a model rev a config
        // edit, not a code change. Any other configured model (e.g. a newly
        // released id the backend has not minted a bucket for) falls through to the
        // overall reading below, unchanged.
        var routedDefault = _defaults?.GetDefault(Kind.Value);
        if (!string.IsNullOrWhiteSpace(routedDefault)
            && string.Equals(routedDefault, member.ModelId, StringComparison.OrdinalIgnoreCase)
            && snapshot.PerModel.TryGetValue(SubscriptionUsageBucketName, out var subscriptionQuota))
        {
            var aliasedPerModel = new Dictionary<string, ModelQuota>(snapshot.PerModel, StringComparer.OrdinalIgnoreCase)
            {
                [member.ModelId] = subscriptionQuota,
            };
            return new AgentQuotaSnapshot
            {
                AvailablePct = snapshot.AvailablePct,
                ResetAt = snapshot.ResetAt,
                Notes = snapshot.Notes,
                PerModel = aliasedPerModel,
                Windows = snapshot.Windows,
                ResetCreditsAvailable = snapshot.ResetCreditsAvailable,
            };
        }

        // The configured model is not individually enumerated, but the overall
        // reading is known (guarded above) and is the binding constraint for
        // every codex model. Resolve to it under the configured model id so the
        // floor is enforced on a KNOWN reading rather than degrading to Unknown.
        var perModel = new Dictionary<string, ModelQuota>(snapshot.PerModel, StringComparer.OrdinalIgnoreCase)
        {
            [member.ModelId] = new ModelQuota
            {
                AvailablePct = snapshot.AvailablePct,
                ResetAt = snapshot.ResetAt,
                Window = "overall (model-specific bucket unavailable)",
                Windows = snapshot.Windows,
            },
        };

        bool firstTime;
        lock (_loggedMissingModelsLock)
            firstTime = _loggedMissingModels.Add((token, member.ModelId));
        if (firstTime)
        {
            var modelList = snapshot.PerModel.Count == 0
                ? "(none)"
                : string.Join(", ", snapshot.PerModel.Keys.OrderBy(k => k, StringComparer.Ordinal));
            _log.LogInformation(
                "Codex quota probe: configured model {ModelId} not in response buckets ({BucketList}); resolving to overall account reading ({AvailablePct}%)",
                member.ModelId, modelList, snapshot.AvailablePct);
        }

        return new AgentQuotaSnapshot
        {
            AvailablePct = snapshot.AvailablePct,
            ResetAt = snapshot.ResetAt,
            Notes = snapshot.Notes,
            PerModel = perModel,
            Windows = snapshot.Windows,
            ResetCreditsAvailable = snapshot.ResetCreditsAvailable,
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
        foreach (var (positionalName, window) in windowSources)
        {
            if (window is null)
                continue;

            // Name the window by the length it declares, not by the slot it arrived in.
            var windowName = positionalName == "overall"
                ? positionalName
                : ResolveWindowName(window.Value, positionalName);

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

    /// <summary>
    /// Two days. Any window at least this long is a weekly allowance; anything shorter is the
    /// short rolling one. Sits far from both real values (5h = 18,000s, weekly = 604,800s) so a
    /// provider tweak to either does not flip the classification.
    /// </summary>
    private const double WeeklyWindowThresholdSeconds = 2 * 24 * 60 * 60;

    /// <summary>
    /// The window's real name, taken from the length the payload declares rather than from which
    /// JSON slot it arrived in.
    /// </summary>
    /// <remarks>
    /// The slot is not the window. OpenAI disabled the 5-hour limit for <b>Pro</b> accounts (it now
    /// applies to basic accounts only), so on Pro the WEEKLY allowance arrives in
    /// <c>primary_window</c> (<c>limit_window_seconds: 604800</c>) and <c>secondary_window</c> is
    /// null — while the positional convention would call that weekly window <c>5h-rolling</c>.
    /// Naming by declared length covers both account shapes without branching on plan type. Mislabelling it is not cosmetic: <c>/quota</c> then shows a
    /// weekly exhaustion as a five-hourly one (implying it recovers in hours when it recovers in
    /// days), and the per-window floor in <c>QuotaRouter.MinQuotaPctByWindow</c> is looked up under
    /// the wrong key. Falls back to the positional default when the length is absent.
    /// </remarks>
    private static string ResolveWindowName(JsonElement window, string positionalDefault)
    {
        if ((TryGetDoubleProperty(window, "limit_window_seconds", out var seconds)
             || TryGetDoubleProperty(window, "limitWindowSeconds", out seconds))
            && seconds > 0)
        {
            return seconds >= WeeklyWindowThresholdSeconds ? "weekly" : "5h-rolling";
        }

        return positionalDefault;
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
