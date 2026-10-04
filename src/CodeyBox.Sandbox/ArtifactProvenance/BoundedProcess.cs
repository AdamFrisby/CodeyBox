using System.Diagnostics;
using System.Text;

namespace CodeyBox.Sandbox.ArtifactProvenance;

/// <summary>
/// One subprocess invocation. Arguments travel as an argv array — never a
/// concatenated shell string — and the process starts with
/// <c>UseShellExecute=false</c>, so no shell ever interprets them.
/// </summary>
public sealed record ProcessSpec
{
    /// <summary>Binary name or absolute path (argv[0] resolution, no shell).</summary>
    public required string FileName { get; init; }

    /// <summary>Argument vector. File operands must be host-canonicalized absolute paths.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory, or null for the current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Per-invocation timeout. The process is killed on expiry.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Maximum bytes retained per captured stream.</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;
}

/// <summary>Bounded result of one subprocess invocation.</summary>
public sealed record ProcessResult
{
    /// <summary>Process exit code, or -1 when the process never started.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured stdout, truncated to the configured cap.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured stderr, truncated to the configured cap.</summary>
    public required string StandardError { get; init; }

    /// <summary>True when the timeout expired and the process was killed.</summary>
    public required bool TimedOut { get; init; }
}

/// <summary>
/// Runs verifier subprocesses. The production implementation inherits a
/// minimal environment (PATH only): verification credentials are never
/// injected here, and nothing from this runner reaches plugin or tool
/// processes.
/// </summary>
public interface IVerifierProcessRunner
{
    /// <summary>Runs <paramref name="spec"/> to completion, killing it on timeout or cancellation.</summary>
    ProcessResult Run(ProcessSpec spec, CancellationToken ct);
}

/// <summary>Production bounded subprocess runner: no shell, argv arrays, caps, timeouts.</summary>
public sealed class BoundedProcessRunner : IVerifierProcessRunner
{
    /// <inheritdoc/>
    public ProcessResult Run(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.FileName);
        ArgumentNullException.ThrowIfNull(spec.Arguments);
        if (spec.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(spec), "The process timeout must be positive.");
        if (spec.MaxOutputBytes < 256)
            throw new ArgumentOutOfRangeException(nameof(spec), "The output cap must be at least 256 bytes.");
        foreach (var arg in spec.Arguments)
        {
            if (arg is null)
                throw new ArgumentException("Process arguments cannot contain null entries.", nameof(spec));
            if (arg.Length > 4096 || arg.Any(char.IsControl))
                throw new ArgumentException("Process arguments must be bounded printable text.", nameof(spec));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
            startInfo.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        // Minimal inheritance: PATH only. No tokens, keys, or operator
        // credentials reach verifier subprocesses.
        var path = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment.Clear();
        if (!string.IsNullOrEmpty(path))
            startInfo.Environment["PATH"] = path;

        StringBuilder stdout;
        StringBuilder stderr;
        try
        {
            using var process = new Process { StartInfo = startInfo };
            stdout = new StringBuilder();
            stderr = new StringBuilder();
            var cap = spec.MaxOutputBytes;
            process.OutputDataReceived += (_, e) => AppendCapped(stdout, e.Data, cap);
            process.ErrorDataReceived += (_, e) => AppendCapped(stderr, e.Data, cap);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var deadline = DateTime.UtcNow + spec.Timeout;
            while (!process.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    TryKill(process);
                    return new ProcessResult
                    {
                        ExitCode = process.HasExited ? process.ExitCode : -1,
                        StandardOutput = stdout.ToString(),
                        StandardError = stderr.ToString(),
                        TimedOut = true,
                    };
                }
                if (!process.WaitForExit(Math.Min(100, (int)Math.Max(1, remaining.TotalMilliseconds))))
                    continue;
                break;
            }
            process.WaitForExit();
            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                TimedOut = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Binary missing or not startable: the caller maps this to
            // verifier-unavailable, never to success.
            throw new ArtifactBlockedException(
                $"Verifier '{spec.FileName}' could not start: {ex.GetType().Name}.",
                ProvenanceOutcome.VerifierUnavailable);
        }
    }

    private static void AppendCapped(StringBuilder target, string? line, int cap)
    {
        if (line is null)
            return;
        if (target.Length >= cap)
            return;
        var room = cap - target.Length;
        var text = line.Length > room ? line[..room] : line;
        target.Append(text);
        if (target.Length < cap)
            target.Append('\n');
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
            // Raced with natural exit; the caller re-checks HasExited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Already exiting; same handling as above.
        }
    }
}
