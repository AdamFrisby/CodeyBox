using System.ComponentModel;
using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="OpencodeModelListProbe"/> parsing and CLI outcomes.
/// </summary>
public sealed class OpencodeModelListProbeTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Opencode", "opencode-models.redacted.txt");

    private static string[] ExpectedModelIds =>
        File.ReadAllLines(FixturePath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

    [Fact]
    public void Kind_IsOpencode()
        => Assert.Equal(
            AgentKind.Opencode,
            new OpencodeModelListProbe(new StubOpencodeCliRunner(0, "", "")).Kind);

    [Fact]
    public void ParseModelsOutput_Fixture_YieldsExactlySeventeenModelIds()
    {
        var stdout = File.ReadAllText(FixturePath);
        var ids = OpencodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(ExpectedModelIds, ids);
    }

    [Fact]
    public void ParseModelsOutput_TruncatesAtMaxModelIds()
    {
        var lines = Enumerable.Range(0, OpencodeModelListProbe.MaxModelIds + 10)
            .Select(i => $"opencode/model{i}");
        var stdout = string.Join('\n', lines);

        var ids = OpencodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(OpencodeModelListProbe.MaxModelIds, ids.Count);
        Assert.Equal("opencode/model0", ids[0]);
        Assert.Equal($"opencode/model{OpencodeModelListProbe.MaxModelIds - 1}", ids[^1]);
    }

    [Fact]
    public void ParseModelsOutput_DropsGarbageLines()
    {
        var stdout = """
            INFO: cache hit
            opencode/big-pickle
            deepseek-v4-flash
            Loading providers...
            opencode-go/glm-5
            """;

        var ids = OpencodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(["opencode/big-pickle", "deepseek-v4-flash", "opencode-go/glm-5"], ids);
    }

    // Boundary documentation for the regex broadening (provider/model → bare
    // single token allowed). Anything matching `^[a-z0-9_.-]+(?:/[a-z0-9_.-]+)?$`
    // is accepted; lone lowercase words like "loading" / "models" / "provider"
    // therefore parse as ids when the CLI prints them on their own line. We
    // do NOT have a semantic blocklist for those — accepting one alongside
    // real ids would produce a phantom routing entry that AgentClassConfigValidator
    // rejects downstream as an unknown ModelId. This test pins the boundary so
    // a future tightening of the regex doesn't silently drop real bare-id
    // models like `deepseek-v4-flash` (the case the broadening exists to allow).
    [Theory]
    [InlineData("deepseek-v4-flash")]
    [InlineData("loading")]
    [InlineData("models")]
    [InlineData("provider")]
    [InlineData("providers")]
    [InlineData("ready")]
    [InlineData("done")]
    public void ParseModelsOutput_LowercaseSingleToken_ParsesAsBareId(string line)
    {
        var ids = OpencodeModelListProbe.ParseModelsOutput(line);
        Assert.Equal([line], ids);
    }

    // Coverage for inputs the parser still rejects after the broadening — guards
    // against a future change that further loosens the regex and lets banner /
    // preamble shapes through that the validator can no longer interpret.
    [Theory]
    [InlineData("INFO: cache hit")]               // colon + space + uppercase
    [InlineData("Loading providers...")]          // uppercase + space + ellipsis
    [InlineData("loading providers")]              // lowercase but multi-token
    [InlineData("Loading")]                        // single uppercased token
    [InlineData("opencode/Big-Pickle")]            // uppercase inside id
    [InlineData("opencode//double-slash")]         // malformed separator
    [InlineData("opencode/foo/bar")]               // too many path segments
    [InlineData("=== header ===")]                 // banner punctuation
    [InlineData("* opencode/foo")]                 // bullet-prefixed
    public void ParseModelsOutput_RejectsNonIdShapes(string line)
    {
        var ids = OpencodeModelListProbe.ParseModelsOutput(line);
        Assert.Empty(ids);
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
    public async Task GetModelListAsync_StartFailedExit_ReturnsFailedToStart()
    {
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(1, "", ""));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("opencode CLI failed to start", result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_NonZeroExit_ReturnsFailedWithoutStderrInReason()
    {
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(1, "opencode/foo\n", "secret err"));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
        Assert.Equal("opencode models exited 1", result.FailureReason);
        Assert.DoesNotContain("secret", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetModelListAsync_NonZeroExit_EmptyStderr_ReturnsExitOnlyReason()
    {
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(2, "", ""));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("opencode models exited 2", result.FailureReason);
        Assert.Empty(result.ModelIds);
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
    public async Task GetModelListAsync_Win32Exception_ReturnsCliNotFound()
    {
        var probe = new OpencodeModelListProbe(
            new ThrowingOpencodeCliRunner(new Win32Exception(2, "No such file or directory")));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("opencode CLI not found", result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_Exit127_ReturnsCliNotFound()
    {
        var probe = new OpencodeModelListProbe(new StubOpencodeCliRunner(127, "", "opencode: not found"));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("opencode CLI not found", result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_GenericException_ReturnsFailedWithTypeName()
    {
        var probe = new OpencodeModelListProbe(new ThrowingOpencodeCliRunner(new IOException("disk full")));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("opencode models failed (IOException)", result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_Cancellation_ReturnsTimeout()
    {
        var probe = new OpencodeModelListProbe(new DelayingOpencodeCliRunner());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await probe.GetModelListAsync(cts.Token);

        Assert.Equal("timeout", result.FailureReason);
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

    [Fact]
    public async Task GetModelListAsync_ConstructorBinary_PassedToCliRunner()
    {
        var runner = new CapturingOpencodeCliRunner(0, "", "");
        var probe = new OpencodeModelListProbe(runner, binary: "/custom/opencode");

        _ = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("/custom/opencode", runner.LastBinary);
    }

    [Fact]
    public async Task GetModelListAsync_CodeyboxOpencodeBinaryEnv_ResolvedWhenBinaryOmitted()
    {
        var prior = Environment.GetEnvironmentVariable("CODEYBOX_OPENCODE_BINARY");
        try
        {
            Environment.SetEnvironmentVariable("CODEYBOX_OPENCODE_BINARY", "/env/opencode");
            var runner = new CapturingOpencodeCliRunner(0, "", "");
            var probe = new OpencodeModelListProbe(runner);

            _ = await probe.GetModelListAsync(CancellationToken.None);

            Assert.Equal("/env/opencode", runner.LastBinary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_OPENCODE_BINARY", prior);
        }
    }

    private sealed class StubOpencodeCliRunner(int exitCode, string stdout, string stderr) : IOpencodeCliRunner
    {
        public Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct) =>
            Task.FromResult(new OpencodeCliRunResult(exitCode, stdout, stderr));
    }

    private sealed class CapturingOpencodeCliRunner(int exitCode, string stdout, string stderr) : IOpencodeCliRunner
    {
        public string? LastBinary { get; private set; }

        public Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct)
        {
            LastBinary = binary;
            return Task.FromResult(new OpencodeCliRunResult(exitCode, stdout, stderr));
        }
    }

    private sealed class ThrowingOpencodeCliRunner(Exception ex) : IOpencodeCliRunner
    {
        public Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct) =>
            throw ex;
    }

    private sealed class DelayingOpencodeCliRunner : IOpencodeCliRunner
    {
        public async Task<OpencodeCliRunResult> RunModelsAsync(string binary, CancellationToken ct)
        {
            await Task.Delay(60_000, ct).ConfigureAwait(false);
            return new OpencodeCliRunResult(0, "", "");
        }
    }
}
