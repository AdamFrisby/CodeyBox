namespace CodeyBox.Core;

/// <summary>
/// Sandbox-backed agent phase served by <see cref="IAgentPhaseExecutor"/>.
/// Replaces the old <c>isInitial</c> boolean: work and rework are distinct
/// values instead of two meanings of one flag.
/// </summary>
public enum AgentPhaseKind
{
    Work,
    Rework,
    Merge,
}

/// <summary>
/// Outcome of a phase executed through <see cref="IAgentPhaseExecutor"/>.
/// Phases signal failure by throwing; a returned result always completed.
/// </summary>
public enum AgentPhaseOutcome
{
    Completed,
}

/// <summary>
/// How the phase executor treats a required-build failure on the work branch.
/// Anti-corruption naming for the orchestrator-internal build policy: the
/// policy itself still lives with the build gate, this enum only carries the
/// caller's choice across the seam. Mapped 1:1 at the seam edge; the mapping
/// is exhaustive so a new value fails loudly instead of silently degrading.
/// </summary>
public enum AgentPhaseBuildPolicy
{
    Terminal,
    DeferToAuditLoop,
}

/// <summary>
/// How the phase executor treats a clean-exit-but-empty diff on a rework turn.
/// Anti-corruption naming for the orchestrator-internal handling: mapped 1:1
/// at the seam edge with an exhaustive mapping.
/// </summary>
public enum AgentPhaseReworkNoDiffHandling
{
    TerminalError,
    AuditEmptyRework,
}

/// <summary>
/// Agent route declared for a phase execution: the runner kind plus the
/// model and reasoning mode the turn runs under. Derived from the request's
/// runner and work item (see <see cref="AgentPhaseRequest.AgentRoute"/>), so
/// a request can never declare a route that contradicts what will run.
/// </summary>
public sealed record AgentPhaseAgentRoute(
    AgentKind Kind,
    string? ModelId,
    string? ReasoningMode);

/// <summary>
/// One sandbox-backed agent phase for one work item, shaped as a request
/// object instead of a positional parameter list. Every field the previous
/// positional phase signatures carried is present here; cancellation tokens
/// stay method parameters of <see cref="IAgentPhaseExecutor.ExecuteAsync"/>
/// because they are ambient, not phase state.
/// </summary>
public sealed record AgentPhaseRequest
{
    /// <summary>Work item under execution. Carries resume state
    /// (<c>PreemptCheckpoint</c>, <c>AgentTurnResumeCheckpoint</c>,
    /// <c>AgentTurnRecoveryLease</c>) and the model/reasoning mode.</summary>
    public required WorkItem Item { get; init; }

    /// <summary>Project the work item is bound to.</summary>
    public required Project Project { get; init; }

    /// <summary>Which phase to run. Replaces the old <c>isInitial</c> bool.</summary>
    public required AgentPhaseKind Phase { get; init; }

    /// <summary>Host-side repository id the sandbox clones from.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Branch the work branch is created from (work) or merged into (merge).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>Branch the agent works on (work/rework) or merges (merge).</summary>
    public required string Branch { get; init; }

    /// <summary>Exact runner instance to invoke, resolved by the caller
    /// (including quota-fallback swaps). Never re-resolved from the route:
    /// the executor runs this instance.</summary>
    public required IAgentRunner Runner { get; init; }

    /// <summary>Prompt for work/rework turns. Null for merge, which builds
    /// its own resolution prompt.</summary>
    public string? Prompt { get; init; }

    /// <summary>Sandbox network profile for the phase.</summary>
    public string? NetworkProfile { get; init; }

    /// <summary>Sandbox flavour for the phase.</summary>
    public SandboxProfileFlavor SandboxFlavor { get; init; } = SandboxProfileFlavor.Headless;

    /// <summary>Required-build failure policy for work/rework. Null for merge,
    /// which does not consult the work-phase build gate.</summary>
    public AgentPhaseBuildPolicy? BuildPolicy { get; init; }

    /// <summary>Audit/rework iteration number. Null for the initial work turn.</summary>
    public int? Iteration { get; init; }

    /// <summary>Auditors composing the pre-emptive self-review turn.
    /// Only consulted by the work phase.</summary>
    public IReadOnlyList<IAuditor>? AuditorsForPreemptiveSelfReview { get; init; }

    /// <summary>Empty-diff handling for rework turns.</summary>
    public AgentPhaseReworkNoDiffHandling ReworkNoDiffHandling { get; init; } =
        AgentPhaseReworkNoDiffHandling.TerminalError;

    /// <summary>Pre-turn source commit for durable-turn resume. Null when the
    /// item carries no git checkpoint; the executor resolves it then.</summary>
    public string? ResumePreTurnCommitSha { get; init; }

    /// <summary>Skips the no-changes circuit-breaker contribution for this turn.</summary>
    public bool SuppressNoChangesBreaker { get; init; }

    /// <summary>
    /// Declared agent route for this turn. Always derived from
    /// <see cref="Runner"/> and <see cref="Item"/> so it round-trips
    /// consistently and can never contradict the invocation.
    /// </summary>
    public AgentPhaseAgentRoute AgentRoute =>
        new(Runner.Kind, Item.ModelId, Item.ReasoningMode);
}

/// <summary>
/// Result of one phase executed through <see cref="IAgentPhaseExecutor"/>.
/// Fields a phase does not produce stay at their empty value: agent phases
/// produce no audit findings (empty list), and cost is recorded directly to
/// the cost store rather than returned (null usage).
/// </summary>
public sealed record AgentPhaseResult
{
    /// <summary>Phase that produced this result.</summary>
    public required AgentPhaseKind Phase { get; init; }

    /// <summary>How the phase finished. Failures throw instead of returning.</summary>
    public required AgentPhaseOutcome Outcome { get; init; }

    /// <summary>
    /// Resulting commit: the work-branch tip after work/rework, the merge
    /// commit after merge.
    /// </summary>
    public string? ResultingCommitSha { get; init; }

    /// <summary>Agent stdout for post-phase processing (question parsing, PR text).</summary>
    public string? AgentStdout { get; init; }

    /// <summary>Findings the phase produced. Empty for work/rework/merge.</summary>
    public IReadOnlyList<AuditFinding> Findings { get; init; } = [];

    /// <summary>Usage the phase produced. Null: phases record cost directly
    /// to the cost store instead of returning it.</summary>
    public WorkItemUsageSummary? Usage { get; init; }

    /// <summary>File name of the captured agent stream, when stream capture
    /// was enabled for the turn. Null otherwise.</summary>
    public string? AgentStreamFileName { get; init; }
}

/// <summary>
/// Executes one sandbox-backed agent phase of one work item. The seam
/// between pipeline orchestration and phase machinery: tests substitute a
/// double here to exercise orchestration without a sandbox provider, and
/// alternative implementations can relocate phase execution without
/// touching the pipeline state machine.
/// </summary>
public interface IAgentPhaseExecutor
{
    /// <summary>
    /// Runs <paramref name="request"/> to completion. Returns the phase
    /// result; throws on phase failure. <paramref name="ct"/> cancels the
    /// phase; <paramref name="hostShutdownToken"/> triggers the
    /// checkpoint-and-preserve path.
    /// </summary>
    Task<AgentPhaseResult> ExecuteAsync(
        AgentPhaseRequest request,
        CancellationToken ct,
        CancellationToken hostShutdownToken);
}
