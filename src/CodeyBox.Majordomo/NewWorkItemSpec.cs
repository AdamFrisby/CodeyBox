using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Specification for one work item to create. Used by <c>create_work_item</c>
/// directly and by <see cref="WorkItemChainNode"/> inside
/// <c>create_work_item_chain</c>.
///
/// Every field is validated at construction and the properties are get-only,
/// so an invalid spec — empty title, out-of-range priority, a dependency list
/// past the cap — is unrepresentable rather than rejected downstream.
/// Out-of-range values are REJECTED even where the REST API clamps: a model's
/// mistake must fail at the contract, not be silently reinterpreted.
///
/// <para>
/// The spec deliberately covers ordinary work items only. Check-and-act items
/// and agent-control items are different job types with their own semantics
/// and are out of this contract's scope; there is no field that can smuggle
/// one in. <c>Initiator</c> is likewise absent: the caller's principal is a
/// transport concern, never model-chosen.
/// </para>
///
/// <para>
/// Field SHAPE is enforced here; field EXISTENCE is not knowable at contract
/// time. The executor must repeat the live-registry checks the REST surface
/// performs — project existence for <see cref="ProjectId"/>, agent-registry
/// membership for <see cref="Agent"/>, class-catalog membership for
/// <see cref="AgentClassId"/>, profile existence for
/// <see cref="AuditorProfile"/>, release lookup for <see cref="ReleaseId"/>,
/// and <c>IKnobRegistry.Normalize</c> for <see cref="Knobs"/> — before
/// persisting anything.
/// </para>
/// </summary>
public sealed record NewWorkItemSpec
{
    public NewWorkItemSpec(
        ProjectId projectId,
        string title,
        string prompt,
        AgentKind? agent = null,
        string? agentClassId = null,
        string? auditorProfile = null,
        string? baseBranch = null,
        string? workBranch = null,
        int? priority = null,
        TimeSpan? workTimeout = null,
        TimeSpan? mergeTimeout = null,
        bool? pushUpstream = null,
        IReadOnlyList<WorkItemId>? dependsOn = null,
        IReadOnlyDictionary<string, string>? externalIds = null,
        IReadOnlyList<string>? requiredCapabilities = null,
        int? minModelScore = null,
        int? auditMaxIterations = null,
        string? auditComplexity = null,
        ReleaseId? releaseId = null,
        bool isRefactor = false,
        IReadOnlyDictionary<string, string>? knobs = null)
    {
        var checkedBase = WorkItemFieldValidation.Branch(baseBranch, nameof(baseBranch));
        var checkedWork = WorkItemFieldValidation.Branch(workBranch, nameof(workBranch));
        if (checkedBase is not null && checkedWork is not null
            && string.Equals(checkedBase, checkedWork, StringComparison.Ordinal))
        {
            throw new ArgumentException("workBranch must differ from baseBranch", nameof(workBranch));
        }

        ProjectId = projectId;
        Title = WorkItemFieldValidation.Title(title);
        Prompt = WorkItemFieldValidation.Prompt(prompt);
        Agent = agent;
        AgentClassId = WorkItemFieldValidation.AgentClassId(agentClassId);
        AuditorProfile = WorkItemFieldValidation.AuditorProfile(auditorProfile);
        BaseBranch = checkedBase;
        WorkBranch = checkedWork;
        Priority = priority is { } p ? WorkItemFieldValidation.Priority(p) : null;
        WorkTimeout = workTimeout is { } wt ? WorkItemFieldValidation.WorkTimeout(wt) : null;
        MergeTimeout = mergeTimeout is { } mt ? WorkItemFieldValidation.MergeTimeout(mt) : null;
        PushUpstream = pushUpstream;
        DependsOn = WorkItemFieldValidation.DependsOn(dependsOn);
        ExternalIds = WorkItemFieldValidation.ExternalIds(externalIds);
        RequiredCapabilities = WorkItemFieldValidation.RequiredCapabilities(requiredCapabilities);
        MinModelScore = minModelScore is { } mms ? WorkItemFieldValidation.MinModelScore(mms) : null;
        AuditMaxIterations = auditMaxIterations is { } ami ? WorkItemFieldValidation.AuditMaxIterations(ami) : null;
        AuditComplexity = WorkItemFieldValidation.AuditComplexity(auditComplexity);
        ReleaseId = releaseId;
        IsRefactor = isRefactor;
        Knobs = WorkItemFieldValidation.Knobs(knobs);
    }

    /// <summary>The project the item belongs to.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>Human-readable label for logs and the UI.</summary>
    public string Title { get; }

    /// <summary>The natural-language task to give to the agent.</summary>
    public string Prompt { get; }

    /// <summary>
    /// Optional agent preference, mirroring <see cref="WorkItem.Agent"/>. Not
    /// consulted when <see cref="AgentClassId"/> is set — class routing picks
    /// the member.
    /// </summary>
    public AgentKind? Agent { get; }

    /// <summary>Route via the named agent class instead of a direct agent pick.</summary>
    public string? AgentClassId { get; }

    /// <summary>Audit profile override; null inherits the project default.</summary>
    public string? AuditorProfile { get; }

    /// <summary>Per-item base-branch override; null inherits the project default.</summary>
    public string? BaseBranch { get; }

    /// <summary>Branch the agent pushes to; null means the orchestrator generates one.</summary>
    public string? WorkBranch { get; }

    /// <summary>
    /// Scheduling priority within [<see cref="WorkItemLimits.MinPriority"/>,
    /// <see cref="WorkItemLimits.MaxPriority"/>]. The API additionally clamps
    /// against the project's per-project ceiling at execution time. Null = 0.
    /// </summary>
    public int? Priority { get; }

    /// <summary>Per-item work-phase wall-clock budget; null inherits project/global policy.</summary>
    public TimeSpan? WorkTimeout { get; }

    /// <summary>Per-item merge-phase wall-clock budget; null inherits the default.</summary>
    public TimeSpan? MergeTimeout { get; }

    /// <summary>Push to upstream after merge; null inherits the project default.</summary>
    public bool? PushUpstream { get; }

    /// <summary>
    /// Existing work items that must reach a terminal state before this item
    /// is dispatched. Immutable after creation; empty when unset.
    /// </summary>
    public IReadOnlyList<WorkItemId> DependsOn { get; }

    /// <summary>Namespaced external identifiers; empty when unset.</summary>
    public IReadOnlyDictionary<string, string> ExternalIds { get; }

    /// <summary>Clearance tags an eligible class member must declare; empty = any member.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; }

    /// <summary>Minimum model-quality score for the dispatched member; null = no floor.</summary>
    public int? MinModelScore { get; }

    /// <summary>Per-item audit iteration cap; null inherits the project budget.</summary>
    public int? AuditMaxIterations { get; }

    /// <summary>
    /// Audit complexity label; null (or whitespace, normalised away at
    /// construction) inherits the project default.
    /// </summary>
    public string? AuditComplexity { get; }

    /// <summary>Release to attach the item to; null = unattached.</summary>
    public ReleaseId? ReleaseId { get; }

    /// <summary>
    /// When true the item is created as a project-exclusive refactor: it only
    /// starts once the project has no other in-flight items and blocks other
    /// items while it runs.
    /// </summary>
    public bool IsRefactor { get; }

    /// <summary>Per-item knob overrides; validated against the knob registry at execution.</summary>
    public IReadOnlyDictionary<string, string> Knobs { get; }
}
