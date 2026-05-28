namespace CodeyBox.Core;

/// <summary>
/// Dispatch-path hook that guarantees an agent's in-sandbox CLI has been smoke
/// probed against the active baseline before the router trusts the agent as
/// routable. Unlike the background sweep — which only converges <em>eventually</em>
/// — this is awaited on the routing hot path, so the very first work item after
/// startup or a baseline rebake cannot race ahead of the probe and reproduce
/// the exit-127 / auth-path cascade the probe exists to catch.
///
/// <para>A cache hit is free (no VM); only a cache miss provisions one VM. The
/// implementation must never throw — a probe fault must not take down dispatch.</para>
/// </summary>
public interface IInVmSmokeGate
{
    /// <summary>
    /// Whether in-VM smoke probing is active (feature enabled and at least one
    /// probe registered). Both the dispatch gate and the background sweep
    /// short-circuit when this is false, so consumers can bind to this
    /// abstraction rather than the concrete prober.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Ensures the in-VM smoke verdict for <paramref name="kind"/> against the
    /// active baseline is reflected in the availability registry. A no-op when
    /// the prober is disabled or no in-VM probe is registered for the kind.
    /// </summary>
    Task EnsureProbedAsync(AgentKind kind, CancellationToken ct);

    /// <summary>
    /// Probes every registered agent against the active baseline. Driven by the
    /// background sweep service; sequential (never holds more than one probe VM
    /// at a time) and never throws. A no-op when disabled.
    /// </summary>
    Task ProbeAllAsync(CancellationToken ct);
}
