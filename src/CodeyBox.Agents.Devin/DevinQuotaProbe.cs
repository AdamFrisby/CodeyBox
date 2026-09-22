using System.Net.Http.Headers;
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
/// <para><b>Wire contract (verified 2026-09-21 against the live
/// endpoint).</b> The endpoint speaks binary Connect-RPC only —
/// <c>application/json</c> bodies are rejected <c>invalid_argument</c> — so
/// the request is a hand-encoded protobuf <c>GetUserStatusRequest</c>
/// carrying the credentials token inside a <c>metadata</c> message, with
/// <c>Authorization: Basic &lt;token&gt;</c> (NOT Bearer) and
/// <c>Connect-Protocol-Version: 1</c>. The response is
/// <c>GetUserStatusResponse</c>: field 1 = <c>user_status</c>, whose field 13
/// = <c>plan_status</c> {14: daily_quota_remaining_percent, 15:
/// weekly_quota_remaining_percent, 16: overage_balance_micros, 17:
/// daily_quota_reset_at_unix, 18: weekly_quota_reset_at_unix, 19:
/// acu_consumed, 20: acu_limit}; response field 2 = <c>plan_info</c> {2:
/// plan_name}. Field numbers were confirmed by decoding a live response;
/// unset int64 fields arrive as the proto3 sentinal value
/// <c>ulong.MaxValue</c> and are treated as absent.</para>
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

    // The response carries the account's entire model catalog (~110 KiB for a
    // stock Pro account), so the cap is generous — it exists to bound memory,
    // not to truncate legitimate payloads.
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    internal const string UnexpectedShapeNotes = "unexpected response shape";

    // Request metadata values mirroring what devin 3000.11.1 sends. The
    // server accepts an arbitrary version string; the cli's own build stamps
    // "0.0.0-dev" for local builds, which is honoured fine.
    private const string RequestIdeName = "chisel";
    private const string RequestVersion = "0.0.0-dev";
    private const string RequestLocale = "en";

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
            // Verified live 2026-09-21: the CLI authenticates with the
            // credentials.toml token verbatim under the Basic scheme (the
            // value is already a `devin-session-token$…` bearer string).
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            request.Headers.Add("Connect-Protocol-Version", "1");
            request.Content = new ByteArrayContent(BuildRequestBody(token));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");

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

            if (string.Equals(snapshot.Notes, UnexpectedShapeNotes, StringComparison.Ordinal))
            {
                // Silent fallthrough is what makes shape drift invisible.
                // The body is binary proto — log only its size, never bytes:
                // it can carry account PII (name/email) that doesn't belong
                // in operator logs.
                _log.LogDebug(
                    "Devin quota probe: unexpected response shape ({Length} bytes)",
                    body.Length);
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

    /// <summary>
    /// Builds the <c>GetUserStatusRequest</c>: a single field-1
    /// <c>metadata</c> message mirroring the CLI's own envelope
    /// (ide/version/token/locale/os fields; the 732-byte machine fingerprint
    /// the CLI adds at field 31 is optional — the endpoint serves the RPC
    /// without it).
    /// </summary>
    internal static byte[] BuildRequestBody(string token)
    {
        var metadata = new DevinProtoWire.MessageWriter()
            .Field(1, RequestIdeName)
            .Field(2, RequestVersion)
            .Field(3, token)
            .Field(4, RequestLocale)
            .Field(5, HostOsName())
            .Field(7, RequestVersion)
            .Field(12, RequestIdeName);
        return new DevinProtoWire.MessageWriter().Field(1, metadata).ToArray();
    }

    private static string HostOsName() =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "windows"
        : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX) ? "osx"
        : "linux";

    // Proto field numbers on GetUserStatusResponse / PlanStatus — verified by
    // decoding a live response (devin 3000.11.1, 2026-09-21). Unset int64
    // fields arrive as ulong.MaxValue and are filtered by TryGetRealVarint.
    private const int FieldUserStatus = 1;
    private const int FieldPlanInfo = 2;
    private const int PlanStatusField = 13;
    private const int PlanStatusDailyPctField = 14;
    private const int PlanStatusWeeklyPctField = 15;
    private const int PlanStatusOverageMicrosField = 16;
    private const int PlanStatusDailyResetField = 17;
    private const int PlanStatusWeeklyResetField = 18;
    private const int PlanStatusAcuConsumedField = 19;
    private const int PlanStatusAcuLimitField = 20;
    private const int PlanInfoNameField = 2;

    /// <summary>Varints carrying the "unset" int64 sentinel (-1) count as absent.</summary>
    private static ulong? TryGetRealVarint(DevinProtoWire.Message message, int field)
    {
        var value = message.TryGetVarint(field);
        return value is null or ulong.MaxValue ? null : value;
    }

    internal static AgentQuotaSnapshot ParseResponse(byte[] body)
    {
        DevinProtoWire.Message root;
        try
        {
            root = DevinProtoWire.Parse(body);
        }
        catch (DevinProtoWire.ProtoWireFormatException)
        {
            return Unknown(QuotaUnknownReason.Permanent, "invalid protobuf");
        }

        // Tolerate a flatter serialisation where plan_status lands at the top
        // level (or one wrapper level down under user_status).
        var userStatus = root.TryGetMessage(FieldUserStatus) ?? root;
        var planStatus = userStatus.TryGetMessage(PlanStatusField) ?? userStatus;

        var windows = new List<WindowQuota>();

        var dailyPct = TryGetRealVarint(planStatus, PlanStatusDailyPctField);
        var dailyReset = TryGetRealVarint(planStatus, PlanStatusDailyResetField);
        if (dailyPct.HasValue)
        {
            windows.Add(new WindowQuota
            {
                Name = "daily",
                AvailablePct = ClampAvailable(dailyPct.Value),
                ResetAt = EpochToDateTime(dailyReset),
                UsedPercent = ClampAvailable(100.0 - dailyPct.Value),
                ResetAtEpochSeconds = EpochToLong(dailyReset),
            });
        }

        var weeklyPct = TryGetRealVarint(planStatus, PlanStatusWeeklyPctField);
        var weeklyReset = TryGetRealVarint(planStatus, PlanStatusWeeklyResetField);
        if (weeklyPct.HasValue)
        {
            windows.Add(new WindowQuota
            {
                Name = "weekly",
                AvailablePct = ClampAvailable(weeklyPct.Value),
                ResetAt = EpochToDateTime(weeklyReset),
                UsedPercent = ClampAvailable(100.0 - weeklyPct.Value),
                ResetAtEpochSeconds = EpochToLong(weeklyReset),
            });
        }

        var acuConsumed = TryGetRealVarint(planStatus, PlanStatusAcuConsumedField);
        var acuLimit = TryGetRealVarint(planStatus, PlanStatusAcuLimitField);
        if (acuConsumed.HasValue && acuLimit is > 0)
        {
            var acuRemaining = ClampAvailable(100.0 * (1.0 - (double)acuConsumed.Value / acuLimit.Value));
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
        var planName = root.TryGetMessage(FieldPlanInfo)?.TryGetString(PlanInfoNameField);

        var notes = new List<string>(2);
        if (planName is not null)
            notes.Add($"plan {planName}");
        var overageMicros = TryGetRealVarint(planStatus, PlanStatusOverageMicrosField);
        if (overageMicros is > 0)
            notes.Add($"overage balance ${overageMicros.Value / 1_000_000.0:0.##}");

        return new AgentQuotaSnapshot
        {
            AvailablePct = binding.AvailablePct,
            ResetAt = binding.ResetAt,
            Notes = notes.Count == 0 ? null : string.Join("; ", notes),
            Windows = windows,
        };
    }

    private static long? EpochToLong(ulong? epochSeconds) =>
        epochSeconds is { } seconds && seconds > 0 && seconds <= long.MaxValue
            ? (long)seconds
            : null;

    private static DateTimeOffset? EpochToDateTime(ulong? epochSeconds)
    {
        var seconds = EpochToLong(epochSeconds);
        if (seconds is null)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
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

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        var raw = await content.ReadAsByteArrayAsync(ct);
        return raw.Length <= MaxResponseBytes ? raw : null;
    }
}
