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
