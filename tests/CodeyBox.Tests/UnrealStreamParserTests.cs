using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Agents.Unreal;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="UnrealStreamParser"/>:
/// - Real captured JSONL sample from pinned release v0.1.1
/// - Multi-turn tool execution with tool_call and tool_call_status
/// - Token usage parsing from model_response
/// - Real error event {"type":"error","message":"..."}
/// - Claim discipline (claims Unreal session items, rejects other schemas)
/// </summary>
public sealed class UnrealStreamParserTests
{
    private readonly UnrealStreamParser _parser = new();

    private const string SampleTurn1 =
        """{"Sequence":1,"RecordedAt":"2026-09-24T17:09:01.949420242Z","Kind":"input","Data":{"ID":"afb61d14-8551-4b4e-ad8b-a838f29d0f0e","Kind":"control","Payload":{"Mode":"settings","Reason":"","Parameters":{"ReasoningEffort":"high"}}}}""";

    private const string SampleTurn2 =
        """{"Sequence":2,"RecordedAt":"2026-09-24T17:09:01.960298521Z","Kind":"input","Data":{"ID":"7fe3ec0c-df21-4e78-9375-a25ec96c82f4","Kind":"external","Payload":"test prompt"}}""";

    private const string SampleTurn4 =
        """{"Sequence":4,"RecordedAt":"2026-09-24T17:09:01.977842916Z","Kind":"turn","Data":{"ID":"5729508d-d7f6-491e-8240-87d224509d67","PreviousTurnID":"","Type":"regular"}}""";

    private const string SampleModelResponseWithText =
        """{"Sequence":5,"RecordedAt":"2026-09-24T17:09:01.987212341Z","Kind":"model_response","Data":{"TurnID":"5729508d-d7f6-491e-8240-87d224509d67","Response":{"ID":"resp-1","Stop":"complete","Output":[{"ProviderID":"","Type":"message","Data":{"Role":"assistant","Text":"Hello from unreal agent","Phase":""}}],"Usage":{"InputTokens":10,"CachedInputTokens":2,"CacheWriteInputTokens":0,"OutputTokens":5,"ReasoningTokens":0,"Raw":{"input_tokens":10,"output_tokens":5,"total_tokens":15}},"Failure":null}}}""";

    private const string SampleModelResponseWithToolCall =
        """{"Sequence":5,"RecordedAt":"2026-09-24T17:09:11.316959806Z","Kind":"model_response","Data":{"TurnID":"7687d080-a0f9-4aab-9a8d-d4b91c4e3f52","Response":{"ID":"resp-1","Stop":"complete","Output":[{"ProviderID":"","Type":"tool_call","Data":{"CallID":"call-1","Name":"Bash","Arguments":"{\"command\": \"echo hello > output.txt\"}"}}],"Usage":{"InputTokens":15,"CachedInputTokens":0,"CacheWriteInputTokens":0,"OutputTokens":8,"ReasoningTokens":0,"Raw":{"input_tokens":15,"output_tokens":8,"total_tokens":23}},"Failure":null}}}""";

    private const string SampleToolCallStatusCompleted =
        """{"Sequence":7,"RecordedAt":"2026-09-24T17:09:11.333677171Z","Kind":"tool_call_status","Data":{"TurnID":"7687d080-a0f9-4aab-9a8d-d4b91c4e3f52","CallID":"call-1","Status":{"Error":"","WaitingFor":["2dd39a7a-fd2e-4c95-a032-9646d1446361"]},"Operations":[{"MaxOutputLength":40000,"ID":"2dd39a7a-fd2e-4c95-a032-9646d1446361","Type":"shell","Version":3,"Status":"completed","State":{"Input":{"Command":"echo hello > output.txt","Shell":"/bin/sh","Directory":"/work"},"BaseDirectory":"/tmp","Phase":"","ProcessGroupID":0,"PendingExitCode":null,"OutSize":12,"ErrSize":0,"InlineOut":"","InlineErr":"","InlineOutTail":"","InlineErrTail":"","Result":{"Out":"hello\n","Err":"","OutSize":6,"ErrSize":0,"ExitCode":0},"TerminalError":"","ErrorTruncated":false,"OutTruncated":false,"ErrTruncated":false,"OutPath":"/tmp/out","ErrPath":"/tmp/err"}}]}}""";

    private const string SampleRealErrorEvent =
        """{"type":"error","message":"UNREAL_HARNESS_LLM_API_KEY or OPENAI_API_KEY must be set"}""";

    [Fact]
    public void Kind_IsUnreal()
    {
        Assert.Equal(AgentKind.Unreal, _parser.Kind);
    }

    [Fact]
    public void CanEmitShapeOf_MatchesUnrealOnly()
    {
        Assert.True(_parser.CanEmitShapeOf(AgentKind.Unreal));
        Assert.False(_parser.CanEmitShapeOf(AgentKind.Claude));
        Assert.False(_parser.CanEmitShapeOf(AgentKind.Codex));
        Assert.False(_parser.CanEmitShapeOf(AgentKind.Gemini));
    }

