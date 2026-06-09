using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Probes the cloudcode-pa Code Assist family for Antigravity (<c>agy</c>)
/// availability. The CLI itself exposes <em>no</em> usage/quota/whoami
/// command, so the probe must INFER availability from gateway state.
///
/// <para><b>Per-model gating.</b> Antigravity's gateway meters each gateway
/// model (gemini-3.5-flash-high, claude-opus-4-6-thinking, gpt-oss-120b-medium,
/// …) on its OWN request bucket. The router already keys exhaustion as
/// <c>(AgentKind, ModelId)</c> so the natural design is one
/// <see cref="AgentMembership"/> per accepted gateway model; the probe then
/// gates each membership on its own bucket and the router fails over
/// model-by-model. We do NOT introduce a separate "sub-subscription pool"
/// subsystem — the existing per-model exhaustion key already gives us the
/// pool semantics.</para>
///
/// <para><b>Signal selection.</b> Two endpoints share the family with the
/// Gemini probe: <c>:retrieveUserQuotaSummary</c> (preferred when the response
/// carries per-window/tier data cleanly) and <c>:retrieveUserQuota</c>
/// (per-model bucket fragments). When neither endpoint yields a per-model
/// reading for the requested model, the probe falls back to a minimum-cost
/// <c>:generateContent</c> live ping at that model — 200 ⇒ available, 429 ⇒
/// rate-limited (Retry-After / lockout reset is propagated), anything else ⇒
/// unknown. This matches the Gemini live-ping fallback rationale (the per-
/// model bucket reading can read 100% while a live call returns 429).</para>
///
/// <para><b>7-day lockout handling.</b> AI Pro caps weekly with up to a 7-day
/// lockout on cap breach. The probe must surface that absolute reset time
/// (not a "next 5 min" Retry-After) so failed work items park cleanly in
/// <c>WaitingForQuotaReset</c> instead of churning. We honour both Retry-After
/// (when the gateway emits delta-seconds) and the structured
/// <c>quota_metadata.lockout_until</c> field that the gateway is observed to
/// emit alongside 429.</para>
/// </summary>
public sealed class AntigravityQuotaProbe : IAgentQuotaProbe
{
    internal const string QuotaSummaryEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    internal const string QuotaEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    internal const string GenerateContentEndpoint =
        "https://cloudcode-pa.googleapis.com/v1internal:generateContent";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<AntigravityQuotaProbe> _log;
    private readonly TimeProvider _timeProvider;

    // Cache keyed by (route key, token, modelKey). Per-model so two members on
    // the same account but different gateway models don't clobber each other.
    private readonly Dictionary<(string RouteKey, string Token, string ModelKey), CacheEntry> _cache = new();
    // In-process exhaustion overrides written by MarkExhaustedAsync, keyed the
    // same way. Synthetic AvailablePct=0 + the gateway's reset is surfaced
    // until expiry so a real-time 429 from the runner gates subsequent picks
    // without waiting for the next probe call.
    private readonly Dictionary<(string RouteKey, string Token, string ModelKey), ExhaustionOverride> _exhausted = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Antigravity;

