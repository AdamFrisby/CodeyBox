namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-agent concurrency caps. Layered on top of the global
/// <see cref="WorkerPoolOptions.MaxConcurrentWorkers"/> ceiling so the dispatcher
/// can hold a slot open in the global pool while still skipping items whose
/// routed agent has hit its individual cap.
///
/// <para>
/// Bind under <c>CodeyBox:AgentConcurrency</c>. Keys match <see cref="CodeyBox.Core.AgentKind.Value"/>
/// (case-insensitive). Sum of per-agent caps SHOULD NOT exceed
/// <see cref="WorkerPoolOptions.MaxConcurrentWorkers"/>; if it does the global
/// pool ceiling wins as back-pressure. A missing per-agent entry preserves the
/// pre-feature behaviour (no per-agent cap).
/// </para>
///
/// <para>
/// Example:
/// <code>
/// "AgentConcurrency": {
///   "codex":  { "MaxConcurrent": 1 },
///   "claude": { "MaxConcurrent": 2 },
///   "gemini": { "MaxConcurrent": 1 }
/// }
/// </code>
/// </para>
/// </summary>
public sealed class AgentConcurrencyOptions
{
    /// <summary>Per-agent cap entries keyed by <see cref="CodeyBox.Core.AgentKind.Value"/>.</summary>
    public Dictionary<string, AgentConcurrencyEntry> Members { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentConcurrencyEntry
{
    /// <summary>
    /// Maximum number of work items that may run concurrently while routed to
    /// this agent kind. Values &lt; 1 are treated as "no per-agent cap"
    /// (effectively unlimited within the global pool).
    /// </summary>
    public int MaxConcurrent { get; set; } = 0;
}

/// <summary>
/// Shared, swappable holder for the current <see cref="AgentConcurrencyOptions"/>.
/// Registered as a DI singleton so every consumer (<see cref="OrchestratorService"/>'s
/// dispatch gate AND <see cref="PipelineRunner"/>'s pickup-time rebase-resolver
/// cap-aware routing) reads through the same reference. The hot-reload
/// coordinator updates this one holder via <see cref="Replace"/>, and both
/// consumers' next read picks up the new caps — without it,
/// <see cref="OrchestratorService.ApplyAgentConcurrencyReload"/> would only
/// swap its own field and the resolver in <see cref="PipelineRunner"/> would
/// keep gating against the pre-reload caps until process restart.
/// </summary>
public sealed class AgentConcurrencySnapshot
{
    private AgentConcurrencyOptions _current;

    public AgentConcurrencySnapshot(AgentConcurrencyOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for
    /// any compound read (e.g. iterating <see cref="AgentConcurrencyOptions.Members"/>).
    /// </summary>
    public AgentConcurrencyOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// In-flight reads observe either the old or the new reference, never a
    /// partial state.
    /// </summary>
    public void Replace(AgentConcurrencyOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
