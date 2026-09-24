using CodeyBox.Agents.Unreal;

namespace CodeyBox.Tests;

public sealed class UnrealTerminalDiagnoserTests
{
    [Fact]
    public void TryExtractTerminalError_FromStdoutJson_ReturnsMessage()
    {
        const string stdout =
            """
            {"Sequence":1,"RecordedAt":"2026-09-24T17:09:01Z","Kind":"input","Data":{}}
            {"type":"error","message":"UNREAL_HARNESS_LLM_API_KEY or OPENAI_API_KEY must be set"}
            """;

        var diag = UnrealTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: null);

        Assert.Equal("UNREAL_HARNESS_LLM_API_KEY or OPENAI_API_KEY must be set", diag);
    }

    [Fact]
    public void TryExtractTerminalError_FromStderrLine_ReturnsMessage()
    {
        const string stderr = "unreal-agent-runner: provider rate limit exceeded (429)";

        var diag = UnrealTerminalDiagnoser.TryExtractTerminalError(stdout: null, stderr);

        Assert.Equal("provider rate limit exceeded (429)", diag);
    }

    [Fact]
    public void TryExtractTerminalError_LongMessage_TruncatedTo500Chars()
    {
        var longMsg = new string('x', 600);
        var stdout = $"{{\"type\":\"error\",\"message\":\"{longMsg}\"}}";

        var diag = UnrealTerminalDiagnoser.TryExtractTerminalError(stdout, stderr: null);

        Assert.NotNull(diag);
        Assert.Equal(500, diag.Length);
    }

    [Fact]
    public void TryExtractTerminalError_NoError_ReturnsNull()
    {
        const string stdout = "{\"Sequence\":1,\"Kind\":\"model_response\",\"Data\":{}}";
        const string stderr = "some informative log line";

        var diag = UnrealTerminalDiagnoser.TryExtractTerminalError(stdout, stderr);

        Assert.Null(diag);
    }
}
