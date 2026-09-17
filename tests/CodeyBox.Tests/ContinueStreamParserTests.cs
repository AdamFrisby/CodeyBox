using System.Text.Json;
using CodeyBox.Agents.Continue;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueStreamParser"/> over recorded real
/// @continuedev/cli 1.5.47 output: a plain-text reply, an empty reply (a
/// file-edit run whose work landed in files), and the exit-0 error
/// envelope. The parser claims nothing by shape — notably NOT the
/// <c>{"status":"error",…}</c> envelope, which carries no Continue-unique
/// marker — while still resolving Continue streams to
/// <see cref="AgentKind.Continue"/>; plaintext falls through to the
/// fallback summariser, and the failure envelope is lifted independently by
/// <see cref="ContinueTerminalDiagnoser"/>.
/// </summary>
public sealed class ContinueStreamParserTests
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
    public void Kind_IsContinue()
    {
        Assert.Equal(AgentKind.Continue, new ContinueStreamParser().Kind);
    }

    [Theory]
    // Recorded real reply (@continuedev/cli 1.5.47).
    [InlineData("{\"message\": \"HELLO-CN-JSON\"}")]
    // Recorded real failure envelope ($0-spend-limit key, paid model):
    // deliberately unclaimed — no Continue-unique marker.
    [InlineData("{\"status\":\"error\",\"message\":\"403 Key limit exceeded (total limit).\"}")]
    [InlineData("{\"foo\":\"bar\"}")]
    public void TryClaim_NeverClaims_ByShape(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.False(new ContinueStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public async Task ParseAsync_PlainTextReply_ReturnsUnsupported()
    {
        var summary = await new ContinueStreamParser().ParseAsync(StreamOf("HELLO-CN-OK\n"));

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_EmptyReply_ReturnsUnsupported()
    {
        // Recorded real behaviour: a file-edit run replied with empty stdout
        // (exit 0, change merged). Empty output still routes through the
        // plaintext fallback, not a parse failure.
        var summary = await new ContinueStreamParser().ParseAsync(StreamOf(string.Empty));

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_ErrorEnvelope_ReturnsUnsupported()
    {
        // The failure envelope is lifted by the terminal diagnoser, not the
        // stream parser — parsing stays honest about the unstructured shape.
        const string envelope =
            "{\"status\":\"error\",\"message\":\"403 Key limit exceeded (total limit).\"}\n";

        var summary = await new ContinueStreamParser().ParseAsync(StreamOf(envelope));

        Assert.True(summary.IsUnsupported);
    }
}
