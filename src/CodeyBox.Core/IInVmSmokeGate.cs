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

    /// <summary>
    /// Benches every agent named in <paramref name="classes"/> that has no
    /// registered in-VM probe (AC#1: an agent whose sandbox CLI can never be
    /// verified must be routed past at smoke time, not discovered at first
    /// dispatch). Ownership of the enablement decision, the exempt list, the
    /// registered-probe set, and the availability mutation all live behind this
    /// abstraction so callers (startup coverage validator, hot-reload bridge)
    /// never duplicate that policy or bind to the concrete registry.
    ///
    /// <para>Benching only happens when the prober is active (<see cref="Enabled"/>)
    /// and the agent is not on the exempt list; otherwise the uncovered agent is
    /// reported but left routable. Idempotent — safe to re-run on every config
    /// reload. Returns one outcome per <em>uncovered</em> agent so the caller can
    /// surface it to operators; covered agents are omitted.</para>
    /// </summary>
    IReadOnlyList<InVmSmokeCoverageOutcome> EnforceMissingProbeCoverage(
        IReadOnlyList<InVmSmokeClassCoverage> classes);
}

/// <summary>
/// One agent class's membership, passed to
/// <see cref="IInVmSmokeGate.EnforceMissingProbeCoverage"/>. <see cref="Agents"/>
/// are the raw configured agent names (the same strings the router treats as
/// <c>AgentMembership.Agent</c>), so the bench is keyed identically to the
/// router's availability read.
/// </summary>
public sealed record InVmSmokeClassCoverage(string ClassId, IReadOnlyList<string> Agents);

/// <summary>What happened to an uncovered agent during coverage enforcement.</summary>
public enum InVmSmokeCoverageAction
{
    /// <summary>Benched under the missing-probe source so the router routes past it.</summary>
    Benched,

    /// <summary>Uncovered but on the exempt list (no first-party sandbox CLI) — warned only.</summary>
    Exempt,

    /// <summary>Uncovered but the prober is inactive (disabled / no probes) — warned only.</summary>
    ProberInactive,
}

/// <summary>
/// Per-uncovered-agent result of
/// <see cref="IInVmSmokeGate.EnforceMissingProbeCoverage"/>. <see cref="ClassIds"/>
/// is the full set of classes that named the agent (its blast radius).
/// </summary>
public sealed record InVmSmokeCoverageOutcome(
    string Agent,
    IReadOnlyList<string> ClassIds,
    InVmSmokeCoverageAction Action);
