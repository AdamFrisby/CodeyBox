using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Materialised sandbox environment for leased + static project secrets.
/// For brokered leases <see cref="Environment"/> carries only the
/// non-secret endpoint URL under the declared variable — the value never
/// enters the guest, its files, or any log.
/// </summary>
public sealed record SecretLeaseMaterialization
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Non-secret broker endpoints handed to the workload. Safe to log; never secrets.</summary>
    public IReadOnlyList<string> BrokerEndpoints { get; init; } = [];

    /// <summary>Lease handles issued during this materialisation (identity only, no values).</summary>
    public IReadOnlyList<SecretLeaseRecord> IssuedLeases { get; init; } = [];
}

/// <summary>
/// Outcome of a revocation pass. A lease believed revoked but still live is
/// worse than one known to be outstanding: failures stay loud (error log
/// with lease ids) and recorded (store status + this report).
/// </summary>
public sealed record LeaseRevocationReport
{
    public IReadOnlyList<string> RevokedLeaseIds { get; init; } = [];
    public IReadOnlyList<LeaseRevocationFailure> Failures { get; init; } = [];
    public bool AllRevoked => Failures.Count == 0;
}

public sealed record LeaseRevocationFailure
{
    public required string LeaseId { get; init; }
    public required string Error { get; init; }
}

/// <summary>
/// Outcome of a renewal pass.
/// </summary>
public sealed record LeaseRenewalReport
{
    public IReadOnlyList<string> RenewedLeaseIds { get; init; } = [];
    public IReadOnlyList<LeaseRevocationFailure> Failures { get; init; } = [];
}

/// <summary>
/// Issues, renews, and revokes leased workload secrets, composing with the
/// secret authorisation model: a grant decides whether a group applies, a
/// lease decides how long its value lives. Every issue path re-verifies the
/// grant at the moment of action, so leasing can never become a route to a
/// secret that was not granted.
///
/// <para>Providers offering only static values do not implement
/// <see cref="ILeaseCapableSecretProvider"/>: secrets they cannot issue
/// fall back to the existing host-environment read unchanged.</para>
/// </summary>
public sealed class SecretLeaseManager
{
    private readonly ISecretLeaseStore _store;
    private readonly IReadOnlyList<ILeaseCapableSecretProvider> _providers;
    private readonly Func<SecretLeasingOptions> _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger? _log;

