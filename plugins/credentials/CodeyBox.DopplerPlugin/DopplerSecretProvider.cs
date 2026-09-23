using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.DopplerPlugin;

/// <summary>
/// CodeyBox lease-shaped secret provider for Doppler. One project, one
/// plugin: secrets from one Doppler workplace, mapped project/config by
/// project/config onto CodeyBox secret groups.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted, and issues nothing until <c>Enabled=true</c> in its own
/// section. Authorisation stays with the host manager — a grant decides
/// whether a group applies; this provider only resolves mapped secrets the
/// manager already approved, and a group without a matching grant is never
/// fetched (the manager never calls here for it).</para>
/// <para>Provider credentials come from the host credential chain
/// (environment variables naming restricted service tokens or a fresh OIDC
/// token); configuration holds only the variable <em>names</em>, never
/// values. Nothing emitted — logs, lease records, exceptions — ever
/// carries a secret value or token.</para>
/// <para>Honest lease surface: short-lived service-account identity tokens
/// are genuine server-side leases (server <c>expires_at</c>, server revoke
/// via <c>/v3/auth/revoke</c>). Static secrets fetched with restricted
/// service tokens have no server-side lease to revoke — the lease is a
/// client-side validity window: renewal re-fetches (rotation propagates
/// within one window) and revocation is local invalidation plus the
/// orchestrator's teardown scrub of the per-exec environment. See the
/// plugin README for the full statement.</para>
/// </summary>
[CodeyBoxPlugin(
    id: DopplerOptions.PluginId,
    displayName: "CodeyBox: Doppler Secrets",
    minHostApiVersion: "1.0")]
