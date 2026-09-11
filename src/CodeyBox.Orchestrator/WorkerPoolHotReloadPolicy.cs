namespace CodeyBox.Orchestrator;

/// <summary>
/// Single source of truth for which <c>CodeyBox:WorkerPool</c> fields are
/// hot-reloadable and which still require a restart.
///
/// <para>
/// <b>Hot-reloadable</b> (re-bound by <c>AgentConfigHotReload</c> without
/// restarting the host):
/// <list type="bullet">
/// <item><c>MaxConcurrentWorkers</c> — resizes the dispatcher concurrency gate.</item>
/// <item><c>MaxConcurrentSandboxes</c> — resizes the sandbox admission gate.</item>
/// <item><c>MinSpawnInterval</c> — replaces the live spawn-pacing floor.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Restart-required</b> (captured into singleton plumbing at startup):
/// <list type="bullet">
/// <item><c>DispatchGateAcquisitionBackoff</c> and
/// <c>MaxConsecutiveDispatchGateTimeoutsBeforeEscalation</c> — read from the
/// startup <c>OrchestratorOptions</c> snapshot on every dispatch pickup; there
/// is no live reload bridge for them yet.</item>
/// <item><c>NoProgressBackoffBase</c>, <c>NoProgressBackoffMax</c>, and
/// <c>MaxNoProgressRedispatches</c> — likewise read from the startup snapshot
/// on every no-progress re-dispatch decision.</item>
/// </list>
/// </para>
///
/// <para>
/// The sets are string names (not lambdas) so tests can assert by reflection
/// that they partition every <see cref="WorkerPoolOptions"/> property: adding
/// a new option without classifying it here fails the parity test.
/// </para>
/// </summary>
public static class WorkerPoolHotReloadPolicy
{
    /// <summary>WorkerPool fields the reload path re-binds at runtime.</summary>
    public static IReadOnlyList<string> HotReloadableFields { get; } =
    [
        nameof(WorkerPoolOptions.MaxConcurrentWorkers),
        nameof(WorkerPoolOptions.MaxConcurrentSandboxes),
        nameof(WorkerPoolOptions.MinSpawnInterval),
    ];

    /// <summary>WorkerPool fields that require a restart to take effect.</summary>
    public static IReadOnlyList<string> RestartRequiredFields { get; } =
    [
        nameof(WorkerPoolOptions.DispatchGateAcquisitionBackoff),
        nameof(WorkerPoolOptions.MaxConsecutiveDispatchGateTimeoutsBeforeEscalation),
        nameof(WorkerPoolOptions.NoProgressBackoffBase),
        nameof(WorkerPoolOptions.NoProgressBackoffMax),
        nameof(WorkerPoolOptions.MaxNoProgressRedispatches),
    ];
}
