using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

/// <summary>
/// Probes the Google Code Assist private API to estimate available Gemini
/// subscription quota for users on the OAuth (Sign-in-with-Google) path.
///
/// <para>
/// Primary signal: live <c>:generateContent</c> calls. For a fixed
/// <see cref="AgentMembership.ModelId"/> we issue a single minimal-cost ping at
/// that model; <c>200</c> ⇒ 100% available, <c>429</c> ⇒ 0% with Retry-After
/// reset, everything else ⇒ Unknown. When the membership uses the
/// <c>auto</c> sentinel (<see cref="GeminiKnownModels.AutoSentinel"/>) we fan
/// the ping across <see cref="GeminiKnownModels.All"/> at bounded concurrency
/// and aggregate. The per-model rate-limit fragmentation Code Assist exhibits
/// (e.g. <c>gemini-2.5-pro</c> 200 while <c>gemini-2.5-flash</c> 429) is
/// invisible to the aggregated <c>:retrieveUserQuota</c> fraction; the live
/// ping is the only signal that distinguishes them.
/// </para>
///
/// <para>
/// Legacy fallback: when no ModelId is configured we have no specific model
/// to ping, so we fall through to <c>:retrieveUserQuota</c> for an overall
/// snapshot. Callers that want per-model live data must pass a ModelId (or
/// the <c>auto</c> sentinel).
/// </para>
///
/// <para>
/// <c>:retrieveUserQuota</c> is still parsed by <see cref="ParseResponse"/>
/// for the no-ModelId legacy path and for future TierSignal use; it is no
/// longer the primary AvailablePct signal because operator testing confirmed
/// it can report 100% while a live <c>:generateContent</c> returns 429.
/// </para>
///
/// <para>
/// Only valid for OAuth subscription users (Code Assist Individual / AI Pro /
/// AI Ultra). API-key (PayPerApi) and Vertex paths have no analogous endpoint
/// — leave them as PayPerApi members in the agent class config.
/// </para>
///
/// <para>
/// Any network error, expired token, or unrecognised shape returns
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1.
/// </para>
/// </summary>
public sealed class GeminiQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    internal const string GenerateContentEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:generateContent";

    /// <summary>Bounded concurrency for the auto-sentinel fan-out.</summary>
    internal const int AutoFanOutConcurrency = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<GeminiQuotaProbe> _log;

    // Cache keyed by (token, modelKey). modelKey = the configured model id, or
    // GeminiKnownModels.AutoSentinel for the auto fan-out result, or "" for the
    // no-ModelId legacy fallback that hits retrieveUserQuota. Two members on
    // the same account using different models don't clobber each other.
    private readonly Dictionary<(string Token, string ModelKey), CacheEntry> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Gemini;

    public GeminiQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<GeminiQuotaProbe> log)
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

        bool isAuto = GeminiKnownModels.IsAuto(member.ModelId);
        bool hasFixedModel = !isAuto && !string.IsNullOrWhiteSpace(member.ModelId);
        string modelKey = isAuto ? GeminiKnownModels.AutoSentinel
            : hasFixedModel ? member.ModelId!
            : "";

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue((token, modelKey), out var entry)
                && DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                snapshot = entry.Snapshot;
            }
            else
            {
                snapshot = isAuto
                    ? await FetchAutoAsync(token, ct)
                    : hasFixedModel
                        ? await FetchSingleAsync(token, member.ModelId!, ct)
                        : await FetchTierSignalAsync(token, ct);
                _cache[(token, modelKey)] = new CacheEntry(snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            }
        }
        finally
        {
            _lock.Release();
        }

        return snapshot;
    }

    /// <summary>
    /// Drops the in-process snapshot so the next
    /// <see cref="GetAvailabilityAsync"/> call refetches against Code Assist.
    /// Wire to <see cref="CodeyBox.Orchestrator.CredentialFileSource.TokenUpdated"/>
    /// so an out-of-band host token rotation (operator running the CLI, child
    /// sandbox writeback, scripted refresh) doesn't leave a stale 401 pinned
    /// for the full cache TTL.
    /// </summary>
    public void InvalidateCache()
    {
        _lock.Wait();
        try { _cache.Clear(); }
        finally { _lock.Release(); }
    }

    private const int MaxResponseChars = 64 * 1024;

    /// <summary>
    /// Single-model live probe via <c>:generateContent</c>. 200 ⇒ available,
    /// 429 ⇒ rate-limited (Retry-After-derived reset when present), any other
    /// status (including 404 for a non-existent model id) ⇒ Unknown.
    /// </summary>
    internal async Task<AgentQuotaSnapshot> FetchSingleAsync(string token, string modelId, CancellationToken ct)
    {
        var result = await ProbeOneAsync(token, modelId, ct);
        if (result.Status is null)
            return Unknown($"live probe of {modelId}: transient error");

        if (result.Status == HttpStatusCode.OK)
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

        if (result.Status == HttpStatusCode.TooManyRequests)
        {
            var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
            {
                [modelId] = new ModelQuota { AvailablePct = 0.0, ResetAt = result.ResetAt, Window = "REQUESTS" },
            };
            return new AgentQuotaSnapshot
            {
                AvailablePct = 0.0,
                ResetAt = result.ResetAt,
                Notes = $"live probe via {modelId}: rate-limited",
                PerModel = perModel,
            };
        }

        return Unknown($"live probe of {modelId}: HTTP {(int)result.Status.Value}");
    }

    /// <summary>
    /// Legacy fallback for callers that don't configure a ModelId: hits
    /// <c>:retrieveUserQuota</c> and reports the most-constrained bucket as
    /// overall availability. The response is still parsed by
    /// <see cref="ParseResponse"/> so the per-model breakdown surfaces in
    /// diagnostics.
    /// </summary>
    internal async Task<AgentQuotaSnapshot> FetchTierSignalAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Gemini quota endpoint returned {StatusCode}; treating quota as unknown",
                    (int)response.StatusCode);
                return Unknown($"HTTP {(int)response.StatusCode}");
            }

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
            _log.LogDebug(ex, "Gemini quota probe failed; treating quota as unknown");
            return Unknown("network error");
        }
    }

    /// <summary>
    /// Auto-sentinel ground-truth probe: fans out :generateContent across the
    /// known bucket list (bounded concurrency) and aggregates per-model results.
    /// Any 200 → AvailablePct=100; all 429 → AvailablePct=0 with min reset;
    /// mixed → still 100 (Gemini's ModelRouterService will pick whichever
    /// model is up at run time). Non-2xx/429 statuses are treated as "unknown
    /// for that model" and excluded from the aggregate; if every model is
    /// unknown the snapshot is Unknown.
    /// </summary>
    internal async Task<AgentQuotaSnapshot> FetchAutoAsync(string token, CancellationToken ct)
    {
        var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
        using var sem = new SemaphoreSlim(AutoFanOutConcurrency, AutoFanOutConcurrency);
        var tasks = GeminiKnownModels.All.Select(async modelId =>
        {
            await sem.WaitAsync(ct);
            try
            {
                return (ModelId: modelId, Result: await ProbeOneAsync(token, modelId, ct));
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        string? routedVia = null;
        DateTimeOffset? earliestReset = null;
        bool anyOk = false;

        foreach (var (modelId, result) in results)
        {
            if (result.Status is null)
            {
                // Transient — not a definitive answer for this model; omit.
                continue;
            }
            if (result.Status == HttpStatusCode.OK)
            {
                anyOk = true;
                routedVia ??= modelId;
                perModel[modelId] = new ModelQuota { AvailablePct = 100.0, ResetAt = null, Window = "REQUESTS" };
            }
            else if (result.Status == HttpStatusCode.TooManyRequests)
            {
                perModel[modelId] = new ModelQuota { AvailablePct = 0.0, ResetAt = result.ResetAt, Window = "REQUESTS" };
                if (result.ResetAt is { } r && (earliestReset is null || r < earliestReset))
                    earliestReset = r;
            }
            // else: 4xx/5xx other than 429 — treat as unknown for that model
            // (omitted from perModel).
        }

        // Treat the run as Unknown if no model returned a definitive 200/429
        // (only those statuses populate perModel).
        if (perModel.Count == 0)
            return Unknown("auto fan-out: no definitive responses");

        var notes = anyOk
            ? $"auto routed via {routedVia}"
            : "auto fan-out: all models rate-limited";

        return new AgentQuotaSnapshot
        {
            AvailablePct = anyOk ? 100.0 : 0.0,
            ResetAt = anyOk ? null : earliestReset,
            Notes = notes,
            PerModel = perModel,
        };
    }

    internal async Task<ProbeOneResult> ProbeOneAsync(string token, string modelId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, GenerateContentEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Minimum-cost body that exercises the per-model quota path. The
            // response content is discarded — we only care about status.
            var body = "{\"model\":\"models/" + modelId
                + "\",\"request\":{\"contents\":[{\"parts\":[{\"text\":\"ping\"}]}],"
                + "\"generationConfig\":{\"maxOutputTokens\":1}}}";
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = TryParseRetryAfter(response, DateTimeOffset.UtcNow);
                return new ProbeOneResult(response.StatusCode, reset);
            }
            return new ProbeOneResult(response.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Gemini live-probe of {ModelId} failed; treating as unknown", modelId);
            return new ProbeOneResult(null, null);
        }
    }

    internal record struct ProbeOneResult(HttpStatusCode? Status, DateTimeOffset? ResetAt);

    private static DateTimeOffset? TryParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        // Prefer the Retry-After header (delta-seconds or HTTP-date).
        var ra = response.Headers.RetryAfter;
        if (ra is not null)
        {
            if (ra.Delta is { } delta) return now + delta;
            if (ra.Date is { } when) return when;
        }
        return null;
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

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                return Unknown("unexpected response shape");

            var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);
            ModelQuota? mostConstrained = null;

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object) continue;
                if (!bucket.TryGetProperty("modelId", out var modelEl) || modelEl.ValueKind != JsonValueKind.String) continue;
                var modelId = modelEl.GetString();
                if (string.IsNullOrWhiteSpace(modelId)) continue;

                if (!TryGetDoubleProperty(bucket, "remainingFraction", out var remaining)) continue;
                var availPct = Math.Clamp(remaining * 100.0, 0.0, 100.0);

                var quota = new ModelQuota
                {
                    AvailablePct = availPct,
                    ResetAt = TryGetResetTime(bucket),
                    Window = TryGetString(bucket, "tokenType") ?? "REQUESTS",
                };
                perModel[modelId] = quota;

                if (mostConstrained is null || quota.AvailablePct < mostConstrained.AvailablePct)
                    mostConstrained = quota;
            }

            if (perModel.Count == 0)
                return Unknown("no buckets in response");

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
            return Unknown("invalid JSON");
        }
    }

    private static bool TryGetDoubleProperty(JsonElement el, string name, out double value)
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
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? TryGetResetTime(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("resetTime", out var prop))
            return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(prop.GetString(), out var parsed) ? parsed : null;
    }

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };

    private sealed record CacheEntry(AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt);
}
