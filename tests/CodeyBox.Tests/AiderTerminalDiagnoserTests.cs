using CodeyBox.Agents.Aider;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AiderTerminalDiagnoser"/> using the terminal shapes
/// verified against aider 0.86.2: exit-0 runs whose only failure signal is the
/// verbatim litellm exception relay plus the trailing authenticate sentence.
/// </summary>
public sealed class AiderTerminalDiagnoserTests
{
    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(AiderTerminalDiagnoser.TryExtractTerminalError(null));
        Assert.Null(AiderTerminalDiagnoser.TryExtractTerminalError(""));
        Assert.Null(AiderTerminalDiagnoser.TryExtractTerminalError("   "));
    }

    [Fact]
    public void HealthyRun_ReturnsNull()
    {
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "Tokens: 766 sent, 905 received.\n" +
            "\n" +
            "pong2.txt\n" +
            "Applied edit to pong2.txt\n";

        Assert.Null(AiderTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void AuthRelay_ExtractsFirstErrorLine()
    {
        // Live shape from the aider 0.86.2 auth probe (bogus OpenRouter key).
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "litellm.AuthenticationError: AuthenticationError: OpenrouterException - \n" +
            "{\"error\":{\"message\":\"Missing Authentication header\",\"code\":401}}\n" +
            "The API provider is not able to authenticate you. Check your API key.\n";

        var result = AiderTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(result);
        Assert.Contains("litellm.AuthenticationError", result);
    }

    [Fact]
    public void AuthenticateSentenceAlone_Extracted()
    {
        const string stdout =
            "Aider v0.86.2\n" +
            "The API provider is not able to authenticate you. Check your API key.\n";

        var result = AiderTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(result);
        Assert.Contains("not able to authenticate you", result);
    }

    [Fact]
    public void ModelOutputDiscussingErrors_NotExtracted()
    {
        // Reviewed code mentioning errors must not read as a terminal failure:
        // only the litellm relay prefix and the authenticate sentence qualify.
        const string stdout =
            "Fixed the AuthenticationError handling in client.py\n" +
            "Tokens: 100 sent, 50 received.\n" +
            "Applied edit to client.py\n";

        Assert.Null(AiderTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void LongErrorLine_TruncatedToCap()
    {
        var stdout = "litellm.RateLimitError: " + new string('x', 1000) + "\n";

        var result = AiderTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(result);
        Assert.True(result!.Length <= AiderTerminalDiagnoser.MaxDiagnosticChars + 1);
    }
}
