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
/// pool ceiling wins as back-pressure.
/// </para>
///
/// <para>
/// <b>Semantics:</b>
/// <list type="bullet">
/// <item><c>MaxConcurrent &gt;= 1</c> — active per-agent cap.</item>
/// <item>Agent entry omitted entirely — no per-agent cap; agent is bounded
///   only by the global worker-pool ceiling. This is how you express
///   "unlimited within the global pool".</item>
/// <item><c>MaxConcurrent &lt;= 0</c> — <b>rejected at load time</b>. Setting
///   a cap of 0 used to silently mean "unlimited", which is dangerously
///   counter-intuitive: operators trying to pause an agent by setting
///   <c>MaxConcurrent: 0</c> would instead remove its cap entirely. The safe
///   ways to stop an agent are to remove it from the relevant
///   <c>AgentClasses[*].Members</c> entry or pause the queue.</item>
/// </list>
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
/// To leave an agent uncapped, omit its entry — do NOT set
/// <c>MaxConcurrent: 0</c>.
/// </para>
/// </summary>
public sealed class AgentConcurrencyOptions
{
    /// <summary>Per-agent cap entries keyed by <see cref="CodeyBox.Core.AgentKind.Value"/>.</summary>
    public Dictionary<string, AgentConcurrencyEntry> Members { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a list of human-readable validation failures, or an empty list
    /// when <paramref name="opts"/> is valid. Rejects any member entry with
    /// <c>MaxConcurrent &lt;= 0</c> — see the type-level remarks for why.
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentConcurrencyOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var failures = new List<string>();
        foreach (var kv in opts.Members)
        {
            if (kv.Value is null)
            {
                failures.Add(
                    $"CodeyBox:AgentConcurrency:Members:{kv.Key} is null — remove the key " +
                    "to leave the agent uncapped, or supply a MaxConcurrent >= 1.");
                continue;
            }
            if (kv.Value.MaxConcurrent <= 0)
            {
                failures.Add(
                    $"CodeyBox:AgentConcurrency:Members:{kv.Key}:MaxConcurrent must be >= 1 " +
                    $"(got {kv.Value.MaxConcurrent}). To leave the agent uncapped, omit the " +
                    "entry entirely. To stop dispatch to this agent, remove it from " +
                    "AgentClasses[*].Members or pause the queue — MaxConcurrent=0 is NOT a " +
                    "'disabled' switch and is rejected to prevent the counter-intuitive " +
                    "'0 means unlimited' footgun.");
            }
        }
        return failures;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <see cref="Validate"/> reports
    /// any failures, joining the messages so the operator sees every problem at
    /// once. Called from the orchestrator constructor and from
    /// <see cref="OrchestratorService.ApplyAgentConcurrencyReload"/> so both
    /// startup and hot-reload reject bad config loudly.
    /// </summary>
    public static void ValidateAndThrow(AgentConcurrencyOptions opts)
    {
        var failures = Validate(opts);
        if (failures.Count == 0) return;
        throw new ArgumentException(
            "Invalid CodeyBox:AgentConcurrency configuration: " + string.Join("; ", failures),
            nameof(opts));
    }
}

public sealed class AgentConcurrencyEntry
{
    /// <summary>
    /// Maximum number of work items that may run concurrently while routed to
    /// this agent kind. <b>Must be &gt;= 1.</b> Values &lt;= 0 are rejected at
    /// config load — to leave the agent uncapped, omit the entry entirely.
    /// See <see cref="AgentConcurrencyOptions"/> for the rationale.
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
