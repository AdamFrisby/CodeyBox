using CodeyBox.Core;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CodeyBox.ExploratoryTesting;

internal static class AccessibilityTreeParser
{
    private static readonly string[] RoleNames = ["role", "Role", "controlType", "type"];
    private static readonly string[] NameNames = ["name", "Name", "label", "title", "accessibleName"];
    private static readonly string[] TextNames = ["text", "Text", "value", "description"];
    private static readonly string[] ElementTypeNames = ["elementType", "ElementType", "tagName", "className"];
    private static readonly string[] BoundsNames = ["bounds", "Bounds", "rect", "Rect", "boundingBox", "BoundingBox"];
    private static readonly string[] FocusNames = ["focused", "Focused", "hasFocus", "HasFocus", "has_focus", "isFocused", "is_focused"];

    public static bool TryParseNodes(string? json, out IReadOnlyList<ParsedAccessibilityNode> nodes)
        => TryParseNodes(json, CancellationToken.None, out nodes);

    public static bool TryParseNodes(
        string? json,
        CancellationToken ct,
        out IReadOnlyList<ParsedAccessibilityNode> nodes)
    {
        nodes = [];
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var parsed = new List<ParsedAccessibilityNode>();
            Search(doc.RootElement, parsed, ct);
            nodes = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryFindFocusedNode(string? json, [NotNullWhen(true)] out ParsedAccessibilityNode? node)
    {
        if (TryParseNodes(json, out var nodes))
        {
            foreach (var candidate in nodes)
            {
                if (!candidate.IsFocused) continue;
                node = candidate;
                return true;
            }
        }

        node = null;
        return false;
    }

    public static bool TryFindNodeAtPoint(
        string? json,
        int x,
        int y,
        SandboxAccessibilitySnapshot? topMost,
        [NotNullWhen(true)] out ParsedAccessibilityNode? node)
    {
        if (!TryParseNodes(json, out var nodes))
        {
            node = null;
            return false;
        }

        ParsedAccessibilityNode? firstContaining = null;
        foreach (var candidate in nodes)
        {
            if (candidate.Bounds is not { } bounds || !Contains(bounds, x, y))
                continue;

            firstContaining ??= candidate;
            if (topMost is not null && SnapshotEquivalent(candidate.Snapshot, topMost))
            {
                node = candidate;
                return true;
            }
        }

        if (topMost is null && firstContaining is not null)
        {
            node = firstContaining;
            return true;
        }

        node = null;
        return false;
    }

    private static void Search(
        JsonElement element,
        List<ParsedAccessibilityNode> nodes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var descriptor = DescriptorFromObject(element);
            var snapshot = new SandboxAccessibilitySnapshot
            {
                Role = descriptor.Role,
                Name = descriptor.Name,
                Text = descriptor.Text,
                ElementType = descriptor.ElementType,
            };
            var bounds = TryReadBounds(element, out var region) ? region : null;
            if (bounds is not null)
                descriptor = descriptor with { Bounds = bounds };

            nodes.Add(new ParsedAccessibilityNode(descriptor, snapshot, bounds, IsFocusedNode(element)));

            foreach (var property in element.EnumerateObject())
            {
                Search(property.Value, nodes, ct);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Search(item, nodes, ct);
            }
        }
    }

    private static TraceAccessibilityDescriptor DescriptorFromObject(JsonElement obj) => new()
    {
        Role = ReadString(obj, RoleNames),
        Name = ReadString(obj, NameNames),
        Text = ReadString(obj, TextNames),
        ElementType = ReadString(obj, ElementTypeNames),
    };

    private static bool IsFocusedNode(JsonElement node)
    {
        foreach (var name in FocusNames)
        {
            if (!TryGetProperty(node, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.True) return true;
            if (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out var parsed)
                && parsed)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBounds(JsonElement obj, out TraceBoundingRegion region)
    {
        if (TryReadRectObject(obj, out region)) return true;

        foreach (var name in BoundsNames)
        {
            if (!TryGetProperty(obj, name, out var child)) continue;
            if (child.ValueKind == JsonValueKind.Object && TryReadRectObject(child, out region)) return true;
            if (child.ValueKind == JsonValueKind.Array && TryReadRectArray(child, out region)) return true;
        }

        region = ZeroRegion();
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

        region = ZeroRegion();
        return false;
    }

    private static bool TryReadRectArray(JsonElement array, out TraceBoundingRegion region)
    {
        if (array.GetArrayLength() < 4)
        {
            region = ZeroRegion();
            return false;
        }

        var values = new int[4];
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (i >= 4) break;
            if (!TryReadInt(item, out values[i]))
            {
                region = ZeroRegion();
                return false;
            }
            i++;
        }

        if (values[2] <= 0 || values[3] <= 0)
        {
            region = ZeroRegion();
            return false;
        }

        region = new TraceBoundingRegion { X = values[0], Y = values[1], Width = values[2], Height = values[3] };
        return true;
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
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value))
            return true;

        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool Contains(TraceBoundingRegion region, int x, int y)
        => x >= region.X
            && x < region.X + region.Width
            && y >= region.Y
            && y < region.Y + region.Height;

    private static bool SnapshotEquivalent(SandboxAccessibilitySnapshot candidate, SandboxAccessibilitySnapshot topMost)
        => Same(candidate.Role, topMost.Role)
            && Same(candidate.Name, topMost.Name)
            && Same(candidate.Text, topMost.Text)
            && Same(candidate.ElementType, topMost.ElementType);

    private static bool Same(string? left, string? right)
        => string.IsNullOrEmpty(right) || string.Equals(left ?? "", right, StringComparison.Ordinal);

    private static TraceBoundingRegion ZeroRegion()
        => new() { X = 0, Y = 0, Width = 0, Height = 0 };
}

internal sealed record ParsedAccessibilityNode(
    TraceAccessibilityDescriptor Descriptor,
    SandboxAccessibilitySnapshot Snapshot,
    TraceBoundingRegion? Bounds,
    bool IsFocused);
