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
/// Endpoint: <c>POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota</c>.
/// Returns <c>{"buckets": [{ "modelId", "remainingFraction" 0-1, "resetTime", "tokenType" }]}</c>.
///
/// Each bucket represents one model's remaining quota. Overall availability
/// is the most-restrictive bucket (min of remainingFraction × 100). Per-model
/// availability is the bucket for that model id, capped by overall.
///
/// Only valid for OAuth subscription users (Code Assist Individual / AI Pro
/// / AI Ultra). API-key (PayPerApi) and Vertex paths have no analogous
/// endpoint — leave them as PayPerApi members in the agent class config.
///
/// Any network error, expired token, or unrecognised shape returns
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1.
/// </summary>
public sealed class GeminiQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<GeminiQuotaProbe> _log;

    // Single-entry cache: (token, snapshot, expiry). Protected by _lock.
    private (string AccessToken, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Dedupes the Information log line that fires when a configured model is
    // absent from the probe response. Keyed by (token, modelId).
    private readonly HashSet<(string Token, string ModelId)> _loggedMissingModels = new();
    private readonly object _loggedMissingModelsLock = new();

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

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry
                && string.Equals(entry.AccessToken, token, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                snapshot = entry.Snapshot;
            }
            else
            {
                snapshot = await FetchAsync(token, ct);
                _cache = (token, snapshot, DateTimeOffset.UtcNow + _cacheTtl);
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
    /// in the parsed response's per-model buckets, we have no signal for the
    /// model we'd actually invoke. Return <c>AvailablePct = -1</c> so the
    /// router falls through to its <c>QuotaUnknownPolicy</c> instead of
    /// fail-opening on a global mostConstrained that ignores our target.
    /// Logs once per (token, modelId) so operators can spot typos like
    /// <c>gemini-3-flash-preview</c> → <c>gemini-3.1-flash-lite</c>.
    /// </summary>
    private AgentQuotaSnapshot ApplyMemberGate(AgentQuotaSnapshot snapshot, AgentMembership member, string token)
    {
        // Don't override an already-unknown snapshot — the existing notes are more useful.
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
                "Gemini quota probe: configured model {ModelId} not in response buckets ({BucketList}); reporting unknown so the router can apply its unknown policy",
                member.ModelId, modelList);
        }

        return new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            ResetAt = snapshot.ResetAt,
            Notes = notes,
            PerModel = snapshot.PerModel,
        };
    }

    /// <summary>
    /// Drops the in-process snapshot so the next
    /// <see cref="GetAvailabilityAsync"/> call refetches against the Code
    /// Assist quota endpoint. Wire to <see cref="CodeyBox.Orchestrator.CredentialFileSource.TokenUpdated"/>
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

    private const int MaxResponseChars = 64 * 1024;

    private async Task<AgentQuotaSnapshot> FetchAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, UsageEndpoint);
            // Do NOT log the Authorization header — it contains the OAuth token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Empty JSON body is required (the endpoint rejects unknown fields).
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Gemini quota endpoint returned {StatusCode}; treating quota as unknown",
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
            _log.LogDebug(ex, "Gemini quota probe failed; treating quota as unknown");
            return Unknown("network error");
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
}
