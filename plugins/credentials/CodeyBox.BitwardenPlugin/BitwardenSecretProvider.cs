using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// CodeyBox lease-shaped secret provider for Bitwarden Secrets Manager.
/// One project, one plugin: secret values from operator-mapped secrets,
/// read with machine-account access tokens minted via the
/// client-credentials grant — pure HTTPS, no subprocess, no CLI, no shell.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted, and issues nothing until <c>Enabled=true</c> in its own
/// section. Authorisation stays with the host manager — a grant decides
/// whether a group applies; this provider only resolves mapped secrets the
/// manager already approved, and a group without a matching grant is never
/// fetched (the manager never calls here for it).</para>
/// <para>Provider credentials come from the host credential chain
/// (environment variables holding the single machine-account access tokens
/// as issued, <c>0.{id}.{secret}:{key}</c> — grant material and decryption
/// key in one string); configuration holds only the variable
/// <em>names</em>, never values — plus the client ids, which are not
/// secrets. Secret values are opened from their end-to-end-encrypted
/// CipherStrings with that key (see <see cref="BitwardenCrypto"/>); nothing
/// emitted — logs, lease records, exceptions — ever carries a secret value,
/// an access token, a client secret, or key material.</para>
/// <para>Honest lease surface: Secrets Manager secrets are static values
/// with no server-side lease to delete — the lease is a client-side
/// validity window over a genuinely short-lived access token: renewal
/// re-authenticates and re-fetches (an edit or rotation propagates within
/// one window), the minted expiry never exceeds the token's own server
/// lifetime, and revocation drops the cached token plus local invalidation
/// (a revoked lease refuses renewal in this process — verified, not
/// assumed) plus the orchestrator's teardown scrub of the per-exec
/// environment. There is no token-revocation endpoint in the
/// client-credentials flow, so a minted token remains bearer-valid until
/// its own short expiry; the window above bounds that exposure. See the
/// plugin README for the full statement, including why the native C# SDK
/// was deliberately not taken as a dependency.</para>
/// </summary>
[CodeyBoxPlugin(
    id: BitwardenOptions.PluginId,
    displayName: "CodeyBox: Bitwarden Secrets",
    minHostApiVersion: "1.0")]
