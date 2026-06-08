using System.Collections.Concurrent;

namespace CodeyBox.Agents.Claude;

/// <summary>
/// Bound from <c>CodeyBox:ClaudeSession</c>. The session-capable worker
/// (<see cref="ClaudeSessionWorker"/>) is OFF by default — the existing
/// one-shot <see cref="ClaudeAgentRunner"/> is the registered runner for
/// Claude unless an operator opts in here.
///
/// <para>
/// The instance is a singleton: callers (DI factories, hot-reload hooks) mutate
/// the same object so the worker observes the new values on the NEXT turn
/// without re-resolving anything. Reads are atomic for the primitive fields;
/// the override dictionaries use <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// so reads during a concurrent reload write are safe.
/// </para>
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

    /// <summary>
    /// Default transport (command-delivery + billing channel) for new
    /// Claude session turns. Defaults to <see cref="ClaudeSessionTransport.Print"/>
    /// — the existing <c>claude --print --resume</c> path — so an operator
    /// upgrade is a no-op. Set to <see cref="ClaudeSessionTransport.Acp"/> to
    /// route Claude work through the Agent Client Protocol (<c>claude --ide</c>).
    /// Hot-reloadable: subsequent turns read the new value at
    /// <see cref="ClaudeSessionWorker.SendTurnAsync"/> time.
    /// </summary>
    public ClaudeSessionTransport Transport { get; set; } = ClaudeSessionTransport.Print;

    /// <summary>
    /// Optional per-agent-class-member overrides for <see cref="Transport"/>.
    /// Keyed by the agent-class-member name; lookup is case-insensitive.
    /// When an entry matches the member that opened the session (carried on
    /// <see cref="ClaudeSessionWorker.AgentClassMemberMetadataKey"/>), it takes
    /// precedence over <see cref="Transport"/>. Edit via
    /// <c>CodeyBox:ClaudeSession:TransportOverridesByAgentClassMember:{member}</c>.
    /// </summary>
    public ConcurrentDictionary<string, ClaudeSessionTransport> TransportOverridesByAgentClassMember { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional per-project overrides for <see cref="Transport"/>. Keyed by
    /// project id (case-insensitive). Same lookup precedence as
    /// <see cref="TransportOverridesByAgentClassMember"/> — when both match,
    /// the per-class-member override wins (narrower scope).
    /// </summary>
    public ConcurrentDictionary<string, ClaudeSessionTransport> TransportOverridesByProject { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective transport for a session given the metadata hint
    /// stamped on the handle (project id, agent-class-member name). Order of
    /// precedence: per-agent-class-member &gt; per-project &gt; global
    /// <see cref="Transport"/>. Unknown keys silently fall through.
    /// </summary>
    public ClaudeSessionTransport ResolveTransport(IReadOnlyDictionary<string, string>? handleMetadata)
    {
        if (handleMetadata is not null)
        {
            if (handleMetadata.TryGetValue(ClaudeSessionWorker.AgentClassMemberMetadataKey, out var member)
                && !string.IsNullOrWhiteSpace(member)
                && TransportOverridesByAgentClassMember.TryGetValue(member, out var memberOverride))
                return memberOverride;
            if (handleMetadata.TryGetValue(ClaudeSessionWorker.ProjectIdMetadataKey, out var projectId)
                && !string.IsNullOrWhiteSpace(projectId)
                && TransportOverridesByProject.TryGetValue(projectId, out var projectOverride))
                return projectOverride;
        }
        return Transport;
    }
}
