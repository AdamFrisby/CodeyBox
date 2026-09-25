using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OpenBaoPlugin;

/// <summary>
/// CodeyBox lease-shaped secret provider for OpenBao. One project, one
/// plugin: static KV reads plus genuine server-side dynamic leases (issue,
/// renew, explicit revoke) from one OpenBao cluster, mapped read-path by
/// read-path onto CodeyBox secret groups.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted, and issues nothing until <c>Enabled=true</c> in its own
/// section. Authorisation stays with the host manager — a grant decides
/// whether a group applies; this provider only resolves mapped secrets the
/// manager already approved, and a group without a matching grant is never
/// fetched (the manager never calls here for it).</para>
/// <para>Provider credentials come from the host credential chain
/// (environment variables naming an AppRole role/secret pair or a
/// ready-made token); configuration holds only the variable
/// <em>names</em>, never values. Nothing emitted — logs, lease records,
/// exceptions — ever carries a secret value, token, or secret ID.</para>
/// <para>Honest lease surface: dynamic mappings are genuine server-side
/// leases — the server <c>lease_id</c> is the lease identity, renewal is
/// <c>sys/leases/renew</c> while the phase runs, and revocation is
/// <c>sys/leases/revoke</c> with <c>sync=true</c> at teardown, explicit and
/// immediate. Static KV secrets carry no server-side lease — the lease is
/// a client-side validity window: renewal re-fetches (rotation propagates
/// within one window) and revocation is local invalidation plus the
/// orchestrator's teardown scrub of the per-exec environment. See the
/// plugin README for the full statement.</para>
/// </summary>
[CodeyBoxPlugin(
    id: OpenBaoOptions.PluginId,
    displayName: "CodeyBox: OpenBao Secrets",
    minHostApiVersion: "1.0")]
