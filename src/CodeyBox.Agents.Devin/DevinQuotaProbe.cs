using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Probes Devin's Connect-RPC
/// <c>SeatManagementService.GetUserStatus</c> endpoint to estimate
/// subscription quota. Uses the <c>agent-quota</c> named HTTP client.
///
/// <para><b>Endpoint.</b> Unlike the other subscription probes, devin's API
/// base URL is NOT a compile-time constant: <c>devin auth login</c> assigns an
/// <c>api_server_url</c> per credential (stored in
/// <c>~/.local/share/devin/credentials.toml</c>) and the public
/// <c>api.devin.ai</c> host does not serve this RPC (verified 2026-09-21:
/// returns 404). The probe therefore reads the endpoint from the credential
/// (<see cref="AgentQuotaCredentials.EndpointBaseUrl"/>) and fails closed when
/// it is absent or not an absolute http(s) URL.</para>
///
/// <para><b>Shape.</b> <c>GetUserStatusResponse.user_status.plan_status</c>
/// carries (proto field names from the devin 3000.11.1 binary; protojson may
/// emit camelCase, so the parser accepts both):
/// <c>daily_quota_remaining_percent</c>,
/// <c>weekly_quota_remaining_percent</c>,
/// <c>daily_quota_reset_at_unix</c>, <c>weekly_quota_reset_at_unix</c>,
/// <c>acu_consumed</c>, <c>acu_limit</c>,
/// <c>available_prompt_credits</c>, <c>available_flow_credits</c>,
/// <c>available_flex_credits</c>, <c>overage_balance_micros</c>,
/// <c>grace_period_status</c>, <c>top_up_status</c>;
/// <c>plan_info.plan_name</c> identifies the plan.</para>
///
/// <para><b>Headline.</b> <see cref="AgentQuotaSnapshot.AvailablePct"/> is the
/// MINIMUM of the present quota windows (daily / weekly / ACU-derived), so the
/// most-constrained window wins and an exhausted weekly quota cannot hide
/// behind a fresh daily one. The top-up credit fields are deliberately NOT
/// mapped to <see cref="AgentQuotaSnapshot.BalanceRemaining"/>: the quota gate
/// treats balance-metered pools as depleting and ignores AvailablePct, which
/// is wrong here — the plan windows are the primary bucket and credits are a
/// secondary top-up pool. They are surfaced in <see cref="AgentQuotaSnapshot.Notes"/>
/// instead.</para>
///
/// <para>Thread-safe; results are cached for <c>cacheTtl</c> to avoid hammering
/// the endpoint when several work items pick up close together. Modeled on
/// <c>CursorQuotaProbe</c>.</para>
/// </summary>
public sealed class DevinQuotaProbe : IAgentQuotaProbe, IAgentQuotaCacheInvalidator
{
    /// <summary>
    /// Connect-RPC method path appended to the credential's
    /// <c>api_server_url</c>. The host is deliberately NOT baked in — it is
    /// login-assigned per credential (see class doc).
    /// </summary>
    internal const string UsageEndpointPath =
        "/exa.seat_management_pb.SeatManagementService/GetUserStatus";

    private const int MaxResponseChars = 64 * 1024; // 64 KiB
    private const int UnexpectedShapeLogCapChars = 1024;
    internal const string UnexpectedShapeNotes = "unexpected response shape";

    // Value class is `(?:[^"\\]|\\.)*` so JSON strings with escaped quotes
    // are matched in full instead of stopping at the first escape, which
    // would leave the suffix exposed in the operator log. Field-name class
    // allows hyphens too, so kebab-case keys don't silently bypass redaction.
    private static readonly Regex TokenLikeFieldPattern = new(
        @"(""[A-Za-z0-9_\-]*(?:token|key|secret|password|auth|session|cookie|bearer)[A-Za-z0-9_\-]*"")\s*:\s*""(?:[^""\\]|\\.)*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<DevinQuotaProbe> _log;

    private (string RouteKey, string AccessToken, string Endpoint, AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Devin;

    public DevinQuotaProbe(
        IHttpClientFactory httpClientFactory,
        string? token,
        string? endpointBaseUrl,
        TimeSpan cacheTtl,
        ILogger<DevinQuotaProbe> log)
        : this(httpClientFactory, () => new AgentQuotaCredentials(token, EndpointBaseUrl: endpointBaseUrl), cacheTtl, log)
    {
    }

    public DevinQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<DevinQuotaProbe> log)
        : this(httpClientFactory, _ => credentialsProvider(), cacheTtl, log)
    {
    }

    public DevinQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<DevinQuotaProbe> log)
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

