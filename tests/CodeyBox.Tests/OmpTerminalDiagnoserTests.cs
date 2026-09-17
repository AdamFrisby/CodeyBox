using System.Text.Json;
using CodeyBox.Agents.Omp;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OmpTerminalDiagnoser"/>: structured-error lifting
/// from the <c>message</c> envelope and the omp-specific <c>messages</c>
/// array envelope, the stderr plaintext pre-session failure, and the healthy
/// null path. Fixtures are recorded real frames from omp 18.2.2 (provider
/// key-id tails redacted — assertions only touch the stable refusal text).
/// </summary>
public sealed class OmpTerminalDiagnoserTests
{
    [Fact]
    public void Extract_MessageEndError_ReturnsFirstErrorMessage()
    {
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit).\"}}\n" +
            "{\"type\":\"turn_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit).\"},\"toolResults\":[]}";

        var error = OmpTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("Key limit exceeded", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_AgentEndMessagesArrayError_ReturnsNestedError()
    {
        // OMP's agent_end repeats the failed assistant message inside a
        // messages array (with isTerminal: true) rather than pi's message
        // envelope — recorded real shape from the paid-model refusal.
        const string stdout =
            "{\"type\":\"agent_end\",\"messages\":[{\"role\":\"user\",\"content\":[]}," +
            "{\"role\":\"assistant\",\"content\":[],\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit).\"}],\"isTerminal\":true}";

        var error = OmpTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.NotNull(error);
        Assert.Contains("Key limit exceeded", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_MissingKeyStderrCrash_ReturnsPlaintextLine()
    {
        // No usable key: stdout carries only the session header; the cause
        // is the bun crash line on stderr.
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"timestamp\":\"2026-09-16T23:49:32.601Z\",\"cwd\":\"/tmp/omptest\"}";
        const string stderr = "466349 | ` + `Use /login, set an API key environment variable.\nerror: No API key found for anthropic.";

        var error = OmpTerminalDiagnoser.TryExtractTerminalError(stdout, stderr);

        Assert.NotNull(error);
        Assert.Contains("No API key found", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_HealthyRun_ReturnsNull()
    {
        // Recorded real success frames: stopReason stop with usage, no
        // errorMessage anywhere.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[],\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":19200,\"output\":45,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":19245}," +
            "\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[{\"role\":\"assistant\",\"stopReason\":\"stop\"}],\"isTerminal\":true}";

        Assert.Null(OmpTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty));
    }

    [Fact]
    public void Extract_EmptyResponse_ReturnsNull()
    {
        Assert.Null(OmpTerminalDiagnoser.TryExtractTerminalError(stdout: string.Empty, stderr: string.Empty));
        Assert.Null(OmpTerminalDiagnoser.TryExtractTerminalError(stdout: null, stderr: null));
    }

    [Fact]
    public void Extract_MalformedLines_SkippedWithoutThrowing()
    {
        const string stdout =
            "not json at all\n" +
            "{\"type\":\"message_end\", truncated\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"error\",\"errorMessage\":\"boom\"}}";

        var error = OmpTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.Equal("boom", error);
    }

    [Fact]
    public void Extract_LongErrorMessage_TruncatedToCap()
    {
        var longMessage = new string('x', OmpTerminalDiagnoser.MaxDiagnosticChars + 100);
        var stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"error\"," +
            "\"errorMessage\":\"" + longMessage + "\"}}";

        var error = OmpTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: string.Empty);

        Assert.NotNull(error);
        Assert.True(error.Length <= OmpTerminalDiagnoser.MaxDiagnosticChars + 1);
    }
}
