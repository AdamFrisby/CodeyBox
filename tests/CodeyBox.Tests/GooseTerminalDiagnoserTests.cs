using CodeyBox.Agents.Goose;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GooseTerminalDiagnoser"/> over recorded real goose
/// 1.50.1 output: the exit-0 provider error block, the plaintext missing-key
/// failure, and healthy/empty streams.
/// </summary>
public sealed class GooseTerminalDiagnoserTests
{
    [Fact]
    public void StructuredAuthError_Extracted()
    {
        // Recorded real shape (bad OpenRouter key): exit 0 with the cause
        // only in a content error block.
        const string stdout =
            "__( O)>  ● new session · openrouter nvidia/nemotron-3.5-lightning:free\n" +
            """{"type":"message","message":{"id":"msg_1","role":"assistant","created":1789551852,"content":[{"type":"error","kind":"authentication","message":"Ran into this error: Authentication error: Authentication failed for https://openrouter.ai/api/v1/chat/completions. Status: 401 Unauthorized. Response: Missing Authentication header."}],"metadata":{"userVisible":true,"agentVisible":false}}}""" + "\n" +
            """{"type":"complete","total_tokens":0,"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"cost_usd":0.0}""";

        var error = GooseTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(error);
        Assert.Contains("401 Unauthorized", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaintextMissingKeyError_Extracted()
    {
        // Recorded real shape (no key configured): exit 1 with a plaintext
        // pre-session line and no JSON frames.
        const string stdout =
            "error: Error Configuration value not found: OPENROUTER_API_KEY.\n" +
            "Please check your system keychain and run 'goose configure' again.\n";

        var error = GooseTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(error);
        Assert.Contains("OPENROUTER_API_KEY", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthyRun_ReturnsNull()
    {
        const string stdout =
            """{"type":"message","message":{"id":"gen-1","role":"assistant","created":1789551816,"content":[{"type":"text","text":"GOOSE_OK"}],"metadata":{"userVisible":true}}}""" + "\n" +
            """{"type":"complete","total_tokens":5329,"input_tokens":5257,"output_tokens":72,"cache_read_input_tokens":4352,"cache_write_input_tokens":0,"cost_usd":0.0}""";

        Assert.Null(GooseTerminalDiagnoser.TryExtractTerminalError(stdout));
    }

    [Fact]
    public void NullAndEmpty_ReturnNull()
    {
        Assert.Null(GooseTerminalDiagnoser.TryExtractTerminalError(null));
        Assert.Null(GooseTerminalDiagnoser.TryExtractTerminalError(string.Empty));
        Assert.Null(GooseTerminalDiagnoser.TryExtractTerminalError("   \n  "));
    }

    [Fact]
    public void LongError_IsTruncated()
    {
        var longMessage = new string('e', GooseTerminalDiagnoser.MaxDiagnosticChars + 200);
        var stdout = string.Concat(
            "{\"type\":\"message\",\"message\":{\"id\":\"m\",\"role\":\"assistant\",\"created\":1,",
            "\"content\":[{\"type\":\"error\",\"kind\":\"provider\",\"message\":\"",
            longMessage,
            "\"}],\"metadata\":{}}}");

        var error = GooseTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(error);
        Assert.True(error!.Length <= GooseTerminalDiagnoser.MaxDiagnosticChars + 1);
    }

    [Fact]
    public void ErrorBlockWithoutMessage_FallsBackToKind()
    {
        const string stdout =
            """{"type":"message","message":{"id":"m","role":"assistant","created":1,"content":[{"type":"error","kind":"rate_limit"}],"metadata":{}}}""";

        var error = GooseTerminalDiagnoser.TryExtractTerminalError(stdout);

        Assert.NotNull(error);
        Assert.Contains("rate_limit", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverThrows_OnHostileInput()
    {
        // Every hostile shape must return normally with null (no terminal
        // error), never throw and never fabricate a diagnostic.
        string? truncated = null;
        string? huge = null;
        string? wrongType = null;
        var ex = Record.Exception(() =>
        {
            truncated = GooseTerminalDiagnoser.TryExtractTerminalError("{\"type\":\"message\",\"message\":");
            huge = GooseTerminalDiagnoser.TryExtractTerminalError(new string('z', 100_000));
            wrongType = GooseTerminalDiagnoser.TryExtractTerminalError("{\"message\":{\"content\":[42]}}");
        });

        Assert.Null(ex);
        Assert.Null(truncated);
        Assert.Null(huge);
        Assert.Null(wrongType);
    }
}