        if (!TryResolveEndpoint(credentials.EndpointBaseUrl, out var endpoint))
        {
            // The credentials file carries no (or a malformed) api_server_url —
            // there is no safe default host to fall back to since api.devin.ai
            // does not serve this RPC.
            return Unknown(
                QuotaUnknownReason.NoCredential,
                "credentials.toml has no usable api_server_url");
        }

        var routeKey = member.RouteKey;

        AgentQuotaSnapshot snapshot;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } entry
                && string.Equals(entry.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.AccessToken, token, StringComparison.Ordinal)
                && string.Equals(entry.Endpoint, endpoint, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < entry.ExpiresAt)
            {
                snapshot = entry.Snapshot;
            }
            else
            {
                snapshot = await FetchAsync(token, endpoint, ct);
                _cache = (routeKey, token, endpoint, snapshot, DateTimeOffset.UtcNow + _cacheTtl);
            }
        }
        finally
        {
            _lock.Release();
        }

        return snapshot;
    }

    /// <summary>
    /// Resolves the outbound request target from the credential's
    /// <c>api_server_url</c>. The credentials file is operator-controlled but
    /// still crosses a trust boundary into an outbound request target, so the
    /// guard lives here at the sink: only absolute http(s) URIs are accepted,
    /// and the fixed RPC path is joined to the URI's base — a credentials file
    /// carrying a non-http(s) or relative value cannot redirect the probe.
    /// </summary>
    internal static bool TryResolveEndpoint(string? endpointBaseUrl, out string endpoint)
    {
        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(endpointBaseUrl))
            return false;
        if (!Uri.TryCreate(endpointBaseUrl.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;
        endpoint = new Uri(uri, UsageEndpointPath).ToString();
        return true;
    }

    /// <summary>
    /// Drops the in-process snapshot so the next
    /// <see cref="GetAvailabilityAsync"/> call refetches against the upstream
    /// status endpoint. Wire to <see cref="CodeyBox.Orchestrator.CredentialFileSource.TokenUpdated"/>
    /// so an out-of-band host credential rotation doesn't leave a stale
    /// reading pinned for the full cache TTL.
    /// </summary>
    public void InvalidateCache()
    {
        _lock.Wait();
        try { _cache = null; }
        finally { _lock.Release(); }
    }

    private async Task<AgentQuotaSnapshot> FetchAsync(string token, string endpoint, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Devin user-status endpoint returned {StatusCode}; treating quota as unknown",
                    (int)response.StatusCode);
                return Unknown(QuotaUnknownReasons.FromHttpStatus(response.StatusCode), $"HTTP {(int)response.StatusCode}");
            }

            var body = await ReadCappedAsync(response.Content, ct);
            if (body is null) return Unknown(QuotaUnknownReason.Permanent, "response too large");
            var snapshot = ParseResponse(body);

            // Log raw body when the parser bailed to Unknown — silent
            // fallthrough is what makes shape drift invisible. Capped and
            // token-redacted so bearer-shaped strings never reach operator
            // logs.
            if (string.Equals(snapshot.Notes, UnexpectedShapeNotes, StringComparison.Ordinal))
            {
                _log.LogDebug(
                    "Devin quota probe: unexpected response shape; raw body (redacted, capped to {Cap} chars): {Body}",
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
            _log.LogDebug("Devin quota probe timed out; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "request timeout");
        }
        catch (HttpRequestException ex)
        {
            _log.LogDebug(ex, "Devin quota probe HTTP error; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "HTTP error");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Devin quota probe failed; treating quota as unknown");
            return Unknown(QuotaUnknownReason.Transient, "unexpected error");
        }
    }

    internal static AgentQuotaSnapshot ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unknown(QuotaUnknownReason.Permanent, UnexpectedShapeNotes);

            // Response is {user_status|userStatus: {plan_status|planStatus:
            // {...}, plan_info|planInfo: {...}}}; tolerate the status object
            // landing at the top level for flatter serialisations.
            if (!TryGetObject(root, "user_status", out var userStatus)
                && !TryGetObject(root, "userStatus", out userStatus))
            {
                userStatus = root;
            }
            if (!TryGetObject(userStatus, "plan_status", out var planStatus)
                && !TryGetObject(userStatus, "planStatus", out planStatus))
            {
                planStatus = userStatus;
            }

            var windows = new List<WindowQuota>();

            var dailyPct = TryGetPercent(planStatus, "daily_quota_remaining_percent", "dailyQuotaRemainingPercent");
            var dailyReset = TryGetEpoch(planStatus, "daily_quota_reset_at_unix", "dailyQuotaResetAtUnix");
            if (dailyPct.HasValue)
            {
                windows.Add(new WindowQuota
                {
                    Name = "daily",
                    AvailablePct = ClampAvailable(dailyPct.Value),
                    ResetAt = EpochToDateTime(dailyReset),
                    UsedPercent = ClampAvailable(100.0 - dailyPct.Value),
                    ResetAtEpochSeconds = dailyReset,
                });
            }

            var weeklyPct = TryGetPercent(planStatus, "weekly_quota_remaining_percent", "weeklyQuotaRemainingPercent");
            var weeklyReset = TryGetEpoch(planStatus, "weekly_quota_reset_at_unix", "weeklyQuotaResetAtUnix");
            if (weeklyPct.HasValue)
            {
                windows.Add(new WindowQuota
                {
                    Name = "weekly",
                    AvailablePct = ClampAvailable(weeklyPct.Value),
                    ResetAt = EpochToDateTime(weeklyReset),
                    UsedPercent = ClampAvailable(100.0 - weeklyPct.Value),
                    ResetAtEpochSeconds = weeklyReset,
                });
            }

            // When the percent fields are absent but the ACU counters are
            // present, derive the remaining share from acu_consumed/acu_limit
            // rather than degrading to unknown.
            var acuConsumed = TryGetDouble(planStatus, "acu_consumed", "acuConsumed");
            var acuLimit = TryGetDouble(planStatus, "acu_limit", "acuLimit");
            if (acuConsumed.HasValue && acuLimit is > 0)
            {
                var acuRemaining = ClampAvailable(100.0 * (1.0 - acuConsumed.Value / acuLimit.Value));
                windows.Add(new WindowQuota
                {
                    Name = "acu",
                    AvailablePct = acuRemaining,
                    UsedPercent = ClampAvailable(100.0 - acuRemaining),
                });
            }

            if (windows.Count == 0)
                return Unknown(QuotaUnknownReason.Permanent, UnexpectedShapeNotes);

            var binding = windows.OrderBy(w => w.AvailablePct).First();
            var planName = TryGetObject(userStatus, "plan_info", out var planInfo)
                || TryGetObject(userStatus, "planInfo", out planInfo)
                    ? TryGetString(planInfo, "plan_name") ?? TryGetString(planInfo, "planName")
                    : TryGetString(userStatus, "plan_name") ?? TryGetString(userStatus, "planName");

            var credits = new List<string>(4);
            AppendCredit(credits, "prompt", TryGetDouble(planStatus, "available_prompt_credits", "availablePromptCredits"));
            AppendCredit(credits, "flow", TryGetDouble(planStatus, "available_flow_credits", "availableFlowCredits"));
            AppendCredit(credits, "flex", TryGetDouble(planStatus, "available_flex_credits", "availableFlexCredits"));

            var notes = planName is null && credits.Count == 0
                ? null
                : string.Join(
                    "; ",
                    (planName is null ? Enumerable.Empty<string>() : new[] { $"plan {planName}" })
                        .Concat(credits.Count == 0 ? Enumerable.Empty<string>() : new[] { $"top-up credits: {string.Join(", ", credits)}" }));

            return new AgentQuotaSnapshot
            {
                AvailablePct = binding.AvailablePct,
                ResetAt = binding.ResetAt,
                Notes = notes,
                Windows = windows,
            };
        }
        catch (JsonException)
        {
            return Unknown(QuotaUnknownReason.Permanent, "invalid JSON");
        }
    }

    private static void AppendCredit(List<string> sink, string name, double? value)
    {
        if (value.HasValue)
            sink.Add($"{name}={value.Value:0.##}");
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static double? TryGetPercent(JsonElement element, params string[] names)
    {
        var value = TryGetDouble(element, names);
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            return null;
        return value;
    }

    private static double? TryGetDouble(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
                return d;
            // protojson serialises int64 fields as strings — accept them.
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static long? TryGetEpoch(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l))
                return l;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static DateTimeOffset? EpochToDateTime(long? epochSeconds)
    {
        if (epochSeconds is not { } seconds || seconds <= 0)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static double ClampAvailable(double value) =>
        Math.Clamp(value, 0.0, 100.0);

    private static AgentQuotaSnapshot Unknown(QuotaUnknownReason reason, string notes) =>
        AgentQuotaSnapshot.UnknownSnapshot(reason, notes);

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        var raw = await content.ReadAsStringAsync(ct);
        return raw.Length <= MaxResponseChars ? raw : null;
    }

    private static string RedactAndCap(string body, int cap)
    {
        var redacted = TokenLikeFieldPattern.Replace(body, "$1:\"[REDACTED]\"");
        return redacted.Length <= cap ? redacted : redacted[..cap] + "…";
    }
}
