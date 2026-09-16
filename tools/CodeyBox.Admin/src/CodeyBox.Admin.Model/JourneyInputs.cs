namespace CodeyBox.Admin.Model;

/// <summary>
/// Caller-supplied inputs to the per-item journey projection. Each record
/// mirrors one existing orchestrator surface (see
/// <see cref="WorkItemJourneyBuilder"/>); the builder never fetches anything
/// itself. All collections are treated as untrusted input: null-tolerant and
/// length-bounded before buffering.
/// </summary>
public sealed record JourneySnapshot
{
    /// <summary>Upper bound on list inputs accepted; entries beyond it are ignored.</summary>
    public const int MaxEntries = 4096;

    public required JourneyItem Item { get; init; }

    /// <summary>
    /// Rows from <c>GET /workitems/{id}/audit-progress</c> (all attempts;
    /// the builder groups by <see cref="JourneyAuditIteration.WorkAttemptKey"/>).
    /// </summary>
    public IReadOnlyList<JourneyAuditIteration> AuditIterations { get; init; } = [];

    /// <summary>
    /// Entries from <c>GET /workitems/{id}/agent-history</c> (who ran each phase).
    /// </summary>
    public IReadOnlyList<JourneyPhaseRun> PhaseRuns { get; init; } = [];

    /// <summary>
    /// Per-phase durations aggregated by the caller from
    /// <c>GET /workitems/{id}/timings</c> (phase → total milliseconds).
    /// </summary>
    public IReadOnlyList<JourneyPhaseTiming> Timings { get; init; } = [];

    /// <summary>
    /// Infra-shaped interruptions for this item, filtered caller-side from the
    /// global <c>GET /workitems/failure-events</c> feed (or the timeline):
    /// <c>failureKind == "infrastructure"</c> rows with sandbox/provider context.
    /// </summary>
    public IReadOnlyList<JourneyInfraEvent> InfraEvents { get; init; } = [];

    /// <summary>
    /// Retained per-phase sandbox evidence from
    /// <c>GET /workitems/{id}/agent-streams</c>. A phase with a matching entry
    /// renders a sandbox link; a phase without one renders none (never a dead
    /// control).
    /// </summary>
    public IReadOnlyList<JourneyStreamFile> StreamFiles { get; init; } = [];

    /// <summary>
    /// Whether <c>GET /workitems/{id}/diff</c> currently returns content.
    /// Per-iteration diffs are not derivable (only branch-tip SHAs are
    /// recorded) — see the package README gaps.
    /// </summary>
    public bool DiffAvailable { get; init; }

    /// <summary>
    /// Project-level default audit budget, used when the item carries no
    /// <see cref="JourneyItem.AuditMaxIterations"/> override.
    /// </summary>
    public int? ProjectDefaultMaxIterations { get; init; }
}

/// <summary>
/// Minimal item shape for the journey: identity, lifecycle state, audit
/// budget, retry counters, and waiting-for context. Mirrors the fields of
/// <c>GET /workitems/{id}</c> the projection reads.
/// </summary>
public sealed record JourneyItem
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Orchestrator lifecycle state name (e.g. "Reworking", "Done").</summary>
    public string State { get; init; } = string.Empty;

    public string Agent { get; init; } = string.Empty;

    /// <summary>Per-item audit ceiling override (null = project default applies).</summary>
    public int? AuditMaxIterations { get; init; }

    public int UpstreamPushAttempts { get; init; }

    public int ConflictReworkAttempts { get; init; }

    public int DelegationAttempts { get; init; }

    /// <summary>Informational failure category ("infrastructure", "quota", ...).</summary>
    public string? FailureKind { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? NextQuotaRetryAt { get; init; }

    public DateTimeOffset? NextTransientRetryAt { get; init; }

    public DateTimeOffset? QuotaResetAt { get; init; }

    public string? AgentPauseTarget { get; init; }
}

/// <summary>
/// One audit-progress row. Mirrors <c>AuditProgressDto</c> (list shape):
/// finding descriptions are unnecessary here — convergence compares stable
/// <see cref="BlockingFindingIds"/> by exact match only.
/// </summary>
public sealed record JourneyAuditIteration
{
    /// <summary>Partition key; empty string = unpartitioned (null work-attempt).</summary>
    public string WorkAttemptKey { get; init; } = string.Empty;

    public int Iteration { get; init; }

    public int MaxIterations { get; init; }

    /// <summary>"complete", "incomplete", or "in_progress".</summary>
    public string Status { get; init; } = "complete";

    public int BlockingFindings { get; init; }

    public int NonBlockingFindings { get; init; }

    public IReadOnlyList<string> BlockingFindingIds { get; init; } = [];

    public DateTimeOffset RecordedAt { get; init; }

    public string? WorkBranchTip { get; init; }
}

/// <summary>
/// One agent-history entry: who ran a phase, when, and how it ended.
/// Mirrors <c>AgentInvolvementDto</c> plus the sandbox name when the caller
/// recovered one (e.g. from a same-phase failure event).
/// </summary>
public sealed record JourneyPhaseRun
{
    /// <summary>
    /// Phase key: "work", "rework", "merge", "conflict_rework", "delegation",
    /// "planning", "check", or "audit:&lt;auditor&gt;".
    /// </summary>
    public string Phase { get; init; } = string.Empty;

    public string AgentKind { get; init; } = string.Empty;

    public string? ModelId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public int? Iteration { get; init; }

    /// <summary>"success", "failure:&lt;category&gt;", or null while running.</summary>
    public string? Outcome { get; init; }

    /// <summary>Sandbox/VM name when known; null renders no sandbox control.</summary>
    public string? SandboxName { get; init; }
}

/// <summary>Aggregated wall-clock for one timing phase (caller-summed).</summary>
public sealed record JourneyPhaseTiming
{
    public string Phase { get; init; } = string.Empty;

    public long DurationMs { get; init; }
}

/// <summary>
/// One infrastructure-shaped interruption: an incomplete audit verdict or a
/// failure event with an infra category. Shown distinctly from merit
/// (rework-counting) iterations.
/// </summary>
public sealed record JourneyInfraEvent
{
    /// <summary>Lifecycle phase active when the interruption hit.</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>Category: "infrastructure", "quota", "transient", or "incomplete-audit".</summary>
    public string Kind { get; init; } = string.Empty;

    public string? Detail { get; init; }

    public string? SandboxName { get; init; }

    public string? Provider { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// One retained agent-stream capture: durable per-phase sandbox evidence.
/// Mirrors one entry of <c>GET /workitems/{id}/agent-streams</c>.
/// </summary>
public sealed record JourneyStreamFile
{
    public string FileName { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public int? Iteration { get; init; }
}