public sealed class DopplerSecretProvider : ILeaseCapableSecretProvider, IPluginInitializer, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private bool _ownsHttp;
    private DopplerRestClient? _api;
    private readonly object _clientLock = new();
    private readonly ConcurrentDictionary<string, IssuedLeaseContext> _issued = new(StringComparer.Ordinal);

    /// <summary>
    /// Identity handles revoked in this process. A retry after a verified
    /// server revoke succeeds locally without re-addressing a dead token
    /// (the revoke endpoint treats unknown tokens as success too); the set
    /// is memory-only, so a restart still fails loudly rather than
    /// assuming anything. Bounded indirectly: entries are lease-id sized
    /// and one per issued identity lease.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _revokedIdentities = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Production constructor.</summary>
    public DopplerSecretProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal DopplerSecretProvider(
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
    public string ProviderId => DopplerOptions.PluginId;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        EnsureClients();
        var warnings = new List<string>();
        var options = DopplerOptions.FromConfiguration(context.ScopedConfig, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("Doppler plugin option: {Warning}", warning);
        foreach (var error in options.Validate())
            _logger.LogError("Doppler plugin configuration: {Error}", error);
        if (!options.Enabled)
        {
            _logger.LogInformation("Doppler secret provider is disabled (Enabled=false); issuing nothing.");
            return Task.CompletedTask;
        }
        _logger.LogInformation(
            "Doppler secret provider initialised for {Api} with {Count} secret mapping(s){Identity}.",
            options.ApiUrl, options.Mappings.Count,
            options.IdentityConfigured ? " and service-account identity exchange" : string.Empty);
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
            _logger.LogDebug(ex, "Doppler CanIssue suppressed an unexpected error; treating as cannot-issue.");
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
            ?? throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                $"Doppler has no mapping for sandbox variable '{secret.SandboxEnvVar}'; the operator never put this secret against this backend.");
        EnsureClients();
        var (project, config, secretName) = ResolveTriple(options, mapping);

        LeasedSecretMaterial material;
        IssuedLeaseContext context;
        if (UseIdentity(options, mapping))
        {
            var minted = await ExchangeIdentityAsync(options, ct).ConfigureAwait(false);
            var value = await _api!.GetSecretAsync(
                options.ApiUrl, minted.Token, project, config, secretName,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
                throw new DopplerException(
                    CredentialFailureKind.InvalidResponse,
                    $"Doppler secret '{secretName}' returned an empty value.");
            var leaseId = DopplerLeaseIds.BuildIdentity(mapping.SandboxEnvVar);
            context = new IssuedLeaseContext(
                DopplerLeaseIds.LeaseKind.Identity, mapping, project, config, secretName,
                minted.Token, minted.ExpiresAt, minted.ExpiresAt);
            material = new LeasedSecretMaterial
            {
                LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
                Value = value,
                ExpiresAt = minted.ExpiresAt,
                Brokered = false,
            };
            _logger.LogInformation(
                "Doppler issued identity lease '{LeaseId}' for '{Var}' (scope {Scope}) expiring {ExpiresAt}.",
                leaseId, mapping.SandboxEnvVar, scope, minted.ExpiresAt);
        }
        else
        {
            var token = ReadServiceToken(options, mapping);
            var value = await _api!.GetSecretAsync(
                options.ApiUrl, token, project, config, secretName,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
                throw new DopplerException(
                    CredentialFailureKind.InvalidResponse,
                    $"Doppler secret '{secretName}' returned an empty value.");
            var window = StaticWindow(options, requestedTtl);
            var expiresAt = _clock.GetUtcNow() + window;
            var leaseId = DopplerLeaseIds.BuildStatic(mapping.SandboxEnvVar);
            context = new IssuedLeaseContext(
                DopplerLeaseIds.LeaseKind.Static, mapping, project, config, secretName,
                IdentityToken: null, TokenExpiresAt: default, expiresAt);
            material = new LeasedSecretMaterial
            {
                LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
                Value = value,
                ExpiresAt = expiresAt,
                Brokered = false,
            };
            _logger.LogInformation(
                "Doppler issued static lease '{LeaseId}' for '{Var}' (scope {Scope}) from project '{Project}' config '{Config}'.",
                leaseId, mapping.SandboxEnvVar, scope, project, config);
        }

        _issued[material.LeaseId] = context;
        return material;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> RenewAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);
        var options = RequireUsableOptions();
        EnsureClients();
        var context = _issued.TryGetValue(leaseId, out var known)
            ? known
            : RebuildContext(leaseId, options, parsed);

        if (parsed.Kind == DopplerLeaseIds.LeaseKind.Static)
        {
            // Static renewal re-fetches so rotation propagates within one
            // window; the fresh value reaches the guest on next provisioning.
            var token = ReadServiceToken(options, context.Mapping);
            await _api!.GetSecretAsync(
                options.ApiUrl, token, context.Project, context.Config, context.SecretName,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
            var expiresAt = _clock.GetUtcNow() + StaticWindow(options, TimeSpan.Zero);
            _issued[leaseId] = context with { ExpiresAt = expiresAt };
            _logger.LogDebug("Doppler renewed static lease '{LeaseId}'.", leaseId);
            return expiresAt;
        }

        // Identity renewal re-fetches while the minted token is still valid.
        // Past its lifetime a fresh OIDC exchange is required — and the
        // OIDC token itself is short-lived, so renewal past that point
        // fails loudly as infrastructure (never a diff verdict) while the
        // sweep keeps retrying. See the README's honest lease statement.
        var skew = TimeSpan.FromSeconds(Math.Clamp(options.TokenRefreshSkewSeconds, 0, 3600));
        string bearer = string.Empty;
        DateTimeOffset tokenExpiry = default;
        if (!string.IsNullOrEmpty(context.IdentityToken)
            && _clock.GetUtcNow() + skew < context.TokenExpiresAt)
        {
            bearer = context.IdentityToken;
            tokenExpiry = context.TokenExpiresAt;
        }
        else
        {
            var minted = await ExchangeIdentityAsync(options, ct).ConfigureAwait(false);
            bearer = minted.Token;
            tokenExpiry = minted.ExpiresAt;
        }
        await _api!.GetSecretAsync(
            options.ApiUrl, bearer, context.Project, context.Config, context.SecretName,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
        _issued[leaseId] = context with
        {
            IdentityToken = bearer,
            TokenExpiresAt = tokenExpiry,
            ExpiresAt = tokenExpiry,
        };
        _logger.LogDebug("Doppler renewed identity lease '{LeaseId}' to {ExpiresAt}.", leaseId, tokenExpiry);
        return tokenExpiry;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);
        var options = RequireUsableOptions();
        EnsureClients();

        if (parsed.Kind == DopplerLeaseIds.LeaseKind.Static)
        {
            // Static Doppler secrets carry no server-side lease: local
            // invalidation (dropping the registry entry) plus the
            // orchestrator's teardown scrub of the per-exec environment is
            // the complete revocation. Always succeeds and is idempotent by
            // construction.
            _issued.TryRemove(leaseId, out _);
            _logger.LogInformation("Doppler invalidated static lease '{LeaseId}'.", leaseId);
            return;
        }

        var context = _issued.TryGetValue(leaseId, out var known)
            ? known
            : RebuildContext(leaseId, options, parsed);
        if (string.IsNullOrEmpty(context.IdentityToken))
        {
            if (_revokedIdentities.ContainsKey(leaseId))
            {
                // Verified server revoke earlier in this process; the dead
                // token cannot be addressed again, and the endpoint treats
                // unknown tokens as success — so this is complete, not
                // assumed.
                _logger.LogInformation(
                    "Doppler identity lease '{LeaseId}' was already revoked in this process.", leaseId);
                return;
            }
            // Restart path: the minted token lived only in host memory and
            // is gone with it, so there is nothing addressable to revoke.
            // Failing loudly (not silently succeeding) keeps the
            // revocation honest: the sweep retries and then parks the lease
            // visibly, while the server-side expiry — short by design —
            // bounds the exposure. Never fake a revocation.
            throw new DopplerException(
                CredentialFailureKind.Unreachable,
                $"Doppler identity lease '{leaseId}' holds no minted token in this process (orchestrator restarted); " +
                $"the server-side token expires at {context.TokenExpiresAt:O} and cannot be revoked from here.");
        }
        var doomed = context.IdentityToken;
        await _api!.RevokeTokenAsync(options.ApiUrl, doomed, ct).ConfigureAwait(false);
        // Drop the token only after the server confirmed: a failed revoke
        // keeps the registry entry so the sweep can retry with the same
        // token instead of losing it.
        _issued.TryRemove(leaseId, out _);
        _revokedIdentities[leaseId] = true;
        _logger.LogInformation("Doppler revoked identity lease '{LeaseId}'.", leaseId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Drop minted identity tokens from memory; only provider-built
        // clients are disposed here (test-injected clients stay owned by
        // their test).
        foreach (var key in _issued.Keys)
        {
            if (_issued.TryGetValue(key, out var context) && context.IdentityToken is not null)
                _issued[key] = context with { IdentityToken = null };
        }
        if (_ownsHttp)
        {
            try { _http?.Dispose(); } catch (Exception) { }
        }
    }

    internal DopplerOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new DopplerOptions()
            : DopplerOptions.FromConfiguration(section);
    }

    private async Task<DopplerIdentityToken> ExchangeIdentityAsync(DopplerOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.IdentityId) || string.IsNullOrWhiteSpace(options.OidcTokenEnvVar))
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                "Doppler service-account identity is not configured (IdentityId and OidcTokenEnvVar must both be set).");
        var oidcToken = _env(options.OidcTokenEnvVar);
        if (string.IsNullOrWhiteSpace(oidcToken))
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                $"Doppler OIDC token env '{options.OidcTokenEnvVar}' is empty. Provision a fresh OIDC token from the host credential chain.");
        EnsureClients();
        var skew = TimeSpan.FromSeconds(Math.Clamp(options.TokenRefreshSkewSeconds, 0, 3600));
        var minted = await _api!.ExchangeOidcAsync(
            options.ApiUrl, options.IdentityId, oidcToken, skew,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
        if (minted.ExpiresAt <= _clock.GetUtcNow())
            throw new DopplerException(
                CredentialFailureKind.InvalidResponse,
                "Doppler OIDC exchange returned an already-expired token.");
        return minted;
    }

    private string ReadServiceToken(DopplerOptions options, DopplerSecretMapping mapping)
    {
        var name = string.IsNullOrWhiteSpace(mapping.TokenEnvVar)
            ? options.ServiceTokenEnvVar
            : mapping.TokenEnvVar;
        var token = string.IsNullOrWhiteSpace(name) ? null : _env(name);
        if (string.IsNullOrWhiteSpace(token))
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                $"Doppler service token env '{name}' is empty. Provision a restricted service token " +
                $"scoped to the mapped project/config from the host credential chain.");
        return token;
    }

    private DopplerOptions RequireUsableOptions()
    {
        var options = CurrentOptions();
        if (!options.Enabled)
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                "Doppler provider is disabled (Enabled=false); enable it to issue.");
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                $"Doppler configuration is invalid: {errors[0]}");
        return options;
    }

    private static bool UseIdentity(DopplerOptions options, DopplerSecretMapping mapping)
    {
        // An explicit per-mapping service token always wins for that
        // mapping: it names the least-privilege credential for exactly one
        // project/config. Otherwise the configured identity path applies.
        if (!string.IsNullOrWhiteSpace(mapping.TokenEnvVar))
            return false;
        return options.IdentityConfigured;
    }

    private static (string Project, string Config, string SecretName) ResolveTriple(
        DopplerOptions options, DopplerSecretMapping mapping)
    {
        var project = string.IsNullOrWhiteSpace(mapping.Project) ? options.DefaultProject : mapping.Project;
        var config = string.IsNullOrWhiteSpace(mapping.Config) ? options.DefaultConfig : mapping.Config;
        var secretName = string.IsNullOrWhiteSpace(mapping.SecretName) ? mapping.SandboxEnvVar : mapping.SecretName;
        return (project.Trim(), config.Trim(), secretName.Trim());
    }

    private static DopplerSecretMapping? FindMapping(DopplerOptions options, ProjectSandboxSecret secret)
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
        string leaseId, DopplerOptions options, DopplerLeaseIds.ParsedLeaseId parsed)
    {
        // Restart path: the in-memory issue registry is gone, but the lease
        // id carries the sandbox variable — enough to re-resolve today's
        // mapping. The grant itself was verified at issue time and is not
        // re-decided here; renewal extends the same lease. Minted identity
        // tokens are NOT recoverable (memory-only by design): identity
        // renewal re-exchanges, identity revocation fails loudly.
        DopplerSecretMapping? mapping = null;
        foreach (var candidate in options.Mappings)
        {
            if (!string.Equals(candidate.SandboxEnvVar, parsed.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            mapping = candidate;
            break;
        }
        if (mapping is null)
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                $"Doppler lease '{leaseId}' names sandbox variable '{parsed.SandboxEnvVar}' with no current mapping; restore the mapping or revoke server-side.");
        var (project, config, secretName) = ResolveTriple(options, mapping);
        return new IssuedLeaseContext(
            parsed.Kind, mapping, project, config, secretName,
            IdentityToken: null, TokenExpiresAt: default, _clock.GetUtcNow());
    }

    private static DopplerLeaseIds.ParsedLeaseId ParseOurs(string leaseId)
    {
        if (!DopplerLeaseIds.TryParse(leaseId, out var parsed))
            throw new DopplerException(
                CredentialFailureKind.Misconfigured,
                $"Lease '{leaseId ?? string.Empty}' is not a Doppler lease handle.");
        return parsed;
    }

    private static TimeSpan StaticWindow(DopplerOptions options, TimeSpan requestedTtl)
    {
        var configured = TimeSpan.FromMinutes(Math.Clamp(options.StaticLeaseTtlMinutes, 1, 1440));
        return requestedTtl > TimeSpan.Zero && requestedTtl < configured ? requestedTtl : configured;
    }

    private static TimeSpan ClientTimeout(DopplerOptions options) =>
        TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

    private void EnsureClients()
    {
        if (_api is not null)
            return;
        lock (_clientLock)
        {
            if (_api is not null)
                return;
            if (_http is null)
            {
                _http = CredentialHttp.CreateNoRedirectClient(ClientTimeout(CurrentOptions()));
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
            _api = new DopplerRestClient(_http, _clock, _logger);
        }
    }

    private sealed record IssuedLeaseContext(
        DopplerLeaseIds.LeaseKind Kind,
        DopplerSecretMapping Mapping,
        string Project,
        string Config,
        string SecretName,
        string? IdentityToken,
        DateTimeOffset TokenExpiresAt,
        DateTimeOffset ExpiresAt);
}
