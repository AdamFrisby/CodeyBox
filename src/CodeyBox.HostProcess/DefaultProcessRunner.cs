using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DiagProcess = System.Diagnostics.Process;

namespace CodeyBox.HostProcess;

/// <summary>
/// Default <see cref="IProcessRunner"/> using a host OS process. On Linux,
/// <c>setsid</c> from util-linux is required so cancellation owns and verifies
/// teardown of the complete process group, including an orphaned descendant.
/// </summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    internal const int DefaultMaxOutputBytes = 16 * 1024 * 1024;
    private const int ReadBufferChars = 4096;
    private const int SignalKill = 9;
    private const int SignalProbe = 0;
    private const int NoSuchProcess = 3;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessRunResult> RunAsync(
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
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
            throw new ArgumentException("Process argv must contain an executable.", nameof(argv));
        if (maxStdoutBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(maxStdoutBytes));
        if (maxStderrBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(maxStderrBytes));

        var effectiveMaxStdoutBytes = maxStdoutBytes ?? DefaultMaxOutputBytes;
        var effectiveMaxStderrBytes = maxStderrBytes ?? DefaultMaxOutputBytes;

        var isolatedLinuxProcessGroup = OperatingSystem.IsLinux();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = isolatedLinuxProcessGroup ? ResolveSetsidPath() : argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isolatedLinuxProcessGroup)
        {
            // The freshly spawned setsid process is not a process-group leader,
            // so setsid replaces itself with the requested executable while
            // retaining its PID as the new session/process-group ID. This lets
            // cancellation kill descendants even when the root exits first.
            psi.ArgumentList.Add("--");
            foreach (var argument in argv)
                psi.ArgumentList.Add(argument);
        }
        else
        {
            for (var i = 1; i < argv.Count; i++)
                psi.ArgumentList.Add(argv[i]);
        }

        if (environment is not null)
        {
            psi.EnvironmentVariables.Clear();
            foreach (var (key, value) in environment)
                psi.EnvironmentVariables[key] = value;
        }

        using var p = new DiagProcess { StartInfo = psi };
        if (!p.Start())
            return new ProcessRunResult(1, "", "", StartFailed: true);

        Task<LimitedReadResult>? limitedStdoutTask;
        Task<LimitedReadResult>? limitedStderrTask;
        Exception? outputLimitTerminationFailure = null;
        var outputLimitTerminationRequested = 0;
        void KillForLimit()
        {
            Interlocked.Exchange(ref outputLimitTerminationRequested, 1);
            try
            {
                if (isolatedLinuxProcessGroup)
                {
                    var result = SendSignal(-p.Id, SignalKill);
                    var error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
                    if (result != 0 && error != NoSuchProcess)
                    {
                        throw new Win32Exception(
                            error,
                            "Unable to terminate the isolated host process group after an output limit.");
                    }
                }
                else if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref outputLimitTerminationFailure, ex, null);
            }
        }

        limitedStdoutTask = ReadLimitedAsync(
            p.StandardOutput,
            effectiveMaxStdoutBytes,
            stdoutChunkCallback,
            killOnOutputLimit ? KillForLimit : null,
            ct);
        limitedStderrTask = ReadLimitedAsync(
            p.StandardError,
            effectiveMaxStderrBytes,
            stderrChunkCallback,
            killOnOutputLimit ? KillForLimit : null,
            ct);

        try
        {
            if (stdin is not null)
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
                await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                p.StandardInput.Close();
            }

            await p.WaitForExitAsync(ct).ConfigureAwait(false);

            if (Volatile.Read(ref outputLimitTerminationRequested) != 0)
            {
                var cleanupErrors = await VerifyOutputLimitTerminationAsync(
                    p,
                    isolatedLinuxProcessGroup,
                    outputLimitTerminationFailure,
                    limitedStdoutTask,
                    limitedStderrTask).ConfigureAwait(false);
                if (cleanupErrors.Count != 0)
                {
                    throw new AggregateException(
                        "Host process exceeded an output limit and its process-group teardown could not be fully verified.",
                        cleanupErrors);
                }
            }
            var stdoutResult = await limitedStdoutTask.ConfigureAwait(false);
            var stderrResult = await limitedStderrTask.ConfigureAwait(false);
            return new ProcessRunResult(
                p.ExitCode,
                stdoutResult.Text,
                stderrResult.Text,
                stdoutResult.LimitExceeded,
                stderrResult.LimitExceeded);
        }
        catch (Exception initiatingError)
        {
            var cleanupErrors = await TerminateAndDrainAsync(
                p,
                isolatedLinuxProcessGroup,
                stdoutTask: null,
                stderrTask: null,
                limitedStdoutTask,
                limitedStderrTask).ConfigureAwait(false);
            if (cleanupErrors.Count == 0)
                throw;
            throw new AggregateException(
                "Host process failed and its process-group teardown could not be fully verified.",
                [initiatingError, .. cleanupErrors]);
        }
    }

    private static async Task<IReadOnlyList<Exception>> VerifyOutputLimitTerminationAsync(
        DiagProcess process,
        bool isolatedLinuxProcessGroup,
        Exception? terminationFailure,
        Task<LimitedReadResult> stdoutTask,
        Task<LimitedReadResult> stderrTask)
    {
        var errors = new List<Exception>();
        if (terminationFailure is not null)
            errors.Add(terminationFailure);
        using var deadline = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            CloseRedirectedStreams(process, errors);
            errors.Add(new TimeoutException("Host process output readers remained open after output-limit termination."));
        }
        catch (Exception ex)
        {
            errors.Add(new InvalidOperationException("Host process output readers failed during output-limit termination.", ex));
        }

        if (isolatedLinuxProcessGroup
            && !await WaitForProcessGroupExitAsync(process.Id, deadline.Token).ConfigureAwait(false))
        {
            errors.Add(new TimeoutException("The isolated host process group still exists after output-limit termination."));
        }
        return errors;
    }

    private static async Task<IReadOnlyList<Exception>> TerminateAndDrainAsync(
        DiagProcess process,
        bool isolatedLinuxProcessGroup,
        Task<string>? stdoutTask,
        Task<string>? stderrTask,
        Task<LimitedReadResult>? limitedStdoutTask,
        Task<LimitedReadResult>? limitedStderrTask)
    {
        var errors = new List<Exception>();
        using var cleanupDeadline = new CancellationTokenSource(CleanupTimeout);

        if (isolatedLinuxProcessGroup)
        {
            var result = SendSignal(-process.Id, SignalKill);
            if (result != 0 && Marshal.GetLastPInvokeError() != NoSuchProcess)
            {
                errors.Add(new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Unable to terminate the isolated host process group."));
            }
        }
        else
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException("Unable to terminate the host process tree.", ex));
            }
        }

        try
        {
            await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add(new TimeoutException("The host process root did not exit within the cleanup deadline.", ex));
        }

        CloseRedirectedStreams(process, errors);
        if (!await ObserveOutputTasksBestEffortAsync(
                cleanupDeadline.Token,
                stdoutTask,
                stderrTask,
                limitedStdoutTask,
                limitedStderrTask).ConfigureAwait(false))
        {
            errors.Add(new TimeoutException("Host process output readers did not stop within the cleanup deadline."));
        }

        if (isolatedLinuxProcessGroup
            && !await WaitForProcessGroupExitAsync(process.Id, cleanupDeadline.Token).ConfigureAwait(false))
        {
            errors.Add(new TimeoutException("The isolated host process group still exists after termination."));
        }

        return errors;
    }

    private static void CloseRedirectedStreams(DiagProcess process, ICollection<Exception> errors)
    {
        if (process.StartInfo.RedirectStandardInput)
        {
            try
            {
                process.StandardInput.Dispose();
            }
            catch (IOException)
            {
                // A broken stdin pipe is commonly the initiating failure and
                // already proves that no writer handle remains to clean up.
            }
        }
        try
        {
            process.StandardOutput.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add(new InvalidOperationException("Unable to close redirected host stdout.", ex));
        }
        try
        {
            process.StandardError.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add(new InvalidOperationException("Unable to close redirected host stderr.", ex));
        }
    }

    private static async Task<bool> ObserveOutputTasksBestEffortAsync(
        CancellationToken ct,
        Task<string>? stdoutTask,
        Task<string>? stderrTask,
        Task<LimitedReadResult>? limitedStdoutTask,
        Task<LimitedReadResult>? limitedStderrTask)
    {
        var tasks = new List<Task>(4);
        if (stdoutTask is not null) tasks.Add(stdoutTask);
        if (stderrTask is not null) tasks.Add(stderrTask);
        if (limitedStdoutTask is not null) tasks.Add(limitedStdoutTask);
        if (limitedStderrTask is not null) tasks.Add(limitedStderrTask);
        if (tasks.Count == 0)
            return true;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            // Stream disposal and the initiating cancellation are expected to
            // fault pending readers. Awaiting here observes those failures.
            return true;
        }
    }

    private static async Task<bool> WaitForProcessGroupExitAsync(int processGroupId, CancellationToken ct)
    {
        while (true)
        {
            if (SendSignal(-processGroupId, SignalProbe) != 0)
                return Marshal.GetLastPInvokeError() == NoSuchProcess;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private static string ResolveSetsidPath()
    {
        if (File.Exists("/usr/bin/setsid"))
            return "/usr/bin/setsid";
        if (File.Exists("/bin/setsid"))
            return "/bin/setsid";
        throw new PlatformNotSupportedException(
            "Linux host process isolation requires setsid from util-linux at /usr/bin/setsid or /bin/setsid.");
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);

    private static async Task<LimitedReadResult> ReadLimitedAsync(
        StreamReader reader,
        int maxBytes,
        Action<string>? chunkCallback,
        Action? onLimitExceeded,
        CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[ReadBufferChars];
        var totalBytes = 0;
        var limitExceeded = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
                return new LimitedReadResult(output.ToString(), limitExceeded);

            var chunk = new string(buffer, 0, read);
            if (!limitExceeded)
            {
                var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
                if (chunkBytes > maxBytes - totalBytes)
                {
                    var remaining = Math.Max(0, maxBytes - totalBytes);
                    if (remaining > 0)
                    {
                        var truncated = TakeUtf8Prefix(chunk, remaining);
                        output.Append(truncated);
                        chunkCallback?.Invoke(truncated);
                    }

                    totalBytes = maxBytes;
                    limitExceeded = true;
                    onLimitExceeded?.Invoke();
                    if (onLimitExceeded is not null)
                        return new LimitedReadResult(output.ToString(), LimitExceeded: true);
                    continue;
                }

                totalBytes += chunkBytes;
            }
            else
            {
                continue;
            }

            output.Append(chunk);
            chunkCallback?.Invoke(chunk);
        }
    }

    private static string TakeUtf8Prefix(string value, int maxBytes)
    {
        var used = 0;
        for (var i = 0; i < value.Length;)
        {
            var charCount = char.IsHighSurrogate(value[i])
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1])
                    ? 2
                    : 1;
            var charBytes = Encoding.UTF8.GetByteCount(value.AsSpan(i, charCount));
            if (used + charBytes > maxBytes)
                return value[..i];
            used += charBytes;
            i += charCount;
        }

        return value;
    }

    private readonly record struct LimitedReadResult(string Text, bool LimitExceeded);
}
