using CodeyBox.Agents.Opencode;
using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies <see cref="DefaultOpencodeCliRunner"/> argv wiring via <see cref="IProcessRunner"/>.
/// </summary>
public sealed class DefaultOpencodeCliRunnerTests
{
    [Fact]
    public async Task RunModelsAsync_InvokesBinaryWithModelsArgument()
    {
        var runner = new RecordingProcessRunner();
        var cli = new DefaultOpencodeCliRunner(runner);

        _ = await cli.RunModelsAsync("opencode", CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("opencode", call[0]);
        Assert.Equal("models", call[1]);
    }

    [Fact]
    public async Task RunModelsAsync_PassesMinimalEnvironment()
    {
        var runner = new RecordingProcessRunner();
        var cli = new DefaultOpencodeCliRunner(runner);

        _ = await cli.RunModelsAsync("opencode", CancellationToken.None);

        var env = Assert.Single(runner.Environments);
        Assert.NotNull(env);
        foreach (var key in env.Keys)
            Assert.Contains(key, new[] { "PATH", "HOME", "XDG_CONFIG_HOME" });
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> Environments { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(argv.ToArray());
            Environments.Add(environment);
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }
    }
}
