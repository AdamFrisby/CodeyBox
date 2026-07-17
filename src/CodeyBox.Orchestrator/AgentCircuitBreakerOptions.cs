using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hot-reloadable tuning for the per-agent dispatch <see cref="AgentCircuitBreaker"/>.
/// Bound directly from the <c>CodeyBox:AgentCircuitBreaker</c> config section.
/// Defaults are deliberately lenient: a healthy agent never hits
/// <see cref="FailureThreshold"/> consecutive failures, so enabling the breaker
/// by default only changes behaviour for agents that are genuinely failing —
/// which is exactly the safety net this exists to provide.
/// </summary>
public sealed class AgentCircuitBreakerOptions
{
    /// <summary>
    /// Master switch. When false the breaker is a no-op and every gate check
    /// returns "allowed" so dispatch behaves exactly as before this feature.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Failures of ANY kind within <see cref="Window"/> (with no intervening
    /// success) that OPEN the breaker. Default 3 — one transient blip never
    /// benches an otherwise-healthy agent.
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Rolling window over which failures are counted. Default 5 minutes.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the breaker stays Open (excluding the agent from dispatch)
    /// before admitting a half-open trial. Default 2 minutes.
    /// </summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Trial dispatches admitted while HalfOpen before further dispatch is
    /// blocked pending an outcome. One success closes the breaker; one failure
    /// re-opens it. Default 1.
    /// </summary>
    public int HalfOpenTrials { get; set; } = 1;

    /// <summary>
    /// Optional per-agent overrides keyed by <see cref="AgentKind.Value"/>.
    /// Any unset field on an entry inherits the corresponding global value.
    /// </summary>
    public Dictionary<string, AgentCircuitBreakerOverride> PerAgent { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective, validated <see cref="CircuitBreakerSettings"/> for
    /// an agent kind, applying any per-agent override on top of the globals.
    /// </summary>
    public CircuitBreakerSettings ResolveSettings(AgentKind agent)
    {
        AgentCircuitBreakerOverride? o = null;
        if (!string.IsNullOrEmpty(agent.Value)
            && PerAgent is { } overrides
            && overrides.TryGetValue(agent.Value, out var found))
            o = found;

        return CircuitBreakerSettings.Create(
            o?.FailureThreshold ?? FailureThreshold,
            o?.Window ?? Window,
            o?.Cooldown ?? Cooldown,
            o?.HalfOpenTrials ?? HalfOpenTrials);
    }
}

/// <summary>
/// Per-agent override for <see cref="AgentCircuitBreakerOptions"/>. Null fields
/// inherit the corresponding global value.
/// </summary>
public sealed class AgentCircuitBreakerOverride
{
    public int? FailureThreshold { get; set; }
    public TimeSpan? Window { get; set; }
    public TimeSpan? Cooldown { get; set; }
    public int? HalfOpenTrials { get; set; }
}

/// <summary>
/// Shared, swappable holder for the current <see cref="AgentCircuitBreakerOptions"/>.
/// Registered as a DI singleton so the single <see cref="AgentCircuitBreaker"/>
/// (shared by the router's dispatch gate and the pipeline's outcome feed) reads
/// through one reference; the hot-reload coordinator publishes new tuning via
/// <see cref="Replace"/> and the next gate/outcome read picks it up without a
/// process restart. Mirrors <see cref="AgentConcurrencySnapshot"/>.
/// </summary>
public sealed class AgentCircuitBreakerSnapshot
{
    private AgentCircuitBreakerOptions _current;

    public AgentCircuitBreakerSnapshot(AgentCircuitBreakerOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>Current snapshot. Volatile read so a concurrent <see cref="Replace"/> cannot tear the reference.</summary>
    public AgentCircuitBreakerOptions Current => Volatile.Read(ref _current);

    /// <summary>Atomically publishes <paramref name="next"/> as the new snapshot.</summary>
    public void Replace(AgentCircuitBreakerOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
