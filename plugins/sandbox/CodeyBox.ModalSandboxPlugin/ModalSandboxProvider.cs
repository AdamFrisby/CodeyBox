using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.ModalPlugin;

/// <summary>
/// CodeyBox sandbox provider plugin for Modal Sandboxes: hosted containers
/// created on demand over Modal's control-plane API, with custom images,
/// filesystem snapshots, high concurrency, and streaming execution. Disabled
/// unless the operator allowlists and enables <c>codeybox.modal</c>.
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
/// <para>Credentials come from the credential chain (the token id and secret
/// are resolved from <see cref="ModalSandboxOptions.TokenIdEnvironmentVariable"/>
/// and <see cref="ModalSandboxOptions.TokenSecretEnvironmentVariable"/> at use
/// time), never from configuration files. Admission and capacity stay
/// host-owned: member gates size admission from
/// <see cref="SandboxMember.Capacity"/> and the composition root wraps this
/// provider exactly like a built-in one.</para>
/// </summary>
[CodeyBoxPlugin(
    id: ModalSandboxOptions.PluginId,
    displayName: "CodeyBox: Modal Sandboxes",
    minHostApiVersion: "1.0")]
public sealed class ModalSandboxProvider : ISandboxProvider, IActiveSandboxProvider, IPluginInitializer
{
    /// <summary>
    /// Guest path reserved for credential files. Must match the host's
    /// <c>SandboxConventions.CredentialsDir</c> (the plugin boundary forbids
    /// referencing that assembly; equality is enforced by test).
    /// </summary>
    internal const string CredentialMountPath = "/run/codeybox/creds";

    private const int MaxCreateEnvironmentVariables = 256;

    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly ModalApiClient _client;
    private readonly TimeProvider _timeProvider;

    private Func<ModalSandboxOptions> _readOptions;
    private ILogger _log;

    /// <summary>DI constructor used by the plugin host.</summary>
    public ModalSandboxProvider(IConfiguration configuration, ILogger<ModalSandboxProvider> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = logger;
        var section = configuration.GetSection($"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}");
        _readOptions = () => ModalSandboxOptions.FromConfiguration(section);
        _client = new ModalApiClient(new HttpClient());
    }

