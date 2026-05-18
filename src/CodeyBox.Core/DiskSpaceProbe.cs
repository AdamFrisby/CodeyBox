namespace CodeyBox.Core;

/// <summary>
/// Free-space probe abstraction. Production code uses
/// <see cref="DefaultDiskSpaceProbe"/> (DriveInfo-backed); tests substitute
/// an in-memory implementation to drive the disk-guard branches without
/// touching the host filesystem.
/// </summary>
public interface IDiskSpaceProbe
{
    /// <summary>
    /// Returns free bytes available to the unprivileged caller on the volume
    /// that contains <paramref name="path"/>. Returns <c>null</c> when the
    /// path does not resolve to any volume (e.g. the directory has been
    /// deleted) or when the platform refuses to report — in which case the
    /// caller treats the probe as inconclusive and must not refuse work.
    /// </summary>
    long? GetFreeBytes(string path);
}

/// <summary>
/// DriveInfo-backed probe. Walks up the path until it finds a parent that
/// exists, because <see cref="DriveInfo"/> requires an extant directory but
/// the multipass staging root may not have been created yet on a fresh host.
/// </summary>
public sealed class DefaultDiskSpaceProbe : IDiskSpaceProbe
{
    public long? GetFreeBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var resolved = ResolveExistingAncestor(path);
            if (resolved is null) return null;
            var drive = new DriveInfo(resolved);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExistingAncestor(string path)
    {
        var candidate = Path.GetFullPath(path);
        for (var i = 0; i < 16; i++)
        {
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || parent == candidate) return null;
            candidate = parent;
        }
        return null;
    }
}

/// <summary>
/// Optional provider-level capability: sandbox providers that maintain a
/// free-disk preflight expose the per-mount snapshot through this interface
/// so dashboards, /healthz, and the startup banner can render the same view
/// the guard uses to decide deferrals — without the API layer taking a
/// concrete-type dependency on any single provider implementation.
/// </summary>
public interface IDiskGuardedSandboxProvider
{
    /// <summary>
    /// Returns the current snapshot for each monitored mount. Empty when
    /// the implementation's disk-guard is unconfigured / disabled.
    /// </summary>
    IReadOnlyList<DiskGuardSample> SampleDiskGuardState();
}

/// <summary>
/// One row of <see cref="IDiskGuardedSandboxProvider.SampleDiskGuardState"/>.
/// <c>FreeBytes</c> is <c>null</c> when the probe could not resolve the
/// mount (treated as inconclusive — the preflight does not block work in
/// that case).
/// </summary>
public sealed record DiskGuardSample(string Path, long? FreeBytes, long ThresholdBytes);

/// <summary>
/// Thrown by a sandbox provider's <c>CreateAsync</c> when a configured
/// disk-space preflight refuses to launch because free space on one of the
/// monitored mounts dropped below the threshold. The orchestrator catches
/// this, schedules a deferred re-pickup, and fires a <c>disk.deferred</c>
/// webhook — same semantics as a budget cap.
/// </summary>
public sealed class SandboxDiskDeferredException : Exception
{
    public SandboxDiskDeferredException(
        string mountPath,
        long freeBytes,
        long thresholdBytes,
        TimeSpan recheckIn)
        : base(BuildMessage(mountPath, freeBytes, thresholdBytes))
    {
        MountPath = mountPath;
        FreeBytes = freeBytes;
        ThresholdBytes = thresholdBytes;
        RecheckIn = recheckIn;
    }

    public string MountPath { get; }
    public long FreeBytes { get; }
    public long ThresholdBytes { get; }
    public TimeSpan RecheckIn { get; }

    private static string BuildMessage(string mount, long free, long threshold) =>
        $"disk preflight: only {free:N0} bytes free on '{mount}' (threshold {threshold:N0})";
}

/// <summary>
/// Thrown by the SQLite work-item store when the underlying database write
/// fails with <c>SQLITE_FULL</c> (primary error code 13). Surfacing a typed
/// exception lets the orchestrator stop accepting new work cleanly instead
/// of letting the raw <c>Microsoft.Data.Sqlite.SqliteException</c> escape
/// out of HTTP handlers as an unredacted stack trace.
/// </summary>
public sealed class WorkItemStoreDiskFullException : Exception
{
    public WorkItemStoreDiskFullException(string operation, Exception inner)
        : base($"SQLite reported SQLITE_FULL during '{operation}' — host disk is exhausted", inner)
    {
        Operation = operation;
    }

    public string Operation { get; }
}
