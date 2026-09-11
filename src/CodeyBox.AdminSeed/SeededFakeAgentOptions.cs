namespace CodeyBox.AdminSeed;

/// <summary>
/// Hot-reloadable options for the seeded fake-agent run mode. Binds the
/// <c>CodeyBox:SeededFakeAgents</c> configuration section; every operational
/// value is a knob here, never a literal in the runner.
/// </summary>
public sealed class SeededFakeAgentOptions
{
    /// <summary>
    /// Master switch. False (the default) leaves the real agent runners as
    /// the only registered <see cref="Core.IAgentRunner"/> implementations.
    /// The seeded E2E/demo instance sets this true via environment config.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Determinism seed. The same seed + the same prompts always select the
    /// same fake behaviors and produce byte-identical artifact content.
    /// </summary>
    public int Seed { get; set; } = 42;

    /// <summary>
    /// File the fake runner stages on the success path. Must be a bare file
    /// name (no directories, no separators); validated at use.
    /// </summary>
    public string ArtifactFileName { get; set; } = "seeded-fake-change.md";

    /// <summary>
    /// Quota-reset hint (seconds) embedded in the quota-park diagnostic.
    /// </summary>
    public int QuotaResetSeconds { get; set; } = 60;

    /// <summary>
    /// Out of 100 hash buckets, how many default to success when the prompt
    /// carries no explicit behavior marker. Bounded to [0, 100].
    /// </summary>
    public int DefaultSuccessBuckets { get; set; } = 70;
}
