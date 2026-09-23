using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// CodeyBox lease-shaped secret provider for Infisical. One project, one
/// plugin: ordinary retrieval (static secrets and dynamic-secret leases)
/// plus Agent Proxy brokering (the loopback injecting proxy), kept
/// separable so an operator can use either.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted, and issues nothing until <c>Enabled=true</c> in its own
/// section. Authorisation stays with the host manager — a grant decides
/// whether a group applies; this provider only resolves mapped secrets the
/// manager already approved, and a group without a matching grant is never
/// fetched (the manager never calls here for it).</para>
/// <para>Provider credentials come from the host credential chain
/// (environment variables naming the machine identity or a ready-made
/// token); configuration holds only the variable <em>names</em>, never
/// values. Nothing emitted — logs, lease records, exceptions, endpoint
/// URLs — ever carries a secret value or token.</para>
/// <para>Honest lease surface: dynamic leases are genuine server-side
/// leases (Infisical id, server renew, server revoke). Static secrets have
/// no server-side lease to revoke — the lease is a client-side validity
/// window over a versioned secret: renewal re-fetches (rotation propagates
/// within one window) and revocation is local invalidation plus the
/// orchestrator's teardown scrub of the per-exec environment. See the
/// plugin README for the full statement.</para>
/// </summary>
[CodeyBoxPlugin(
    id: InfisicalOptions.PluginId,
    displayName: "CodeyBox: Infisical Secrets",
    minHostApiVersion: "1.0")]
public sealed class InfisicalSecretProvider : ILeaseCapableSecretProvider, IPluginInitializer, IDisposable
{
    /// <summary>How long an externally supplied token is cached before the env var is re-read.</summary>
    internal const int ExternalTokenCacheMinutes = 5;

    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private HttpClient? _brokerForward;
    private bool _ownsHttp;
    private bool _ownsBrokerForward;
    private InfisicalRestClient? _api;
    private InfisicalBrokerServer? _broker;
    private readonly object _clientLock = new();
    private readonly ConcurrentDictionary<string, IssuedLeaseContext> _issued = new(StringComparer.Ordinal);
    private readonly object _tokenLock = new();
    private string _tokenFingerprint = string.Empty;
    private string _token = string.Empty;
    private DateTimeOffset _tokenExpiresAt;
    private bool _disposed;