    public SecretLeaseManager(
        ISecretLeaseStore store,
        IEnumerable<ILeaseCapableSecretProvider>? providers = null,
        Func<SecretLeasingOptions>? options = null,
        Func<DateTimeOffset>? utcNow = null,
        ILogger? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providers = (providers ?? []).ToList().AsReadOnly();
        _options = options ?? (() => new SecretLeasingOptions());
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <summary>True when at least one lease-capable provider is registered.</summary>
    public bool HasLeaseProviders => _providers.Count > 0;

    /// <summary>
    /// Resolves the sandbox environment for <paramref name="scope"/>
    /// authorised for <paramref name="workItemId"/>. Grant-denied groups
    /// inject nothing (default deny, never an error). Lease-capable secrets
    /// are issued as time-bound (or brokered) leases and persisted; the rest
    /// fall back to the static host-environment read with identical guards.
    /// Nothing emitted here ever carries a secret value.
    /// </summary>
    public async Task<SecretLeaseMaterialization> MaterializeForScopeAsync(
        Project project,
        WorkItemId workItemId,
        string scope,
        Func<string, string?> readHostEnvironment,
        DateTimeOffset? itemDeadline,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(readHostEnvironment);
        var options = _options();
        var now = _utcNow();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var brokerEndpoints = new List<string>();
        var issued = new List<SecretLeaseRecord>();
        var staticSecrets = new List<ProjectSandboxSecret>();
        // Shared aggregate budget with the static path: leased, brokered,
        // and static values land in the same SandboxSpec.Environment sink,
        // so all three accumulate here via the shared guard.
        long aggregateBytes = 0;

        foreach (var secret in project.SandboxSecrets)
        {
            if (!secret.AppliesTo(scope))
                continue;
            // The grant decides whether the group applies — checked here, at
            // the sink, for every secret on every materialisation.
            var grant = ProjectSandboxSecretResolver.FindAuthorizingGrant(
                project.SandboxSecretGrants, secret.Group, workItemId);
            if (grant is null)
                continue;

            var provider = _providers.FirstOrDefault(p => p.CanIssue(secret));
            if (provider is null)
            {
                staticSecrets.Add(secret);
                continue;
            }

            // Declared-name guard applies to leased and brokered secrets
            // alike; the static subset gets it from the resolver below.
            CodeyBox.Sandbox.SandboxEnvironmentVariablePolicy.ValidateCredentialEnvironmentVariable(
                secret.SandboxEnvVar, nameof(project));

            LeasedSecretMaterial material;
            try
            {
                material = await provider.IssueAsync(
                    secret, workItemId.Value, scope, options.DefaultLeaseTtl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failing issuer must not silently downgrade a leased
                // secret to a static read (which would bypass the TTL) nor
                // fail the phase: skip with a names-only error and let the
                // item run without this secret, exactly like an unset host
                // variable.
                (log ?? _log)?.LogError(
                    ex,
                    "Secret lease issue failed for project {ProjectId} secret '{SandboxEnvVar}' via provider '{ProviderId}'; running without it.",
                    project.Id.Value, secret.SandboxEnvVar, provider.ProviderId);
                continue;
            }

            if (material.Brokered)
            {
                if (string.IsNullOrWhiteSpace(material.Endpoint))
                {
                    (log ?? _log)?.LogError(
                        "Secret lease issue for project {ProjectId} secret '{SandboxEnvVar}' via provider '{ProviderId}' claimed brokered without an endpoint; running without it.",
                        project.Id.Value, secret.SandboxEnvVar, provider.ProviderId);
                    continue;
                }
                // The endpoint URL is not a secret: it names where the
                // workload calls, authenticated by sandbox identity. The
                // value never enters the guest environment, files, or logs.
                // It still occupies the same Environment sink, so it counts
                // toward the shared aggregate budget via the shared guard.
                ProjectSandboxSecretResolver.AccumulateSandboxSecretValue(
                    project.Id.Value, secret.SandboxEnvVar, material.Endpoint!, ref aggregateBytes);
                env[secret.SandboxEnvVar] = material.Endpoint!;
                brokerEndpoints.Add(material.Endpoint!);
            }
            else
            {
                if (string.IsNullOrEmpty(material.Value))
                {
                    (log ?? _log)?.LogWarning(
                        "Secret lease issue for project {ProjectId} secret '{SandboxEnvVar}' via provider '{ProviderId}' returned no value; running without it.",
                        project.Id.Value, secret.SandboxEnvVar, provider.ProviderId);
                    continue;
                }
                // Shared sink-adjacent guard (NUL, per-value, aggregate):
                // the same helper the static resolver uses, accumulating
                // into the merged budget. Checked before persisting the
                // lease so a budget rejection never orphans a live lease.
                ProjectSandboxSecretResolver.AccumulateSandboxSecretValue(
                    project.Id.Value, secret.SandboxEnvVar, material.Value!, ref aggregateBytes);
                env[secret.SandboxEnvVar] = material.Value!;
            }

            var record = new SecretLeaseRecord
            {
                LeaseId = SecretLeasePolicy.ValidateLeaseId(material.LeaseId, nameof(material)),
                ProviderId = provider.ProviderId,
                WorkItemId = workItemId.Value,
                Group = secret.Group,
                SandboxEnvVar = secret.SandboxEnvVar,
                Scope = scope,
                ExpiresAt = CapExpiry(material.ExpiresAt, now, itemDeadline, options),
                CreatedAt = now,
                Brokered = material.Brokered,
                Endpoint = material.Brokered ? material.Endpoint : null,
            };
            await _store.UpsertAsync(record, ct).ConfigureAwait(false);
            issued.Add(record);
            (log ?? _log)?.LogInformation(
                "Project {ProjectId} issued {Brokered}secret lease '{LeaseId}' for '{SandboxEnvVar}' via provider '{ProviderId}'.",
                project.Id.Value, material.Brokered ? "brokered " : string.Empty,
                record.LeaseId, secret.SandboxEnvVar, provider.ProviderId);
        }

        if (staticSecrets.Count > 0)
        {
            // Static-only path: identical guards (reserved names, NUL, byte
            // budgets) via the existing resolver on the static subset. Each
            // resolved entry is then accumulated into the merged budget, so
            // the combined leased + static environment can never exceed the
            // sandbox credential budget even when each subset fits alone.
            var staticProject = project with { SandboxSecrets = staticSecrets };
            var staticEnv = ProjectSandboxSecretResolver.ResolveForScope(
                staticProject, workItemId, scope, readHostEnvironment, log ?? _log);
            foreach (var (k, v) in staticEnv)
            {
                ProjectSandboxSecretResolver.AccumulateSandboxSecretValue(
                    project.Id.Value, k, v, ref aggregateBytes);
                env[k] = v;
            }
        }

        return new SecretLeaseMaterialization
        {
            Environment = new ReadOnlyDictionary<string, string>(env),
            BrokerEndpoints = brokerEndpoints.AsReadOnly(),
            IssuedLeases = issued.AsReadOnly(),
        };
    }

    /// <summary>
    /// Renews every outstanding lease of live work items whose expiry is
    /// within the renew window. Renewals are capped at the work item's own
    /// deadline (and at the absolute max lease lifetime) so a lease can
    /// never be extended indefinitely past the work it was issued for.
    /// </summary>
    public async Task<LeaseRenewalReport> RenewDueLeasesAsync(
        Func<Guid, DateTimeOffset?> getItemDeadline,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(getItemDeadline);
        var options = _options();
        var now = _utcNow();
        var renewed = new List<string>();
        var failures = new List<LeaseRevocationFailure>();

        foreach (var lease in await _store.ListOutstandingAsync(ct).ConfigureAwait(false))
        {
            if (lease.Status != SecretLeaseStatus.Active)
                continue;
            if (!SecretLeasePolicy.ShouldRenew(now, lease.ExpiresAt, options.RenewBeforeExpiry))
                continue;
            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, lease.ProviderId, StringComparison.Ordinal));
            if (provider is null)
                continue;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(options.RevocationTimeout);
                var expiry = await provider.RenewAsync(lease.LeaseId, attemptCts.Token).ConfigureAwait(false);
                var capped = CapExpiry(expiry, lease.CreatedAt, getItemDeadline(lease.WorkItemId), options);
                if (!await _store.TryUpdateExpiryAsync(lease.LeaseId, capped, ct).ConfigureAwait(false))
                {
                    // Lost a race with revocation: the lease is no longer
                    // Active, so there is nothing to renew.
                    continue;
                }
                renewed.Add(lease.LeaseId);
            }
            catch (Exception ex)
            {
                // Loud but non-fatal: the lease may still be valid until its
                // recorded expiry, and the next sweep retries.
                _log?.LogWarning(
                    ex,
                    "Secret lease renewal failed for lease '{LeaseId}' (work item {WorkItemId}); retrying on the next sweep.",
                    lease.LeaseId, lease.WorkItemId);
                failures.Add(new LeaseRevocationFailure { LeaseId = lease.LeaseId, Error = ex.Message });
            }
        }

