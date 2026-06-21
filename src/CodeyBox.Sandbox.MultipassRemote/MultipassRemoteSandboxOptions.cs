namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Configuration for <see cref="MultipassRemoteSandboxProvider"/>. The whole
/// options record is hot-reloadable through a delegate accessor — a config
/// edit lands on the next <c>CreateAsync</c> without an orchestrator restart.
/// </summary>
public sealed record MultipassRemoteSandboxOptions
{
    public const int DefaultServerAliveIntervalSeconds = 30;
    public const int DefaultServerAliveCountMax = 6;
    public const int DefaultConnectTimeoutSeconds = 20;
    public static readonly TimeSpan DefaultVmStartTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// OpenSSH client binary. Resolved via $PATH when bare ("ssh"). Override
    /// with an absolute path on hosts where $PATH-based resolution is
    /// unreliable (e.g. systemd unit with a stripped PATH).
    /// </summary>
    public string SshBinary { get; init; } = "ssh";

    /// <summary>
    /// SSH destination passed verbatim to <c>ssh &lt;target&gt;</c>. Standard
    /// OpenSSH shapes accepted: <c>user@host</c>, a host alias from the
    /// orchestrator user's <c>~/.ssh/config</c>, or a bare hostname when
    /// the user matches the orchestrator user.
    /// </summary>
    public string SshTarget { get; init; } = "";

    /// <summary>Optional SSH port override. Null = OpenSSH default (22 / config-file).</summary>
    public int? SshPort { get; init; }

    /// <summary>
    /// Identity file passed via <c>-i</c>. Null = whatever the host SSH agent
    /// or <c>~/.ssh/config</c> resolves. When set, <c>IdentitiesOnly=yes</c>
    /// is also passed so the SSH agent's other keys do not get attempted —
    /// reduces surprise auth retries that can lock out a service account.
    /// </summary>
    public string? SshKeyPath { get; init; }

    /// <summary>
    /// Extra <c>-o Key=Value</c> options appended to every ssh invocation.
    /// Validated to look like <c>Key=Value</c> with no whitespace before use.
    /// </summary>
    public IReadOnlyList<string> ExtraSshOptions { get; init; } = [];

    /// <summary>
    /// When true, OpenSSH's <c>StrictHostKeyChecking</c> is set to
    /// <c>accept-new</c> so the host key is trusted on first contact and
    /// pinned in <c>~/.ssh/known_hosts</c>. When false (default), the host
    /// must already be present in <c>known_hosts</c> — a key change or
    /// brand-new host fails fast rather than silently trusting whatever
    /// answers. Leave false in production deployments.
    /// </summary>
    public bool AcceptUnknownHostKeys { get; init; } = false;

    /// <summary>
    /// OpenSSH <c>ServerAliveInterval</c> in seconds. Must be &gt; 0. Lower
    /// values catch dead peers faster; higher values waste fewer keepalive
    /// packets. The default (30s) matches Multipass's own keepalive cadence.
    /// </summary>
    public int ServerAliveIntervalSeconds { get; init; } = DefaultServerAliveIntervalSeconds;

    /// <summary>
    /// OpenSSH <c>ServerAliveCountMax</c>. Number of consecutive keepalives
    /// allowed to fail before the client gives up. With the default
    /// 30s interval × 6 count, a dead peer is detected within ~3 minutes —
    /// long enough that a transient network hiccup doesn't drop the
    /// connection mid-agent-run, short enough that a genuinely dead remote
    /// host fails fast.
    /// </summary>
    public int ServerAliveCountMax { get; init; } = DefaultServerAliveCountMax;

    /// <summary>
    /// OpenSSH <c>ConnectTimeout</c> in seconds, applied to the initial TCP
    /// connect only. Subsequent stalls are governed by the ServerAlive*
    /// settings.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;

    /// <summary>
    /// Local tar binary used by the SCP-via-tar staging pipeline. Almost
    /// always "tar" on Linux/macOS; override only when a deployment ships a
    /// non-standard tar.
    /// </summary>
    public string LocalTarBinary { get; init; } = "tar";

    /// <summary>
    /// Absolute path to the multipass binary on the remote host. The remote
    /// host may have multipass installed via snap (<c>/snap/bin/multipass</c>)
    /// or as a system package; this lets the operator point at whichever.
    /// </summary>
    public string RemoteMultipassPath { get; init; } = "/snap/bin/multipass";

    /// <summary>
    /// Absolute path on the remote host where each sandbox stages its
    /// bind-mount sources and tmpfs-equivalent directories. The provider
    /// creates per-sandbox subdirectories under this root and chmods 0700.
    /// Must be a path the remote multipass daemon can read — on snap
    /// installs the daemon's AppArmor profile only allows paths under
    /// <c>~/snap/multipass/common/</c>, so the default reflects that.
    /// </summary>
    public string RemoteStagingRoot { get; init; } = "/home/ubuntu/snap/multipass/common/codeybox-remote-staging";

    /// <summary>
    /// Default image alias when <see cref="CodeyBox.Core.SandboxSpec.ImageReference"/>
    /// is empty / "ignored". E.g. "24.04". Empty → multipass picks the current LTS.
    /// </summary>
    public string? DefaultImage { get; init; }

    /// <summary>
    /// Deadline for waiting for the VM to enter the <c>Running</c> state.
    /// </summary>
    public TimeSpan VmStartTimeout { get; init; } = DefaultVmStartTimeout;

    /// <summary>
    /// Deadline for waiting for the VM to enter the <c>Stopped</c> state.
    /// </summary>
    public TimeSpan VmStopTimeout { get; init; } = DefaultVmStopTimeout;

    /// <summary>
    /// Polling interval used while waiting for VM state transitions. Lower
    /// values catch transitions faster at the cost of more ssh round-trips.
    /// </summary>
    public TimeSpan VmStateCheckInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Naming prefix for sandbox VMs created by this provider. Used by
    /// <see cref="MultipassRemoteSandboxProvider.ListAllManagedAsync"/> to
    /// recognise its own VMs in <c>multipass list</c>.
    /// </summary>
    public string VmNamePrefix { get; init; } = "codeybox-r-";
}
