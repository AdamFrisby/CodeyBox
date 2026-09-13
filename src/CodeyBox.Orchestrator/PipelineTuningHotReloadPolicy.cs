namespace CodeyBox.Orchestrator;

/// <summary>
/// Single source of truth for which <c>CodeyBox:PipelineTuning</c> fields are
/// hot-reloadable and which still require a restart.
///
/// <para>
/// Every field listed here as hot-reloadable is re-bound by
/// <c>AgentConfigHotReload</c> into <see cref="PipelineTuningSnapshot"/> (plus
/// the two process-wide statics for <c>AgentSuspendMaxRetries</c> /
/// <c>AgentSessionResumeMaxAttempts</c>). Consumers read the snapshot's
/// <c>Current</c> at each use, so the new values take effect on the next
/// pickup / audit run without a restart. In-flight runs keep the reference
/// they started with.
/// </para>
///
/// <para>
/// The sets are string names (not lambdas) so tests can assert by reflection
/// that they partition every <see cref="PipelineTuningOptions"/> property:
/// adding a new option without classifying it here fails the parity test, and
/// a dedicated fingerprint test proves the reload path actually observes every
/// hot-reloadable field (a field missing from the fingerprint would silently
/// behave as restart-required, as <c>AuditorAbsoluteTimeout</c> did before
/// this policy existed).
/// </para>
/// </summary>
public static class PipelineTuningHotReloadPolicy
{
    /// <summary>PipelineTuning fields the reload path re-binds at runtime.</summary>
    public static IReadOnlyList<string> HotReloadableFields { get; } =
    [
        nameof(PipelineTuningOptions.MaxPlanReviewIterations),
        nameof(PipelineTuningOptions.PlanTaskBindingCoverageRatio),
        nameof(PipelineTuningOptions.DefaultQuotaFailurePause),
        nameof(PipelineTuningOptions.DefaultRateLimitPause),
        nameof(PipelineTuningOptions.QuotaExhaustionFallbackTtl),
        nameof(PipelineTuningOptions.MaxParsedQuotaResetWindow),
        nameof(PipelineTuningOptions.MergeSandboxStagingRestoreAttempts),
        nameof(PipelineTuningOptions.MaxQuestionsPerWorkItem),
        nameof(PipelineTuningOptions.AgentSuspendMaxRetries),
        nameof(PipelineTuningOptions.AgentSessionResumeMaxAttempts),
        nameof(PipelineTuningOptions.MaxRetainedAgentTurnSandboxes),
        nameof(PipelineTuningOptions.AutoMergeRaceRecoveryMaxAttempts),
        nameof(PipelineTuningOptions.EnableSandboxReuse),
        nameof(PipelineTuningOptions.MaxSandboxReuses),
        nameof(PipelineTuningOptions.MaxSandboxLifetime),
        nameof(PipelineTuningOptions.SandboxPressureThreshold),
        nameof(PipelineTuningOptions.SandboxPermitWaitWarningThreshold),
        nameof(PipelineTuningOptions.AuditShortCircuitEnabled),
        nameof(PipelineTuningOptions.EmptyReworkEscalationRetries),
        nameof(PipelineTuningOptions.AuditorIdleTimeout),
        nameof(PipelineTuningOptions.AuditorAbsoluteTimeout),
        nameof(PipelineTuningOptions.BlockRedundantDotnetBuildTestInAuditSandbox),
        nameof(PipelineTuningOptions.CSharpTestPassAuditorIdleTimeout),
        nameof(PipelineTuningOptions.CSharpTestPassBlameHangTimeout),
        nameof(PipelineTuningOptions.EnableHandoffSeeding),
        nameof(PipelineTuningOptions.SelfReviewChecklistEnabled),
        nameof(PipelineTuningOptions.PlannedItemAuditRebalanceEnabled),
        nameof(PipelineTuningOptions.PlannedItemAdvisoryAuditors),
    ];

    /// <summary>
    /// PipelineTuning fields that require a restart to take effect. Empty:
    /// every field is consumed through the live snapshot. Kept (rather than
    /// deleted) so the partition test forces an explicit decision for each
    /// future property instead of defaulting it to hot-reloadable.
    /// </summary>
    public static IReadOnlyList<string> RestartRequiredFields { get; } = [];
}
