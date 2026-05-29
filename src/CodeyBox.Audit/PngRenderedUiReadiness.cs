namespace CodeyBox.Audit;

/// <summary>
/// Host-side check that a PNG screenshot shows a non-uniform rendered desktop
/// (same pixel-diversity rules as <see cref="GraphicalSmokeAuditor"/>).
/// </summary>
public static class PngRenderedUiReadiness
{
    // WHY: match GraphicalSmokeAuditor — allow small compositor variation, reject
    // flat black/gray/white frames that indicate nothing useful rendered.
    private const int MinimumUsefulLumaRange = 8;

    /// <summary>
    /// Returns true when <paramref name="png"/> decodes as a supported PNG and
    /// has at least two distinct colors with sufficient luma range.
    /// </summary>
    public static bool LooksLikeRenderedUi(ReadOnlySpan<byte> png)
    {
        try
        {
            return PassesPixelDiversity(PngPixelStats.FromPng(png.ToArray()));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="png"/> decodes as a supported PNG and
    /// has at least two distinct colors with sufficient luma range.
    /// </summary>
    public static bool LooksLikeRenderedUi(byte[] png)
    {
        try
        {
            return PassesPixelDiversity(PngPixelStats.FromPng(png));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static bool PassesPixelDiversity(PngPixelStats stats) =>
        stats.PixelCount > 0
        && stats.UniqueColors >= 2
        && stats.LumaRange >= MinimumUsefulLumaRange;
}
