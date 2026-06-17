namespace CodeyBox.Core;

/// <summary>
/// SQLite-resident metadata index for work-item attachments. The blob bytes
/// live on the host filesystem under a content-addressed root keyed by
/// <see cref="WorkItemAttachmentRecord.Sha256"/>; this store only owns the
/// per-attachment row pointing at them.
/// </summary>
public interface IWorkItemAttachmentStore
{
    /// <summary>Inserts a new attachment metadata row.</summary>
    Task CreateAsync(WorkItemAttachmentRecord record, CancellationToken ct = default);

    /// <summary>Gets a single attachment by id, or null if not found.</summary>
    Task<WorkItemAttachmentRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Lists all attachments for a work item, oldest first.</summary>
    Task<IReadOnlyList<WorkItemAttachmentRecord>> ListForWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default);

    /// <summary>Aggregate count + total size for a single work item's attachments.</summary>
    Task<(int Count, long TotalBytes)> AggregateForWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the metadata row by id and returns the deleted record (or null
    /// if it did not exist). Caller is responsible for reference-counting the
    /// underlying blob via <see cref="CountReferencesAsync"/> before deleting
    /// it from disk.
    /// </summary>
    Task<WorkItemAttachmentRecord?> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Number of metadata rows still pointing at the given blob hash. Callers
    /// use this to decide whether to delete the on-disk blob: if zero, the
    /// blob is unreferenced and may be removed.
    /// </summary>
    Task<int> CountReferencesAsync(string sha256, CancellationToken ct = default);

    /// <summary>
    /// Returns every distinct sha256 the store currently references. Used by
    /// the orphan blob sweep to compute (on-disk hashes) MINUS (referenced
    /// hashes).
    /// </summary>
    Task<IReadOnlyCollection<string>> ListReferencedHashesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the work items eligible for terminal-state cleanup: those whose
    /// state is terminal AND whose updated_at is older than
    /// <paramref name="olderThan"/>, with at least one attachment row.
    /// </summary>
    IAsyncEnumerable<WorkItemId> ListTerminalWithAttachmentsAsync(
        DateTimeOffset olderThan,
        CancellationToken ct = default);

    /// <summary>Deletes every attachment row for a work item and returns their previous records.</summary>
    Task<IReadOnlyList<WorkItemAttachmentRecord>> DeleteAllForWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default);
}