        return new LeaseRenewalReport
        {
            RenewedLeaseIds = renewed.AsReadOnly(),
            Failures = failures.AsReadOnly(),
        };
    }

    /// <summary>
    /// Revokes every outstanding lease for <paramref name="workItemId"/>.
    /// Called on sandbox teardown and terminal transitions. Per-lease
    /// failures are recorded in the store (stays outstanding for the sweep)
    /// and surfaced in the report — never swallowed.
    /// </summary>
    public async Task<LeaseRevocationReport> RevokeWorkItemLeasesAsync(
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        var leases = await _store.ListForWorkItemAsync(workItemId.Value, ct).ConfigureAwait(false);
        return await RevokeLeasesAsync(leases, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciliation sweep for the failure case: revokes leases whose work
    /// item is terminal regardless of whether teardown succeeded, and renews
    /// due leases of live items. Anything that could not be revoked is
    /// reported, never swallowed.
    /// </summary>
    public async Task<LeaseRevocationReport> ReconcileAsync(
        Func<Guid, Task<WorkItemState?>> getItemState,
        Func<Guid, DateTimeOffset?> getItemDeadline,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(getItemState);
        ArgumentNullException.ThrowIfNull(getItemDeadline);
        var outstanding = await _store.ListOutstandingAsync(ct).ConfigureAwait(false);
        var toRevoke = new List<SecretLeaseRecord>();
        foreach (var group in outstanding.GroupBy(l => l.WorkItemId))
        {
            WorkItemState? state;
            try
            {
                state = await getItemState(group.Key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(
                    ex,
                    "Secret lease reconciliation could not read state for work item {WorkItemId}; leaving {Count} lease(s) outstanding for the next sweep.",
                    group.Key, group.Count());
                continue;
            }
            // A deleted item (null state) is terminal for lease purposes: no
            // live phase can still need the credential.
            if (state is null || WorkItemStates.IsTerminal(state.Value))
                toRevoke.AddRange(group);
        }

        var report = await RevokeLeasesAsync(toRevoke, ct).ConfigureAwait(false);
        await RenewDueLeasesAsync(getItemDeadline, ct).ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// Revokes the lease carried by an agent credential, if any. Static
    /// credentials (no lease handle) are a no-op returning success.
    /// </summary>
    public async Task<bool> RevokeAgentCredentialLeaseAsync(
        AgentCredential credential,
        IEnumerable<ILeaseCapableCredentialProvider> credentialProviders,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.LeaseId is null)
            return true;
        var provider = (credentialProviders ?? []).FirstOrDefault(p =>
            string.Equals(p.LeaseProviderId, credential.LeaseProviderId, StringComparison.Ordinal));
        if (provider is null)
        {
            _log?.LogError(
                "Agent credential lease '{LeaseId}' has no registered provider '{ProviderId}'; lease is orphaned and recorded as outstanding.",
                credential.LeaseId, credential.LeaseProviderId);
            return false;
        }
        try
        {
            await provider.RevokeCredentialAsync(credential.LeaseId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log?.LogError(
                ex,
                "Agent credential lease revocation failed for lease '{LeaseId}'; the lease may still be live.",
                credential.LeaseId);
            return false;
        }
    }

    private async Task<LeaseRevocationReport> RevokeLeasesAsync(
        IReadOnlyList<SecretLeaseRecord> leases,
        CancellationToken ct)
    {
        var options = _options();
        var revoked = new List<string>();
        var failures = new List<LeaseRevocationFailure>();
        foreach (var lease in leases)
        {
            if (lease.Status != SecretLeaseStatus.Active && lease.Status != SecretLeaseStatus.RevocationFailed)
                continue;
            if (lease.AttemptCount >= options.MaxRevocationAttempts && lease.Status == SecretLeaseStatus.RevocationFailed)
            {
                // Parked loudly: keep reporting it every sweep so an
                // operator sees the outstanding live lease.
                _log?.LogError(
                    "Secret lease '{LeaseId}' (work item {WorkItemId}) exhausted {Attempts} revocation attempts and may still be live; operator action required.",
                    lease.LeaseId, lease.WorkItemId, lease.AttemptCount);
                failures.Add(new LeaseRevocationFailure
                {
                    LeaseId = lease.LeaseId,
                    Error = lease.LastError ?? "Revocation attempts exhausted.",
                });
                continue;
            }

            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, lease.ProviderId, StringComparison.Ordinal));
            if (provider is null)
            {
                await _store.MarkRevocationFailedAsync(
                    lease.LeaseId, $"No registered provider '{lease.ProviderId}'.", ct).ConfigureAwait(false);
                _log?.LogError(
                    "Secret lease revocation failed for lease '{LeaseId}': no registered provider '{ProviderId}'.",
                    lease.LeaseId, lease.ProviderId);
                failures.Add(new LeaseRevocationFailure
                {
                    LeaseId = lease.LeaseId,
                    Error = $"No registered provider '{lease.ProviderId}'.",
                });
                continue;
            }

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(options.RevocationTimeout);
                await provider.RevokeAsync(lease.LeaseId, attemptCts.Token).ConfigureAwait(false);
                await _store.MarkRevokedAsync(lease.LeaseId, ct).ConfigureAwait(false);
                revoked.Add(lease.LeaseId);
                _log?.LogInformation(
                    "Revoked secret lease '{LeaseId}' (work item {WorkItemId}).",
                    lease.LeaseId, lease.WorkItemId);
            }
            catch (Exception ex)
            {
                await _store.MarkRevocationFailedAsync(lease.LeaseId, ex.Message, ct).ConfigureAwait(false);
                _log?.LogError(
                    ex,
                    "Secret lease revocation failed for lease '{LeaseId}' (work item {WorkItemId}); recorded and will be retried.",
                    lease.LeaseId, lease.WorkItemId);
                failures.Add(new LeaseRevocationFailure { LeaseId = lease.LeaseId, Error = ex.Message });
            }
        }

        return new LeaseRevocationReport
        {
            RevokedLeaseIds = revoked.AsReadOnly(),
            Failures = failures.AsReadOnly(),
        };
    }

    private static DateTimeOffset CapExpiry(
        DateTimeOffset renewedExpiry,
        DateTimeOffset createdAt,
        DateTimeOffset? itemDeadline,
        SecretLeasingOptions options)
    {
        var capped = renewedExpiry;
        if (itemDeadline.HasValue)
            capped = SecretLeasePolicy.CapAtItemDeadline(capped, itemDeadline.Value);
        var absoluteCap = createdAt + options.MaxLeaseLifetime;
        if (capped > absoluteCap)
            capped = absoluteCap;
        return capped;
    }
}
