using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Bubblewrap;

/// <summary>
/// Sandbox provider backed by bubblewrap (<c>bwrap</c>). Single-package
/// install, no daemon, no podman, no /etc edits — runs entirely as the
/// orchestrator user via Linux user namespaces.
///
/// <para><b>What you get:</b> mount namespace (with explicit binds and
/// tmpfs), PID namespace, IPC namespace, UTS namespace, user namespace.
/// The agent process tree is invisible to other processes on the host;
/// host filesystems beyond what's bound are unreachable.</para>
///
/// <para><b>What you don't get:</b> a separate guest kernel. A Linux
/// kernel exploit in the agent reaches the host kernel — same fundamental
/// risk as plain containers. Pick Multipass if that matters.</para>
///
/// <para><b>Network:</b> bubblewrap can either share the host network
/// namespace (full network access) or unshare it (no network). It cannot
/// enforce per-host allowlists itself; that's a host firewall concern.
/// This provider takes the pragmatic path: if the spec requests any
/// network egress, share the host network. The orchestrator's
/// <c>AgentAllowedHosts</c> setting is documented but not enforced here —
/// operators wanting hostname allowlisting should pick Multipass and
/// configure the host nftables bridges from
/// <c>scripts/setup-host-networks.sh</c>.</para>
///
/// <para><b>Resource limits:</b> bubblewrap doesn't enforce CPU/memory
/// caps. Wrap with <c>systemd-run</c> if you need them; or just don't
/// pick this provider for production.</para>
/// </summary>
public sealed class BubblewrapSandboxProvider : ISandboxProvider
{
    private readonly BubblewrapSandboxOptions _opts;
    private readonly ILogger<BubblewrapSandboxProvider> _log;
    private readonly ITimingStore? _timings;

    public BubblewrapSandboxProvider(BubblewrapSandboxOptions opts, ILogger<BubblewrapSandboxProvider> log,
        ITimingStore? timings = null)
    {
        _opts = opts;
        _log = log;
        _timings = timings;
    }

    public string Name => "bubblewrap";

    /// <inheritdoc/>
    /// <remarks>
    /// Bubblewrap sandboxes are transient processes with no persistent lifecycle
    /// marker the reaper can interrogate after a crash. The process exits on its
    /// own when the orchestrator dies, leaving only a tmpfs staging directory.
    /// Returns empty — bubblewrap leaks are not tracked by the reaper.
    /// </remarks>
    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    /// <inheritdoc/>
    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var timingStore = _timings is not null && spec.TimingWorkItemId.HasValue ? _timings : null;
        var timingItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var timingPhase = spec.TimingPhase ?? "work";

        await using var setupScope = await TimingScope.BeginAsync(
            timingStore, timingItemId, timingPhase, "bwrap.exec_setup", log: _log);

        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-bwrap-{id}");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // For each tmpfs mount, allocate a real host dir under the sandbox
        // root. For each bind mount, remember the host source. The lists
        // are ordered so the longest sandbox path comes first (tmpfs
        // entries are deeper paths typical for /run/codeybox/creds).
        var binds = new List<BindEntry>();
        foreach (var m in spec.Mounts)
        {
            if (m.Tmpfs)
            {
                var hostPath = Path.Combine(root, "fs" + m.SandboxPath.Replace('/', '-'));
                Directory.CreateDirectory(hostPath);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(hostPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                binds.Add(new BindEntry(m.SandboxPath, hostPath, ReadOnly: false));
            }
            else if (m.HostPath is not null)
            {
                binds.Add(new BindEntry(m.SandboxPath, m.HostPath, m.ReadOnly));
            }
        }

        var sandbox = new BubblewrapSandbox(id, root, spec, binds, _opts, _log, timingStore, timingItemId, timingPhase);
        SandboxLiveCounter.Increment();
        var hasNet = spec.Network.AllowedHosts.Count > 0 || spec.Network.HostGitEndpoint is not null;
        if (hasNet)
            _log.LogWarning(
                "Bubblewrap sandbox {Id}: agent network policy is NOT enforced by this provider. " +
                "The agent has full host network access. For hostname allowlisting use Multipass " +
                "with scripts/setup-host-networks.sh.", id);
        return sandbox;
    }
}

public sealed record BubblewrapSandboxOptions
{
    public string BwrapBinary { get; init; } = "bwrap";

    /// <summary>
    /// Read-only host directories the sandbox is allowed to see (for binaries
    /// and libraries). Defaults are sensible for most Linux distros; override
    /// only if you have an unusual layout.
    /// </summary>
    public IReadOnlyList<string> ReadOnlyHostBinds { get; init; } =
        ["/usr", "/lib", "/lib64", "/etc", "/bin", "/sbin"];
}

internal sealed record BindEntry(string SandboxPath, string HostPath, bool ReadOnly);

