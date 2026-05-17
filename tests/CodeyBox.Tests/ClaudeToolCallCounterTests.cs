using CodeyBox.Agents.Claude;

namespace CodeyBox.Tests;

public sealed class ClaudeToolCallCounterTests
{
    private const string SampleStreamJson = """
        {"type":"system","subtype":"init","sessionId":"s1"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}],"stop_reason":"tool_use"}}
        {"type":"tool_result","tool_use_id":"t1","content":"file1.txt\nfile2.txt","is_error":false}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Edit","input":{"path":"/work/foo.cs","content":"..."}},{"type":"tool_use","id":"t3","name":"Bash","input":{"command":"dotnet build"}}],"stop_reason":"tool_use"}}
        {"type":"tool_result","tool_use_id":"t2","content":"done"}
        {"type":"tool_result","tool_use_id":"t3","content":"Build succeeded","is_error":false}
        {"type":"result","subtype":"success","result":"I have completed the task.","cost_usd":0.05}
        """;

    [Fact]
    public void TryParse_ValidStreamJson_ExtractsToolCounts()
    {
        var result = ClaudeToolCallCounter.TryParse(SampleStreamJson);

        Assert.NotNull(result);
        Assert.Equal(2, result!.ToolCallCounts["Bash"]);
        Assert.Equal(1, result.ToolCallCounts["Edit"]);
    }

    [Fact]
    public void TryParse_ValidStreamJson_ExtractsFinalText()
    {
        var result = ClaudeToolCallCounter.TryParse(SampleStreamJson);

        Assert.NotNull(result);
        Assert.Equal("I have completed the task.", result!.FinalText);
    }

    [Fact]
    public void TryParse_PlainText_ReturnsNull()
    {
        var result = ClaudeToolCallCounter.TryParse("This is plain text output from the agent.");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Null_ReturnsNull()
    {
        Assert.Null(ClaudeToolCallCounter.TryParse(null));
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsNull()
    {
        Assert.Null(ClaudeToolCallCounter.TryParse(""));
    }

    [Fact]
    public void TryParse_MalformedLines_SkipsAndContinues()
    {
        var input = """
            {this is not valid json}
            {"type":"result","subtype":"success","result":"Done!"}
            """;
        var result = ClaudeToolCallCounter.TryParse(input);
        Assert.NotNull(result);
        Assert.Equal("Done!", result!.FinalText);
        Assert.Empty(result.ToolCallCounts);
    }

    [Fact]
    public void TryParse_NoToolsNoResult_ReturnsNull()
    {
        var input = """
            {"type":"system","subtype":"init","sessionId":"x"}
            """;
        Assert.Null(ClaudeToolCallCounter.TryParse(input));
    }

    [Fact]
    public void TryParse_SingleToolCall_CountsCorrectly()
    {
        var input = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"/foo"}}],"stop_reason":"tool_use"}}
            {"type":"result","subtype":"success","result":"ok"}
            """;
        var result = ClaudeToolCallCounter.TryParse(input);
        Assert.NotNull(result);
        Assert.Equal(1, result!.ToolCallCounts["Read"]);
    }

    [Fact]
    public void TryParse_NoToolUseInContent_ReturnsEmptyToolCounts()
    {
        var input = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn"}}
            {"type":"result","subtype":"success","result":"hello"}
            """;
        var result = ClaudeToolCallCounter.TryParse(input);
        Assert.NotNull(result);
        Assert.Empty(result!.ToolCallCounts);
        Assert.Equal("hello", result.FinalText);
    }
}
