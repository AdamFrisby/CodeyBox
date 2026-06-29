using CodeyBox.Core;
using SharedPngBitmap = CodeyBox.ExploratoryTesting.Replay.PngBitmap;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Non-accessibility recognition fallback. The locator consumes the recorded
/// visual descriptor in this order:
///
/// <list type="number">
///   <item>Search the current screenshot for <see cref="TraceVisualDescriptor.TemplatePng"/>.</item>
///   <item>When no explicit template exists, crop the recorded
///   <see cref="TraceVisualDescriptor.SourceScreenshotPng"/> at
///   <see cref="TraceVisualDescriptor.Region"/> and search for that crop on
///   the current screenshot.</item>
///   <item>Use <see cref="TraceVisualDescriptor.OcrText"/> against the
///   current accessibility/OCR tree when the sandbox exposes text bounds.</item>
///   <item>As a final compatibility fallback, accept a full current/source
///   byte match.</item>
/// </list>
///
/// <para>All positive paths require some recorded visual signal to match the
/// current screen. When matching cannot be proven, the locator returns null so
/// replay fails with NotFound instead of clicking stale coordinates.</para>
/// </summary>
public sealed class VisualSignatureElementLocator : IElementLocator
{
    private readonly IElementLocator _accessibilityLocator;
    private readonly IOcrTextLocator _ocrLocator;

    public VisualSignatureElementLocator()
        : this(DefaultAccessibilityMatcher.Instance)
    {
    }

    public VisualSignatureElementLocator(IAccessibilityMatcher matcher)
        : this(new AccessibilityElementLocator(matcher))
    {
    }

    public VisualSignatureElementLocator(IElementLocator accessibilityLocator)
        : this(accessibilityLocator, null)
    {
    }

    public VisualSignatureElementLocator(IElementLocator accessibilityLocator, IOcrTextLocator? ocrLocator)
    {
        _accessibilityLocator = accessibilityLocator
            ?? throw new ArgumentNullException(nameof(accessibilityLocator));
        _ocrLocator = ocrLocator ?? TesseractOcrTextLocator.Instance;
    }

    public async Task<LocatedTarget?> LocateAsync(
        ISandbox sandbox,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        var visual = descriptor.Visual;
        var region = visual.Region;
        if (!HasAnyVisualSignal(visual)) return null;

        var current = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);

        if (current is null || current.Length == 0) return null;

        if (TryLocateTemplate(current, visual, ct, out var templateHit))
            return templateHit;

        if (TryLocateSourceCrop(current, visual, ct, out var cropHit))
            return cropHit;

        var ocrTreeHit = await LocateOcrTextAsync(sandbox, current, visual, options, ct).ConfigureAwait(false);
        if (ocrTreeHit is not null) return ocrTreeHit;

        var source = visual.SourceScreenshotPng;
        if (source is not null
            && source.Length > 0
            && region.Width > 0
            && region.Height > 0
            && current.Length == source.Length
            && current.AsSpan().SequenceEqual(source))
        {
            return FromRegion(visual, "visual-signature", 0.85);
        }

