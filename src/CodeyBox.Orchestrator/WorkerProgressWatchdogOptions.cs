namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for the worker-progress watchdog — a lifecycle-wide progress
/// enforcer that supplements <see cref="DeadWorkerOptions"/> + <c>WorkTimeout</c>.
/// Bind under <c>CodeyBox:WorkerProgressWatchdog</c>. Hot-reloadable: every
/// sweep resolves the current value via the registered accessor.
///
/// <para>
/// Failure mode covered: a worker keeps heartbeating yet its work item makes
/// no progress (item.updatedAt frozen, no new agent-stream activity, and no
/// worker-side activity signal). The dead-worker reaper only catches workers
/// whose heartbeat went stale, and <c>WorkTimeout</c> only fences the agent
/// subprocess — neither covers the post-agent commit/transition step, nor the
/// pre-agent provisioning step.
/// </para>
/// </summary>
public sealed class WorkerProgressWatchdogOptions
{
    /// <summary>
    /// Wall-clock window without observed progress (item.updatedAt advancing
    /// OR the newest agent-stream <c>*.jsonl</c> mtime advancing OR a configured
    /// worker activity signal firing) before a bound worker is considered
    /// wedged. Heartbeat does NOT count as progress. Default 60 min. Set to
    /// <see cref="TimeSpan.Zero"/> to disable the watchdog entirely.
    /// </summary>
    public TimeSpan ProgressTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>How often the watchdog sweep runs. Default 60 s.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When true, a wedged worker is automatically disposed and its item
    /// re-queued from the appropriate recoverable resume state. When false,
    /// the item is parked at <see cref="CodeyBox.Core.WorkItemState.NeedsOperatorInput"/>
    /// with a diagnostic <c>LastError</c> so an operator can triage. Default
    /// true — autonomous-delivery posture; the dispatch queue keeps moving.
    /// </summary>
    public bool AutoRecover { get; set; } = true;

    /// <summary>
    /// When true, the watchdog treats item-owned host processes whose CPU tick
    /// counters advance between observations as progress. Merely seeing a
    /// tagged process is not enough. Sandbox providers derive
    /// <c>CODEYBOX_WORK_ITEM_ID</c> from timing work-item context so this
    /// signal is scoped to the work item instead of all agent CLIs on the host.
    /// Default true. Hot-reloadable on the next sweep.
    /// </summary>
    public bool ProcessCpuProgressSignalEnabled { get; set; } = true;

    /// <summary>
    /// When true, the watchdog can treat provider-reported sandbox activity
    /// transitions as progress. Static ownership of a live sandbox is not
    /// enough: providers whose guest CPU is not visible from host <c>/proc</c>
    /// must report a changing activity projection for this signal to fire.
    /// Default true. Hot-reloadable on the next sweep.
    /// </summary>
    public bool ActiveSandboxProgressSignalEnabled { get; set; } = true;

    /// <summary>
    /// Bounded timeout for the post-agent commit/branch-push/state-transition
    /// step. The agent subprocess already lives inside <c>WorkTimeout</c>;
    /// this fences the work the pipeline does AFTER the agent exits so a hang
    /// in <c>git commit</c> / <c>git push</c> / <c>store.UpdateAsync</c> fails
    /// the item within bounded time instead of holding the pool slot
    /// indefinitely. Default 10 min.
    /// </summary>
    public TimeSpan PostAgentTransitionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum number of automatic recovery transitions before the watchdog
    /// gives up and transitions an item to
    /// <c>AbandonedAfterRecoveryAttempts</c>. Mirrors
    /// <see cref="DeadWorkerOptions.MaxRecoveryAttempts"/> so a chronically
    /// wedging item cannot loop Working → Queued → Working forever consuming
    /// a slot on every iteration. Watchdog recoveries share the same
    /// <c>RecoveryAttempts</c> budget as reaper recoveries — they both
    /// represent a forced state transition driven by a stuck worker, just
    /// detected via different signals (stale heartbeat vs. stalled progress).
    /// Default 10.
    /// </summary>
    public int MaxRecoveryAttempts { get; set; } = 10;

