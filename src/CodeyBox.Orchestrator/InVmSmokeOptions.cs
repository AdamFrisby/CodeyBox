using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning for the in-VM smoke prober (<see cref="InVmSmokeProber"/>). Bound
/// from <c>CodeyBox:Smoke:InVm</c>.
/// </summary>
public sealed record InVmSmokeOptions
{
    /// <summary>
    /// Enable or disable the in-VM smoke prober entirely. Default true.
    /// Operators whose sandbox provider has no VM (process / bubblewrap local
    /// dev) or who want to skip the per-baseline provision can set this false.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Sandbox image to clone the probe VM from (mirrors the dispatch image).</summary>
    public string ImageReference { get; init; } = "";

    /// <summary>
    /// Hosts the probe sandbox is allowed to reach. The auth/status steps
    /// (cursor <c>agent status</c>, opencode <c>opencode providers</c>) need
    /// egress to the agent's API; mirror the agent allow-list here.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>
    /// Optional host network profile override for project-less probe paths
    /// such as manual force-probes and legacy sweeps. Dispatch passes an
    /// explicit project phase target; when both this option and a project target
    /// are absent, the prober treats the baseline target as unclonable rather
    /// than falling back to a live launch.
    /// </summary>
    public string? NetworkProfile { get; init; }

    /// <summary>Per-step exec timeout inside the sandbox. Default 30s.</summary>
    public int StepTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Hard timeout on the VM provisioning step (<see cref="ISandboxProvider.CreateAsync"/>):
    /// baseline clone, "multipass launch" / "multipass start", and any wait for
    /// the sandbox to reach Running. Default 120s.
    ///
    /// <para>The per-step exec timeout (<see cref="StepTimeoutSeconds"/>) only
    /// bounds <em>each in-sandbox command</em>; it cannot fire until provisioning
    /// has produced a sandbox to exec into. A wedged or pathologically slow clone
    /// (observed 2026-06-01: multipass logs "Launching multipass VM ... (10-30s)"
    /// and never returns, while /repo is never mounted) would otherwise hang the
    /// dispatch gate forever — pre-step timeouts never fire, the gate never reaches
    /// a verdict, <see cref="FailClosedOnProbeFault"/> only triggers on a verdict
    /// fault (not a hang), so every worker wedges before mounting /repo and the
    /// queue stalls. This timeout is the hard floor under that failure mode: an
    /// overrun throws a transient fault that benches the agent under the
    /// configured policy (fail-closed on the gate, fail-open on the sweep), the
    /// worker pool slot is released by the existing finally block, and dispatch
    /// continues.</para>
    /// </summary>
    public int ProvisionTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Top-level deadline on the dispatch gate call
    /// (<see cref="InVmSmokeProber.EnsureProbedAsync"/>) — a safety net that
    /// guarantees the gate returns a verdict (or a fail-closed bench) within a
    /// bounded wall-clock time, even if some inner step the per-operation timeouts
    /// don't cover (a stuck sandbox dispose, an unanticipated hang in a custom
    /// probe) would otherwise leave the worker waiting forever. Default 180s.
    ///
    /// <para>When the deadline fires the gate benches the agent under the
    /// <see cref="FailClosedOnProbeFault"/> policy and returns; the in-flight
    /// probe task is left to finish in the background and reconciles on the next
    /// gate call. Non-positive disables the deadline (the gate then relies on
    /// inner timeouts only — appropriate for tests with synthetic clocks). The
    /// background sweep (<see cref="InVmSmokeProber.ProbeAllAsync"/>) is not
    /// bound by this deadline since it performs no dispatch and cannot wedge a
    /// worker; the per-operation timeouts apply there.</para>
    /// </summary>
    public int GateDeadlineSeconds { get; init; } = 180;

    /// <summary>
    /// How the <em>dispatch gate</em> (<see cref="InVmSmokeProber.EnsureProbedAsync"/>)
    /// reacts when an in-VM probe cannot run to a verdict — provisioning fault,
    /// exec error, step timeout, or credential-store fault.
    ///
    /// <para><b>true (default, fail-closed):</b> an inconclusive dispatch-gate
    /// probe temporarily benches the agent under the in-VM smoke source so the
    /// router never dispatches to a CLI that was never verified in-sandbox —
    /// the first work item after startup or a baseline rebake is gated by a real
    /// in-VM check rather than racing the background sweep, which is the whole
    /// point of this gate. The bench is not cached and self-heals on the next
    /// successful probe (background sweep or a later gate probe), so it converges
    /// back without an operator reset once the host recovers — keep the
    /// background sweep enabled so benched agents can recover. <b>false
    /// (fail-open):</b> a transient fault leaves availability unchanged, so a
    /// flaky host never benches a working agent; the trade-off is that the
    /// exit-127 / auth cascade window stays open for the first dispatch until a
    /// later probe runs. Operators on infra so flaky that benching causes more
    /// disruption than the cascade risk can opt out by setting this false.</para>
    ///
    /// <para>The background sweep (<see cref="InVmSmokeProber.ProbeAllAsync"/>)
    /// always fails open regardless of this setting — it performs no dispatch,
    /// so a transient sweep fault has no cascade to gate and benching there
    /// would only risk a self-inflicted outage.</para>
    /// </summary>
    public bool FailClosedOnProbeFault { get; init; } = true;

    /// <summary>
    /// Agents allowed to route without a registered <c>IInVmSmokeProbe</c>.
    /// When the prober is active, an agent named in an <c>AgentClass</c> with no
    /// in-VM probe is benched at startup (AC#1: caught at smoke time, not first
    /// dispatch) unless its <see cref="AgentKind.Value"/> is listed here —
    /// the escape hatch for agents with no first-party sandbox CLI driven by
    /// this pipeline. Defaults to <c>copilot</c> to preserve back-compat for
    /// operators who have not yet installed the Copilot CLI in their baseline
    /// image; once <c>CopilotInVmSmokeProbe</c> is registered the probe runs
    /// and the entry has no effect for that agent. Matched case-insensitively.
    /// </summary>
    public IReadOnlyList<string> ExemptAgentsWithoutProbe { get; init; } = [AgentKind.Copilot.Value];

    /// <summary>
    /// How long an in-VM result is cached for a given baseline ref before a
    /// re-probe. Default 60 minutes — a rebake invalidates immediately via the
    /// changed ref, so this only bounds intra-baseline staleness.
    /// </summary>
    public int CacheTtlMinutes { get; init; } = 60;

    /// <summary>
    /// Interval between background re-probe sweeps. The first sweep runs at
    /// startup. Cache hits make steady-state sweeps free; a sweep only
    /// provisions a VM when the baseline ref changed or the TTL expired.
    /// Default 5 minutes. Non-positive disables the periodic sweep.
    /// </summary>
    public int SweepIntervalSeconds { get; init; } = 300;
}
