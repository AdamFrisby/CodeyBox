namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Coarse, host-side check that a screenshot byte sequence looks like a
/// rendered application UI rather than a blank desktop or a missing frame.
/// Uses PNG signature + compressed-size as a low-cost proxy: a flat-color
/// XFCE desktop PNG-compresses to a few hundred bytes, while any rendered
/// UI with text and chrome essentially never goes under ~2 KiB.
///
/// <para>This is intentionally a stand-in for the deeper pixel-diversity
/// check that <c>CodeyBox.Audit.GraphicalSmokeAuditor</c> uses
/// (<c>PngPixelStats.FromPng</c>, which decompresses scanlines and tracks
/// unique-color count + luma range). When the harness is promoted to the
/// shared assembly, swap this for that — the API shape (one
/// <see cref="LooksLikeRenderedUi"/> call returning bool) stays the same.</para>
/// </summary>
internal static class ScreenshotReadinessProbe
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// Floor on PNG byte length we trust as "the browser drew our app." A
    /// totally blank XFCE desktop at 1280x800 PNG-compresses to ~600-900
    /// bytes; even loading-spinner-on-white is comfortably under 1 KiB.
    /// 2 KiB picks a value past those without false-negativing on a small
    /// real UI (which has text glyphs and chrome and rarely compresses
    /// below 5-10 KiB).
    /// </summary>
    public const int MinBytesForRenderedUi = 2048;

    public static bool LooksLikeRenderedUi(ReadOnlySpan<byte> png) =>
        png.Length >= MinBytesForRenderedUi
        && png.Length >= PngSignature.Length
        && png[..PngSignature.Length].SequenceEqual(PngSignature);
}
