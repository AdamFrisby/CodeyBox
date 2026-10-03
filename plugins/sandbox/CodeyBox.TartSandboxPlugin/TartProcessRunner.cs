using System.Diagnostics;
using System.Text;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>One captured host-process invocation.</summary>
public sealed record TartProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>One host-process invocation: argv array, never a shell string.</summary>
public sealed record TartProcessSpec(
    string Executable,
    IReadOnlyList<string> Argv,
    string? Stdin,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null);

/// <summary>A detached host process (a running Tart VM's foreground CLI).</summary>
public interface ITartDetachedProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    void Kill();
}

/// <summary>
/// Spawns host processes for the Tart provider: the <c>tart</c> CLI plus the
/// guest SSH transport. The interface keeps every invocation an argv array
/// (never a concatenated shell string) and every secret in the environment
/// block or stdin pipe (never argv), so fakes can emulate the whole host
/// surface without spawning anything.
/// </summary>
public interface ITartProcessRunner
{
    /// <summary>
    /// Runs one short control-plane process to completion, capturing bounded
    /// stdout/stderr and streaming chunks as they arrive. Kills the process on
    /// timeout or cancellation. Never throws for a non-zero exit — the result
    /// carries it and the caller classifies it.
    /// </summary>
    Task<TartProcessResult> RunAsync(
        TartProcessSpec spec,
        Action<string>? stdoutChunk,
        Action<string>? stderrChunk,
        int maxOutputBytes,
        CancellationToken ct);

    /// <summary>Starts a long-lived foreground process (<c>tart run</c>) without waiting for it.</summary>
    ITartDetachedProcess StartDetached(TartProcessSpec spec);
}

/// <summary>Production <see cref="ITartProcessRunner"/> over <see cref="Process"/>.</summary>
public sealed class SystemTartProcessRunner : ITartProcessRunner
{
    public async Task<TartProcessResult> RunAsync(
        TartProcessSpec spec,
        Action<string>? stdoutChunk,
        Action<string>? stderrChunk,
        int maxOutputBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Executable);
        ArgumentNullException.ThrowIfNull(spec.Argv);
        if (maxOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));

        using var process = new Process();
        process.StartInfo.FileName = spec.Executable;
        foreach (var arg in spec.Argv)
            process.StartInfo.ArgumentList.Add(arg ?? string.Empty);
        process.StartInfo.RedirectStandardInput = spec.Stdin is not null;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        if (spec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in spec.ExtraEnvironment)
                process.StartInfo.Environment[key] = value;
        }

        try
        {
            if (!process.Start())
                throw new TartCliException(spec.Executable, spec.Argv, null, "unreachable", "process failed to start");
        }
        catch (Exception ex) when (ex is not TartCliException)
        {
            throw new TartCliException(spec.Executable, spec.Argv, null, "unreachable", Truncate(LastLine(ex.Message), 500), ex);
        }

        if (spec.Stdin is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(spec.Stdin.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillBestEffort(process);
                throw;
            }
            catch (IOException)
            {
                // The child exited before draining stdin; its exit code below carries the verdict.
            }
            process.StandardInput.Close();
        }

        var stdout = new OutputAccumulator(maxOutputBytes);
        var stderr = new OutputAccumulator(maxOutputBytes);
        var stdoutTask = PumpAsync(process.StandardOutput, text => { stdout.Append(text); stdoutChunk?.Invoke(text); }, ct);
        var stderrTask = PumpAsync(process.StandardError, text => { stderr.Append(text); stderrChunk?.Invoke(text); }, ct);

        try
        {
            using var timeoutCts = new CancellationTokenSource(spec.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillBestEffort(process);
            throw new TartCliException(spec.Executable, spec.Argv, null, "timeout", $"command exceeded its {spec.Timeout.TotalSeconds:F0}s deadline");
        }
        catch (OperationCanceledException)
        {
            KillBestEffort(process);
            throw;
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Stream pump cancelled after exit; captured output so far still stands.
        }

        return new TartProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public ITartDetachedProcess StartDetached(TartProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Executable);

        var process = new Process();
        process.StartInfo.FileName = spec.Executable;
        foreach (var arg in spec.Argv)
            process.StartInfo.ArgumentList.Add(arg ?? string.Empty);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        if (spec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in spec.ExtraEnvironment)
                process.StartInfo.Environment[key] = value;
        }

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new TartCliException(spec.Executable, spec.Argv, null, "unreachable", "detached process failed to start");
            }
        }
        catch (Exception ex) when (ex is not TartCliException)
        {
            process.Dispose();
            throw new TartCliException(spec.Executable, spec.Argv, null, "unreachable", Truncate(LastLine(ex.Message), 500), ex);
        }

        // Drain asynchronously so a chatty VM log can never block the guest on a full pipe.
        _ = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                while (await process.StandardOutput.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
            }
            catch (Exception)
            {
                // Best effort only; the VM lifecycle is driven through the CLI, not this pipe.
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                while (await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
            }
            catch (Exception)
            {
                // Best effort only; see above.
            }
        });

        return new SystemTartDetachedProcess(process);
    }

    private static async Task PumpAsync(TextReader reader, Action<string> onChunk, CancellationToken ct)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            onChunk(new string(buffer, 0, read));
    }

    private static void KillBestEffort(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best effort: the CLI call already failed; do not mask it.
        }
    }

    private static string LastLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return line;
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;
        return text[..max];
    }

    private sealed class OutputAccumulator
    {
        private readonly int _max;
        private readonly StringBuilder _builder = new();
        private int _chars;

        public OutputAccumulator(int max) => _max = max;

        public void Append(string text)
        {
            if (_chars >= _max)
                return;
            var room = _max - _chars;
            if (text.Length <= room)
            {
                _builder.Append(text);
                _chars += text.Length;
            }
            else
            {
                _builder.Append(text.AsSpan(0, room));
                _chars = _max;
            }
        }

        public override string ToString() => _builder.ToString();
    }

    private sealed class SystemTartDetachedProcess(Process process) : ITartDetachedProcess
    {
        public int Id => process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best effort; VM teardown goes through the CLI regardless.
            }
        }

        public void Dispose() => process.Dispose();
    }
}
