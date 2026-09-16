using System.Text.Json;
using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DotNetOpencodeStreamParser"/>. Claim vocabulary pins
/// the envelope recorded live against
/// 0.1.0-ci.20260905083303.33955573552.1 (<c>step_start</c> with
/// <c>ses_/prt_/msg_</c> ids, <c>error</c> frames); the remaining verbs
/// (<c>text</c>, <c>reasoning</c>, <c>tool_use</c>, <c>step_finish</c>) are the
/// set the CLI's run-output layer emits.
/// </summary>
public sealed class DotNetOpencodeStreamParserTests
{
    private static JsonElement ParseLine(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Kind_IsDotNetOpencode()
    {
        Assert.Equal(AgentKind.DotNetOpencode, new DotNetOpencodeStreamParser().Kind);
    }

    [Fact]
    public void TryClaim_RecordedStepStart_Claimed()
    {
        // Recorded live 2026-09-16.
        var line = ParseLine(
            "{\"type\":\"step_start\",\"timestamp\":1789512734124,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"part\":{\"id\":\"prt_0a74555ac001QmCuITT448cuhf\",\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"messageID\":\"msg_0a745489a001WHQtkGpO5IBS2h\",\"type\":\"step-start\"}}");

        Assert.True(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public void TryClaim_RecordedErrorFrame_Claimed()
    {
        // Recorded live 2026-09-16 (bogus-key 401).
        var line = ParseLine(
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}");

        Assert.True(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("reasoning")]
    [InlineData("tool_use")]
    [InlineData("step_finish")]
    public void TryClaim_RunVerbs_Claimed(string type)
    {
        var line = ParseLine(
            $"{{\"type\":\"{type}\",\"timestamp\":1,\"sessionID\":\"ses_1\"}}");

        Assert.True(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("agent_end")]
    [InlineData("message_end")]
    [InlineData("assistant")]
    [InlineData("result")]
    public void TryClaim_ForeignVerbs_NotClaimed(string type)
    {
        // pi / claude verbs must never be stolen even with a sessionID.
        var line = ParseLine(
            $"{{\"type\":\"{type}\",\"timestamp\":1,\"sessionID\":\"ses_1\"}}");

        Assert.False(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public void TryClaim_MissingSessionId_NotClaimed()
    {
        // A foreign {"type":"text"} line without the CLI envelope stays foreign.
        var line = ParseLine("{\"type\":\"text\",\"text\":\"hello\"}");

        Assert.False(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public void TryClaim_NonObject_NotClaimed()
    {
        var line = ParseLine("[{\"type\":\"text\",\"sessionID\":\"ses_1\"}]");

        Assert.False(new DotNetOpencodeStreamParser().TryClaim(line));
    }
}
