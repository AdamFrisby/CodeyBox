using System.Text;
using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the non-killing output-limit contract in
/// <see cref="DefaultProcessRunner"/> that agent-turn streaming relies on:
/// with <c>killOnOutputLimit: false</c> every emitted chunk still reaches
/// the callbacks (the file sink observes the full volume) while the retained
/// prefix stays bounded; with killing enabled the process is terminated.
/// </summary>
public sealed class DefaultProcessRunnerStreamingTests
{
    [Fact]
    public async Task NonKillingLimit_StreamsFullVolumeWithBoundedRetention()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = new DefaultProcessRunner();
        var streamed = new StringBuilder();
        const int emittedBytes = 100_000;

        var result = await runner.RunAsync(
            ["/bin/sh", "-c", $"yes '0123456789abcdef' | head -c {emittedBytes}"],
            stdin: null,
            CancellationToken.None,
            stdoutChunkCallback: chunk => streamed.Append(chunk),
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096,
            killOnOutputLimit: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(emittedBytes, Encoding.UTF8.GetByteCount(streamed.ToString()));
        Assert.True(Encoding.UTF8.GetByteCount(result.Stdout) <= 4096,
            $"retained {Encoding.UTF8.GetByteCount(result.Stdout)} bytes, bound 4096");
        Assert.True(result.StdoutLimitExceeded);
        Assert.StartsWith(result.Stdout, streamed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillingLimit_TerminatesOverCapProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = new DefaultProcessRunner();
        var streamedBytes = 0;

        var result = await runner.RunAsync(
            ["/bin/sh", "-c", "yes '0123456789abcdef' | head -c 100000"],
            stdin: null,
            CancellationToken.None,
            stdoutChunkCallback: chunk => streamedBytes += Encoding.UTF8.GetByteCount(chunk),
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096,
            killOnOutputLimit: true);

        Assert.True(result.StdoutLimitExceeded);
        Assert.False(result.Success);
    }
}
