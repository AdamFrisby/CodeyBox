using CodeyBox.Core;

namespace CodeyBox.ExploratoryTesting.Replay;

/// <summary>
/// Default <see cref="IElementLocator"/>. Recognition strategy:
///
/// <list type="number">
///   <item>If the descriptor carries at least one non-empty accessibility
///   field (role, name, text, or element-type), search the current
///   accessibility tree for a matching node with bounds. On a match, return a
///   high-confidence hit at the node's current centre.</item>
///   <item>If the tree has no usable bounded match, probe at the recorded
///   centre with <see cref="ISandbox.GetAccessibilityAtPointAsync"/>. On a
///   match, return a hit at that point.</item>
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
/// <para>The point/ring probes are compatibility fallbacks for providers that
/// do not yet expose tree bounds. Tree recognition is the primary path so a
/// target that moved elsewhere on the current screen is found by identity, not
/// by stale coordinates.</para>
/// </summary>
public sealed class AccessibilityElementLocator : IElementLocator
{
    // Ring-hit confidence: lower than the centre-hit's 1.0 because nearby
    // matches reflect a small layout nudge, not the exact recorded geometry.
    // Surfaced for diagnostics only — the engine does not gate on confidence.
    internal const double RingHitConfidence = 0.85;

    private readonly IAccessibilityMatcher _matcher;

    public AccessibilityElementLocator(IAccessibilityMatcher? matcher = null)
    {
        _matcher = matcher ?? DefaultAccessibilityMatcher.Instance;
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

        var expected = descriptor.Accessibility;
        // Strict policy: with no usable accessibility signature, leave it for
        // the chained non-accessibility locator (e.g.
        // VisualSignatureElementLocator) to handle. An all-null descriptor
        // would silently match any element returned at the recorded centre
        // (StringMatches treats null/empty as 'matches anything'), which is a
        // false-positive failure class the brief explicitly calls out.
        if (expected is null) return null;
        if (!_matcher.HasAnyAccessibilitySignal(expected)) return null;

        var region = descriptor.Visual.Region;
        var hasPoint = region.Width > 0 && region.Height > 0;
        var treeHit = await LocateFromAccessibilityTreeAsync(sandbox, expected, descriptor.Visual, ct)
            .ConfigureAwait(false);
        if (treeHit.Status == TreeLocateStatus.Found) return treeHit.Target;
        if (treeHit.Status == TreeLocateStatus.Ambiguous) return null;

        if (!hasPoint) return null;

        var (cx, cy) = RecordedClickPoint(descriptor.Visual);

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
                Evidence = LocatedTargetEvidence.Accessibility,
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
                var match = await ProbeAccessibilityAsync(sandbox, px, py, expected, ct).ConfigureAwait(false);
                if (match is not null)
                {
                    var shiftedRegion = ShiftRegionToPoint(region, cx, cy, px, py);
                    return new LocatedTarget
                    {
                        CenterX = px,
                        CenterY = py,
                        Region = shiftedRegion,
                        Source = "accessibility-ring",
                        Confidence = RingHitConfidence,
                        Evidence = LocatedTargetEvidence.Accessibility,
                    };
                }
            }
        }

        return null;
    }

    private async Task<SandboxAccessibilitySnapshot?> ProbeAccessibilityAsync(
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
        return _matcher.Matches(snap, expected) ? snap : null;
    }

    private async Task<TreeLocateResult> LocateFromAccessibilityTreeAsync(
        ISandbox sandbox,
        TraceAccessibilityDescriptor expected,
        TraceVisualDescriptor visual,
        CancellationToken ct)
    {
        string? json;
        try
        {
            json = await sandbox.GetAccessibilityTreeJsonAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return TreeLocateResult.None;
        }

        if (string.IsNullOrWhiteSpace(json)) return TreeLocateResult.None;

        if (!AccessibilityTreeParser.TryParseNodes(json, ct, out var nodes))
            return TreeLocateResult.None;

        var candidates = new List<LocatedTarget>();
        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            if (node.Bounds is not { } region || !_matcher.Matches(node.Snapshot, expected))
                continue;

            var (cx, cy) = RecordedClickPointForCurrentBounds(visual, region, expected.Bounds);
            candidates.Add(new LocatedTarget
            {
                CenterX = cx,
                CenterY = cy,
                Region = region,
                Source = "accessibility-tree",
                Confidence = 1.0,
                Evidence = LocatedTargetEvidence.Accessibility,
            });
        }

        return SelectTreeCandidate(candidates);
    }

    private static TreeLocateResult SelectTreeCandidate(IReadOnlyList<LocatedTarget> candidates)
    {
        if (candidates.Count == 0) return TreeLocateResult.None;
        if (candidates.Count == 1) return TreeLocateResult.Found(candidates[0]);
        return TreeLocateResult.Ambiguous;
    }

    private static (int X, int Y) RecordedClickPoint(TraceVisualDescriptor visual)
    {
        var region = visual.Region;
        var x = visual.ClickOffsetX is int offsetX && offsetX >= 0 && offsetX < region.Width
            ? region.X + offsetX
            : region.X + region.Width / 2;
        var y = visual.ClickOffsetY is int offsetY && offsetY >= 0 && offsetY < region.Height
            ? region.Y + offsetY
            : region.Y + region.Height / 2;
        return (x, y);
    }

    private static TraceBoundingRegion ShiftRegionToPoint(
        TraceBoundingRegion region,
        int oldCenterX,
        int oldCenterY,
        int newCenterX,
        int newCenterY)
        => new()
        {
            X = region.X + newCenterX - oldCenterX,
            Y = region.Y + newCenterY - oldCenterY,
            Width = region.Width,
            Height = region.Height,
        };

    private static (int X, int Y) RecordedClickPointForCurrentBounds(
        TraceVisualDescriptor visual,
        TraceBoundingRegion currentBounds,
        TraceBoundingRegion? recordedAccessibilityBounds)
    {
        var recordedClick = RecordedClickPoint(visual);
        if (recordedAccessibilityBounds is { Width: > 0, Height: > 0 }
            && Contains(recordedAccessibilityBounds, recordedClick.X, recordedClick.Y))
        {
            return (
                currentBounds.X + Clamp(recordedClick.X - recordedAccessibilityBounds.X, 0, currentBounds.Width - 1),
                currentBounds.Y + Clamp(recordedClick.Y - recordedAccessibilityBounds.Y, 0, currentBounds.Height - 1));
        }

        if (Contains(currentBounds, recordedClick.X, recordedClick.Y))
            return recordedClick;

        return (currentBounds.X + currentBounds.Width / 2, currentBounds.Y + currentBounds.Height / 2);
    }

    private static bool Contains(TraceBoundingRegion region, int x, int y)
        => x >= region.X
            && x < region.X + region.Width
            && y >= region.Y
            && y < region.Y + region.Height;

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

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

    private enum TreeLocateStatus
    {
        None,
        Found,
        Ambiguous,
    }

    private sealed record TreeLocateResult(TreeLocateStatus Status, LocatedTarget? Target)
    {
        public static TreeLocateResult None { get; } = new(TreeLocateStatus.None, null);
        public static TreeLocateResult Ambiguous { get; } = new(TreeLocateStatus.Ambiguous, null);
        public static TreeLocateResult Found(LocatedTarget target) => new(TreeLocateStatus.Found, target);
    }
}
