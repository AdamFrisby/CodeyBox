using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IElementLocator"/>. Recognition strategy:
///
/// <list type="number">
///   <item>If the descriptor carries at least one non-empty accessibility
///   field (role, name, text, or element-type), probe at the recorded centre
///   with <see cref="ISandbox.GetAccessibilityAtPointAsync"/>. On a match,
///   return a high-confidence hit at the recorded centre.</item>
///   <item>If the point probe misses, scan outward in concentric square rings
///   at <see cref="ReplayOptions.RingSearchStep"/>-pixel granularity up to
///   <see cref="ReplayOptions.RingSearchRadius"/> — every step-aligned cell
///   in each ring is probed, not just the corners — returning the first
///   accessibility match.</item>
///   <item>If the descriptor has no accessibility signal (or all
///   accessibility fields are null/empty), return null. Non-accessibility
///   recognition is the responsibility of a sibling locator chained behind
///   this one via <see cref="CompositeElementLocator"/> (the default engine
///   wiring includes <see cref="VisualSignatureElementLocator"/>).</item>
/// </list>
///
/// <para>The recorded centre is the central anchor for the point probe; it
/// survives small viewport shifts but not full layout re-flows. A future
/// vision-assisted heal seam (see <see cref="ILocatorHealer"/>) closes that
/// gap; this locator deliberately fails fast on a layout regression rather
/// than silently rewriting it.</para>
/// </summary>
public sealed class AccessibilityElementLocator : IElementLocator
{
    // Ring-hit confidence: lower than the centre-hit's 1.0 because nearby
    // matches reflect a small layout nudge, not the exact recorded geometry.
    // Surfaced for diagnostics only — the engine does not gate on confidence.
    internal const double RingHitConfidence = 0.85;

    public async Task<LocatedTarget?> LocateAsync(
        ISandbox sandbox,
        TraceTargetDescriptor descriptor,
        ReplayOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        var expected = descriptor.Accessibility;
        // Strict policy: with no usable accessibility signature, leave it for
        // the chained non-accessibility locator (e.g.
        // VisualSignatureElementLocator) to handle. An all-null descriptor
        // would silently match any element returned at the recorded centre
        // (StringMatches treats null/empty as 'matches anything'), which is a
        // false-positive failure class the brief explicitly calls out.
        if (expected is null) return null;
        if (!HasAnyAccessibilitySignal(expected)) return null;

        var region = descriptor.Visual.Region;
        var hasPoint = region.Width > 0 && region.Height > 0;
        if (!hasPoint) return null;

        var cx = region.X + region.Width / 2;
        var cy = region.Y + region.Height / 2;

        // Probe at the recorded centre even if it falls outside the viewport:
        // the reachability checker is the layer that distinguishes "off-screen
        // but resolvable by scroll" from "genuinely gone." If the sandbox can
        // answer for that coordinate at all, we honour the hit and let
        // reachability decide whether it's reachable.
        var pointHit = await ProbeAccessibilityAsync(sandbox, cx, cy, expected, ct).ConfigureAwait(false);
        if (pointHit is not null)
        {
            return new LocatedTarget
            {
                CenterX = cx,
                CenterY = cy,
                Region = region,
                Source = "accessibility-point",
                Confidence = 1.0,
            };
        }

        for (var radius = options.RingSearchStep;
             radius <= options.RingSearchRadius;
             radius += options.RingSearchStep)
        {
            foreach (var (dx, dy) in SquareRingOffsets(radius, options.RingSearchStep))
            {
                ct.ThrowIfCancellationRequested();
                var px = cx + dx;
                var py = cy + dy;
                if (!InScreen(px, py, options)) continue;
                var match = await ProbeAccessibilityAsync(sandbox, px, py, expected, ct).ConfigureAwait(false);
                if (match is not null)
                {
                    return new LocatedTarget
                    {
                        CenterX = px,
                        CenterY = py,
                        Region = region,
                        Source = "accessibility-ring",
                        Confidence = RingHitConfidence,
                    };
                }
            }
        }

        return null;
    }

    private static bool InScreen(int x, int y, ReplayOptions options) =>
        x >= 0 && x < options.ScreenWidth && y >= 0 && y < options.ScreenHeight;

    private static async Task<SandboxAccessibilitySnapshot?> ProbeAccessibilityAsync(
        ISandbox sandbox,
        int x,
        int y,
        TraceAccessibilityDescriptor expected,
        CancellationToken ct)
    {
        SandboxAccessibilitySnapshot? snap;
        try
        {
            snap = await sandbox.GetAccessibilityAtPointAsync(x, y, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A point probe can fail for benign reasons (the underlying tree
            // implementation refused this coordinate, transient IPC blip).
            // Treating it as "no element here" lets the search continue to the
            // next probe instead of aborting the whole replay. A genuine
            // sandbox crash will resurface on the next call site.
            return null;
        }
        if (snap is null) return null;
        return Matches(snap, expected) ? snap : null;
    }

    internal static bool Matches(SandboxAccessibilitySnapshot snap, TraceAccessibilityDescriptor expected)
    {
        if (!HasAnyAccessibilitySignal(expected)) return false;
        if (!StringMatches(snap.Role, expected.Role)) return false;
        if (!StringMatches(snap.Name, expected.Name)) return false;
        if (!StringMatches(snap.Text, expected.Text)) return false;
        if (!StringMatches(snap.ElementType, expected.ElementType)) return false;
        return true;
    }

    internal static bool HasAnyAccessibilitySignal(TraceAccessibilityDescriptor expected) =>
        !string.IsNullOrEmpty(expected.Role)
        || !string.IsNullOrEmpty(expected.Name)
        || !string.IsNullOrEmpty(expected.Text)
        || !string.IsNullOrEmpty(expected.ElementType);

    private static bool StringMatches(string? actual, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(actual ?? "", expected, StringComparison.Ordinal);
    }

    // Yields every (dx, dy) on the square ring at max(|dx|, |dy|) == radius,
    // stepping by `step`. Probes every step-aligned cell in the ring (not
    // only the corners), so a small layout nudge that misses the centre
    // point still gets sampled within the configured radius.
    private static IEnumerable<(int Dx, int Dy)> SquareRingOffsets(int radius, int step)
    {
        if (radius <= 0) yield break;
        for (var dy = -radius; dy <= radius; dy += step)
        {
            for (var dx = -radius; dx <= radius; dx += step)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                yield return (dx, dy);
            }
        }
    }
}
