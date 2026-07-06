using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background sweep that bounds attachment storage growth. Runs three passes
/// on independent cadences:
/// <list type="bullet">
///   <item><b>Terminal-state cleanup</b> — removes metadata rows for work
///   items that have been terminal longer than the configured TTL. On-disk
///   blobs are NOT deleted here: a concurrent upload of the same bytes may
///   have staged a dedup'd copy and be about to write its metadata row, and
///   deleting the blob out from under it would orphan that row. The orphan
///   sweep reclaims unreferenced blobs after the grace window.</item>
///   <item><b>Orphan-blob sweep</b> — removes blobs on disk that have no
///   metadata row referring to them. Protected by a grace window (sourced
///   from the blob's last-write time, which is refreshed on every stage /
///   dedup) so a freshly-staged blob whose metadata write is still pending is
///   not deleted out from under the upload.</item>
///   <item><b>Temp-file sweep</b> — removes staged-but-never-promoted temp
///   files left behind by interrupted uploads (client disconnect, process
///   kill, host crash) that are older than the grace window. Without this the
///   <c>.tmp</c> directory grows without bound on every crashed upload.</item>
/// </list>
/// </summary>
public sealed class AttachmentCleanupService : BackgroundService
{
    private static readonly TimeSpan MinTickFloor = TimeSpan.FromSeconds(30);

    private readonly IWorkItemAttachmentStore _store;
    private readonly IWorkItemAttachmentBlobStoreAdmin _blobs;
    private readonly Func<AttachmentsOptions> _options;
    private readonly ILogger<AttachmentCleanupService> _log;
    private readonly TimeProvider _time;

    public AttachmentCleanupService(
        IWorkItemAttachmentStore store,
        IWorkItemAttachmentBlobStoreAdmin blobs,
        Func<AttachmentsOptions> options,
        ILogger<AttachmentCleanupService> log,
        TimeProvider? time = null)
    {
        _store = store;
        _blobs = blobs;
        _options = options;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer the first sweep by one tick so a fleet-wide restart does not
        // thundering-herd every host's SQL scan + directory enumeration at
        // boot. Subsequent passes run on their own cadences.
        var opts = _options();
        var tick = ComputeTick(opts);
        await DelayAsync(tick, stoppingToken).ConfigureAwait(false);

        var lastTerminal = DateTimeOffset.MinValue;
        var lastOrphan = DateTimeOffset.MinValue;

        // Drive the loop on Task.Delay with the fresh opts value each cycle
        // so a hot-reload of CleanupSweepInterval / OrphanSweepInterval is
        // honoured immediately, not just on the elapsed-since-last branch.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                opts = _options();
                tick = ComputeTick(opts);
                var now = _time.GetUtcNow();
                if (now - lastTerminal >= opts.CleanupSweepInterval)
                {
                    lastTerminal = now;
                    await RunTerminalCleanupAsync(opts, now, stoppingToken).ConfigureAwait(false);
                }
                if (now - lastOrphan >= opts.OrphanSweepInterval)
                {
                    lastOrphan = now;
                    await RunOrphanSweepAsync(opts, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "AttachmentCleanup: sweep failed");
            }

            try { await DelayAsync(tick, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (_time == TimeProvider.System)
            await Task.Delay(delay, ct).ConfigureAwait(false);
        else
        {
            // Honour the injected clock in tests: spin on a short sleep so the
            // ManualTimeProvider's Advance() can unblock the loop promptly.
            var remaining = delay;
            while (remaining > TimeSpan.Zero && !ct.IsCancellationRequested)
            {
                var step = TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds));
                await Task.Delay(step, ct).ConfigureAwait(false);
                remaining -= step;
            }
        }
    }

    private static TimeSpan ComputeTick(AttachmentsOptions opts)
    {
        var tick = TimeSpan.FromTicks(Math.Min(opts.CleanupSweepInterval.Ticks, opts.OrphanSweepInterval.Ticks));
        return tick < MinTickFloor ? MinTickFloor : tick;
    }

    internal async Task<int> RunTerminalCleanupAsync(
        AttachmentsOptions opts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var cutoff = now - opts.TerminalCleanupTtl;
        var deletedItems = 0;
        await foreach (var itemId in _store.ListTerminalWithAttachmentsAsync(cutoff, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            // Delete metadata rows only. The on-disk blobs are left for the
            // orphan sweep: a concurrent upload of the same bytes may have a
            // staged dedup copy whose metadata row has not landed yet, and
            // deleting the blob here would orphan that row.
            var rows = await _store.DeleteAllForWorkItemAsync(itemId, ct).ConfigureAwait(false);
            if (rows.Count == 0) continue;
            deletedItems++;
        }
        if (deletedItems > 0)
            _log.LogInformation(
                "AttachmentCleanup: terminal sweep removed attachment metadata for {Items} work item(s) older than {Cutoff:o}",
                deletedItems, cutoff);
        return deletedItems;
    }

    internal async Task<int> RunOrphanSweepAsync(
        AttachmentsOptions opts,
        CancellationToken ct)
    {
        var onDisk = _blobs.EnumerateHashes();
        var referenced = await _store.ListReferencedHashesAsync(ct).ConfigureAwait(false);
        var referencedSet = referenced is HashSet<string> hs
            ? hs
            : new HashSet<string>(referenced, StringComparer.Ordinal);

        var grace = opts.OrphanGracePeriod;
        var now = _time.GetUtcNow();
        var deleted = 0;
        foreach (var hash in onDisk)
        {
            ct.ThrowIfCancellationRequested();
            if (referencedSet.Contains(hash)) continue;
            // Grace-window check via the blob store's last-write time (not
            // filesystem CreationTimeUtc, which is missing on stock ext4 and
            // silently returns a sentinel). Every stage / dedup touch
            // refreshes last-write, so an in-flight upload whose metadata row
            // has not landed yet reads as fresh and is protected.
            if (grace > TimeSpan.Zero)
            {
                var lastWrite = _blobs.GetBlobLastWriteTimeUtc(hash);
                if (lastWrite is { } lw && now - lw < grace)
                    continue;
            }
            if (_blobs.TryDelete(hash)) deleted++;
        }

        // Sweep staged-but-never-promoted temp files from interrupted uploads.
        // These never appear in EnumerateHashes (the .tmp directory is not a
        // hex shard), so without a dedicated pass they accumulate forever on
        // every crashed upload.
        var tempDeleted = _blobs.SweepTempFiles(grace);

        var total = deleted + tempDeleted;
        if (total > 0)
            _log.LogInformation(
                "AttachmentCleanup: orphan sweep removed {Blobs} unreferenced blob(s) and {Temp} stale temp file(s)",
                deleted, tempDeleted);
        return total;
    }
}
