namespace CodeyBox.Orchestrator;

/// <summary>
/// Operational tuning knobs consumed by <see cref="PipelineRunner"/>'s
/// quota-fallback and merge-staging retry paths. Bound from
/// <c>CodeyBox:PipelineTuning</c> and hot-reloaded via
/// <see cref="PipelineTuningSnapshot"/>.
/// </summary>
public sealed class PipelineTuningOptions
{
    /// <summary>
    /// Last-resort pause applied when a quota-shaped terminal failure occurs and
    /// neither the agent output nor quota probes expose a reset window.
    /// Default 5 minutes.
    /// </summary>
    public TimeSpan DefaultQuotaFailurePause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-process exhausted-member TTL when the chosen agent hits quota
    /// mid-flight. Subscription windows reset on the order of hours; one hour
    /// is a conservative upper bound that keeps the in-process cache useful
    /// across consecutive pickups without blocking long enough to delay an
    /// actual reset by a meaningful amount. Default 1 hour.
    /// </summary>
    public TimeSpan QuotaExhaustionFallbackTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Upper bound for parsed reset-window hints extracted from an agent's
    /// stdout/stderr. Without a cap, a maliciously-crafted Retry-After header
    /// could park an item arbitrarily far in the future. Default 24 hours.
    /// </summary>
    public TimeSpan MaxParsedQuotaResetWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum attempts to restore a missing staging clone before rethrowing
    /// during merge-sandbox creation. Default 2. One retry is the production
    /// heal contract — if the source disappears AGAIN after restore, the loop
    /// falls through to rethrow rather than spinning indefinitely.
    /// </summary>
    public int MergeSandboxStagingRestoreAttempts { get; set; } = 2;

    /// <summary>
    /// Maximum number of operator questions an agent can emit per work item
    /// before the pipeline ignores further questions and continues processing.
    /// Default 10.
    /// </summary>
    public int MaxQuestionsPerWorkItem { get; set; } = 10;

    /// <summary>
    /// Maximum automatic re-invocations after a failed agent exec, applied by
    /// <see cref="Agents.AgentSuspendResilience"/>. Default 1.
    /// </summary>
    public int AgentSuspendMaxRetries { get; set; } = 1;

    /// <summary>
    /// Maximum CLI-native session-resume retries the base agent runner will
    /// attempt after a transient crash that captured a session id in stdout.
    /// Applied by <see cref="Agents.SessionResumeOptions"/>. Default 2 — one
    /// retry covers the typical 429 / OOM / SIGPIPE blip, the second exists
    /// so a single mid-resume blip does not collapse the work item. Set to 0
    /// to disable session resume (fall back to the legacy single-shot
    /// re-invocation retry).
    /// </summary>
    public int AgentSessionResumeMaxAttempts { get; set; } = Agents.SessionResumeOptions.DefaultMaxResumeAttempts;

    /// <summary>
    /// Maximum number of sequential auto-merge race recoveries the upstream-push
    /// loop will perform before parking the item. Each recovery costs a full LLM
    /// merge-phase re-invocation. When the upstream base is a moving target
    /// (hammered by sibling writes / direct pushes), this cap bounds the retry
    /// cost. Default 3 (separate from and narrower than <c>UpstreamPushMaxAttempts</c>).
    /// </summary>
    public int AutoMergeRaceRecoveryMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Whether to keep the same warm VM/sandbox alive across work<->rework cycles.
    /// Default true.
    /// </summary>
    public bool EnableSandboxReuse { get; set; } = true;

    /// <summary>
    /// Maximum reuse cycles (invocations) for a single work sandbox before it is recreated.
    /// Default 3.
    /// </summary>
    public int MaxSandboxReuses { get; set; } = 3;

    /// <summary>
    /// Maximum lifetime of a reused work sandbox before it is recreated.
    /// Default 1 hour.
    /// </summary>
    public TimeSpan MaxSandboxLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Utilization threshold (CurrentAdmittedSandboxes / MaxConcurrentSandboxes) above which
    /// a reused sandbox is disposed to free capacity for other runs.
    /// Default 0.85 (85%).
    /// </summary>
    public double SandboxPressureThreshold { get; set; } = 0.85;

    /// <summary>
    /// When true, auditors declaring
    /// <see cref="CodeyBox.Core.IAuditor.CanShortCircuitOnBlockingFinding"/> run before
    /// the remaining auditors and a blocking gate result skips the rest of the
    /// audit iteration. Default true.
    /// </summary>
    public bool AuditShortCircuitEnabled { get; set; } = true;

    /// <summary>
    /// Maximum time a single auditor may run without completing or emitting
    /// an LLM stdout chunk. A value of zero disables the per-auditor idle
    /// guard. Default 5 minutes.
    /// </summary>
    public TimeSpan AuditorIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether audit sandboxes prepend a lightweight <c>dotnet</c> shim that
    /// turns redundant auditor-initiated <c>dotnet build</c> and
    /// <c>dotnet test</c> invocations into an immediate successful no-op.
    /// The deterministic required-build gate has already run before LLM
    /// review, so this protects auditor capacity without changing work,
    /// merge, or conflict-resolution sandboxes. Default true.
    /// </summary>
    public bool BlockRedundantDotnetBuildTestInAuditSandbox { get; set; } = true;

    /// <summary>
    /// Whether to build and seed a cross-agent handoff brief on the next
    /// invocation after the orchestrator falls back from one
    /// <c>AgentKind</c> to another mid-iteration. The brief itself is built
    /// by the wired <c>ICrossAgentHandoffBriefBuilder</c> (currently
    /// <c>AgentStreamBriefBuilder</c>) and injected by the
    /// <c>CrossAgentHandoffPromptPreprocessor</c> as a fenced
    /// <c>[UNTRUSTED DATA SECTION]</c> block. Default <c>false</c>: the
    /// brief is sourced from attacker-influenceable inputs (prior agent
    /// stream + branch state), so the feature is opt-in until an operator
    /// has confidence in the upstream defences for their workload.
    /// </summary>
    public bool EnableHandoffSeeding { get; set; }

    public void Validate()
    {
        if (MaxSandboxReuses < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSandboxReuses), "MaxSandboxReuses must be >= 1");
        }
        if (MaxSandboxLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSandboxLifetime), "MaxSandboxLifetime must be positive");
        }
        if (double.IsNaN(SandboxPressureThreshold)
            || double.IsInfinity(SandboxPressureThreshold)
            || SandboxPressureThreshold < 0.0
            || SandboxPressureThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(SandboxPressureThreshold), "SandboxPressureThreshold must be between 0.0 and 1.0 inclusive");
        }
        if (AuditorIdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AuditorIdleTimeout), "AuditorIdleTimeout must be non-negative");
        }
    }
}

/// <summary>
/// Shared, swappable holder for the current <see cref="PipelineTuningOptions"/>.
/// Registered as a DI singleton so <see cref="PipelineRunner"/> reads through
/// the same reference the hot-reload coordinator writes to.
/// Mirrors the <see cref="AgentConcurrencySnapshot"/> pattern.
/// </summary>
public sealed class PipelineTuningSnapshot
{
    private PipelineTuningOptions _current;

    public PipelineTuningSnapshot(PipelineTuningOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        initial.Validate();
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read.
    /// </summary>
    public PipelineTuningOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// In-flight reads observe either the old or the new reference, never a
    /// partial state.
    /// </summary>
    public void Replace(PipelineTuningOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        next.Validate();
        Volatile.Write(ref _current, next);
    }
}
