using CodeyBox.Core;

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
    /// Name of the environment variable carrying the orchestrator API key.
    /// The key itself is never stored in config files and never logged.
    /// </summary>
    public string ApiKeyEnvVar { get; set; } = "CODEYBOX_API_KEY";

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

    /// <summary>Names of the agent credential sets this host holds.</summary>
    public List<string> DeclaredCredentials { get; set; } = [];

    /// <summary>When true the host drains: registers and heartbeats but is never selected for new placements.</summary>
    public bool Cordoned { get; set; }

    /// <summary>Operator health gate. False routes new placements away without removing the registration.</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>How often the executor heartbeats into the worker registry. Default 15 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Per-request timeout for registration and heartbeat calls. Default 20 s.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Local sandbox backend the executor provisions through. Exact-match,
    /// one of <c>process</c> (plain-process dev runner — UNSAFE, local testing
    /// only) or <c>bubblewrap</c> (namespace isolation, shared kernel).
    /// VM-backed backends stay orchestrator-side; the executor runs work where
    /// it runs, never by remote-driving another machine.
    /// </summary>
    public string LocalSandboxProvider { get; set; } = "process";

    /// <summary>
    /// What happens to a running sandbox when the orchestrator connection
    /// drops. Only <see cref="ExecutorDisconnectPolicy.RetainSandboxForResume"/>
    /// exists in this item: retain for resumption, reconcile on reconnect.
    /// </summary>
    public ExecutorDisconnectPolicy DisconnectPolicy { get; set; } = ExecutorDisconnectPolicy.RetainSandboxForResume;

    /// <summary>
    /// Builds the registration assertion sent on connect. Registration is the
    /// executor's claim of what it can run; the orchestrator decides placement.
    /// </summary>
    public ExecutorRegistration ToRegistration() => new()
    {
        HostId = HostId.Trim(),
        MaxConcurrentSandboxes = MaxConcurrentSandboxes,
        AllowedNetworkProfiles = [.. AllowedNetworkProfiles],
        DeclaredCredentials = [.. DeclaredCredentials],
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
        if (HeartbeatInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Executor:HeartbeatInterval must be positive.");
        if (RequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("CodeyBox:Executor:RequestTimeout must be positive.");
        if (!Enum.IsDefined(DisconnectPolicy))
            throw new InvalidOperationException("CodeyBox:Executor:DisconnectPolicy names an unknown policy.");
        var provider = (LocalSandboxProvider ?? "").Trim().ToLowerInvariant();
        if (provider is not ("process" or "bubblewrap"))
            throw new InvalidOperationException(
                "CodeyBox:Executor:LocalSandboxProvider must be 'process' or 'bubblewrap'.");
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
