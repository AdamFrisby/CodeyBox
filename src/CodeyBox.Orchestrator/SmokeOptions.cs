namespace CodeyBox.Orchestrator;

/// <summary>
/// Credential smoke test tuning. Bound from <c>CodeyBox:Smoke</c> in config.
/// </summary>
public sealed record SmokeOptions
{
    /// <summary>
    /// Enable or disable the smoke gate entirely. Default true. Operators on
    /// offline-only deployments can set this to false to skip all probes.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long to cache a probe result (Ok or failed) before re-probing.
    /// Default 15 minutes.
    /// </summary>
    public int CacheTtlMinutes { get; init; } = 15;

    /// <summary>
    /// Per-agent timeout for the startup probe. If the upstream API is slow
    /// at startup, this prevents blocking the orchestrator indefinitely.
    /// Default 10 seconds.
    /// </summary>
    public int StartupTimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Shared, swappable holder for the current <see cref="SmokeOptions"/>.
/// Registered as a DI singleton so dispatch gates read through the same
/// reference the hot-reload coordinator writes to.
/// </summary>
public sealed class SmokeOptionsSnapshot
{
    private SmokeOptions _current;

    public SmokeOptionsSnapshot(SmokeOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>
    /// Current snapshot. Volatile read so a concurrent <see cref="Replace"/>
    /// cannot tear the reference. Callers should bind once into a local for any
    /// compound read.
    /// </summary>
    public SmokeOptions Current => Volatile.Read(ref _current);

    public bool Enabled => Current.Enabled;

    /// <summary>
    /// Atomically publishes <paramref name="next"/> as the new snapshot.
    /// </summary>
    public void Replace(SmokeOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}