    /// <summary>Test constructor with full control over options, transport, and clock.</summary>
    internal ModalSandboxProvider(
        Func<ModalSandboxOptions> readOptions,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _client = new ModalApiClient(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Provider kind. Normalised (trimmed, lowercase) and matched by exact ordinal equality.</summary>
    public string Name => ModalSandboxOptions.ProviderKind;

    /// <summary>
    /// Conservative isolation claim for workload-trust routing: Modal sandboxes
    /// are containers scheduled on shared workers, so they are treated as a
    /// shared-kernel boundary until a reviewed change says otherwise.
    /// Untrusted workloads in production still pass the normal workload-trust
    /// gate. This says nothing about egress, which stays
    /// <see cref="EgressEnforcementLocation.NotEnforced"/> (host-owned).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.SharedKernel;

    /// <summary>
    /// Honestly declared capabilities: teardown (snapshot-and-terminate
    /// preserve, terminate-on-dispose) only. Baseline bake, suspend/resume,
    /// disk guard, cache seeding, and port publishing are deliberately absent —
    /// placement refuses work requiring them rather than failing deep inside a
    /// phase. Custom images and restore snapshots arrive via operator
    /// configuration (<c>ImageRef</c>/<c>SnapshotId</c>), not provider baking.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.Teardown];

    /// <summary>
    /// Whether a sandbox with this name could belong to this provider. Pure
    /// and cheap: prefix match only, never touches the network. Fails closed
    /// (true) on misconfiguration so disposal verification stays conservative.
    /// </summary>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        try
        {
            return IsManagedName(name, _readOptions().NamePrefix);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Live sandbox count for observability and tests.</summary>
    internal int ActiveSandboxCount => _activeSandboxes.Count;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _log = context.Logger;
        var section = context.ScopedConfig;
        _readOptions = () => ModalSandboxOptions.FromConfiguration(section);

        var opts = _readOptions();
        ValidateOptions(opts);
        ResolveCredentials(opts);

        _log.LogWarning(
            "Modal sandbox provider '{Kind}': egress NOT enforced — no host network isolation. " +
            "It may only serve sandboxes with no named network profile and must never be described as isolation.",
            ModalSandboxOptions.ProviderKind);
        return Task.CompletedTask;
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = _readOptions();
        ValidateOptions(opts);
        ValidateSpec(spec);
        var credentials = ResolveCredentials(opts);

        if (spec.RecoveryLease is not null)
        {
            throw new InvalidOperationException(
                $"Sandbox provider kind '{ModalSandboxOptions.ProviderKind}' cannot adopt retained sandboxes: " +
                "it provisions fresh sandboxes and implements no recovery-lease adoption path. " +
                "Refusing instead of substituting a fresh sandbox for retained state.");
        }

        var sandboxName = BuildSandboxName(opts.NamePrefix);
        var environment = ValidateEnvironment(spec.Environment, opts.MaxEnvironmentBytes);

        var request = new ModalSandboxCreateRequest(
            sandboxName,
            opts.AppName,
            ImageRef: string.IsNullOrWhiteSpace(EffectiveSnapshotId(spec, opts)) ? opts.ImageRef : null,
            SnapshotId: EffectiveSnapshotId(spec, opts),
            CpuCount: spec.Limits.CpuCount ?? opts.CpuCount,
            MemoryMiB: spec.Limits.MemoryBytes is { } memoryBytes && memoryBytes > 0
                ? Math.Max(1, (int)Math.Ceiling(memoryBytes / (1024.0 * 1024.0)))
                : opts.MemoryMiB,
            IdleTimeoutSeconds: Math.Clamp(opts.IdleTimeoutSeconds, 60, 86400),
            Environment: environment.Count > 0 ? environment : null,
            Metadata: new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = ModalSandboxOptions.ProviderKind,
                ["codeybox-purpose"] = spec.Purpose.ToString(),
            },
            AllowedHosts: spec.Network.AllowedHosts.Count > 0 ? spec.Network.AllowedHosts.ToList() : null);

        ModalSandboxView created;
        try
        {
            created = await _client.CreateSandboxAsync(opts.ApiBaseUrl, credentials, request, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (ModalApiException ex)
        {
            throw ToDeferred(ex, "create-sandbox");
        }

        if (string.IsNullOrWhiteSpace(created.Id))
        {
            throw ToDeferred(new ModalApiException(null, "malformed-response", "create-sandbox returned no sandbox id"), "create-sandbox");
        }

        var sandbox = new ModalSandbox(
            created.Id,
            sandboxName,
            _client,
            _readOptions,
            () => ResolveCredentials(_readOptions()),
            spec,
            _timeProvider,
            _log,
            id =>
            {
                _activeSandboxes.TryRemove(id, out _);
                SandboxLiveCounter.Decrement();
            });
        var entry = new ActiveSandboxEntry(spec.TimingWorkItemId, sandbox);
        _activeSandboxes[created.Id] = entry;
        SandboxLiveCounter.Increment();

        try
        {
            await WaitForRunningAsync(opts, credentials, created.Id, ct).ConfigureAwait(false);
            var writableMounts = await StageMountsAsync(opts, credentials, sandbox, spec, ct).ConfigureAwait(false);
            sandbox.SetWritableMounts(writableMounts);
            return sandbox;
        }
        catch (OperationCanceledException)
        {
            var removed = await TerminateAndProveGoneAsync(opts, credentials, created.Id).ConfigureAwait(false);
            if (!removed)
            {
                _log.LogError(
                    "Modal sandbox {SandboxId}: cancelled create could not prove removal — it may still be running on the service.",
                    created.Id);
            }

            _activeSandboxes.TryRemove(created.Id, out _);
            SandboxLiveCounter.Decrement();
            throw;
        }
        catch (Exception ex)
        {
            var removed = await TerminateAndProveGoneAsync(opts, credentials, created.Id).ConfigureAwait(false);
            _activeSandboxes.TryRemove(created.Id, out _);
            SandboxLiveCounter.Decrement();
            if (!removed)
            {
                throw ToDeferred(
                    new ModalApiException(null, "leak-risk", $"create failed and terminate did not prove sandbox {created.Id} was removed: {ex.Message}"),
                    "create-sandbox");
            }

            if (ex is ModalApiException apiEx)
            {
                throw ToDeferred(apiEx, "create-sandbox");
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = _readOptions();
        ValidateOptions(opts);
        var credentials = ResolveCredentials(opts);

        var managed = new List<ManagedSandboxInfo>();
        string? cursor = null;
        for (var page = 0; page < Math.Max(1, opts.MaxListPages); page++)
        {
            ModalSandboxListPage list;
            try
            {
                list = await _client.ListSandboxesAsync(opts.ApiBaseUrl, credentials, 100, cursor, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (ModalApiException ex)
            {
                throw ToDeferred(ex, "list-sandboxes");
            }

            foreach (var sandbox in list.Sandboxes)
            {
                if (!IsManaged(sandbox, opts.NamePrefix))
                {
                    continue;
                }

                managed.Add(new ManagedSandboxInfo(
                    sandbox.Id,
                    sandbox.CreatedAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(sandbox.CreatedAtUnix) : null,
                    null,
                    _activeSandboxes.ContainsKey(sandbox.Id),
                    HasPreemptMarker: false,
                    IsSuspendLifecycleOrFrozen: false));
            }

            if (!list.HasMore || list.Sandboxes.Count == 0)
            {
                break;
            }

            cursor = list.NextCursor;
            if (string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return managed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);
        var credentials = ResolveCredentials(opts);

        try
        {
            await _client.TerminateSandboxAsync(opts.ApiBaseUrl, credentials, name, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (ModalApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Where(static entry => entry.WorkItemId is not null)
            .Select(static entry => (entry.WorkItemId!.Value, (IShutdownTeardownSandbox)entry.Sandbox))
            .ToList();

    internal static void ValidateOptions(ModalSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!Uri.TryCreate(opts.ApiBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && !(opts.AllowUnsafeHttp && baseUri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:ApiBaseUrl must be an absolute https URL" +
                (opts.AllowUnsafeHttp ? " (http is allowed only with AllowUnsafeHttp for local mock servers)." : "."));
        }

        if (opts.AllowUnsafeHttp && baseUri.Scheme == Uri.UriSchemeHttp
            && !IsLoopbackHost(baseUri.Host))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:AllowUnsafeHttp permits http only for loopback hosts; " +
                $"refusing remote cleartext control plane '{baseUri.Host}' that would carry the API secret in the clear.");
        }

        if (string.IsNullOrWhiteSpace(opts.TokenIdEnvironmentVariable))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:TokenIdEnvironmentVariable must be set.");
        }

        if (string.IsNullOrWhiteSpace(opts.TokenSecretEnvironmentVariable))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:TokenSecretEnvironmentVariable must be set.");
        }

        if (string.IsNullOrWhiteSpace(opts.AppName))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:AppName must be set.");
        }

        if (string.IsNullOrWhiteSpace(opts.NamePrefix)
            || opts.NamePrefix.Length > 48
            || opts.NamePrefix.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || !char.IsAsciiLetter(opts.NamePrefix[0]))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:NamePrefix must start with a letter " +
                "and contain only ASCII letters, digits, and hyphens (max 48 chars).");
        }

        if (opts.CpuCount <= 0 || opts.MemoryMiB <= 0 || opts.IdleTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{ModalSandboxOptions.PluginId}:CpuCount, MemoryMiB, and IdleTimeoutSeconds must be positive.");
        }
    }

    internal static void ValidateSpec(SandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
        {
            throw new NotSupportedException(
                "The modal provider does not support graphical sandboxes: Modal sandboxes expose no display server.");
        }

        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            throw new InvalidOperationException(
                $"Sandbox provider kind '{ModalSandboxOptions.ProviderKind}' cannot serve network profile " +
                $"'{spec.Network.ProfileName.Trim()}': the kind is classified 'NotEnforced' (no host-enforced egress filtering). " +
                "A 'NotEnforced' provider may only serve sandboxes with no named network profile.");
        }
    }

    internal static Dictionary<string, string> ValidateEnvironment(
        IReadOnlyDictionary<string, string> environment, int maxEnvironmentBytes)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Count > MaxCreateEnvironmentVariables)
        {
            throw new ArgumentException($"Sandbox environment exceeds {MaxCreateEnvironmentVariables} variables.", nameof(environment));
        }

        var validated = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
        long bytes = 0;
        foreach (var (key, value) in environment)
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(environment));
            validated[key] = value ?? string.Empty;
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(validated[key]);
            if (bytes > maxEnvironmentBytes)
            {
                throw new ArgumentException(
                    $"Sandbox environment exceeds {maxEnvironmentBytes} bytes; refusing to create the sandbox.",
                    nameof(environment));
            }
        }

        return validated;
    }

