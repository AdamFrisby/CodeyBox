using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

/// <summary>
/// Probes the Antigravity (<c>agy</c>) gateway for availability. The CLI exposes
/// <em>no</em> usage/quota/whoami command, so the probe must INFER availability
/// from gateway state.
///
/// <para><b>Surface (verified 2026-06-10, agy 1.0.7).</b> The <c>agy</c> binary
/// itself talks to <c>daily-cloudcode-pa.googleapis.com/v1internal</c> (NOT the
/// <c>cloudcode-pa</c> host the Gemini Code Assist probes use). On that host,
/// for our Sign-in-with-Google credential:
/// <list type="bullet">
///   <item><description><c>:loadCodeAssist</c> with
///   <c>{"metadata":{"pluginType":"GEMINI"}}</c> → <b>200</b>, returning the
///   account's tier (<c>currentTier</c>/<c>paidTier</c>, e.g.
///   <c>g1-pro-tier</c> = Google One AI Pro). Costs no generation — it is a
///   pure authorization/tier read.</description></item>
///   <item><description><c>:retrieveUserQuotaSummary</c> and
///   <c>:retrieveUserQuota</c> → <b>403 PERMISSION_DENIED</b> for every body
///   shape. There is NO readable per-model quota meter on this surface, so the
///   probe cannot report a real remaining-fraction.</description></item>
///   <item><description><c>:fetchAvailableModels</c> → <b>403</b>; the gateway
///   model list comes from <see cref="AntigravityKnownModels"/>, not a live
///   read.</description></item>
/// </list></para>
///
/// <para><b>Design.</b> Because no quota-number endpoint is reachable, the probe
/// is an <em>authorization/liveness</em> signal: a 200 from <c>:loadCodeAssist</c>
/// means the credential is valid and the subscription is active ⇒ dispatchable
/// (reported as 100% available). A 429 surfaces the gateway reset so a weekly
/// lockout parks the member. Any other status is Unknown (the router's
/// QuotaUnknownPolicy decides). We deliberately do NOT issue a live
/// <c>:generateContent</c> ping for routine probing — that would burn a request
/// from the very (weekly-capped) quota we are trying to preserve.</para>
///
/// <para><b>Per-model 429 gating.</b> Antigravity meters each gateway model
/// (gemini-3.5-flash-high, claude-opus-4-6-thinking, gpt-oss-120b-medium, …) on
/// its own bucket, keyed by the router as <c>(AgentKind, ModelId)</c>. Since the
/// buckets are not readable up front, per-model exhaustion is learned reactively:
/// the runner calls <see cref="MarkExhaustedAsync"/> on a real 429 (with the
/// gateway's reset / <c>lockout_until</c>), and that synthetic 0% override gates
/// subsequent picks of that model until the reset moment.</para>
///
/// <para><b>7-day lockout handling.</b> AI Pro caps weekly with up to a 7-day
/// lockout on cap breach. When a 429 carries an absolute reset (Retry-After date
/// or the structured <c>quota_metadata.lockout_until</c> the failure detector
/// reads), the probe surfaces that exact moment so failed items park cleanly in
/// <c>WaitingForQuotaReset</c> instead of churning.</para>
/// </summary>
public sealed class AntigravityQuotaProbe : IAgentQuotaProbe
{
    /// <summary>
    /// The <c>agy</c> gateway host. The <c>daily-</c> prefix is what agy 1.0.7
    /// ships; kept as a single constant so a future build that moves hosts is a
    /// one-line change shared by every RPC below.
    /// </summary>
    internal const string GatewayBase = "https://daily-cloudcode-pa.googleapis.com/v1internal";

    /// <summary>
    /// Authorization/tier read. 200 ⇒ credential valid + subscription active.
    /// The only gateway RPC that answers for our credential without spending
    /// quota; the <c>:retrieveUserQuota*</c> meters return 403.
    /// </summary>
    internal const string LoadCodeAssistEndpoint = GatewayBase + ":loadCodeAssist";

