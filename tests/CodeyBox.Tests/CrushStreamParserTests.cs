using System.Text.Json;
using CodeyBox.Agents.Crush;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrushStreamParser"/> over recorded real
/// @charmland/crush 0.95.0 output: a plain-text reply, a repo-edit summary,
/// an empty reply (a run whose work landed in files), and a styled
/// <c>ERROR</c> failure block. The parser claims nothing by shape — the
/// failure text carries no Crush-unique marker — while still resolving
/// Crush streams to <see cref="AgentKind.Crush"/>; plaintext falls through
/// to the fallback summariser, and failures are lifted independently by
/// <see cref="CrushTerminalDiagnoser"/>.
/// </summary>
public sealed class CrushStreamParserTests
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
    public void Kind_IsCrush()
    {
        Assert.Equal(AgentKind.Crush, new CrushStreamParser().Kind);
    }

    [Theory]
    // Recorded real reply (@charmland/crush 0.95.0).
    [InlineData("{\"message\": \"hello-crush-ok\"}")]
    // Recorded real failure text (styled ERROR block): deliberately
    // unclaimed — fixed-width human rendering with no Crush-unique marker.
    [InlineData("{\"text\": \"Agent processing failed: forbidden: Key limit exceeded (total limit).\"}")]
    [InlineData("{\"foo\":\"bar\"}")]
    public void TryClaim_NeverClaims_ByShape(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.False(new CrushStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public async Task ParseAsync_PlainTextReply_ReturnsUnsupported()
    {
        var summary = await new CrushStreamParser().ParseAsync(StreamOf("hello-crush-ok\n"));

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_RepoEditSummary_ReturnsUnsupported()
    {
        // Recorded real output (seeded-bug repo-edit run): the summary is
        // plain text, not a structured frame.
        var summary = await new CrushStreamParser().ParseAsync(StreamOf(
            "Fixed. The `add` function in `/tmp/crushrepo/calc.py:1` now returns `a + b` instead of `a - b`.\n"));

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_EmptyReply_ReturnsUnsupported()
    {
        // A run whose work landed in files may reply with empty stdout.
        // Empty output still routes through the plaintext fallback, not a
        // parse failure.
        var summary = await new CrushStreamParser().ParseAsync(StreamOf(string.Empty));

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_FailureBlock_ReturnsUnsupported()
    {
        // The failure text is lifted by the terminal diagnoser, not the
        // stream parser — parsing stays honest about the unstructured shape.
        const string failure =
            "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).\n";

        var summary = await new CrushStreamParser().ParseAsync(StreamOf(failure));

        Assert.True(summary.IsUnsupported);
    }
}
