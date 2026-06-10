using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <see cref="AgentQuotaSnapshot.AvailablePct"/> is computed from the
/// percent-used dimensions on <c>planUsage</c>:
/// <code>
/// availablePct = 100 - max(totalPercentUsed, autoPercentUsed, apiPercentUsed)
/// </code>
/// We take the MAX so the most-constrained dimension wins; the router floor
/// then gates cursor below <c>minQuotaPct</c>. The earlier
/// (<c>a803b459</c>) "headline must be <c>remaining/limit</c>, not
/// <c>totalPercentUsed</c>" decision was written against an ASSUMED shape: the
/// live response has NO <c>planUsage.remaining</c> field, so the old parser
/// always returned the <c>Unknown("unexpected response shape")</c> sentinel
/// and the router could never gate Cursor by quota. The probe was
/// unauthenticated until 2026-06-04 — this is the first time the endpoint
/// actually returned data, which is why the shape mismatch went undetected
/// for so long. Captured live 2026-06-04 (account out of usage):
/// <code>
/// "planUsage": {
///   "totalSpend": 19903, "includedSpend": 2000, "bonusSpend": 17903,
///   "limit": 2000, "remainingBonus": false,
///   "autoPercentUsed": 100, "apiPercentUsed": 100, "totalPercentUsed": 100
/// }
/// </code>
/// — no <c>remaining</c>; the percent-used fields ARE the headline.</para>
///
/// <para>Explicit out-of-usage signals override the percent-derived headline
/// to a hard 0%, so the router gates cursor even if a percent field is missing
/// or partial: <c>remainingBonus == false &amp;&amp; totalSpend &gt;= limit</c>,
/// <c>displayMessage</c> matching <c>/hit your .*usage limit/i</c>, or
/// <c>enabled == false</c>. Any one of these is sufficient.</para>
///
/// <para><c>resetAt</c> = <c>billingCycleEnd</c>, which is an epoch
/// MILLISECONDS string (NOT seconds, NOT a number). The cycle is ~31 days /
/// monthly, not weekly.</para>
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
    private const int UnexpectedShapeLogCapChars = 1024;

    // Single source of truth for the "fell through to Unknown" sentinel.
    // FetchAsync uses it to decide whether to log the raw response body for
    // diagnosis; ParseResponse returns it. Keeping them in lockstep prevents
    // the silent-fallthrough regression this probe was rewritten to avoid.
    internal const string UnexpectedShapeNotes = "unexpected response shape";

    private static readonly Regex OutOfUsageDisplayMessagePattern = new(
        @"hit your .*usage limit",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Value class is `(?:[^"\\]|\\.)*` so JSON strings with escaped quotes
    // (e.g. "sessionToken":"abc\"def") are matched in full instead of stopping
    // at the first escape, which would leave the suffix exposed in the
    // operator log. Field-name class allows hyphens too, so kebab-case keys
    // (e.g. "access-token") don't silently bypass redaction.
    private static readonly Regex TokenLikeFieldPattern = new(
        @"(""[A-Za-z0-9_\-]*(?:token|key|secret|password|auth|session|cookie|bearer)[A-Za-z0-9_\-]*"")\s*:\s*""(?:[^""\\]|\\.)*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            return Unknown(QuotaUnknownReason.NoCredential, "no token configured");
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
            Unknown = QuotaUnknownReason.Permanent,
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
                return Unknown(QuotaUnknownReasons.FromHttpStatus(response.StatusCode), $"HTTP {(int)response.StatusCode}");
            }

            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null) return Unknown(QuotaUnknownReason.Permanent, "response too large");
            var snapshot = ParseResponse(body);

            // Log raw body when the parser bailed to Unknown — silent fallthrough
            // is what made the prior shape-mismatch invisible for weeks. Capped
            // and token-redacted so we don't leak bearer-shaped strings into
            // operator logs.
            if (string.Equals(snapshot.Notes, UnexpectedShapeNotes, StringComparison.Ordinal))
            {
                _log.LogDebug(
                    "Cursor quota probe: unexpected response shape; raw body (redacted, capped to {Cap} chars): {Body}",
                    UnexpectedShapeLogCapChars,
                    RedactAndCap(body, UnexpectedShapeLogCapChars));
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _log.LogDebug("Cursor quota probe timed out; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "request timeout");
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "Cursor quota probe HTTP error; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "HTTP error");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cursor quota probe failed; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "unexpected error");
        }
    }

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("planUsage", out var planUsage) ||
                planUsage.ValueKind != JsonValueKind.Object)
            {
                return Unknown(QuotaUnknownReason.Permanent, UnexpectedShapeNotes);
            }

            var hasTotal = TryGetDoubleProperty(planUsage, "totalPercentUsed", out var totalUsed);
            var hasAuto = TryGetDoubleProperty(planUsage, "autoPercentUsed", out var autoUsed);
            var hasApi = TryGetDoubleProperty(planUsage, "apiPercentUsed", out var apiUsed);

            if (!hasTotal && !hasAuto && !hasApi)
                return Unknown(QuotaUnknownReason.Permanent, UnexpectedShapeNotes);

            // Most-constrained percent dimension wins. The real shape can carry
            // total/auto/api at different fractions (e.g. auto fully used,
            // total partially used) — taking the max keeps cursor gated when
            // any single dimension is exhausted.
            var maxUsed = 0.0;
            if (hasTotal) maxUsed = Math.Max(maxUsed, totalUsed);
            if (hasAuto) maxUsed = Math.Max(maxUsed, autoUsed);
            if (hasApi) maxUsed = Math.Max(maxUsed, apiUsed);
            var availablePct = ClampAvailable(100.0 - maxUsed);

            // Hard 0% override on explicit out-of-usage signals — guards against
            // a partial response where the spend math says we're exhausted but
            // a percent field is missing or out-of-date.
            if (IsExplicitlyOutOfUsage(root, planUsage))
                availablePct = 0.0;

            var resetAt = TryGetBillingCycleEnd(root);
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
            return Unknown(QuotaUnknownReason.Permanent, "invalid JSON");
        }
    }

    private static bool IsExplicitlyOutOfUsage(JsonElement root, JsonElement planUsage)
    {
        // Bonus exhausted AND spend has eaten through the limit.
        if (planUsage.TryGetProperty("remainingBonus", out var remainingBonus) &&
            remainingBonus.ValueKind == JsonValueKind.False &&
            TryGetDoubleProperty(planUsage, "totalSpend", out var totalSpend) &&
            TryGetDoubleProperty(planUsage, "limit", out var limit) &&
            limit > 0 && totalSpend >= limit)
        {
            return true;
        }

        // Dashboard surfaces "You've hit your usage limit" verbatim when the
        // account is out — match leniently so cosmetic copy edits don't drop
        // the signal.
        if (root.TryGetProperty("displayMessage", out var msg) &&
            msg.ValueKind == JsonValueKind.String)
        {
            var text = msg.GetString();
            if (!string.IsNullOrEmpty(text) && OutOfUsageDisplayMessagePattern.IsMatch(text))
                return true;
        }

        // The dashboard surfaces a paused subscription as enabled=false.
        if (root.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind == JsonValueKind.False)
        {
            return true;
        }

        return false;
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
            JsonValueKind.String => double.TryParse(
                el.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
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

    /// <summary>
    /// Strips values for any key whose name contains a token-shaped substring
    /// (token, key, secret, password, auth, session, cookie, bearer) and
    /// truncates the result to <paramref name="maxLen"/> characters. Used only
    /// for the unexpected-shape Debug log so a bearer or session id can't slip
    /// into operator logs if the response shape ever drifts to include one.
    /// </summary>
    internal static string RedactAndCap(string body, int maxLen)
    {
        var redacted = TokenLikeFieldPattern.Replace(body, "$1:\"<redacted>\"");
        if (redacted.Length <= maxLen) return redacted;
        return redacted.Substring(0, maxLen) + "…[truncated]";
    }

    private static double ClampAvailable(double pct) => Math.Clamp(pct, 0.0, 100.0);

    private static AgentQuotaSnapshot Unknown(QuotaUnknownReason reason, string notes) =>
        AgentQuotaSnapshot.UnknownSnapshot(reason, notes);
}