        return null;
    }

    private static bool TryLocateTemplate(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        CancellationToken ct,
        out LocatedTarget? hit)
    {
        hit = null;
        var templatePng = visual.TemplatePng;
        if (templatePng is null || templatePng.Length == 0) return false;

        if (SharedPngBitmap.TryDecode(currentPng, out var current)
            && SharedPngBitmap.TryDecode(templatePng, out var template)
            && (current.TryFindBestExact(template, PreferredTopLeft(visual.Region), ct, out var point)
                || current.TryFindBestTolerant(template, PreferredTopLeft(visual.Region), ct, out point)))
        {
            var (offsetX, offsetY) = ResolveClickOffset(visual, template.Width, template.Height);
            hit = new LocatedTarget
            {
                CenterX = point.X + offsetX,
                CenterY = point.Y + offsetY,
                Region = new TraceBoundingRegion
                {
                    X = point.X,
                    Y = point.Y,
                    Width = template.Width,
                    Height = template.Height,
                },
                Source = "visual-template",
                Confidence = 0.9,
                Evidence = LocatedTargetEvidence.Visual,
            };
            return true;
        }

        return false;
    }

    private static bool TryLocateSourceCrop(
        byte[] currentPng,
        TraceVisualDescriptor visual,
        CancellationToken ct,
        out LocatedTarget? hit)
    {
        hit = null;
        var sourcePng = visual.SourceScreenshotPng;
        if (sourcePng is null || sourcePng.Length == 0) return false;

        if (SharedPngBitmap.TryDecode(sourcePng, out var source)
            && SharedPngBitmap.TryDecode(currentPng, out var current)
            && source.TryCrop(visual.Region, out var crop)
            && (current.TryFindBestExact(crop, PreferredTopLeft(visual.Region), ct, out var point)
                || current.TryFindBestTolerant(crop, PreferredTopLeft(visual.Region), ct, out point)))
        {
            var (offsetX, offsetY) = ResolveClickOffset(visual, crop.Width, crop.Height);
            hit = new LocatedTarget
            {
                CenterX = point.X + offsetX,
                CenterY = point.Y + offsetY,
                Region = new TraceBoundingRegion
                {
                    X = point.X,
                    Y = point.Y,
                    Width = crop.Width,
                    Height = crop.Height,
                },
                Source = "visual-source-crop",
                Confidence = 0.88,
                Evidence = LocatedTargetEvidence.Visual,
            };
            return true;
        }

        return false;
    }

    private async Task<LocatedTarget?> LocateOcrTextAsync(
        ISandbox sandbox,
        byte[] currentPng,
        TraceVisualDescriptor visual,
        ReplayOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(visual.OcrText)) return null;

        var descriptor = new TraceTargetDescriptor
        {
            Accessibility = new TraceAccessibilityDescriptor { Text = visual.OcrText },
            Visual = new TraceVisualDescriptor { Region = visual.Region },
        };
        var hit = await _accessibilityLocator.LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
        if (hit is not null)
            return hit with
            {
                Source = "visual-ocr-tree",
                Evidence = hit.Evidence | LocatedTargetEvidence.Accessibility | LocatedTargetEvidence.Ocr,
            };

        descriptor = descriptor with
        {
            Accessibility = new TraceAccessibilityDescriptor { Name = visual.OcrText },
        };
        hit = await _accessibilityLocator.LocateAsync(sandbox, descriptor, options, ct).ConfigureAwait(false);
        if (hit is not null)
            return hit with
            {
                Source = "visual-ocr-tree",
                Evidence = hit.Evidence | LocatedTargetEvidence.Accessibility | LocatedTargetEvidence.Ocr,
            };

        return await _ocrLocator.LocateTextAsync(sandbox, currentPng, visual, ct).ConfigureAwait(false);
    }

    private static bool HasAnyVisualSignal(TraceVisualDescriptor visual)
    {
        var region = visual.Region;
        return region.Width > 0 && region.Height > 0
            || visual.TemplatePng is { Length: > 0 }
            || visual.SourceScreenshotPng is { Length: > 0 }
            || !string.IsNullOrWhiteSpace(visual.OcrText);
    }

    private static LocatedTarget FromRegion(TraceVisualDescriptor visual, string source, double confidence)
    {
        var region = visual.Region;
        var (offsetX, offsetY) = ResolveClickOffset(visual, region.Width, region.Height);
        return new LocatedTarget
        {
            CenterX = region.X + offsetX,
            CenterY = region.Y + offsetY,
            Region = region,
            Source = source,
            Confidence = confidence,
            Evidence = LocatedTargetEvidence.Visual,
        };
    }

    private static (int X, int Y)? PreferredTopLeft(TraceBoundingRegion region) =>
        region.Width > 0 && region.Height > 0 ? (region.X, region.Y) : null;

    private static (int X, int Y) ResolveClickOffset(
        TraceVisualDescriptor visual,
        int width,
        int height)
    {
        var offsetX = visual.ClickOffsetX is int x && x >= 0 && x < width ? x : width / 2;
        var offsetY = visual.ClickOffsetY is int y && y >= 0 && y < height ? y : height / 2;
        return (offsetX, offsetY);
    }

}
