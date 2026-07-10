namespace CodeyBox.Core;

/// <summary>
/// Result of staging an upload stream to host disk.
/// </summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the staged bytes.</param>
/// <param name="SizeBytes">Number of bytes received from the stream.</param>
/// <param name="WasDeduplicated">
/// True when a blob with the same hash was already on disk and the staged
/// upload was discarded. False when the upload promoted a fresh blob into the
/// content-addressed root.
/// </param>
public sealed record AttachmentBlobStageResult(string Sha256, long SizeBytes, bool WasDeduplicated);

/// <summary>
/// Thrown when an upload exceeds the configured max-file-size limit. The
/// blob store cancels the stream and removes the partial temp file before
/// throwing.
/// </summary>
public sealed class AttachmentBlobTooLargeException : Exception
{
    public long LimitBytes { get; }
    public AttachmentBlobTooLargeException(long limitBytes)
        : base($"Attachment exceeds the configured maximum size of {limitBytes} bytes.")
    {
        LimitBytes = limitBytes;
    }
}

/// <summary>
/// Host-filesystem blob storage for work-item attachments. Blobs are written
/// content-addressed under a configured root (default
/// <c>~/.codeybox/attachments/&lt;sha256-prefix&gt;/&lt;sha256&gt;</c>). The store
/// streams uploads to a temp file, hashes on the way, and atomically promotes
/// the temp file into its final path. Identical bytes (same SHA-256) are
/// deduplicated — repeated uploads of the same blob keep a single on-disk copy.
/// </summary>
public interface IWorkItemAttachmentBlobStore
{
    /// <summary>
    /// Streams <paramref name="source"/> into the content-addressed root,
    /// returning the resulting blob hash and size. Enforces
    /// <paramref name="maxBytes"/>: throws
    /// <see cref="AttachmentBlobTooLargeException"/> once that many bytes have
    /// been received, removing the partial temp file before the throw.
    /// </summary>
    Task<AttachmentBlobStageResult> StageAsync(
        Stream source,
        long maxBytes,
        CancellationToken ct = default);

    /// <summary>
    /// Opens a read-only stream over the blob identified by <paramref name="sha256"/>.
    /// Returns null when no blob with that hash is on disk.
    /// </summary>
    Stream? OpenRead(string sha256);

    /// <summary>True if a blob with the given hash exists on disk.</summary>
    bool Exists(string sha256);
}
