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

    /// <summary>
    /// Returns all sandboxes on the host that belong to this provider
    /// (i.e. match the <c>codeybox-*</c> naming prefix). Used by the
    /// <see cref="CodeyBox.Orchestrator.SandboxLeakReaper"/> to detect
    /// sandboxes that outlived their work item.
    ///
    /// <para>Implementations that have no persistent sandbox lifecycle
    /// (bubblewrap, process) return an empty list.</para>
    ///
    /// <para>Implementations that shell out to an external tool (multipass)
    /// cache results for a short TTL to avoid hammering the daemon on
    /// repeated API calls.</para>
    /// </summary>
    Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct);

    /// <summary>
    /// Best-effort dispose of a sandbox by name. Used by the
    /// <see cref="CodeyBox.Orchestrator.SandboxLeakReaper"/> when
    /// <c>AutoDispose=true</c>, and by the
    /// <c>POST /sandboxes/leaked/{name}/dispose</c> operator endpoint.
    ///
    /// <para>Implementations that have no persistent lifecycle (bubblewrap,
    /// process) are no-ops. Implementations may throw on failure; all callers
    /// must wrap invocations in try/catch and log the exception.</para>
    /// </summary>
    Task DisposeLeakedAsync(string name, CancellationToken ct);
}

/// <summary>
/// Snapshot of a sandbox that exists on the host, returned by
/// <see cref="ISandboxProvider.ListAllManagedAsync"/>.
/// </summary>
/// <param name="Name">VM name / namespace ID.</param>
/// <param name="CreatedAt">Best-effort creation timestamp; null if not derivable.</param>
/// <param name="DiskBytes">Reported disk usage; null if not available.</param>
/// <param name="IsTrackedActive">
/// True when this sandbox was created by the current orchestrator process
/// and has not yet been disposed. False means the sandbox exists on the
/// host but the current process has no record of creating it — the primary
/// indicator of a leak.
/// </param>
public sealed record ManagedSandboxInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    long? DiskBytes,
    bool IsTrackedActive);

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

    /// <summary>
    /// Optional timing context. When set, sandbox providers emit vm.* / bwrap.*
    /// lifecycle timing rows for this work item using ITimingStore.
    /// </summary>
    public WorkItemId? TimingWorkItemId { get; init; }
    public string? TimingPhase { get; init; }
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
/// Sandbox network policy. Egress filtering is host-side: the provider
/// attaches the sandbox to the host bridge mapped from
/// <see cref="ProfileName"/>, and the bridge's nftables rules (set up
/// once by <c>scripts/setup-host-networks.sh</c>) drop everything not
/// on that profile's allowlist. The agent cannot disable this —
/// enforcement lives in the host kernel.
///
/// <para><see cref="AllowedHosts"/> is a documentation/intent field
/// describing what the agent expects to reach; it does not by itself
/// install any in-sandbox rule. The Bubblewrap provider uses it only
/// to gate "any network" vs "no network". The Process provider has no
/// network isolation at all.</para>
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
