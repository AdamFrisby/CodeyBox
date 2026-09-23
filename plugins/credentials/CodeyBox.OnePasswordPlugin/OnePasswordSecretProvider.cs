using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OnePasswordPlugin;

/// <summary>
/// CodeyBox lease-shaped secret provider for 1Password. One project, one
/// plugin: item fields from operator-mapped vaults, read either from the
/// self-hosted Connect server (Connect token) or with the <c>op</c> CLI
/// under a service-account token.
/// <para>Off unless an operator enables it: the plugin loads only when
/// allowlisted, and issues nothing until <c>Enabled=true</c> in its own
/// section. Authorisation stays with the host manager — a grant decides
/// whether a group applies; this provider only resolves mapped secrets the
/// manager already approved, and a group without a matching grant is never
/// fetched (the manager never calls here for it).</para>
/// <para>Provider credentials come from the host credential chain
/// (environment variables holding a Connect token or a service-account
/// token); configuration holds only the variable <em>names</em>, never
/// values. Nothing emitted — logs, lease records, exceptions — ever
/// carries a secret value or token.</para>
/// <para>Honest lease surface: Connect items and <c>op</c>-read fields have
/// no server-side lease to delete — the lease is a client-side validity
/// window: renewal re-fetches (an edit or rotation propagates within one
/// window) and revocation is local invalidation plus the orchestrator's
/// teardown scrub of the per-exec environment. A revoked lease refuses
/// renewal in this process (verified, not assumed); after a restart the
/// handle re-resolves its mapping, because revocation state is memory-only
/// by design. See the plugin README for the full statement.</para>
/// </summary>
[CodeyBoxPlugin(
    id: OnePasswordOptions.PluginId,
    displayName: "CodeyBox: 1Password Secrets",
    minHostApiVersion: "1.0")]
