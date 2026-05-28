using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="OpencodeModelListProbe"/> parsing and CLI outcomes.
/// </summary>
public sealed class OpencodeModelListProbeTests
{
    private static readonly string[] ExpectedModelIds =
    [
        "opencode/big-pickle",
        "opencode/deepseek-v4-flash-free",
        "opencode/mimo-v2.5-free",
        "opencode/nemotron-3-super-free",
        "opencode-go/deepseek-v4-flash",
        "opencode-go/deepseek-v4-pro",
        "opencode-go/glm-5",
        "opencode-go/glm-5.1",
        "opencode-go/kimi-k2.5",
        "opencode-go/kimi-k2.6",
        "opencode-go/mimo-v2.5",
        "opencode-go/mimo-v2.5-pro",
        "opencode-go/minimax-m2.5",
        "opencode-go/minimax-m2.7",
        "opencode-go/qwen3.5-plus",
        "opencode-go/qwen3.6-plus",
        "opencode-go/qwen3.7-max",
    ];

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Opencode", "opencode-models.redacted.txt");

    [Fact]
    public void Kind_IsOpencode()
        => Assert.Equal(AgentKind.Opencode, new OpencodeModelListProbe().Kind);

    [Fact]
    public void ParseModelsOutput_Fixture_YieldsExactlySeventeenModelIds()
    {
        var stdout = File.ReadAllText(FixturePath);
        var ids = OpencodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(ExpectedModelIds, ids);
    }

    [Fact]
    public void ParseModelsOutput_DropsGarbageLines()
    {
        var stdout = """
            INFO: cache hit
            opencode/big-pickle
            Loading providers...
            opencode-go/glm-5
            """;

        var ids = OpencodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(["opencode/big-pickle", "opencode-go/glm-5"], ids);
    }

    [Fact]
    public async Task GetModelListAsync_EmptyStdout_ReturnsFailed()
    {
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(0, "", ""));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
        Assert.Contains("no models parsed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetModelListAsync_NonZeroExit_ReturnsFailed()
    {
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(1, "opencode/foo\n", "err"));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
        Assert.Contains("exited 1", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetModelListAsync_CliNotFound_ReturnsFailed()
    {
        var probe = new OpencodeModelListProbe(new ThrowingOpencodeCliRunner(new FileNotFoundException("opencode")));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("opencode CLI not found", result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_Success_FromStubRunner()
    {
        var stdout = File.ReadAllText(FixturePath);
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(0, stdout, ""));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(ExpectedModelIds, result.ModelIds);
    }

    private sealed class StubOpencodeCliRunner(int exitCode, string stdout, string stderr) : IOpencodeCliRunner
    {
        public Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct) =>
            Task.FromResult(new OpencodeCliRunResult(exitCode, stdout, stderr));
    }

    private sealed class ThrowingOpencodeCliRunner(Exception ex) : IOpencodeCliRunner
    {
        public Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct) =>
            throw ex;
    }
}
