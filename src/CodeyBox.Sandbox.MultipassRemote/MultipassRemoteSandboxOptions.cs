using System.Collections.Generic;

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
    public const long DefaultStageOutMaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultStageOutMaxEntries = 200_000;
    public const double DefaultStageOutMaxExpansionRatio = 1.5d;
    public static readonly TimeSpan DefaultVmStartTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Stable host id used in placement logs and metrics. Empty means this
    /// options instance is the top-level legacy single-host config; resolved
    /// host snapshots always have a non-empty id.
    /// </summary>
    public string HostId { get; init; } = "";

    /// <summary>
    /// Host-local sandbox capacity. Null means "uncapped here" and the global
    /// worker/sandbox admission gates remain the only ceiling.
    /// </summary>
    public int? MaxConcurrentSandboxes { get; init; }

    /// <summary>
    /// When true, the host is draining: no new VMs are placed here, while
    /// existing active VMs are allowed to finish and release their slots.
    /// </summary>
    public bool Cordoned { get; init; }

    /// <summary>
    /// Operator-configured health gate. Set false to route new VMs away from a
    /// host without removing it from config; existing active VMs keep running.
    /// </summary>
    public bool Healthy { get; init; } = true;

    /// <summary>
    /// Logical network profiles this host may accept. Empty means all profiles;
    /// "*" also means all profiles. Use "(default)" for sandboxes with no
    /// explicit <see cref="CodeyBox.Core.SandboxNetworkPolicy.ProfileName"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedNetworkProfiles { get; init; } = [];

    /// <summary>
    /// Maps logical sandbox network profile names to bridge interface names on
    /// each remote executor host. Mirrors local MultipassSandboxOptions.
    /// </summary>
    public IReadOnlyDictionary<string, string> NetworkProfiles { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Multi-host pool. When empty, the legacy top-level SSH fields are treated
    /// as a single host named "default".
    /// </summary>
    public IReadOnlyList<MultipassRemoteExecutorHostOptions> ExecutorHosts { get; init; } = [];

    /// <summary>
    /// Requeue delay surfaced when no executor host can currently accept a
    /// sandbox because every eligible host is full, cordoned, or unhealthy.
    /// </summary>
    public TimeSpan PlacementRecheckIn { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runtime backoff applied after an SSH transport drop. The host is skipped
    /// for new placements until the deadline, then probed opportunistically by
    /// the next placement/list operation.
    /// </summary>
    public TimeSpan RuntimeUnhealthyBackoff { get; init; } = TimeSpan.FromMinutes(1);

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
    /// Maximum tar bytes accepted from a remote writable mount during stage-out.
    /// The archive lands on the coordinator before validation, so this cap bounds
    /// how much sandbox-controlled content can be persisted locally per sync.
    /// </summary>
    public long StageOutMaxArchiveBytes { get; init; } = DefaultStageOutMaxArchiveBytes;

    /// <summary>
    /// Maximum non-metadata tar entries accepted during stage-out validation.
    /// Keeps hostile trees from forcing unbounded validation/extraction work.
    /// </summary>
    public int StageOutMaxEntries { get; init; } = DefaultStageOutMaxEntries;

    /// <summary>
    /// Maximum declared regular-file payload divided by archive bytes. Remote
    /// stage-out uses uncompressed tar, so ratios materially above 1 indicate
    /// sparse or malformed archive content that could expand unexpectedly.
    /// </summary>
    public double StageOutMaxExpansionRatio { get; init; } = DefaultStageOutMaxExpansionRatio;

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

    public static IReadOnlyList<MultipassRemoteSandboxOptions> ResolveExecutorHosts(MultipassRemoteSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<MultipassRemoteExecutorHostOptions> hosts = options.ExecutorHosts.Count == 0
            ? new MultipassRemoteExecutorHostOptions[]
            {
                new MultipassRemoteExecutorHostOptions
                {
                    Id = string.IsNullOrWhiteSpace(options.HostId) ? "default" : options.HostId,
                    SshTarget = options.SshTarget,
                    SshBinary = options.SshBinary,
                    SshPort = options.SshPort,
                    SshKeyPath = options.SshKeyPath,
                    ExtraSshOptions = options.ExtraSshOptions,
                    AcceptUnknownHostKeys = options.AcceptUnknownHostKeys,
                    ServerAliveIntervalSeconds = options.ServerAliveIntervalSeconds,
                    ServerAliveCountMax = options.ServerAliveCountMax,
                    ConnectTimeoutSeconds = options.ConnectTimeoutSeconds,
                    LocalTarBinary = options.LocalTarBinary,
                    StageOutMaxArchiveBytes = options.StageOutMaxArchiveBytes,
                    StageOutMaxEntries = options.StageOutMaxEntries,
                    StageOutMaxExpansionRatio = options.StageOutMaxExpansionRatio,
                    RemoteMultipassPath = options.RemoteMultipassPath,
                    RemoteStagingRoot = options.RemoteStagingRoot,
                    DefaultImage = options.DefaultImage,
                    VmStartTimeout = options.VmStartTimeout,
                    VmStopTimeout = options.VmStopTimeout,
                    VmStateCheckInterval = options.VmStateCheckInterval,
                    VmNamePrefix = options.VmNamePrefix,
                    MaxConcurrentSandboxes = options.MaxConcurrentSandboxes,
                    Cordoned = options.Cordoned,
                    Healthy = options.Healthy,
                    AllowedNetworkProfiles = options.AllowedNetworkProfiles,
                },
            }
            : options.ExecutorHosts;

        var resolved = new List<MultipassRemoteSandboxOptions>(hosts.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < hosts.Count; i++)
        {
            var host = hosts[i];
            var hostId = string.IsNullOrWhiteSpace(host.Id)
                ? (options.ExecutorHosts.Count == 0
                    ? "default"
                    : throw new InvalidOperationException($"MultipassRemoteSandbox executor host at index {i} must set a stable Id."))
                : host.Id.Trim();
            if (!seen.Add(hostId))
                throw new InvalidOperationException($"Duplicate MultipassRemoteSandbox executor host id '{hostId}'.");

            resolved.Add(options with
            {
                HostId = hostId,
                SshTarget = FirstNonWhiteSpace(host.SshTarget, options.SshTarget),
                SshBinary = FirstNonWhiteSpace(host.SshBinary, options.SshBinary),
                SshPort = host.SshPort ?? options.SshPort,
                SshKeyPath = host.SshKeyPath ?? options.SshKeyPath,
                ExtraSshOptions = host.ExtraSshOptions ?? options.ExtraSshOptions,
                AcceptUnknownHostKeys = host.AcceptUnknownHostKeys ?? options.AcceptUnknownHostKeys,
                ServerAliveIntervalSeconds = host.ServerAliveIntervalSeconds ?? options.ServerAliveIntervalSeconds,
                ServerAliveCountMax = host.ServerAliveCountMax ?? options.ServerAliveCountMax,
                ConnectTimeoutSeconds = host.ConnectTimeoutSeconds ?? options.ConnectTimeoutSeconds,
                LocalTarBinary = FirstNonWhiteSpace(host.LocalTarBinary, options.LocalTarBinary),
                StageOutMaxArchiveBytes = host.StageOutMaxArchiveBytes ?? options.StageOutMaxArchiveBytes,
                StageOutMaxEntries = host.StageOutMaxEntries ?? options.StageOutMaxEntries,
                StageOutMaxExpansionRatio = host.StageOutMaxExpansionRatio ?? options.StageOutMaxExpansionRatio,
                RemoteMultipassPath = FirstNonWhiteSpace(host.RemoteMultipassPath, options.RemoteMultipassPath),
                RemoteStagingRoot = FirstNonWhiteSpace(host.RemoteStagingRoot, options.RemoteStagingRoot),
                DefaultImage = host.DefaultImage ?? options.DefaultImage,
                VmStartTimeout = host.VmStartTimeout ?? options.VmStartTimeout,
                VmStopTimeout = host.VmStopTimeout ?? options.VmStopTimeout,
                VmStateCheckInterval = host.VmStateCheckInterval ?? options.VmStateCheckInterval,
                VmNamePrefix = FirstNonWhiteSpace(host.VmNamePrefix, options.VmNamePrefix),
                MaxConcurrentSandboxes = host.MaxConcurrentSandboxes ?? options.MaxConcurrentSandboxes,
                Cordoned = host.Cordoned ?? options.Cordoned,
                Healthy = host.Healthy ?? options.Healthy,
                AllowedNetworkProfiles = host.AllowedNetworkProfiles ?? options.AllowedNetworkProfiles,
                NetworkProfiles = options.NetworkProfiles,
                ExecutorHosts = [],
            });
        }

        return resolved;
    }

    internal static int EffectiveCapacity(MultipassRemoteSandboxOptions hostOptions) =>
        hostOptions.MaxConcurrentSandboxes is { } cap ? cap : int.MaxValue;

    private static string FirstNonWhiteSpace(string? first, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first! : fallback;
}

public sealed record MultipassRemoteExecutorHostOptions
{
    public string? Id { get; init; }
    public string? SshTarget { get; init; }
    public string? SshBinary { get; init; }
    public int? SshPort { get; init; }
    public string? SshKeyPath { get; init; }
    public IReadOnlyList<string>? ExtraSshOptions { get; init; }
    public bool? AcceptUnknownHostKeys { get; init; }
    public int? ServerAliveIntervalSeconds { get; init; }
    public int? ServerAliveCountMax { get; init; }
    public int? ConnectTimeoutSeconds { get; init; }
    public string? LocalTarBinary { get; init; }
    public long? StageOutMaxArchiveBytes { get; init; }
    public int? StageOutMaxEntries { get; init; }
    public double? StageOutMaxExpansionRatio { get; init; }
    public string? RemoteMultipassPath { get; init; }
    public string? RemoteStagingRoot { get; init; }
    public string? DefaultImage { get; init; }
    public TimeSpan? VmStartTimeout { get; init; }
    public TimeSpan? VmStopTimeout { get; init; }
    public TimeSpan? VmStateCheckInterval { get; init; }
    public string? VmNamePrefix { get; init; }
    public int? MaxConcurrentSandboxes { get; init; }
    public bool? Cordoned { get; init; }
    public bool? Healthy { get; init; }
    public IReadOnlyList<string>? AllowedNetworkProfiles { get; init; }
}
