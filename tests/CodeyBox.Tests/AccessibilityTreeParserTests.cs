using CodeyBox.Core;
using CodeyBox.ExploratoryTesting;

namespace CodeyBox.Tests;

/// <summary>
/// Direct unit coverage for <see cref="AccessibilityTreeParser"/> — the JSON
/// parser that turns an untrusted accessibility-tree snapshot into typed nodes
/// for the recorder's best-effort descriptor capture. Exercises the
/// property-name variants, the three bounds encodings, focused-node discovery,
/// and the top-most / deepest-node hit-testing rules directly rather than only
/// through the recorder integration path.
/// </summary>
public sealed class AccessibilityTreeParserTests
{
    [Fact]
    public void TryParseNodes_ReturnsFalse_OnNullEmptyOrMalformedJson()
    {
        Assert.False(AccessibilityTreeParser.TryParseNodes(null, out _));
        Assert.False(AccessibilityTreeParser.TryParseNodes("   ", out _));
        Assert.False(AccessibilityTreeParser.TryParseNodes("{ not valid json", out _));
    }

    [Fact]
    public void TryParseNodes_ReadsCaseVariantPropertyNames()
    {
        // controlType/label/value/tagName are accepted aliases for
        // role/name/text/elementType. Array bounds are used so the object has
        // no nested sub-object the tolerant traversal would surface as an
        // extra (phantom) node — this test pins the alias mapping, not
        // traversal shape.
        const string json = """
        { "controlType": "button", "label": "Save", "value": "unsaved", "tagName": "BUTTON",
          "rect": [ 0, 0, 10, 10 ] }
        """;

        Assert.True(AccessibilityTreeParser.TryParseNodes(json, out var nodes));
        var node = Assert.Single(nodes);
        Assert.Equal("button", node.Descriptor.Role);
        Assert.Equal("Save", node.Descriptor.Name);
        Assert.Equal("unsaved", node.Descriptor.Text);
        Assert.Equal("BUTTON", node.Descriptor.ElementType);
    }

    [Theory]
    [InlineData("\"focused\": true")]
    [InlineData("\"isFocused\": true")]
    [InlineData("\"hasFocus\": \"true\"")]
    public void TryFindFocusedNode_FindsFocusFlagVariants(string focusProperty)
    {
        var json = $$"""
        { "role": "root", "children": [
            { "role": "textbox", "name": "Email", {{focusProperty}} }
        ] }
        """;

        Assert.True(AccessibilityTreeParser.TryFindFocusedNode(json, out var node));
        Assert.Equal("textbox", node.Descriptor.Role);
        Assert.Equal("Email", node.Descriptor.Name);
    }

    [Fact]
    public void TryFindFocusedNode_ReturnsFalse_WhenNoNodeFocused()
    {
        const string json = """{ "role": "root", "children": [ { "role": "textbox" } ] }""";
        Assert.False(AccessibilityTreeParser.TryFindFocusedNode(json, out _));
    }

    [Fact]
    public void TryFindNodeAtPoint_ReturnsDeepestContainingNode_WhenTopMostNull()
    {
        // Both the document root and the inner button contain (20,15); the
        // parser must return the innermost (smallest-area) node, not the
        // shallowest ancestor that DFS happens to emit first.
        const string json = """
        { "role": "document", "name": "root",
          "bounds": { "x": 0, "y": 0, "width": 1000, "height": 1000 },
          "children": [
            { "role": "button", "name": "OK",
              "bounds": { "x": 10, "y": 10, "width": 50, "height": 20 } }
          ] }
        """;

        Assert.True(AccessibilityTreeParser.TryFindNodeAtPoint(json, 20, 15, topMost: null, out var node));
        Assert.Equal("button", node.Descriptor.Role);
        Assert.Equal("OK", node.Descriptor.Name);
    }

