using System.Text;
using DiagProcess = System.Diagnostics.Process;

namespace CodeyBox.HostProcess;

/// <summary>Default <see cref="IProcessRunner"/> using a host OS process.</summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    private const int ReadBufferChars = 4096;

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
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);

        if (environment is not null)
        {
            psi.EnvironmentVariables.Clear();
            foreach (var (key, value) in environment)
                psi.EnvironmentVariables[key] = value;
        }

        using var p = new DiagProcess { StartInfo = psi };
        if (!p.Start())
            return new ProcessRunResult(1, "", "", StartFailed: true);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var limitOutput = maxStdoutBytes.HasValue || maxStderrBytes.HasValue;
        var streamChunks = stdoutChunkCallback is not null || stderrChunkCallback is not null;
        if (streamChunks && !limitOutput)
        {
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data + "\n";
                stdout.Append(line);
                stdoutChunkCallback?.Invoke(line);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data + "\n";
                stderr.Append(line);
                stderrChunkCallback?.Invoke(line);
            };
        }

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        Task<LimitedReadResult>? limitedStdoutTask = null;
        Task<LimitedReadResult>? limitedStderrTask = null;
        if (streamChunks && !limitOutput)
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        else if (limitOutput)
        {
            void KillForLimit()
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            }

            limitedStdoutTask = ReadLimitedAsync(
                p.StandardOutput,
                maxStdoutBytes,
                stdoutChunkCallback,
                killOnOutputLimit ? KillForLimit : null,
                ct);
            limitedStderrTask = ReadLimitedAsync(
                p.StandardError,
                maxStderrBytes,
                stderrChunkCallback,
                killOnOutputLimit ? KillForLimit : null,
                ct);
        }
        else
        {
            stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            stderrTask = p.StandardError.ReadToEndAsync(ct);
        }

        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            p.StandardInput.Close();
        }

        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (stdoutTask is not null && stderrTask is not null)
            return new ProcessRunResult(p.ExitCode, await stdoutTask, await stderrTask);
        if (limitedStdoutTask is not null && limitedStderrTask is not null)
        {
            var stdoutResult = await limitedStdoutTask.ConfigureAwait(false);
            var stderrResult = await limitedStderrTask.ConfigureAwait(false);
            return new ProcessRunResult(
                p.ExitCode,
                stdoutResult.Text,
                stderrResult.Text,
                stdoutResult.LimitExceeded,
                stderrResult.LimitExceeded);
        }

        return new ProcessRunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<LimitedReadResult> ReadLimitedAsync(
        StreamReader reader,
        int? maxBytes,
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
            if (maxBytes is { } limit)
            {
                if (limitExceeded)
                    continue;

                var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
                if (totalBytes + chunkBytes > limit)
                {
                    var remaining = Math.Max(0, limit - totalBytes);
                    if (remaining > 0)
                    {
                        var truncated = TakeUtf8Prefix(chunk, remaining);
                        output.Append(truncated);
                        chunkCallback?.Invoke(truncated);
                    }

                    totalBytes = limit;
                    limitExceeded = true;
                    onLimitExceeded?.Invoke();
                    if (onLimitExceeded is not null)
                        return new LimitedReadResult(output.ToString(), LimitExceeded: true);
                    continue;
                }

                totalBytes += chunkBytes;
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
