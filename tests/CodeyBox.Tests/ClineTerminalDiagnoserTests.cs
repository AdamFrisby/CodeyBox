using CodeyBox.Agents.Cline;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClineTerminalDiagnoser"/> (internal — the test
/// project has <c>InternalsVisibleTo</c>). Fixtures are recorded real
/// output (cline 3.0.62); the spend-limit URL's workspace key id is
/// redacted — it is secret-derived.
/// </summary>
public sealed class ClineTerminalDiagnoserTests
{
    [Fact]
    public void TryExtractTerminalError_AgentErrorFrame_Preferred()
    {
        const string stdout =
            "{\"ts\":\"2026-09-16T18:01:41.599Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"name\":\"Error\",\"message\":\"User not found.\"},\"errorClass\":\"auth\",\"recoverable\":false,\"iteration\":1}}\n" +
            "{\"ts\":\"2026-09-16T18:01:41.673Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":407,\"text\":\"User not found.\"}";

        Assert.Equal(
            "User not found.",
            ClineTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty));
    }

    [Fact]
    public void TryExtractTerminalError_ErrorRunResult_UsedWhenNoAgentErrorFrame()
    {
        const string stdout =
            "{\"ts\":\"2026-09-16T18:01:51.706Z\",\"type\":\"run_result\",\"finishReason\":\"error\",\"iterations\":1,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":152,\"text\":\"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED\"}";

        var diagnostic = ClineTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: null);

        Assert.NotNull(diagnostic);
        Assert.Contains("Key limit exceeded", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_StderrOnlyError_UsedAsFallback()
    {
        // Recorded real shape for the missing-prompt refusal: the CLI exits
        // 1 with NOTHING on stdout and the error line on stderr only.
        const string stderr =
            "{\"ts\":\"2026-09-16T18:04:17.213Z\",\"type\":\"error\",\"message\":\"JSON output mode requires a prompt argument or piped stdin (interactive mode is unsupported)\"}";

        var diagnostic = ClineTerminalDiagnoser.TryExtractTerminalError(stdout: string.Empty, stderr);

        Assert.NotNull(diagnostic);
        Assert.Contains("requires a prompt argument", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"ts\":\"2026-09-16T18:02:09.659Z\",\"type\":\"agent_event\",\"event\":{\"type\":\"done\",\"reason\":\"completed\",\"text\":\"The task is complete.\"}}\n" +
            "{\"ts\":\"2026-09-16T18:02:09.691Z\",\"type\":\"run_result\",\"finishReason\":\"completed\",\"iterations\":1,\"usage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"aggregateUsage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0,\"totalCost\":0},\"durationMs\":1000,\"text\":\"The task is complete.\"}";

        Assert.Null(ClineTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty));
        Assert.Null(ClineTerminalDiagnoser.TryExtractTerminalError(null, null));
        Assert.Null(ClineTerminalDiagnoser.TryExtractTerminalError("plain chatter", "more chatter"));
    }

    [Fact]
    public void TryExtractTerminalError_LongMessage_Truncated()
    {
        var longMessage = new string('x', ClineTerminalDiagnoser.MaxDiagnosticChars + 100);
        var stdout =
            "{\"type\":\"agent_event\",\"event\":{\"type\":\"error\",\"error\":{\"message\":\"" + longMessage + "\"}}}";

        var diagnostic = ClineTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: null);

        Assert.NotNull(diagnostic);
        Assert.True(diagnostic.Length <= ClineTerminalDiagnoser.MaxDiagnosticChars + 1);
    }
}
