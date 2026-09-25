using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OpenBaoPlugin;

/// <summary>One fetched static secret: the resolved data object's fields.</summary>
public sealed record OpenBaoStaticSecret(IReadOnlyDictionary<string, string> Data);

/// <summary>One issued dynamic credential: server lease identity, lifetime, and credential fields.</summary>
public sealed record OpenBaoDynamicCredential(
    string LeaseId,
    int LeaseDurationSeconds,
    bool Renewable,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Minimal OpenBao REST client against the Vault-compatible API:
/// AppRole login (<c>POST /v1/auth/{mount}/login</c>), path reads
/// (<c>GET /v1/{path}</c> — KV secrets and dynamic credential endpoints),
/// and the genuine server lease lifecycle (<c>POST /v1/sys/leases/renew</c>,
/// <c>POST /v1/sys/leases/revoke</c>).
/// <para>Authentication travels in the <c>X-Vault-Token</c> header — the
/// documented API-compatible header OpenBao accepts. Every response body is
/// bounded <em>before</em> buffering (<c>ResponseHeadersRead</c> +
/// content-length pre-check + capped copy), so an unbounded upstream can
/// never fill host memory. Every failure surfaces as
/// <see cref="OpenBaoException"/> with safe fields only — values, tokens,
/// role IDs and secret IDs never reach messages, logs, or exceptions.
/// OpenBao error bodies are <c>{"errors": […]}</c>; the raw body is
/// sanitised and truncated before relay, and OpenBao error text names
/// paths, policies, and lease ids — never values.</para>
/// </summary>
public sealed class OpenBaoRestClient
{
    private readonly CredentialTransport _transport;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>
    /// OpenBao relays failures as <c>{"errors": […]}</c> — an array, not a
    /// string field the transport can pick — so no field names apply and
    /// the sanitised raw body is relayed instead.
    /// </summary>
    private static readonly string[] ErrorDetailFields = [];

    // An AppRole login response is a JSON object wrapping a token — typically
    // under a kilobyte; bound generously so real tokens fit while still
    // capping the buffer.
    private const int MaxLoginResponseBytes = 8 * 1024;

    /// <summary>Documented API-compatible auth header OpenBao accepts.</summary>
    private const string TokenHeader = "X-Vault-Token";

    public OpenBaoRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _transport = new CredentialTransport(
            http ?? throw new ArgumentNullException(nameof(http)),
            OpenBaoException.BackendName,
            OpenBaoException.Create,
            ErrorDetailFields,
            relayRawErrorText: true,
            clock: clock);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// AppRole login (<c>POST /v1/auth/{mount}/login</c>). Returns the
    /// client token and its absolute expiry (server
    /// <c>auth.lease_duration</c> seconds minus <paramref name="skew"/>).
    /// The body carries role and secret IDs; it is built, sent, and dropped
    /// here — never logged, never stored, never placed in an exception.
    /// </summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> LoginAppRoleAsync(
        string address,
        string mount,
        string roleId,
        string secretId,
        TimeSpan skew,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        RequireValidPath(mount, "auth mount");
        ArgumentNullException.ThrowIfNull(roleId);
        ArgumentNullException.ThrowIfNull(secretId);
        var url = $"{address.TrimEnd('/')}/v1/auth/{mount}/login";
        var body = JsonSerializer.Serialize(new { role_id = roleId, secret_id = secretId });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _transport.SendAsync(request, "AppRole login", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(
            response, "AppRole login", MaxLoginResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object)
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse, "OpenBao AppRole login returned no auth object.");
            if (!CredentialJson.TryGetString(auth, "client_token", out var token) || string.IsNullOrEmpty(token))
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse, "OpenBao AppRole login returned no client_token.");
            if (!CredentialJson.TryGetInt64(auth, "lease_duration", out var leaseSeconds)
                || leaseSeconds <= 0 || leaseSeconds > int.MaxValue)
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    "OpenBao AppRole login returned a token with no usable lifetime.");
            var expiresAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(leaseSeconds) - skew;
            _log.LogDebug(
                "OpenBao AppRole login on mount '{Mount}' succeeded; token valid until {ExpiresAt}.",
                mount, expiresAt);
            return (token, expiresAt);
        }
    }

    /// <summary>
    /// Reads a static secret (<c>GET /v1/{path}</c>). For KV v2
    /// (<paramref name="kvVersion"/> 2) the credential fields live under
    /// <c>data.data</c>; for KV v1 and other unleased engines directly
    /// under <c>data</c>. A response that arrives carrying a real
    /// <c>lease_id</c> is a backend surprise the mapping declared static —
    /// refused as an invalid response rather than silently holding an
    /// unleased-looking but actually leased credential.
    /// </summary>
    public async Task<OpenBaoStaticSecret> GetStaticSecretAsync(
        string address,
        string token,
        string path,
        int kvVersion,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var doc = await ReadPathAsync(
            address, token, path, $"fetch static secret '{path}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (CredentialJson.TryGetString(root, "lease_id", out var leaseId) && !string.IsNullOrEmpty(leaseId))
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao read of '{path}' returned a server lease; map it with DynamicPath instead of SecretPath.");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao read of '{path}' returned no data object.");
            if (kvVersion == 2)
            {
                if (!data.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
                    throw new OpenBaoException(
                        CredentialFailureKind.InvalidResponse,
                        $"OpenBao KV v2 read of '{path}' returned no nested data object.");
            }
            _log.LogDebug("OpenBao fetched static secret at '{Path}' (kv{KvVersion}).", path, kvVersion);
            return new OpenBaoStaticSecret(CredentialJson.ReadStringFields(data));
        }
    }

    /// <summary>
    /// Issues a dynamic credential (<c>GET /v1/{path}</c> against a
    /// secrets-engine creds endpoint such as <c>database/creds/{role}</c>).
    /// The response must carry a real lease — <c>lease_id</c> and a
    /// positive <c>lease_duration</c> — plus the credential
    /// <c>data</c> object; anything less is a backend surprise refused as
    /// an invalid response, never silently treated as static.
    /// </summary>
    public async Task<OpenBaoDynamicCredential> IssueDynamicCredentialAsync(
        string address,
        string token,
        string path,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var doc = await ReadPathAsync(
            address, token, path, $"issue dynamic credential '{path}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!CredentialJson.TryGetString(root, "lease_id", out var leaseId) || string.IsNullOrEmpty(leaseId))
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao read of '{path}' returned no lease_id; DynamicPath requires a leasing engine.");
            // A minted lease id is dependency output bound for a lease
            // handle and for log lines: refuse a shape a handle cannot
            // carry ('.' or control characters) — it could never be
            // renewed or revoked, and raw control characters would forge
            // audit-log entries on the way out. The credential is still
            // live server-side: hand it back before failing.
            if (!LeaseHandles.IsValidSegment(leaseId))
            {
                await TryRevokeLeaseAsync(address, token, leaseId, sync: true, ct).ConfigureAwait(false);
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao read of '{path}' returned a lease_id that cannot be carried in a lease handle.");
            }
            // A minted lease id with an unusable rest-of-body is still a
            // live credential: hand it back before failing, or it is
            // orphaned until its TTL.
            if (!CredentialJson.TryGetInt64(root, "lease_duration", out var leaseSeconds)
                || leaseSeconds <= 0 || leaseSeconds > int.MaxValue)
            {
                await TryRevokeLeaseAsync(address, token, leaseId, sync: true, ct).ConfigureAwait(false);
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao read of '{path}' returned no usable lease_duration.");
            }
            var renewable = root.TryGetProperty("renewable", out var renew)
                && renew.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                await TryRevokeLeaseAsync(address, token, leaseId, sync: true, ct).ConfigureAwait(false);
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao read of '{path}' returned no data object.");
            }
            _log.LogDebug(
                "OpenBao issued dynamic credential at '{Path}' under lease '{LeaseId}' for {Seconds}s{Renewable}.",
                path, leaseId, leaseSeconds, renewable ? string.Empty : " (not renewable)");
            return new OpenBaoDynamicCredential(
                leaseId, (int)leaseSeconds, renewable, CredentialJson.ReadStringFields(data));
        }
    }

    /// <summary>
    /// Best-effort revoke of a lease the caller is giving up on — a minted
    /// credential whose response was rejected, or one the provider must
    /// return after a local validation failure. Never throws: a failed
    /// give-back is logged (lease id sanitised — it may be exactly the
    /// shape that failed validation) and the lease expires at its server
    /// TTL; the caller surfaces its own failure. Callers passing
    /// <paramref name="sync"/>: issue-path rejections always pass true —
    /// the rejected credential must be dead before the failure surfaces,
    /// whatever <c>RevokeSync</c> says.
    /// </summary>
    internal async Task TryRevokeLeaseAsync(
        string address, string token, string leaseId, bool sync, CancellationToken ct)
    {
        try
        {
            await RevokeLeaseAsync(address, token, leaseId, sync, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "OpenBao could not return rejected lease '{LeaseId}'; it expires at its server TTL.",
                SafeLeaseId(leaseId));
        }
    }

    /// <summary>
    /// Renews a server lease (<c>POST /v1/sys/leases/renew</c>), asking for
    /// <paramref name="incrementSeconds"/> more; the server clamps to the
    /// role's max TTL. Returns the granted lifetime in seconds. A
    /// non-renewable lease fails server-side (HTTP 400) and propagates as a
    /// typed failure — loud, so the sweep records and retries.
    /// </summary>
    public async Task<int> RenewLeaseAsync(
        string address,
        string token,
        string leaseId,
        int incrementSeconds,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        var displayLeaseId = SafeLeaseId(leaseId);
        var url = $"{address.TrimEnd('/')}/v1/sys/leases/renew";
        var body = JsonSerializer.Serialize(new { lease_id = leaseId, increment = incrementSeconds });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddTokenHeader(request, token);
        using var response = await _transport.SendAsync(
            request, $"renew lease '{displayLeaseId}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(
            response, $"renew lease '{displayLeaseId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!CredentialJson.TryGetInt64(doc.RootElement, "lease_duration", out var leaseSeconds)
                || leaseSeconds <= 0 || leaseSeconds > int.MaxValue)
                throw new OpenBaoException(
                    CredentialFailureKind.InvalidResponse,
                    $"OpenBao renew of lease '{displayLeaseId}' returned no usable lease_duration.");
            _log.LogDebug("OpenBao renewed lease '{LeaseId}' for {Seconds}s.", displayLeaseId, leaseSeconds);
            return (int)leaseSeconds;
        }
    }

    /// <summary>
    /// Revokes a server lease (<c>POST /v1/sys/leases/revoke</c>). With
    /// <paramref name="sync"/> the call returns only once the credential is
    /// genuinely dead — teardown revocation is immediate, not queued.
    /// Idempotent: a 404 (already gone) counts as revoked, matching the
    /// manager's must-be-idempotent contract. Every other failure —
    /// notably 401/403 (rejected token) and 429 (rate-limited) —
    /// propagates so the sweep retries a revocation that never happened.
    /// </summary>
    public async Task RevokeLeaseAsync(
        string address,
        string token,
        string leaseId,
        bool sync,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        var displayLeaseId = SafeLeaseId(leaseId);
        var url = $"{address.TrimEnd('/')}/v1/sys/leases/revoke";
        var body = JsonSerializer.Serialize(new { lease_id = leaseId, sync });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddTokenHeader(request, token);
        try
        {
            using var response = await _transport.SendAsync(
                request, $"revoke lease '{displayLeaseId}'", ct).ConfigureAwait(false);
            _log.LogInformation("OpenBao revoked lease '{LeaseId}'.", displayLeaseId);
        }
        catch (OpenBaoException ex) when (ex.Kind == CredentialFailureKind.NotFound)
        {
            // Already gone server-side: revocation is complete by definition.
            _log.LogInformation(
                "OpenBao lease '{LeaseId}' was already absent; treating revocation as complete.",
                displayLeaseId);
        }
    }

    private async Task<JsonDocument> ReadPathAsync(
        string address, string token, string path, string operation,
        int maxResponseBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        RequireValidPath(path, "read path");
        var url = $"{address.TrimEnd('/')}/v1/{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddTokenHeader(request, token);
        var response = await _transport.SendAsync(request, operation, ct).ConfigureAwait(false);
        return await _transport.ReadJsonAsync(response, operation, maxResponseBytes, ct).ConfigureAwait(false);
    }

    private static void RequireValidPath(string path, string what)
    {
        // The configured path lands in the request URL: enforce the segment
        // policy at the sink, not only at the options boundary, so any
        // caller builds only same-origin URLs under /v1/.
        if (!OpenBaoPaths.IsValid(path))
            throw new OpenBaoException(
                CredentialFailureKind.Misconfigured,
                $"OpenBao {what} '{path}' is not a valid relative path (non-empty segments, no '..', no whitespace or control characters).");
    }

    /// <summary>
    /// Attaches the provider token to a request. A token the header sink
    /// cannot carry — control or otherwise invalid header characters,
    /// possible when the token is dependency output (a server-issued
    /// client_token) — is a typed invalid response, not a raw
    /// <see cref="FormatException"/> escaping the transport contract.
    /// </summary>
    private static void AddTokenHeader(HttpRequestMessage request, string token)
    {
        try
        {
            request.Headers.Add(TokenHeader, token);
        }
        catch (FormatException ex)
        {
            throw new OpenBaoException(
                CredentialFailureKind.InvalidResponse,
                "OpenBao credential cannot be carried in an HTTP header (invalid characters).",
                ex);
        }
    }

    /// <summary>
    /// Display form of a lease id for logs and operation text. A lease id
    /// is dependency output — control characters must be flattened before
    /// one reaches a log line or a message, whatever the caller passed.
    /// The raw id still goes on the wire; only the rendered form is
    /// sanitised.
    /// </summary>
    private static string SafeLeaseId(string leaseId)
        => CredentialMessages.Truncate(
            leaseId, "(no lease id)", CredentialMessages.MaxServerDetailChars);
}
