using CodeyBox.Core;
using CodeyBox.Sandbox.Bubblewrap;
using CodeyBox.Sandbox.Incus;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Sandbox.MultipassRemote;
using CodeyBox.Sandbox.Sprites;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Tuning knobs for the remote executor host process. Bind under
/// <c>CodeyBox:Executor</c>. The whole record is hot-reloadable through a
/// delegate accessor — a config edit lands on the next heartbeat without an
/// executor restart. Operational values live here, never as literals in the
/// client or tracker.
/// </summary>
public sealed class ExecutorOptions
{
    /// <summary>
    /// Stable host id this executor registers under. Required on the executor
    /// host; empty means "executor mode disabled" for processes that share
    /// this configuration surface (notably the orchestrator itself).
    /// </summary>
    public string HostId { get; set; } = "";

    /// <summary>Base URL of the orchestrator (for example "https://orchestrator:5000"). Outbound only — the executor never listens.</summary>
    public string OrchestratorBaseUrl { get; set; } = "";

    /// <summary>
    /// Name of the environment variable carrying this host's bearer token.
    /// This must be the env var holding the host-bound executor token for
    /// this host's <see cref="HostId"/> (a <c>CodeyBox:ApiClients</c> entry
    /// with a matching <c>ExecutorHostId</c>), never the shared operator key:
    /// executor endpoints reject the operator key. The key itself is never
    /// stored in config files and never logged.
    /// </summary>
    public string ApiKeyEnvVar { get; set; } = "CODEYBOX_EXECUTOR_API_KEY";

    /// <summary>
    /// Host-local sandbox capacity, mirroring the per-host
    /// <c>MaxConcurrentSandboxes</c> placement attribute. Null means uncapped.
    /// Zero registers the executor but leaves it never selected.
    /// </summary>
    public int? MaxConcurrentSandboxes { get; set; }

    /// <summary>
    /// Logical network profiles this host accepts. Empty means all profiles.
    /// </summary>
    public List<string> AllowedNetworkProfiles { get; set; } = [];

    /// <summary>
    /// Operator narrowing allow-list for the agent credential sets this host
    /// advertises. Empty (or <c>"*"</c>) means "advertise everything actually
    /// runnable"; otherwise only listed names are kept. The effective
    /// declaration is always derived from composed runners plus held
    /// credentials (see <see cref="ExecutorAgentAdvertiser"/>), so this list
    /// can only narrow, never widen.
    /// </summary>
    public List<string> DeclaredCredentials { get; set; } = [];

    /// <summary>
    /// Per-agent ceiling for credential probing when registration derives the
    /// declared credential set (see <see cref="ExecutorAgentAdvertiser"/>).
    /// Bounds one slow provider so it cannot stall registration. Default 10 s.
    /// </summary>
    public TimeSpan AgentCredentialProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Clearance tags this host is trusted to handle, in the same vocabulary
    /// as the work item <c>RequiredCapabilities</c> clearance tags. The
    /// orchestrator only places phases demanding a tag on hosts declaring it.
    /// </summary>
    public List<string> DeclaredCapabilities { get; set; } = [];

    /// <summary>When true the host drains: registers and heartbeats but is never selected for new placements.</summary>
    public bool Cordoned { get; set; }

    /// <summary>Operator health gate. False routes new placements away without removing the registration.</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>How often the executor heartbeats into the worker registry. Default 15 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Per-request timeout for registration and heartbeat calls. Default 20 s.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Local sandbox backend the executor provisions through. Exact-match
    /// against the registered provider kinds (see
    /// <see cref="HostPlatformSupport.AllProviderIds"/>). Kept as the
    /// single-kind shortcut: when <see cref="SandboxProviders"/> is empty
    /// this is the only kind served, so existing deployments are unaffected.
    /// </summary>
    public string LocalSandboxProvider { get; set; } = "process";

    /// <summary>Maximum provider kinds one executor may declare. Bounds the list, not the registry.</summary>
    public const int MaxDeclaredProviderKinds = 16;

