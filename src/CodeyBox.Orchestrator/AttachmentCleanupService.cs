using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background sweep that bounds attachment storage growth. Runs two passes on
/// independent cadences:
/// <list type="bullet">
///   <item><b>Terminal-state cleanup</b> — removes metadata rows + reference-
///   counted blobs for work items that have been in a terminal state longer
///   than the configured TTL.</item>
///   <item><b>Orphan-blob sweep</b> — removes blobs on disk that have no
///   metadata row referring to them. Protected by a grace window so a freshly
///   staged blob whose metadata write is still pending is not deleted out
///   from under the upload.</item>
/// </list>
/// </summary>
public sealed class AttachmentCleanupService : BackgroundService
{
    private readonly IWorkItemAttachmentStore _store;
    private readonly IWorkItemAttachmentAdminBlobStore _blobs;
    private readonly Func<AttachmentsOptions> _options;
    private readonly ILogger<AttachmentCleanupService> _log;
    private readonly TimeProvider _time;

    public AttachmentCleanupService(
        IWorkItemAttachmentStore store,
        IWorkItemAttachmentAdminBlobStore blobs,
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
        // Tick at the shorter of the two cadences; each pass runs only when its
        // own interval has elapsed.
        var opts = _options();
        var tick = TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(30).Ticks,
            Math.Min(opts.CleanupSweepInterval.Ticks, opts.OrphanSweepInterval.Ticks)));

        var lastTerminal = DateTimeOffset.MinValue;
        var lastOrphan = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(tick, _time);
        do
        {
            try
            {
                opts = _options();
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
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
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
            var rows = await _store.DeleteAllForWorkItemAsync(itemId, ct).ConfigureAwait(false);
            if (rows.Count == 0) continue;

            foreach (var row in rows)
            {
                var stillReferenced = await _store.CountReferencesAsync(row.Sha256, ct).ConfigureAwait(false);
                if (stillReferenced == 0)
                    _blobs.TryDelete(row.Sha256);
            }
            deletedItems++;
        }
        if (deletedItems > 0)
            _log.LogInformation(
                "AttachmentCleanup: terminal sweep removed attachments for {Items} work item(s) older than {Cutoff:o}",
                deletedItems, cutoff);
        return deletedItems;
    }

    internal async Task<int> RunOrphanSweepAsync(
        AttachmentsOptions opts,
        CancellationToken ct)
    {
        var onDisk = _blobs.EnumerateHashes();
        if (onDisk.Count == 0) return 0;

        var referenced = await _store.ListReferencedHashesAsync(ct).ConfigureAwait(false);
        var referencedSet = referenced is HashSet<string> hs
            ? hs
            : new HashSet<string>(referenced, StringComparer.Ordinal);

        var grace = opts.OrphanGracePeriod;
        var deleted = 0;
        foreach (var hash in onDisk)
        {
            if (referencedSet.Contains(hash)) continue;
            // Re-check existence + age right before delete: a concurrent upload
            // can promote a temp file into its final path between EnumerateHashes
            // and now, and we must respect the grace window so its metadata write
            // has a chance to land before we treat the blob as orphaned.
            if (!_blobs.Exists(hash)) continue;
            if (grace > TimeSpan.Zero)
            {
                var stream = _blobs.OpenRead(hash);
                if (stream is null) continue;
                stream.Dispose();
                var path = BlobPathForGraceCheck(hash);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        var age = _time.GetUtcNow() - new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
                        if (age < grace) continue;
                    }
                }
                catch (IOException) { /* race; skip */ continue; }
            }
            if (_blobs.TryDelete(hash)) deleted++;
        }
        if (deleted > 0)
            _log.LogInformation("AttachmentCleanup: orphan sweep removed {Count} unreferenced blob(s)", deleted);
        return deleted;
    }

    private string BlobPathForGraceCheck(string hash) =>
        Path.Combine(_blobs.CurrentRoot, hash[..2], hash);
}
