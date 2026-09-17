using CodeyBox.Agents.Qwen;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="QwenTerminalDiagnoser"/> using the terminal shapes
/// verified live against qwen 0.24.0: exit-1 runs whose failure signal is a
/// <c>result/subtype:"error_during_execution"</c> frame on stdout plus an
/// <c>AlreadyReportedError</c> envelope on stderr.
/// </summary>
public sealed class QwenTerminalDiagnoserTests
{
    // Recorded live: bogus OPENAI_API_KEY against OpenRouter, exit 1.
    // assistant frame + terminal result frame (uuids redacted).
    private const string AuthFailureStdout =
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"cwd\":\"/work\",\"model\":\"m\",\"permission_mode\":\"yolo\",\"qwen_code_version\":\"0.24.0\"}\n" +
        "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"[API Error: 401 Missing Authentication header]\"}],\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}}\n" +
        "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"session_id\":\"s\",\"is_error\":true,\"duration_ms\":690,\"duration_api_ms\":0,\"num_turns\":1,\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"cache_read_input_tokens\":0},\"permission_denials\":[],\"error\":{\"message\":\"[API Error: 401 Missing Authentication header]\"}}\n";

    // Recorded live: the stderr twin of the same run (pretty-printed).
    private const string AuthFailureStderr =
        "{\n" +
        "  \"error\": {\n" +
        "    \"type\": \"AlreadyReportedError\",\n" +
        "    \"message\": \"[API Error: 401 Missing Authentication header]\",\n" +
        "    \"code\": 1\n" +
        "  }\n" +
        "}\n";

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(QwenTerminalDiagnoser.TryExtractTerminalError(null, null));
        Assert.Null(QwenTerminalDiagnoser.TryExtractTerminalError("", ""));
        Assert.Null(QwenTerminalDiagnoser.TryExtractTerminalError("   ", null));
    }

    [Fact]
    public void HealthyRun_ReturnsNull()
    {
        // Recorded live: success run (result text redacted to the shape).
        const string stdout =
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"qwen_code_version\":\"0.24.0\"}\n" +
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"QWEN_SMOKE_OK\"}],\"usage\":{\"input_tokens\":24157,\"output_tokens\":58,\"cache_read_input_tokens\":0,\"total_tokens\":24215}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s\",\"is_error\":false,\"duration_ms\":115311,\"duration_api_ms\":70752,\"num_turns\":1,\"result\":\"QWEN_SMOKE_OK\",\"usage\":{\"input_tokens\":33823,\"output_tokens\":795,\"cache_read_input_tokens\":0,\"total_tokens\":34618}}\n";

        Assert.Null(QwenTerminalDiagnoser.TryExtractTerminalError(stdout, ""));
    }

    [Fact]
    public void LiveAuthFailureStdout_ExtractsErrorMessage()
    {
        var result = QwenTerminalDiagnoser.TryExtractTerminalError(AuthFailureStdout, AuthFailureStderr);

        Assert.NotNull(result);
        Assert.Contains("401", result);
        Assert.Contains("Missing Authentication header", result);
    }

    [Fact]
    public void StderrEnvelope_ExtractsWhenStdoutLost()
    {
        // Stdout truncated before the result frame (killed run); the
        // stderr AlreadyReportedError envelope still names the cause.
        const string stdout =
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"qwen_code_version\":\"0.24.0\"}\n";

        var result = QwenTerminalDiagnoser.TryExtractTerminalError(stdout, AuthFailureStderr);

        Assert.NotNull(result);
        Assert.Contains("Missing Authentication header", result);
    }

    [Fact]
    public void LiveQuotaFailureStdout_ExtractsKeyLimitMessage()
    {
        // Recorded live: paid model on a $0-spend-limit key, exit 1.
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"session_id\":\"s\",\"is_error\":true,\"duration_ms\":841,\"duration_api_ms\":0,\"num_turns\":1,\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"cache_read_input_tokens\":0},\"permission_denials\":[],\"error\":{\"message\":\"[API Error: 403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058]\"}}\n";

        var result = QwenTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.Contains("Key limit exceeded", result);
    }

    [Fact]
    public void NonErrorResult_Ignored()
    {
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\"}\n";

        Assert.Null(QwenTerminalDiagnoser.TryExtractTerminalError(stdout, null));
    }

    [Fact]
    public void MalformedLines_Skipped_ErrorStillFound()
    {
        const string stdout =
            "not json at all\n" +
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"error\":{\"message\":\"boom\"}}\n" +
            "{\"truncated\": true\n";

        Assert.Equal("boom", QwenTerminalDiagnoser.TryExtractTerminalError(stdout, null));
    }

    [Fact]
    public void LongErrorMessage_Truncated()
    {
        var longError = new string('e', QwenTerminalDiagnoser.MaxDiagnosticChars + 100);
        var stdout = "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"error\":{\"message\":\"" + longError + "\"}}\n";

        var result = QwenTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.True(result!.Length <= QwenTerminalDiagnoser.MaxDiagnosticChars + 1);
    }

    [Fact]
    public void ErrorFrameWithoutMessage_YieldsNamedFallback()
    {
        const string stdout =
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true}\n";

        var result = QwenTerminalDiagnoser.TryExtractTerminalError(stdout, null);

        Assert.NotNull(result);
        Assert.Contains("error_during_execution", result);
    }
}
