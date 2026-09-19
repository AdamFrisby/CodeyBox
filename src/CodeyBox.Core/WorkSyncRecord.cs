namespace CodeyBox.Core;

/// <summary>
/// What CodeyBox wrote upstream (and what it chose not to write), so the
/// audit trail spans both systems. Written for every ingestion decision and
/// every tracker attempt — including skips and failures.
/// </summary>
public sealed record WorkSyncRecord
{
    /// <summary>Internal record id.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Work item this record belongs to. Null when ingestion was skipped pre-creation.</summary>
    public WorkItemId? WorkItemId { get; init; }

    /// <summary>Provider namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>External id in the provider's system.</summary>
    public required string ExternalId { get; init; }

    /// <summary>What happened.</summary>
    public required WorkSyncRecordKind Kind { get; init; }

    /// <summary>Declared external status at write time, when applicable.</summary>
    public string? ExternalStatus { get; init; }

    /// <summary>Upstream comment/status id when a write succeeded.</summary>
    public string? RemoteId { get; init; }

    /// <summary>True when the upstream write (or intentional skip) completed as decided.</summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>Failure or skip detail. Never a stack trace, never a secret.</summary>
    public string? Detail { get; init; }

    /// <summary>When the record was written.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Kinds of cross-system sync events.</summary>
public enum WorkSyncRecordKind
{
    /// <summary>An external item became a work item.</summary>
    Ingested,
    /// <summary>Ingestion skipped: no signal, CodeyBox-authored, or duplicate.</summary>
    IngestionSkipped,
    /// <summary>Progress posted on a state transition.</summary>
    Progress,
    /// <summary>A question was surfaced upstream.</summary>
    Question,
    /// <summary>An upstream reply was accepted as a question answer.</summary>
    Answer,
    /// <summary>Commit / pull-request links reported upstream.</summary>
    Links,
    /// <summary>Terminal completion or failure reported upstream.</summary>
    Completion,
    /// <summary>A state had no declared mapping and was reported as unmapped.</summary>
    UnmappedState,
    /// <summary>An upstream write failed; the work item is unaffected.</summary>
    SyncFailed,
    /// <summary>The ingestion signal was removed upstream; records the behavior applied.</summary>
    SignalRemoved,
}

/// <summary>Durable store for <see cref="WorkSyncRecord"/> audit entries.</summary>
public interface IWorkSyncRecordStore
{
    /// <summary>Appends a record.</summary>
    Task RecordAsync(WorkSyncRecord record, CancellationToken ct = default);

    /// <summary>Lists records for a work item, oldest first.</summary>
    Task<IReadOnlyList<WorkSyncRecord>> ListByWorkItemAsync(
        WorkItemId workItemId, CancellationToken ct = default);

    /// <summary>
    /// The most recent successfully posted external status for this
    /// item+namespace, or null when nothing was posted yet. Used to post
    /// progress only when the mapped status actually changes.
    /// </summary>
    Task<string?> LastPostedStatusAsync(
        WorkItemId workItemId, string @namespace, CancellationToken ct = default);
}
