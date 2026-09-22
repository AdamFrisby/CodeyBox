using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
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
/// (environment variables holding machine-account client secrets);
/// configuration holds only the variable <em>names</em>, never values —
/// plus the client ids, which are not secrets. Nothing emitted — logs,
/// lease records, exceptions — ever carries a secret value, an access
/// token, or a client secret.</para>
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
    private HttpClient? _http;
    private bool _ownsHttp;
    private BitwardenRestClient? _api;
    private readonly object _clientLock = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IssuedLeaseContext> _issued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BitwardenAccessToken> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Lease handles revoked in this process. Renewal of one fails loudly
    /// instead of silently re-fetching — revocation is verified, not
    /// assumed. The set is memory-only, so a restart re-resolves the
    /// mapping (the documented trade-off of holding no server lease).
    /// Bounded: entries are lease-id sized and one per revoked lease.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _revokedLeases = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Production constructor.</summary>
    public BitwardenSecretProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal BitwardenSecretProvider(
        HttpClient http,
        IConfigurationSection config,
        TimeProvider? clock = null,
        Func<string, string?>? env = null,
        ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _testConfig = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _logger = log ?? NullLogger.Instance;
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
                BitwardenFailureKind.Misconfigured,
                $"Bitwarden has no mapping for sandbox variable '{secret.SandboxEnvVar}'; the operator never put this secret against this backend.");
        EnsureClients();

        var token = await GetAccessTokenAsync(options, mapping, ct).ConfigureAwait(false);
        var value = await FetchValueAsync(options, mapping, token.Token, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(value))
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden secret '{secret.SandboxEnvVar}' returned an empty value.");
        var expiresAt = LeaseExpiry(options, token, requestedTtl);
        var leaseId = BitwardenLeaseIds.BuildStatic(mapping.SandboxEnvVar);
        _issued[leaseId] = new IssuedLeaseContext(mapping, CredentialKey(options, mapping), expiresAt);
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
                BitwardenFailureKind.Misconfigured,
                $"Bitwarden lease '{leaseId}' was revoked in this process and cannot be renewed.");
        var context = _issued.TryGetValue(leaseId, out var known)
            ? known
            : RebuildContext(leaseId, options, parsed);

        // Static renewal re-authenticates (reusing the cached token while
        // it is still safely valid) and re-fetches, so an edit or rotation
        // propagates within one window; the fresh value reaches the guest
        // on next provisioning.
        var token = await GetAccessTokenAsync(options, context.Mapping, ct).ConfigureAwait(false);
        await FetchValueAsync(options, context.Mapping, token.Token, ct).ConfigureAwait(false);
        var expiresAt = LeaseExpiry(options, token, TimeSpan.Zero);
        _issued[leaseId] = context with { ExpiresAt = expiresAt };
        _logger.LogDebug("Bitwarden renewed lease '{LeaseId}'.", leaseId);
        return expiresAt;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string leaseId, CancellationToken ct = default)
    {
        _ = ParseOurs(leaseId);
        var options = RequireUsableOptions();
        EnsureClients();
        await Task.CompletedTask.ConfigureAwait(false);

        // Secrets Manager secrets carry no server-side lease and the
        // client-credentials flow exposes no token-revocation endpoint:
        // revocation is dropping the cached access token (any sibling lease
        // re-authenticates transparently on its next renewal), local
        // invalidation refusing future renewal in this process, plus the
        // orchestrator's teardown scrub of the per-exec environment.
        // Always succeeds and is idempotent by construction.
        if (_issued.TryRemove(leaseId, out var context))
            _tokens.TryRemove(context.CredentialKey, out _);
        _revokedLeases[leaseId] = true;
        _logger.LogInformation("Bitwarden invalidated lease '{LeaseId}'.", leaseId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _issued.Clear();
        _tokens.Clear();
        _tokenLock.Dispose();
        if (_ownsHttp)
        {
            try { _http?.Dispose(); } catch (Exception) { }
        }
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
        var clientId = string.IsNullOrWhiteSpace(mapping.ClientId)
            ? options.ClientId.Trim()
            : mapping.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                $"Bitwarden mapping for '{mapping.SandboxEnvVar}' names no machine-account client id; set mapping ClientId or provider ClientId.");
        var secretEnvName = string.IsNullOrWhiteSpace(mapping.ClientSecretEnvVar)
            ? options.ClientSecretEnvVar
            : mapping.ClientSecretEnvVar;
        var clientSecret = string.IsNullOrWhiteSpace(secretEnvName) ? null : _env(secretEnvName);
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                $"Bitwarden client-secret env '{secretEnvName}' is empty. Provision a machine-account client secret " +
                $"with access to the mapped projects from the host credential chain.");
        var key = CredentialKey(clientId, secretEnvName!);

        if (_tokens.TryGetValue(key, out var cached) && TokenUsable(cached, options))
            return cached;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tokens.TryGetValue(key, out cached) && TokenUsable(cached, options))
                return cached;
            EnsureClients();
            var fresh = await _api!.AuthenticateAsync(
                options.IdentityUrl, clientId, clientSecret, options.MaxResponseBytes, ct).ConfigureAwait(false);
            _tokens[key] = fresh;
            return fresh;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool TokenUsable(BitwardenAccessToken token, BitwardenOptions options)
        => _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(options.TokenRefreshSkewSeconds, 0)) < token.ExpiresAt;

    private async Task<string> FetchValueAsync(
        BitwardenOptions options, BitwardenSecretMapping mapping, string accessToken, CancellationToken ct)
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
            options.ApiUrl, accessToken, organizationId, mapping.SecretKey.Trim(), projectId,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
        return await _api.GetSecretAsync(
            options.ApiUrl, accessToken, secretId, projectId,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
    }

    private DateTimeOffset LeaseExpiry(
        BitwardenOptions options, BitwardenAccessToken token, TimeSpan requestedTtl)
    {
        var now = _clock.GetUtcNow();
        var configured = TimeSpan.FromMinutes(Math.Clamp(options.StaticLeaseTtlMinutes, 1, 1440));
        var window = requestedTtl > TimeSpan.Zero && requestedTtl < configured ? requestedTtl : configured;
        var tokenRemaining = token.ExpiresAt - now - TimeSpan.FromSeconds(Math.Max(options.TokenRefreshSkewSeconds, 0));
        if (tokenRemaining <= TimeSpan.Zero)
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                "Bitwarden access token carries no usable lifetime; refusing to cache a secret beyond its lease.");
        return now + (window < tokenRemaining ? window : tokenRemaining);
    }

    private string CredentialKey(BitwardenOptions options, BitwardenSecretMapping mapping)
    {
        var clientId = string.IsNullOrWhiteSpace(mapping.ClientId)
            ? options.ClientId.Trim()
            : mapping.ClientId.Trim();
        var secretEnvName = string.IsNullOrWhiteSpace(mapping.ClientSecretEnvVar)
            ? options.ClientSecretEnvVar
            : mapping.ClientSecretEnvVar;
        return CredentialKey(clientId, secretEnvName);
    }

    private static string CredentialKey(string clientId, string secretEnvName)
        => $"{clientId}\0{secretEnvName}";

    private BitwardenOptions RequireUsableOptions()
    {
        var options = CurrentOptions();
        if (!options.Enabled)
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                "Bitwarden provider is disabled (Enabled=false); enable it to issue.");
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                $"Bitwarden configuration is invalid: {errors[0]}");
        return options;
    }

    private static BitwardenSecretMapping? FindMapping(BitwardenOptions options, ProjectSandboxSecret secret)
    {
        foreach (var mapping in options.Mappings)
        {
            if (!string.Equals(mapping.SandboxEnvVar, secret.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(mapping.Group)
                && !string.Equals(mapping.Group, secret.Group, StringComparison.Ordinal))
                continue;
            return mapping;
        }
        return null;
    }

    private IssuedLeaseContext RebuildContext(
        string leaseId, BitwardenOptions options, BitwardenLeaseIds.ParsedLeaseId parsed)
    {
        // Restart path: the in-memory issue registry is gone, but the lease
        // id carries the sandbox variable — enough to re-resolve today's
        // mapping. The grant itself was verified at issue time and is not
        // re-decided here; renewal extends the same lease.
        BitwardenSecretMapping? mapping = null;
        foreach (var candidate in options.Mappings)
        {
            if (!string.Equals(candidate.SandboxEnvVar, parsed.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            mapping = candidate;
            break;
        }
        if (mapping is null)
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                $"Bitwarden lease '{leaseId}' names sandbox variable '{parsed.SandboxEnvVar}' with no current mapping; restore the mapping.");
        return new IssuedLeaseContext(mapping, CredentialKey(options, mapping), _clock.GetUtcNow());
    }

    private static BitwardenLeaseIds.ParsedLeaseId ParseOurs(string leaseId)
    {
        if (!BitwardenLeaseIds.TryParse(leaseId, out var parsed))
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                $"Lease '{leaseId ?? string.Empty}' is not a Bitwarden lease handle.");
        return parsed;
    }

    private static TimeSpan ClientTimeout(BitwardenOptions options) =>
        TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

    private void EnsureClients()
    {
        if (_api is not null)
            return;
        lock (_clientLock)
        {
            if (_api is null)
            {
                if (_http is null)
                {
                    _http = BitwardenHttpClients.Create(ClientTimeout(CurrentOptions()));
                    _ownsHttp = true;
                }
                try
                {
                    _http.Timeout = ClientTimeout(CurrentOptions());
                }
                catch (InvalidOperationException)
                {
                    // A request is already in flight; keep the existing timeout.
                }
                _api = new BitwardenRestClient(_http, _clock, _logger);
            }
        }
    }

    private sealed record IssuedLeaseContext(
        BitwardenSecretMapping Mapping,
        string CredentialKey,
        DateTimeOffset ExpiresAt);
}
