using System.Diagnostics;
using System.Text;
using CodeyBox.Core;

namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Host process runner for the test-selection baseline producer. Commands run
/// as argv arrays with a working directory; stdout/stderr are capped BEFORE
/// unbounded buffering.
/// </summary>
public sealed class HostCommandRunner : IHostCommandRunner
{
    private const int ReadBufferChars = 4096;

    /// <summary>
    /// Upper bound on draining redirected output AFTER the child process
    /// exits. Everything the child itself wrote is already buffered in the
    /// pipes and drains immediately, but a detached grandchild (e.g. an
    /// MSBuild node-reuse server spawned by <c>dotnet build</c> /
    /// <c>dotnet msbuild</c>) inherits the pipe write ends and can keep them
    /// open for minutes. Waiting for plain EOF would hang until the command
    /// timeout, so stragglers get this grace window and no more.
    /// </summary>
    private static readonly TimeSpan PostExitDrainGrace = TimeSpan.FromSeconds(5);

    public async Task<HostCommandResult> RunAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        int maxStdoutChars,
        int maxStderrChars,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
            throw new ArgumentException("Process argv must contain an executable.", nameof(argv));
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (maxStdoutChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStdoutChars));
        if (maxStderrChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStderrChars));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
            throw new DirectoryNotFoundException($"Working directory '{cwd}' does not exist.");

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                psi.Environment[key] = value;
        }

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
                psi.Environment[key] = value;
        }

        var env = psi.Environment.Where(kvp => kvp.Value is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.Ordinal);
        // Pin a writable CLI home under /tmp, never under the measured
        // checkout — ApplyIfDotnetInvocation would otherwise create
        // <repo>/.dotnet-cli-home as a side effect of producing a baseline.
        if (DotnetCliHomeConventions.IsDotnetInvocation(argv))
        {
            var cliHomeRoot = Path.Combine(Path.GetTempPath(), "codeybox-test-selection-dotnet-home");
            Directory.CreateDirectory(cliHomeRoot);
            DotnetCliHomeConventions.ApplyIfDotnetInvocation(argv, cliHomeRoot, env);
        }

        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return new HostCommandResult(1, "", "Failed to start process.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, maxStdoutChars, timeoutCts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, maxStderrChars, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            // The child exited; bound the remaining drain (see
            // PostExitDrainGrace) rather than blocking on pipe EOF forever.
            timeoutCts.CancelAfter(PostExitDrainGrace);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new HostCommandResult(
                process.ExitCode,
                stdout.Text,
                stderr.Text,
                stdout.LimitExceeded,
                stderr.LimitExceeded);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"Process '{argv[0]}' exceeded the {timeout.TotalSeconds:0}s timeout.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<CappedRead> ReadCappedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[ReadBufferChars];
        var limitExceeded = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                    return new CappedRead(output.ToString(), limitExceeded);
                if (limitExceeded)
                    continue;
                var remaining = maxChars - output.Length;
                if (read > remaining)
                {
                    if (remaining > 0)
                        output.Append(buffer, 0, remaining);
                    limitExceeded = true;
                    continue;
                }

                output.Append(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // Command timeout, caller cancellation, or the post-exit drain
            // grace elapsed — keep whatever was already buffered. The caller
            // distinguishes the cases at WaitForExitAsync.
            return new CappedRead(output.ToString(), limitExceeded);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private readonly record struct CappedRead(string Text, bool LimitExceeded);
}