    /// <summary>
    /// Per-item stale-updatedAt window. An item parked in an active in-flight
    /// state (Working / Reworking / Auditing / Merging / ReworkingForConflict /
    /// UpstreamPushing) whose <c>UpdatedAt</c> has not advanced for this
    /// duration is treated as wedged INDEPENDENT of pool-level spawn health
    /// and INDEPENDENT of worker heartbeat / process CPU activity. The wedged
    /// worker (if any) is aborted; the item is requeued preserving its work
    /// branch so the next pickup re-rebases onto current upstream main.
    ///
    /// <para>
    /// Distinct from <see cref="ProgressTimeout"/>: that detector walks the
    /// worker registry and treats CPU / sandbox / stream activity as progress,
    /// so a worker stuck in a transport reconnect loop (still burning CPU,
    /// still heartbeating, but the item never transitions) appears healthy.
    /// This detector walks items directly and watches <c>UpdatedAt</c> only —
    /// the user-observable progress signal — so it catches the reconnect-loop
    /// wedge and the orphaned-after-restart wedge that the worker-progress
    /// path misses.
    /// </para>
    ///
    /// <para>
    /// Set this comfortably above a normal phase duration so a long but
    /// legitimately-running phase is not interrupted. Default 75 min — above
    /// the default 60 min <see cref="ProgressTimeout"/> so the worker-progress
    /// path catches recoverable cases first, but tight enough to free a wedged
    /// slot before the ~90 min cases observed in production. Set to
    /// <see cref="TimeSpan.Zero"/> to disable the item-stale
    /// detector while keeping the worker-progress watchdog. Hot-reloadable.
    /// </para>
    /// </summary>
    public TimeSpan ItemStaleTimeout { get; set; } = TimeSpan.FromMinutes(75);

    /// <summary>
    /// How often the per-item stale-updatedAt sweep runs. The sweep walks
    /// items by state (not by the worker registry) so the cost is bounded by
    /// the small in-flight set; a low frequency is fine. Default 5 min.
    /// </summary>
    public TimeSpan ItemStaleCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cap on per-item stale-updatedAt auto-recoveries before the item is
    /// parked at <see cref="CodeyBox.Core.WorkItemState.NeedsOperatorInput"/>
    /// for operator triage. Shares the <c>RecoveryAttempts</c> budget with
    /// the reaper / worker-progress watchdog so a chronically wedging item
    /// cannot loop forever burning a slot per recovery. <c>0</c> means
    /// unlimited (the item-stale path will keep recovering — typically only
    /// useful in tests). Default 3: drastic intervention deserves a tighter
    /// cap than the standard recovery loop. Hot-reloadable.
    /// </summary>
    public int ItemStaleMaxRecoveryAttempts { get; set; } = 3;

    /// <summary>
    /// Validates the configured values. Throws
    /// <see cref="InvalidOperationException"/> on misconfiguration.
    /// </summary>
    public void Validate()
    {
        if (ProgressTimeout < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:ProgressTimeout ({ProgressTimeout}) must be >= 0.");

        if (CheckInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:CheckInterval ({CheckInterval}) must be > 0.");

        if (PostAgentTransitionTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:PostAgentTransitionTimeout ({PostAgentTransitionTimeout}) must be > 0.");

        if (ProgressTimeout > TimeSpan.Zero && ProgressTimeout < CheckInterval)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:ProgressTimeout ({ProgressTimeout.TotalSeconds}s) must be >= CheckInterval ({CheckInterval.TotalSeconds}s) " +
                "so a tick can observe at least one full progress window before tripping.");

        if (MaxRecoveryAttempts < 0)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:MaxRecoveryAttempts ({MaxRecoveryAttempts}) must be >= 0 (0 = unlimited).");

        if (ItemStaleTimeout < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout ({ItemStaleTimeout}) must be >= 0.");

        if (ItemStaleCheckInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:ItemStaleCheckInterval ({ItemStaleCheckInterval}) must be > 0.");

        if (ItemStaleTimeout > TimeSpan.Zero && ItemStaleTimeout < ItemStaleCheckInterval)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout ({ItemStaleTimeout.TotalSeconds}s) must be >= ItemStaleCheckInterval ({ItemStaleCheckInterval.TotalSeconds}s) " +
                "so a sweep can observe at least one full stale window before tripping.");

        if (ItemStaleMaxRecoveryAttempts < 0)
            throw new InvalidOperationException(
                $"CodeyBox:WorkerProgressWatchdog:ItemStaleMaxRecoveryAttempts ({ItemStaleMaxRecoveryAttempts}) must be >= 0 (0 = unlimited).");
    }
}
