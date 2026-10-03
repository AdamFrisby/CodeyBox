using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.E2bSandboxPlugin;

/// <summary>
/// CodeyBox sandbox provider plugin for E2B sandboxes: hosted Firecracker
/// microVMs created on demand over the E2B REST control plane, with
/// pause/resume persistence, snapshots, and opt-in preview URLs. Disabled
/// unless the operator allowlists and enables <c>codeybox.e2b-sandbox</c>.
///
/// <para>Containment posture (host-owned, not claimed here): this is a hosted
/// backend — the guest runs on infrastructure CodeyBox does not control, so
/// the host classifies every plugin kind
/// <see cref="EgressEnforcementLocation.NotEnforced"/>. This provider may
/// only serve sandboxes with no named network profile; profiled work is
/// refused by placement before it reaches this provider, and
/// <see cref="CreateAsync"/> refuses it again at the sink. It must never be
/// described as isolation.</para>
///
/// <para>Credentials come from the credential chain (the API key is resolved
/// from <see cref="E2bSandboxOptions.ApiKeyEnvVar"/> at use time), never from
/// configuration files. Admission and capacity stay host-owned: member gates
/// size admission from <see cref="SandboxMember.Capacity"/> and the
/// composition root wraps this provider exactly like a built-in one. This
/// provider never creates sandboxes outside <see cref="CreateAsync"/> and
/// reports live load through <see cref="SnapshotActiveSandboxes"/> so
/// least-loaded placement stays accurate.</para>
/// </summary>
[CodeyBoxPlugin(
    id: E2bSandboxOptions.PluginId,
    displayName: "CodeyBox: E2B Sandboxes",
    minHostApiVersion: "1.0")]
