namespace CodeyBox.Core;

/// <summary>
/// Lifecycle state of a standalone audit run. Queue-only: runs never
/// transition to plan/work/rework/commit/merge/upstream delivery.
/// </summary>
public enum AuditRunState
{
    Unknown = 0,
    Queued = 1,
    Provisioning = 2,
    Running = 3,
    Collecting = 4,
    Passed = 5,
    Findings = 6,
    Cancelled = 7,
    Failed = 8,
}

/// <summary>
/// Per-auditor execution outcome. Modelled separately from finding verdicts:
/// a version probe is distinct from a main scan, and an incomplete execution
/// must never masquerade as a pass.
/// </summary>
public enum AuditRunAuditorOutcome
{
    Unknown = 0,
    Pass = 1,
    Fail = 2,
    NotRun = 3,
    Unsupported = 4,
    Unavailable = 5,
    TimedOut = 6,
    Cancelled = 7,
    Error = 8,
}

/// <summary>
/// Aggregate outcome across all selected auditors.
/// </summary>
public enum AuditRunAggregateOutcome
{
    Unknown = 0,
    Pass = 1,
    Findings = 2,
    Unsupported = 3,
    MissingTool = 4,
    InfrastructureFailure = 5,
    Cancelled = 6,
}

/// <summary>
/// Why a selected auditor was rejected before provisioning. Rejections are
/// explicit and per-member; members are never silently dropped.
/// </summary>
public enum AuditRunUnsupportedReason
{
    None = 0,
    UnknownAuditor = 1,
    DisabledAuditor = 2,
    LlmAuditor = 3,
    AgentCredentialsRequired = 4,
    ContextDependent = 5,
    MissingExplicitDiffBase = 6,
    ProfileNotFound = 7,
}

