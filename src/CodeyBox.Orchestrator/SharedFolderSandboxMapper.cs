using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Mounts project shared folders into sandbox specs and guards their
/// lifecycle. Shares are runtime mounts resolved per sandbox creation: the
/// phase filter and auditor-always-read-only rule apply here, a missing host
/// path fails required shares with an operator-visible error (never a silent
/// skip), and exclusive write leases are acquired before the provider sees
/// the spec so at most one running item holds a folder read-write.
/// </summary>
internal static class SharedFolderSandboxMapper
{
    /// <summary>
    /// Mounts applicable to one sandbox creation, plus the exclusive folders
    /// that still need a write lease before the provider creates the sandbox.
    /// </summary>
    internal sealed record Plan(
        IReadOnlyList<SandboxMount> Mounts,
        IReadOnlyList<ProjectSharedFolder> ExclusiveWriteFolders);

    /// <summary>
    /// Computes the mount plan for one phase. Pure except for the host-path
    /// existence probe and the log sink: no provider state, no gate.
    /// </summary>
    internal static Plan BuildPlan(
        string projectId,
        IReadOnlyList<ProjectSharedFolder> folders,
        string sandboxScope,
        bool isAudit,
        ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxScope);
        log ??= NullLogger.Instance;
        var mounts = new List<SandboxMount>();
        var exclusive = new List<ProjectSharedFolder>();
        if (folders.Count == 0)
            return new Plan(mounts, exclusive);
        foreach (var folder in folders)
        {
            if (!SharedFolderPolicy.AppliesToPhase(folder, sandboxScope))
                continue;
            var readOnly = SharedFolderPolicy.EffectiveReadOnly(folder, isAudit);
            if (!Directory.Exists(folder.HostPath) && !File.Exists(folder.HostPath))
            {
                if (folder.Required)
                {
                    throw new SandboxMountSourceMissingException(
                        folder.HostPath,
                        $"Project '{projectId}' shared folder '{folder.Name}': host path " +
                        $"'{folder.HostPath}' does not exist. Create it or remove the share; " +
                        $"the sandbox was not started.");
                }
                log.LogWarning(
                    "Project '{Project}' shared folder '{Folder}': optional host path '{HostPath}' is missing; " +
                    "continuing without this mount (reported in project status).",
                    projectId, folder.Name, folder.HostPath);
                continue;
            }
            mounts.Add(new SandboxMount
            {
                SandboxPath = folder.SandboxPath,
                HostPath = folder.HostPath,
                ReadOnly = readOnly,
            });
            if (!readOnly && folder.WriterPolicy == SharedFolderWriterPolicy.Exclusive)
                exclusive.Add(folder);
        }
        return new Plan(mounts, exclusive);
    }

    /// <summary>
    /// Routing-time check against one concrete provider. Throws
    /// <see cref="SandboxPlacementUnplaceableException"/> naming the missing
    /// capability so the item fails operator-visible instead of dispatching
    /// to a provider that cannot serve its required shares.
    /// </summary>
    internal static void ThrowIfProviderCannotServe(
        string projectId,
        IReadOnlyList<ProjectSharedFolder> folders,
        string sandboxScope,
        bool isAudit,
        IReadOnlyList<string> providerCapabilities,
        string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(folders);
        var (ok, reason) = SharedFolderPolicy.CheckProvider(
            folders, sandboxScope, isAudit, providerCapabilities, providerName);
        if (ok)
            return;
        var missing = reason.Contains(
            SandboxCapabilities.HostFolderSharesReadWrite, StringComparison.Ordinal)
            ? SandboxCapabilities.HostFolderSharesReadWrite
            : SandboxCapabilities.HostFolderShares;
        throw new SandboxPlacementUnplaceableException(missing, reason);
    }

    /// <summary>
    /// Capability tags to require from placement for this phase. Callers
    /// building a <see cref="SandboxPlacementAcquisition"/> append these to
    /// the work item's required capabilities so the decider refuses members
    /// whose provider cannot serve the project's required shares.
    /// </summary>
    internal static IReadOnlyList<string> PlacementCapabilities(
        IReadOnlyList<ProjectSharedFolder> folders,
        string sandboxScope,
        bool isAudit) =>
        SharedFolderPolicy.RequiredCapabilitiesForPhase(folders, sandboxScope, isAudit);

    /// <summary>
    /// Merges a plan's mounts into a spec's mount list. A share colliding
    /// with an existing mount destination fails required shares loudly;
    /// optional shares are skipped with a warning.
    /// </summary>
    internal static void AppendTo(
        List<SandboxMount> mounts,
        Plan plan,
        string projectId,
        IReadOnlyList<ProjectSharedFolder> folders,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(folders);
        log ??= NullLogger.Instance;
        var byDestination = new Dictionary<string, SandboxMount>(StringComparer.Ordinal);
        foreach (var mount in mounts)
            byDestination[mount.SandboxPath] = mount;
        foreach (var mount in plan.Mounts)
        {
            if (byDestination.ContainsKey(mount.SandboxPath))
            {
                var declaring = folders.FirstOrDefault(f =>
                    string.Equals(f.SandboxPath, mount.SandboxPath, StringComparison.Ordinal));
                if (declaring is not null && !declaring.Required)
                {
                    log.LogWarning(
                        "Project '{Project}' optional shared folder '{Folder}' collides with an existing " +
                        "mount at '{SandboxPath}'; skipping the share.",
                        projectId, declaring.Name, mount.SandboxPath);
                    continue;
                }
                throw new InvalidOperationException(
                    $"Project '{projectId}' shared folder mount '{mount.SandboxPath}' collides with an " +
                    "existing sandbox mount destination.");
            }
            byDestination.Add(mount.SandboxPath, mount);
            mounts.Add(mount);
        }
    }

    /// <summary>
    /// Flips the named destinations to read-only after an exclusive write
    /// lease was downgraded. Runs before the provider creates the sandbox so
    /// the downgrade is enforced at the VM/mount level.
    /// </summary>
    internal static void DowngradeToReadOnly(List<SandboxMount> mounts, IReadOnlySet<string> sandboxPaths)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(sandboxPaths);
        for (var i = 0; i < mounts.Count; i++)
        {
            if (sandboxPaths.Contains(mounts[i].SandboxPath) && !mounts[i].ReadOnly)
                mounts[i] = mounts[i] with { ReadOnly = true };
        }
    }

    /// <summary>
    /// Acquires exclusive write leases for a plan. Shared folders need no
    /// lease. A <see cref="SharedFolderExclusiveFallback.Wait"/> timeout
    /// surfaces as <see cref="SandboxProvisioningDeferredException"/> so the
    /// item requeues under the standard backoff instead of failing. Leases
    /// must be disposed when the sandbox is disposed (see
    /// <see cref="SharedFolderLeaseSandbox"/>).
    /// </summary>
    internal static async Task<IReadOnlyList<LeaseAcquisition>> AcquireLeasesAsync(
        string projectId,
        Plan plan,
        SharedFolderWriterGate? gate,
        string providerName,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        log ??= NullLogger.Instance;
        var acquired = new List<LeaseAcquisition>(plan.ExclusiveWriteFolders.Count);
        try
        {
            foreach (var folder in plan.ExclusiveWriteFolders)
            {
                if (gate is null)
                {
                    throw new InvalidOperationException(
                        $"Project '{projectId}' shared folder '{folder.Name}' requires exclusive write access, " +
                        "but no writer gate is wired; refusing to mount it read-write without coordination.");
                }
                var key = SharedFolderWriterGate.KeyFor(projectId, folder.Name);
                SharedFolderWriterLease lease;
                try
                {
                    lease = await gate.AcquireAsync(
                        key, folder.WriterPolicy, folder.ExclusiveFallback, ct: ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new SandboxProvisioningDeferredException(
                        providerName,
                        "shared-folder-exclusive-writer",
                        "exclusive-share-held",
                        $"Project '{projectId}' shared folder '{folder.Name}' is exclusively held by " +
                        $"another running item: {ex.Message}",
                        TimeSpan.FromSeconds(30),
                        innerException: ex);
                }
                acquired.Add(new LeaseAcquisition(folder, lease));
                if (lease.DowngradedToReadOnly)
                {
                    log.LogWarning(
                        "Project '{Project}' shared folder '{Folder}': write lease held by another running item; " +
                        "mounting read-only for this sandbox (configured ExclusiveFallback=ReadOnly).",
                        projectId, folder.Name);
                }
            }
            return acquired.AsReadOnly();
        }
        catch
        {
            foreach (var acquisition in acquired)
                await acquisition.Lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One acquired write posture bound to its declaring folder.</summary>
    internal sealed record LeaseAcquisition(ProjectSharedFolder Folder, SharedFolderWriterLease Lease);
}

/// <summary>
/// <see cref="ISandbox"/> decorator holding shared-folder exclusive write
/// leases for the sandbox's whole lifetime. Disposal releases the leases
/// after tearing the sandbox down, so at most one running item holds a
/// folder read-write at any moment.
/// </summary>
internal sealed class SharedFolderLeaseSandbox : ISandbox
{
    private readonly ISandbox _inner;
    private readonly IReadOnlyList<SharedFolderWriterLease> _leases;
    private bool _disposed;

    internal SharedFolderLeaseSandbox(ISandbox inner, IReadOnlyList<SharedFolderWriterLease> leases)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    }

    public string Id => _inner.Id;

    public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;

    public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
        _inner.ExecAsync(exec, ct);

    public Task SyncStateToHostAsync(CancellationToken ct = default) =>
        _inner.SyncStateToHostAsync(ct);

    public Task KillActiveExecsAsync(CancellationToken ct = default) =>
        _inner.KillActiveExecsAsync(ct);

    public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default) =>
        _inner.GetScreenshotAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var lease in _leases)
                await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
