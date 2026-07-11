namespace CodeyBox.Core;

/// <summary>
/// Admin extension of <see cref="IWorkItemAttachmentBlobStore"/> for the
/// composition root and the orphan-sweep background service. Adds the
/// mutation and enumeration operations the cleanup path needs without
/// leaking the on-disk layout (sharded root, temp directory) through the
/// abstraction — callers ask for "age of this blob" or "sweep stale temp
/// files" rather than reconstructing filesystem paths themselves.
/// </summary>
public interface IWorkItemAttachmentBlobStoreAdmin : IWorkItemAttachmentBlobStore
{
    /// <summary>
    /// Deletes the blob from disk if present. Returns true when a blob was
    /// removed, false when no blob with that hash was on disk. Callers must
    /// ensure the blob has no remaining metadata references before calling,
    /// or rely on the orphan-sweep grace window to protect in-flight uploads.
    /// </summary>
    bool TryDelete(string sha256);

    /// <summary>
    /// Returns every lowercase-hex SHA-256 currently on disk in the
    /// content-addressed root. Used by the orphan sweep to compute
    /// (on-disk hashes) MINUS (referenced hashes).
    /// </summary>
    IReadOnlyCollection<string> EnumerateHashes();

    /// <summary>
    /// Returns the UTC last-write time of the blob, or null when no blob
    /// with that hash is on disk. The orphan sweep uses this (rather than
    /// <see cref="System.IO.File.GetCreationTimeUtc"/>) because last-write
    /// time is reliably maintained on every common Linux filesystem and is
    /// refreshed by every stage / dedup touch, so the grace window protects
    /// freshly-staged-and-not-yet-referenced blobs portably.
    /// </summary>
    DateTimeOffset? GetBlobLastWriteTimeUtc(string sha256);

    /// <summary>
    /// Removes staged-but-never-promoted temp files (left behind by
    /// interrupted uploads: client disconnect, process kill, host crash)
    /// that are older than <paramref name="grace"/>. Returns the count
    /// removed. Without this sweep the <c>.tmp</c> directory grows without
    /// bound on every crashed upload.
    /// </summary>
    int SweepTempFiles(TimeSpan grace);
}
