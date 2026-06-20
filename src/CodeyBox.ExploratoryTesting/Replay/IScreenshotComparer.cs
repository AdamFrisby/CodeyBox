namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Compares two screenshots for the purpose of a <c>visual-match</c> assertion.
/// Default-injected into <see cref="DefaultAssertionVerifier"/> so callers can
/// swap the comparison strategy without rewriting the verifier.
///
/// <para><b>Why a seam:</b> the shipped <see cref="ExactBytesScreenshotComparer"/>
/// is byte-equality of raw PNG bytes — a deliberately conservative default that
/// matches only when the recorded and current PNGs are bit-identical. Real
/// renders are non-deterministic (PNG encoder choices, anti-aliasing, font
/// hinting, GPU/driver variance), so byte-equality alone is too strict for any
/// host-to-host replay. Production callers wire a perceptual-diff or
/// pixel-tolerance comparator (e.g. SSIM, PSNR, ΔE2000) here without touching
/// the verifier.</para>
/// </summary>
public interface IScreenshotComparer
{
    /// <summary>
    /// Compare <paramref name="recorded"/> against <paramref name="current"/>.
    /// Both buffers are non-null when the verifier calls this method.
    /// </summary>
    ScreenshotComparison Compare(byte[] recorded, byte[] current);
}

/// <summary>
/// Verdict from <see cref="IScreenshotComparer.Compare"/>.
/// <see cref="Matches"/> is true when the two screenshots are considered
/// equivalent under the comparator's policy; <see cref="Diagnostic"/> is an
/// optional override the verifier surfaces when the screenshots differ.
/// </summary>
public readonly record struct ScreenshotComparison(bool Matches, string? Diagnostic = null);

/// <summary>
/// Default screenshot comparator: byte-equality of raw PNG bytes. Conservative
/// by design — it accepts only bit-identical renders, so a flaky diff never
/// silently passes. For production renders where bit-identical capture is
/// impossible, supply a perceptual-diff implementation via
/// <see cref="DefaultAssertionVerifier"/>'s comparer constructor.
/// </summary>
public sealed class ExactBytesScreenshotComparer : IScreenshotComparer
{
    public static ExactBytesScreenshotComparer Instance { get; } = new();

    public ScreenshotComparison Compare(byte[] recorded, byte[] current)
    {
        if (recorded.Length != current.Length)
            return new ScreenshotComparison(false);
        return new ScreenshotComparison(recorded.AsSpan().SequenceEqual(current));
    }
}
