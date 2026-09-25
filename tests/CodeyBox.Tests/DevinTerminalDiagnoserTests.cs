using CodeyBox.Agents.Devin;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DevinTerminalDiagnoser"/>: the <c>Error: …</c>
/// signal lifted from raw stderr (print-mode path), from
/// <c>codeybox.stderr</c> envelopes on stdout (ACP dispatch path, where the
/// shim relays its whole stderr surface), and the healthy/null cases.
/// </summary>
public sealed class DevinTerminalDiagnoserTests
{
    [Fact]
    public void Extract_RawErrorLineOnStderr_ReturnsLine()
    {
        const string stderr =
            "some earlier noise\n" +
            "Error: Not logged in. Run `devin auth login` to authenticate.";

        var error = DevinTerminalDiagnoser.TryExtractTerminalError(stderr, stdout: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("Not logged in", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ErrorLineInsideStderrEnvelopeOnStdout_ReturnsInnerText()
    {
        // ACP dispatches: the shim folds fd 2 into codeybox.stderr envelopes
        // on stdout, so the CLI's `Error:` line arrives wrapped.
        const string stdout =
            "{\"type\":\"devin.acp\",\"event\":\"session_started\",\"sessionId\":\"s-1\"}\n" +
            "{\"type\":\"codeybox.stderr\",\"text\":\"Error: Not logged in. Run `devin auth login` to authenticate.\"}\n" +
            "{\"type\":\"devin.acp\",\"event\":\"fatal\",\"stage\":\"turn\",\"message\":\"devin acp closed its stdout before the turn completed\"}";

        var error = DevinTerminalDiagnoser.TryExtractTerminalError(stderr: string.Empty, stdout);

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error, StringComparison.Ordinal);
        Assert.Contains("Not logged in", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_StderrTakesPrecedenceOverStdout()
    {
        var error = DevinTerminalDiagnoser.TryExtractTerminalError(
            "Error: stderr wins",
            "{\"type\":\"codeybox.stderr\",\"text\":\"Error: stdout loses\"}");

        Assert.Equal("Error: stderr wins", error);
    }

    [Fact]
    public void Extract_NoErrorLine_ReturnsNull()
    {
        var error = DevinTerminalDiagnoser.TryExtractTerminalError(
            "some noise\n",
            "{\"type\":\"devin.acp\",\"event\":\"turn_complete\",\"stopReason\":\"end_turn\"}\n" +
            "{\"type\":\"codeybox.stderr\",\"text\":\"just a warning\"}");

        Assert.Null(error);
    }

    [Fact]
    public void Extract_MalformedEnvelopeLine_TreatedAsPlainText()
    {
        // A line starting with '{' that is not valid JSON must not break the
        // scan; it simply is not an envelope and is checked as raw text.
        var error = DevinTerminalDiagnoser.TryExtractTerminalError(
            null,
            "{not json at all\n" +
            "{\"type\":\"codeybox.stderr\",\"text\":\"Error: real signal\"}");

        Assert.Equal("Error: real signal", error);
    }

    [Fact]
    public void Extract_OtherEnvelopeTypes_NotUnwrapped()
    {
        // A devin.acp envelope's fields must not be scanned for the Error:
        // prefix — only codeybox.stderr text is unwrapped.
        var error = DevinTerminalDiagnoser.TryExtractTerminalError(
            null,
            "{\"type\":\"devin.acp\",\"event\":\"turn_error\",\"message\":\"Error: inner field\"}");

        Assert.Null(error);
    }

    [Fact]
    public void Extract_LongErrorLine_CappedAtDiagnosticBound()
    {
        var longError = "Error: " + new string('x', DevinTerminalDiagnoser.MaxDiagnosticChars + 50);

        var error = DevinTerminalDiagnoser.TryExtractTerminalError(longError, null);

        Assert.NotNull(error);
        Assert.Equal(DevinTerminalDiagnoser.MaxDiagnosticChars + 1, error.Length);
        Assert.EndsWith("…", error, StringComparison.Ordinal);
    }
}
