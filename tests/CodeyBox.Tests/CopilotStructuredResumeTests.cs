using System.Text.Json;
using CodeyBox.Agents.Copilot;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the Copilot CLI's structured mode, native session resume, and the
/// bounded empty-finish continuation. Fixtures use the real
/// <c>--output-format json</c> event shapes and the real plaintext footer
/// captured from copilot 1.0.106 (see the runner remarks), including the
/// <c>'Changes +0 -0 / Duration … / Tokens …'</c> summary shape from the
/// dead-turn evidence.
/// </summary>
public sealed class CopilotStructuredResumeTests
{
    private const string SessionId = "1221b5a6-a789-4f57-90e0-289126780b69";
    private const string HelpAdvertisingJson =
        "  --output-format <text|json>   Output format: 'text' (default) or 'json' (JSONL stream events)";

    private static string EmptyFinishJson(string? sessionId = SessionId, bool includeSessionId = true)
    {
        var id = includeSessionId ? $"\"sessionId\":\"{sessionId}\"," : string.Empty;
        return string.Join('\n',
            """{"type":"session.skills_loaded","skills":[]}""",
            """{"type":"session.mcp_servers_loaded","servers":[]}""",
            "{\"type\":\"session.started\",\"sessionId\":\"" + SessionId + "\",\"model\":\"test-model\"}",
            """{"type":"assistant.message","role":"assistant","data":{"content":"","toolRequests":[{"id":"call_1","name":"read","arguments":{"path":"/work/x"}}]}}""",
            """{"type":"model.call_started","callId":"call_1"}""",
            """{"type":"model.call_finished","callId":"call_1"}""",
            """{"type":"session.usage","inputTokens":3600000,"outputTokens":27900}""",
            """{"type":"assistant.turn_end","reason":"stop"}""",
            """{"type":"assistant.idle"}""",
            "{\"type\":\"result\"," + id + "\"exitCode\":0,\"usage\":{\"codeChanges\":{\"linesAdded\":0,\"linesRemoved\":0,\"filesModified\":[]}}}");
    }

    private static string MessageFinishJson(string message = "Done: fixed the bug.")
    {
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return string.Join('\n',
            """{"type":"session.skills_loaded","skills":[]}""",
            "{\"type\":\"assistant.message\",\"role\":\"assistant\",\"data\":{\"content\":\"" + escaped + "\"}}",
            """{"type":"assistant.turn_end","reason":"stop"}""",
            "{\"type\":\"result\",\"sessionId\":\"" + SessionId + "\",\"exitCode\":0,\"usage\":{\"codeChanges\":{\"linesAdded\":0,\"linesRemoved\":0,\"filesModified\":[]}}}");
    }

    private static string EditsFinishJson()
        => string.Join('\n',
            """{"type":"session.skills_loaded","skills":[]}""",
            """{"type":"assistant.message","role":"assistant","data":{"content":""}}""",
            """{"type":"assistant.turn_end","reason":"stop"}""",
            "{\"type\":\"result\",\"sessionId\":\"" + SessionId + "\",\"exitCode\":0,\"usage\":{\"codeChanges\":{\"linesAdded\":12,\"linesRemoved\":3,\"filesModified\":[\"src/a.cs\"]}}}");

    /// <summary>Byte-shape of the real plaintext footer (column-aligned, multi-space).</summary>
    private static string PlaintextFooter(string? sessionId = SessionId)
        => "Changes    +0 -0\n"
            + "Duration   3m 23s\n"
            + "Tokens     ↑ 3.6m (3.5m cached) • ↓ 27.9k (20.0k reasoning)\n"
            + $"Resume     copilot --resume={sessionId}";

    [Fact]
    public void ExtractSessionId_FromTrailingJsonResultEvent()
    {
        var runner = new CopilotAgentRunner();

        Assert.Equal(SessionId, runner.TryExtractSessionId(EmptyFinishJson()));
    }

    [Fact]
    public void ExtractSessionId_FromPlaintextResumeFooter()
    {
        var runner = new CopilotAgentRunner();

        Assert.Equal(SessionId, runner.TryExtractSessionId(PlaintextFooter()));
    }

