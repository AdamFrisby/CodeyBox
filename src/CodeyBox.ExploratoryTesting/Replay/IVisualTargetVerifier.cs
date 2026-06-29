namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Verifies that a locator hit still matches the recorded visual descriptor on
/// the current screen. This keeps reachability independent from any concrete
/// visual locator implementation.
/// </summary>
public interface IVisualTargetVerifier
{
    VisualTargetVerificationStatus Verify(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        LocatedTarget target);
}

public enum VisualTargetVerificationStatus
{
    Verified,
    Mismatch,
    Unverifiable,
}

public sealed class DescriptorVisualTargetVerifier : IVisualTargetVerifier
{
    public static DescriptorVisualTargetVerifier Instance { get; } = new();

    public VisualTargetVerificationStatus Verify(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        LocatedTarget target)
    {
        ArgumentNullException.ThrowIfNull(currentPng);
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(target);

        if (visual.TemplatePng is { Length: > 0 }
            && PngBitmap.TryDecode(currentPng, out var currentTemplateScreen)
            && PngBitmap.TryDecode(visual.TemplatePng, out var template))
        {
            return currentTemplateScreen.MatchesAt(template, target.Region.X, target.Region.Y, PngPixelMatchOptions.ReplayTolerance)
                ? VisualTargetVerificationStatus.Verified
                : VisualTargetVerificationStatus.Mismatch;
        }

        if (visual.SourceScreenshotPng is { Length: > 0 } sourcePng)
        {
            if (PngBitmap.TryDecode(sourcePng, out var source)
                && PngBitmap.TryDecode(currentPng, out var current)
                && source.TryCrop(visual.Region, out var crop))
            {
                return current.MatchesAt(crop, target.Region.X, target.Region.Y, PngPixelMatchOptions.ReplayTolerance)
                    ? VisualTargetVerificationStatus.Verified
                    : VisualTargetVerificationStatus.Mismatch;
            }

            if (PngBitmap.TryDecode(sourcePng, out var sourceFull)
                && PngBitmap.TryDecode(currentPng, out var currentFull)
                && sourceFull.HasSamePixelsAs(currentFull))
            {
                return VisualTargetVerificationStatus.Verified;
            }

            if (currentPng.Length == sourcePng.Length && currentPng.AsSpan().SequenceEqual(sourcePng))
                return VisualTargetVerificationStatus.Verified;
        }

        return VisualTargetVerificationStatus.Unverifiable;
    }
}
