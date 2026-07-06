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
///   <item>If the tree has no usable bounded match, return null. Point probes
///   can verify the top-most object at a coordinate, but they do not provide
///   current bounds and therefore cannot relocate a target without trusting
///   recorded coordinates.</item>
///   <item>If the descriptor has no accessibility signal (or all
///   accessibility fields are null/empty), return null. Non-accessibility
///   recognition is the responsibility of a sibling locator chained behind
///   this one via <see cref="CompositeElementLocator"/> (the default engine
///   wiring includes <see cref="VisualSignatureElementLocator"/>).</item>
/// </list>
/// </summary>
public sealed class AccessibilityElementLocator : IElementLocator
{
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

        var treeHit = await LocateFromAccessibilityTreeAsync(sandbox, expected, descriptor.Visual, ct)
            .ConfigureAwait(false);
        if (treeHit.Status == TreeLocateStatus.Found) return treeHit.Target;
        return null;
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