public sealed class BitwardenSecretProvider : ILeaseCapableSecretProvider, IPluginInitializer, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private readonly LazyCredentialClient<BitwardenRestClient> _clients;
    private BitwardenRestClient? _api;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IssuedLeaseContext> _issued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TokenCacheKey, BitwardenAccessToken> _tokens = new();

    /// <summary>
    /// Lease handles revoked in this process. Renewal of one fails loudly
    /// instead of silently re-fetching — revocation is verified, not
    /// assumed. The set is memory-only, so a restart re-resolves the
    /// mapping (the documented trade-off of holding no server lease).
    /// It accumulates one lease-id-sized entry per revocation for the
    /// process lifetime — accepted deliberately, because a revoked handle
    /// must refuse renewal forever and no eviction can safely forget that.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _revokedLeases = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Production constructor.</summary>
    public BitwardenSecretProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _clients = NewClientCache(null);
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal BitwardenSecretProvider(
        HttpClient http,
        IConfigurationSection config,
        TimeProvider? clock = null,
        Func<string, string?>? env = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _testConfig = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _logger = log ?? NullLogger.Instance;
        _clients = NewClientCache(http);
    }

    /// <inheritdoc />
    public string ProviderId => BitwardenOptions.PluginId;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        EnsureClients();
        var warnings = new List<string>();
        var options = BitwardenOptions.FromConfiguration(context.ScopedConfig, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("Bitwarden plugin option: {Warning}", warning);
        foreach (var error in options.Validate())
            _logger.LogError("Bitwarden plugin configuration: {Error}", error);
        if (!options.Enabled)
        {
            _logger.LogInformation("Bitwarden secret provider is disabled (Enabled=false); issuing nothing.");
            return Task.CompletedTask;
        }
        _logger.LogInformation(
            "Bitwarden secret provider initialised for {Api} with {Count} secret mapping(s).",
            options.ApiUrl, options.Mappings.Count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when this backend serves <paramref name="secret"/>: the plugin
    /// is enabled and an operator mapping names its sandbox variable (plus
    /// the mapping's group guard, when set). Never touches the network and
    /// never throws — a surprise means "cannot issue", not a pipeline
    /// failure.
    /// </summary>
    public bool CanIssue(ProjectSandboxSecret secret)
    {
        try
        {
            if (secret is null)
                return false;
            var options = CurrentOptions();
            if (!options.Enabled)
                return false;
            return FindMapping(options, secret) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bitwarden CanIssue suppressed an unexpected error; treating as cannot-issue.");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<LeasedSecretMaterial> IssueAsync(
        ProjectSandboxSecret secret,
        Guid workItemId,
        string scope,
        TimeSpan requestedTtl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var options = RequireUsableOptions();
        var mapping = FindMapping(options, secret)
            ?? throw new BitwardenException(
                CredentialFailureKind.Misconfigured,
                $"Bitwarden has no mapping for sandbox variable '{secret.SandboxEnvVar}'; the operator never put this secret against this backend.");
        EnsureClients();

        var token = await GetAccessTokenAsync(options, mapping, ct).ConfigureAwait(false);
        var value = await FetchValueAsync(options, mapping, token, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(value))
            throw new BitwardenException(
                CredentialFailureKind.InvalidResponse,
                $"Bitwarden secret '{secret.SandboxEnvVar}' returned an empty value.");
        var expiresAt = LeaseExpiry(options, token, requestedTtl);
        var leaseId = BitwardenLeaseIds.BuildStatic(mapping.SandboxEnvVar);
        _issued[leaseId] = new IssuedLeaseContext(mapping, CredentialKey(options, mapping));
        _logger.LogInformation(
            "Bitwarden issued lease '{LeaseId}' for '{Var}' (scope {Scope}).",
            leaseId, mapping.SandboxEnvVar, scope);
        return new LeasedSecretMaterial
        {
            LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
            Value = value,
            ExpiresAt = expiresAt,
            Brokered = false,
        };
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> RenewAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);
        var options = RequireUsableOptions();
        EnsureClients();
        if (_revokedLeases.ContainsKey(leaseId))
            throw new BitwardenException(
                CredentialFailureKind.Misconfigured,
                $"Bitwarden lease '{leaseId}' was revoked in this process and cannot be renewed.");
        var context = _issued.TryGetValue(leaseId, out var known)
            ? known
            : RebuildContext(leaseId, options, parsed);

        // Static renewal re-authenticates (reusing the cached token while
        // it is still safely valid) and re-fetches, so an edit or rotation
        // propagates within one window; the fresh value reaches the guest
        // on next provisioning.
        var token = await GetAccessTokenAsync(options, context.Mapping, ct).ConfigureAwait(false);
        await FetchValueAsync(options, context.Mapping, token, ct).ConfigureAwait(false);
        var expiresAt = LeaseExpiry(options, token, TimeSpan.Zero);
        // Re-register the (possibly rebuilt) context so revocation can still
        // evict its cached token.
        _issued[leaseId] = context;
        _logger.LogDebug("Bitwarden renewed lease '{LeaseId}'.", leaseId);
        return expiresAt;
    }

    /// <inheritdoc />
    public Task RevokeAsync(string leaseId, CancellationToken ct = default)
    {
        _ = ParseOurs(leaseId);

        // Secrets Manager secrets carry no server-side lease and the
        // client-credentials flow exposes no token-revocation endpoint:
        // revocation is dropping the cached access token (any sibling lease
        // re-authenticates transparently on its next renewal), local
        // invalidation refusing future renewal in this process, plus the
        // orchestrator's teardown scrub of the per-exec environment.
        // Always succeeds and is idempotent by construction.
        if (_issued.TryRemove(leaseId, out var context))
        {
            if (_tokens.TryRemove(context.CredentialKey, out var evicted))
                evicted.ClearSecrets();
        }
        _revokedLeases[leaseId] = true;
        _logger.LogInformation("Bitwarden invalidated lease '{LeaseId}'.", leaseId);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _issued.Clear();
        foreach (var token in _tokens.Values)
            token.ClearSecrets();
        _tokens.Clear();
        _tokenLock.Dispose();
        _clients.Dispose();
    }

    internal BitwardenOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new BitwardenOptions()
            : BitwardenOptions.FromConfiguration(section);
    }

    private async Task<BitwardenAccessToken> GetAccessTokenAsync(
        BitwardenOptions options, BitwardenSecretMapping mapping, CancellationToken ct)
    {
        var secretEnvName = string.IsNullOrWhiteSpace(mapping.ClientSecretEnvVar)
            ? options.ClientSecretEnvVar
            : mapping.ClientSecretEnvVar;
        var rawCredential = string.IsNullOrWhiteSpace(secretEnvName) ? null : _env(secretEnvName);
        if (string.IsNullOrWhiteSpace(rawCredential))
            throw new BitwardenException(
                CredentialFailureKind.Misconfigured,
                $"Bitwarden client-secret env '{secretEnvName}' is empty. Provision a machine-account access token " +
                $"with access to the mapped projects from the host credential chain.");
        // The credential chain holds the single machine-account access
        // token as issued (0.{id}.{secret}:{key}): the id/secret drive the
        // client-credentials grant and the :key opens CipherString values.
        // A bare client secret (legacy shape) still authenticates but
        // carries no decryption key. An explicit mapping ClientId always
        // wins; otherwise the token's own id is the client id.
        var hasTokenFormat = BitwardenCrypto.TryParseMachineCredential(
            rawCredential, out var tokenClientId, out var tokenClientSecret, out var tokenKey);
        var clientId = !string.IsNullOrWhiteSpace(mapping.ClientId)
            ? mapping.ClientId.Trim()
            : hasTokenFormat
                ? tokenClientId
                : options.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new BitwardenException(
                CredentialFailureKind.Misconfigured,
                $"Bitwarden mapping for '{mapping.SandboxEnvVar}' names no machine-account client id; set mapping ClientId or provider ClientId.");
        var clientSecret = hasTokenFormat ? tokenClientSecret : rawCredential.Trim();
        var key = CredentialKey(options, mapping);

        if (_tokens.TryGetValue(key, out var cached) && TokenUsable(cached, options))
        {
            ZeroKeyMaterial(tokenKey);
            return cached;
        }

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tokens.TryGetValue(key, out cached) && TokenUsable(cached, options))
            {
                ZeroKeyMaterial(tokenKey);
                return cached;
            }
            EnsureClients();
            BitwardenAccessToken fresh;
            try
            {
                fresh = await _api!.AuthenticateAsync(
                    options.IdentityUrl, clientId, clientSecret, options.MaxResponseBytes, tokenKey, ct).ConfigureAwait(false);
            }
            catch
            {
                // The mint failed, so the parsed key reached no token;
                // do not leave it on the heap.
                ZeroKeyMaterial(tokenKey);
                throw;
            }
            if (_tokens.TryGetValue(key, out var replaced))
                replaced.ClearSecrets();
            _tokens[key] = fresh;
            return fresh;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Zeroes a parsed access-token key that is not being handed to a
    /// minted token (a cache hit or a failed mint). On success the minted
    /// <see cref="BitwardenAccessToken"/> owns the array and
    /// <see cref="BitwardenAccessToken.ClearSecrets"/> zeroes it.
    /// </summary>
    private static void ZeroKeyMaterial(byte[]? key)
    {
        if (key is not null)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
    }

    private bool TokenUsable(BitwardenAccessToken token, BitwardenOptions options)
        => _clock.GetUtcNow() + CredentialOptions.TokenSkewSpan(options.TokenRefreshSkewSeconds) < token.ExpiresAt;

    private async Task<string> FetchValueAsync(
        BitwardenOptions options, BitwardenSecretMapping mapping, BitwardenAccessToken accessToken, CancellationToken ct)
    {
        EnsureClients();
        var projectId = string.IsNullOrWhiteSpace(mapping.ProjectId) ? null : mapping.ProjectId.Trim();
        if (!string.IsNullOrWhiteSpace(mapping.SecretId))
        {
            return await _api!.GetSecretAsync(
                options.ApiUrl, accessToken, mapping.SecretId.Trim(), projectId,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
        }
        var organizationId = string.IsNullOrWhiteSpace(mapping.OrganizationId)
            ? options.OrganizationId.Trim()
            : mapping.OrganizationId.Trim();
        var secretId = await _api!.ResolveSecretIdAsync(
            options.ApiUrl, accessToken.Token, organizationId, mapping.SecretKey.Trim(), projectId,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
        return await _api.GetSecretAsync(
            options.ApiUrl, accessToken, secretId, projectId,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
    }

    private DateTimeOffset LeaseExpiry(
        BitwardenOptions options, BitwardenAccessToken token, TimeSpan requestedTtl)
    {
        var now = _clock.GetUtcNow();
        var window = CredentialOptions.StaticLeaseWindow(options.StaticLeaseTtlMinutes, requestedTtl);
        var tokenRemaining = token.ExpiresAt - now - CredentialOptions.TokenSkewSpan(options.TokenRefreshSkewSeconds);
        if (tokenRemaining <= TimeSpan.Zero)
            throw new BitwardenException(
                CredentialFailureKind.InvalidResponse,
                "Bitwarden access token carries no usable lifetime; refusing to cache a secret beyond its lease.");
        return now + (window < tokenRemaining ? window : tokenRemaining);
    }

    private static TokenCacheKey CredentialKey(BitwardenOptions options, BitwardenSecretMapping mapping)
    {
        var clientId = string.IsNullOrWhiteSpace(mapping.ClientId)
            ? options.ClientId.Trim()
            : mapping.ClientId.Trim();
        var secretEnvName = string.IsNullOrWhiteSpace(mapping.ClientSecretEnvVar)
            ? options.ClientSecretEnvVar
            : mapping.ClientSecretEnvVar;
        // Bound to the origins that minted the token: options are re-read
        // on every call for hot reload, so an operator changing ApiUrl or
        // IdentityUrl must never replay a token minted by the old origin
        // against the new one.
        return new TokenCacheKey(
            options.IdentityUrl.Trim(),
            options.ApiUrl.Trim(),
            clientId,
            secretEnvName);
    }

    private BitwardenOptions RequireUsableOptions()
        => CredentialOptions.RequireUsable(CurrentOptions(), BitwardenException.BackendName, BitwardenException.Create);

    private static BitwardenSecretMapping? FindMapping(BitwardenOptions options, ProjectSandboxSecret secret)
    {
        foreach (var mapping in FindMappingsByEnvVar(options, secret.SandboxEnvVar))
        {
            if (!string.IsNullOrWhiteSpace(mapping.Group)
                && !string.Equals(mapping.Group, secret.Group, StringComparison.Ordinal))
                continue;
            return mapping;
        }
        return null;
    }

    /// <summary>Mappings naming this sandbox variable, in declaration order.</summary>
    private static IEnumerable<BitwardenSecretMapping> FindMappingsByEnvVar(
        BitwardenOptions options, string sandboxEnvVar)
    {
        foreach (var mapping in options.Mappings)
        {
            if (string.Equals(mapping.SandboxEnvVar, sandboxEnvVar, StringComparison.Ordinal))
                yield return mapping;
        }
    }

    private IssuedLeaseContext RebuildContext(
        string leaseId, BitwardenOptions options, string sandboxEnvVar)
    {
        // Restart path: the in-memory issue registry is gone, but the lease
        // id carries the sandbox variable — enough to re-resolve today's
        // mapping. The grant itself was verified at issue time and is not
        // re-decided here; renewal extends the same lease.
        var mapping = FindMappingsByEnvVar(options, sandboxEnvVar).FirstOrDefault();
        if (mapping is null)
            throw new BitwardenException(
                CredentialFailureKind.Misconfigured,
                $"Bitwarden lease '{leaseId}' names sandbox variable '{sandboxEnvVar}' with no current mapping; restore the mapping.");
        return new IssuedLeaseContext(mapping, CredentialKey(options, mapping));
    }

    private static string ParseOurs(string leaseId)
        => LeaseHandles.ParseOrThrow<string>(
            leaseId, BitwardenException.BackendName, BitwardenLeaseIds.TryParse, BitwardenException.Create);

    private void EnsureClients() => _api = _clients.Get();

    private LazyCredentialClient<BitwardenRestClient> NewClientCache(HttpClient? injected)
        => new(
            () => CredentialOptions.TimeoutSpan(CurrentOptions().TimeoutSeconds),
            http => new BitwardenRestClient(http, _clock, _logger),
            injected);

    /// <summary>
    /// Identity of one cached machine-account token: the origins it was
    /// minted against plus the client id and credential env var it was
    /// minted from. A structural-equality record so the cache key is
    /// compile-time checked rather than positionally concatenated.
    /// </summary>
    private sealed record TokenCacheKey(string IdentityUrl, string ApiUrl, string ClientId, string SecretEnvVar);

    private sealed record IssuedLeaseContext(
        BitwardenSecretMapping Mapping,
        TokenCacheKey CredentialKey);
}
