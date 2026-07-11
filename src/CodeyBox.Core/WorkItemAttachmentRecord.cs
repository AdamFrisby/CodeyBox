namespace CodeyBox.Core;

/// <summary>
/// Persisted attachment metadata for a work item. The blob itself lives on the
/// host filesystem under a content-addressed root (sha256-keyed); this record
/// is the SQLite-resident index pointing at it.
/// </summary>
/// <remarks>
/// The same blob (identical <see cref="Sha256"/>) is deduplicated across
/// attachments — multiple records may reference one on-disk blob. Blob cleanup
/// is reference-counted: a blob is only deleted when the last metadata row
/// referencing it goes away.
/// </remarks>
public sealed record WorkItemAttachmentRecord
{
    /// <summary>Random opaque identifier for this attachment row.</summary>
    public required string Id { get; init; }

    /// <summary>Work item this attachment belongs to.</summary>
    public required WorkItemId WorkItemId { get; init; }

    /// <summary>
    /// Operator-facing filename, sanitized at upload time. Never contains
    /// path separators; never used to build host paths (the on-disk filename
    /// is the sha256).
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>MIME type. Empty when the uploader did not declare one.</summary>
    public required string ContentType { get; init; }

    /// <summary>Blob size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the blob bytes. Doubles as the on-disk filename.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Optional operator-supplied note explaining what this attachment is for.</summary>
    public string Caption { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
