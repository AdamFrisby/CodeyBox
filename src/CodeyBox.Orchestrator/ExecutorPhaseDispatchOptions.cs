namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for executor phase dispatch (see <see cref="ExecutorPhaseProxy"/>).
/// Bound under <c>CodeyBox:ExecutorPhaseDispatch</c>. The whole record is
/// hot-reloadable through a delegate accessor — a config edit lands on the
/// next dispatch without an orchestrator restart. Operational values live
/// here, never as literals in the proxy or validator.
/// </summary>
public sealed class ExecutorPhaseDispatchOptions
{
    /// <summary>
    /// Maximum tar bytes accepted back from an executor per dispatch. The
    /// transport enforces this cap while receiving (see
    /// <c>IExecutorPhaseTransport.StageOutToArchiveAsync</c>) so
    /// executor-controlled content cannot fill the orchestrator disk before
    /// validation; the validator re-checks the landed size as defense in
    /// depth.
    /// Equivalent to <c>MultipassRemoteSandboxOptions.StageOutMaxArchiveBytes</c>.
    /// </summary>
    public long StageOutMaxArchiveBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Maximum non-metadata tar entries accepted per staged-back archive.
    /// Equivalent to <c>MultipassRemoteSandboxOptions.StageOutMaxEntries</c>.
    /// </summary>
    public int StageOutMaxEntries { get; set; } = 200_000;

    /// <summary>
    /// Maximum declared regular-file payload divided by archive bytes.
    /// Equivalent to <c>MultipassRemoteSandboxOptions.StageOutMaxExpansionRatio</c>.
    /// </summary>
    public double StageOutMaxExpansionRatio { get; set; } = 1.5d;

    /// <summary>
    /// How long a delivered dispatch result is replayed from the idempotency
    /// store on redelivery. Mirrors the API idempotency TTL.
    /// </summary>
    public TimeSpan IdempotencyTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Maximum serialized bytes accepted in a dispatch request payload.</summary>
    public int MaxRequestPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum findings accepted in an executor-returned result.</summary>
    public int MaxResultFindings { get; set; } = 128;

    /// <summary>Maximum chars accepted per finding in an executor-returned result.</summary>
    public int MaxFindingLengthChars { get; set; } = 8192;

    /// <summary>Maximum chars accepted in an executor-returned error message.</summary>
    public int MaxResultErrorLengthChars { get; set; } = 8192;

    /// <summary>
    /// Requeue delay surfaced when no executor host can currently accept a
    /// phase because every eligible host is full, cordoned, unhealthy, or
    /// mismatched on credential/profile. Mirrors
    /// <c>MultipassRemoteSandboxOptions.PlacementRecheckIn</c>: the work item
    /// is requeued under this backoff rather than failed. Hot-reloadable.
    /// </summary>
    public TimeSpan PlacementRecheckIn { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a host that fails dispatch with a transport error is skipped
    /// for new placements before the next dispatch probes it again. Mirrors
    /// <c>MultipassRemoteSandboxOptions.RuntimeUnhealthyBackoff</c>.
    /// Hot-reloadable.
    /// </summary>
    public TimeSpan RuntimeUnhealthyBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fails fast on misconfiguration so a bad bound surfaces at dispatch
    /// time instead of silently admitting an unbounded payload.
    /// </summary>
    public void Validate()
    {
        if (StageOutMaxArchiveBytes <= 0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:StageOutMaxArchiveBytes must be > 0.");
        if (StageOutMaxEntries <= 0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:StageOutMaxEntries must be > 0.");
        if (double.IsNaN(StageOutMaxExpansionRatio) || double.IsInfinity(StageOutMaxExpansionRatio) || StageOutMaxExpansionRatio < 1.0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:StageOutMaxExpansionRatio must be a finite value >= 1.0.");
        if (IdempotencyTtl <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:IdempotencyTtl must be positive.");
        if (MaxRequestPayloadBytes <= 0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:MaxRequestPayloadBytes must be > 0.");
        if (MaxResultFindings <= 0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:MaxResultFindings must be > 0.");
        if (MaxFindingLengthChars <= 0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:MaxFindingLengthChars must be > 0.");
        if (MaxResultErrorLengthChars <= 0)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:MaxResultErrorLengthChars must be > 0.");
        if (PlacementRecheckIn <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:PlacementRecheckIn must be positive.");
        if (RuntimeUnhealthyBackoff <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "CodeyBox:ExecutorPhaseDispatch:RuntimeUnhealthyBackoff must be positive.");
    }
}
