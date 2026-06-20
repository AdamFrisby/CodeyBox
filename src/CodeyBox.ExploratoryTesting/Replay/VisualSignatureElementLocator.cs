using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Non-accessibility recognition fallback: returns the recorded target's
/// centre <b>iff</b> the current screen is byte-identical to the
/// recorder's <see cref="TraceVisualDescriptor.SourceScreenshotPng"/>. The
/// brief mandates 'accessibility tree when present, ELSE OCR /
/// visual-template / vision match of the recorded descriptor' — this is the
/// minimum-viable visual recognition that does not require a PNG-decoding
/// dependency or a vision model.
///
/// <list type="bullet">
///   <item><b>Recognition, not coordinate-trust.</b> The locator returns
///   the recorded centre only when the visual identity check passes — i.e.
///   the entire current screen looks pixel-equal to the recorded source.
///   That is a strict visual match: when it holds, every element on screen
///   is at the position it was recorded at, so the recorded centre is
///   genuinely the right click target. When it fails, the locator returns
///   null and the engine surfaces NotFound rather than driving input at
///   stale pixels.</item>
///   <item><b>Tightly scoped.</b> Strict PNG byte-equality misses any
///   render where encoder choices, anti-aliasing, or driver variance shifts
///   the bytes. This is intentional — the conservative default keeps the
///   locator from silently approving a layout regression. A richer
///   template / OCR / vision-LLM locator can plug in front of this one via
///   <see cref="CompositeElementLocator"/> when a future PR adds the
///   image-processing infrastructure.</item>
///   <item><b>No recorded source = no match.</b> When the recorder did not
///   capture <see cref="TraceVisualDescriptor.SourceScreenshotPng"/>,
///   the locator returns null. Callers that want a 'best-effort recorded-
///   region' coordinate trust must wire that explicitly — the brief
///   forbids implicit raw-coordinate fallbacks.</item>
/// </list>
/// </summary>
public sealed class VisualSignatureElementLocator : IElementLocator
{
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
        var source = visual.SourceScreenshotPng;
        if (source is null || source.Length == 0) return null;

        var region = visual.Region;
        if (region.Width <= 0 || region.Height <= 0) return null;

        byte[] current;
        try
        {
            current = await sandbox.GetScreenshotAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A transient screenshot failure cannot be confused with a real
            // 'screen has changed' signal — return null so the engine surfaces
            // NotFound rather than silently driving input at a stale pixel.
            return null;
        }

        if (current is null) return null;
        if (current.Length != source.Length) return null;
        if (!current.AsSpan().SequenceEqual(source)) return null;

        var cx = region.X + region.Width / 2;
        var cy = region.Y + region.Height / 2;
        return new LocatedTarget
        {
            CenterX = cx,
            CenterY = cy,
            Region = region,
            Source = "visual-signature",
            Confidence = 0.9,
        };
    }
}
