using System.Text.Json;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Resolves a stable CSS selector from accessibility signals captured during
/// computer-use exploration. Prefers explicit selector fields embedded in the
/// accessibility tree, then id / data-testid attributes, then role+name pairs.
/// </summary>
public static class E2eSelectorResolver
{
    public static string? Resolve(
        TraceAccessibilityDescriptor? descriptor,
        string? accessibilityTreeJson)
    {
        if (descriptor is null)
            return null;

        var nodes = ParseNodes(accessibilityTreeJson);
        if (nodes.Count > 0)
        {
            var match = FindBestMatch(nodes, descriptor);
            if (match is not null)
                return match;
        }

        if (!string.IsNullOrWhiteSpace(descriptor.ElementType)
            && descriptor.ElementType.StartsWith("css:", StringComparison.Ordinal))
            return descriptor.ElementType["css:".Length..];

        if (!string.IsNullOrWhiteSpace(descriptor.Name) && !string.IsNullOrWhiteSpace(descriptor.Role))
            return $"{descriptor.Role}[name=\"{EscapeCssValue(descriptor.Name)}\"]";

        return null;
    }

    private static string? FindBestMatch(IReadOnlyList<AccessibilityNode> nodes, TraceAccessibilityDescriptor descriptor)
    {
        AccessibilityNode? exact = null;
        AccessibilityNode? roleMatch = null;
        foreach (var node in nodes)
        {
            var roleMatches = RolesEqual(node.Role, descriptor.Role);
            var nameMatches = NamesEqual(node.Name, descriptor.Name);
            if (roleMatches && nameMatches)
                exact = node;
            else if (roleMatches && roleMatch is null)
                roleMatch = node;
        }

        var chosen = exact ?? roleMatch;
        if (chosen is null)
            return null;

        if (!string.IsNullOrWhiteSpace(chosen.Selector))
            return chosen.Selector;
        if (!string.IsNullOrWhiteSpace(chosen.Id))
            return $"#{chosen.Id}";
        if (!string.IsNullOrWhiteSpace(chosen.TestId))
            return $"[data-testid=\"{EscapeCssValue(chosen.TestId)}\"]";

        return null;
    }

    private static bool RolesEqual(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static bool NamesEqual(string? left, string? right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeCssValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static IReadOnlyList<AccessibilityNode> ParseNodes(string? accessibilityTreeJson)
    {
        if (string.IsNullOrWhiteSpace(accessibilityTreeJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(accessibilityTreeJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            if (!doc.RootElement.TryGetProperty("nodes", out var nodesElement)
                || nodesElement.ValueKind != JsonValueKind.Array)
                return [];

            var nodes = new List<AccessibilityNode>();
            foreach (var node in nodesElement.EnumerateArray())
            {
                nodes.Add(new AccessibilityNode(
                    Role: ReadString(node, "role"),
                    Name: ReadString(node, "name"),
                    Selector: ReadString(node, "selector"),
                    Id: ReadString(node, "id"),
                    TestId: ReadString(node, "testId", "data-testid", "dataTestId")));
            }

            return nodes;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private sealed record AccessibilityNode(
        string? Role,
        string? Name,
        string? Selector,
        string? Id,
        string? TestId);
}
