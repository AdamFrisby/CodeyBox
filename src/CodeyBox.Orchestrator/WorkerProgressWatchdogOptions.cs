using CodeyBox.Core;

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
    /// When true, the watchdog treats item-owned host processes as progress
    /// when either (a) their CPU tick counters advance between observations,
    /// or (b) a tagged process is currently in the running kernel state
    /// (<c>R</c>). A brief CPU-bound spike that doesn't span two samples still
    /// counts. Static presence of a tagged process alone, including
    /// uninterruptible sleep (<c>D</c>), is not enough. Sandbox providers derive
    /// <c>CODEYBOX_WORK_ITEM_ID</c> from
    /// timing work-item context so the signal is scoped to the work item
    /// instead of all agent CLIs on the host. Default true. Hot-reloadable
    /// on the next sweep.
    /// </summary>
    public bool ProcessCpuProgressSignalEnabled { get; set; } = true;

    /// <summary>
    /// When true, the watchdog can treat provider-reported active sandbox
    /// ownership as progress. Stable ownership of a live sandbox is enough;
    /// providers may report richer changing activity projections, but detached
    /// Multipass batch runs rely on the live VM ownership signal because guest
    /// CPU is not visible from host <c>/proc</c>. Default true. Hot-reloadable
    /// on the next sweep.
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
    /// Per-agent override map for <see cref="ProgressTimeout"/> and
    /// <see cref="ItemStaleTimeout"/>. Keyed on the lowercase
    /// <see cref="AgentKind.Value"/> (e.g. <c>"crock"</c>). When an in-flight
    /// item's <see cref="CodeyBox.Core.WorkItem.Agent"/> matches a key, the
    /// per-agent override wins over the global default for that sweep.
    ///
    /// <para>
    /// Motivation: agents with intrinsically long no-emission windows
    /// (notably <c>crock</c>, which submits to Anthropic's Message Batches
    /// API and waits minutes-to-hours on the batch worker) would otherwise
    /// be killed by the default 60-minute ProgressTimeout. Per-agent
    /// overrides keep the global default tight for synchronous agents while
    /// giving batch-latency agents room to breathe — without bumping the
    /// global value and losing protection for genuinely-wedged synchronous
    /// workers.
    /// </para>
    ///
    /// <para>
    /// Items with an unset or unmatched <see cref="CodeyBox.Core.WorkItem.Agent"/>
    /// fall back to the global timeouts. Hot-reloadable on the next sweep.
    /// </para>
    /// </summary>
    public Dictionary<string, AgentWatchdogOverride> PerAgent { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective <see cref="ProgressTimeout"/> for an item bound
    /// to <paramref name="agent"/>. Returns the per-agent override when one is
    /// configured for the kind, otherwise the global default.
    /// </summary>
    public TimeSpan ResolveProgressTimeout(AgentKind? agent)
    {
        if (agent is { } kind
            && PerAgent.TryGetValue(kind.Value, out var per)
            && per.ProgressTimeout is { } overrideValue
            && overrideValue >= TimeSpan.Zero)
        {
            return overrideValue;
        }
        return ProgressTimeout;
    }

    /// <summary>
    /// Resolves the effective <see cref="ItemStaleTimeout"/> for an item bound
    /// to <paramref name="agent"/>. Returns the per-agent override when one is
    /// configured for the kind, otherwise the global default.
    /// </summary>
    public TimeSpan ResolveItemStaleTimeout(AgentKind? agent)
    {
        if (agent is { } kind
            && PerAgent.TryGetValue(kind.Value, out var per)
            && per.ItemStaleTimeout is { } overrideValue
            && overrideValue >= TimeSpan.Zero)
        {
            return overrideValue;
        }
        return ItemStaleTimeout;
    }

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

        foreach (var (key, per) in PerAgent)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "CodeyBox:WorkerProgressWatchdog:PerAgent contains an empty or whitespace key; use the lowercase agent kind value (e.g. \"crock\").");

            if (per is null)
                throw new InvalidOperationException(
                    $"CodeyBox:WorkerProgressWatchdog:PerAgent:{key} is null; remove the entry or supply at least one override field.");

            if (per.ProgressTimeout is { } pt && pt < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"CodeyBox:WorkerProgressWatchdog:PerAgent:{key}:ProgressTimeout ({pt}) must be >= 0.");

            if (per.ItemStaleTimeout is { } it && it < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"CodeyBox:WorkerProgressWatchdog:PerAgent:{key}:ItemStaleTimeout ({it}) must be >= 0.");

            if (per.ProgressTimeout is { } ptCheck && ptCheck > TimeSpan.Zero && ptCheck < CheckInterval)
                throw new InvalidOperationException(
                    $"CodeyBox:WorkerProgressWatchdog:PerAgent:{key}:ProgressTimeout ({ptCheck.TotalSeconds}s) must be >= CheckInterval ({CheckInterval.TotalSeconds}s).");

            if (per.ItemStaleTimeout is { } itCheck && itCheck > TimeSpan.Zero && itCheck < ItemStaleCheckInterval)
                throw new InvalidOperationException(
                    $"CodeyBox:WorkerProgressWatchdog:PerAgent:{key}:ItemStaleTimeout ({itCheck.TotalSeconds}s) must be >= ItemStaleCheckInterval ({ItemStaleCheckInterval.TotalSeconds}s).");
        }
    }
}

/// <summary>
/// Per-agent override of <see cref="WorkerProgressWatchdogOptions.ProgressTimeout"/>
/// and <see cref="WorkerProgressWatchdogOptions.ItemStaleTimeout"/>. Unset
/// fields fall back to the global value; only the fields explicitly set on
/// the override participate. Both timeouts are validated against the global
/// <c>CheckInterval</c> / <c>ItemStaleCheckInterval</c> so a misconfigured
/// override surfaces at DI resolve time.
/// </summary>
public sealed class AgentWatchdogOverride
{
    /// <summary>
    /// Wall-clock window without observed progress for items bound to this
    /// agent kind. <see cref="TimeSpan.Zero"/> disables the per-worker
    /// watchdog for the agent kind (useful for an agent whose progress signal
    /// is entirely external to the orchestrator); leave null to inherit the
    /// global <see cref="WorkerProgressWatchdogOptions.ProgressTimeout"/>.
    /// </summary>
    public TimeSpan? ProgressTimeout { get; set; }

    /// <summary>
    /// Per-item <see cref="WorkItem.UpdatedAt"/>-stale window for items bound
    /// to this agent kind. <see cref="TimeSpan.Zero"/> disables the per-item
    /// stale watchdog for the agent kind; leave null to inherit the global
    /// <see cref="WorkerProgressWatchdogOptions.ItemStaleTimeout"/>.
    /// </summary>
    public TimeSpan? ItemStaleTimeout { get; set; }
}