    /// <summary>
    /// Every sandbox provider kind this host serves. Empty means
    /// "just <see cref="LocalSandboxProvider"/>". When non-empty it wins
    /// entirely and <see cref="LocalSandboxProvider"/> is only the
    /// compatibility default for the primary slot; each entry must name a
    /// registered provider kind (exact-match, case-insensitive, no
    /// duplicates). A host can offer both an Incus VM and a lightweight
    /// process sandbox from one process.
    /// </summary>
    public List<string> SandboxProviders { get; set; } = [];

    /// <summary>
    /// Bubblewrap settings bound from <c>CodeyBox:Executor:Bubblewrap</c>.
    /// Read once when the kind is first built; an edit applies to providers
    /// built afterwards (process restart in practice, since the registry
    /// shares one instance per kind).
    /// </summary>
    public BubblewrapSandboxOptions Bubblewrap { get; set; } = new();

    /// <summary>
    /// Multipass settings bound from <c>CodeyBox:Executor:Multipass</c>.
    /// Held as a live accessor, so edits land on the next VM launch.
    /// </summary>
    public MultipassSandboxOptions Multipass { get; set; } = new();

    /// <summary>
    /// Incus settings bound from <c>CodeyBox:Executor:Incus</c>.
    /// Held as a live accessor, so edits land on the next operation.
    /// </summary>
    public IncusSandboxOptions Incus { get; set; } = new();

    /// <summary>
    /// Multipass-remote settings bound from
    /// <c>CodeyBox:Executor:MultipassRemote</c>. Held as a live accessor.
    /// </summary>
    public MultipassRemoteSandboxOptions MultipassRemote { get; set; } = new();

    /// <summary>
    /// Sprites settings bound from <c>CodeyBox:Executor:Sprites</c>.
    /// Held as a live accessor.
    /// </summary>
    public SpritesSandboxOptions Sprites { get; set; } = new();

    /// <summary>
    /// What happens to a running sandbox when the orchestrator connection
    /// drops. Only <see cref="ExecutorDisconnectPolicy.RetainSandboxForResume"/>
    /// exists in this item: retain for resumption, reconcile on reconnect.
    /// </summary>
    public ExecutorDisconnectPolicy DisconnectPolicy { get; set; } = ExecutorDisconnectPolicy.RetainSandboxForResume;

    /// <summary>
    /// Root directory under which the executor resolves staged bare-repo
    /// copies, one leaf per dispatch (per dispatched <c>RepositoryId</c> plus
    /// the dispatch key, so concurrent dispatches against one repo stay
    /// isolated). Empty means a process-temp subdirectory. The delivery plane
    /// stages the phase's single bare repo here; the phase runner resolves it
    /// with canonicalize-then-contain and refuses to run when it is absent.
    /// </summary>
    public string PhaseStagingRoot { get; set; } = "";

    /// <summary>Maximum path chars accepted in <see cref="PhaseStagingRoot"/>.</summary>
    public const int MaxPhaseStagingRootLength = 1024;

    /// <summary>
    /// Image reference stamped on sandbox specs the phase runner provisions.
    /// Empty means "provider default". Hosts on VM-backed providers must set
    /// a real image; the lightweight process provider ignores it.
    /// </summary>
    public string PhaseSandboxImageReference { get; set; } = "";

    /// <summary>Maximum chars accepted in <see cref="PhaseSandboxImageReference"/>.</summary>
    public const int MaxPhaseSandboxImageReferenceLength = 512;

    /// <summary>
    /// Maximum completed phase results the executor-side replay guard keeps.
    /// Oldest-completed entries are evicted first; in-flight phases are never
    /// evicted. Bounds the guard's memory; the control plane's idempotency
    /// store stays authoritative across restarts.
    /// </summary>
    public int MaxCachedPhaseResults { get; set; } = 1024;

    /// <summary>Maximum entries accepted in the phase-result replay guard.</summary>
    public const int MaxCachedPhaseResultsLimit = 1_000_000;