public sealed class E2bSandboxProvider : ISandboxProvider, ISuspendingSandboxProvider,
    IActiveSandboxProvider, IPluginInitializer
{
    /// <summary>
    /// Guest path reserved for credential files. Must match the host's
    /// <c>SandboxConventions.CredentialsDir</c> (the plugin boundary forbids
    /// referencing that assembly; equality is enforced by test).
    /// </summary>
    internal const string CredentialMountPath = "/run/codeybox/creds";

    private static readonly IReadOnlySet<string> KnownSandboxDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "e2b.app", "e2b.dev", "e2b.pro",
    };

    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly E2bApiClient _client;
    private readonly TimeProvider _timeProvider;

    private Func<E2bSandboxOptions> _readOptions;
    private ILogger _log;

    /// <summary>DI constructor used by the plugin host.</summary>
    public E2bSandboxProvider(IConfiguration configuration, ILogger<E2bSandboxProvider> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = logger;
        var section = configuration.GetSection($"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}");
        _readOptions = () => E2bSandboxOptions.FromConfiguration(section);
        _client = new E2bApiClient(new HttpClient());
    }

    /// <summary>Test constructor with full control over options, transport, and clock.</summary>
    internal E2bSandboxProvider(
        Func<E2bSandboxOptions> readOptions,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _client = new E2bApiClient(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Provider kind. Normalised (trimmed, lowercase) and matched by exact ordinal equality.</summary>
    public string Name => E2bSandboxOptions.ProviderKind;

    /// <inheritdoc/>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        try
        {
            return IsManaged(new E2bSandboxDto { SandboxID = name }, _readOptions().NamePrefix);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>E2B guests are Firecracker microVMs with a separate guest kernel.</summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honestly declared capabilities: suspend/resume (pause + connect),
    /// teardown (stop-and-preserve, dispose), and port publishing (E2B preview
    /// URLs — declared so placement can require it, but never enabled by
    /// default; <see cref="E2bSandboxOptions.EnablePreviewUrls"/> gates actual
    /// minting). Baseline bake, cache seeding, and disk guard are deliberately
    /// absent — placement refuses work requiring them rather than failing deep
    /// inside a phase.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities => [
        SandboxCapabilities.SuspendResume,
        SandboxCapabilities.Teardown,
        SandboxCapabilities.PortPublishing,
    ];

    /// <summary>Live sandbox count for observability and tests.</summary>
    internal int ActiveSandboxCount => _activeSandboxes.Count;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(context);
        _log = context.Logger;
        var section = context.ScopedConfig;
        _readOptions = () => E2bSandboxOptions.FromConfiguration(section);

        // Fail the host fast on operator misconfiguration that can be checked
        // without network I/O. Key presence is checked here too: without it
        // every placement would fail, so refusing startup is the honest signal.
        var opts = _readOptions();
        ValidateOptions(opts);
        ResolveApiKey(opts);

        _log.LogWarning(
            "E2B sandbox provider '{Kind}': egress NOT enforced — no host network isolation. " +
            "It may only serve sandboxes with no named network profile and must never be described as isolation.",
            E2bSandboxOptions.ProviderKind);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Derives the per-sandbox envd gateway base URL
    /// (<c>https://{envdPort}-{sandboxId}.{domain}</c>). Host format verified
    /// against the E2B JS SDK (<c>getHost</c>); the sandbox id is an opaque
    /// service-issued token, so it is allowlist-validated here
    /// (<c>^[A-Za-z0-9-]{1,128}$</c>) before interpolation to keep a crafted
    /// id from breaking out of the DNS label (SSRF + token leak).
    /// </summary>
    internal static string EnvdBaseUrl(E2bSandboxOptions opts, string sandboxId)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ValidateSandboxId(sandboxId);
        var scheme = opts.ApiBaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        return $"{scheme}://{opts.EnvdPort}-{sandboxId}.{opts.SandboxDomain}";
    }

    /// <summary>
    /// Exact-match allowlist for E2B sandbox ids interpolated into DNS hosts
    /// (envd gateway and preview hosts). Only ASCII letters, digits, hyphens,
    /// and underscores (1…128 chars): enough to keep the id inside one DNS
    /// label (no dots, slashes, or URL metacharacters), while accepting the
    /// recorded service shape (<c>sb_abc123</c>). Underscores are not strict
    /// LDH but cannot break out of the label.
    /// </summary>
    internal static bool IsValidSandboxId(string? sandboxId)
    {
        if (string.IsNullOrWhiteSpace(sandboxId) || sandboxId.Length > 128)
        {
            return false;
        }

        foreach (var c in sandboxId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rejects sandbox ids that would escape the DNS-label host sink.</summary>
    internal static void ValidateSandboxId(string sandboxId)
    {
        if (!IsValidSandboxId(sandboxId))
        {
            throw new ArgumentException(
                "E2B sandbox id must match ^[A-Za-z0-9_-]{1,128}$ to stay inside the envd DNS label.",
                nameof(sandboxId));
        }
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = _readOptions();
        ValidateOptions(opts);
        ValidateSpec(spec);
        var apiKey = ResolveApiKey(opts);

        var sandboxName = BuildSandboxName(opts.NamePrefix);
        _ = ValidateEnvironment(spec.Environment);
        var request = new E2bCreateSandboxRequest
        {
            TemplateID = opts.TemplateId,
            Timeout = Math.Clamp(opts.SandboxTimeoutSeconds, 60, 86400),
            Metadata = new Dictionary<string, string>
            {
                [E2bSandboxOptions.ManagedMetadataKey] = E2bSandboxOptions.ManagedMetadataValue,
                [E2bSandboxOptions.ProviderMetadataKey] = E2bSandboxOptions.ProviderKind,
                ["codeybox-name"] = sandboxName,
                ["codeybox-purpose"] = spec.Purpose.ToString(),
            },
        };

        E2bSandboxDto created;
        try
        {
            created = await _client.CreateSandboxAsync(opts.ApiBaseUrl, apiKey, request, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToDeferred(ex, "create-sandbox");
        }

        if (!IsValidSandboxId(created.SandboxID))
        {
            // Control-plane paths escape the id (Uri.EscapeDataString), so
            // best-effort cleanup is safe even for a DNS-invalid id; the id
            // never reaches a DNS host because EnvdBaseUrl rejects it.
            if (!string.IsNullOrWhiteSpace(created.SandboxID))
            {
                await DeleteBestEffortAsync(opts, apiKey, created.SandboxID).ConfigureAwait(false);
            }

            throw ToDeferred(new E2bApiException(null, "malformed-response", "create-sandbox returned an invalid sandbox id"), "create-sandbox");
        }

        if (string.IsNullOrWhiteSpace(created.EnvdAccessToken))
        {
            await DeleteBestEffortAsync(opts, apiKey, created.SandboxID).ConfigureAwait(false);
            throw ToDeferred(new E2bApiException(null, "malformed-response", "create-sandbox returned no envd access token"), "create-sandbox");
        }

        var sandbox = new E2bSandbox(
            created.SandboxID,
            created.EnvdAccessToken,
            _client,
            _readOptions,
            () => ResolveApiKey(_readOptions()),
            spec,
            _timeProvider,
            _log,
            id => _activeSandboxes.TryRemove(id, out _));
        var entry = new ActiveSandboxEntry(spec.TimingWorkItemId, sandbox);
        _activeSandboxes[created.SandboxID] = entry;

        try
        {
            await WaitForRunningAsync(opts, apiKey, created.SandboxID, ct).ConfigureAwait(false);
            await _client.CheckEnvdHealthAsync(EnvdBaseUrl(opts, created.SandboxID), created.EnvdAccessToken, opts.ApiTimeout, ct).ConfigureAwait(false);
            var writableMounts = await StageMountsAsync(opts, sandbox, spec, ct).ConfigureAwait(false);
            sandbox.SetWritableMounts(writableMounts);
            await _client.ExtendTimeoutAsync(opts.ApiBaseUrl, apiKey, created.SandboxID, opts.SandboxTimeoutSeconds, opts.ApiTimeout, ct).ConfigureAwait(false);
            return sandbox;
        }
        catch (E2bApiException ex)
        {
            await DeleteBestEffortAsync(opts, apiKey, created.SandboxID).ConfigureAwait(false);
            _activeSandboxes.TryRemove(created.SandboxID, out _);
            throw ToDeferred(ex, "create-sandbox");
        }
        catch (Exception)
        {
            await DeleteBestEffortAsync(opts, apiKey, created.SandboxID).ConfigureAwait(false);
            _activeSandboxes.TryRemove(created.SandboxID, out _);
            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = _readOptions();
        ValidateOptions(opts);
        var apiKey = ResolveApiKey(opts);

        List<E2bSandboxDto> sandboxes;
        try
        {
            sandboxes = (await _client.ListSandboxesAsync(opts.ApiBaseUrl, apiKey, opts.ApiTimeout, ct).ConfigureAwait(false)).ToList();
        }
        catch (E2bApiException ex)
        {
            throw ToDeferred(ex, "list-sandboxes");
        }

        var managed = new List<ManagedSandboxInfo>();
        foreach (var sandbox in sandboxes)
        {
            if (!IsManaged(sandbox, opts.NamePrefix))
            {
                continue;
            }

            managed.Add(new ManagedSandboxInfo(
                sandbox.SandboxID,
                null,
                null,
                _activeSandboxes.ContainsKey(sandbox.SandboxID),
                HasPreemptMarker: false,
                IsSuspendLifecycleOrFrozen: string.Equals(sandbox.LifecycleState, "paused", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sandbox.LifecycleState, "pausing", StringComparison.OrdinalIgnoreCase)));
        }

        return managed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);
        var apiKey = ResolveApiKey(opts);

        try
        {
            await _client.DeleteSandboxAsync(opts.ApiBaseUrl, apiKey, name, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
    }

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);
        var apiKey = ResolveApiKey(opts);

        E2bSandboxDto? sandbox;
        try
        {
            sandbox = await _client.GetSandboxAsync(opts.ApiBaseUrl, apiKey, name, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToDeferred(ex, "resume-sandbox");
        }

        if (sandbox is null)
        {
            // "Sandbox not found" is non-fatal so the startup handler can clear
            // bookkeeping for items whose sandbox no longer exists.
            return;
        }

        if (string.Equals(sandbox.LifecycleState, "running", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(sandbox.LifecycleState, "paused", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        E2bSandboxDto resumed;
        try
        {
            resumed = await _client.ResumeSandboxAsync(
                opts.ApiBaseUrl, apiKey, name, opts.SandboxTimeoutSeconds, E2bApiClient.E2bApiClientWaitTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
        }
        catch (E2bApiException ex)
        {
            throw ToDeferred(ex, "resume-sandbox");
        }

        if (_activeSandboxes.TryGetValue(name, out var entry) && !string.IsNullOrWhiteSpace(resumed.EnvdAccessToken))
        {
            entry.Sandbox.RefreshAccessToken(resumed.EnvdAccessToken);
        }

        await WaitForRunningAsync(opts, apiKey, name, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Where(static entry => entry.WorkItemId is not null)
            .Select(static entry => (entry.WorkItemId!.Value, (IShutdownTeardownSandbox)entry.Sandbox))
            .ToList();

    internal static void ValidateOptions(E2bSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!Uri.TryCreate(opts.ApiBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && !(opts.AllowUnsafeHttp && baseUri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:ApiBaseUrl must be an absolute https URL" +
                (opts.AllowUnsafeHttp ? " (http is allowed only with AllowUnsafeHttp for local mock servers)." : "."));
        }

        if (string.IsNullOrWhiteSpace(opts.ApiKeyEnvVar))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:ApiKeyEnvVar must be set.");
        }

        if (!KnownSandboxDomains.Contains(opts.SandboxDomain.Trim().TrimEnd('.')))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:SandboxDomain '{opts.SandboxDomain}' is unknown. " +
                $"Valid: {string.Join(", ", KnownSandboxDomains.OrderBy(static s => s, StringComparer.Ordinal))}.");
        }

        if (opts.EnvdPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:EnvdPort must be 1…65535 (E2B default 49983).");
        }

        if (string.IsNullOrWhiteSpace(opts.TemplateId) || opts.TemplateId.Length > 128)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:TemplateId must be set (max 128 chars).");
        }

        if (string.IsNullOrWhiteSpace(opts.NamePrefix)
            || opts.NamePrefix.Length > 48
            || opts.NamePrefix.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || !char.IsAsciiLetter(opts.NamePrefix[0]))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:NamePrefix must start with a letter " +
                "and contain only ASCII letters, digits, and hyphens (max 48 chars).");
        }

        if (opts.SandboxTimeoutSeconds is < 60 or > 86400)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:SandboxTimeoutSeconds must be 60…86400.");
        }

        if (opts.EnablePreviewUrls && opts.AllowedPreviewPorts.Count == 0)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{E2bSandboxOptions.PluginId}:EnablePreviewUrls requires at least one AllowedPreviewPorts entry; " +
                "an empty allowlist would declare port-publishing while publishing nothing.");
        }
    }

    internal static void ValidateSpec(SandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
        {
            throw new NotSupportedException(
                "The e2b provider does not support graphical sandboxes: E2B sandboxes expose no desktop, display, or VNC server.");
        }

        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            throw new InvalidOperationException(
                $"Sandbox provider kind '{E2bSandboxOptions.ProviderKind}' cannot serve network profile " +
                $"'{spec.Network.ProfileName.Trim()}': the kind is classified 'NotEnforced' (no host-enforced egress filtering). " +
                "A 'NotEnforced' provider may only serve sandboxes with no named network profile.");
        }
    }

    internal static Dictionary<string, string> ValidateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        const int MaxVariables = 256;
        if (environment.Count > MaxVariables)
        {
            throw new ArgumentException($"Sandbox environment exceeds {MaxVariables} variables.", nameof(environment));
        }

        var validated = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(environment));
            validated[key] = value ?? string.Empty;
        }

        return validated;
    }

    internal string ResolveApiKey(E2bSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            return opts.ApiKey;
        }

        var apiKey = Environment.GetEnvironmentVariable(opts.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"E2B API key environment variable '{opts.ApiKeyEnvVar}' is not set. " +
                "Set it in the orchestrator process environment (never in a configuration file).");
        }

        return apiKey;
    }

    private static string BuildSandboxName(string prefix)
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return $"{prefix.TrimEnd('-').ToLowerInvariant()}-{suffix}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000}";
    }

    private async Task WaitForRunningAsync(E2bSandboxOptions opts, string apiKey, string sandboxId, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + opts.WaitForRunningTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToDeferred(
                    new E2bApiException(null, "timeout", $"sandbox {sandboxId} did not reach running in time"),
                    "wait-for-running");
            }

            E2bSandboxDto? sandbox;
            try
            {
                sandbox = await _client.GetSandboxAsync(opts.ApiBaseUrl, apiKey, sandboxId, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (E2bApiException ex)
            {
                throw ToDeferred(ex, "wait-for-running");
            }

            if (sandbox is not null && string.Equals(sandbox.LifecycleState, "running", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (sandbox is not null
                && (string.Equals(sandbox.LifecycleState, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sandbox.LifecycleState, "error", StringComparison.OrdinalIgnoreCase)))
            {
                throw ToDeferred(
                    new E2bApiException(null, "terminal-state", $"sandbox {sandboxId} entered '{sandbox.LifecycleState}' while starting"),
                    "wait-for-running");
            }

            try
            {
                await Task.Delay(opts.StatusPollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<IReadOnlyList<E2bWritableMountSync>> StageMountsAsync(
        E2bSandboxOptions opts, E2bSandbox sandbox, SandboxSpec spec, CancellationToken ct)
    {
        var writable = new List<E2bWritableMountSync>();
        long totalBytes = 0;
        var totalFiles = 0;

        foreach (var mount in spec.Mounts)
        {
            ct.ThrowIfCancellationRequested();
            HostedGuestPath.ValidateAbsolute(mount.SandboxPath);

            if (mount.Tmpfs)
            {
                if (string.Equals(mount.SandboxPath, CredentialMountPath, StringComparison.Ordinal)
                    || mount.SandboxPath.StartsWith(CredentialMountPath + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"E2B sandboxes have no tmpfs and their disk is hosted storage CodeyBox does not control: credential mount '{mount.SandboxPath}' would persist " +
                        "on third-party infrastructure. Pass credentials as environment variables instead.");
                }

                if (!opts.AllowPersistentTmpfsDowngrade)
                {
                    throw new InvalidOperationException(
                        $"E2B sandboxes have no tmpfs: mount '{mount.SandboxPath}' needs AllowPersistentTmpfsDowngrade " +
                        "to downgrade to a persistent guest directory.");
                }

                _log.LogWarning(
                    "E2B sandbox {SandboxId}: tmpfs mount {Path} downgraded to a persistent guest directory.",
                    sandbox.Id, mount.SandboxPath);
                await sandbox.RunInternalAsync(opts, ["mkdir", "-p", mount.SandboxPath], "/", ct).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mount.HostPath))
            {
                await sandbox.RunInternalAsync(opts, ["mkdir", "-p", mount.SandboxPath], "/", ct).ConfigureAwait(false);
                continue;
            }

            var hostRoot = Path.GetFullPath(mount.HostPath);
            if (!File.Exists(hostRoot) && !Directory.Exists(hostRoot))
            {
                throw new SandboxMountSourceMissingException(
                    mount.HostPath,
                    $"E2B mount source '{mount.HostPath}' does not exist at stage time.");
            }

            var staged = await StageHostPathAsync(opts, sandbox, mount.SandboxPath, hostRoot, ct).ConfigureAwait(false);
            totalBytes += staged.Bytes;
            totalFiles += staged.Files;
            if (totalBytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"E2B mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            if (totalFiles > opts.MaxStageFileCount)
            {
                throw new InvalidOperationException(
                    $"E2B mount staging exceeds {opts.MaxStageFileCount} files in total; refusing to stage.");
            }

            if (!mount.ReadOnly)
            {
                writable.Add(new E2bWritableMountSync(mount.SandboxPath, hostRoot));
            }
        }

        return writable;
    }

    private async Task<(long Bytes, int Files)> StageHostPathAsync(
        E2bSandboxOptions opts,
        E2bSandbox sandbox,
        string guestRoot,
        string hostRoot,
        CancellationToken ct)
    {
        await sandbox.RunInternalAsync(opts, ["mkdir", "-p", guestRoot], "/", ct).ConfigureAwait(false);

        var files = new List<(string HostFull, string Relative)>();
        if (File.Exists(hostRoot))
        {
            files.Add((hostRoot, Path.GetFileName(hostRoot)));
        }
        else
        {
            foreach (var staged in HostedMountStaging.CollectStageFiles(hostRoot, "E2B", opts.MaxStageFileCount, ct))
            {
                files.Add(staged);
            }
        }

        long bytes = 0;
        foreach (var (hostFull, relative) in files)
        {
            ct.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(hostFull);
            if (!fileInfo.Exists || (fileInfo.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                continue;
            }

            if (fileInfo.Length > opts.MaxStageFileBytes)
            {
                throw new InvalidOperationException(
                    $"E2B mount file '{relative}' is {fileInfo.Length} bytes (limit {opts.MaxStageFileBytes}); refusing to stage.");
            }

            var guestPath = guestRoot.TrimEnd('/') + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            HostedGuestPath.ValidateAbsolute(guestPath);

            HostedMountStaging.ThrowIfSymlinked(hostRoot, hostFull, relative, "E2B");

            byte[] content;
            try
            {
                using var stream = File.OpenRead(hostFull);
                using var sink = new MemoryStream();
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (sink.Length + read > opts.MaxStageFileBytes)
                    {
                        throw new InvalidOperationException($"E2B mount file '{relative}' exceeds the stage limit while reading.");
                    }

                    sink.Write(buffer, 0, read);
                }

                content = sink.ToArray();
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"E2B mount file '{relative}' cannot be read: {ex.GetType().Name}.", ex);
            }

            bytes += content.Length;
            if (bytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"E2B mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            try
            {
                await sandbox.WriteFileBytesAsync(guestPath, content, ct).ConfigureAwait(false);
            }
            catch (SandboxExecutionUnavailableException ex)
            {
                throw ToDeferred(new E2bApiException(null, "unreachable", $"stage-mount: {ex.Message}", ex), "stage-mount");
            }
        }

        return (bytes, files.Count);
    }

    private static bool IsManaged(E2bSandboxDto sandbox, string namePrefix)
    {
        _ = namePrefix;
        return sandbox.Metadata is not null
            && sandbox.Metadata.TryGetValue(E2bSandboxOptions.ManagedMetadataKey, out var managed)
            && string.Equals(managed, E2bSandboxOptions.ManagedMetadataValue, StringComparison.Ordinal)
            && sandbox.Metadata.TryGetValue(E2bSandboxOptions.ProviderMetadataKey, out var provider)
            && string.Equals(provider, E2bSandboxOptions.ProviderKind, StringComparison.Ordinal);
    }

    private async Task DeleteBestEffortAsync(E2bSandboxOptions opts, string apiKey, string sandboxId)
    {
        try
        {
            await _client.DeleteSandboxAsync(opts.ApiBaseUrl, apiKey, sandboxId, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "E2B sandbox {SandboxId}: best-effort delete after failed create failed.", sandboxId);
        }
    }

    private SandboxProvisioningDeferredException ToDeferred(E2bApiException ex, string operation)
    {
        var classification = E2bFailureClassification.Classify(ex.StatusCode, operation);
        return new SandboxProvisioningDeferredException(
            E2bSandboxOptions.ProviderKind,
            operation,
            ex.ErrorClass,
            TruncateDetail(ex.Detail),
            classification.RecheckIn,
            innerException: ex);
    }

    private static string TruncateDetail(string detail) =>
        detail.Length > 512 ? detail[..512] : detail;

    private sealed record ActiveSandboxEntry(WorkItemId? WorkItemId, E2bSandbox Sandbox);
}
