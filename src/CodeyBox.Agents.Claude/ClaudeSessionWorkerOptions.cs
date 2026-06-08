namespace CodeyBox.Agents.Claude;

/// <summary>
/// Bound from <c>CodeyBox:ClaudeSession</c>. The session-capable worker
/// (<see cref="ClaudeSessionWorker"/>) is OFF by default — the existing
/// one-shot <see cref="ClaudeAgentRunner"/> is the registered runner for
/// Claude unless an operator opts in here.
///
/// <para>This is item 2 of 3 in the resumable-Claude rollout: the worker
/// itself is built but the orchestrator-side dispatch wiring lands in item 3.
/// Once that ships, the global <see cref="Enabled"/> flag will compose with
/// per-agent-class-member and per-project opt-in switches so an operator can
/// route specific work items to the session worker incrementally.</para>
/// </summary>
public sealed class ClaudeSessionWorkerOptions
{
    /// <summary>
    /// Master switch. Default <c>false</c> — until an operator flips this,
    /// every Claude dispatch uses the legacy one-shot path. The current
    /// dispatched item is unaffected on hot-reload; only NEW dispatches read
    /// the updated value.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When the session worker is enabled and a turn observes a stream-json
    /// usage record, the per-turn metrics emitted via
    /// <c>IClaudeSessionMetricsSink</c> include the <c>cache_read</c> share so
    /// operators can verify the session is paying off. Setting this to false
    /// suppresses metric emission entirely (useful for diagnostic comparisons
    /// against the one-shot baseline).
    /// </summary>
    public bool EmitTurnMetrics { get; set; } = true;
}
