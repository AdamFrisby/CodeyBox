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
    /// Host network profile used both to attach the probe sandbox and to
    /// resolve the active baseline ref (baselines are keyed by profile+flavor).
    /// Null = resolve against the provider's default.
    /// </summary>
    public string? NetworkProfile { get; init; }

    /// <summary>Per-step exec timeout inside the sandbox. Default 30s.</summary>
    public int StepTimeoutSeconds { get; init; } = 30;

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
