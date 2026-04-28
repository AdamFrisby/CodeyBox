namespace CodeyBox.Core;

/// <summary>
/// Builds and starts isolated execution sandboxes. Implementations include a
/// plain-process dev runner (UNSAFE; for local testing only), bubblewrap
/// (namespace isolation, shared kernel), and Multipass (KVM-backed VMs with
/// a separate guest kernel — recommended for production). The orchestrator
/// picks one provider per deployment.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>Stable identifier for diagnostics ("process", "bubblewrap", "multipass").</summary>
    string Name { get; }

    /// <summary>
    /// Provisions a sandbox according to the given spec. The returned handle
    /// holds the running sandbox until disposed; disposal must tear it down
    /// regardless of state.
    /// </summary>
    Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default);
}

/// <summary>A live sandbox. Disposing destroys it.</summary>
public interface ISandbox : IAsyncDisposable
{
    string Id { get; }

    /// <summary>
    /// Executes a command inside the sandbox. The command is run with
    /// /work as the working directory unless overridden. Output streams are
    /// captured fully; for long-running commands prefer streaming variants
    /// added later.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default);
}

/// <summary>
/// Description of a sandbox to provision. Mounts and environment are the only
/// channels by which the host injects state into the sandbox.
/// </summary>
public sealed record SandboxSpec
{
    public required string ImageReference { get; init; }
    public IReadOnlyList<SandboxMount> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public SandboxResourceLimits Limits { get; init; } = SandboxResourceLimits.Default;
    public SandboxNetworkPolicy Network { get; init; } = SandboxNetworkPolicy.Denied;
    public string WorkingDirectory { get; init; } = "/work";
}

/// <summary>
/// Mount of a host path into the sandbox. <see cref="ReadOnly"/> mounts are
/// strongly preferred; the writable agent workspace is the only common
/// exception. <see cref="Tmpfs"/> mounts back the path with an in-memory
/// filesystem of <paramref name="SizeBytes"/> (used for credentials).
/// </summary>
public sealed record SandboxMount
{
    public required string SandboxPath { get; init; }
    public string? HostPath { get; init; }
    public bool ReadOnly { get; init; } = true;
    public bool Tmpfs { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed record SandboxResourceLimits
{
    public int? CpuCount { get; init; }
    public long? MemoryBytes { get; init; }
    public long? DiskBytes { get; init; }
    public TimeSpan? WallClock { get; init; }

    public static SandboxResourceLimits Default { get; } = new()
    {
        CpuCount = 2,
        MemoryBytes = 2L * 1024 * 1024 * 1024,
        DiskBytes = 8L * 1024 * 1024 * 1024,
        WallClock = TimeSpan.FromMinutes(60),
    };
}

/// <summary>
/// Sandbox network policy. Default is deny-all; agents are explicitly granted
/// only the destinations they need (their API endpoint and the host git
/// endpoint). Upstream remotes (GitHub) are NEVER allowed from inside.
///
/// <para>Two enforcement modes (provider-dependent):</para>
/// <list type="bullet">
///   <item><b>Advisory (in-VM):</b> the provider applies an in-sandbox firewall
///   based on <see cref="AllowedHosts"/>. A privileged agent inside the sandbox
///   can disable this; useful only against well-behaved agents.</item>
///   <item><b>Enforced (host-side, profile-based):</b> when <see cref="ProfileName"/>
///   is set and the provider supports it, the orchestrator selects a pre-
///   configured host-side network profile (e.g. a bridge with nftables rules
///   the operator set up via setup-host-networks.sh). The agent cannot
///   disable this — enforcement lives in the host kernel.</item>
/// </list>
/// </summary>
public sealed record SandboxNetworkPolicy
{
    /// <summary>Hostnames the sandbox is allowed to reach. Empty = no egress.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>If non-null, sandbox can reach this host:port for git operations.</summary>
    public string? HostGitEndpoint { get; init; }

    /// <summary>
    /// Name of a pre-configured host-side network profile. When set, the
    /// provider attaches the sandbox to the matching host bridge (and its
    /// host-enforced egress rules) instead of relying on in-VM filtering.
    /// The provider's options map this name to a bridge name.
    /// </summary>
    public string? ProfileName { get; init; }

    public static SandboxNetworkPolicy Denied { get; } = new();
}

public sealed record SandboxExec
{
    public required IReadOnlyList<string> Argv { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
    public string? Stdin { get; init; }
}

public sealed record SandboxExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
