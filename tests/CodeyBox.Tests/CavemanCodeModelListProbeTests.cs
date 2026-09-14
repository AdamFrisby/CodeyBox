using System.ComponentModel;
using CodeyBox.Agents.CavemanCode;
using CodeyBox.Core;
using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="CavemanCodeModelListProbe"/> parsing and CLI
/// outcomes. The table fixture is verbatim
/// <c>caveman-code --list-models</c> output captured against 0.65.2 (public
/// model catalog — no secrets to redact, suffix kept for convention).
/// </summary>
public sealed class CavemanCodeModelListProbeTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "CavemanCode", "caveman-code-list-models.redacted.txt");

    [Fact]
    public void Kind_IsCavemanCode()
        => Assert.Equal(
            AgentKind.CavemanCode,
            new CavemanCodeModelListProbe(new StubCavemanCodeCliRunner(0, "", "")).Kind);

    [Fact]
    public void ParseModelsOutput_Fixture_YieldsPrefixedAndBareIds()
    {
        var stdout = File.ReadAllText(FixturePath);
        var ids = CavemanCodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(
            new[]
            {
                "anthropic/claude-haiku-4-5", "claude-haiku-4-5",
                "anthropic/claude-opus-4-6", "claude-opus-4-6",
                "anthropic/claude-sonnet-4-6", "claude-sonnet-4-6",
                "openai/gpt-5-codex", "gpt-5-codex",
                "openai/gpt-5.5", "gpt-5.5",
            },
            ids);
    }

    [Fact]
    public void ParseModelsOutput_HeaderAndEmptyState_YieldNothing()
    {
        var stdout = """
            provider  model    context  max-out  thinking  images
            No models available. Set API keys in environment variables.
            """;

        Assert.Empty(CavemanCodeModelListProbe.ParseModelsOutput(stdout));
    }

    [Fact]
    public void ParseModelsOutput_SingleSpacedNoise_YieldsNothing()
    {
        // Columns are joined with 2+ spaces, so single-spaced process noise
        // (npm chatter, log lines) never yields two columns.
        var stdout = """
            added 241 packages in 1m
            INFO: cache hit for registry
            Loading providers...
            """;

        Assert.Empty(CavemanCodeModelListProbe.ParseModelsOutput(stdout));
    }

    [Fact]
    public void ParseModelsOutput_TruncatesAtMaxModelIds()
    {
        var lines = Enumerable.Range(0, CavemanCodeModelListProbe.MaxModelIds + 10)
            .Select(i => $"openai  model{i}  400K  128K  yes  yes");
        var stdout = string.Join('\n', lines);

        var ids = CavemanCodeModelListProbe.ParseModelsOutput(stdout);

        Assert.Equal(CavemanCodeModelListProbe.MaxModelIds, ids.Count);
    }

    [Fact]
    public async Task GetModelListAsync_NonZeroExit_Fails()
    {
        var probe = new CavemanCodeModelListProbe(new StubCavemanCodeCliRunner(1, "out", "boom"));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    [Fact]
    public async Task GetModelListAsync_Exit127_FailsAsNotFound()
    {
        var probe = new CavemanCodeModelListProbe(new StubCavemanCodeCliRunner(127, "", ""));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("caveman-code CLI not found", result.FailureReason);
    }

    [Fact]
    public async Task GetModelListAsync_FileNotFoundThrow_MapsToNotFound()
    {
        var probe = new CavemanCodeModelListProbe(new ThrowingCavemanCodeCliRunner(new FileNotFoundException()));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("caveman-code CLI not found", result.FailureReason);
    }

    [Fact]
    public async Task GetModelListAsync_EnoentThrow_MapsToNotFound()
    {
        var probe = new CavemanCodeModelListProbe(
            new ThrowingCavemanCodeCliRunner(new Win32Exception(2, "No such file or directory")));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Equal("caveman-code CLI not found", result.FailureReason);
    }

    [Fact]
    public async Task GetModelListAsync_Success_ParsesFixture()
    {
        var stdout = await File.ReadAllTextAsync(FixturePath);
        var probe = new CavemanCodeModelListProbe(new StubCavemanCodeCliRunner(0, stdout, ""));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Contains("openai/gpt-5.5", result.ModelIds);
        Assert.Contains("anthropic/claude-opus-4-6", result.ModelIds);
    }

    [Fact]
    public async Task DefaultCliRunner_StartFailed_MapsToFailureResult()
    {
        var process = new StartFailedProcessRunner();
        var runner = new DefaultCavemanCodeCliRunner(
            process,
            environment: new Dictionary<string, string>());

        var run = await runner.RunListModelsAsync("caveman-code", CancellationToken.None);

        Assert.Equal(new[] { "caveman-code", "--list-models" }, process.SeenArgv);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("", run.Stdout);
        Assert.Equal("", run.Stderr);
    }

    [Fact]
    public async Task GetModelListAsync_BinaryMissing_FailsGracefullyThroughRealRunner()
    {
        // Real runner + probe wiring with only the process boundary stubbed:
        // a missing binary must surface as a probe failure, never a throw
        // and never an empty success.
        var probe = new CavemanCodeModelListProbe(
            new DefaultCavemanCodeCliRunner(
                new StartFailedProcessRunner(),
                environment: new Dictionary<string, string>()));

        var result = await probe.GetModelListAsync(CancellationToken.None);

        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.ModelIds);
    }

    private sealed class StartFailedProcessRunner : IProcessRunner
    {
        public IReadOnlyList<string> SeenArgv { get; private set; } = Array.Empty<string>();

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            SeenArgv = argv;
            return Task.FromResult(new ProcessRunResult(1, "", "", StartFailed: true));
        }
    }

    private sealed class StubCavemanCodeCliRunner(int exitCode, string stdout, string stderr) : ICavemanCodeCliRunner
    {
        public Task<CavemanCodeCliRunResult> RunListModelsAsync(string binary, CancellationToken ct) =>
            Task.FromResult(new CavemanCodeCliRunResult(exitCode, stdout, stderr));
    }

    private sealed class ThrowingCavemanCodeCliRunner(Exception ex) : ICavemanCodeCliRunner
    {
        public Task<CavemanCodeCliRunResult> RunListModelsAsync(string binary, CancellationToken ct) =>
            throw ex;
    }
}
