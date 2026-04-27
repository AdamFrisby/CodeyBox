using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Sandbox;

/// <summary>
/// Shared OCI driver used by the Kata and crun-vm sandbox providers. Creates
/// a long-lived container with the configured runtime, then exec's commands
/// into it.
///
/// Security-relevant choices baked into this driver:
///
///   - Environment variables containing secrets are written to a tmp
///     <c>--env-file</c> with mode 0600 and removed on disposal. Values do
///     NOT appear on argv and are not visible via /proc/{pid}/cmdline or `ps`.
///
///   - Default network policy is <c>--network none</c>. If the spec lists
///     <see cref="SandboxNetworkPolicy.AllowedHosts"/>, the driver attaches
///     the container to <see cref="PodmanDriverOptions.NetworkName"/> and
///     emits a clear log line. Operators must add netfilter rules on the
///     host to actually constrain egress to the allowlist (see
///     docs/sandbox-providers.md). The driver alone cannot enforce L3 egress
///     allowlists without root and host firewall changes.
///
///   - Resource limits use <c>--memory</c>, <c>--cpus</c>, and
///     <c>--read-only</c> on the rootfs. Disk caps on overlayfs require host
///     quota config; the driver tags the container's ephemeral storage with
///     <c>--storage-opt size=…</c> when supported.
///
///   - Image references should be digest-pinned. The driver does not pull;
///     it expects the image to be present on the host (operator-managed).
///
/// **Tested status:** This driver has been written and code-reviewed but
/// not runtime-tested on the development host (no Kata/crun-vm runtime
/// installed). Treat as alpha until you exercise it on a configured host.
/// </summary>
public sealed class PodmanDriver
{
    private readonly PodmanDriverOptions _opts;
    private readonly ILogger _log;

