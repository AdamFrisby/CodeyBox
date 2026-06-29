namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Compares two screenshots for the purpose of a <c>visual-match</c> assertion.
/// Default-injected into <see cref="DefaultAssertionVerifier"/> so callers can
/// swap the comparison strategy without rewriting the verifier.
///
/// <para><b>Why a seam:</b> the shipped default compares decoded PNG pixels,
/// not the encoded byte stream. Callers can still wire a perceptual-diff or
/// wider tolerance comparator (e.g. SSIM, PSNR, DeltaE) without touching the
/// verifier.</para>
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
/// Comparator for tests or callers that really need encoded byte equality.
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

/// <summary>
/// Default screenshot comparator: decoded-pixel equality for PNGs, falling back
/// to byte equality for non-PNG buffers used by lightweight tests.
/// </summary>
public sealed class DecodedPixelsScreenshotComparer : IScreenshotComparer
{
    public static DecodedPixelsScreenshotComparer Instance { get; } = new();

    public ScreenshotComparison Compare(byte[] recorded, byte[] current)
    {
        if (PngBitmap.TryDecode(recorded, out var recordedBitmap)
            && PngBitmap.TryDecode(current, out var currentBitmap))
        {
            var matches = recordedBitmap.HasSamePixelsAs(currentBitmap);
            return matches
                ? new ScreenshotComparison(true)
                : new ScreenshotComparison(false, "visual-match assertion: decoded pixels differ");
        }

        if (recorded.Length != current.Length)
            return new ScreenshotComparison(false);
        return new ScreenshotComparison(recorded.AsSpan().SequenceEqual(current));
    }
}
