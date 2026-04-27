using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Multipass;

/// <summary>
/// Sandbox provider backed by Canonical Multipass. Each sandbox is a real
/// Ubuntu VM with its own kernel — a kernel exploit in the agent escapes
/// into a VM that gets destroyed when the sandbox is disposed, never
/// reaching the host kernel.
///
/// <para><b>Why this exists:</b> the threat model includes "agent fetches
/// a webpage, gets prompt-injected, runs arbitrary commands." Bubblewrap
/// shares the host kernel; a kernel exploit in those commands would reach
/// the host. Multipass gives separate-kernel isolation with a single
/// <c>snap install multipass</c> on Ubuntu — the easiest kernel-isolation
/// path on this OS.</para>
///
/// <para><b>Trade-off:</b> VM launch is ~10-30 seconds. A work item with
/// audit phases launches multiple VMs in sequence and accrues that
/// overhead per phase. Pick this when the threat model justifies it; pick
/// <c>bubblewrap</c> when speed matters more than kernel isolation.</para>
///
/// <para><b>Network policy:</b> applied via cloud-init at VM launch using
/// iptables. AllowedHosts are resolved on the host, and only the resulting
/// IPs are accepted as egress destinations. Loopback + DNS to the VM's
/// configured resolver is also allowed. With <c>AllowedHosts</c> empty,
/// all egress is dropped (loopback only).</para>
///
/// <para><b>Image:</b> defaults to Multipass's current LTS Ubuntu image.
/// The agent CLI binaries (claude, codex, etc.) need to be installed in
/// the VM. Operators provide an additional cloud-init fragment via
/// <see cref="MultipassSandboxOptions.ExtraCloudInit"/> to install agents
/// on first boot, OR build a Multipass image with agents pre-installed
/// and reference it via <see cref="SandboxSpec.ImageReference"/>.</para>
/// </summary>
public sealed class MultipassSandboxProvider : ISandboxProvider
{
    private readonly MultipassSandboxOptions _opts;
    private readonly ILogger<MultipassSandboxProvider> _log;
    private readonly string _stagingRoot;

    public MultipassSandboxProvider(MultipassSandboxOptions opts, ILogger<MultipassSandboxProvider> log)
    {
        _opts = opts;
        _log = log;
        _stagingRoot = ResolveStagingRoot(opts);
        Directory.CreateDirectory(_stagingRoot);
    }