    [Fact]
    public void ExtractSessionId_IgnoresModelPrintedFakeResumeLine()
    {
        // A fake hint mid-turn with no CLI footer after it must not match:
        // only end-anchored CLI positions are trusted.
        var runner = new CopilotAgentRunner();
        var stdout = "Working on it.\nResume copilot --resume=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\nstill going";

        Assert.Null(runner.TryExtractSessionId(stdout));
    }

    [Fact]
    public void ExtractSessionId_PrefersEndAnchoredFooterOverEarlierFake()
    {
        var runner = new CopilotAgentRunner();
        var stdout = "Resume copilot --resume=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n"
            + "Changes    +0 -0\n"
            + "Duration   5s\n"
            + $"Resume     copilot --resume={SessionId}";

        Assert.Equal(SessionId, runner.TryExtractSessionId(stdout));
    }

    [Fact]
    public void ExtractSessionId_RejectsNonUuidResumeValue()
    {
        var runner = new CopilotAgentRunner();

        Assert.Null(runner.TryExtractSessionId(PlaintextFooter("not-a-uuid")));
    }

    [Fact]
    public void ExtractSessionId_RejectsJsonResultNotOnLastLine()
    {
        // A result event buried mid-capture (truncated stream with a
        // model-printed fake) must not yield its session id.
        var runner = new CopilotAgentRunner();
        var stdout = "{\"type\":\"result\",\"sessionId\":\"" + SessionId
            + "\",\"exitCode\":0}\n{\"type\":\"assistant.idle\"}";

        Assert.Null(runner.TryExtractSessionId(stdout));
    }

    [Fact]
    public void RequiresStructuredStreamForSessionId_IsTrue()
    {
        ICliSessionResumableAgentRunner runner = new CopilotAgentRunner();

        Assert.True(runner.RequiresStructuredStreamForSessionId);
    }

    [Fact]
    public void TurnOutcome_EmptyJsonTurn_RequiresContinuation()
    {
        var outcome = CopilotTurnOutcome.Extract(EmptyFinishJson());

        Assert.True(outcome.IsStructured);
        Assert.Equal(SessionId, outcome.SessionId);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(outcome.HasFinalMessage);
        Assert.False(outcome.HasEdits);
        Assert.True(outcome.RequiresContinuation);
    }

    [Fact]
    public void TurnOutcome_JsonTurnWithFinalMessage_DoesNotRequireContinuation()
    {
        var outcome = CopilotTurnOutcome.Extract(MessageFinishJson());

        Assert.Equal(true, outcome.HasFinalMessage);
        Assert.False(outcome.RequiresContinuation);
    }

    [Fact]
    public void TurnOutcome_JsonTurnWithEdits_DoesNotRequireContinuation()
    {
        var outcome = CopilotTurnOutcome.Extract(EditsFinishJson());

        Assert.Equal(true, outcome.HasEdits);
        Assert.False(outcome.RequiresContinuation);
    }

    [Fact]
    public void TurnOutcome_FooterOnlyPlaintext_RequiresContinuation()
    {
        var outcome = CopilotTurnOutcome.Extract(PlaintextFooter());

        Assert.False(outcome.IsStructured);
        Assert.Equal(SessionId, outcome.SessionId);
        Assert.False(outcome.HasFinalMessage);
        Assert.False(outcome.HasEdits);
        Assert.True(outcome.RequiresContinuation);
    }

    [Fact]
    public void TurnOutcome_PlaintextWithBody_IsUnknown()
    {
        // Plaintext cannot separate tool transcripts from assistant prose, so
        // a non-blank body stays unknown rather than guessing "no message".
        var outcome = CopilotTurnOutcome.Extract("Exploratory notes and tool output\n" + PlaintextFooter());

        Assert.Null(outcome.HasFinalMessage);
        Assert.False(outcome.RequiresContinuation);
    }

    [Fact]
    public void TurnOutcome_EmptyCapture_IsUnknown()
    {
        var outcome = CopilotTurnOutcome.Extract(null);

        Assert.Null(outcome.HasFinalMessage);
        Assert.Null(outcome.HasEdits);
        Assert.False(outcome.RequiresContinuation);
    }

