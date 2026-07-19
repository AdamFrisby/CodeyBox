using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Process;

/// <summary>
/// Plain-process sandbox provider. UNSAFE: provides only filesystem isolation
/// via a temp working directory. There is no kernel isolation, no cgroup
/// limits, and the network policy is purely advisory. Use for local
/// development of the orchestrator pipeline only — never in production or
/// against untrusted prompts.
/// </summary>
public sealed class ProcessSandboxProvider : ISandboxProvider
{
    private readonly ILogger<ProcessSandboxProvider> _log;

    public ProcessSandboxProvider(ILogger<ProcessSandboxProvider> log)
    {
        _log = log;
    }

    public string Name => "process";

    /// <inheritdoc/>
    /// <remarks>
    /// The process provider has no managed VM lifecycle; it returns an empty list.
    /// </remarks>
    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    /// <inheritdoc/>
    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        spec = SandboxConventions.WithTimingEnvironment(spec);

        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "codeybox-sandbox-" + id);
        Directory.CreateDirectory(root);

        // Materialise convention dirs and bind-mount equivalents (copies for read-only, links for writable).
        // The "image reference" is ignored by this provider; commands run on the host's PATH.
        foreach (var mount in spec.Mounts)
        {
            var target = MapToHostPath(root, mount.SandboxPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (mount.Tmpfs)
            {
                // Real tmpfs would require root; fall back to a fresh dir.
                // Credentials sit here, so we'll restrict perms below.
                Directory.CreateDirectory(target);
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                    catch { /* non-Unix or unsupported FS */ }
                }
            }
            else if (mount.HostPath is not null)
            {
                if (Directory.Exists(mount.HostPath))
                {
                    if (mount.ReadOnly)
                    {
                        // Copy so writes don't escape; perms enforce read-only.
                        CopyDir(mount.HostPath, target, readOnly: true);
                    }
                    else
                    {
                        // Symlink so writes (e.g. git push to a bare repo) land
                        // on the real host path. ProcessSandbox is dev-only —
                        // there is no isolation to break here anyway.
                        File.CreateSymbolicLink(target, mount.HostPath);
                    }
                }
                else if (File.Exists(mount.HostPath))
                {
                    if (mount.ReadOnly)
                    {
                        File.Copy(mount.HostPath, target, overwrite: true);
                        File.SetAttributes(target, FileAttributes.ReadOnly);
                    }
                    else
                    {
                        File.CreateSymbolicLink(target, mount.HostPath);
                    }
                }
            }
        }

        Directory.CreateDirectory(MapToHostPath(root, spec.WorkingDirectory));
        Directory.CreateDirectory(MapToHostPath(root, "home/codeybox"));

        // Build longest-first mount path table for argv translation. The
        // orchestrator addresses files by sandbox-absolute paths (e.g.
        // "/repos/<id>.git"); since ProcessSandbox doesn't really isolate
        // the filesystem, ExecAsync rewrites those to their host-fs
        // equivalents. UNSAFE provider — accuracy here is for runnability,
        // not security.
        var mountPaths = spec.Mounts
            .Concat(new[] { new SandboxMount { SandboxPath = spec.WorkingDirectory, Tmpfs = true } })
            .Select(m => m.SandboxPath.TrimEnd('/'))
            .Where(p => p.StartsWith('/'))
            .Distinct()
            .OrderByDescending(p => p.Length)
            .ToArray();

        var sandbox = new ProcessSandbox(id, root, spec, mountPaths, _log);
        SandboxLiveCounter.Increment();
        _log.LogWarning("ProcessSandbox {Id} created at {Root} (UNSAFE provider — no isolation)", id, root);
        return Task.FromResult<ISandbox>(sandbox);
    }

    private static string MapToHostPath(string root, string sandboxPath)
    {
        // Treat sandbox absolute path as a subpath under root.
        var trimmed = sandboxPath.TrimStart('/');
        return Path.Combine(root, trimmed);
    }

    private static void CopyDir(string src, string dst, bool readOnly)
    {
        Directory.CreateDirectory(dst);
        foreach (var sub in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(sub.Replace(src, dst, StringComparison.Ordinal));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = f.Replace(src, dst, StringComparison.Ordinal);
            File.Copy(f, target, overwrite: true);
            if (readOnly)
                File.SetAttributes(target, FileAttributes.ReadOnly);
        }
    }

    internal static string MapToHostPathInternal(string root, string sandboxPath) => MapToHostPath(root, sandboxPath);
}

