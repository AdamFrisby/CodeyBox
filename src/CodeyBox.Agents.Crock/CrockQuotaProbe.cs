using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Quota / liveness probe for the <c>crock</c> agent. CrockCode submits work
/// to Anthropic's Message Batches API using a pay-per-token API key (with the
/// documented ~50% batch discount applied at billing time by Anthropic). The
/// Anthropic platform exposes <em>no</em> per-API-key remaining-credit endpoint
/// for raw API-key billing — only org-scoped <c>usage_report</c> /
/// <c>cost_report</c> behind an Admin API token, which CodeyBox does not have
/// access to from a workspace key.
///
/// <para><b>What this probe does.</b> It hits the cheapest authenticated
/// Anthropic endpoint that validates the API key — <c>GET /v1/models</c>,
/// which costs zero tokens and returns 200 only when the key is live. Result
/// shapes:</para>
/// <list type="bullet">
///   <item><description><b>200</b> ⇒ credential live ⇒ <c>AvailablePct=100</c>,
///   notes carry the pricing model so operators see why "100%" appears for a
///   pay-per-token agent (the real spend gate lives in
///   <see cref="IAgentBudgetProvider"/>; the router takes
///   <c>MIN(probe, budget)</c>).</description></item>
///   <item><description><b>429</b> ⇒ rate-limited; surfaces
///   <c>Retry-After</c> as <c>ResetAt</c> so failed items park cleanly in
///   <see cref="WorkItemState.WaitingForQuotaReset"/>.</description></item>
///   <item><description><b>401/403</b> ⇒
///   <see cref="QuotaUnknownReason.Permanent"/> with notes; the router's
///   unknown policy decides whether to fail closed or fall back to observed
///   failures.</description></item>
///   <item><description><b>5xx / network / timeout</b> ⇒
///   <see cref="QuotaUnknownReason.Transient"/> so a one-off blip does not
///   permanently disable the member.</description></item>
/// </list>
///
/// <para><b>Why not call <c>:generateContent</c> or submit a probe batch?</b>
/// Because batch lifetime is minutes-to-hours and any probe submission would
/// either burn tokens (against a real key the operator pays for) or sit
/// pending until the batch resolves — both are unacceptable for a routine
/// availability check. The list-models endpoint is the canonical zero-cost
/// auth check.</para>
///
/// <para><b>Reactive 429 gating (planned).</b>
/// <see cref="MarkExhaustedAsync"/> is the hook the pipeline calls when a
/// real dispatch returns 429 so the next pickup of the same member is gated
/// immediately, without waiting for the next periodic probe. Wiring is
/// driven by the pipeline's <c>CompositeQuotaFailureClassifier</c> +
/// <c>TerminalQuotaError</c> path; today no <c>IAgentQuotaFailureDetector</c>
/// is registered for <see cref="AgentKind.Crock"/>, so the override fires
/// only when an operator drives it directly (and via dedicated unit tests).
/// Adding a <c>CrockQuotaFailureDetector</c> mirroring
/// <c>ClaudeQuotaFailureDetector</c> (CrockCode rides Anthropic's
/// <c>/v1/messages</c> wire shapes) is the follow-up.</para>
///
/// <para><b>Subscription OAuth NEVER reaches Anthropic via this path.</b> The
/// caller supplies <see cref="AgentQuotaCredentials.AccessToken"/> derived
/// from the configured CrockCode API key (extracted from the JSON config the
/// host ships through <c>CODEYBOX_CROCK_CONFIG_JSON</c>). The factory wiring
/// in <c>Program.cs</c> does NOT fall back to a subscription OAuth token —
/// this path uses the CrockCode API key only, per the contract.</para>
/// </summary>
public sealed class CrockQuotaProbe : IAgentQuotaProbe
{
    /// <summary>
    /// Anthropic "list models" endpoint — token-free, returns 200 only when
    /// the API key is valid. The lowest-cost probe surface for a pay-per-token
    /// key; <c>:generateContent</c> would burn tokens and the batches API
    /// would queue a no-op submission for hours.
    /// </summary>
    internal const string ListModelsEndpoint = "https://api.anthropic.com/v1/models?limit=1";

    /// <summary>
    /// Anthropic pins the wire schema by date header rather than a media-type
    /// version. Same pin every other Anthropic client uses.
    /// </summary>
    internal const string AnthropicVersion = "2023-06-01";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<AgentMembership, AgentQuotaCredentials> _credentialsProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<CrockQuotaProbe> _log;
    private readonly TimeProvider _timeProvider;

    // Cache keyed by (route key, token). Tokens are paragmatic anti-collision
    // material — two crock members on the same route but different API keys
    // must not share a cache entry.
    private readonly Dictionary<(string RouteKey, string Token), CacheEntry> _cache = new();
    private readonly Dictionary<(string RouteKey, string Token), ExhaustionOverride> _exhausted = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AgentKind Kind => AgentKind.Crock;