/// <summary>
/// Immutable identity for a standalone audit run.
/// </summary>
public readonly record struct AuditRunId(Guid Value)
{
    public static AuditRunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Exactly one selection mechanism: explicit auditor IDs or a named profile.
/// </summary>
public sealed record AuditRunSelection
{
    public IReadOnlyList<string> AuditorIds { get; init; } = [];
    public string? Profile { get; init; }

    public bool IsExplicit => AuditorIds.Count > 0;
    public bool IsProfile => !string.IsNullOrWhiteSpace(Profile);
}

/// <summary>
/// Client-supplied create request. The service resolves Ref/BaseRef to
/// immutable SHAs once and freezes them in provenance.
/// </summary>
public sealed record AuditRunCreateRequest
{
    public required string ProjectId { get; init; }
    public required string Ref { get; init; }
    public string? BaseRef { get; init; }
    public AuditRunSelection Selection { get; init; } = new();
    public string? IdempotencyKey { get; init; }
    public string? RepositoryOverride { get; init; }
}

/// <summary>
/// Frozen provenance captured at create time. Never overwritten by retries.
/// </summary>
public sealed record AuditRunProvenance
{
    public required string ProjectId { get; init; }
    public required string RequestedRef { get; init; }
    public required string ResolvedSha { get; init; }
    public string? RequestedBaseRef { get; init; }
    public string? ResolvedBaseSha { get; init; }
    public required IReadOnlyList<string> ResolvedAuditorIds { get; init; }
    public string? ResolvedProfile { get; init; }
    public required string ConfigDigest { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Per-auditor durable result.
/// </summary>
public sealed record AuditRunAuditorResult
{
    public required string AuditorName { get; init; }
    public required string AuditorKind { get; init; }
    public required AuditRunAuditorOutcome Outcome { get; init; }
    public AuditRunUnsupportedReason UnsupportedReason { get; init; }
    public string? UnsupportedDetail { get; init; }
    public IReadOnlyList<AuditReportFinding> Findings { get; init; } = [];
    public int ExitCode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public long DurationMs { get; init; }
    public string? ToolVersion { get; init; }
    public string? ToolDigest { get; init; }
    public int Attempt { get; init; } = 1;
    public bool EvidenceSufficient { get; init; }
    /// <summary>Bounded redacted log excerpt; the full log lives in the artifact store.</summary>
    public string? LogExcerpt { get; init; }
    /// <summary>SHA-256 of the full stored log artifact, when stored.</summary>
    public string? LogArtifactDigest { get; init; }
}

/// <summary>
/// Named artifact stored separately from truncated RawOutput.
/// </summary>
public sealed record AuditRunArtifactRef
{
    public required string Name { get; init; }
    public required string ContentDigest { get; init; }
    public required long SizeBytes { get; init; }
    public required string MediaType { get; init; }
}

/// <summary>
/// Durable standalone audit run record.
/// </summary>
public sealed record AuditRunRecord
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required AuditRunState State { get; init; }
    public required AuditRunAggregateOutcome AggregateOutcome { get; init; }
    public required AuditRunProvenance Provenance { get; init; }
    public IReadOnlyList<AuditRunAuditorResult> AuditorResults { get; init; } = [];
    public IReadOnlyList<AuditRunArtifactRef> Artifacts { get; init; } = [];
    public string? IdempotencyKey { get; init; }
    public string? IdempotencyBodyHash { get; init; }
    public int Attempts { get; init; }
    public string? FailureDetail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Pure lifecycle transitions. Terminal states are sticky; cancellation is
/// honored from any non-terminal state.
/// </summary>
public static class AuditRunLifecycle
{
    public static bool IsTerminal(AuditRunState state) => state is
        AuditRunState.Passed or AuditRunState.Findings or AuditRunState.Cancelled or AuditRunState.Failed;

    public static bool CanTransition(AuditRunState from, AuditRunState to) => (from, to) switch
    {
        (_, AuditRunState.Cancelled) => !IsTerminal(from),
        (AuditRunState.Queued, AuditRunState.Provisioning) => true,
        (AuditRunState.Provisioning, AuditRunState.Running) => true,
        (AuditRunState.Running, AuditRunState.Collecting) => true,
        (AuditRunState.Collecting, AuditRunState.Passed) => true,
        (AuditRunState.Collecting, AuditRunState.Findings) => true,
        (AuditRunState.Collecting, AuditRunState.Failed) => true,
        (AuditRunState.Queued, AuditRunState.Failed) => true,
        (AuditRunState.Provisioning, AuditRunState.Failed) => true,
        (AuditRunState.Running, AuditRunState.Failed) => true,
        _ => false,
    };

    /// <summary>
    /// Aggregate outcome from per-auditor outcomes. Pass requires every
    /// required applicable auditor to have completed with sufficient
    /// evidence; empty/skipped/partial execution never yields Pass.
    /// Completed findings are preserved alongside later infra failure.
    /// </summary>
    public static AuditRunAggregateOutcome Aggregate(IReadOnlyList<AuditRunAuditorResult> results)
    {
        if (results.Count == 0)
            return AuditRunAggregateOutcome.InfrastructureFailure;
        if (results.All(r => r.Outcome == AuditRunAuditorOutcome.Cancelled))
            return AuditRunAggregateOutcome.Cancelled;
        if (results.Any(r => r.Outcome is AuditRunAuditorOutcome.Error or AuditRunAuditorOutcome.TimedOut))
            return AuditRunAggregateOutcome.InfrastructureFailure;
        if (results.Any(r => r.Outcome == AuditRunAuditorOutcome.Unavailable))
            return AuditRunAggregateOutcome.MissingTool;
        if (results.All(r => r.Outcome is AuditRunAuditorOutcome.Unsupported or AuditRunAuditorOutcome.NotRun))
            return AuditRunAggregateOutcome.Unsupported;
        if (results.Any(r => r.Outcome == AuditRunAuditorOutcome.Unsupported))
            return AuditRunAggregateOutcome.Unsupported;
        var executed = results.Where(r => r.Outcome is AuditRunAuditorOutcome.Pass or AuditRunAuditorOutcome.Fail).ToList();
        if (executed.Count == 0)
            return AuditRunAggregateOutcome.InfrastructureFailure;
        if (executed.Any(r => !r.EvidenceSufficient))
            return AuditRunAggregateOutcome.InfrastructureFailure;
        return executed.Any(r => r.Outcome == AuditRunAuditorOutcome.Fail)
            ? AuditRunAggregateOutcome.Findings
            : AuditRunAggregateOutcome.Pass;
    }

    public static AuditRunState TerminalStateFor(AuditRunAggregateOutcome outcome) => outcome switch
    {
        AuditRunAggregateOutcome.Pass => AuditRunState.Passed,
        AuditRunAggregateOutcome.Findings => AuditRunState.Findings,
        AuditRunAggregateOutcome.Cancelled => AuditRunState.Cancelled,
        _ => AuditRunState.Failed,
    };
}
