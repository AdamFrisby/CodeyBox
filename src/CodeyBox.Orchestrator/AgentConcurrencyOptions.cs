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
