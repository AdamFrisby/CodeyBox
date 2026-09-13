namespace CodeyBox.Core;

/// <summary>
/// Lifecycle status of a human deployment review. A review is created
/// <see cref="Pending"/> when the audit iteration parks; the operator's
/// verdict moves it to <see cref="Approved"/> or <see cref="Rejected"/>;
/// the sweeper (or the resume path) moves an undecided review past its
/// deadline to <see cref="Expired"/> — fail-closed, never an implicit pass.
/// <see cref="ConsumedAt"/> marks that the audit loop has folded the outcome
/// into its verdict and torn the deployment down.
/// </summary>
public enum HumanDeploymentReviewStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
}

/// <summary>
/// Durable record of one parked human deployment review for a single audit
/// iteration. The deployment stays alive in the deployment manager's active
/// set (keyed by <see cref="DeploymentId"/>) while the worker slot and audit
/// sandbox are released; on verdict or expiry the audit loop re-attaches by
/// id, records an <see cref="AuditResult"/>-shaped outcome like any auditor,
/// and tears the deployment down immediately.
/// </summary>
public sealed record HumanDeploymentReview
{
    /// <summary>Work item under review (WorkItemId.ToString()).</summary>
    public required string WorkItemId { get; init; }

    /// <summary>Audit iteration that parked.</summary>
    public required int Iteration { get; init; }

    /// <summary>
    /// Id of the live deployment held in the manager's active set while the
    /// verdict is pending. Survives only in-process; after a restart the id
    /// no longer resolves and the resume path treats the review as expired.
    /// </summary>
    public required string DeploymentId { get; init; }

    /// <summary>JSON-serialized <see cref="DeploymentEndpoint"/>.</summary>
    public required string EndpointJson { get; init; }

    /// <summary>
    /// Absolute time the deployment must be torn down by (start + the
    /// recipe's max lifetime). An undecided review at this time expires
    /// fail-closed with an 'expired unreviewed' blocking finding.
    /// </summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>When the review was requested (park time, UTC).</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// Operator brief: deployment endpoint, expiry, and what to verify (the
    /// item's acceptance criteria). Also used as the backing question text.
    /// Bounded at creation; never contains secrets.
    /// </summary>
    public required string Brief { get; init; }

    /// <summary>Question id of the backing operator question.</summary>
    public required string QuestionId { get; init; }

    /// <summary>JSON-serialized names of the human auditors awaiting verdict.</summary>
    public required string HumanAuditorsJson { get; init; }

    /// <summary>
    /// JSON-serialized code-stage findings already collected before parking
    /// (blocking-free by construction — parking requires a clean code
    /// stage), so the resumed iteration's report stays complete without
    /// re-running quota-spending code auditors.
    /// </summary>
    public required string CodeFindingsJson { get; init; }

    /// <summary>
    /// JSON-serialized code-stage completed auditor names, so the resumed
    /// iteration's completion set still covers the auditors that ran before
    /// the park (the merge gate validates the persisted snapshot against
    /// the scheduled panel).
    /// </summary>
    public required string CodeCompletedJson { get; init; }

    /// <summary>
    /// JSON-serialized automated deployment-stage findings already collected
    /// before parking, so the resume path merges them without re-running
    /// quota-spending auditors against the held deployment.
    /// </summary>
    public required string AutomatedFindingsJson { get; init; }

    /// <summary>JSON-serialized completed automated auditor names.</summary>
    public required string AutomatedCompletedJson { get; init; }

    /// <summary>JSON-serialized incomplete automated auditor names.</summary>
    public required string AutomatedIncompleteJson { get; init; }

    /// <summary>AgentKind value that ran automated auditors, if any.</summary>
    public string? ActiveAuditAgentKind { get; init; }

    public bool DeclaredShortCircuitBlocking { get; init; }

    public bool IncompleteVerdict { get; init; }

    public HumanDeploymentReviewStatus Status { get; init; } = HumanDeploymentReviewStatus.Pending;

    /// <summary>Reject notes / approval note recorded with the verdict.</summary>
    public string? Notes { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Operator identity that recorded the verdict, when known.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>When the audit loop consumed the outcome (null = not yet).</summary>
    public DateTimeOffset? ConsumedAt { get; init; }
}

/// <summary>
/// Durable store for parked human deployment reviews. Implementations must be
/// safe for concurrent calls and perform compare-and-set transitions (a
/// verdict, expiry, or consume applies only from the expected prior state).
/// </summary>
public interface IHumanDeploymentReviewStore
{
    /// <summary>
    /// Inserts a pending review. When a consumed review already exists for the
    /// same (work item, iteration) it is replaced with the fresh pending row
    /// (supports re-review of the same iteration after a manual replay);
    /// when a non-consumed row exists it is returned unchanged.
    /// Returns the effective row.
    /// </summary>
    Task<HumanDeploymentReview> GetOrCreatePendingAsync(HumanDeploymentReview review, CancellationToken ct = default);

    /// <summary>Returns the review for (work item, iteration), or null.</summary>
    Task<HumanDeploymentReview?> TryGetAsync(string workItemId, int iteration, CancellationToken ct = default);

    /// <summary>
    /// Returns the newest non-consumed review for a work item, or null.
    /// At most one such row exists: parking always returns the worker slot,
    /// so a second park cannot start before the first is consumed.
    /// </summary>
    Task<HumanDeploymentReview?> GetActiveForWorkItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Records the operator verdict. Applies only when the row is still
    /// <see cref="HumanDeploymentReviewStatus.Pending"/>; returns false
    /// (leaving the row untouched) otherwise.
    /// </summary>
    Task<bool> RecordVerdictAsync(
        string workItemId,
        int iteration,
        bool approved,
        string? notes,
        string? decidedBy,
        DateTimeOffset decidedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a pending review expired. Applies only from
    /// <see cref="HumanDeploymentReviewStatus.Pending"/>; returns false otherwise.
    /// </summary>
    Task<bool> MarkExpiredAsync(string workItemId, int iteration, DateTimeOffset expiredAt, CancellationToken ct = default);

    /// <summary>
    /// Marks a decided/expired review consumed by the audit loop. Idempotent:
    /// always succeeds, stamping <see cref="HumanDeploymentReview.ConsumedAt"/>
    /// when not already set.
    /// </summary>
    Task MarkConsumedAsync(string workItemId, int iteration, DateTimeOffset consumedAt, CancellationToken ct = default);

    /// <summary>Pending reviews whose deadline has passed (sweeper input).</summary>
    Task<IReadOnlyList<HumanDeploymentReview>> ListExpiredPendingAsync(DateTimeOffset now, CancellationToken ct = default);
}
