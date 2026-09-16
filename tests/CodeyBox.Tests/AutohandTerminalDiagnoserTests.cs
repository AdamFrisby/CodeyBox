using CodeyBox.Agents.Autohand;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandTerminalDiagnoser"/>: first-error-wins
/// extraction from the stream-json event stream, truncation, and the healthy
/// / empty null paths. Shapes are recorded real autohand-cli 0.9.7 output
/// (key ids redacted).
/// </summary>
public sealed class AutohandTerminalDiagnoserTests
{
    [Fact]
    public void TryExtractTerminalError_AuthFailure_ReturnsMessage()
    {
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Authentication failed. Please verify your OpenRouter API key in ~/.autohand/config.json.\\nUser not found.\"}";

        var error = AutohandTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(error);
        Assert.Contains("Authentication failed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractTerminalError_GenericExitZeroError_ReturnsMessage()
    {
        const string stdout =
            "{\"type\":\"error\",\"message\":\"Command did not complete successfully.\"}";

        Assert.Equal(
            "Command did not complete successfully.",
            AutohandTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void TryExtractTerminalError_FirstErrorWins()
    {
        const string stdout =
            "{\"type\":\"tool_end\",\"toolId\":\"a\",\"toolName\":\"read_file\",\"toolSuccess\":true}\n" +
            "{\"type\":\"error\",\"message\":\"First problem.\"}\n" +
            "{\"type\":\"error\",\"message\":\"Second problem.\"}";

        Assert.Equal("First problem.", AutohandTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void TryExtractTerminalError_HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"tool_end\",\"toolId\":\"a\",\"toolName\":\"read_file\",\"toolSuccess\":true}\n" +
            "{\"type\":\"result\",\"content\":\"The task is complete.\"}";

        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void TryExtractTerminalError_EmptyAndChatter_ReturnNull()
    {
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError(null));
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError(string.Empty));
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError("plain banner chatter\nnot json\n"));
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError("{\"type\":\"error\"}"));
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError("{\"type\":\"error\",\"message\":\"  \"}"));
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError("{\"type\":\"error\",\"message\":42}"));
        Assert.Null(AutohandTerminalDiagnoser.TryExtractTerminalError("{truncated half-written frame"));
    }

    [Fact]
    public void TryExtractTerminalError_LongMessage_Truncated()
    {
        var longMessage = new string('e', AutohandTerminalDiagnoser.MaxDiagnosticChars + 100);
        var stdout = "{\"type\":\"error\",\"message\":\"" + longMessage + "\"}";

        var error = AutohandTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(error);
        Assert.Equal(AutohandTerminalDiagnoser.MaxDiagnosticChars + 1, error.Length);
    }
}
