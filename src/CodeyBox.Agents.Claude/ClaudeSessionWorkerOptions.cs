namespace CodeyBox.Agents.Claude;

/// <summary>
/// Bound from <c>CodeyBox:ClaudeSession</c>. The session-capable worker
/// (<see cref="ClaudeSessionWorker"/>) is OFF by default — the existing
/// one-shot <see cref="ClaudeAgentRunner"/> is the registered runner for
/// Claude unless an operator opts in here. The flags compose with the
/// per-agent-class-member <c>UseSessionWorker</c> and per-project
/// <c>UseClaudeSessionWorker</c> switches; the worker is only used when ALL
/// three (global enable, project allow, member opt-in) agree.
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