internal sealed class BubblewrapSandbox : ISandbox
{
    private readonly string _root;
    private readonly SandboxSpec _spec;
    private readonly IReadOnlyList<BindEntry> _binds;
    private readonly BubblewrapSandboxOptions _opts;
    private readonly ILogger _log;
    private readonly ITimingStore? _timings;
    private readonly WorkItemId _timingItemId;
    private readonly string _timingPhase;
    private int _firstExecEmitted;
    private bool _disposed;

    public BubblewrapSandbox(string id, string root, SandboxSpec spec, IReadOnlyList<BindEntry> binds,
        BubblewrapSandboxOptions opts, ILogger log,
        ITimingStore? timings = null, WorkItemId timingItemId = default, string timingPhase = "work")
    {
        Id = id;
        _root = root;
        _spec = spec;
        _binds = binds;
        _opts = opts;
        _log = log;
        _timings = timings;
        _timingItemId = timingItemId;
        _timingPhase = timingPhase;
    }

    public string Id { get; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (exec.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(exec));

        var argv = BuildArgv(exec);

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = exec.Stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);

        // Start with a clean env. Credentials and per-spec env are passed
        // via ProcessStartInfo.EnvironmentVariables (in-memory; not on argv)
        // and bwrap inherits them by default (no --clearenv).
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = exec.WorkingDirectory ?? _spec.WorkingDirectory;
        foreach (var (k, v) in _spec.Environment) psi.EnvironmentVariables[k] = v;
        if (exec.ExtraEnvironment is not null)
            foreach (var (k, v) in exec.ExtraEnvironment) psi.EnvironmentVariables[k] = v;

        var isFirstExec = Interlocked.CompareExchange(ref _firstExecEmitted, 1, 0) == 0;
        TimingScope? firstExecScope = isFirstExec
            ? await TimingScope.BeginAsync(_timings, _timingItemId, _timingPhase, "bwrap.exec_first", log: _log)
            : null;
        try
        {
            using var proc = new Process { StartInfo = psi };
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

            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw;
            }

            return new SandboxExecResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            if (firstExecScope is not null)
                await firstExecScope.DisposeAsync();
        }
    }

    private List<string> BuildArgv(SandboxExec exec)
    {
        var argv = new List<string> { _opts.BwrapBinary };
        argv.Add("--die-with-parent");
        // unshare everything; --share-net is added back below if we need network.
        argv.Add("--unshare-user");
        argv.Add("--unshare-pid");
        argv.Add("--unshare-ipc");
        argv.Add("--unshare-uts");
        argv.Add("--unshare-cgroup-try");

        var hasNet = _spec.Network.AllowedHosts.Count > 0 || _spec.Network.HostGitEndpoint is not null;
        if (!hasNet)
            argv.Add("--unshare-net");
        // (else: don't unshare net — the sandbox shares the host's network namespace.)

        // Standard host filesystem (binaries + libraries). Read-only.
        // On modern usr-merged distros (Debian 12+, Fedora, Arch, …),
        // /bin /sbin /lib /lib64 are symlinks pointing into /usr.
        // Bind-mounting the symlink target only is not enough — programs
        // invoke /bin/sh and expect /bin to exist. Recreate the host
        // symlink inside the sandbox via --symlink so PATH resolution
        // behaves identically.
        foreach (var hostDir in _opts.ReadOnlyHostBinds)
        {
            if (!Directory.Exists(hostDir) && !File.Exists(hostDir)) continue;
            var linkTarget = ReadSymlinkTarget(hostDir);
            if (linkTarget is not null)
            {
                argv.Add("--symlink");
                argv.Add(linkTarget);
                argv.Add(hostDir);
            }
            else
            {
                argv.Add("--ro-bind");
                argv.Add(hostDir);
                argv.Add(hostDir);
            }
        }

        // Per-spec mounts (project bare repo, /work tmpfs, creds tmpfs, etc.).
        foreach (var b in _binds)
        {
            argv.Add(b.ReadOnly ? "--ro-bind" : "--bind");
            argv.Add(b.HostPath);
            argv.Add(b.SandboxPath);
        }

        // /proc and /dev — the agent expects them.
        argv.Add("--proc");
        argv.Add("/proc");
        argv.Add("--dev");
        argv.Add("/dev");

        // /tmp tmpfs — always isolated from the host /tmp.
        argv.Add("--tmpfs");
        argv.Add("/tmp");

        // Working directory inside the sandbox.
        argv.Add("--chdir");
        argv.Add(exec.WorkingDirectory ?? _spec.WorkingDirectory);

        // The actual command.
        argv.AddRange(exec.Argv);
        return argv;
    }

    private static string? ReadSymlinkTarget(string path)
    {
        try
        {
            var dirInfo = new DirectoryInfo(path);
            if (dirInfo.LinkTarget is not null) return dirInfo.LinkTarget;
            var fileInfo = new FileInfo(path);
            return fileInfo.LinkTarget;
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        SandboxLiveCounter.Decrement();
        await using var teardownScope = await TimingScope.BeginAsync(
            _timings, _timingItemId, _timingPhase, "bwrap.teardown", log: _log);
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clean up bwrap sandbox root {Root}", _root);
        }
    }
}
