using System.Text;
using CodeyBox.HostProcess;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the output-limit contracts in <see cref="DefaultProcessRunner"/> that
/// agent-turn streaming relies on:
/// - with <c>killOnOutputLimit: false</c> every emitted chunk still reaches
/// the callbacks (the file sink observes the full volume) while the retained
/// prefix stays bounded;
/// - with <c>killOnOutputLimit: true</c> an unbounded over-cap process is
/// terminated by the runner;
/// - with <c>killOnOutputLimit: true</c> a process that cleanly exits (code 0)
/// before or during kill is faithfully reported with <see cref="ProcessRunResult.ExitCode"/> = 0
/// and <see cref="ProcessRunResult.Success"/> = true, while recording that the output limit was exceeded.
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(
            ["/bin/sh", "-c", "exec yes '0123456789abcdef'"],
            stdin: null,
            cts.Token,
            stdoutChunkCallback: chunk => streamedBytes += Encoding.UTF8.GetByteCount(chunk),
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096,
            killOnOutputLimit: true);

        Assert.True(result.StdoutLimitExceeded);
        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
    }

    /// <summary>
    /// When a process exceeds the configured output limit but terminates cleanly
    /// (with exit code 0) before the runner's limit-driven kill action takes effect,
    /// the runner correctly reports <see cref="ProcessRunResult.StdoutLimitExceeded"/> = true,
    /// <see cref="ProcessRunResult.ExitCode"/> = 0, and <see cref="ProcessRunResult.Success"/> = true.
    ///
    /// Architectural rationale:
    /// 1. <see cref="ProcessRunResult.ExitCode"/> reflects the true operating system exit code
    ///    reported for the process. Fabricating a non-zero exit code or synthetic error for an
    ///    already-reaped process that exited cleanly would violate contract fidelity.
    /// 2. <see cref="ProcessRunResult.Success"/> is defined as <c>ExitCode == 0 &amp;&amp; !StartFailed &amp;&amp; !ExecutionUnavailable</c>.
    /// 3. Stream bounds are tracked orthogonally through <see cref="ProcessRunResult.StdoutLimitExceeded"/>
    ///    and <see cref="ProcessRunResult.StderrLimitExceeded"/>. Callers requiring full untruncated
    ///    output check <c>!result.Success || result.StdoutLimitExceeded</c>.
    /// </summary>
    [Fact]
    public async Task KillingLimit_CleanExitBeforeKill_ReportsSuccessWithLimitExceeded()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var runner = new DefaultProcessRunner();
        var gateFile = Path.Combine(Path.GetTempPath(), $"codeybox-gate-{Guid.NewGuid():N}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var script = $"printf 'pid=%s\\n' $$; count=0; while [ ! -f '{gateFile}' ] && [ $count -lt 500 ]; do sleep 0.01; count=$((count+1)); done; yes '0123456789abcdef' | head -c 5000; exit 0";

            var callbackInvoked = false;
            var result = await runner.RunAsync(
                ["/bin/sh", "-c", script],
                stdin: null,
                cts.Token,
                stdoutChunkCallback: chunk =>
                {
                    if (callbackInvoked)
                        return;

                    if (chunk.StartsWith("pid=", StringComparison.Ordinal))
                    {
                        var newline = chunk.IndexOf('\n');
                        var pidStr = newline >= 0 ? chunk[4..newline] : chunk[4..];
                        if (int.TryParse(pidStr, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                        {
                            callbackInvoked = true;
                            using var child = System.Diagnostics.Process.GetProcessById(pid);
                            File.WriteAllText(gateFile, "go");
                            child.WaitForExit(5000);
                        }
                    }
                },
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096,
                killOnOutputLimit: true);

            Assert.True(callbackInvoked, "Callback should have observed the child PID.");
            Assert.True(result.StdoutLimitExceeded);
            Assert.True(result.Success);
            Assert.Equal(0, result.ExitCode);
            Assert.True(Encoding.UTF8.GetByteCount(result.Stdout) <= 4096,
                $"Retained {Encoding.UTF8.GetByteCount(result.Stdout)} bytes, bound 4096");
        }
        finally
        {
            try
            {
                File.Delete(gateFile);
            }
            catch
            {
                // Ignore cleanup failures of temp gate file.
            }
        }
    }
}
