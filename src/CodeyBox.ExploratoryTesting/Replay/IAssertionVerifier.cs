namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Verifies a recorded <see cref="TraceAssertion"/> against the current
/// observation of the screen, in real terms (what the user actually sees /
/// what the accessibility tree exposes). Returns null on success, or a
/// human-readable diagnostic on mismatch.
///
/// <para>Treated as a seam so non-web modalities can plug in richer
/// matchers (CLI output match, API response shape, ...). The default
/// implementation handles the three kinds documented on
/// <see cref="TraceAssertion.Kind"/>.</para>
/// </summary>
public interface IAssertionVerifier
{
    /// <summary>
    /// Return null on success; a diagnostic string when the assertion
    /// does not hold against the current observation.
    /// </summary>
    /// <param name="assertion">The recorded assertion to verify.</param>
    /// <param name="currentScreenshotPng">Screenshot captured AFTER the action — the "now" frame.</param>
    /// <param name="recordedScreenshotPng">
    /// Per-step recorded observation screenshot, when present. The recorded
    /// screenshot is threaded through the call (not stored on the verifier)
    /// so multiple replays may share one verifier instance safely in parallel.
    /// </param>
    /// <param name="accessibilitySnapshotJson">Current accessibility-tree snapshot, when available.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> VerifyAsync(
        TraceAssertion assertion,
        byte[]? currentScreenshotPng,
        byte[]? recordedScreenshotPng,
        string? accessibilitySnapshotJson,
        CancellationToken ct);
}
