using CodeyBox.Audit;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Host-side check that a screenshot byte sequence shows a rendered application
/// UI rather than a blank or uniform desktop frame. Delegates to
/// <see cref="PngRenderedUiReadiness"/> (same pixel-diversity rules as
/// <see cref="GraphicalSmokeAuditor"/>).
/// </summary>
internal static class ScreenshotReadinessProbe
{
    public static bool LooksLikeRenderedUi(ReadOnlySpan<byte> png) =>
        PngRenderedUiReadiness.LooksLikeRenderedUi(png);
}
