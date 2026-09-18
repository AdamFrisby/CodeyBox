namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for the startup sweep that reaps stale <c>codeybox-*</c>
/// entries abandoned in the system temp path. Nests on the typed root as
/// <c>CodeyBox:TempSweep</c>; read once at host startup (a restart picks up
/// edits — there is no periodic re-sweep to hot-reload into).
///
/// <para>On hosts where <c>/tmp</c> is a tmpfs, every unreaped entry pins
/// RAM until reboot, so the defaults favour leaving entries alone: an entry
/// is removed only when its newest write timestamp is older than
/// <see cref="MaxAge"/>, and <see cref="MaxAge"/> is clamped to at least
/// <see cref="MinimumMaxAge"/> so a misconfigured zero threshold cannot wipe
/// a live run's working files.</para>
/// </summary>
public sealed class TempSweepOptions
{
    /// <summary>
    /// Floor for <see cref="MaxAge"/>. Values below this are raised to the
    /// floor instead of honoured: "remove everything newer than an hour" is
    /// never a legitimate startup sweep on a host that may be mid-run.
    /// </summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromHours(1);

    /// <summary>When false, the startup sweep is skipped entirely. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// An entry is stale only when nothing under it has been written for this
    /// long. Default 48 hours. Values under <see cref="MinimumMaxAge"/> are
    /// raised to the floor.
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Upper bound on candidate (<c>codeybox-*</c>) temp entries examined per
    /// sweep. Bounds startup latency on hosts with huge <c>/tmp</c> backlogs;
    /// when the cap trips the sweep stops early and reports
    /// <c>Truncated</c> so the next restart continues where it left off.
    /// Default 20,000. Zero or negative removes the cap (not recommended on
    /// hosts with large backlogs).
    /// </summary>
    public int MaxEntriesPerSweep { get; set; } = 20_000;

    /// <summary>
    /// Override for the directory swept. Null or empty (default) sweeps the
    /// system temp path. Set in tests to point at an isolated directory.
    /// </summary>
    public string? TempDirectory { get; set; }

    /// <summary>
    /// The effective staleness threshold after the conservative floor is applied.
    /// </summary>
    public TimeSpan EffectiveMaxAge => MaxAge < MinimumMaxAge ? MinimumMaxAge : MaxAge;
}