    public AntigravityQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<AntigravityQuotaProbe> log,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _cacheTtl = cacheTtl;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var credentials = _credentialsProvider(member);
        var token = credentials.AccessToken;
        if (string.IsNullOrEmpty(token))
            return Unknown("no token configured");
        var routeKey = member.RouteKey;
        var modelKey = string.IsNullOrWhiteSpace(member.ModelId) ? "" : member.ModelId!;
        var cacheKey = (routeKey, token, modelKey);

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_exhausted.TryGetValue(cacheKey, out var ex) && ex.ExpiresAt > now)
            {
                snapshot = new AgentQuotaSnapshot
                {
                    AvailablePct = 0.0,
                    ResetAt = ex.ResetAt ?? ex.ExpiresAt,
                    Notes = "exhausted (runtime 429 hint)",
                };
                return snapshot;
            }
            _exhausted.Remove(cacheKey);

            if (_cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt > now)
                return entry.Snapshot;

            snapshot = string.IsNullOrEmpty(modelKey)
                ? await FetchTierSignalAsync(token, ct).ConfigureAwait(false)
                : await FetchSingleAsync(token, modelKey, ct).ConfigureAwait(false);
            _cache[cacheKey] = new CacheEntry(snapshot, now + _cacheTtl);
        }
        finally
        {
            _lock.Release();
        }

        return snapshot;
    }

    public async Task MarkExhaustedAsync(
        AgentMembership member,
        TimeSpan ttl,
        DateTimeOffset? resetAt = null,
        CancellationToken ct = default)
    {
        var credentials = _credentialsProvider(member);
        var token = credentials.AccessToken;
        if (string.IsNullOrEmpty(token)) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            // Cap the lockout window at the gateway-provided reset when it is
            // sooner than TTL — a runtime hint shouldn't push the parking
            // window past the actual reset moment.
            var expiry = now + (ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(1));
            if (resetAt is { } r && r > now && r < expiry)
                expiry = r;
            var modelKey = string.IsNullOrWhiteSpace(member.ModelId) ? "" : member.ModelId!;
            _exhausted[(member.RouteKey, token, modelKey)] = new ExhaustionOverride(expiry, resetAt);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateCache()
    {
        _lock.Wait();
        try { _cache.Clear(); _exhausted.Clear(); }
        finally { _lock.Release(); }
    }

    private const int MaxResponseChars = 64 * 1024;

    /// <summary>
    /// Per-model probe. Prefers <c>:retrieveUserQuotaSummary</c>, then
    /// <c>:retrieveUserQuota</c>, then a live <c>:generateContent</c> ping at
    /// the requested model id.
    /// </summary>
    internal async Task<AgentQuotaSnapshot> FetchSingleAsync(string token, string modelId, CancellationToken ct)
    {
        var summary = await TryReadSummaryAsync(token, ct).ConfigureAwait(false);
        if (summary is not null && summary.PerModel.TryGetValue(modelId, out var modelQuota))
        {
            return new AgentQuotaSnapshot
            {
                AvailablePct = modelQuota.AvailablePct,
                ResetAt = modelQuota.ResetAt,
                Notes = "retrieveUserQuotaSummary",
                PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                {
                    [modelId] = modelQuota,
                },
            };
        }

        var legacy = await TryReadQuotaAsync(token, ct).ConfigureAwait(false);
        if (legacy is not null && legacy.PerModel.TryGetValue(modelId, out var legacyQuota))
        {
            return new AgentQuotaSnapshot
            {
                AvailablePct = legacyQuota.AvailablePct,
                ResetAt = legacyQuota.ResetAt,
                Notes = "retrieveUserQuota",
                PerModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                {
                    [modelId] = legacyQuota,
                },
            };
        }

        // Live ping fallback: matches GeminiQuotaProbe.ProbeOneAsync.
        var live = await LivePingAsync(token, modelId, ct).ConfigureAwait(false);
        if (live.Status is null)
            return Unknown($"live probe of {modelId}: transient error");
        if (live.Status == HttpStatusCode.OK)
        {
            var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                [modelId] = new ModelQuota { AvailablePct = 100.0, ResetAt = null, Window = "REQUESTS" },
            };
            return new AgentQuotaSnapshot
            {
                AvailablePct = 100.0,
                ResetAt = null,
                Notes = $"live probe via {modelId}",
                PerModel = perModel,
            };
        }
        if (live.Status == HttpStatusCode.TooManyRequests)
        {
            var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                [modelId] = new ModelQuota { AvailablePct = 0.0, ResetAt = live.ResetAt, Window = "REQUESTS" },
            };
            return new AgentQuotaSnapshot
            {
                AvailablePct = 0.0,
                ResetAt = live.ResetAt,
                Notes = $"live probe via {modelId}: rate-limited",
                PerModel = perModel,
            };
        }
        return Unknown($"live probe of {modelId}: HTTP {(int)live.Status.Value}");
    }

    /// <summary>
    /// Legacy fallback for callers that don't configure a ModelId. Hits
    /// <c>:retrieveUserQuotaSummary</c> for an overall snapshot; falls back to
    /// <c>:retrieveUserQuota</c>. The most-constrained bucket becomes the
    /// reported <c>AvailablePct</c>.
    /// </summary>
    internal async Task<AgentQuotaSnapshot> FetchTierSignalAsync(string token, CancellationToken ct)
    {
        var summary = await TryReadSummaryAsync(token, ct).ConfigureAwait(false);
        if (summary is not null) return summary;
        var legacy = await TryReadQuotaAsync(token, ct).ConfigureAwait(false);
        return legacy ?? Unknown("no tier signal");
    }

    private async Task<AgentQuotaSnapshot?> TryReadSummaryAsync(string token, CancellationToken ct)
        => await TryReadJsonAsync(token, QuotaSummaryEndpoint, ParseSummaryResponse, ct).ConfigureAwait(false);

    private async Task<AgentQuotaSnapshot?> TryReadQuotaAsync(string token, CancellationToken ct)
        => await TryReadJsonAsync(token, QuotaEndpoint, ParseQuotaResponse, ct).ConfigureAwait(false);

    private async Task<AgentQuotaSnapshot?> TryReadJsonAsync(
        string token,
        string endpoint,
        Func<string, AgentQuotaSnapshot?> parser,
        CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Antigravity {Endpoint} returned {StatusCode}", endpoint, (int)response.StatusCode);
                return null;
            }
            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (body is null) return null;
            return parser(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Antigravity quota endpoint {Endpoint} failed", endpoint);
            return null;
        }
    }

    internal async Task<LivePingResult> LivePingAsync(string token, string modelId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, GenerateContentEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = "{\"model\":\"models/" + modelId
                + "\",\"request\":{\"contents\":[{\"parts\":[{\"text\":\"ping\"}]}],"
                + "\"generationConfig\":{\"maxOutputTokens\":1}}}";
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = TryParseRetryAfter(response, _timeProvider.GetUtcNow())
                    ?? await TryParseStructuredResetAsync(response, ct).ConfigureAwait(false);
                return new LivePingResult(response.StatusCode, reset);
            }
            return new LivePingResult(response.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Antigravity live probe failed");
            return new LivePingResult(null, null);
        }
    }

    private static DateTimeOffset? TryParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return now + delta;
        if (ra.Date is { } when) return when;
        return null;
    }

    private static async Task<DateTimeOffset?> TryParseStructuredResetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // The gateway has been observed to surface lockout_until alongside 429
        // bodies. Try to parse it so a 7-day lockout pins ResetAt to the exact
        // moment instead of relying on Retry-After delta-seconds.
        try
        {
            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (body is null) return null;
            return AntigravityQuotaFailureDetector.ExtractStructuredLockoutReset(body);
        }
        catch
        {
            return null;
        }
    }

    internal record struct LivePingResult(HttpStatusCode? Status, DateTimeOffset? ResetAt);

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

    /// <summary>
    /// Parses the <c>retrieveUserQuotaSummary</c> response. Expected shape:
    /// <code>
    /// {
    ///   "windows": [{"name":"weekly","remainingFraction":0.42,"resetTime":"2026-06-16T12:00:00Z"}, ...],
    ///   "perModel": [{"modelId":"gemini-3.5-flash-high","remainingFraction":0.42,"resetTime":"...","window":"weekly"}, ...]
    /// }
    /// </code>
    /// Either field may be absent on tiers without that meter. We also accept
    /// the <c>retrieveUserQuota</c>-style <c>buckets</c> array as a defensive
    /// alias so a quietly-renamed endpoint keeps parsing.
    /// </summary>
    internal static AgentQuotaSnapshot? ParseSummaryResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var windows = new List<WindowQuota>();
            ModelQuota? mostConstrained = null;

            if (root.TryGetProperty("windows", out var winEl) && winEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in winEl.EnumerateArray())
                {
                    if (w.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetDouble(w, "remainingFraction", out var remaining)) continue;
                    var name = TryGetString(w, "name") ?? TryGetString(w, "window") ?? "window";
                    var availPct = Math.Clamp(remaining * 100.0, 0.0, 100.0);
                    var resetAt = TryGetResetTime(w);
                    windows.Add(new WindowQuota { Name = name, AvailablePct = availPct, ResetAt = resetAt });
                    var quota = new ModelQuota { AvailablePct = availPct, ResetAt = resetAt, Window = name };
                    if (mostConstrained is null || quota.AvailablePct < mostConstrained.AvailablePct)
                        mostConstrained = quota;
                }
            }

            var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("perModel", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
                CollectPerModel(modelsEl, perModel);
            else if (root.TryGetProperty("buckets", out var bucketsEl) && bucketsEl.ValueKind == JsonValueKind.Array)
                CollectPerModel(bucketsEl, perModel);

            if (perModel.Count == 0 && mostConstrained is null) return null;

            return new AgentQuotaSnapshot
            {
                AvailablePct = mostConstrained?.AvailablePct
                    ?? (perModel.Count > 0 ? perModel.Values.Min(v => v.AvailablePct) : -1),
                ResetAt = mostConstrained?.ResetAt,
                Notes = null,
                PerModel = perModel,
                Windows = windows,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the legacy <c>retrieveUserQuota</c> response (the same bucket
    /// array shape <c>GeminiQuotaProbe</c> reads). Used as a fallback when
    /// <c>retrieveUserQuotaSummary</c> is absent or empty.
    /// </summary>
    internal static AgentQuotaSnapshot? ParseQuotaResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return null;

            var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
            CollectPerModel(buckets, perModel);
            if (perModel.Count == 0) return null;

            var mostConstrained = perModel.Values.MinBy(q => q.AvailablePct);
            return new AgentQuotaSnapshot
            {
                AvailablePct = mostConstrained?.AvailablePct ?? -1,
                ResetAt = mostConstrained?.ResetAt,
                Notes = null,
                PerModel = perModel,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectPerModel(JsonElement array, Dictionary<string, ModelQuota> sink)
    {
        foreach (var bucket in array.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object) continue;
            var modelId = TryGetString(bucket, "modelId")
                ?? TryGetString(bucket, "model")
                ?? TryGetString(bucket, "limit_name");
            if (string.IsNullOrWhiteSpace(modelId)) continue;
            if (!TryGetDouble(bucket, "remainingFraction", out var remaining)
                && !TryGetDouble(bucket, "remaining_fraction", out remaining))
            {
                continue;
            }
            var availPct = Math.Clamp(remaining * 100.0, 0.0, 100.0);
            sink[modelId] = new ModelQuota
            {
                AvailablePct = availPct,
                ResetAt = TryGetResetTime(bucket),
                Window = TryGetString(bucket, "window") ?? TryGetString(bucket, "tokenType") ?? "REQUESTS",
            };
        }
    }

    private static bool TryGetDouble(JsonElement el, string name, out double value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return false;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(prop.GetString(), out value),
            _ => false,
        };
    }

    private static string? TryGetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? TryGetResetTime(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "resetTime", "reset_at", "lockoutUntil", "lockout_until" })
        {
            if (!el.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(prop.GetString(), out var parsed))
                return parsed;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var unix))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(unix); }
                catch (ArgumentOutOfRangeException) { /* fall through */ }
            }
        }
        return null;
    }

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };

    private sealed record CacheEntry(AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt);
    private sealed record ExhaustionOverride(DateTimeOffset ExpiresAt, DateTimeOffset? ResetAt);
}
