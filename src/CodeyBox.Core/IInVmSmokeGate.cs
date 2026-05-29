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
    /// Returns <paramref name="kind"/>'s current availability, having first
    /// ensured the in-VM smoke verdict against the baseline the work item will
    /// actually be cloned from is reflected in it. The gate owns the full
    /// read→probe→re-read sequence so routing consumers get a verdict from this
    /// one call and never have to bind to the availability registry alongside the
    /// gate.
    ///
    /// <para><b>What provisions a VM.</b> Only a cache <em>miss</em> for the
    /// target <paramref name="baselineRef"/> provisions a VM. A cache hit never
    /// does — but a hit is not a no-op: the cached passing verdict is re-applied
    /// to the registry (reconciliation), so a cache-hit call can still <em>clear</em>
    /// an exclusion. An already-excluded agent normally short-circuits with no
    /// probe; the one exception is B1 pinning — when the agent is globally benched
    /// but this work item's pinned ref has its own cached pass, the gate re-enters
    /// to reconcile that per-ref pass (still a free cache hit, no VM) rather than
    /// returning the unrelated active-image bench.</para>
    ///
    /// <para><b>On an inconclusive probe fault</b> (provisioning, exec, timeout, or
    /// credential resolution) the behaviour follows the operator's
    /// <c>FailClosedOnProbeFault</c> policy: under the default fail-closed policy
    /// the agent is benched and a <em>new excluded</em> availability is returned
    /// (the failing verdict is never cached, so it self-heals on the next clean
    /// probe); under the opt-out fail-open policy the prior availability is
    /// returned unchanged. Never throws — a probe fault must not take down
    /// dispatch.</para>
    ///
    /// <para><paramref name="baselineRef"/> is the work item's pinned
    /// <see cref="WorkItem.BaselineImageRef"/> (B1 pinning) — the image the
    /// sandbox will be cloned from at dispatch. The gate probes and caches against
    /// that exact ref so a pass proves the CLI on the <em>pinned</em> image is
    /// healthy, not merely on whatever baseline happens to be active after a
    /// rebake. Pass <c>null</c> for unpinned work; the gate then falls back to the
    /// active baseline resolved internally.</para>
    /// </summary>
    Task<AgentAvailability> EnsureAvailableAsync(AgentKind kind, string? baselineRef, CancellationToken ct);

    /// <summary>
    /// Probes every registered agent against the active baseline. Driven by the
    /// background sweep service; sequential (never holds more than one probe VM
    /// at a time) and never throws. A no-op when disabled.
    /// </summary>
    Task ProbeAllAsync(CancellationToken ct);

    /// <summary>
    /// Operator-triggered forced re-probe of a single agent against the active
    /// baseline, used by the <c>/admin/agent/{name}/smoke</c> recovery endpoint
    /// after a fix (installed binary, rotated credentials, corrected auth path).
    /// Unlike <see cref="EnsureAvailableAsync"/> this does <em>not</em>
    /// short-circuit on an already-excluded agent — re-verifying a benched
    /// agent's in-sandbox CLI is the whole point of the operator call, so an
    /// in-VM exclusion (exit 127 / auth-path drift / workspace-trust) is cleared
    /// here rather than waiting for the next background sweep. Routes the admin
    /// endpoint through this port instead of the host-only credential probe, so
    /// an operator can no longer get "smoke ok" while an in-VM bench stands.
    /// Never throws. Returns the agent's availability after the probe, or
    /// <c>null</c> when in-VM smoke is disabled or no in-VM probe is registered
    /// for <paramref name="kind"/> (the caller then falls back to the host probe
    /// verdict for its 404 / response decision).
    /// </summary>
    Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct);
}

/// <summary>
/// Startup/hot-reload coverage policy for the in-VM smoke subsystem. Kept
/// separate from <see cref="IInVmSmokeGate"/> (interface segregation): coverage
/// enforcement is a pure config-driven decision that never provisions a VM,
/// whereas the gate's <see cref="IInVmSmokeGate.EnsureAvailableAsync"/> /
/// <see cref="IInVmSmokeGate.ProbeAllAsync"/> are runtime hooks that may. Callers
/// that only enforce coverage (startup validator, hot-reload bridge) depend on
/// this narrow port rather than the full dispatch-gate contract.
/// </summary>
public interface IInVmSmokeCoveragePolicy
{
    /// <summary>
    /// Benches every agent named in <paramref name="classes"/> that has no
    /// registered in-VM probe (AC#1: an agent whose sandbox CLI can never be
    /// verified must be routed past at smoke time, not discovered at first
    /// dispatch). Ownership of the enablement decision, the exempt list, the
    /// registered-probe set, and the availability mutation all live behind this
    /// abstraction so callers (startup coverage validator, hot-reload bridge)
    /// never duplicate that policy or bind to the concrete registry.
    ///
    /// <para>Benching only happens when the prober is active (feature enabled and
    /// at least one probe registered) and the agent is not on the exempt list;
    /// otherwise the uncovered agent is reported but left routable. Idempotent —
    /// safe to re-run on every config reload. Returns one outcome per
    /// <em>uncovered</em> agent so the caller can surface it to operators;
    /// covered agents are omitted.</para>
    /// </summary>
    IReadOnlyList<InVmSmokeCoverageOutcome> EnforceMissingProbeCoverage(
        IReadOnlyList<InVmSmokeClassCoverage> classes);
}

/// <summary>
/// One agent class's membership, passed to
/// <see cref="IInVmSmokeCoveragePolicy.EnforceMissingProbeCoverage"/>. <see cref="Agents"/>
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
/// <see cref="IInVmSmokeCoveragePolicy.EnforceMissingProbeCoverage"/>. <see cref="ClassIds"/>
/// is the full set of classes that named the agent (its blast radius).
/// </summary>
public sealed record InVmSmokeCoverageOutcome(
    string Agent,
    IReadOnlyList<string> ClassIds,
    InVmSmokeCoverageAction Action);
