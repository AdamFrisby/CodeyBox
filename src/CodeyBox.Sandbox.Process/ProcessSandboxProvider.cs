using System.Diagnostics;
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

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
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

internal sealed class ProcessSandbox : ISandbox
{
    private readonly string _root;
    private readonly SandboxSpec _spec;
    private readonly string[] _mountPaths; // longest-first
    private readonly ILogger _log;
    private bool _disposed;

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
        psi.EnvironmentVariables["HOME"] = cwd;
        foreach (var (k, v) in _spec.Environment) psi.EnvironmentVariables[k] = v;
        if (exec.ExtraEnvironment is not null)
            foreach (var (k, v) in exec.ExtraEnvironment) psi.EnvironmentVariables[k] = v;

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
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

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (exec.Stdin is not null)
        {
            await proc.StandardInput.WriteAsync(exec.Stdin);
            proc.StandardInput.Close();
        }

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        return new SandboxExecResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

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

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
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
