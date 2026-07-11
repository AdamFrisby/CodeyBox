using CodeyBox.Core;

namespace CodeyBox.Audit.Llm;

/// <summary>
/// Code-target LLM reviewer that checks the work-phase diff against the
/// reviewed-and-approved implementation PLAN and flags UNJUSTIFIED deviations
/// from the agreed approach. It closes the planning loop on the implementation
/// side: the subjective/architectural judgement happened at the plan stage, and
/// this reviewer verifies the code actually followed the plan the panel approved.
///
/// <para>Active only for PLANNED items. Adherence is meaningless without a plan,
/// so when the code-audit <see cref="AuditContext.PlanArtifact"/> is absent
/// (the item was never planned, or planning is off) the auditor passes as a
/// no-op WITHOUT spending agent quota — unplanned items are completely
/// unaffected. When a plan is present it delegates to a code-review
/// <see cref="LlmReviewAuditor"/> configured with the plan-adherence frame, which
/// renders the approved plan as untrusted data and asks the reviewer to judge
/// adherence.</para>
///
/// <para>Composition over inheritance: this wraps a single inner
/// <see cref="LlmReviewAuditor"/> rather than subclassing it, so the shared
/// sandbox/verdict machinery stays in one place and this type only adds the
/// planned-only gate.</para>
/// </summary>
public sealed class PlanAdherenceAuditor : IAuditor, IRequiresPassedBuildTestGate
{
    private readonly LlmReviewAuditor _inner;

    /// <param name="agent">
    /// The agent runner used when a real review is required. The pipeline always
    /// overrides this per-invocation via <see cref="AuditContext.AuditRunner"/>
    /// (cross-review routing); this baked-in value is only the fallback the
    /// composer supplies from the resolving project's work runner.
    /// </param>
    public PlanAdherenceAuditor(IAgentRunner agent, PlanAdherenceAuditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("PlanAdherenceAuditor requires a non-empty Name.", nameof(options));

        Name = options.Name;
        _inner = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = options.Name,
            Agent = agent,
            ReviewFocus = options.ReviewFocus,
            FrameTemplate = options.FrameTemplate,
            // Code only: adherence needs a diff, so there is nothing to review at
            // the plan stage (which has no implementation yet).
            Targets = AuditTargets.CodeOnly,
        });
    }

    public string Name { get; }

    public string Kind => _inner.Kind;

    public AuditCapabilities Required => _inner.Required;

    public IReadOnlySet<AuditTarget> Targets => AuditTargets.CodeOnly;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Active only when the item was planned. A blank plan artifact means the
        // item never went through planning, so there is no approved approach to
        // check adherence against: pass as a no-op before touching the agent.
        if (string.IsNullOrWhiteSpace(context.PlanArtifact))
            return new AuditResult(true, []);

        return await _inner.RunAsync(sandbox, workingDirectory, context, ct);
    }
}

/// <summary>
/// Options for <see cref="PlanAdherenceAuditor"/>. Bound from the
/// <c>CodeyBox:PlanAdherence</c> configuration section and read through an
/// <c>IOptionsMonitor</c> so operators can toggle or retune the reviewer without
/// a restart. Only <see cref="Enabled"/> and the review text are operational;
/// the frame template defaults to the shared plan-adherence contract.
/// </summary>
public sealed class PlanAdherenceAuditorOptions
{
    /// <summary>
    /// Stable auditor name used for logs, findings, audit reports, and
    /// <c>ExcludedAuditors</c> removal. Defaults to <c>plan:adherence</c>.
    /// </summary>
    public const string DefaultName = "plan:adherence";

    /// <summary>
    /// Default review-dimension text rendered into the plan-adherence frame's
    /// <c>{{reviewFocus}}</c> slot.
    /// </summary>
    public const string DefaultReviewFocus =
        "YOUR REVIEW DIMENSION: plan adherence only. Compare the diff to the approved plan's " +
        "approach, declared files/areas, and test strategy. Do not re-review general code " +
        "quality, architecture, or security here — other auditors own those lanes. Flag only " +
        "where the implementation departs from the approved approach without justification.";

    /// <summary>
    /// When false the reviewer is not composed into the audit panel at all. When
    /// true it is composed for every project but still self-limits to planned
    /// items at run time (see <see cref="PlanAdherenceAuditor"/>). Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Auditor name. See <see cref="DefaultName"/>.</summary>
    public string Name { get; set; } = DefaultName;

    /// <summary>Review-dimension text. See <see cref="DefaultReviewFocus"/>.</summary>
    public string ReviewFocus { get; set; } = DefaultReviewFocus;

    /// <summary>
    /// Code-review frame template. Defaults to
    /// <see cref="LlmPromptFrameTemplate.DefaultPlanAdherenceFrameTemplate"/>,
    /// which renders the approved plan as untrusted data and emits the standard
    /// result.json verdict.
    /// </summary>
    public string FrameTemplate { get; set; } = LlmPromptFrameTemplate.DefaultPlanAdherenceFrameTemplate;
}