    /// <summary>
    /// Production constructor. The plugin builds its own redirect-proof
    /// HTTP clients (see <see cref="CredentialHttp"/>) rather than
    /// the shared factory's redirect-following defaults, so a backend 3xx
    /// can never re-send a credential off-origin.
    /// </summary>
    public InfisicalSecretProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
    }

    /// <summary>Test constructor: explicit clients, config, clock, and environment.</summary>
    internal InfisicalSecretProvider(
        HttpClient http,
        IConfigurationSection config,
        TimeProvider? clock = null,
        Func<string, string?>? env = null,
        HttpClient? brokerForwardClient = null,
        ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _testConfig = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _brokerForward = brokerForwardClient;
        _logger = log ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string ProviderId => InfisicalOptions.PluginId;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        EnsureClients();
        var warnings = new List<string>();
        var options = InfisicalOptions.FromConfiguration(context.ScopedConfig, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("Infisical plugin option: {Warning}", warning);
        foreach (var error in options.Validate())
            _logger.LogError("Infisical plugin configuration: {Error}", error);
        if (!options.Enabled)
        {
            _logger.LogInformation("Infisical secret provider is disabled (Enabled=false); issuing nothing.");
            return Task.CompletedTask;
        }
        _logger.LogInformation(
            "Infisical secret provider initialised for site {Site} with {Count} secret mapping(s); broker {Broker}.",
            options.SiteUrl, options.Mappings.Count, options.BrokerEnabled ? "enabled" : "disabled");
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
            _logger.LogDebug(ex, "Infisical CanIssue suppressed an unexpected error; treating as cannot-issue.");
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
            ?? throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                $"Infisical has no mapping for sandbox variable '{secret.SandboxEnvVar}'; the operator never put this secret against this backend.");
        EnsureClients();

        LeasedSecretMaterial material;
        IssuedLeaseContext context;
        if (!string.IsNullOrWhiteSpace(mapping.SecretKey))
        {
            var token = await GetAccessTokenAsync(options, ct).ConfigureAwait(false);
            var fetched = await _api!.GetStaticSecretAsync(
                options.SiteUrl, token, options.WorkspaceId, options.Environment,
                mapping.SecretKey, mapping.SecretPath, options.MaxResponseBytes, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fetched.Value))
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
                    $"Infisical secret '{mapping.SecretKey}' returned an empty value.");
            var window = StaticWindow(options, requestedTtl);
            var expiresAt = _clock.GetUtcNow() + window;
            var leaseId = InfisicalLeaseIds.BuildStatic(mapping.SandboxEnvVar, mapping.Brokered);
            context = new IssuedLeaseContext(
                InfisicalLeaseIds.LeaseKind.Static, mapping, ServerLeaseId: null,
                Version: fetched.Version, ExpiresAt: expiresAt);
            material = new LeasedSecretMaterial
            {
                LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
                Value = fetched.Value,
                ExpiresAt = expiresAt,
                Brokered = false,
            };
            _logger.LogInformation(
                "Infisical issued static lease '{LeaseId}' for '{Var}' (scope {Scope}, key version {Version}).",
                leaseId, mapping.SandboxEnvVar, scope, fetched.Version);
        }
        else
        {
            (material, context) = await IssueDynamicAsync(options, mapping, scope, ct).ConfigureAwait(false);
        }

        if (mapping.Brokered)
        {
            if (!options.BrokerEnabled)
                throw new InfisicalException(
                    InfisicalFailureKind.Misconfigured,
                    $"Infisical mapping for '{mapping.SandboxEnvVar}' is brokered but BrokerEnabled is false; refusing a silent downgrade to direct issue.");
            if (string.IsNullOrEmpty(material.Value))
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
                    $"Infisical brokered issue for '{mapping.SandboxEnvVar}' produced no value to hold proxy-side.");
            var endpoint = EnsureBroker(options, mapping, material.LeaseId, material.Value, material.ExpiresAt);
            material = material with { Value = null, Brokered = true, Endpoint = endpoint };
            context = context with { Brokered = true };
            _logger.LogInformation(
                "Infisical issued brokered lease '{LeaseId}' for '{Var}' (scope {Scope}); guest receives only the endpoint.",
                material.LeaseId, mapping.SandboxEnvVar, scope);
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
        var mapping = context.Mapping;

        if (parsed.Kind == InfisicalLeaseIds.LeaseKind.Static)
        {
            var token = await GetAccessTokenAsync(options, ct).ConfigureAwait(false);
            var fetched = await _api!.GetStaticSecretAsync(
                options.SiteUrl, token, options.WorkspaceId, options.Environment,
                mapping.SecretKey, mapping.SecretPath, options.MaxResponseBytes, ct).ConfigureAwait(false);
            // Renewal extends the window; the fresh value reaches the guest
            // on the next provisioning, and refreshes a broker entry that
            // survived alongside the lease.
            var expiresAt = _clock.GetUtcNow() + StaticWindow(options, TimeSpan.Zero);
            _issued[leaseId] = context with { Version = fetched.Version, ExpiresAt = expiresAt };
            RefreshBrokerEntry(options, leaseId, context, fetched.Value, expiresAt);
            _logger.LogDebug("Infisical renewed static lease '{LeaseId}' (key version {Version}).", leaseId, fetched.Version);
            return expiresAt;
        }

        var renewed = await _api!.RenewDynamicLeaseAsync(
            options.SiteUrl,
            await GetAccessTokenAsync(options, ct).ConfigureAwait(false),
            context.ServerLeaseId!,
            RequireProjectSlug(options),
            options.Environment,
            mapping.SecretPath,
            mapping.Ttl,
            options.MaxResponseBytes,
            ct).ConfigureAwait(false);
        _issued[leaseId] = context with { ExpiresAt = renewed };
        if (context.Brokered && _broker is not null && !_broker.Extend(leaseId, renewed))
        {
            // The broker entry is gone (orchestrator restart drops host
            // memory) and a dynamic value cannot be re-read from the lease
            // GET — the endpoint stays revoked until re-provisioning. Loud,
            // documented, and the server-side lease itself is still renewed.
            _logger.LogError(
                "Infisical broker entry for dynamic lease '{LeaseId}' is gone; the endpoint stays revoked until re-provisioning.",
                leaseId);
        }
        _logger.LogDebug("Infisical renewed dynamic lease '{LeaseId}'.", leaseId);
        return renewed;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);
        var options = RequireUsableOptions();
        EnsureClients();
        if (_broker is not null && _broker.Unregister(leaseId))
            _logger.LogInformation("Infisical broker dropped endpoint for lease '{LeaseId}'.", leaseId);
        var context = _issued.TryGetValue(leaseId, out var known)
            ? known
            : RebuildContext(leaseId, options, parsed);
        _issued.TryRemove(leaseId, out _);

        if (parsed.Kind == InfisicalLeaseIds.LeaseKind.Static)
        {
            // Static Infisical secrets carry no server-side lease: local
            // invalidation (above) plus the orchestrator's teardown scrub of
            // the per-exec environment is the complete revocation. Always
            // succeeds and is idempotent by construction.
            _logger.LogInformation("Infisical invalidated static lease '{LeaseId}'.", leaseId);
            return;
        }

        await _api!.RevokeDynamicLeaseAsync(
            options.SiteUrl,
            await GetAccessTokenAsync(options, ct).ConfigureAwait(false),
            context.ServerLeaseId!,
            RequireProjectSlug(options),
            options.Environment,
            context.Mapping.SecretPath,
            ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _broker?.Dispose(); } catch (Exception) { }
        _token = string.Empty;
        // Only provider-built clients are disposed here; test-injected
        // clients stay owned by their test.
        if (_ownsHttp)
        {
            try { _http?.Dispose(); } catch (Exception) { }
        }
        if (_ownsBrokerForward)
        {
            try { _brokerForward?.Dispose(); } catch (Exception) { }
        }
    }

    internal InfisicalOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new InfisicalOptions()
            : InfisicalOptions.FromConfiguration(section);
    }

    internal InfisicalBrokerServer? BrokerForTests => _broker;

    private async Task<(LeasedSecretMaterial Material, IssuedLeaseContext Context)> IssueDynamicAsync(
        InfisicalOptions options, InfisicalSecretMapping mapping, string scope, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(options, ct).ConfigureAwait(false);
        var created = await _api!.CreateDynamicLeaseAsync(
            options.SiteUrl, token, RequireProjectSlug(options), options.Environment,
            mapping.SecretPath, mapping.DynamicSecretName, mapping.Ttl,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
        if (!created.Data.TryGetValue(mapping.DataField, out var value) || string.IsNullOrEmpty(value))
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                $"Infisical dynamic lease for '{mapping.DynamicSecretName}' has no field '{mapping.DataField}'; check DataField against the dynamic-secret type.");
        var leaseId = InfisicalLeaseIds.BuildDynamic(mapping.SandboxEnvVar, created.LeaseId, mapping.Brokered);
        var material = new LeasedSecretMaterial
        {
            LeaseId = SecretLeasePolicy.ValidateLeaseId(leaseId, nameof(leaseId)),
            Value = value,
            ExpiresAt = created.ExpiresAt,
            Brokered = false,
        };
        var context = new IssuedLeaseContext(
            InfisicalLeaseIds.LeaseKind.Dynamic, mapping, created.LeaseId,
            Version: 0, ExpiresAt: created.ExpiresAt);
        _logger.LogInformation(
            "Infisical issued dynamic lease '{LeaseId}' for '{Var}' (scope {Scope}, server lease {ServerLease}).",
            leaseId, mapping.SandboxEnvVar, scope, created.LeaseId);
        return (material, context);
    }

    private string EnsureBroker(
        InfisicalOptions options, InfisicalSecretMapping mapping,
        string leaseId, string value, DateTimeOffset expiresAt)
    {
        EnsureBrokerServer(options);
        _broker!.Register(new InfisicalBrokerEntry
        {
            LeaseId = leaseId,
            Value = value,
            UpstreamBaseUrl = mapping.BrokerUpstreamBaseUrl,
            InjectHeader = mapping.BrokerInjectHeader,
            InjectScheme = mapping.BrokerInjectScheme,
            AllowedPaths = mapping.BrokerAllowedPaths,
            ExpiresAt = expiresAt,
        });
        var advertise = string.IsNullOrWhiteSpace(options.BrokerAdvertiseHost)
            ? options.BrokerBindHost
            : options.BrokerAdvertiseHost.Trim();
        return $"http://{advertise}:{_broker.ActualPort}/v1/proxy/{leaseId}";
    }

    private void EnsureBrokerServer(InfisicalOptions options)
    {
        if (_broker is { Running: true })
            return;
        lock (_clientLock)
        {
            if (_broker is { Running: true })
                return;
            if (_brokerForward is null)
            {
                _brokerForward = CredentialHttp.CreateNoRedirectClient(ClientTimeout(options));
                _ownsBrokerForward = true;
            }
            _brokerForward.Timeout = ClientTimeout(options);
            var server = new InfisicalBrokerServer(
                _brokerForward, _clock, _logger,
                options.BrokerMaxBodyBytes, options.BrokerMaxConcurrency, options.BrokerMaxPathChars);
            try
            {
                server.Start(options.BrokerBindHost, options.BrokerBindPort);
            }
            catch (InvalidOperationException ex)
            {
                // Bind failure is host configuration (port in use,
                // unbindable interface) — loud, and the secret is skipped
                // rather than downgraded to a direct lease.
                throw new InfisicalException(
                    InfisicalFailureKind.Misconfigured,
                    $"Infisical broker could not bind {options.BrokerBindHost}:{options.BrokerBindPort}.",
                    ex);
            }
            _broker = server;
        }
    }

    private void RefreshBrokerEntry(
        InfisicalOptions options, string leaseId, IssuedLeaseContext context,
        string value, DateTimeOffset expiresAt)
    {
        if (!context.Brokered)
            return;
        if (_broker is { Running: true } && _broker.Extend(leaseId, expiresAt))
            return;
        if (context.Kind == InfisicalLeaseIds.LeaseKind.Static)
        {
            // A static value CAN be re-fetched, so a missing broker entry
            // (orchestrator restart drops host memory) is restored instead
            // of revoked — the endpoint keeps working.
            EnsureBrokerServer(options);
            _broker!.Register(new InfisicalBrokerEntry
            {
                LeaseId = leaseId,
                Value = value,
                UpstreamBaseUrl = context.Mapping.BrokerUpstreamBaseUrl,
                InjectHeader = context.Mapping.BrokerInjectHeader,
                InjectScheme = context.Mapping.BrokerInjectScheme,
                AllowedPaths = context.Mapping.BrokerAllowedPaths,
                ExpiresAt = expiresAt,
            });
            _logger.LogInformation(
                "Infisical broker re-registered endpoint for static lease '{LeaseId}'.", leaseId);
        }
    }

    private async Task<string> GetAccessTokenAsync(InfisicalOptions options, CancellationToken ct)
    {
        var fingerprint = string.Join('|',
            options.SiteUrl, options.ClientIdEnvVar, options.ClientSecretEnvVar, options.AccessTokenEnvVar);
        lock (_tokenLock)
        {
            if (!string.IsNullOrEmpty(_token)
                && string.Equals(_tokenFingerprint, fingerprint, StringComparison.Ordinal)
                && _clock.GetUtcNow() < _tokenExpiresAt)
                return _token;
        }

        var clientId = string.IsNullOrWhiteSpace(options.ClientIdEnvVar)
            ? null : _env(options.ClientIdEnvVar);
        var clientSecret = string.IsNullOrWhiteSpace(options.ClientSecretEnvVar)
            ? null : _env(options.ClientSecretEnvVar);
        string token;
        DateTimeOffset expiresAt;
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            EnsureClients();
            var skew = TimeSpan.FromSeconds(Math.Clamp(options.TokenRefreshSkewSeconds, 0, 3600));
            (token, expiresAt) = await _api!.LoginUniversalAuthAsync(
                options.SiteUrl, clientId, clientSecret, skew, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(options.AccessTokenEnvVar)
            && !string.IsNullOrWhiteSpace(_env(options.AccessTokenEnvVar)))
        {
            // Externally injected token of unknown lifetime: cache briefly
            // so rotation propagates within minutes.
            token = _env(options.AccessTokenEnvVar)!;
            expiresAt = _clock.GetUtcNow() + TimeSpan.FromMinutes(ExternalTokenCacheMinutes);
            _logger.LogDebug("Infisical using externally supplied access token; re-reading within minutes.");
        }
        else
        {
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                $"Infisical provider credentials are not set: env '{options.ClientIdEnvVar}'/'{options.ClientSecretEnvVar}' (universal auth) and " +
                (string.IsNullOrWhiteSpace(options.AccessTokenEnvVar)
                    ? "no access-token variable is configured."
                    : $"env '{options.AccessTokenEnvVar}' are all empty. Provision them from the host credential chain."));
        }

        lock (_tokenLock)
        {
            _tokenFingerprint = fingerprint;
            _token = token;
            _tokenExpiresAt = expiresAt;
        }
        return token;
    }

    private InfisicalOptions RequireUsableOptions()
    {
        var options = CurrentOptions();
        if (!options.Enabled)
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                "Infisical provider is disabled (Enabled=false); enable it to issue.");
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                $"Infisical configuration is invalid: {errors[0]}");
        if (string.IsNullOrWhiteSpace(options.WorkspaceId))
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                "Infisical WorkspaceId is required (the Infisical project ID).");
        return options;
    }

    private static InfisicalSecretMapping? FindMapping(InfisicalOptions options, ProjectSandboxSecret secret)
    {
        foreach (var mapping in options.Mappings)
        {
            if (!string.Equals(mapping.SandboxEnvVar, secret.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(mapping.Group)
                && !string.Equals(mapping.Group, secret.Group, StringComparison.Ordinal))
                continue;
            var hasStatic = !string.IsNullOrWhiteSpace(mapping.SecretKey);
            var hasDynamic = !string.IsNullOrWhiteSpace(mapping.DynamicSecretName);
            if (hasStatic == hasDynamic)
                continue;
            if (hasDynamic && string.IsNullOrWhiteSpace(mapping.DataField))
                continue;
            return mapping;
        }
        return null;
    }

    private IssuedLeaseContext RebuildContext(
        string leaseId, InfisicalOptions options, InfisicalLeaseIds.ParsedLeaseId parsed)
    {
        // Restart path: the in-memory issue registry is gone, but the lease
        // id carries the sandbox variable (and the broker flag), and the
        // tail carries the server lease id (dynamic) — enough to re-resolve
        // today's mapping. The grant itself was verified at issue time and
        // is not re-decided here; renewal extends the same lease.
        InfisicalSecretMapping? mapping = null;
        foreach (var candidate in options.Mappings)
        {
            if (!string.Equals(candidate.SandboxEnvVar, parsed.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            var hasStatic = !string.IsNullOrWhiteSpace(candidate.SecretKey);
            var hasDynamic = !string.IsNullOrWhiteSpace(candidate.DynamicSecretName);
            if (hasStatic == hasDynamic)
                continue;
            if (hasDynamic && string.IsNullOrWhiteSpace(candidate.DataField))
                continue;
            if ((parsed.Kind == InfisicalLeaseIds.LeaseKind.Static) == hasDynamic)
                continue;
            mapping = candidate;
            break;
        }
        if (mapping is null)
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                $"Infisical lease '{leaseId}' names sandbox variable '{parsed.SandboxEnvVar}' with no current mapping; restore the mapping or revoke server-side.");
        return new IssuedLeaseContext(
            parsed.Kind, mapping,
            parsed.Kind == InfisicalLeaseIds.LeaseKind.Dynamic ? parsed.Tail : null,
            Version: 0, ExpiresAt: _clock.GetUtcNow(), Brokered: parsed.Brokered);
    }

    private static InfisicalLeaseIds.ParsedLeaseId ParseOurs(string leaseId)
    {
        if (!InfisicalLeaseIds.TryParse(leaseId, out var parsed))
            throw new InfisicalException(
                InfisicalFailureKind.Misconfigured,
                $"Lease '{leaseId ?? string.Empty}' is not an Infisical lease handle.");
        return parsed;
    }

    private static string RequireProjectSlug(InfisicalOptions options)
    {
        // Dynamic-lease endpoints address the project by slug while static
        // reads accept the ID; the operator sets both when dynamic leases
        // are mapped (validated at startup).
        if (!string.IsNullOrWhiteSpace(options.ProjectSlug))
            return options.ProjectSlug;
        throw new InfisicalException(
            InfisicalFailureKind.Misconfigured,
            "Infisical ProjectSlug is required for dynamic-secret leases (dynamic-lease endpoints address the project by slug).");
    }

    private static TimeSpan StaticWindow(InfisicalOptions options, TimeSpan requestedTtl)
    {
        var configured = TimeSpan.FromMinutes(Math.Clamp(options.StaticLeaseTtlMinutes, 1, 1440));
        return requestedTtl > TimeSpan.Zero && requestedTtl < configured ? requestedTtl : configured;
    }

    private static TimeSpan ClientTimeout(InfisicalOptions options) =>
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
            _api = new InfisicalRestClient(_http, _clock, _logger);
        }
    }

    private sealed record IssuedLeaseContext(
        InfisicalLeaseIds.LeaseKind Kind,
        InfisicalSecretMapping Mapping,
        string? ServerLeaseId,
        long Version,
        DateTimeOffset ExpiresAt,
        bool Brokered = false);
}