internal sealed class ProcessSandbox : IPreemptibleSandbox, IPreserveOnDisposeSandbox
{
    private readonly string _root;
    private readonly SandboxSpec _spec;
    private readonly string[] _mountPaths; // longest-first
    private readonly ILogger _log;
    private readonly object _processGate = new();
    private readonly HashSet<System.Diagnostics.Process> _processes = [];
    private bool _disposed;
    private bool _preserved;

    public ProcessSandbox(string id, string root, SandboxSpec spec, string[] mountPaths, ILogger log)
    {
        Id = id;
        _root = root;
        _spec = spec;
        _mountPaths = mountPaths;
        _log = log;
    }

    public string Id { get; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessSandbox));
        if (exec.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(exec));

        var cwd = ProcessSandboxProvider.MapToHostPathInternal(_root, exec.WorkingDirectory ?? _spec.WorkingDirectory);
        Directory.CreateDirectory(cwd);

        var psi = new ProcessStartInfo
        {
            FileName = TranslatePath(exec.Argv[0]),
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = exec.Stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < exec.Argv.Count; i++)
            psi.ArgumentList.Add(TranslatePath(exec.Argv[i]));

        // Start clean and only add what is requested. The dev sandbox does not
        // attempt to enforce the network policy at the OS level.
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = ProcessSandboxProvider.MapToHostPathInternal(_root, "/home/codeybox");
        foreach (var (k, v) in _spec.Environment) psi.EnvironmentVariables[k] = TranslateEnvironmentValue(k, v);
        if (exec.ExtraEnvironment is not null)
            foreach (var (k, v) in exec.ExtraEnvironment) psi.EnvironmentVariables[k] = TranslateEnvironmentValue(k, v);
        exec.ApplyEnvironmentRemovals(name => psi.EnvironmentVariables.Remove(name));

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var limitOutput = exec.MaxStdoutBytes.HasValue || exec.MaxStderrBytes.HasValue;
        if (!limitOutput)
        {
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data + "\n";
                stdout.Append(line);
                exec.StdoutChunkCallback?.Invoke(line);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data + "\n";
                stderr.Append(line);
                exec.StderrChunkCallback?.Invoke(line);
            };
        }

        await StartWithTransientRetryAsync(
            proc.Start,
            static (attempt, token) => Task.Delay(SpawnRetryBackoffs[attempt - 1], token),
            SpawnMaxAttempts,
            ct).ConfigureAwait(false);
        RegisterProcess(proc);
        Task<LimitedReadResult>? limitedStdoutTask = null;
        Task<LimitedReadResult>? limitedStderrTask = null;
        try
        {
            if (limitOutput)
            {
                void KillForLimit()
                {
                    KillProcessTree(proc);
                }

                limitedStdoutTask = ReadLimitedAsync(
                    proc.StandardOutput,
                    exec.MaxStdoutBytes,
                    exec.StdoutChunkCallback,
                    exec.KillOnOutputLimit ? KillForLimit : null,
                    ct);
                limitedStderrTask = ReadLimitedAsync(
                    proc.StandardError,
                    exec.MaxStderrBytes,
                    exec.StderrChunkCallback,
                    exec.KillOnOutputLimit ? KillForLimit : null,
                    ct);
            }
            else
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            if (exec.Stdin is not null)
            {
                try
                {
                    await WriteStandardInputAsync(proc, exec.Stdin, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillProcessTree(proc);
                    throw;
                }
            }

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(proc);
                throw;
            }

            if (limitedStdoutTask is not null && limitedStderrTask is not null)
            {
                var stdoutResult = await limitedStdoutTask;
                var stderrResult = await limitedStderrTask;
                return new SandboxExecResult(
                    proc.ExitCode,
                    stdoutResult.Text,
                    stderrResult.Text,
                    stdoutResult.LimitExceeded,
                    stderrResult.LimitExceeded);
            }

            return new SandboxExecResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            UnregisterProcess(proc);
        }
    }

    private static async Task WriteStandardInputAsync(
        System.Diagnostics.Process process,
        string stdin,
        CancellationToken ct)
    {
        var shouldClose = true;
        try
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            // The child closed stdin early; keep waiting so callers receive its exit code and stderr.
            shouldClose = false;
        }

        if (!shouldClose)
            return;

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException ex) when (IsBrokenPipe(ex))
        {
            // Close may flush buffered data after the child has already exited.
        }
    }

    private static bool IsBrokenPipe(IOException ex)
    {
        const int hResultBrokenPipe = unchecked((int)0x8007006D);
        const int hResultNoData = unchecked((int)0x800700E8);
        const int nativeBrokenPipe = 32;

        return ex.HResult is hResultBrokenPipe or hResultNoData
            || ex.InnerException is SocketException socket
            && (socket.NativeErrorCode == nativeBrokenPipe
                || socket.SocketErrorCode is SocketError.Shutdown or SocketError.ConnectionReset);
    }

    private void RegisterProcess(System.Diagnostics.Process process)
    {
        lock (_processGate)
            _processes.Add(process);
    }

    private void UnregisterProcess(System.Diagnostics.Process process)
    {
        lock (_processGate)
            _processes.Remove(process);
    }

    private static void KillProcessTree(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort teardown path.
        }
    }

    public Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        System.Diagnostics.Process[] activeProcesses;
        lock (_processGate)
            activeProcesses = _processes.ToArray();
        foreach (var process in activeProcesses)
            KillProcessTree(process);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Total number of <c>posix_spawn</c> attempts (initial + retries) before a
    /// transient spawn failure is allowed to propagate.
    /// </summary>
    internal const int SpawnMaxAttempts = 4;

    /// <summary>
    /// Backoff before each retry. One entry per retry (<see cref="SpawnMaxAttempts"/>
    /// minus the initial attempt). Deliberately short: the failures we retry are
    /// momentary kernel resource exhaustion (EAGAIN/EMFILE) that clears as the
    /// full-suite fork/fd storm drains, not a sustained outage.
    /// </summary>
    private static readonly TimeSpan[] SpawnRetryBackoffs =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
    ];

    /// <summary>
    /// Starts a process, retrying a bounded number of times on a TRANSIENT
    /// spawn failure. Under heavy parallel load (the full test suite launches
    /// thousands of short-lived subprocesses through redirected pipes)
    /// <c>posix_spawn</c>/<c>fork</c> can momentarily return EAGAIN (thread/PID
    /// pressure) or EMFILE/ENFILE (fd pressure); treating that blip as a hard
    /// failure spuriously degrades the caller (e.g. silently disables agy
    /// structured-stream capture for a whole run). The child never started on a
    /// throwing attempt, so re-issuing the spawn is safe and idempotent.
    /// A non-transient failure (e.g. ENOENT — binary missing) is rethrown
    /// immediately rather than retried.
    /// </summary>
    /// <param name="start">Spawns the process once; returns its result (ignored).
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> on spawn failure.</param>
    /// <param name="delay">Backoff before retry <paramref name="attempt"/> (1-based).</param>
    /// <param name="maxAttempts">Total attempts including the first; must be &gt;= 1.</param>
    internal static async Task StartWithTransientRetryAsync(
        Func<bool> start,
        Func<int, CancellationToken, Task> delay,
        int maxAttempts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                start();
                return;
            }
            catch (System.ComponentModel.Win32Exception ex)
                when (attempt < maxAttempts && IsTransientSpawnFailure(ex))
            {
                await delay(attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// True for kernel error codes that indicate momentary resource exhaustion
    /// during spawn — worth a bounded retry — rather than a deterministic
    /// misconfiguration (bad path, permission) that a retry cannot fix.
    /// </summary>
    private static bool IsTransientSpawnFailure(System.ComponentModel.Win32Exception ex) =>
        ex.NativeErrorCode is 11 /* EAGAIN */ or 12 /* ENOMEM */ or 23 /* ENFILE */ or 24 /* EMFILE */;

    private static async Task<LimitedReadResult> ReadLimitedAsync(
        StreamReader reader,
        int? maxBytes,
        Action<string>? chunkCallback,
        Action? onLimitExceeded,
        CancellationToken ct)
    {
        const int readBufferChars = 4096;
        var output = new StringBuilder();
        var buffer = new char[readBufferChars];
        var totalBytes = 0;
        var limitExceeded = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
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

    private static string TakeUtf8Prefix(string input, int maxBytes)
    {
        var bytes = 0;
        for (var i = 0; i < input.Length;)
        {
            var charCount = char.IsHighSurrogate(input[i])
                && i + 1 < input.Length
                && char.IsLowSurrogate(input[i + 1])
                    ? 2
                    : 1;
            var charBytes = Encoding.UTF8.GetByteCount(input.AsSpan(i, charCount));
            if (bytes + charBytes > maxBytes)
                return input[..i];
            bytes += charBytes;
            i += charCount;
        }
        return input;
    }

    private sealed record LimitedReadResult(string Text, bool LimitExceeded);

    /// <summary>
    /// Maps a sandbox-absolute path under a known mount point to its host-fs
    /// equivalent under the sandbox temp root. Leaves anything else alone:
    /// binaries on PATH, non-path argv elements, etc.
    /// </summary>
    private string TranslatePath(string arg)
    {
        if (string.IsNullOrEmpty(arg) || arg[0] != '/') return arg;
        foreach (var mount in _mountPaths)
        {
            if (arg.Length == mount.Length && arg == mount)
                return Path.Combine(_root, mount.TrimStart('/'));
            if (arg.Length > mount.Length && arg[mount.Length] == '/' && arg.StartsWith(mount, StringComparison.Ordinal))
            {
                var tail = arg[(mount.Length + 1)..];
                return Path.Combine(_root, mount.TrimStart('/'), tail);
            }
        }
        return arg;
    }

    private string TranslateEnvironmentValue(string key, string value)
    {
        if (!string.Equals(key, "PATH", StringComparison.Ordinal))
            return value;

        var entries = value.Split(':');
        for (var i = 0; i < entries.Length; i++)
        {
            if (!string.IsNullOrEmpty(entries[i]) && entries[i][0] == '/')
                entries[i] = TranslatePath(entries[i]);
        }

        return string.Join(Path.PathSeparator, entries);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        SandboxLiveCounter.Decrement();
        KillActiveExecsAsync().GetAwaiter().GetResult();
        if (_preserved)
            return ValueTask.CompletedTask;
        try
        {
            // Clear read-only bits and remove symlinks (without following them)
            // so the temp root can be deleted without touching the real targets.
            if (Directory.Exists(_root))
            {
                RemoveTreeSafely(_root);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clean up ProcessSandbox {Id} root {Root}", Id, _root);
        }
        return ValueTask.CompletedTask;
    }

    public Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".codeybox-preempt"), DateTimeOffset.UtcNow.ToString("O"));
        _preserved = true;
        return Task.CompletedTask;
    }

    public void DisablePreserveOnDispose() => _preserved = false;

    private static void RemoveTreeSafely(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null)
            {
                // Symlink — delete the link, not the target.
                File.Delete(entry);
                continue;
            }
            if (Directory.Exists(entry))
            {
                RemoveTreeSafely(entry);
            }
            else
            {
                try { File.SetAttributes(entry, FileAttributes.Normal); } catch { /* best-effort */ }
                File.Delete(entry);
            }
        }
        Directory.Delete(path);
    }
}
