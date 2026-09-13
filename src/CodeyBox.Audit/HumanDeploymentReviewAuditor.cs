using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Human deployment reviewer: the operator acting as a reviewer through the
/// standard auditor seam. Declares <c>Kind = "human"</c> and the
/// <c>deployment</c> target (composable with other targets for future
/// human-review surfaces); the kind composes with target filtering like any
/// other auditor, so <c>ComposeForTarget(..., Deployment)</c> selects it and
/// <c>ExcludedAuditors</c> removes it by name.
///
/// <para>The reviewer never runs inline: <see cref="RunAsync"/> always throws
/// <see cref="AuditUnavailableException"/> because a human verdict cannot be
/// produced inside an audit sandbox. The pipeline detects human-kind
/// auditors in the deployment stage and takes the async park/resume path
/// instead — provision the deployment, park the item at
/// <c>NeedsOperatorInput</c> (releasing the worker slot and audit sandbox
/// while keeping only the deployment alive), notify the operator with the
/// endpoint + expiry + acceptance criteria, and on verdict resume the
/// iteration, record the verdict like any <see cref="AuditResult"/>, and
/// tear the deployment down immediately. Approve passes; reject-with-notes
/// yields blocking findings feeding the normal rework loop; an undecided
/// review past the recipe's max lifetime fails closed as 'expired
/// unreviewed'. The pipeline never calls <see cref="RunAsync"/>; the throw
/// below is a backstop so a future caller that runs it inline fails loudly
/// instead of recording a fake pass.</para>
/// </summary>
public sealed class HumanDeploymentReviewAuditor : IAuditor
{
    private readonly HumanDeploymentReviewOptions _options;

    public HumanDeploymentReviewAuditor(HumanDeploymentReviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("Human review requires a non-empty Name.", nameof(options));
        _options = options;
    }

    public string Name => _options.Name;

    public string Kind => WellKnownAuditorKinds.Human;

    public AuditCapabilities Required => AuditCapabilities.None;

    public IReadOnlySet<AuditTarget> Targets => AuditTargets.DeploymentOnly;

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        throw new AuditUnavailableException(
            $"'{Name}' cannot run inline: a human verdict arrives asynchronously through the " +
            "operator park/resume path (approve/reject via the deployment-review endpoints or " +
            "by answering the review question). The pipeline handles human-kind auditors in the " +
            "deployment stage without invoking them; reaching this method is a wiring bug, " +
            "reported as an incomplete iteration rather than a pass.");
    }
}

/// <summary>
/// Options for <see cref="HumanDeploymentReviewAuditor"/>. Bound from the
/// <c>CodeyBox:HumanReview</c> configuration section and read through an
/// <c>IOptionsMonitor</c> so operators can toggle or retune the reviewer
/// without a restart. Operational values only — no literals in source.
/// </summary>
public sealed class HumanDeploymentReviewOptions
{
    /// <summary>
    /// When false the human reviewer is not composed into the audit panel at
    /// all (today's behaviour is unchanged). When true it is composed into
    /// the deployment stage of every project with deployment auditing
    /// enabled; a project still opts out via <c>ExcludedAuditors</c> by name.
    /// Default false — human review is an explicit opt-in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Stable auditor name used for logs, findings, audit reports, and
    /// <c>ExcludedAuditors</c> removal. Defaults to
    /// <c>human:deployment-review</c>.
    /// </summary>
    public string Name { get; set; } = WellKnownAuditorNames.HumanDeploymentReview;

    /// <summary>
    /// How often the expiry sweeper scans for undecided reviews past their
    /// deadline. Default 30 seconds. Set to <see cref="TimeSpan.Zero"/> to
    /// disable sweep-driven expiry (the resume path still fails closed when
    /// it observes an expired review).
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
