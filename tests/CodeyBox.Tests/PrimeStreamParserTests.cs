using System.Text.Json;
using CodeyBox.Agents.Prime;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PrimeStreamParser"/>: the no-claim policy over the
/// pi-shared wire shape, the <c>CanEmitShapeOf</c> compatibility matrix, and
/// usage mapping over RECORDED prime-agent 0.9.5 output (a real OpenRouter
/// run reporting <c>usage {input:1185, output:104, cacheRead:4352}</c>, a
/// <c>stopReason:"error"</c> failure run, and an empty response).
/// </summary>
public sealed class PrimeStreamParserTests
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

    // Recorded prime-agent 0.9.5 session header (rlmDepth is prime's additive
    // envelope detail on the otherwise pi-identical header).
    private const string RecordedSessionHeader =
        "{\"type\":\"session\",\"version\":3,\"id\":\"01a0aab7-edf7-7559-b8ef-b86a752e0c99\"," +
        "\"timestamp\":\"2026-09-16T14:56:15.863Z\",\"cwd\":\"/tmp/prime-probe/fresh\",\"rlmDepth\":0}";

    // Recorded assistant frame from the live OpenRouter success run.
    private const string RecordedSuccessFrame =
        "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"provider\":\"openrouter\"," +
        "\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
        "\"usage\":{\"input\":1185,\"output\":104,\"cacheRead\":4352,\"cacheWrite\":0,\"totalTokens\":5641," +
        "\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}," +
        "\"stopReason\":\"stop\"}}";

    // Recorded assistant frame from the live bad-key failure run.
    private const string RecordedErrorFrame =
        "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"provider\":\"openrouter\"," +
        "\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
        "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0}," +
        "\"stopReason\":\"error\",\"errorMessage\":\"401 User not found.\\n\\nRun /login to update credentials.\"}}";

    [Fact]
    public void TryClaim_PrimeSessionHeader_NotClaimed()
    {
        // Pi owns the shared shape: claiming here would steal real pi
        // streams depending on parser registration order.
        using var doc = JsonDocument.Parse(RecordedSessionHeader);

        Assert.False(new PrimeStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("agent_start")]
    [InlineData("agent_end")]
    [InlineData("turn_start")]
    [InlineData("turn_end")]
    [InlineData("message_start")]
    [InlineData("message_update")]
    [InlineData("message_end")]
    public void TryClaim_LifecycleVerbs_NotClaimed(string type)
    {
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.False(new PrimeStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("prime")]
    [InlineData("pi")]
    public void CanEmitShapeOf_PrimeAndPi_Compatible(string kind)
    {
        Assert.True(new PrimeStreamParser().CanEmitShapeOf(new AgentKind(kind)));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("aider")]
    [InlineData("cursor")]
    public void CanEmitShapeOf_ForeignKinds_NotCompatible(string kind)
    {
        Assert.False(new PrimeStreamParser().CanEmitShapeOf(new AgentKind(kind)));
    }

    [Fact]
    public async Task ParseAsync_RecordedSuccessRun_MapsUsageNames()
    {
        // The shared ParseUsage only knows input_tokens/prompt_tokens; prime
        // reports usage:{input,output,cacheRead} like pi.
        var parser = new PrimeStreamParser();
        await using var stream = StreamOf(
            RecordedSessionHeader + "\n" +
            RecordedSuccessFrame + "\n" +
            "{\"type\":\"agent_end\",\"messages\":[]}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(1185, summary.InputTokens);
        Assert.Equal(104, summary.OutputTokens);
        Assert.Equal(4352, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_RecordedFailureRun_YieldsNoUsage()
    {
        // Error runs report all-zero usage; the failure signal belongs to
        // the terminal diagnoser, not the stream summary.
        var parser = new PrimeStreamParser();
        await using var stream = StreamOf(
            RecordedSessionHeader + "\n" +
            RecordedErrorFrame + "\n" +
            "{\"type\":\"agent_end\",\"messages\":[]}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Empty(summary.ToolCalls);
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_YieldsEmptySummary()
    {
        // A run that produced no events at all (missing CLI, killed
        // process): zeros and no tool calls, never a fabricated reading.
        var parser = new PrimeStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Empty(summary.ToolCalls);
    }

    [Fact]
    public async Task ParseAsync_ToolExecutionEvents_DoNotCorruptSummary()
    {
        // tool_execution_start/end are prime/pi tool-call events the base
        // parser does not model (same as pi today): they must be ignored,
        // not corrupt the usage accounting.
        var parser = new PrimeStreamParser();
        await using var stream = StreamOf(
            RecordedSessionHeader + "\n" +
            "{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"ipython\"}\n" +
            RecordedSuccessFrame + "\n" +
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"call-1\",\"toolName\":\"ipython\",\"result\":{},\"isError\":false}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(1185, summary.InputTokens);
        Assert.Equal(104, summary.OutputTokens);
    }
}
