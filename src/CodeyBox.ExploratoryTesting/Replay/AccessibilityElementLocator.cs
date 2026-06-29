using CodeyBox.Core;
using System.Text.Json;

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
                    return new LocatedTarget
                    {
                        CenterX = px,
                        CenterY = py,
                        Region = region,
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

        try
        {
            using var doc = JsonDocument.Parse(json);
            var candidates = new List<LocatedTarget>();
            SearchTree(doc.RootElement, expected, visual, candidates, ct);
            return SelectTreeCandidate(candidates, visual);
        }
        catch (JsonException)
        {
            return TreeLocateResult.None;
        }
    }

    private void SearchTree(
        JsonElement element,
        TraceAccessibilityDescriptor expected,
        TraceVisualDescriptor visual,
        List<LocatedTarget> candidates,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var snap = SnapshotFromObject(element);
            if (_matcher.Matches(snap, expected) && TryReadBounds(element, out var region))
            {
                var (cx, cy) = RecordedClickPointForCurrentBounds(visual, region);
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

            foreach (var property in element.EnumerateObject())
            {
                SearchTree(property.Value, expected, visual, candidates, ct);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                SearchTree(item, expected, visual, candidates, ct);
            }
        }
    }

    private static TreeLocateResult SelectTreeCandidate(
        IReadOnlyList<LocatedTarget> candidates,
        TraceVisualDescriptor visual)
    {
        if (candidates.Count == 0) return TreeLocateResult.None;
        if (candidates.Count == 1) return TreeLocateResult.Found(candidates[0]);
        if (visual.Region.Width <= 0 || visual.Region.Height <= 0)
            return TreeLocateResult.Ambiguous;

        var (targetX, targetY) = RecordedClickPoint(visual);
        LocatedTarget? best = null;
        var bestScore = long.MaxValue;
        var ambiguous = false;
        foreach (var candidate in candidates)
        {
            var score = DistanceSquared(candidate.CenterX, candidate.CenterY, targetX, targetY);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
                ambiguous = false;
            }
            else if (score == bestScore)
            {
                ambiguous = true;
            }
        }

        return best is not null && !ambiguous
            ? TreeLocateResult.Found(best with { Source = "accessibility-tree-disambiguated" })
            : TreeLocateResult.Ambiguous;
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
        TraceBoundingRegion currentBounds)
    {
        var x = visual.ClickOffsetX is int offsetX && offsetX >= 0 && offsetX < currentBounds.Width
            ? currentBounds.X + offsetX
            : currentBounds.X + currentBounds.Width / 2;
        var y = visual.ClickOffsetY is int offsetY && offsetY >= 0 && offsetY < currentBounds.Height
            ? currentBounds.Y + offsetY
            : currentBounds.Y + currentBounds.Height / 2;
        return (x, y);
    }

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        var dx = (long)x1 - x2;
        var dy = (long)y1 - y2;
        return dx * dx + dy * dy;
    }

    private static SandboxAccessibilitySnapshot SnapshotFromObject(JsonElement obj) => new()
    {
        Role = ReadString(obj, "role", "Role", "controlType", "type"),
        Name = ReadString(obj, "name", "Name", "label", "title", "accessibleName"),
        Text = ReadString(obj, "text", "Text", "value", "description"),
        ElementType = ReadString(obj, "elementType", "ElementType", "tagName", "className"),
    };

    private static bool TryReadBounds(JsonElement obj, out TraceBoundingRegion region)
    {
        if (TryReadRectObject(obj, out region)) return true;

        foreach (var name in new[] { "bounds", "Bounds", "rect", "Rect", "boundingBox", "BoundingBox" })
        {
            if (!TryGetProperty(obj, name, out var child)) continue;
            if (child.ValueKind == JsonValueKind.Object && TryReadRectObject(child, out region)) return true;
            if (child.ValueKind == JsonValueKind.Array && TryReadRectArray(child, out region)) return true;
        }

        region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        return false;
    }

    private static bool TryReadRectObject(JsonElement obj, out TraceBoundingRegion region)
    {
        if (TryReadInt(obj, "x", out var x)
            && TryReadInt(obj, "y", out var y)
            && TryReadInt(obj, "width", out var width)
            && TryReadInt(obj, "height", out var height)
            && width > 0
            && height > 0)
        {
            region = new TraceBoundingRegion { X = x, Y = y, Width = width, Height = height };
            return true;
        }

        if (TryReadInt(obj, "left", out var left)
            && TryReadInt(obj, "top", out var top)
            && TryReadInt(obj, "right", out var right)
            && TryReadInt(obj, "bottom", out var bottom)
            && right > left
            && bottom > top)
        {
            region = new TraceBoundingRegion
            {
                X = left,
                Y = top,
                Width = right - left,
                Height = bottom - top,
            };
            return true;
        }

        region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        return false;
    }

    private static bool TryReadRectArray(JsonElement array, out TraceBoundingRegion region)
    {
        if (array.GetArrayLength() >= 4)
        {
            var values = new int[4];
            var i = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (i >= 4) break;
                if (!TryReadInt(item, out values[i]))
                {
                    region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
                    return false;
                }
                i++;
            }

            if (values[2] > 0 && values[3] > 0)
            {
                region = new TraceBoundingRegion
                {
                    X = values[0],
                    Y = values[1],
                    Width = values[2],
                    Height = values[3],
                };
                return true;
            }
        }

        region = new TraceBoundingRegion { X = 0, Y = 0, Width = 0, Height = 0 };
        return false;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static bool TryReadInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return TryGetProperty(obj, name, out var property) && TryReadInt(property, out value);
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
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