    internal ModalCredentials ResolveCredentials(ModalSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var tokenId = !string.IsNullOrWhiteSpace(opts.TokenId)
            ? opts.TokenId
            : Environment.GetEnvironmentVariable(opts.TokenIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            throw new InvalidOperationException(
                $"Modal token id environment variable '{opts.TokenIdEnvironmentVariable}' is not set. " +
                "Set it in the orchestrator process environment (never in a configuration file).");
        }

        var tokenSecret = !string.IsNullOrWhiteSpace(opts.TokenSecret)
            ? opts.TokenSecret
            : Environment.GetEnvironmentVariable(opts.TokenSecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(tokenSecret))
        {
            throw new InvalidOperationException(
                $"Modal token secret environment variable '{opts.TokenSecretEnvironmentVariable}' is not set. " +
                "Set it in the orchestrator process environment (never in a configuration file).");
        }

        return new ModalCredentials(tokenId, tokenSecret);
    }

    private static string? EffectiveSnapshotId(SandboxSpec spec, ModalSandboxOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(spec.BaselineImageRef))
        {
            return spec.BaselineImageRef.Trim();
        }

        return string.IsNullOrWhiteSpace(opts.SnapshotId) ? null : opts.SnapshotId;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal);

    private static string BuildSandboxName(string prefix)
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return $"{prefix.TrimEnd('-').ToLowerInvariant()}-{suffix}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000}";
    }

    private static bool IsManagedName(string name, string namePrefix) =>
        !string.IsNullOrWhiteSpace(name)
        && name.StartsWith(namePrefix, StringComparison.Ordinal);

    private static bool IsManaged(ModalSandboxView sandbox, string namePrefix)
    {
        if (sandbox.Metadata is not null
            && sandbox.Metadata.TryGetValue("codeybox-managed", out var managed)
            && string.Equals(managed, "true", StringComparison.OrdinalIgnoreCase)
            && sandbox.Metadata.TryGetValue("codeybox-provider", out var provider)
            && string.Equals(provider, ModalSandboxOptions.ProviderKind, StringComparison.Ordinal))
        {
            return true;
        }

        return IsManagedName(sandbox.Name, namePrefix);
    }

    private async Task WaitForRunningAsync(
        ModalSandboxOptions opts, ModalCredentials credentials, string sandboxId, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + opts.WaitForRunningTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToDeferred(
                    new ModalApiException(null, "timeout", $"sandbox {sandboxId} did not reach running in time"),
                    "wait-for-running");
            }

            ModalSandboxView sandbox;
            try
            {
                sandbox = await _client.GetSandboxAsync(opts.ApiBaseUrl, credentials, sandboxId, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (ModalApiException ex)
            {
                throw ToDeferred(ex, "wait-for-running");
            }

            if (string.Equals(sandbox.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(sandbox.Status, "terminated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sandbox.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw ToDeferred(
                    new ModalApiException(null, "terminal-state", $"sandbox {sandboxId} entered '{sandbox.Status}' while starting"),
                    "wait-for-running");
            }

            try
            {
                await Task.Delay(opts.ExecPollInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<IReadOnlyList<WritableMountSync>> StageMountsAsync(
        ModalSandboxOptions opts,
        ModalCredentials credentials,
        ModalSandbox sandbox,
        SandboxSpec spec,
        CancellationToken ct)
    {
        var writable = new List<WritableMountSync>();
        long totalBytes = 0;
        var totalFiles = 0;

        foreach (var mount in spec.Mounts)
        {
            ct.ThrowIfCancellationRequested();
            ModalGuestPath.ValidateAbsolute(mount.SandboxPath);

            if (mount.Tmpfs)
            {
                if (string.Equals(mount.SandboxPath, CredentialMountPath, StringComparison.Ordinal)
                    || mount.SandboxPath.StartsWith(CredentialMountPath + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Modal sandboxes have no tmpfs: credential mount '{mount.SandboxPath}' would persist on the service-side disk " +
                        "and in snapshots. Pass credentials as environment variables instead.");
                }

                if (!opts.AllowPersistentTmpfsDowngrade)
                {
                    throw new InvalidOperationException(
                        $"Modal sandboxes have no tmpfs: mount '{mount.SandboxPath}' needs AllowPersistentTmpfsDowngrade " +
                        "to downgrade to a persistent guest directory.");
                }

                _log.LogWarning(
                    "Modal sandbox {SandboxId}: tmpfs mount {Path} downgraded to a persistent guest directory.",
                    sandbox.Id, mount.SandboxPath);
                await WriteFileWithTimeoutAsync(
                    opts, credentials, sandbox.Id, mount.SandboxPath.TrimEnd('/') + "/.codeybox-keep",
                    "# persistent downgrade marker"u8.ToArray(), ct).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mount.HostPath))
            {
                await WriteFileWithTimeoutAsync(
                    opts, credentials, sandbox.Id, mount.SandboxPath.TrimEnd('/') + "/.codeybox-keep",
                    "# empty mount marker"u8.ToArray(), ct).ConfigureAwait(false);
                continue;
            }

            var hostRoot = Path.GetFullPath(mount.HostPath);
            if (!File.Exists(hostRoot) && !Directory.Exists(hostRoot))
            {
                throw new SandboxMountSourceMissingException(
                    mount.HostPath,
                    $"Modal mount source '{mount.HostPath}' does not exist at stage time.");
            }

            var staged = await StageHostPathAsync(opts, credentials, sandbox.Id, mount.SandboxPath, hostRoot, ct).ConfigureAwait(false);
            totalBytes += staged.Bytes;
            totalFiles += staged.Files;
            if (totalBytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Modal mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            if (totalFiles > opts.MaxStageFileCount)
            {
                throw new InvalidOperationException(
                    $"Modal mount staging exceeds {opts.MaxStageFileCount} files in total; refusing to stage.");
            }

            if (!mount.ReadOnly)
            {
                writable.Add(new WritableMountSync(mount.SandboxPath, hostRoot, IsFile: File.Exists(hostRoot)));
            }
        }

        return writable;
    }

    private async Task<(long Bytes, int Files)> StageHostPathAsync(
        ModalSandboxOptions opts,
        ModalCredentials credentials,
        string sandboxId,
        string guestRoot,
        string hostRoot,
        CancellationToken ct)
    {
        var files = new List<(string HostFull, string Relative)>();
        if (File.Exists(hostRoot))
        {
            files.Add((hostRoot, Path.GetFileName(hostRoot)));
        }
        else
        {
            foreach (var hostFile in Directory.EnumerateFiles(hostRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(hostFile);
                if (!full.StartsWith(hostRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Mount source escapes its root: '{hostFile}'.");
                }

                var relative = Path.GetRelativePath(hostRoot, full);
                if (relative.Split(Path.DirectorySeparatorChar).Any(static s => s == ".." || s.Length == 0))
                {
                    throw new InvalidOperationException($"Mount source escapes its root: '{hostFile}'.");
                }

                files.Add((full, relative));
                if (files.Count > opts.MaxStageFileCount)
                {
                    throw new InvalidOperationException(
                        $"Modal mount staging exceeds {opts.MaxStageFileCount} files; refusing to stage.");
                }
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
                    $"Modal mount file '{relative}' is {fileInfo.Length} bytes (limit {opts.MaxStageFileBytes}); refusing to stage.");
            }

            var guestPath = guestRoot.TrimEnd('/') + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            ModalGuestPath.ValidateAbsolute(guestPath);

            byte[] content;
            try
            {
                content = await ReadBoundedFileAsync(hostFull, opts.MaxStageFileBytes, relative, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Modal mount file '{relative}' cannot be read: {ex.GetType().Name}.", ex);
            }

            bytes += content.Length;
            if (bytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Modal mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            try
            {
                await WriteFileWithTimeoutAsync(opts, credentials, sandboxId, guestPath, content, ct).ConfigureAwait(false);
            }
            catch (ModalApiException ex)
            {
                throw ToDeferred(ex, "stage-mount");
            }
        }

        return (bytes, files.Count);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string hostFull, long maxBytes, string relative, CancellationToken ct)
    {
        using var stream = File.OpenRead(hostFull);
        using var sink = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (sink.Length + read > maxBytes)
            {
                throw new InvalidOperationException($"Modal mount file '{relative}' exceeds the stage limit while reading.");
            }

            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }

    private async Task WriteFileWithTimeoutAsync(
        ModalSandboxOptions opts,
        ModalCredentials credentials,
        string sandboxId,
        string guestPath,
        byte[] content,
        CancellationToken ct)
    {
        ModalGuestPath.ValidateAbsolute(guestPath);
        await _client.WriteFileAsync(opts.ApiBaseUrl, credentials, sandboxId, guestPath, content, opts.ApiTimeout, ct).ConfigureAwait(false);
    }

    private async Task<bool> TerminateAndProveGoneAsync(ModalSandboxOptions opts, ModalCredentials credentials, string sandboxId)
    {
        try
        {
            await _client.TerminateSandboxAsync(opts.ApiBaseUrl, credentials, sandboxId, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModalApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: best-effort terminate after failed create failed.", sandboxId);
            return false;
        }

        try
        {
            await _client.GetSandboxAsync(opts.ApiBaseUrl, credentials, sandboxId, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (ModalApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modal sandbox {SandboxId}: removal proof read failed.", sandboxId);
            return false;
        }
    }

    private SandboxProvisioningDeferredException ToDeferred(ModalApiException ex, string operation)
    {
        var classification = ModalFailureClassification.Classify(ex.StatusCode, operation, ex.RetryAfter);
        return new SandboxProvisioningDeferredException(
            ModalSandboxOptions.ProviderKind,
            operation,
            ex.ErrorClass,
            TruncateDetail(ex.Detail),
            classification.RecheckIn,
            innerException: ex);
    }

    private static string TruncateDetail(string detail) =>
        detail.Length > 512 ? detail[..512] : detail;

    private sealed record ActiveSandboxEntry(WorkItemId? WorkItemId, ModalSandbox Sandbox);
}
