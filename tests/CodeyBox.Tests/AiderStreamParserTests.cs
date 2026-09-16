using System.Text.Json;
using CodeyBox.Agents.Aider;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AiderStreamParser"/>: aider's one-shot output is
/// plaintext, so the parser claims nothing by shape (mirroring the opencode
/// slot) while still resolving aider streams to <see cref="AgentKind.Aider"/>.
/// </summary>
public sealed class AiderStreamParserTests
{
    private static Stream StreamOf(string text)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(text);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Kind_IsAider()
    {
        Assert.Equal(AgentKind.Aider, new AiderStreamParser().Kind);
    }

    [Theory]
    [InlineData("{\"type\":\"session\",\"version\":3}")]
    [InlineData("{\"type\":\"message_end\"}")]
    [InlineData("{\"foo\":\"bar\"}")]
    public void TryClaim_NeverClaims_ByShape(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.False(new AiderStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public async Task ParseAsync_PlaintextRun_ReturnsUnsupported()
    {
        // Recorded aider 0.86.2 one-shot shape: plaintext header, accounting
        // line, applied-edit line. Unsupported routes the file through the
        // plaintext-fallback summariser; the parser only needs to stay honest
        // about what it cannot parse.
        const string text =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "Tokens: 766 sent, 905 received.\n" +
            "Applied edit to pong2.txt\n";

        var summary = await new AiderStreamParser().ParseAsync(StreamOf(text));

        Assert.True(summary.IsUnsupported);
    }
}