    /// <summary>
    /// How long the executor replays a completed phase result on redelivery
    /// before dropping it. Mirrors the dispatch idempotency TTL.
    /// </summary>
    public TimeSpan PhaseResultCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Normalised provider kinds this host serves, in declaration order.
    /// <see cref="SandboxProviders"/> wins when non-empty; otherwise the
    /// single-kind <see cref="LocalSandboxProvider"/> shortcut applies.
    /// Entries are trimmed, lowercased, and deduplicated (first wins).
    /// </summary>
    public IReadOnlyList<string> GetDeclaredKinds()
    {
        var source = SandboxProviders.Count > 0
            ? (IReadOnlyList<string>)SandboxProviders
            : [LocalSandboxProvider];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(source.Count);
        foreach (var raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var normalized = raw.Trim().ToLowerInvariant();
            if (seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    /// <summary>
    /// Primary kind: the first declared kind, backing the singleton
    /// <see cref="CodeyBox.Core.ISandboxProvider"/> for compatibility with
    /// single-provider consumers. Falls back to <c>process</c> only when
    /// configuration names nothing usable (validation rejects that first).
    /// </summary>
    public string GetPrimarySandboxKind()
    {
        var kinds = GetDeclaredKinds();
        return kinds.Count > 0 ? kinds[0] : HostPlatformSupport.Process;
    }

    /// <summary>
    /// Every buildable provider kind, normalised and sorted for messages.
    /// Single source of truth for executor-side validation; mirrors the
    /// registry's known kinds.
    /// </summary>
    public static string ValidProviderKinds => string.Join(
        ", ",
        HostPlatformSupport.AllProviderIds.OrderBy(static s => s, StringComparer.Ordinal));
    /// <summary>
    /// Builds the registration assertion sent on connect. Registration is the
    /// executor's claim of what it can run; the orchestrator decides placement.
    /// <see cref="DeclaredCredentials"/> is the operator's narrowing allow-list
    /// here — the client replaces it with the derived runnable set (intersected
    /// with this list) whenever an <see cref="ExecutorAgentAdvertiser"/> is wired.
    /// </summary>
    public ExecutorRegistration ToRegistration() => new()
    {
        HostId = HostId.Trim(),
        MaxConcurrentSandboxes = MaxConcurrentSandboxes,
        AllowedNetworkProfiles = [.. AllowedNetworkProfiles],
        DeclaredCredentials = [.. DeclaredCredentials],
        DeclaredCapabilities = [.. DeclaredCapabilities],
        Cordoned = Cordoned,
        Healthy = Healthy,
    };

    /// <summary>
    /// Validates executor-mode configuration. Throws
    /// <see cref="InvalidOperationException"/> on misconfiguration so a bad
    /// host id or URL fails fast at startup instead of registering garbage.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(HostId))
            throw new InvalidOperationException(
                "CodeyBox:Executor:HostId is required to run executor mode; it must be a stable, non-empty host id.");
        if (HostId.Trim().Length > ExecutorRegistration.MaxHostIdLength)
            throw new InvalidOperationException(
                $"CodeyBox:Executor:HostId must be at most {ExecutorRegistration.MaxHostIdLength} characters.");
        if (HostId.Trim().Any(char.IsControl))
            throw new InvalidOperationException("CodeyBox:Executor:HostId must not contain control characters.");
        if (string.IsNullOrWhiteSpace(OrchestratorBaseUrl))
            throw new InvalidOperationException(
                "CodeyBox:Executor:OrchestratorBaseUrl is required to run executor mode.");
        if (!Uri.TryCreate(OrchestratorBaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                "CodeyBox:Executor:OrchestratorBaseUrl must be an absolute http(s) URL.");
        if (MaxConcurrentSandboxes is { } cap
            && (cap < 0 || cap > ExecutorRegistration.MaxDeclaredCapacity))
            throw new InvalidOperationException(
                $"CodeyBox:Executor:MaxConcurrentSandboxes must be between 0 and {ExecutorRegistration.MaxDeclaredCapacity}.");
        ValidateEntries(AllowedNetworkProfiles, nameof(AllowedNetworkProfiles));
        ValidateEntries(DeclaredCredentials, nameof(DeclaredCredentials));
        ValidateEntries(DeclaredCapabilities, nameof(DeclaredCapabilities));
        if (HeartbeatInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Executor:HeartbeatInterval must be positive.");
        if (RequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Executor:RequestTimeout must be positive.");
        if (AgentCredentialProbeTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Executor:AgentCredentialProbeTimeout must be positive.");
        if (!Enum.IsDefined(DisconnectPolicy))
            throw new InvalidOperationException("CodeyBox:Executor:DisconnectPolicy names an unknown policy.");
        ValidatePhaseExecution();
        var provider = (LocalSandboxProvider ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider))
            throw new InvalidOperationException(
                $"CodeyBox:Executor:LocalSandboxProvider must name a sandbox provider kind. Valid: {ValidProviderKinds}.");
        ValidateAdditionalProviderKinds();
    }

    private void ValidateAdditionalProviderKinds()
    {
        if (SandboxProviders.Count > MaxDeclaredProviderKinds)
            throw new InvalidOperationException(
                $"CodeyBox:Executor:SandboxProviders may contain at most {MaxDeclaredProviderKinds} entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SandboxProviders)
        {
            var normalized = (raw ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
                throw new InvalidOperationException(
                    "CodeyBox:Executor:SandboxProviders entries must be non-empty.");
            if (normalized.Length > ExecutorRegistration.MaxDeclaredEntryLength)
                throw new InvalidOperationException(
                    $"CodeyBox:Executor:SandboxProviders entries must be at most {ExecutorRegistration.MaxDeclaredEntryLength} characters.");
            if (!seen.Add(normalized))
                throw new InvalidOperationException(
                    $"CodeyBox:Executor:SandboxProviders names '{normalized}' more than once.");
        }
    }

    private void ValidatePhaseExecution()
    {
        if (!string.IsNullOrWhiteSpace(PhaseStagingRoot))
        {
            if (PhaseStagingRoot.Trim().Length > MaxPhaseStagingRootLength)
                throw new InvalidOperationException(
                    $"CodeyBox:Executor:PhaseStagingRoot must be at most {MaxPhaseStagingRootLength} characters.");
            if (PhaseStagingRoot.Any(char.IsControl))
                throw new InvalidOperationException("CodeyBox:Executor:PhaseStagingRoot must not contain control characters.");
            if (!Path.IsPathFullyQualified(PhaseStagingRoot.Trim()))
                throw new InvalidOperationException("CodeyBox:Executor:PhaseStagingRoot must be an absolute path.");
        }
        if (!string.IsNullOrEmpty(PhaseSandboxImageReference))
        {
            if (PhaseSandboxImageReference.Length > MaxPhaseSandboxImageReferenceLength)
                throw new InvalidOperationException(
                    $"CodeyBox:Executor:PhaseSandboxImageReference must be at most {MaxPhaseSandboxImageReferenceLength} characters.");
            if (PhaseSandboxImageReference.Any(char.IsControl))
                throw new InvalidOperationException("CodeyBox:Executor:PhaseSandboxImageReference must not contain control characters.");
        }
        if (MaxCachedPhaseResults < 1 || MaxCachedPhaseResults > MaxCachedPhaseResultsLimit)
            throw new InvalidOperationException(
                $"CodeyBox:Executor:MaxCachedPhaseResults must be between 1 and {MaxCachedPhaseResultsLimit}.");
        if (PhaseResultCacheTtl <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Executor:PhaseResultCacheTtl must be positive.");
    }

    private static void ValidateEntries(List<string> entries, string fieldName)
    {
        if (entries.Count > ExecutorRegistration.MaxDeclaredEntries)
            throw new InvalidOperationException(
                $"CodeyBox:Executor:{fieldName} may contain at most {ExecutorRegistration.MaxDeclaredEntries} entries.");
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw new InvalidOperationException($"CodeyBox:Executor:{fieldName} entries must be non-empty.");
            if (entry.Trim().Length > ExecutorRegistration.MaxDeclaredEntryLength)
                throw new InvalidOperationException(
                    $"CodeyBox:Executor:{fieldName} entries must be at most {ExecutorRegistration.MaxDeclaredEntryLength} characters.");
        }
    }
}
