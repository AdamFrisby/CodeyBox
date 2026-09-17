using CodeyBox.Agents.Cmd;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdTerminalDiagnoser"/>: the terminal
/// <c>type: "result"</c> error lift (recorded real command-code 1.54.2
/// shapes — exit 1 unknown-model, exit 4 spend refusal, exit 8 max-turns),
/// the <c>run_error</c> fallback, the stderr plaintext pre-harness
/// failures, the permission-gate counter, and the healthy null path.
/// </summary>
public sealed class CmdTerminalDiagnoserTests
{
    [Fact]
    public void Extract_ResultError_LiftsProviderBody()
    {
        // Recorded real shape (unknown model id, exit 1).
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"s\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 400\"}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"isError\":true,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":2206,\"finalText\":\"\",\"error\":\"Error: 400\"}";

        var error = CmdTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("400", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_MaxTurnsSubtype_NamesSubtype()
    {
        // Recorded real shape (exit 8): max_turns subtype with an error body
        // lifts the body, so the run is classified instead of dead-lettering
        // as "produced no changes".
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"max_turns\",\"isError\":true," +
            "\"usage\":{\"inputTokens\":222468,\"outputTokens\":8281,\"cacheReadTokens\":4096,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1684026,\"finalText\":\"\",\"error\":\"Stopped: exceeded maximum turns (100).\"}";

        var error = CmdTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("maximum turns", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_RunErrorOnly_LiftsCompanion()
    {
        // Truncated capture missing the terminal result line: the run_error
        // companion is the fallback.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 403\"}}}";

        var error = CmdTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("403", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_StderrPreHarnessFailure_LiftsErrorLine()
    {
        // Config-shape rejections never reach the harness (exit 1, empty
        // stdout): the --effort refusal and the missing-key error, both
        // recorded real stderr shapes.
        Assert.Contains(
            "no adjustable reasoning effort",
            CmdTerminalDiagnoser.TryExtractTerminalError(string.Empty, "openrouter/x has no adjustable reasoning effort.")!,
            StringComparison.Ordinal);
        Assert.Contains(
            "OPENROUTER_API_KEY",
            CmdTerminalDiagnoser.TryExtractTerminalError(string.Empty, "Error: API key environment variable OPENROUTER_API_KEY is not set")!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_MaxTurnsStderrWarning_LiftsWarningLine()
    {
        // Recorded real stderr shape on exit 8.
        const string stderr = "Warning: Reached maximum conversation turns (100). Set a higher --max-turns limit or simplify the task.";

        var error = CmdTerminalDiagnoser.TryExtractTerminalError(string.Empty, stderr);

        Assert.NotNull(error);
        Assert.Contains("maximum conversation turns", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_UndeclaredModelAdvisory_IsNotAnError()
    {
        // The "isn't declared under provider" note fires on healthy runs too
        // (the CLI sends the id anyway) — never a failure.
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false," +
            "\"usage\":{\"inputTokens\":101,\"outputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"ok\"}";
        const string stderr = "\"deepseek/deepseek-v3\" isn't declared under provider 'openrouter' in providers.json — sending it anyway.";

        Assert.Null(CmdTerminalDiagnoser.TryExtractTerminalError(stdout, stderr));
    }

    [Fact]
    public void Extract_HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"s\"}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false," +
            "\"usage\":{\"inputTokens\":101,\"outputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"CMD_SMOKE_OK\"}";

        Assert.Null(CmdTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty));
    }

    [Fact]
    public void CountPermissionBlockedCalls_CountsGateFramesOnly()
    {
        // Recorded real no-yolo shape: two blocked calls.
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_queued\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\",\"input\":{}}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_hook_blocked\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\"," +
            "\"hookOutput\":\"Tool \\\"write_file\\\" requires permissions. Use --yolo to allow all tools in trusted environments.\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_hook_blocked\",\"toolCallId\":\"call-2\",\"toolName\":\"shell\"," +
            "\"hookOutput\":\"Tool \\\"shell\\\" requires permissions. Use --yolo to allow all tools in trusted environments.\"}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false,\"usage\":{\"inputTokens\":100,\"outputTokens\":50,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"Reviewed.\"}";

        Assert.Equal(2, CmdTerminalDiagnoser.CountPermissionBlockedCalls(stdout));
    }

    [Fact]
    public void CountPermissionBlockedCalls_HealthyRun_ReturnsZero()
    {
        const string stdout =
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_completed\",\"toolCallId\":\"c\",\"toolName\":\"glob\",\"result\":[],\"deferred\":false}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false,\"usage\":{\"inputTokens\":101,\"outputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"ok\"}";

        Assert.Equal(0, CmdTerminalDiagnoser.CountPermissionBlockedCalls(stdout));
        Assert.Equal(0, CmdTerminalDiagnoser.CountPermissionBlockedCalls(null));
        Assert.Equal(0, CmdTerminalDiagnoser.CountPermissionBlockedCalls("not json\n{truncated"));
    }

    [Fact]
    public void Extract_MalformedLines_NeverThrows()
    {
        const string stdout = "not json\n{\"type\":\"result\", truncated\n";

        Assert.Null(CmdTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: "also {truncated"));
    }
}
