namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Tuning knobs for <see cref="ReplayEngine"/>. Defaults match the JobTrack
/// pilot's web-graphical target; CLI / native modalities pass their own
/// instance.
///
/// <para>All numeric knobs validate in their <c>init</c> setters so a caller
/// can't accidentally configure the locator into an infinite loop (a
/// zero-step search would never advance) or the visual wait into a tight
/// spin (zero poll interval). Validation runs whether the instance is built
/// with object-initializer syntax or via a <c>with</c> expression.</para>
/// </summary>
public sealed record ReplayOptions
{
    private readonly int _screenWidth = 1280;
    private readonly int _screenHeight = 800;
    private readonly int _maxScrollAttempts = 3;
    private readonly int _scrollStep = 5;
    private readonly TimeSpan _visualWaitTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _visualWaitPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly int _stableFrameCount = 2;
    private readonly int _ringSearchRadius = 24;
    private readonly int _ringSearchStep = 8;

    /// <summary>
    /// Width of the captured screen, in pixels. Used by the default
    /// reachability checker to decide whether a located rect is off-screen.
    /// Defaults to the JobTrack pilot resolution.
    /// </summary>
    public int ScreenWidth
    {
        get => _screenWidth;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "ScreenWidth must be positive.");
            _screenWidth = value;
        }
    }

    /// <summary>
    /// Height of the captured screen, in pixels.
    /// </summary>
    public int ScreenHeight
    {
        get => _screenHeight;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "ScreenHeight must be positive.");
            _screenHeight = value;
        }
    }

    /// <summary>
    /// Number of scroll attempts the engine performs when the located target
    /// is off-screen, before giving up with <see cref="ReplayFailureKind.OffScreen"/>.
    /// </summary>
    public int MaxScrollAttempts
    {
        get => _maxScrollAttempts;
        init
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxScrollAttempts cannot be negative.");
            _maxScrollAttempts = value;
        }
    }

    /// <summary>
    /// Magnitude (in scroll units) of each scroll attempt. Positive scrolls
    /// down/right when the target is below/beyond the viewport; negative
    /// scrolls the other way.
    /// </summary>
    public int ScrollStep
    {
        get => _scrollStep;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "ScrollStep must be positive.");
            _scrollStep = value;
        }
    }

    /// <summary>
    /// Maximum wall-clock the visual / observational wait will spend polling
    /// for a stable / expected screen state before failing the step with
    /// <see cref="ReplayFailureKind.WaitTimeout"/>.
    /// </summary>
    public TimeSpan VisualWaitTimeout
    {
        get => _visualWaitTimeout;
        init
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "VisualWaitTimeout must be positive.");
            _visualWaitTimeout = value;
        }
    }

    /// <summary>
    /// Interval between screenshot polls during the visual wait. Must be &gt; 0
    /// to avoid a tight-spin polling loop.
    /// </summary>
    public TimeSpan VisualWaitPollInterval
    {
        get => _visualWaitPollInterval;
        init
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "VisualWaitPollInterval must be positive.");
            _visualWaitPollInterval = value;
        }
    }

    /// <summary>
    /// Number of consecutive identical screenshots required before the
    /// visual wait declares the screen "settled". Two is enough for a
    /// genuine settle (one frame to capture + one equal follower); bump
    /// higher on noisy displays. Rejects <c>&lt; 2</c> because the wait
    /// needs at least one prior frame to compare against — a single-frame
    /// settle is indistinguishable from "I just captured for the first time
    /// and have nothing to compare to."
    /// </summary>
    public int StableFrameCount
    {
        get => _stableFrameCount;
        init
        {
            if (value < 2)
                throw new ArgumentOutOfRangeException(nameof(value), value, "StableFrameCount must be at least 2 — the wait needs a prior frame to compare against.");
            _stableFrameCount = value;
        }
    }

    /// <summary>
    /// Custom-locator compatibility knob for bounded coordinate-neighborhood
    /// searches. The default accessibility locator ignores this value because
    /// point/ring probes do not supply current bounds and must not be treated
    /// as successful relocation.
    /// </summary>
    public int RingSearchRadius
    {
        get => _ringSearchRadius;
        init
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "RingSearchRadius cannot be negative.");
            _ringSearchRadius = value;
        }
    }

    /// <summary>
    /// Pixel step for custom locators that still perform bounded
    /// coordinate-neighborhood searches. Must be &gt; 0 so custom search loops
    /// can guarantee forward progress.
    /// </summary>
    public int RingSearchStep
    {
        get => _ringSearchStep;
        init
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "RingSearchStep must be positive.");
            _ringSearchStep = value;
        }
    }
}
