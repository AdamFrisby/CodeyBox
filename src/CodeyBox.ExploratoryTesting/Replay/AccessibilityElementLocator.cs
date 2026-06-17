using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IElementLocator"/>. Recognition strategy:
///
/// <list type="number">
///   <item>If the descriptor has accessibility role/name, probe at the recorded
///   centre with <see cref="ISandbox.GetAccessibilityAtPointAsync"/>. On a
///   role/name match, return a high-confidence hit at the recorded centre.</item>
///   <item>If the point probe misses, spiral outward in
///   <see cref="ReplayOptions.SpiralSearchStep"/>-pixel steps up to
///   <see cref="ReplayOptions.SpiralSearchRadius"/>, returning the first
///   accessibility match.</item>
///   <item>If the descriptor has no accessibility signal OR all spiral
///   probes miss, fall back to the recorded region — but only when the
///   region carries a real (non-zero) size. The reachability check still
///   verifies the fallback is actually on screen and not occluded, so a
///   genuine regression still fails.</item>
/// </list>
///
/// <para>The recorded centre is the central anchor for both the point probe
/// and the fallback; it survives small viewport shifts but not full layout
/// re-flows. A future vision-assisted heal seam (see
/// <see cref="ILocatorHealer"/>) will close that gap; this locator
/// deliberately fails fast on a layout regression rather than silently
/// rewriting it.</para>
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

        var region = descriptor.Visual.Region;
        var hasPoint = region.Width > 0 && region.Height > 0;
        var cx = hasPoint ? region.X + region.Width / 2 : 0;
        var cy = hasPoint ? region.Y + region.Height / 2 : 0;

        var expected = descriptor.Accessibility;

        if (hasPoint && expected is not null)
        {
            var hit = await ProbeAccessibilityAsync(sandbox, cx, cy, expected, ct).ConfigureAwait(false);
            if (hit is not null)
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
                foreach (var (dx, dy) in SpiralOffsets(radius))
                {
                    ct.ThrowIfCancellationRequested();
                    var px = cx + dx;
                    var py = cy + dy;
                    if (px < 0 || py < 0) continue;
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
        }

        // Strict policy: when the recorder captured an accessibility signature
        // and no probe in the spiral matched, the target has genuinely regressed.
        // Falling back to the raw recorded region here would mask the bug — the
        // reachability check would then either pass against a wrong element or
        // surface a confusing "occluded" diagnostic. Surface NotFound instead so
        // the engine fails deterministically per the brief.
        if (expected is not null) return null;

        if (hasPoint)
        {
            return new LocatedTarget
            {
                CenterX = cx,
                CenterY = cy,
                Region = region,
                Source = "recorded-region",
                Confidence = 0.5,
            };
        }

        return null;
    }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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

    private static IEnumerable<(int Dx, int Dy)> SpiralOffsets(int radius)
    {
        for (var dy = -radius; dy <= radius; dy += radius)
        {
            for (var dx = -radius; dx <= radius; dx += radius)
            {
                if (dx == 0 && dy == 0) continue;
                yield return (dx, dy);
            }
        }
    }
}