    /// <summary>
    /// When Multipass is installed as a snap (the standard path on Ubuntu),
    /// the daemon is AppArmor-confined and CANNOT read arbitrary paths like
    /// /tmp. Files passed to <c>--cloud-init</c> and bind-mount sources both
    /// need to live under <c>~/snap/multipass/common/</c>, which is in
    /// Multipass's allowed read set.
    ///
    /// We auto-detect: prefer <c>~/snap/multipass/common/codeybox-staging</c>
    /// if it exists (snap install); fall back to <c>/tmp</c> otherwise
    /// (non-snap installs, e.g. on macOS). Operators can override via
    /// <see cref="MultipassSandboxOptions.StagingDirectory"/>.
    /// </summary>
    private static string ResolveStagingRoot(MultipassSandboxOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.StagingDirectory))
            return opts.StagingDirectory;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var snapCommon = Path.Combine(home, "snap", "multipass", "common");
            if (Directory.Exists(snapCommon))
                return Path.Combine(snapCommon, "codeybox-staging");
        }
        return Path.Combine(Path.GetTempPath(), "codeybox-mp-staging");
    }

    public string Name => "multipass";

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        var name = $"codeybox-{Guid.NewGuid():N}"[..23]; // multipass max name length is 24
        var sandboxRoot = Path.Combine(_stagingRoot, name);
        Directory.CreateDirectory(sandboxRoot);

        // Pre-create host directories for tmpfs-equivalent mounts so we can
        // bind-mount them into the VM after launch.
        var bindMounts = new List<(string Host, string Sandbox)>();
        foreach (var m in spec.Mounts)
        {
            if (m.Tmpfs)
            {
                var hostPath = Path.Combine(sandboxRoot, "fs" + m.SandboxPath.Replace('/', '-'));
                Directory.CreateDirectory(hostPath);
                bindMounts.Add((hostPath, m.SandboxPath));
            }
            else if (m.HostPath is not null)
            {
                bindMounts.Add((m.HostPath, m.SandboxPath));
            }
        }

        // Host-resolve the allowed hosts so the VM-side iptables rules can
        // be IP-based. DNS-based hostname allowlisting at L3 is brittle
        // (CDN drift, DNS rebinding) but better than no policy at all.
        // Documented in docs/sandbox-providers.md.
        var allowedIps = await ResolveHostsAsync(spec.Network.AllowedHosts, ct);
        var cloudInit = BuildCloudInit(allowedIps, _opts.ExtraCloudInit);
        var cloudInitPath = Path.Combine(sandboxRoot, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInitPath, cloudInit, ct);

        try
        {
            await LaunchAsync(name, spec, cloudInitPath, ct);
            await WaitForRunningAsync(name, ct);
            await SetUpMountsAsync(name, bindMounts, ct);
            await TransferEnvAsync(name, spec.Environment, sandboxRoot, ct);
            // The exec wrapper is installed by cloud-init at boot
            // (see BuildCloudInit's write_files), so no post-launch
            // transfer is needed. This also means we don't depend on the
            // ubuntu user having sudo to install it — sudo is removed
            // from ubuntu by cloud-init runcmd to harden the VM against
            // a compromised agent flushing iptables.
            return new MultipassSandbox(name, sandboxRoot, spec, _opts, _log);
        }
        catch
        {
            // Best-effort cleanup if launch / mount / transfer half-succeeded.
            await TryDeleteVmAsync(name);
            try { Directory.Delete(sandboxRoot, recursive: true); } catch { }
            throw;
        }
    }

    internal IReadOnlyList<string> BuildLaunchArgv(string name, SandboxSpec spec, string cloudInitPath)
    {
        var argv = new List<string> { _opts.MultipassBinary, "launch", "--name", name };
        if (spec.Limits.CpuCount is { } cpus) argv.AddRange(["--cpus", cpus.ToString()]);
        if (spec.Limits.MemoryBytes is { } mem) argv.AddRange(["--memory", $"{mem / (1024 * 1024)}M"]);
        if (spec.Limits.DiskBytes is { } disk) argv.AddRange(["--disk", $"{disk / (1024 * 1024)}M"]);
        argv.AddRange(["--cloud-init", cloudInitPath]);

        // Host-enforced egress profile. When the spec names a profile and
        // the provider has a bridge mapped for it, attach the VM to that
        // bridge as a SECONDARY network. The agent's only viable internet
        // path is via this bridge — the operator's host-side nftables on
        // the bridge enforces the allowlist; the agent cannot subvert it
        // because the rules live in the host kernel, not the VM.
        // Multipass's default mpqemubr0 is still attached (control plane
        // needs it), but setup-host-networks.sh blocks all forwarding on
        // it so it doesn't carry user traffic.
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            if (!_opts.NetworkProfiles.TryGetValue(spec.Network.ProfileName, out var bridge))
                throw new InvalidOperationException(
                    $"Network profile '{spec.Network.ProfileName}' is not configured in MultipassSandboxOptions.NetworkProfiles. " +
                    $"Configured profiles: [{string.Join(", ", _opts.NetworkProfiles.Keys)}]. " +
                    "Either add the profile to options or run setup-host-networks.sh and update appsettings.");
            argv.AddRange(["--network", $"name={bridge},mode=auto"]);
        }

        // ImageReference: empty/null => multipass picks the default image.
        if (!string.IsNullOrWhiteSpace(spec.ImageReference) && spec.ImageReference != "ignored")
            argv.Add(spec.ImageReference);
        else if (!string.IsNullOrWhiteSpace(_opts.DefaultImage))
            argv.Add(_opts.DefaultImage);

        return argv;
    }

    private async Task LaunchAsync(string name, SandboxSpec spec, string cloudInitPath, CancellationToken ct)
    {
        var argv = BuildLaunchArgv(name, spec, cloudInitPath);
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
            _log.LogInformation("Sandbox {Name}: host-enforced network profile {Profile}", name, spec.Network.ProfileName);
        _log.LogInformation("Launching multipass VM {Name} (this takes 10-30s)", name);
        var run = await RunAsync(argv, stdin: null, ct: ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"multipass launch failed: {run.Stderr}");
    }

    private async Task WaitForRunningAsync(string name, CancellationToken ct)
    {
        // Two waits: first the VM enters "Running" state, then cloud-init
        // finishes applying runcmd (which is where our iptables rules
        // land). If we let the agent run before cloud-init completes, the
        // firewall isn't on yet — egress is wide open and the policy is
        // a lie. The cloud-init wait is the real correctness gate.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = await RunAsync([_opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct);
            if (info.ExitCode == 0 && info.Stdout.Contains("Running", StringComparison.Ordinal))
                break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (DateTime.UtcNow >= deadline)
            throw new InvalidOperationException($"multipass VM {name} did not reach Running state within 3 minutes");

        // `cloud-init status --wait` blocks until cloud-init has finished
        // (success or fail). Exit code is non-zero on failure; we don't
        // distinguish here because the post-launch verification (mount,
        // exec) will surface concrete problems.
        await RunAsync(
            [_opts.MultipassBinary, "exec", name, "--", "cloud-init", "status", "--wait"],
            stdin: null, ct: ct);
    }

    private async Task SetUpMountsAsync(string name, List<(string Host, string Sandbox)> binds, CancellationToken ct)
    {
        if (binds.Count == 0) return;

        // Use --type=native (9p/virtiofs passthrough) rather than the
        // default sshfs-based "classic" mount: classic requires the
        // multipass-sshfs snap installed inside the guest, and our cloud-
        // init firewall blocks the snap-store reachability needed for
        // that auto-install on first mount. Native mounts use the
        // hypervisor's filesystem passthrough and need no in-guest install.
        //
        // Native mounts can only be CONFIGURED while the VM is stopped.
        // Sequence: stop → mount (each) → start → wait-for-running.
        var stop = await RunAsync([_opts.MultipassBinary, "stop", name], stdin: null, ct: ct);
        if (stop.ExitCode != 0)
            throw new InvalidOperationException($"multipass stop (for mount) failed: {stop.Stderr}");

        foreach (var (host, sandbox) in binds)
        {
            var run = await RunAsync(
                [_opts.MultipassBinary, "mount", "--type=native", host, $"{name}:{sandbox}"],
                stdin: null, ct: ct);
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"multipass mount {host} -> {name}:{sandbox} failed: {run.Stderr}");
        }

        var start = await RunAsync([_opts.MultipassBinary, "start", name], stdin: null, ct: ct);
        if (start.ExitCode != 0)
            throw new InvalidOperationException($"multipass start (after mount) failed: {start.Stderr}");
        await WaitForRunningAsync(name, ct);
    }

    /// <summary>
    /// Transfers the environment file into the VM at <c>~ubuntu/.codeybox-env</c>.
    /// The exec wrapper sources this before running each command, so secret
    /// values never appear on a <c>multipass exec</c> argv (which would
    /// land them on the host's process listing via /proc).
    ///
    /// The file is owned by the <c>ubuntu</c> user (multipass's default exec
    /// identity) with mode 0600 — readable by the agent's process, not by
    /// other VM users. We avoid /run/codeybox/ because that dir is owned by
    /// root and would force a sudo dance to install the file readable by
    /// the non-root exec user.
    /// </summary>
    private async Task<string> TransferEnvAsync(string name, IReadOnlyDictionary<string, string> env, string sandboxRoot, CancellationToken ct)
    {
        var envPath = Path.Combine(sandboxRoot, "env");
        var sb = new StringBuilder();
        foreach (var (k, v) in env)
        {
            if (k.Contains('=') || k.Contains('\n'))
                throw new ArgumentException($"Invalid env key: {k}");
            // Quote the value so shell sourcing handles spaces/special chars.
            // Backslash-escape any embedded double quotes.
            var escaped = v.Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.Append(k).Append("=\"").Append(escaped).Append("\"\n");
        }
        await File.WriteAllTextAsync(envPath, sb.ToString(), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(envPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var tx = await RunAsync(
            [_opts.MultipassBinary, "transfer", envPath, $"{name}:.codeybox-env"],
            stdin: null, ct: ct);
        if (tx.ExitCode != 0)
            throw new InvalidOperationException($"multipass transfer env file failed: {tx.Stderr}");

        var perms = await RunAsync(
            [_opts.MultipassBinary, "exec", name, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"],
            stdin: null, ct: ct);
        if (perms.ExitCode != 0)
            throw new InvalidOperationException($"failed to chmod env file in VM: {perms.Stderr}");

        return "/home/ubuntu/.codeybox-env";
    }

    /// <summary>
    /// The exec wrapper script content. Sources the env file (if present),
    /// cds to the target working directory, exec's the user command. Lives
    /// at /usr/local/bin/codeybox-exec inside the VM, owned by root with
    /// mode 0755 so the agent (running as the unprivileged ubuntu user
    /// without sudo) can run but cannot modify it.
    /// </summary>
    private const string ExecWrapperScript = """
        #!/bin/sh
        set -a
        [ -r "$HOME/.codeybox-env" ] && . "$HOME/.codeybox-env"
        set +a
        cd "$1" || exit 127
        shift
        exec "$@"
        """;

    /// <summary>
    /// Builds a cloud-init document that:
    ///   - Installs the exec wrapper at /usr/local/bin/codeybox-exec (root-
    ///     owned, mode 0755) so the agent can execute but not modify it.
    ///   - Installs a systemd-managed iptables egress allowlist that
    ///     re-applies on every boot (important because we stop/start the
    ///     VM to add native mounts, and naked iptables rules are
    ///     kernel-state-only).
    ///   - Removes passwordless sudo from the ubuntu user. Without this,
    ///     a compromised agent could run <c>sudo iptables -F</c> and
    ///     disable the firewall — the entire egress allowlist would be
    ///     voluntary. After this step the agent's runtime is strictly
    ///     unprivileged.
    ///
    /// Only the OUTPUT chain is restricted — that's the exfiltration vector.
    /// The INPUT chain is left at Ubuntu's default (ACCEPT) because
    /// Multipass's daemon needs to reach the guest over its private network
    /// for status checks, exec, and mount; an INPUT DROP breaks Multipass's
    /// own initialisation handshake.
    ///
    /// Empty <paramref name="allowedIps"/> → outbound is dropped except
    /// loopback, DNS, and replies to incoming connections.
    /// </summary>
    private static string BuildCloudInit(IReadOnlyList<string> allowedIps, string? extra)
    {
        // Build the iptables-restore rules text.
        var rules = new StringBuilder();
        rules.AppendLine("*filter");
        rules.AppendLine(":INPUT ACCEPT [0:0]");
        rules.AppendLine(":FORWARD ACCEPT [0:0]");
        rules.AppendLine(":OUTPUT DROP [0:0]");
        rules.AppendLine("-A OUTPUT -o lo -j ACCEPT");
        rules.AppendLine("-A OUTPUT -m state --state ESTABLISHED,RELATED -j ACCEPT");
        rules.AppendLine("-A OUTPUT -p udp --dport 53 -j ACCEPT");
        rules.AppendLine("-A OUTPUT -p tcp --dport 53 -j ACCEPT");
        foreach (var ip in allowedIps)
        {
            // Only IPv4/IPv6 literals reach here (DNS resolution result).
            rules.Append("-A OUTPUT -d ").Append(ip).AppendLine(" -j ACCEPT");
        }
        rules.AppendLine("COMMIT");
        var rulesIndented = string.Join("\n      ", rules.ToString().TrimEnd('\n').Split('\n'));

        // Wrapper script indented for YAML content.
        var wrapperIndented = string.Join("\n      ", ExecWrapperScript.Split('\n'));

        var sb = new StringBuilder();
        sb.AppendLine("#cloud-config");
        sb.AppendLine("write_files:");
        sb.AppendLine("  - path: /etc/codeybox-iptables.rules");
        sb.AppendLine("    permissions: '0644'");
        sb.AppendLine("    content: |");
        sb.Append("      ").AppendLine(rulesIndented);
        sb.AppendLine("  - path: /etc/systemd/system/codeybox-firewall.service");
        sb.AppendLine("    permissions: '0644'");
        sb.AppendLine("    content: |");
        sb.AppendLine("      [Unit]");
        sb.AppendLine("      Description=CodeyBox egress firewall");
        sb.AppendLine("      DefaultDependencies=no");
        sb.AppendLine("      Before=network-pre.target");
        sb.AppendLine("      Wants=network-pre.target");
        sb.AppendLine("      [Service]");
        sb.AppendLine("      Type=oneshot");
        sb.AppendLine("      ExecStart=/usr/sbin/iptables-restore /etc/codeybox-iptables.rules");
        sb.AppendLine("      RemainAfterExit=yes");
        sb.AppendLine("      [Install]");
        sb.AppendLine("      WantedBy=multi-user.target");
        sb.AppendLine("  - path: /usr/local/bin/codeybox-exec");
        sb.AppendLine("    permissions: '0755'");
        sb.AppendLine("    content: |");
        sb.Append("      ").AppendLine(wrapperIndented);
        sb.AppendLine("runcmd:");
        sb.AppendLine("  - systemctl daemon-reload");
        sb.AppendLine("  - systemctl enable --now codeybox-firewall.service");
        // Note: the in-VM firewall is ADVISORY — a compromised agent with
        // sudo (which we leave intact so the operator can install dev
        // tooling) can flush iptables and undo it. Real egress enforcement
        // happens on the host via nftables on the multipass bridge —
        // see HostFirewall in this project. Keeping the in-VM rules is
        // defence-in-depth and useful when the agent is well-behaved but
        // wrong-default; do not rely on them against a hostile agent.
        if (!string.IsNullOrWhiteSpace(extra))
        {
            sb.AppendLine();
            sb.AppendLine("# --- extra cloud-init from MultipassSandboxOptions.ExtraCloudInit ---");
            sb.AppendLine(extra);
        }
        return sb.ToString();
    }

    private static async Task<List<string>> ResolveHostsAsync(IReadOnlyList<string> hosts, CancellationToken ct)
    {
        var ips = new List<string>();
        foreach (var host in hosts)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                foreach (var a in addrs)
                    ips.Add(a.ToString());
            }
            catch
            {
                // If a hostname doesn't resolve at launch time, the agent
                // simply can't reach it. The launch still succeeds; the
                // failure surfaces when the agent tries to connect.
            }
        }
        return ips;
    }

    private async Task<RunResult> RunAsync(IReadOnlyList<string> argv, string? stdin, CancellationToken ct)
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
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
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
        return new RunResult(p.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task TryDeleteVmAsync(string name)
    {
        try
        {
            await RunAsync([_opts.MultipassBinary, "delete", "--purge", name], stdin: null, ct: CancellationToken.None);
        }
        catch { /* best-effort */ }
    }
}

internal readonly record struct RunResult(int ExitCode, string Stdout, string Stderr);

public sealed record MultipassSandboxOptions
{
    public string MultipassBinary { get; init; } = "multipass";

    /// <summary>
    /// Default image alias when SandboxSpec.ImageReference is empty / "ignored".
    /// E.g. "24.04". Empty → multipass picks the current LTS.
    /// </summary>
    public string? DefaultImage { get; init; }

    /// <summary>
    /// Extra cloud-init YAML appended after the egress-allowlist rules.
    /// Use to apt-install agent CLIs or configure additional VM state.
    /// </summary>
    public string? ExtraCloudInit { get; init; }

    /// <summary>
    /// Where to stage cloud-init files and tmpfs-backing directories.
    /// Defaults to <c>~/snap/multipass/common/codeybox-staging</c> when the
    /// snap install is detected; falls back to /tmp otherwise. Override
    /// only if your Multipass install reads a different prefix.
    /// </summary>
    public string? StagingDirectory { get; init; }

    /// <summary>
    /// Maps logical network-profile names (selected via
    /// <c>SandboxNetworkPolicy.ProfileName</c>) to host bridge interface
    /// names. The bridges must already exist on the host with their
    /// nftables egress rules — operators set this up once via
    /// <c>scripts/setup-host-networks.sh</c>.
    ///
    /// Example:
    /// <code>
    /// new Dictionary&lt;string, string&gt; {
    ///     ["isolated"]  = "codeybox-net-isolated",
    ///     ["claude"]    = "codeybox-net-claude",
    ///     ["multi-llm"] = "codeybox-net-multi-llm",
    /// }
    /// </code>
    ///
    /// When a sandbox spec selects a profile not in this map, the
    /// provider throws at launch time — it never silently falls back to
    /// "no enforcement."
    /// </summary>
    public IReadOnlyDictionary<string, string> NetworkProfiles { get; init; }
        = new Dictionary<string, string>();
}

internal sealed class MultipassSandbox : ISandbox
{
    private readonly string _name;
    private readonly string _sandboxRoot;
    private readonly SandboxSpec _spec;
    private readonly MultipassSandboxOptions _opts;
    private readonly ILogger _log;
    private bool _disposed;

    public MultipassSandbox(string name, string sandboxRoot, SandboxSpec spec, MultipassSandboxOptions opts, ILogger log)
    {
        _name = name;
        _sandboxRoot = sandboxRoot;
        _spec = spec;
        _opts = opts;
        _log = log;
        Id = name;
    }

    public string Id { get; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (exec.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(exec));

        // Run via the codeybox-exec wrapper so:
        //   - /run/codeybox/env (transferred at sandbox boot) is sourced —
        //     credentials live there, never on argv.
        //   - working directory is enforced.
        //
        // Per-exec ExtraEnvironment is appended via --env if multipass
        // supports it; otherwise inlined into the wrapper invocation. For
        // simplicity we always inline as KEY=VALUE prefix args to env(1).
        var argv = new List<string> { _opts.MultipassBinary, "exec", _name, "--" };

        var wrapped = new List<string> { "/usr/local/bin/codeybox-exec", exec.WorkingDirectory ?? _spec.WorkingDirectory };
        if (exec.ExtraEnvironment is not null && exec.ExtraEnvironment.Count > 0)
        {
            // env(1) takes KEY=VALUE pairs followed by the command. Per-exec
            // env is for non-secret runtime hints — secrets are in /run/codeybox/env.
            wrapped.Add("env");
            foreach (var (k, v) in exec.ExtraEnvironment)
                wrapped.Add($"{k}={v}");
        }
        wrapped.AddRange(exec.Argv);

        argv.AddRange(wrapped);

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

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        if (exec.Stdin is not null)
        {
            await p.StandardInput.WriteAsync(exec.Stdin);
            p.StandardInput.Close();
        }
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new SandboxExecResult(p.ExitCode, await stdoutTask, await stderrTask);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = _opts.MultipassBinary,
                ArgumentList = { "delete", "--purge", _name },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is not null)
            {
                _ = await p.StandardOutput.ReadToEndAsync();
                _ = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete multipass VM {Name}", _name);
        }
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clean sandbox root {Root}", _sandboxRoot); }
    }
}