public sealed class OnePasswordSecretProvider : ILeaseCapableSecretProvider, IPluginInitializer, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _env;
    private readonly IConfigurationSection? _testConfig;
    private readonly IOnePasswordProcessRunner? _testRunner;

    private IPluginHost? _host;
    private ILogger _logger = NullLogger.Instance;
    private HttpClient? _http;
    private bool _ownsHttp;
    private OnePasswordRestClient? _api;
    private OnePasswordServiceAccountClient? _cli;
    private readonly object _clientLock = new();
    private readonly ConcurrentDictionary<string, IssuedLeaseContext> _issued = new(StringComparer.Ordinal);

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
    public OnePasswordSecretProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _env = Environment.GetEnvironmentVariable;
    }

    /// <summary>Test constructor: explicit client, config, clock, environment, and CLI runner.</summary>
    internal OnePasswordSecretProvider(
        HttpClient http,
        IConfigurationSection config,
        TimeProvider? clock = null,
        Func<string, string?>? env = null,
        ILogger? log = null,
        IOnePasswordProcessRunner? cliRunner = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _testConfig = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _logger = log ?? NullLogger.Instance;
        _testRunner = cliRunner;
    }

    /// <inheritdoc />
    public string ProviderId => OnePasswordOptions.PluginId;

    /// <inheritdoc />
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _logger = context.Logger ?? NullLogger.Instance;
        EnsureClients();
        var warnings = new List<string>();
        var options = OnePasswordOptions.FromConfiguration(context.ScopedConfig, warnings);
        foreach (var warning in warnings)
            _logger.LogWarning("1Password plugin option: {Warning}", warning);
        foreach (var error in options.Validate())
            _logger.LogError("1Password plugin configuration: {Error}", error);
        if (!options.Enabled)
        {
            _logger.LogInformation("1Password secret provider is disabled (Enabled=false); issuing nothing.");
            return Task.CompletedTask;
        }
        _logger.LogInformation(
            "1Password secret provider initialised for {Server} with {Count} secret mapping(s){ServiceAccount}.",
            options.ServerUrl, options.Mappings.Count,
            options.ServiceAccountConfigured ? " and service-account retrieval" : string.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when this backend serves <paramref name="secret"/>: the plugin
    /// is enabled and an operator mapping names its sandbox variable (plus
    /// the mapping's group guard, when set). Never touches the network,
    /// never spawns a process, and never throws — a surprise means
    /// "cannot issue", not a pipeline failure.
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
            _logger.LogDebug(ex, "1Password CanIssue suppressed an unexpected error; treating as cannot-issue.");
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
            ?? throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password has no mapping for sandbox variable '{secret.SandboxEnvVar}'; the operator never put this secret against this backend.");
        EnsureClients();

        var value = await FetchValueAsync(options, mapping, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(value))
            throw new OnePasswordException(
                CredentialFailureKind.InvalidResponse,
                $"1Password secret '{secret.SandboxEnvVar}' returned an empty value.");
        var window = StaticWindow(options, requestedTtl);
        var expiresAt = _clock.GetUtcNow() + window;
        var kind = mapping.UseServiceAccount
            ? OnePasswordLeaseIds.LeaseKind.ServiceAccount
            : OnePasswordLeaseIds.LeaseKind.Connect;
        var leaseId = kind == OnePasswordLeaseIds.LeaseKind.Connect
            ? OnePasswordLeaseIds.BuildConnect(mapping.SandboxEnvVar)
            : OnePasswordLeaseIds.BuildServiceAccount(mapping.SandboxEnvVar);
        _issued[leaseId] = new IssuedLeaseContext(kind, mapping, expiresAt);
        _logger.LogInformation(
            "1Password issued {Transport} lease '{LeaseId}' for '{Var}' (scope {Scope}).",
            mapping.UseServiceAccount ? "service-account" : "Connect",
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
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password lease '{leaseId}' was revoked in this process and cannot be renewed.");
        var context = _issued.TryGetValue(leaseId, out var known)
            ? known
            : RebuildContext(leaseId, options, parsed);
        if (context.Kind != parsed.Kind)
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password lease '{leaseId}' changed retrieval transport; re-provision instead of renewing.");

        // Static renewal re-fetches so an edit or rotation propagates
        // within one window; the fresh value reaches the guest on next
        // provisioning.
        await FetchValueAsync(options, context.Mapping, ct).ConfigureAwait(false);
        var expiresAt = _clock.GetUtcNow() + StaticWindow(options, TimeSpan.Zero);
        _issued[leaseId] = context with { ExpiresAt = expiresAt };
        _logger.LogDebug("1Password renewed lease '{LeaseId}'.", leaseId);
        return expiresAt;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string leaseId, CancellationToken ct = default)
    {
        var parsed = ParseOurs(leaseId);
        _ = RequireUsableOptions();
        EnsureClients();
        await Task.CompletedTask.ConfigureAwait(false);

        // 1Password items carry no server-side lease: local invalidation
        // (dropping the registry entry and refusing future renewal in this
        // process) plus the orchestrator's teardown scrub of the per-exec
        // environment is the complete revocation. Always succeeds and is
        // idempotent by construction.
        _issued.TryRemove(leaseId, out _);
        _revokedLeases[leaseId] = true;
        _logger.LogInformation(
            "1Password invalidated {Kind} lease '{LeaseId}'.",
            parsed.Kind == OnePasswordLeaseIds.LeaseKind.Connect ? "Connect" : "service-account",
            leaseId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _issued.Clear();
        if (_ownsHttp)
        {
            try { _http?.Dispose(); } catch (Exception) { }
        }
    }

    internal OnePasswordOptions CurrentOptions()
    {
        var section = _host?.ScopedConfig ?? _testConfig;
        return section is null
            ? new OnePasswordOptions()
            : OnePasswordOptions.FromConfiguration(section);
    }

    private async Task<string> FetchValueAsync(
        OnePasswordOptions options, OnePasswordSecretMapping mapping, CancellationToken ct)
    {
        if (mapping.UseServiceAccount)
        {
            EnsureClients();
            var token = ReadServiceAccountToken(options);
            var opTimeout = TimeSpan.FromSeconds(Math.Clamp(options.OpTimeoutSeconds, 1, 300));
            return await _cli!.ReadFieldAsync(
                options.OpBinaryPath,
                token,
                VaultRef(mapping),
                ItemRef(mapping),
                string.IsNullOrWhiteSpace(mapping.Field) ? OnePasswordOptions.DefaultField : mapping.Field.Trim(),
                opTimeout,
                ct).ConfigureAwait(false);
        }

        var connectToken = ReadConnectToken(options, mapping);
        EnsureClients();
        return await _api!.GetItemFieldAsync(
            options.ServerUrl, connectToken, mapping,
            options.MaxResponseBytes, ct).ConfigureAwait(false);
    }

    private string ReadConnectToken(OnePasswordOptions options, OnePasswordSecretMapping mapping)
    {
        var name = string.IsNullOrWhiteSpace(mapping.TokenEnvVar)
            ? options.ConnectTokenEnvVar
            : mapping.TokenEnvVar;
        var token = string.IsNullOrWhiteSpace(name) ? null : _env(name);
        if (string.IsNullOrWhiteSpace(token))
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password Connect token env '{name}' is empty. Provision a Connect token " +
                $"scoped to the mapped vault from the host credential chain.");
        return token;
    }

    private string ReadServiceAccountToken(OnePasswordOptions options)
    {
        var name = options.ServiceAccountTokenEnvVar;
        var token = string.IsNullOrWhiteSpace(name) ? null : _env(name);
        if (string.IsNullOrWhiteSpace(token))
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password service-account token env '{name}' is empty. Provision a service-account token " +
                $"with access to the mapped vaults from the host credential chain.");
        return token;
    }

    private static string VaultRef(OnePasswordSecretMapping mapping)
        => string.IsNullOrWhiteSpace(mapping.VaultId) ? mapping.VaultName.Trim() : mapping.VaultId.Trim();

    private static string ItemRef(OnePasswordSecretMapping mapping)
        => string.IsNullOrWhiteSpace(mapping.ItemId) ? mapping.ItemTitle.Trim() : mapping.ItemId.Trim();

    private OnePasswordOptions RequireUsableOptions()
    {
        var options = CurrentOptions();
        if (!options.Enabled)
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                "1Password provider is disabled (Enabled=false); enable it to issue.");
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password configuration is invalid: {errors[0]}");
        return options;
    }

    private static OnePasswordSecretMapping? FindMapping(OnePasswordOptions options, ProjectSandboxSecret secret)
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
        string leaseId, OnePasswordOptions options, OnePasswordLeaseIds.ParsedLeaseId parsed)
    {
        // Restart path: the in-memory issue registry is gone, but the lease
        // id carries the sandbox variable and the transport kind — enough
        // to re-resolve today's mapping. The grant itself was verified at
        // issue time and is not re-decided here; renewal extends the same
        // lease.
        OnePasswordSecretMapping? mapping = null;
        foreach (var candidate in options.Mappings)
        {
            if (!string.Equals(candidate.SandboxEnvVar, parsed.SandboxEnvVar, StringComparison.Ordinal))
                continue;
            mapping = candidate;
            break;
        }
        if (mapping is null)
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"1Password lease '{leaseId}' names sandbox variable '{parsed.SandboxEnvVar}' with no current mapping; restore the mapping.");
        var expectedKind = mapping.UseServiceAccount
            ? OnePasswordLeaseIds.LeaseKind.ServiceAccount
            : OnePasswordLeaseIds.LeaseKind.Connect;
        return new IssuedLeaseContext(expectedKind, mapping, _clock.GetUtcNow());
    }

    private static OnePasswordLeaseIds.ParsedLeaseId ParseOurs(string leaseId)
    {
        if (!OnePasswordLeaseIds.TryParse(leaseId, out var parsed))
            throw new OnePasswordException(
                CredentialFailureKind.Misconfigured,
                $"Lease '{leaseId ?? string.Empty}' is not a 1Password lease handle.");
        return parsed;
    }

    private static TimeSpan StaticWindow(OnePasswordOptions options, TimeSpan requestedTtl)
    {
        var configured = TimeSpan.FromMinutes(Math.Clamp(options.StaticLeaseTtlMinutes, 1, 1440));
        return requestedTtl > TimeSpan.Zero && requestedTtl < configured ? requestedTtl : configured;
    }

    private static TimeSpan ClientTimeout(OnePasswordOptions options) =>
        TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));

    private void EnsureClients()
    {
        if (_api is not null && _cli is not null)
            return;
        lock (_clientLock)
        {
            if (_api is null)
            {
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
                _api = new OnePasswordRestClient(_http, _clock, _logger);
            }
            _cli ??= new OnePasswordServiceAccountClient(_testRunner, _logger);
        }
    }

    private sealed record IssuedLeaseContext(
        OnePasswordLeaseIds.LeaseKind Kind,
        OnePasswordSecretMapping Mapping,
        DateTimeOffset ExpiresAt);
}
