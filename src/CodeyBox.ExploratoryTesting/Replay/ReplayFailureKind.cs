namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// The precise reason a replay step failed. Reported back on
/// <see cref="ReplayStepResult.FailureKind"/> so callers can distinguish a
/// genuinely-regressed UI from a transient environment issue without parsing
/// the human-readable diagnostic.
/// </summary>
public enum ReplayFailureKind
{
    /// <summary>
    /// The recorded target could not be re-located on the current screen by
    /// any signal the engine attempted (accessibility / OCR / visual match).
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The target was re-located but lies entirely outside the current
    /// viewport and the configured scroll attempts did not bring it into view.
    /// Treated as a real regression class (off-screen control) — never coerced
    /// into success.
    /// </summary>
    OffScreen = 2,

    /// <summary>
    /// The target was re-located inside the viewport but a different element
    /// is on top at the intended click point. Treated as a real regression
    /// class (occluded control) — never coerced into success.
    /// </summary>
    Occluded = 3,

    /// <summary>
    /// The recorded post-condition assertion did not hold after the action
    /// re-applied.
    /// </summary>
    AssertionMismatch = 4,

    /// <summary>
    /// The visual / observational wait primitive did not see the expected
    /// state (or could not detect a stable frame) before the configured
    /// timeout elapsed.
    /// </summary>
    WaitTimeout = 5,

    /// <summary>
    /// The underlying input-dispatch call (real keyboard / mouse) raised
    /// an error from the sandbox layer.
    /// </summary>
    ActionFailed = 6,
}
