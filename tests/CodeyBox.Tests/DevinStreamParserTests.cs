using System.Text.Json;
using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins <see cref="DevinStreamParser"/>'s claim discipline: devin print mode
/// emits plaintext only (no stream-json exists in devin 3000.11.1), so the
/// parser must never claim a shape — mis-tagging another agent's NDJSON would
/// corrupt stream attribution.
/// </summary>
public sealed class DevinStreamParserTests
{
    [Fact]
    public void Kind_IsDevin()
        => Assert.Equal(AgentKind.Devin, new DevinStreamParser().Kind);

    [Theory]
    [InlineData("""{"type":"assistant","message":"hi"}""")]
    [InlineData("""{"type":"result","result":"done"}""")]
    [InlineData("""{}""")]
    public void TryClaim_AnyJsonShape_NeverClaimed(string line)
    {
        using var doc = JsonDocument.Parse(line);
        Assert.False(new DevinStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_OnlyOwnKind()
    {
        var parser = new DevinStreamParser();
        Assert.True(parser.CanEmitShapeOf(AgentKind.Devin));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Cursor));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Claude));
    }
}