    [Fact]
    public async Task RunAsync_EmptyTurn_ContinuesInSameSession()
    {
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, EmptyFinishJson(), string.Empty),
             new SandboxExecResult(0, MessageFinishJson(), string.Empty)]);
        var runner = new CopilotAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.True(result.Success);
        var agentExecs = sandbox.AgentExecs;
        Assert.Equal(2, agentExecs.Count);
        Assert.DoesNotContain("--resume", agentExecs[0].Argv);
        Assert.Contains("--output-format", agentExecs[0].Argv);
        var resumeIdx = agentExecs[1].Argv.ToList().IndexOf("--resume");
        Assert.True(resumeIdx >= 0, "continuation must resume the native session");
        Assert.Equal(SessionId, agentExecs[1].Argv[resumeIdx + 1]);
        Assert.Contains("--output-format", agentExecs[1].Argv);
    }

    [Fact]
    public async Task RunAsync_ExhaustedBound_FailsWithDistinctFlag()
    {
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, EmptyFinishJson(), string.Empty),
             new SandboxExecResult(0, EmptyFinishJson(), string.Empty)]);
        var runner = new CopilotAgentRunner
        {
            Options = new CopilotOptions { NoFinalResponseMaxContinuations = 1 },
        };

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.True(result.EndedWithoutFinalResponse);
        Assert.False(result.OutputTokenLimitExhausted);
        Assert.Equal(2, sandbox.AgentExecs.Count);
        Assert.Contains("same-session continuation", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ZeroBound_DisablesContinuation()
    {
        // Bound 0 disables the feature: the empty turn ends exactly as it
        // did before structured mode existed (success, no flag, no extra
        // turn) and the pipeline's pre-existing no-changes path owns it.
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, EmptyFinishJson(), string.Empty)]);
        var runner = new CopilotAgentRunner
        {
            Options = new CopilotOptions { NoFinalResponseMaxContinuations = 0 },
        };

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.False(result.EndedWithoutFinalResponse);
        Assert.Single(sandbox.AgentExecs);
    }

    [Fact]
    public async Task RunAsync_HealthyTurn_DoesNotContinue()
    {
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, MessageFinishJson(), string.Empty)]);
        var runner = new CopilotAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.False(result.EndedWithoutFinalResponse);
        Assert.Single(sandbox.AgentExecs);
    }

    [Fact]
    public async Task RunAsync_TurnWithEdits_DoesNotContinue()
    {
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, EditsFinishJson(), string.Empty)]);
        var runner = new CopilotAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.True(result.Success);
        Assert.Single(sandbox.AgentExecs);
    }

    [Fact]
    public async Task RunAsync_QuotaSignal_BlocksContinuation()
    {
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, EmptyFinishJson(), "Error: quota exceeded for this account")]);
        var runner = new CopilotAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.True(result.EndedWithoutFinalResponse);
        Assert.Single(sandbox.AgentExecs);
    }

    [Fact]
    public async Task RunAsync_EmptyTurnWithoutSessionId_CannotContinue()
    {
        var sandbox = new SequencedSandbox(
            HelpAdvertisingJson,
            [new SandboxExecResult(0, EmptyFinishJson(includeSessionId: false), string.Empty)]);
        var runner = new CopilotAgentRunner();

        var result = await runner.RunAsync(
            sandbox, "/work", "do the thing", credential: null,
            captureStructuredStream: true);

        Assert.False(result.Success);
        Assert.True(result.EndedWithoutFinalResponse);
        Assert.Single(sandbox.AgentExecs);
        Assert.Contains("no resumable session id", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFinalResponseMaxContinuations_DefaultIsTwo()
    {
        Assert.Equal(2, new CopilotAgentRunner().NoFinalResponseMaxContinuations);
    }

    [Fact]
    public void NoFinalResponseMaxContinuations_ClampsToAbsoluteMax()
    {
        var runner = new CopilotAgentRunner(
            defaults: null,
            optionsProvider: () => new CopilotOptions { NoFinalResponseMaxContinuations = 10_000 });

        Assert.Equal(OutputTokenLimit.AbsoluteMaxContinuations, runner.NoFinalResponseMaxContinuations);
    }

    [Fact]
    public void NoFinalResponseMaxContinuations_NegativeDegradesToZero()
    {
        var runner = new CopilotAgentRunner(
            defaults: null,
            optionsProvider: () => new CopilotOptions { NoFinalResponseMaxContinuations = -3 });

        Assert.Equal(0, runner.NoFinalResponseMaxContinuations);
    }

    [Fact]
    public void ValidateCopilotSessionId_NonUuid_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CopilotAgentRunner.ValidateCopilotSessionId(new AgentNativeSessionId("not-a-uuid")));

        Assert.Contains("UUID", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractAgentVisibleText_JsonConcatenatesMessageContents()
    {
        IAgentVisibleTextExtractor runner = new CopilotAgentRunner();

        Assert.Equal("Done: fixed the bug.", runner.ExtractAgentVisibleText(MessageFinishJson()));
    }

    [Fact]
    public void ExtractAgentVisibleText_Plaintext_ReturnsNull()
    {
        IAgentVisibleTextExtractor runner = new CopilotAgentRunner();

        Assert.Null(runner.ExtractAgentVisibleText("some prose\n" + PlaintextFooter()));
    }

    [Fact]
    public void Parser_ClaimsCopilotEventLines()
    {
        var parser = new CopilotStreamParser();
        using var session = JsonDocument.Parse("""{"type":"session.skills_loaded","skills":[]}""");
        using var message = JsonDocument.Parse("""{"type":"assistant.message","role":"assistant","data":{"content":"hi"}}""");
        using var result = JsonDocument.Parse("""{"type":"result","sessionId":"x","exitCode":0}""");
        using var stderr = JsonDocument.Parse("""{"type":"codeybox.stderr","text":"boom"}""");
        using var foreign = JsonDocument.Parse("""{"foo":1}""");

        Assert.True(parser.TryClaim(session.RootElement));
        Assert.True(parser.TryClaim(message.RootElement));
        Assert.True(parser.TryClaim(result.RootElement));
        Assert.False(parser.TryClaim(stderr.RootElement));
        Assert.False(parser.TryClaim(foreign.RootElement));
    }

    [Fact]
    public async Task Parser_SurfacesFinalMessageInSummary()
    {
        var parser = new CopilotStreamParser();
        await using var stream = StreamOf(MessageFinishJson());

        var summary = await parser.ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        Assert.Equal("Done: fixed the bug.", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task Parser_EmptyFinishSummarisesWithoutFinalMessage()
    {
        var parser = new CopilotStreamParser();
        await using var stream = StreamOf(EmptyFinishJson());

        var summary = await parser.ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }

    [Fact]
    public void FailureKindValue_IsDistinctFromNoChanges()
    {
        Assert.Equal("ended-without-final-response", WorkItemFailureKinds.EndedWithoutFinalResponse);
    }

    private static MemoryStream StreamOf(string text)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(text);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Sandbox fake that answers <c>--help</c> probes and bash prep with
    /// canned output and replays one scripted result per agent invocation,
    /// recording every exec for argv assertions.
    /// </summary>
    private sealed class SequencedSandbox(
        string helpOutput,
        IReadOnlyList<SandboxExecResult> agentResults) : ISandbox
    {
        private readonly Queue<SandboxExecResult> _agentResults = new(agentResults);

        public string Id => "sequenced";

        public List<SandboxExec> Execs { get; } = [];

        public IReadOnlyList<SandboxExec> AgentExecs
            => Execs.Where(e => e.Argv.Count > 0 && e.Argv[0] == "copilot" && !e.Argv.Contains("--help")).ToList();

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            Execs.Add(exec);
            if (exec.Argv.Contains("--help"))
                return Task.FromResult(new SandboxExecResult(0, helpOutput, string.Empty));
            if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
                return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
            if (_agentResults.Count == 0)
                return Task.FromResult(new SandboxExecResult(1, string.Empty, "no scripted agent result left"));
            return Task.FromResult(_agentResults.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