public sealed class OpenBaoSecretProvider : ILeaseCapableSecretProvider, IPluginInitializer, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private readonly LazyCredentialClient<OpenBaoRestClient> _clients;
    private OpenBaoRestClient? _api;
    // Static leases only: renewal re-fetches through the mapping, so the
    // registry remembers which mapping served the lease. Dynamic leases
    // need no entry — renew and revoke derive everything from the handle
    // tail.
    private readonly ConcurrentDictionary<string, OpenBaoSecretMapping> _issued = new(StringComparer.Ordinal);
    private readonly object _tokenLock = new();
    private string? _loggedWarningsSignature;
    private string _tokenFingerprint = string.Empty;
    private string _token = string.Empty;
    private DateTimeOffset _tokenExpiresAt;
    private bool _disposed;

    /// <summary>Production constructor.</summary>
    public OpenBaoSecretProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
        _clients = NewClientCache(null);
    }

    /// <summary>Test constructor: explicit client, config, clock, and environment.</summary>
    internal OpenBaoSecretProvider(
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
    public string ProviderId => OpenBaoOptions.PluginId;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        EnsureClients();
        var warnings = new List<string>();
        var options = OpenBaoOptions.FromConfiguration(context.ScopedConfig, warnings);
        LogOptionWarnings(warnings);
        foreach (var error in options.Validate())
            _logger.LogError("OpenBao plugin configuration: {Error}", error);
        if (!options.Enabled)
        {
            _logger.LogInformation("OpenBao secret provider is disabled (Enabled=false); issuing nothing.");
            return Task.CompletedTask;
        }
        _logger.LogInformation(
            "OpenBao secret provider initialised for {Address} with {Count} secret mapping(s); auth {Auth}.",
            options.Address, options.Mappings.Count,
            options.AppRoleConfigured ? $"AppRole on '{options.AuthMount}'" : $"token env '{options.TokenEnvVar}'");
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
            _logger.LogDebug(ex, "OpenBao CanIssue suppressed an unexpected error; treating as cannot-issue.");
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
            ?? throw new OpenBaoException(
                CredentialFailureKind.Misconfigured,
                $"OpenBao has no mapping for sandbox variable '{secret.SandboxEnvVar}'; the operator never put this secret against this backend.");
        EnsureClients();
        var token = await GetTokenAsync(options, ct).ConfigureAwait(false);
        var skew = CredentialOptions.TokenSkewSpan(options.TokenRefreshSkewSeconds);
        var now = _clock.GetUtcNow();

        LeasedSecretMaterial material;
        if (!string.IsNullOrWhiteSpace(mapping.SecretPath))
        {
            var fetched = await _api!.GetStaticSecretAsync(
                options.Address, token, mapping.SecretPath, mapping.KvVersion,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
            var value = RequireField(fetched.Data, mapping);
            var expiresAt = now
                + CredentialOptions.StaticLeaseWindow(options.StaticLeaseTtlMinutes, requestedTtl);
            var leaseId = OpenBaoLeaseIds.BuildStatic(mapping.SandboxEnvVar);
            material = new LeasedSecretMaterial
            {
                LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
                Value = value,
                ExpiresAt = expiresAt,
                Brokered = false,
            };
            // Static renewal re-fetches through the mapping: remember which
            // mapping served this lease.
            _issued[material.LeaseId] = mapping;
            _logger.LogInformation(
                "OpenBao issued static lease '{LeaseId}' for '{Var}' (scope {Scope}) from '{Path}'.",
                leaseId, mapping.SandboxEnvVar, scope, mapping.SecretPath);
        }
        else
        {
            var issued = await _api!.IssueDynamicCredentialAsync(
                options.Address, token, mapping.DynamicPath,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
            // The server already minted the credential: every rejection
            // below (missing field, unroundtrippable or stillborn lease id)
            // must first try to give it back, or a live credential would be
            // orphaned until its TTL. Best-effort revoke — the failure that
            // sent us here is the one that surfaces.
            string value;
            string leaseId;
            DateTimeOffset expiresAt;
            try
            {
                value = RequireField(issued.Data, mapping);
                try
                {
                    leaseId = OpenBaoLeaseIds.BuildDynamic(mapping.SandboxEnvVar, issued.LeaseId);
                }
                catch (ArgumentException inner)
                {
                    // The server lease id embeds in the handle: a response
                    // carrying one that cannot round-trip is a backend
                    // response failure, typed like every sibling failure.
                    throw new OpenBaoException(
                        CredentialFailureKind.InvalidResponse,
                        "OpenBao dynamic credential carried a lease id that cannot round-trip a lease handle.",
                        inner);
                }
                expiresAt = now + TimeSpan.FromSeconds(issued.LeaseDurationSeconds) - skew;
                if (expiresAt <= now)
                    throw new OpenBaoException(
                        CredentialFailureKind.InvalidResponse,
                        $"OpenBao dynamic credential at '{mapping.DynamicPath}' arrived already expired.");
            }
            catch (OpenBaoException)
            {
                await _api!.TryRevokeLeaseAsync(
                    options.Address, token, issued.LeaseId, options.RevokeSync, ct).ConfigureAwait(false);
                throw;
            }
            material = new LeasedSecretMaterial
            {
                LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
                Value = value,
                ExpiresAt = expiresAt,
                Brokered = false,
            };
            _logger.LogInformation(
                "OpenBao issued dynamic lease '{LeaseId}' for '{Var}' (scope {Scope}, server lease '{ServerLease}', renewable {Renewable}) expiring {ExpiresAt}.",
                leaseId, mapping.SandboxEnvVar, scope, issued.LeaseId, issued.Renewable, expiresAt);
        }

        return material;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> RenewAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);
        var options = RequireUsableOptions();
        EnsureClients();
        var skew = CredentialOptions.TokenSkewSpan(options.TokenRefreshSkewSeconds);

        if (parsed.Kind == OpenBaoLeaseIds.LeaseKind.Static)
        {
            var mapping = _issued.TryGetValue(leaseId, out var known)
                ? known
                : RebuildMapping(leaseId, options, parsed);
            var token = await GetTokenAsync(options, ct).ConfigureAwait(false);
            // Static renewal re-fetches so rotation propagates within one
            // window; the fresh value reaches the guest on next provisioning.
            var fetched = await _api!.GetStaticSecretAsync(
                options.Address, token, mapping.SecretPath, mapping.KvVersion,
                options.MaxResponseBytes, ct).ConfigureAwait(false);
            RequireField(fetched.Data, mapping);
            var expiresAt = _clock.GetUtcNow()
                + CredentialOptions.StaticLeaseWindow(options.StaticLeaseTtlMinutes, TimeSpan.Zero);
            // Re-register the (possibly rebuilt) mapping so later renewals
            // keep resolving the read path.
            _issued[leaseId] = mapping;
            _logger.LogDebug("OpenBao renewed static lease '{LeaseId}'.", leaseId);
            return expiresAt;
        }

        // Dynamic renewal needs only the server lease id, which the handle
        // tail carries — no current mapping required, so a live lease
        // stays renewable even if its mapping was later removed.
        var dynamicToken = await GetTokenAsync(options, ct).ConfigureAwait(false);
        var seconds = await _api!.RenewLeaseAsync(
            options.Address, dynamicToken, parsed.Tail,
            options.RenewIncrementSeconds, options.MaxResponseBytes, ct).ConfigureAwait(false);
        var renewed = _clock.GetUtcNow() + TimeSpan.FromSeconds(seconds) - skew;
        _logger.LogDebug("OpenBao renewed dynamic lease '{LeaseId}' to {ExpiresAt}.", leaseId, renewed);
        return renewed;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);

        if (parsed.Kind == OpenBaoLeaseIds.LeaseKind.Static)
        {
            // Static KV secrets carry no server-side lease: local
            // invalidation (dropping the registry entry) plus the
            // orchestrator's teardown scrub of the per-exec environment is
            // the complete revocation. Needs no options, no network, and
            // no current mapping — always succeeds and is idempotent by
            // construction.
            _issued.TryRemove(leaseId, out _);
            _logger.LogInformation("OpenBao invalidated static lease '{LeaseId}'.", leaseId);
            return;
        }

        var options = RequireConfiguredOptions();
        EnsureClients();
        var token = await GetTokenAsync(options, ct).ConfigureAwait(false);
        // The handle tail carries the server lease id, so revocation
        // addresses the lease directly — a removed mapping can never
        // strand a live server credential.
        await _api!.RevokeLeaseAsync(
            options.Address, token, parsed.Tail,
            options.RevokeSync, ct).ConfigureAwait(false);
        _logger.LogInformation("OpenBao revoked dynamic lease '{LeaseId}'.", leaseId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Drop the cached provider token from memory; only provider-built
        // clients are disposed here (test-injected clients stay owned by
        // their test).
        lock (_tokenLock)
        {
            _token = string.Empty;
            _tokenFingerprint = string.Empty;
            _tokenExpiresAt = default;
        }
        _clients.Dispose();
    }

    internal OpenBaoOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        if (section is null)
            return new OpenBaoOptions();
        var warnings = new List<string>();
        var options = OpenBaoOptions.FromConfiguration(section, warnings);
        LogOptionWarnings(warnings);
        return options;
    }

    /// <summary>
    /// Options re-bind on every call for hot-reload, so a parse warning on
    /// reload (a typo silently falling back to a default — including
    /// silently disabling the plugin) must surface too. Logged once per
    /// distinct warning set so a steady bad state does not spam the log.
    /// </summary>
    private void LogOptionWarnings(List<string> warnings)
    {
        var signature = string.Join('\n', warnings);
        if (signature.Length == 0)
        {
            Volatile.Write(ref _loggedWarningsSignature, null);
            return;
        }
        if (string.Equals(Volatile.Read(ref _loggedWarningsSignature), signature, StringComparison.Ordinal))
            return;
        Volatile.Write(ref _loggedWarningsSignature, signature);
        foreach (var warning in warnings)
            _logger.LogWarning("OpenBao plugin option: {Warning}", warning);
    }

    /// <summary>
    /// Resolves the provider credential from the host credential chain:
    /// AppRole login when both env-var names are configured and populated
    /// (the minted client token is cached until its server expiry), else
    /// the env var holding a ready-made token — re-read every call so an
    /// external rotation propagates without a restart.
    /// </summary>
    private async Task<string> GetTokenAsync(OpenBaoOptions options, CancellationToken ct)
    {
        if (options.AppRoleConfigured)
        {
            var roleId = _env(options.AppRoleIdEnvVar);
            var secretId = _env(options.AppRoleSecretIdEnvVar);
            if (!string.IsNullOrWhiteSpace(roleId) && !string.IsNullOrWhiteSpace(secretId))
            {
                var fingerprint = string.Join('|',
                    options.Address, options.AuthMount,
                    options.AppRoleIdEnvVar, options.AppRoleSecretIdEnvVar);
                lock (_tokenLock)
                {
                    if (!string.IsNullOrEmpty(_token)
                        && string.Equals(_tokenFingerprint, fingerprint, StringComparison.Ordinal)
                        && _clock.GetUtcNow() < _tokenExpiresAt)
                        return _token;
                }
                EnsureClients();
                var skew = CredentialOptions.TokenSkewSpan(options.TokenRefreshSkewSeconds);
                var (token, expiresAt) = await _api!.LoginAppRoleAsync(
                    options.Address, options.AuthMount, roleId, secretId, skew, ct).ConfigureAwait(false);
                lock (_tokenLock)
                {
                    _tokenFingerprint = fingerprint;
                    _token = token;
                    _tokenExpiresAt = expiresAt;
                }
                return token;
            }
        }

        var name = options.TokenEnvVar;
        var direct = string.IsNullOrWhiteSpace(name) ? null : _env(name);
        if (string.IsNullOrWhiteSpace(direct))
            throw new OpenBaoException(
                CredentialFailureKind.Misconfigured,
                $"OpenBao provider credentials are not set: env '{options.AppRoleIdEnvVar}'/'{options.AppRoleSecretIdEnvVar}' (AppRole) and " +
                (string.IsNullOrWhiteSpace(name)
                    ? "no token variable is configured."
                    : $"env '{name}' are all empty. Provision them from the host credential chain."));
        return direct;
    }

    private static string RequireField(IReadOnlyDictionary<string, string> data, OpenBaoSecretMapping mapping)
    {
        if (!data.TryGetValue(mapping.DataField, out var value) || string.IsNullOrEmpty(value))
            throw new OpenBaoException(
                CredentialFailureKind.Misconfigured,
                $"OpenBao mapping for '{mapping.SandboxEnvVar}': the response data has no field '{mapping.DataField}'; check DataField against the engine's data shape.");
        return value;
    }

    private OpenBaoOptions RequireUsableOptions()
        => CredentialOptions.RequireUsable(CurrentOptions(), OpenBaoException.BackendName, OpenBaoException.Create);

    /// <summary>
    /// The revocation gate: options must be valid but need not be enabled —
    /// disabling the plugin is a natural response to a suspect backend and
    /// must not strand already-issued leases until their server TTL.
    /// </summary>
    private OpenBaoOptions RequireConfiguredOptions()
        => CredentialOptions.RequireValid(CurrentOptions(), OpenBaoException.BackendName, OpenBaoException.Create);

    private static OpenBaoSecretMapping? FindMapping(OpenBaoOptions options, ProjectSandboxSecret secret)
    {
        foreach (var mapping in options.Mappings)
        {
            if (!string.Equals(mapping.SandboxEnvVar, secret.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(mapping.Group)
                && !string.Equals(mapping.Group, secret.Group, StringComparison.Ordinal))
                continue;
            var hasStatic = !string.IsNullOrWhiteSpace(mapping.SecretPath);
            var hasDynamic = !string.IsNullOrWhiteSpace(mapping.DynamicPath);
            if (hasStatic == hasDynamic || string.IsNullOrWhiteSpace(mapping.DataField))
                continue;
            return mapping;
        }
        return null;
    }

    private static OpenBaoSecretMapping RebuildMapping(
        string leaseId, OpenBaoOptions options, OpenBaoLeaseIds.ParsedLeaseId parsed)
    {
        // Restart path for static leases: the in-memory issue registry is
        // gone, but the lease id carries the sandbox variable — enough to
        // re-resolve today's mapping. (Dynamic renew/revoke never rebuild:
        // the handle tail already carries the server lease id.) The grant
        // itself was verified at issue time and is not re-decided here;
        // renewal extends the same lease.
        OpenBaoSecretMapping? mapping = null;
        foreach (var candidate in options.Mappings)
        {
            if (!string.Equals(candidate.SandboxEnvVar, parsed.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            if (string.IsNullOrWhiteSpace(candidate.SecretPath)
                || !string.IsNullOrWhiteSpace(candidate.DynamicPath)
                || string.IsNullOrWhiteSpace(candidate.DataField))
                continue;
            mapping = candidate;
            break;
        }
        if (mapping is null)
            throw new OpenBaoException(
                CredentialFailureKind.Misconfigured,
                $"OpenBao lease '{leaseId}' names sandbox variable '{parsed.SandboxEnvVar}' with no current static mapping; restore the mapping or let the lease window expire.");
        return mapping;
    }

    private static OpenBaoLeaseIds.ParsedLeaseId ParseOurs(string leaseId)
        => LeaseHandles.ParseOrThrow<OpenBaoLeaseIds.ParsedLeaseId>(
            leaseId, OpenBaoException.BackendName, OpenBaoLeaseIds.TryParse, OpenBaoException.Create);

    private void EnsureClients() => _api = _clients.Get();

    private LazyCredentialClient<OpenBaoRestClient> NewClientCache(HttpClient? injected)
        => new(
            () => CredentialOptions.TimeoutSpan(CurrentOptions().TimeoutSeconds),
            http => new OpenBaoRestClient(http, _clock, _logger),
            injected);
}
