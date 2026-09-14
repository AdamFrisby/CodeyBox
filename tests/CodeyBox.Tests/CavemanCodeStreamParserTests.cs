using System.Text.Json;
using CodeyBox.Agents.CavemanCode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CavemanCodeStreamParserTests
{
    [Fact]
    public void TryClaim_JsonLine_ReturnsFalse()
    {
        // Plaintext -p output: the parser claims nothing by shape so the
        // plaintext-fallback summariser handles the run (opencode parity).
        var parser = new CavemanCodeStreamParser();
        using var doc = JsonDocument.Parse("""{"type":"message","content":"hi"}""");

        Assert.False(parser.TryClaim(doc.RootElement));
    }
}

public sealed class DefaultCavemanCodeCliRunnerTests
{
    [Fact]
    public async Task RunListModelsAsync_StartFailed_MapsToExitOneEmpty()
    {
        var runner = new DefaultCavemanCodeCliRunner(new StartFailingProcessRunner());

        var result = await runner.RunListModelsAsync("caveman-code", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    private sealed class StartFailingProcessRunner : CodeyBox.HostProcess.IProcessRunner
    {
        public Task<CodeyBox.HostProcess.ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true) =>
            Task.FromResult(new CodeyBox.HostProcess.ProcessRunResult(1, "", "", StartFailed: true));
    }
}
