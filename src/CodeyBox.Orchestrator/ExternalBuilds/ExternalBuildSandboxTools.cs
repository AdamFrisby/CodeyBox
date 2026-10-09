using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Host-issued scoped caller identity for sandbox build tools. Issued and
/// scoped by the host to project/work item/phase/iteration/attempt with an
/// expiry; rechecked at every sink. Stale or foreign handles are rejected.
/// </summary>
public sealed record ExternalBuildCapability(
    string Handle,
    string ProjectId,
    string WorkItemId,
    string Phase,
    int Iteration,
    int Attempt,
    DateTimeOffset ExpiresAt,
    bool Revoked);

/// <summary>
/// Sandbox-facing build/start, status, result, cancel, bounded diagnostics
/// and artifact list/read tools. The host owns provider credentials and
/// publication; the sandbox receives only this expiring revocable capability.
/// Majordomo credentials are never reused and its operator vocabulary is not
/// exposed here.
/// </summary>
public sealed class ExternalBuildSandboxTools
{
    private readonly ExternalBuildService _service;
    private readonly IExternalBuildStore _store;
    private readonly Func<ExternalBuildOptions> _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, ExternalBuildCapability> _capabilities = new(StringComparer.Ordinal);

    public ExternalBuildSandboxTools(
        ExternalBuildService service,
        IExternalBuildStore store,
        Func<ExternalBuildOptions> options,
        TimeProvider? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    public ExternalBuildCapability IssueCapability(
        string projectId, string workItemId, string phase, int iteration, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var cap = new ExternalBuildCapability(
            "xbc-" + Guid.NewGuid().ToString("N")[..16],
            projectId, workItemId, phase, iteration, attempt,
            _clock.GetUtcNow().AddSeconds(_options().CapabilityLifetimeSeconds), false);
        lock (_gate) _capabilities[cap.Handle] = cap;
        return cap;
    }

    public void Revoke(string handle)
    {
        lock (_gate)
        {
            if (_capabilities.TryGetValue(handle, out var cap))
                _capabilities[handle] = cap with { Revoked = true };
        }
    }

    public async Task<ExternalBuildRecord> StartAsync(
        string handle, string approvedTarget, ExternalBuildSourceIdentity source,
        string? idempotencyKey, CancellationToken ct = default)
    {
        var cap = Require(handle);
        var request = new ExternalBuildStartRequest
        {
            ProjectId = cap.ProjectId,
            WorkItemId = cap.WorkItemId,
            Phase = cap.Phase,
            Iteration = cap.Iteration,
            Attempt = cap.Attempt,
            ApprovedTargetName = approvedTarget,
            Source = source,
            IdempotencyKey = idempotencyKey,
        };
        var input = new ExternalBuildSubmitInput();
        return await _service.StartAsync(request, input, ct).ConfigureAwait(false);
    }

    public async Task<ExternalBuildRecord> StatusAsync(string handle, string buildId, CancellationToken ct = default)
    {
        var cap = Require(handle);
        var record = await OwnedAsync(cap, buildId, ct).ConfigureAwait(false);
        return await _service.ReconcileAsync(record.Id, ct).ConfigureAwait(false);
    }

    public async Task<ExternalBuildRecord> ResultAsync(string handle, string buildId, CancellationToken ct = default)
        => await StatusAsync(handle, buildId, ct).ConfigureAwait(false);

    public async Task<ExternalBuildRecord> CancelAsync(string handle, string buildId, CancellationToken ct = default)
    {
        var cap = Require(handle);
        await OwnedAsync(cap, buildId, ct).ConfigureAwait(false);
        return await _service.CancelAsync(buildId, ExternalBuildTerminalCause.UserCancelled, ct).ConfigureAwait(false);
    }

    public async Task<string> DiagnosticsAsync(string handle, string buildId, CancellationToken ct = default)
    {
        var cap = Require(handle);
        var record = await OwnedAsync(cap, buildId, ct).ConfigureAwait(false);
        var raw = record.FailureDetail ?? record.Evidence?.WorkflowIdentity ?? "no diagnostics yet";
        var redacted = ExternalBuildArtifactGuard.Redact(raw);
        return ExternalBuildArtifactGuard.TruncateBounded(redacted, _options().MaxDiagnosticsChars);
    }

    public async Task<IReadOnlyList<ExternalBuildArtifactRef>> ListArtifactsAsync(
        string handle, string buildId, IExternalBuildProvider provider, CancellationToken ct = default)
    {
        var cap = Require(handle);
        var record = await OwnedAsync(cap, buildId, ct).ConfigureAwait(false);
        if (record.ProviderRunId is null) return [];
        var refs = await provider.ListArtifactsAsync(record.ProviderRunId, ct).ConfigureAwait(false);
        var opts = _options();
        var safe = new List<ExternalBuildArtifactRef>();
        foreach (var r in refs.Take(opts.MaxArtifactsPerBuild))
            if (ExternalBuildArtifactGuard.ValidateRef(r, opts) is null)
                safe.Add(r);
        return safe;
    }

    public async Task<ExternalBuildArtifactPayload> ReadArtifactAsync(
        string handle, string buildId, string artifactName, IExternalBuildProvider provider, CancellationToken ct = default)
    {
        var cap = Require(handle);
        var record = await OwnedAsync(cap, buildId, ct).ConfigureAwait(false);
        if (record.ProviderRunId is null)
            throw new InvalidOperationException("Build has no provider run yet.");
        var payload = await provider.ReadArtifactAsync(record.ProviderRunId, artifactName, ct).ConfigureAwait(false);
        var error = ExternalBuildArtifactGuard.ValidatePayload(payload, _options());
        if (error is not null) throw new InvalidOperationException("Artifact rejected: " + error);
        return payload;
    }

    private ExternalBuildCapability Require(string handle)
    {
        ExternalBuildCapability? cap;
        lock (_gate) _capabilities.TryGetValue(handle, out cap);
        if (cap is null) throw new ExternalBuildCapabilityExpiredException();
        if (cap.Revoked) throw new ExternalBuildCapabilityExpiredException();
        if (_clock.GetUtcNow() >= cap.ExpiresAt) throw new ExternalBuildCapabilityExpiredException();
        return cap;
    }

    private async Task<ExternalBuildRecord> OwnedAsync(
        ExternalBuildCapability cap, string buildId, CancellationToken ct)
    {
        var record = await _store.GetAsync(buildId, ct).ConfigureAwait(false)
            ?? throw new ExternalBuildOwnershipException(buildId);
        if (!string.Equals(record.ProjectId, cap.ProjectId, StringComparison.Ordinal)
            || !string.Equals(record.WorkItemId, cap.WorkItemId, StringComparison.Ordinal)
            || !string.Equals(record.Phase, cap.Phase, StringComparison.Ordinal)
            || record.Iteration != cap.Iteration
            || record.Attempt != cap.Attempt)
            throw new ExternalBuildOwnershipException(buildId);
        return record;
    }
}
