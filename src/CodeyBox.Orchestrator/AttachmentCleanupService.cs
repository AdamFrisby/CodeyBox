using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background sweep that bounds attachment storage growth. Runs three passes
/// on independent cadences:
/// <list type="bullet">
///   <item><b>Terminal/TTL cleanup</b> — removes metadata rows for work items
///   that are terminal or whose last update is older than the configured TTL,
///   then deletes any blob that no remaining metadata row references.</item>
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

        // Read fresh options each cycle so hot-reloaded cleanup intervals and
        // lifecycle windows apply to the next sweep decision and delay.
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
        await Task.Delay(delay, _time, ct).ConfigureAwait(false);
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
        var deletedBlobs = 0;
        await foreach (var itemId in _store.ListCleanupCandidatesWithAttachmentsAsync(cutoff, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await _store.DeleteAllForWorkItemAsync(itemId, ct).ConfigureAwait(false);
            if (rows.Count == 0) continue;
            deletedItems++;
            foreach (var hash in rows.Select(static r => r.Sha256).Distinct(StringComparer.Ordinal))
            {
                if (await _store.CountReferencesAsync(hash, ct).ConfigureAwait(false) == 0
                    && _blobs.TryDelete(hash))
                {
                    deletedBlobs++;
                }
            }
        }
        if (deletedItems > 0)
            _log.LogInformation(
                "AttachmentCleanup: terminal/TTL sweep removed attachment metadata for {Items} work item(s) and {Blobs} blob(s); TTL cutoff {Cutoff:o}",
                deletedItems, deletedBlobs, cutoff);
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
