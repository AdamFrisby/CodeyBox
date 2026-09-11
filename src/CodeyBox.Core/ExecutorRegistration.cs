namespace CodeyBox.Core;

/// <summary>
/// An executor host's self-declared placement attributes, sent on registration.
/// Registration is the executor's assertion of what it can run; the
/// orchestrator decides what to send it. The vocabulary intentionally mirrors
/// the per-host placement attributes on
/// <c>MultipassRemoteSandboxOptions</c> (<c>HostId</c>,
/// <c>MaxConcurrentSandboxes</c>, <c>Cordoned</c>, <c>Healthy</c>,
/// <c>AllowedNetworkProfiles</c>) so capacity, cordoning and health have one
/// meaning on both sides of the connection instead of two competing ones.
/// </summary>
public sealed record ExecutorRegistration
{
    /// <summary>
    /// Prefix applied to the worker-registry id derived from <see cref="HostId"/>.
    /// The prefix namespaces executor rows away from in-process worker-slot
    /// rows so the dead-worker reaper can tell them apart in logs.
    /// </summary>
    public const string WorkerIdPrefix = "executor:";

    /// <summary>Maximum entries accepted in any declared list on registration.</summary>
    public const int MaxDeclaredEntries = 64;

    /// <summary>Maximum length of a single declared network profile or credential name.</summary>
    public const int MaxDeclaredEntryLength = 128;

    /// <summary>Maximum length of a stable host id.</summary>
    public const int MaxHostIdLength = 128;

    /// <summary>Maximum sandbox capacity an executor may declare.</summary>
    public const int MaxDeclaredCapacity = 100_000;

    /// <summary>
    /// Stable host id configured by the operator (for example the machine's
    /// hostname or an inventory id). Survives executor restarts so a
    /// re-registering host reclaims its own registry row instead of leaking one
    /// row per restart.
    /// </summary>
    public required string HostId { get; init; }

    /// <summary>
    /// Host-local sandbox capacity, mirroring
    /// <c>MultipassRemoteSandboxOptions.MaxConcurrentSandboxes</c>. Null means
    /// "uncapped here". Zero means the executor registers but is never
    /// selected for placement (see <see cref="ExecutorEligibility"/>).
    /// </summary>
    public int? MaxConcurrentSandboxes { get; init; }

    /// <summary>
    /// Logical network profiles this host may accept, mirroring
    /// <c>MultipassRemoteSandboxOptions.AllowedNetworkProfiles</c>. Empty means
    /// all profiles; "*" also means all profiles.
    /// </summary>
    public IReadOnlyList<string> AllowedNetworkProfiles { get; init; } = [];

    /// <summary>
    /// Names of the agent credential sets this host holds (for example
    /// "claude", "codex"). The orchestrator only routes agent classes whose
    /// credentials the executor declares. Entries are opaque names matched by
    /// exact ordinal equality — never by substring.
    /// </summary>
    public IReadOnlyList<string> DeclaredCredentials { get; init; } = [];

    /// <summary>
    /// When true the host is draining: it registers and heartbeats but is
    /// never selected for new placements, mirroring
    /// <c>MultipassRemoteSandboxOptions.Cordoned</c>.
    /// </summary>
    public bool Cordoned { get; init; }

    /// <summary>
    /// Operator-configured health gate, mirroring
    /// <c>MultipassRemoteSandboxOptions.Healthy</c>. False routes new
    /// placements away without removing the registration.
    /// </summary>
    public bool Healthy { get; init; } = true;

    /// <summary>
    /// Deterministic worker-registry id for this host. Stable across restarts
    /// so re-registration is an upsert, not a leak.
    /// </summary>
    public string WorkerId => WorkerIdPrefix + HostId.Trim();

    /// <summary>
    /// Worker-registry id for an arbitrary host id. Centralises the derivation
    /// so endpoint, client and reaper-adjacent code cannot drift apart.
    /// </summary>
    public static string WorkerIdFor(string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        return WorkerIdPrefix + hostId.Trim();
    }
}
