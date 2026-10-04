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
    /// A per-host pending-upload cap (<paramref name="maxPerHost"/>) bounds
    /// orchestrator temp disk: replacing the same (host, key) never counts
    /// against the cap, but a new key beyond the cap throws
    /// <see cref="ExecutorPhaseTransportException"/> and the caller must
    /// delete its temp file.
    /// </summary>
    public void Put(string hostId, string dispatchKey, string tarPath, DateTimeOffset expiresAt, int maxPerHost = 64)
    {
        if (maxPerHost <= 0)
            throw new InvalidOperationException("maxPerHost must be positive.");
        var host = ExecutorPhaseBroker.NormalizeHostId(hostId);
        var key = ExecutorPhaseBroker.NormalizeDispatchKey(dispatchKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tarPath);
        lock (_mutex)
        {
            ThrowIfDisposed();
            SweepExpiredLocked(_clock.GetUtcNow());
            var mapKey = (host, key);
            if (_uploads.Remove(mapKey, out var prior))
            {
                if (!string.Equals(prior.TarPath, tarPath, StringComparison.Ordinal))
                    DeleteQuietly(prior.TarPath);
            }
            else
            {
                var owned = 0;
                foreach (var existing in _uploads.Keys)
                {
                    if (string.Equals(existing.HostId, host, StringComparison.Ordinal))
                        owned++;
                }
                if (owned >= maxPerHost)
                    throw new ExecutorPhaseTransportException(host, "phase-stageout", "Too many pending stage-out uploads for this host.");
            }
            _uploads[mapKey] = new UploadEntry(host, tarPath, expiresAt);
        }
    }

    /// <summary>
    /// Pending stage-out upload count for <paramref name="hostId"/>,
    /// after sweeping expired entries. Used by tests to assert the
    /// per-host cap.
    /// </summary>
    public int CountForHost(string? hostId)
    {
        var host = ExecutorPhaseBroker.NormalizeHostId(hostId);
        lock (_mutex)
        {
            ThrowIfDisposed();
            SweepExpiredLocked(_clock.GetUtcNow());
            var owned = 0;
            foreach (var existing in _uploads.Keys)
            {
                if (string.Equals(existing.HostId, host, StringComparison.Ordinal))
                    owned++;
            }
            return owned;
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
