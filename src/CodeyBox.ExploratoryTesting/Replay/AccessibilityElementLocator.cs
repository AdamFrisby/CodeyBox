using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IElementLocator"/>. Recognition strategy:
///
/// <list type="number">
///   <item>If the descriptor has accessibility role/name, probe at the recorded
///   centre with <see cref="ISandbox.GetAccessibilityAtPointAsync"/>. On a
///   role/name match, return a high-confidence hit at the recorded centre.</item>
///   <item>If the point probe misses, scan outward in concentric square rings
///   at <see cref="ReplayOptions.SpiralSearchStep"/>-pixel granularity up to
///   <see cref="ReplayOptions.SpiralSearchRadius"/> — every step-aligned cell
///   in each ring is probed, not just the corners — returning the first
///   accessibility match.</item>
///   <item>If the descriptor has no accessibility signal, return null —
///   the brief forbids trusting raw recorded coordinates. Once visual-
///   template / OCR fallback locators land they will plug in via additional
///   <see cref="IElementLocator"/> implementations chained behind this one.</item>
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
        // Strict policy: with no accessibility signature on the descriptor,
        // the brief forbids us from trusting the recorded raw coordinates.
        // Surfaces NotFound so a canvas/3D/untagged target without a visual-
        // template locator wired in fails deterministically instead of
        // silently driving input at a stale pixel.
        if (expected is null) return null;

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

        for (var radius = options.SpiralSearchStep;
             radius <= options.SpiralSearchRadius;
             radius += options.SpiralSearchStep)
        {
            foreach (var (dx, dy) in SquareRingOffsets(radius, options.SpiralSearchStep))
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
                        Source = "accessibility-spiral",
                        Confidence = 0.85,
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
        if (!StringMatches(snap.Role, expected.Role)) return false;
        if (!StringMatches(snap.Name, expected.Name)) return false;
        if (!StringMatches(snap.Text, expected.Text)) return false;
        if (!StringMatches(snap.ElementType, expected.ElementType)) return false;
        return true;
    }

    private static bool StringMatches(string? actual, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        return string.Equals(actual ?? "", expected, StringComparison.Ordinal);
    }

    // Yields every (dx, dy) on the square ring at max(|dx|, |dy|) == radius,
    // stepping by `step`. Not a true spiral — concentric square rings — but
    // far denser than the previous 8-corner sample (~3R/step cells per ring),
    // so a small layout nudge that misses the centre point still gets probed.
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