    [Fact]
    public void TryFindNodeAtPoint_ReturnsFalse_WhenPointOutsideAllBounds()
    {
        const string json = """
        { "role": "button", "bounds": { "x": 0, "y": 0, "width": 10, "height": 10 } }
        """;
        Assert.False(AccessibilityTreeParser.TryFindNodeAtPoint(json, 500, 500, topMost: null, out _));
    }

    [Fact]
    public void TryFindNodeAtPoint_ReturnsTopMostMatch_WhenSnapshotEquivalent()
    {
        const string json = """
        { "role": "document", "name": "root",
          "bounds": { "x": 0, "y": 0, "width": 1000, "height": 1000 },
          "children": [
            { "role": "button", "name": "OK",
              "bounds": { "x": 10, "y": 10, "width": 50, "height": 20 } }
          ] }
        """;
        var topMost = new SandboxAccessibilitySnapshot { Role = "button", Name = "OK" };

        Assert.True(AccessibilityTreeParser.TryFindNodeAtPoint(json, 20, 15, topMost, out var node));
        Assert.Equal("button", node.Descriptor.Role);
        Assert.Equal("OK", node.Descriptor.Name);
    }

    [Fact]
    public void TryFindNodeAtPoint_FailsClosed_OnAllNullTopMost()
    {
        // Regression guard: an all-null top-most snapshot carries no identifying
        // signal and MUST NOT rubber-stamp the first containing node. With a
        // non-null-but-empty topMost the parser reports "not found" rather than
        // a false-positive top-most match.
        const string json = """
        { "role": "document", "name": "root",
          "bounds": { "x": 0, "y": 0, "width": 1000, "height": 1000 },
          "children": [
            { "role": "button", "name": "OK",
              "bounds": { "x": 10, "y": 10, "width": 50, "height": 20 } }
          ] }
        """;
        var allNull = new SandboxAccessibilitySnapshot();

        Assert.False(AccessibilityTreeParser.TryFindNodeAtPoint(json, 20, 15, allNull, out _));
    }

    [Fact]
    public void TryFindNodeAtPoint_ParsesLeftTopRightBottomBounds()
    {
        const string json = """
        { "role": "button", "name": "Edit",
          "bounds": { "left": 10, "top": 10, "right": 60, "bottom": 30 } }
        """;

        Assert.True(AccessibilityTreeParser.TryFindNodeAtPoint(json, 30, 20, topMost: null, out var node));
        Assert.Equal("Edit", node.Descriptor.Name);
        Assert.Equal(50, node.Bounds!.Width);
        Assert.Equal(20, node.Bounds!.Height);
    }

    [Fact]
    public void TryFindNodeAtPoint_ParsesArrayBounds()
    {
        const string json = """
        { "role": "button", "name": "Arr", "rect": [ 10, 20, 30, 40 ] }
        """;

        Assert.True(AccessibilityTreeParser.TryFindNodeAtPoint(json, 15, 25, topMost: null, out var node));
        Assert.Equal("Arr", node.Descriptor.Name);
        Assert.Equal(10, node.Bounds!.X);
        Assert.Equal(30, node.Bounds!.Width);
    }

    [Fact]
    public void TryFindNodeAtPoint_RejectsShortArrayBounds()
    {
        // A 3-element array is not a valid [x,y,w,h] rect — the node parses with
        // no bounds and therefore contains no point.
        const string json = """
        { "role": "button", "name": "Short", "bounds": [ 10, 20, 30 ] }
        """;
        Assert.False(AccessibilityTreeParser.TryFindNodeAtPoint(json, 15, 25, topMost: null, out _));
    }

    [Fact]
    public void TryFindNodeAtPoint_RejectsNonPositiveDimensionBounds()
    {
        // width 0 is not a usable rect; the node has no bounds and the point
        // matches nothing.
        const string json = """
        { "role": "button", "bounds": { "x": 0, "y": 0, "width": 0, "height": 10 } }
        """;
        Assert.False(AccessibilityTreeParser.TryFindNodeAtPoint(json, 0, 5, topMost: null, out _));
    }
}
