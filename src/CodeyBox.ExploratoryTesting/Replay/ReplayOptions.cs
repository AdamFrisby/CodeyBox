namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Tuning knobs for <see cref="ReplayEngine"/>. Defaults match the JobTrack
/// pilot's web-graphical target; CLI / native modalities pass their own
/// instance.
/// </summary>
public sealed record ReplayOptions
{
    /// <summary>
    /// Width of the captured screen, in pixels. Used by the default
    /// reachability checker to decide whether a located rect is off-screen.
    /// Defaults to the JobTrack pilot resolution.
    /// </summary>
    public int ScreenWidth { get; init; } = 1280;

    /// <summary>
    /// Height of the captured screen, in pixels.
    /// </summary>
    public int ScreenHeight { get; init; } = 800;

    /// <summary>
    /// Number of scroll attempts the engine performs when the located target
    /// is off-screen, before giving up with <see cref="ReplayFailureKind.OffScreen"/>.
    /// </summary>
    public int MaxScrollAttempts { get; init; } = 3;

    /// <summary>
    /// Magnitude (in scroll units) of each scroll attempt. Positive scrolls
    /// down when the target is below the viewport; negative scrolls up.
    /// </summary>
    public int ScrollStep { get; init; } = 5;

    /// <summary>
    /// Maximum wall-clock the visual / observational wait will spend polling
    /// for a stable / expected screen state before failing the step with
    /// <see cref="ReplayFailureKind.WaitTimeout"/>.
    /// </summary>
    public TimeSpan VisualWaitTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Interval between screenshot polls during the visual wait.
    /// </summary>
    public TimeSpan VisualWaitPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Number of consecutive identical screenshots required before the
    /// visual wait declares the screen "settled". Two is enough for a
    /// genuine settle; bump higher on noisy displays.
    /// </summary>
    public int StableFrameCount { get; init; } = 2;

    /// <summary>
    /// Half-size of the accessibility spiral search around the recorded
    /// click centre, in pixels. The engine probes
    /// <c>(cx + dx, cy + dy)</c> for <c>|dx|, |dy| &lt;= radius</c> stepping
    /// by <see cref="SpiralSearchStep"/> until an accessibility match is
    /// found or the radius is exhausted.
    /// </summary>
    public int SpiralSearchRadius { get; init; } = 24;

    /// <summary>
    /// Pixel step of the accessibility spiral search.
    /// </summary>
    public int SpiralSearchStep { get; init; } = 8;
}