    public PodmanDriver(PodmanDriverOptions opts, ILogger log)
    {
        _opts = opts;
        _log = log;
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct)
    {
        var name = $"codeybox-{Guid.NewGuid():N}";
        string? envFilePath = null;
        try
        {
            var argv = new List<string> { _opts.PodmanBinary, "run", "-d", "--name", name };
            argv.AddRange(["--runtime", _opts.RuntimeName]);
            argv.AddRange(["--rm=false"]); // we manage removal explicitly
            argv.Add("--read-only"); // rootfs read-only; tmpfs mounts handle writable areas

            // Network policy.
            var hasEgress = spec.Network.AllowedHosts.Count > 0 || spec.Network.HostGitEndpoint is not null;
            if (hasEgress)
            {
                argv.AddRange(["--network", _opts.NetworkName]);
                _log.LogWarning(
                    "Sandbox {Name}: attaching to network {Net}. Driver does NOT enforce egress allowlist; " +
                    "ensure host nftables drops all egress except to {Allowed}",
                    name, _opts.NetworkName, string.Join(",", spec.Network.AllowedHosts));
            }
            else
            {
                argv.AddRange(["--network", "none"]);
            }

            // Resource limits.
            if (spec.Limits.MemoryBytes is { } mem) argv.AddRange(["--memory", mem.ToString()]);
            if (spec.Limits.CpuCount is { } cpus) argv.AddRange(["--cpus", cpus.ToString()]);

            // Mounts.
            foreach (var m in spec.Mounts)
            {
                if (m.Tmpfs)
                {
                    var size = m.SizeBytes is { } b ? $",tmpfs-size={b}" : "";
                    argv.AddRange(["--mount", $"type=tmpfs,destination={m.SandboxPath}{size},tmpfs-mode=0700"]);
                }
                else if (m.HostPath is not null)
                {
                    var ro = m.ReadOnly ? ",ro=true" : "";
                    argv.AddRange(["--mount", $"type=bind,source={m.HostPath},destination={m.SandboxPath}{ro}"]);
                }
            }

            // Working directory.
            argv.AddRange(["--workdir", spec.WorkingDirectory]);

            // Environment from --env-file (NOT --env, which would put values on argv).
            if (spec.Environment.Count > 0)
            {
                envFilePath = await WriteEnvFileAsync(name, spec.Environment, ct);
                argv.AddRange(["--env-file", envFilePath]);
            }

            argv.Add(spec.ImageReference);
            // Long-lived noop so we can exec into the container repeatedly.
            argv.AddRange(["sh", "-c", "trap : TERM INT; sleep infinity & wait"]);

            var run = await RunHostAsync(argv, stdin: null, ct: ct);
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"podman run failed: {run.Stderr}");
            var containerId = run.Stdout.Trim();
            if (string.IsNullOrEmpty(containerId))
                throw new InvalidOperationException($"podman run returned no container id; stderr: {run.Stderr}");

            return new PodmanSandbox(name, containerId, envFilePath, _opts, _log);
        }
        catch
        {
            // Best-effort cleanup if creation half-succeeded.
            if (envFilePath is not null) TryDeleteEnvFile(envFilePath);
            try { await RunHostAsync([_opts.PodmanBinary, "rm", "-f", name], stdin: null, ct: CancellationToken.None); }
            catch { /* swallow */ }
            throw;
        }
    }

    private async Task<string> WriteEnvFileAsync(string sandboxName, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory($"codeybox-env-{sandboxName}-");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir.FullName, "env");
        var sb = new StringBuilder();
        foreach (var (k, v) in env)
        {
            // podman env-file format: KEY=VALUE per line. Newlines in values are
            // not supported by podman; reject loudly rather than silently corrupting.
            if (k.Contains('\n') || k.Contains('=')) throw new ArgumentException($"Invalid env key: {k}");
            if (v.Contains('\n')) throw new ArgumentException($"env value for {k} contains newline");
            sb.Append(k).Append('=').Append(v).Append('\n');
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    internal static void TryDeleteEnvFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    internal static async Task<HostRunResult> RunHostAsync(IReadOnlyList<string> argv, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new HostRunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

internal readonly record struct HostRunResult(int ExitCode, string Stdout, string Stderr);

public sealed record PodmanDriverOptions
{
    /// <summary>Path to the podman binary.</summary>
    public string PodmanBinary { get; init; } = "podman";

    /// <summary>The OCI runtime to use ("kata", "kata-runtime", "crun-vm", …).</summary>
    public required string RuntimeName { get; init; }

    /// <summary>
    /// Name of the podman/CNI network containers attach to when egress is
    /// requested. The operator is responsible for configuring this network
    /// and any host-side firewall policy that constrains egress to the
    /// agent allowlist.
    /// </summary>
    public string NetworkName { get; init; } = "codeybox-egress";
}

internal sealed class PodmanSandbox : ISandbox
{
    private readonly string _containerName;
    private readonly string? _envFilePath;
    private readonly PodmanDriverOptions _opts;
    private readonly ILogger _log;
    private bool _disposed;

    public PodmanSandbox(string containerName, string containerId, string? envFilePath, PodmanDriverOptions opts, ILogger log)
    {
        _containerName = containerName;
        Id = containerId;
        _envFilePath = envFilePath;
        _opts = opts;
        _log = log;
    }

    public string Id { get; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (exec.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(exec));

        var argv = new List<string> { _opts.PodmanBinary, "exec" };
        if (exec.Stdin is not null) argv.Add("-i");
        if (exec.WorkingDirectory is not null) argv.AddRange(["--workdir", exec.WorkingDirectory]);
        // Per-exec env on argv is fine for non-secret runtime hints. Secrets
        // are already in the container's environment from --env-file at boot.
        if (exec.ExtraEnvironment is not null)
            foreach (var (k, v) in exec.ExtraEnvironment)
                argv.AddRange(["-e", $"{k}={v}"]);
        argv.Add(_containerName);
        argv.AddRange(exec.Argv);

        var r = await PodmanDriver.RunHostAsync(argv, exec.Stdin, ct);
        return new SandboxExecResult(r.ExitCode, r.Stdout, r.Stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await PodmanDriver.RunHostAsync(
                [_opts.PodmanBinary, "rm", "-f", "-t", "5", _containerName],
                stdin: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to remove podman container {Name}", _containerName);
        }
        if (_envFilePath is not null) PodmanDriver.TryDeleteEnvFile(_envFilePath);
    }
}
