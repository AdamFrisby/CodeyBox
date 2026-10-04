using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Holds executor-uploaded stage-out tars between the upload call and the
/// completion call of the two-call complete protocol
/// (<c>POST phase/stageout</c> then <c>POST phase/complete</c>). Keyed by
/// (host, dispatch key) with exact host ownership: one host can never
/// overwrite or complete with another host's upload. Entries expire under
/// the lease clock and are swept on every operation so an executor that
/// uploads but never completes cannot pin orchestrator temp disk forever.
/// </summary>
public sealed class ExecutorPhaseStageOutUploads : IDisposable
{
    private readonly object _mutex = new();
    private readonly Dictionary<(string HostId, string DispatchKey), UploadEntry> _uploads = new();
    private readonly TimeProvider _clock;
    private bool _disposed;

    public ExecutorPhaseStageOutUploads(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Stores <paramref name="tarPath"/> for (<paramref name="hostId"/>,
    /// <paramref name="dispatchKey"/>), expiring at <paramref name="expiresAt"/>.
    /// Replaces any prior upload for the same (host, key) pair (deleting the
    /// orphaned file). An upload from another host for the same dispatch key
    /// is stored separately and never overwrites or deletes this entry, so a
    /// foreign host cannot destroy a dispatch's staged-back tar.
    /// </summary>
    public void Put(string hostId, string dispatchKey, string tarPath, DateTimeOffset expiresAt)
    {
        var host = ExecutorPhaseBroker.NormalizeHostId(hostId);
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tarPath);
        lock (_mutex)
        {
            ThrowIfDisposed();
            SweepExpiredLocked(_clock.GetUtcNow());
            var mapKey = (host, key);
            if (_uploads.Remove(mapKey, out var prior) && !string.Equals(prior.TarPath, tarPath, StringComparison.Ordinal))
                DeleteQuietly(prior.TarPath);
            _uploads[mapKey] = new UploadEntry(host, tarPath, expiresAt);
        }
    }

    /// <summary>
    /// Takes the uploaded tar for (<paramref name="hostId"/>,
    /// <paramref name="dispatchKey"/>), removing the entry. Ownership is
    /// exact-match; unknown, expired, or foreign entries throw
    /// <see cref="ExecutorPhaseTransportException"/> without touching any
    /// other host's upload.
    /// </summary>
    public string Take(string? hostId, string? dispatchKey)
    {
        var host = ExecutorPhaseBroker.NormalizeHostId(hostId);
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        lock (_mutex)
        {
            ThrowIfDisposed();
            SweepExpiredLocked(_clock.GetUtcNow());
            if (!_uploads.Remove((host, key), out var entry))
                throw new ExecutorPhaseTransportException(host, "phase-complete", "No stage-out upload matches this dispatch key.");
            return entry.TarPath;
        }
    }

    public void Dispose()
    {
        List<UploadEntry> entries;
        lock (_mutex)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _uploads.Values];
            _uploads.Clear();
        }
        foreach (var entry in entries)
            DeleteQuietly(entry.TarPath);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ExecutorPhaseTransportException("(unknown)", "phase-complete", "The dispatch broker shut down.");
    }

    private void SweepExpiredLocked(DateTimeOffset now)
    {
        foreach (var (key, entry) in _uploads.ToArray())
        {
            if (entry.ExpiresAt <= now)
            {
                _uploads.Remove(key);
                DeleteQuietly(entry.TarPath);
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private sealed record UploadEntry(string HostId, string TarPath, DateTimeOffset ExpiresAt);
}