    /// <summary>
    /// Parameter-less constructor preserved for the scaffold's DI registration
    /// shape (Unknown-only fallback). The router treats this as Unknown /
    /// Permanent because no credentials and no HTTP client are supplied;
    /// operators wiring real probing use the full constructor below.
    /// </summary>
    public CrockQuotaProbe()
        : this(httpClientFactory: null!,
               credentialsProvider: _ => new AgentQuotaCredentials(null),
               cacheTtl: TimeSpan.FromMinutes(1),
               log: null!,
               timeProvider: null)
    {
    }

    public CrockQuotaProbe(
        IHttpClientFactory httpClientFactory,
        Func<AgentMembership, AgentQuotaCredentials> credentialsProvider,
        TimeSpan cacheTtl,
        ILogger<CrockQuotaProbe> log,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsProvider = credentialsProvider;
        _cacheTtl = cacheTtl <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : cacheTtl;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        var credentials = _credentialsProvider(member);
        var token = credentials.AccessToken;
        if (string.IsNullOrEmpty(token))
            return AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.NoCredential, "no crock API key configured");

        if (_httpClientFactory is null || _log is null)
        {
            // Scaffold default-constructor path: no HTTP client, no logger.
            // Still surfaces Unknown/Permanent so the router's unknown policy
            // gates dispatch — but identifies the configuration gap clearly.
            return AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Permanent,
                "crock probe not wired (no HTTP client / logger)");
        }

        var routeKey = member.RouteKey;
        var cacheKey = (routeKey, token);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_exhausted.TryGetValue(cacheKey, out var ex) && ex.ExpiresAt > now)
            {
                return new AgentQuotaSnapshot
                {
                    AvailablePct = 0.0,
                    ResetAt = ex.ResetAt ?? ex.ExpiresAt,
                    Notes = "exhausted (runtime 429 hint)",
                };
            }
            _exhausted.Remove(cacheKey);

            if (_cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt > now)
                return entry.Snapshot;

            var snapshot = await ProbeListModelsAsync(token, ct).ConfigureAwait(false);
            _cache[cacheKey] = new CacheEntry(snapshot, now + _cacheTtl);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
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
            // Cap the lockout window at the provider-supplied reset when it
            // is sooner than the TTL — a runtime hint shouldn't push the
            // parking window past the actual reset moment.
            var expiry = now + (ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(1));
            if (resetAt is { } r && r > now && r < expiry)
                expiry = r;
            _exhausted[(member.RouteKey, token)] = new ExhaustionOverride(expiry, resetAt);
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

    internal async Task<AgentQuotaSnapshot> ProbeListModelsAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("agent-quota");
            using var request = new HttpRequestMessage(HttpMethod.Get, ListModelsEndpoint);
            request.Headers.TryAddWithoutValidation("x-api-key", token);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = TryParseRetryAfter(response, _timeProvider.GetUtcNow());
                return new AgentQuotaSnapshot
                {
                    AvailablePct = 0.0,
                    ResetAt = reset,
                    Notes = "anthropic: rate-limited",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Crock quota probe: anthropic /v1/models returned {Status}", (int)response.StatusCode);
                return AgentQuotaSnapshot.UnknownSnapshot(
                    QuotaUnknownReasons.FromHttpStatus(response.StatusCode),
                    $"anthropic: HTTP {(int)response.StatusCode}");
            }

            // 200 ⇒ credential is live. There is no remaining-credit field to
            // surface for a raw API-key path; spend gating lives in
            // IAgentBudgetProvider. Report 100% with a Notes tag that
            // explains the pricing model so the operator dashboard reads
            // truthfully.
            return new AgentQuotaSnapshot
            {
                AvailablePct = 100.0,
                Notes = "anthropic api-key: pay-per-token (~50% batch discount); no remaining-credit endpoint",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Crock quota probe: transient failure calling anthropic /v1/models");
            return AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient,
                "anthropic: transient probe error");
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

    /// <summary>
    /// Extracts the Anthropic API key from a CrockCode <c>config.json</c>
    /// payload. CrockCode's config shape is
    /// <c>{ "anthropic_api_key": "sk-…", "tunnel_provider": "…" }</c>; the key
    /// extractor is tolerant of leading/trailing whitespace, alternate
    /// casings (<c>ANTHROPIC_API_KEY</c>), and the camelCase variant
    /// (<c>anthropicApiKey</c>) that operators sometimes hand-write. Returns
    /// null on any parse failure — the caller treats null as "no credential".
    /// </summary>
    public static string? TryExtractApiKey(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                if (string.Equals(prop.Name, "anthropic_api_key", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prop.Name, "anthropicApiKey", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prop.Name, "ANTHROPIC_API_KEY", StringComparison.Ordinal))
                {
                    var raw = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CacheEntry(AgentQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt);
    private sealed record ExhaustionOverride(DateTimeOffset ExpiresAt, DateTimeOffset? ResetAt);
}
