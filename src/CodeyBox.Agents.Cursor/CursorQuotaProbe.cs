using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

/// <summary>
/// Probes Cursor's Connect-RPC <c>DashboardService.GetCurrentPeriodUsage</c>
/// endpoint to estimate subscription quota. Uses the <c>agent-quota</c> named
/// HTTP client.
///
/// Any network error, unexpected status code, or unrecognised response shape
/// returns <see cref="AgentQuotaSnapshot.AvailablePct"/> = -1; the router's
/// unknown policy decides whether that blocks pickup.
///
/// <para>Modeled on <c>CodexQuotaProbe</c> (caches per-token, dedupes
/// missing-model warnings, invalidates on file source <c>TokenUpdated</c>).</para>
///
/// <para><b>HEADLINE-METRIC.</b> The headline
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> is computed from spend-vs-limit,
/// NOT from <c>totalPercentUsed</c>:
/// <code>
/// availablePct = (planUsage.remaining / planUsage.limit) * 100
///             // equivalent to: 100 - (planUsage.totalSpend / planUsage.limit * 100)
/// </code>
/// <c>planUsage.limit == 0</c> (or a response missing <c>remaining</c>/<c>limit</c>)
/// returns the -1 "unknown" sentinel. The <c>totalPercentUsed</c> /
/// <c>autoPercentUsed</c> / <c>apiPercentUsed</c> fields are normalised against a
/// much larger denominator (likely including usage-based-billing headroom) and DO
/// NOT match what the Cursor web UI shows the operator. Captured live response:
/// <c>totalSpend=1313, limit=2000, remaining=687, totalPercentUsed=6.73</c>;
/// the same response's <c>displayMessage</c> reads
/// "You've used 66% of your included usage" — i.e. 1313/2000 = 65.65%, NOT
/// 6.73%. Picking <c>totalPercentUsed</c> would disagree with the UI by ~60
/// points and keep dispatching Cursor when it is near cap.</para>
///
/// <para>Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together.</para>
/// </summary>
public sealed class CursorQuotaProbe : IAgentQuotaProbe
{
    internal const string UsageEndpoint =
        "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage";

    /// <summary>Default routed Cursor model in agent-class configs.</summary>
    internal const string DefaultRoutedModelId = "composer-2.5";

    private const int MaxResponseChars = 64 * 1024; // 64 KiB

    private static readonly string[] FallbackAutoBucketModels =
    [
        "default",
        DefaultRoutedModelId,
        "composer-1.5",
        "composer-2",
        "composer-2.5-fast",
        "composer-3-preview",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CursorQuotaProbe> _log;

    private (string RouteKey, string AccessToken, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly HashSet<(string Token, string ModelId)> _loggedMissingModels = new();
    private readonly object _loggedMissingModelsLock = new();

    public AgentKind Kind => AgentKind.Cursor;

    public CursorQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        TimeSpan cacheTtl,
        ILogger<CursorQuotaProbe> log)
        : this(httpClientFactory, () => new AgentQuotaCredentials(token), cacheTtl, log)
    {
    }

    public CursorQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<CursorQuotaProbe> log)
        : this(httpClientFactory, _ => credentialsProvider(), cacheTtl, log)
    {
    }

