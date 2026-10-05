namespace CodeyBox.Core;

/// <summary>
/// Durable progress of one audit check-run publication, keyed by repository,
/// exact SHA, work item, target, attempt/iteration, and auditor/aggregate
/// scope. The store is the reconciliation anchor: concurrent delivery,
/// restarts, and lost create responses converge on the stored
/// <see cref="CheckRunId"/> instead of creating duplicates.
/// </summary>
public sealed record AuditCheckPublicationRecord
{
    public required string Repository { get; init; }
    public required string HeadSha { get; init; }
    public required string WorkItemId { get; init; }
    public required string Target { get; init; }
    public required int Iteration { get; init; }
    public required int Attempt { get; init; }
    public required string Scope { get; init; }
    public required string CheckName { get; init; }
    public required string ExternalId { get; init; }

    /// <summary>Forge-assigned check-run id once a create/update has provably succeeded.</summary>
    public long? CheckRunId { get; init; }
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public int AnnotationsPublished { get; init; }
    public int BatchesSent { get; init; }
    /// <summary>True when the last batch write was ambiguous (see result docs).</summary>
    public bool LastBatchUncertain { get; init; }
    public required AuditCheckPublicationState State { get; init; }
    /// <summary>Machine-readable terminal detail: auth denial, validation error, delivery-required, etc.</summary>
    public string? BlockedReason { get; init; }
    public string? LastError { get; init; }
    public int TransportAttempts { get; init; }
    public DateTimeOffset? NextRetryUtc { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
}

public enum AuditCheckPublicationState
{
    /// <summary>A publish/reconcile pass is underway or due.</summary>
    Pending,

    /// <summary>Waiting out bounded backoff after a retryable transport/rate-limit failure.</summary>
    AwaitingRetry,

    /// <summary>Forge state reconciled; check run reflects this publication key.</summary>
    Completed,

    /// <summary>
    /// Actionable terminal state: authentication/permission denial or request
    /// validation failure. Never retried automatically; surfaces the missing
    /// scope or bad request for the operator.
    /// </summary>
    Blocked,
}

/// <summary>
/// Persistence for audit check-run publication identity and progress.
/// Implementations must be safe for concurrent writers and process restarts:
/// upserts are atomic per key, and stale completions never overwrite newer runs.
/// </summary>
public interface IAuditCheckPublicationStore
{
    Task<AuditCheckPublicationRecord?> GetAsync(
        string repository,
        string headSha,
        string workItemId,
        string target,
        int iteration,
        int attempt,
        string scope,
        CancellationToken ct = default);

    Task UpsertAsync(AuditCheckPublicationRecord record, CancellationToken ct = default);

    /// <summary>
    /// Atomically marks the record completed only when no newer attempt for the
    /// same (repository, sha, work item, target, scope) has since been stored:
    /// a stale completion (older iteration/attempt) never overwrites a newer run.
    /// Returns false when the record was superseded or missing.
    /// </summary>
    Task<bool> TryCompleteAsync(AuditCheckPublicationRecord record, CancellationToken ct = default);

    /// <summary>
    /// Records due for a retry pass (awaiting-retry with NextRetryUtc elapsed),
    /// capped at <paramref name="limit"/> rows (bound enforced in storage).
    /// </summary>
    Task<IReadOnlyList<AuditCheckPublicationRecord>> ListDueForRetryAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken ct = default);
}