    /// <summary>
    /// Request body for <see cref="LoadCodeAssistEndpoint"/>. <c>pluginType</c>
    /// must be <c>GEMINI</c> — the proto on this host rejects <c>ANTIGRAVITY</c>
    /// (400 "Invalid value at 'metadata.plugin_type'"); GEMINI returns the same
    /// account/tier the agy credential is backed by.
    /// </summary>
    private const string LoadCodeAssistBody = "{\"metadata\":{\"pluginType\":\"GEMINI\"}}";

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
            return Unknown(QuotaUnknownReason.NoCredential, "no token configured");
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

            snapshot = await ProbeAuthorizationAsync(token, modelKey, ct).ConfigureAwait(false);
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
    /// Reads <c>:loadCodeAssist</c> as the authorization/liveness signal.
    /// <list type="bullet">
    ///   <item><description>200 ⇒ credential valid + subscription active ⇒
    ///   available (100%). When a <paramref name="modelId"/> is supplied the
    ///   reading is mirrored into <c>PerModel</c> so the router's per-model key
    ///   has an entry.</description></item>
    ///   <item><description>429 ⇒ rate-limited / weekly lockout; surfaces the
    ///   gateway reset.</description></item>
    ///   <item><description>anything else (401/403/5xx/transport) ⇒ Unknown; the
    ///   router's QuotaUnknownPolicy decides.</description></item>
    /// </list>
    /// </summary>
    internal async Task<AgentQuotaSnapshot> ProbeAuthorizationAsync(string token, string modelId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(LoadCodeAssistBody, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = TryParseRetryAfter(response, _timeProvider.GetUtcNow())
                    ?? await TryParseStructuredResetAsync(response, ct).ConfigureAwait(false);
                var perModel0 = string.IsNullOrEmpty(modelId)
                    ? new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                    {
                        [modelId] = new ModelQuota { AvailablePct = 0.0, ResetAt = reset, Window = "REQUESTS" },
                    };
                return new AgentQuotaSnapshot
                {
                    AvailablePct = 0.0,
                    ResetAt = reset,
                    Notes = "loadCodeAssist: rate-limited",
                    PerModel = perModel0,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Antigravity loadCodeAssist returned {StatusCode}", (int)response.StatusCode);
                return Unknown(QuotaUnknownReasons.FromHttpStatus(response.StatusCode), $"loadCodeAssist: HTTP {(int)response.StatusCode}");
            }

            var body = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            var tier = body is null ? null : ParseTier(body);
            var note = tier is null ? "loadCodeAssist: authorized" : $"loadCodeAssist: authorized (tier={tier})";
            var perModel = string.IsNullOrEmpty(modelId)
                ? new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ModelQuota>(StringComparer.OrdinalIgnoreCase)
                {
                    [modelId] = new ModelQuota { AvailablePct = 100.0, ResetAt = null, Window = "REQUESTS" },
                };
            return new AgentQuotaSnapshot
            {
                AvailablePct = 100.0,
                ResetAt = null,
                Notes = note,
                PerModel = perModel,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Antigravity loadCodeAssist probe failed");
            return Unknown(QuotaUnknownReason.Transient, "loadCodeAssist: transient error");
        }
    }

    /// <summary>
    /// Extracts a human-readable tier label from a <c>:loadCodeAssist</c> 200
    /// body for the snapshot Notes. Prefers <c>paidTier.id</c> (e.g.
    /// <c>g1-pro-tier</c>) then <c>currentTier.id</c>. Display-only; never gates.
    /// </summary>
    internal static string? ParseTier(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return TierId(root, "paidTier") ?? TierId(root, "currentTier");
        }
        catch (JsonException)
        {
            return null;
        }

        static string? TierId(JsonElement root, string name) =>
            root.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object
                ? TryGetString(t, "id")
                : null;
    }

    private static DateTimeOffset? TryParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return now + delta;
        if (ra.Date is { } when) return when;
        return null;
    }

    private async Task<DateTimeOffset?> TryParseStructuredResetAsync(HttpResponseMessage response, CancellationToken ct)
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

    private static string? TryGetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static AgentQuotaSnapshot Unknown(QuotaUnknownReason reason, string notes) =>
        AgentQuotaSnapshot.UnknownSnapshot(reason, notes);

    private sealed record CacheEntry(AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt);
    private sealed record ExhaustionOverride(DateTimeOffset ExpiresAt, DateTimeOffset? ResetAt);
}