    public CursorQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<CursorQuotaProbe> log)
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
            return Unknown("no token configured");
        var routeKey = member.RouteKey;

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry
                && string.Equals(entry.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.AccessToken, token, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                snapshot = entry.Snapshot;
            }
            else
            {
                snapshot = await FetchAsync(token, ct);
                _cache = (routeKey, token, snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            }
        }
        finally
        {
            _lock.Release();
        }

        return ApplyMemberGate(snapshot, member, token);
    }

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
                "Cursor quota probe: configured model {ModelId} not in response buckets ({BucketList}); reporting unknown so the router can apply its unknown policy",
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
    /// <see cref="GetAvailabilityAsync"/> call refetches against the upstream
    /// usage endpoint. Wire to <see cref="CodeyBox.Orchestrator.CredentialFileSource.TokenUpdated"/>
    /// so an out-of-band host token rotation doesn't leave a stale 401 pinned
    /// for the full cache TTL.
    /// </summary>
    public void InvalidateCache()
    {
        _lock.Wait();
        try { _cache = null; }
        finally { _lock.Release(); }
    }

    private async Task<AgentQuotaSnapshot> FetchAsync(string token, CancellationToken ct)
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
                _log.LogDebug("Cursor usage endpoint returned {StatusCode}; treating quota as unknown",
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
        catch (TaskCanceledException)
        {
            _log.LogDebug("Cursor quota probe timed out; treating quota as unknown");
            return Unknown("request timeout");
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "Cursor quota probe HTTP error; treating quota as unknown");
            return Unknown("HTTP error");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cursor quota probe failed; treating quota as unknown");
            return Unknown("unexpected error");
        }
    }

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("planUsage", out var planUsage) ||
                planUsage.ValueKind != JsonValueKind.Object ||
                !TryGetDoubleProperty(planUsage, "remaining", out var remaining) ||
                !TryGetDoubleProperty(planUsage, "limit", out var limit))
            {
                return Unknown("unexpected response shape");
            }

            // Headline = remaining/limit (spend-vs-limit), NOT totalPercentUsed —
            // see class remarks for the Cursor-UI-disagreement rationale.
            if (limit <= 0)
                return Unknown("planUsage.limit is zero/absent");

            var resetAt = TryGetBillingCycleEnd(root);
            var availablePct = ClampAvailable(remaining / limit * 100.0);
            var perModel = ParsePerModel(root, planUsage, resetAt);
            CapPerModelByOverall(perModel, availablePct, resetAt);

            return new AgentQuotaSnapshot
            {
                AvailablePct = availablePct,
                ResetAt = resetAt,
                PerModel = perModel,
            };
        }
        catch (JsonException)
        {
            return Unknown("invalid JSON");
        }
    }

    private static Dictionary<string, ModelQuota> ParsePerModel(
        JsonElement root,
        JsonElement planUsage,
        DateTimeOffset? resetAt)
    {
        var perModel = new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase);

        var hasAuto = TryGetDoubleProperty(planUsage, "autoPercentUsed", out var autoUsed);
        var hasApi = TryGetDoubleProperty(planUsage, "apiPercentUsed", out _);
        if (!hasAuto && !hasApi)
            return perModel;

        if (hasAuto)
        {
            var autoQuota = new ModelQuota
            {
                AvailablePct = ClampAvailable(100.0 - autoUsed),
                ResetAt = resetAt,
                Window = "auto",
            };

            foreach (var modelId in ParseAutoBucketModels(root))
                perModel[modelId] = autoQuota;
        }

        // apiPercentUsed is aggregate-only in the dashboard response; there is
        // no per-model id list for the API bucket, so we do not synthesize keys.

        return perModel;
    }

    private static void CapPerModelByOverall(
        Dictionary<string, ModelQuota> perModel,
        double overallAvailablePct,
        DateTimeOffset? resetAt)
    {
        if (perModel.Count == 0) return;

        foreach (var key in perModel.Keys.ToList())
        {
            var v = perModel[key];
            if (v.AvailablePct <= overallAvailablePct) continue;

            perModel[key] = new ModelQuota
            {
                AvailablePct = overallAvailablePct,
                ResetAt = resetAt ?? v.ResetAt,
                Window = $"{v.Window} (capped by overall)",
                Windows = v.Windows,
            };
        }
    }

    private static IEnumerable<string> ParseAutoBucketModels(JsonElement root)
    {
        if (root.TryGetProperty("autoBucketModels", out var models) &&
            models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var id = item.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }

            yield break;
        }

        foreach (var modelId in FallbackAutoBucketModels)
            yield return modelId;
    }

    private static DateTimeOffset? TryGetBillingCycleEnd(JsonElement root)
    {
        if (!root.TryGetProperty("billingCycleEnd", out var end))
            return null;
        return TryGetUnixTimeMilliseconds(end);
    }

    private static DateTimeOffset? TryGetUnixTimeMilliseconds(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var ms) =>
                DateTimeOffset.FromUnixTimeMilliseconds(ms),
            JsonValueKind.String when long.TryParse(el.GetString(), out var ms) =>
                DateTimeOffset.FromUnixTimeMilliseconds(ms),
            _ => null,
        };

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

    private static double ClampAvailable(double pct) => Math.Clamp(pct, 0.0, 100.0);

    private static AgentQuotaSnapshot Unknown(string reason) =>
        new() { AvailablePct = -1, Notes = reason };
}