    [Fact]
    public void TryClaim_UnrealSessionEvents_ClaimsTrue()
    {
        using var doc1 = JsonDocument.Parse(SampleTurn1);
        Assert.True(_parser.TryClaim(doc1.RootElement));

        using var doc5 = JsonDocument.Parse(SampleModelResponseWithText);
        Assert.True(_parser.TryClaim(doc5.RootElement));

        using var doc7 = JsonDocument.Parse(SampleToolCallStatusCompleted);
        Assert.True(_parser.TryClaim(doc7.RootElement));
    }

    [Fact]
    public void TryClaim_NonUnrealEvents_RejectsClaims()
    {
        // Claude / StreamJson frame
        using var claudeDoc = JsonDocument.Parse("""{"type":"assistant","message":{"content":[]}}""");
        Assert.False(_parser.TryClaim(claudeDoc.RootElement));

        // Codex frame
        using var codexDoc = JsonDocument.Parse("""{"type":"turn","turn":{"turn_id":"1"}}""");
        Assert.False(_parser.TryClaim(codexDoc.RootElement));

        // Bare error event (not a session item with Sequence/RecordedAt/Data)
        using var errDoc = JsonDocument.Parse(SampleRealErrorEvent);
        Assert.False(_parser.TryClaim(errDoc.RootElement));
    }

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
    public async Task ParseAsync_ModelResponse_ExtractsAssistantTextAndTokens()
    {
        var text = string.Join("\n", SampleTurn1, SampleTurn2, SampleTurn4, SampleModelResponseWithText);
        var summary = await _parser.ParseAsync(StreamOf(text));

        Assert.False(summary.IsUnsupported);
        Assert.Equal("Hello from unreal agent", summary.FinalAssistantMessage);
        Assert.Equal(10, summary.InputTokens);
        Assert.Equal(2, summary.CachedInputTokens);
        Assert.Equal(5, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_ModelResponseWithToolCallAndStatus_ExtractsCompletedResult()
    {
        var text = string.Join("\n", SampleModelResponseWithToolCall, SampleToolCallStatusCompleted);
        var summary = await _parser.ParseAsync(StreamOf(text));

        Assert.False(summary.IsUnsupported);
        Assert.Single(summary.ToolCalls);
        var tool = summary.ToolCalls[0];
        Assert.Equal("call-1", tool.ToolUseId);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Contains("echo hello", tool.InputSummary);
        Assert.True(tool.Succeeded);
        Assert.Equal(6, tool.OutputBytes);
        Assert.Equal(15, summary.InputTokens);
        Assert.Equal(8, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_RealCapturedSession_MultiTurnExecution()
    {
        var line1 = SampleTurn1;
        var line2 = SampleTurn2;
        var line4 = SampleTurn4;
        var line5 = SampleModelResponseWithToolCall;
        var line7 = SampleToolCallStatusCompleted;
        var line9 = """{"Sequence":9,"RecordedAt":"2026-09-24T17:09:11.33678647Z","Kind":"model_response","Data":{"TurnID":"deeae52f-2d0a-4891-9737-d377204222f2","Response":{"ID":"resp-2","Stop":"complete","Output":[{"ProviderID":"","Type":"message","Data":{"Role":"assistant","Text":"I have created output.txt","Phase":""}}],"Usage":{"InputTokens":20,"CachedInputTokens":0,"CacheWriteInputTokens":0,"OutputTokens":8,"ReasoningTokens":0,"Raw":{"input_tokens":20,"output_tokens":8,"total_tokens":28}},"Failure":null}}}""";

        var text = string.Join("\n", line1, line2, line4, line5, line7, line9);
        var summary = await _parser.ParseAsync(StreamOf(text));

        Assert.False(summary.IsUnsupported);
        Assert.Equal("I have created output.txt", summary.FinalAssistantMessage);
        Assert.Single(summary.ToolCalls);
        Assert.Equal("call-1", summary.ToolCalls[0].ToolUseId);
        Assert.True(summary.ToolCalls[0].Succeeded);
        Assert.Equal(20, summary.InputTokens);
        Assert.Equal(8, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_RealErrorEvent_ExtractsErrorMessage()
    {
        var summary = await _parser.ParseAsync(StreamOf(SampleRealErrorEvent));

        Assert.False(summary.IsUnsupported);
        Assert.NotNull(summary.FinalAssistantMessage);
        Assert.Contains("UNREAL_HARNESS_LLM_API_KEY or OPENAI_API_KEY must be set", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_IsUnsupported()
    {
        var summary = await _parser.ParseAsync(StreamOf(string.Empty));
        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_NonJsonNoiseOnly_IsUnsupported()
    {
        var summary = await _parser.ParseAsync(StreamOf("random log line\nanother line\n"));
        Assert.True(summary.IsUnsupported);
    }
}
