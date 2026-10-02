using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.RunloopPlugin;

/// <summary>
/// CodeyBox sandbox provider plugin for Runloop Devboxes: hosted Linux VMs
/// created on demand over the Runloop REST API, with disk snapshots and
/// suspend/resume. Disabled unless the operator allowlists and enables
/// <c>codeybox.runloop</c>.
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
/// <para>Credentials come from the credential chain (the bearer token is
/// resolved from <see cref="RunloopSandboxOptions.TokenEnvironmentVariable"/>
/// at use time), never from configuration files. Admission and capacity stay
/// host-owned: member gates size admission from
/// <see cref="SandboxMember.Capacity"/> and the composition root wraps this
/// provider exactly like a built-in one.</para>
/// </summary>
[CodeyBoxPlugin(
    id: RunloopSandboxOptions.PluginId,
    displayName: "CodeyBox: Runloop Devboxes",
    minHostApiVersion: "1.0")]
public sealed class RunloopSandboxProvider : ISandboxProvider, ISuspendingSandboxProvider,
    IActiveSandboxProvider, IPluginInitializer
{
    /// <summary>
    /// Guest path reserved for credential files. Must match the host's
    /// <c>SandboxConventions.CredentialsDir</c> (the plugin boundary forbids
    /// referencing that assembly; equality is enforced by test).
    /// </summary>
    internal const string CredentialMountPath = "/run/codeybox/creds";

    private static readonly IReadOnlySet<string> KnownResourceSizes = new HashSet<string>(StringComparer.Ordinal)
    {
        "X_SMALL", "SMALL", "MEDIUM", "LARGE", "X_LARGE", "XX_LARGE", "CUSTOM_SIZE",
    };

    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly RunloopApiClient _client;
    private readonly TimeProvider _timeProvider;

    private Func<RunloopSandboxOptions> _readOptions;
    private ILogger _log;

    /// <summary>DI constructor used by the plugin host.</summary>
    public RunloopSandboxProvider(IConfiguration configuration, ILogger<RunloopSandboxProvider> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = logger;
        var section = configuration.GetSection($"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}");
        _readOptions = () => RunloopSandboxOptions.FromConfiguration(section);
        _client = new RunloopApiClient(new HttpClient());
    }

    /// <summary>Test constructor with full control over options, transport, and clock.</summary>
    internal RunloopSandboxProvider(
        Func<RunloopSandboxOptions> readOptions,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _client = new RunloopApiClient(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Provider kind. Normalised (trimmed, lowercase) and matched by exact ordinal equality.</summary>
    public string Name => RunloopSandboxOptions.ProviderKind;

    /// <inheritdoc/>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
            return true;
        try
        {
            return IsManaged(new DevboxView { Name = name }, _readOptions().NamePrefix);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Runloop guests are VMs with a separate guest kernel.</summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honestly declared capabilities: suspend/resume (disk snapshot + resume)
    /// and teardown (stop-and-preserve, dispose). Baseline bake, cache seeding,
    /// disk guard, and port publishing are deliberately absent — placement
    /// refuses work requiring them rather than failing deep inside a phase.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.SuspendResume, SandboxCapabilities.Teardown];

    /// <summary>Live devbox count for observability and tests.</summary>
    internal int ActiveSandboxCount => _activeSandboxes.Count;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _log = context.Logger;
        var section = context.ScopedConfig;
        _readOptions = () => RunloopSandboxOptions.FromConfiguration(section);

        // Fail the host fast on operator misconfiguration that can be checked
        // without network I/O. Token presence is checked here too: without it
        // every placement would fail, so refusing startup is the honest signal.
        var opts = _readOptions();
        ValidateOptions(opts);
        ResolveToken(opts);

        _log.LogWarning(
            "Runloop sandbox provider '{Kind}': egress NOT enforced — no host network isolation. " +
            "It may only serve sandboxes with no named network profile and must never be described as isolation.",
            RunloopSandboxOptions.ProviderKind);
        return Task.CompletedTask;
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = _readOptions();
        ValidateOptions(opts);
        ValidateSpec(spec);
        var token = ResolveToken(opts);

        var sandboxName = BuildSandboxName(opts.NamePrefix);
        var environment = ValidateEnvironment(spec.Environment);
        var request = new DevboxCreateRequest
        {
            Name = sandboxName,
            EnvironmentVariables = environment.Count > 0 ? environment : null,
            BlueprintId = opts.BlueprintId,
            BlueprintName = opts.BlueprintId is null ? opts.BlueprintName : null,
            SnapshotId = opts.SnapshotId,
            Metadata = new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = RunloopSandboxOptions.ProviderKind,
                ["codeybox-purpose"] = spec.Purpose.ToString(),
            },
            LaunchParameters = new DevboxLaunchParameters
            {
                ResourceSizeRequest = opts.ResourceSize,
                CustomCpuCores = opts.CustomCpuCores,
                CustomGbMemory = opts.CustomMemoryGiB,
                CustomDiskSize = opts.CustomDiskGiB,
                KeepAliveTimeSeconds = Math.Clamp(opts.KeepAliveSeconds, 60, 172800),
            },
        };

        DevboxView created;
        try
        {
            created = await _client.CreateDevboxAsync(opts.ApiBaseUrl, token, request, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToDeferred(ex, "create-devbox");
        }

        if (string.IsNullOrWhiteSpace(created.Id))
        {
            throw ToDeferred(new RunloopApiException(null, "malformed-response", "create-devbox returned no devbox id"), "create-devbox");
        }

        var sandbox = new RunloopSandbox(
            created.Id,
            sandboxName,
            _client,
            _readOptions,
            () => ResolveToken(_readOptions()),
            spec,
            _timeProvider,
            _log,
            id => _activeSandboxes.TryRemove(id, out _));
        var entry = new ActiveSandboxEntry(spec.TimingWorkItemId, sandbox);
        _activeSandboxes[created.Id] = entry;

        try
        {
            await WaitForRunningAsync(opts, token, created.Id, ct).ConfigureAwait(false);
            var writableMounts = await StageMountsAsync(opts, token, sandbox, spec, ct).ConfigureAwait(false);
            sandbox.SetWritableMounts(writableMounts);
            return sandbox;
        }
        catch (Exception ex)
        {
            await ShutdownBestEffortAsync(opts, token, created.Id).ConfigureAwait(false);
            _activeSandboxes.TryRemove(created.Id, out _);
            if (ex is RunloopApiException apiEx)
            {
                throw ToDeferred(apiEx, "create-devbox");
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = _readOptions();
        ValidateOptions(opts);
        var token = ResolveToken(opts);

        var managed = new List<ManagedSandboxInfo>();
        string? cursor = null;
        for (var page = 0; page < Math.Max(1, opts.MaxListPages); page++)
        {
            DevboxListPage list;
            try
            {
                list = await _client.ListDevboxesAsync(opts.ApiBaseUrl, token, 100, cursor, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (RunloopApiException ex)
            {
                throw ToDeferred(ex, "list-devboxes");
            }

            foreach (var devbox in list.Devboxes)
            {
                if (!IsManaged(devbox, opts.NamePrefix))
                {
                    continue;
                }

                managed.Add(new ManagedSandboxInfo(
                    devbox.Id,
                    devbox.CreateTimeMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(devbox.CreateTimeMs) : null,
                    null,
                    _activeSandboxes.ContainsKey(devbox.Id),
                    HasPreemptMarker: false,
                    IsSuspendLifecycleOrFrozen: string.Equals(devbox.Status, "suspended", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(devbox.Status, "suspending", StringComparison.OrdinalIgnoreCase)));
            }

            if (!list.HasMore || list.Devboxes.Count == 0)
            {
                break;
            }

            cursor = list.Devboxes[^1].Id;
        }

        return managed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);
        var token = ResolveToken(opts);

        try
        {
            await _client.ShutdownAsync(opts.ApiBaseUrl, token, name, force: false, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        catch (RunloopApiException ex) when (string.Equals(ex.ErrorClass, "conflict", StringComparison.Ordinal))
        {
            await _client.ShutdownAsync(opts.ApiBaseUrl, token, name, force: true, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
    }

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);
        var token = ResolveToken(opts);

        DevboxView devbox;
        try
        {
            devbox = await _client.GetDevboxAsync(opts.ApiBaseUrl, token, name, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // "VM not found" is non-fatal so the startup handler can clear
            // bookkeeping for items whose devbox no longer exists.
            return;
        }
        catch (RunloopApiException ex)
        {
            throw ToDeferred(ex, "resume-devbox");
        }

        if (string.Equals(devbox.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(devbox.Status, "suspended", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _client.ResumeAsync(opts.ApiBaseUrl, token, name, ToLongApiTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
        }
        catch (RunloopApiException ex)
        {
            throw ToDeferred(ex, "resume-devbox");
        }

        await WaitForRunningAsync(opts, token, name, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Where(static entry => entry.WorkItemId is not null)
            .Select(static entry => (entry.WorkItemId!.Value, (IShutdownTeardownSandbox)entry.Sandbox))
            .ToList();

    internal static void ValidateOptions(RunloopSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!Uri.TryCreate(opts.ApiBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && !(opts.AllowUnsafeHttp && baseUri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}:ApiBaseUrl must be an absolute https URL" +
                (opts.AllowUnsafeHttp ? " (http is allowed only with AllowUnsafeHttp for local mock servers)." : "."));
        }

        if (string.IsNullOrWhiteSpace(opts.TokenEnvironmentVariable))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}:TokenEnvironmentVariable must be set.");
        }

        if (string.IsNullOrWhiteSpace(opts.NamePrefix)
            || opts.NamePrefix.Length > 48
            || opts.NamePrefix.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || !char.IsAsciiLetter(opts.NamePrefix[0]))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}:NamePrefix must start with a letter " +
                "and contain only ASCII letters, digits, and hyphens (max 48 chars).");
        }

        if (!KnownResourceSizes.Contains(opts.ResourceSize))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}:ResourceSize '{opts.ResourceSize}' is unknown. " +
                $"Valid: {string.Join(", ", KnownResourceSizes.OrderBy(static s => s, StringComparer.Ordinal))}.");
        }

        var custom = string.Equals(opts.ResourceSize, "CUSTOM_SIZE", StringComparison.Ordinal);
        if (custom && (opts.CustomCpuCores is null || opts.CustomMemoryGiB is null))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}: CUSTOM_SIZE requires CustomCpuCores and CustomMemoryGiB.");
        }

        if (!custom && (opts.CustomCpuCores is not null || opts.CustomMemoryGiB is not null || opts.CustomDiskGiB is not null))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}: custom CPU/memory/disk apply only to CUSTOM_SIZE.");
        }

        if (!string.IsNullOrWhiteSpace(opts.BlueprintId) && !string.IsNullOrWhiteSpace(opts.SnapshotId))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{RunloopSandboxOptions.PluginId}: BlueprintId and SnapshotId are mutually exclusive.");
        }
    }

    internal static void ValidateSpec(SandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
        {
            throw new NotSupportedException(
                "The runloop provider does not support graphical sandboxes: devboxes expose no display or VNC server.");
        }

        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            throw new InvalidOperationException(
                $"Sandbox provider kind '{RunloopSandboxOptions.ProviderKind}' cannot serve network profile " +
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

    internal string ResolveToken(RunloopSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!string.IsNullOrWhiteSpace(opts.Token))
        {
            return opts.Token;
        }

        var token = Environment.GetEnvironmentVariable(opts.TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Runloop bearer token environment variable '{opts.TokenEnvironmentVariable}' is not set. " +
                $"Set it in the orchestrator process environment (never in a configuration file).");
        }

        return token;
    }

    private static string BuildSandboxName(string prefix)
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return $"{prefix.TrimEnd('-').ToLowerInvariant()}-{suffix}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000}";
    }

    private async Task WaitForRunningAsync(RunloopSandboxOptions opts, string token, string devboxId, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + opts.WaitForRunningTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToDeferred(
                    new RunloopApiException(null, "timeout", $"devbox {devboxId} did not reach running in time"),
                    "wait-for-running");
            }

            DevboxView devbox;
            try
            {
                devbox = await _client.WaitForDevboxStatusAsync(
                    opts.ApiBaseUrl, token, devboxId, ["running"], 25, RunloopApiClient.WaitCallTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
            }
            catch (RunloopApiException ex) when (string.Equals(ex.ErrorClass, "timeout", StringComparison.Ordinal))
            {
                try
                {
                    devbox = await _client.GetDevboxAsync(opts.ApiBaseUrl, token, devboxId, opts.ApiTimeout, ct).ConfigureAwait(false);
                }
                catch (RunloopApiException inner)
                {
                    throw ToDeferred(inner, "wait-for-running");
                }
            }
            catch (RunloopApiException ex)
            {
                throw ToDeferred(ex, "wait-for-running");
            }

            if (string.Equals(devbox.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(devbox.Status, "failure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(devbox.Status, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                throw ToDeferred(
                    new RunloopApiException(null, "terminal-state", $"devbox {devboxId} entered '{devbox.Status}' while starting"),
                    "wait-for-running");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<IReadOnlyList<WritableMountSync>> StageMountsAsync(
        RunloopSandboxOptions opts, string token, RunloopSandbox sandbox, SandboxSpec spec, CancellationToken ct)
    {
        var writable = new List<WritableMountSync>();
        long totalBytes = 0;
        var totalFiles = 0;

        foreach (var mount in spec.Mounts)
        {
            ct.ThrowIfCancellationRequested();
            RunloopGuestPath.ValidateAbsolute(mount.SandboxPath);

            if (mount.Tmpfs)
            {
                if (string.Equals(mount.SandboxPath, CredentialMountPath, StringComparison.Ordinal)
                    || mount.SandboxPath.StartsWith(CredentialMountPath + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Runloop devboxes have no tmpfs: credential mount '{mount.SandboxPath}' would persist on the devbox disk " +
                        "and in snapshots. Pass credentials as environment variables instead.");
                }

                if (!opts.AllowPersistentTmpfsDowngrade)
                {
                    throw new InvalidOperationException(
                        $"Runloop devboxes have no tmpfs: mount '{mount.SandboxPath}' needs AllowPersistentTmpfsDowngrade " +
                        "to downgrade to a persistent guest directory.");
                }

                _log.LogWarning(
                    "Runloop devbox {DevboxId}: tmpfs mount {Path} downgraded to a persistent guest directory.",
                    sandbox.Id, mount.SandboxPath);
                await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["mkdir", "-p", mount.SandboxPath], WorkingDirectory = "/" }, ct).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mount.HostPath))
            {
                await sandbox.ExecAsync(
                    new SandboxExec { Argv = ["mkdir", "-p", mount.SandboxPath], WorkingDirectory = "/" }, ct).ConfigureAwait(false);
                continue;
            }

            var hostRoot = Path.GetFullPath(mount.HostPath);
            if (!File.Exists(hostRoot) && !Directory.Exists(hostRoot))
            {
                throw new SandboxMountSourceMissingException(
                    mount.HostPath,
                    $"Runloop mount source '{mount.HostPath}' does not exist at stage time.");
            }

            var staged = await StageHostPathAsync(opts, token, sandbox, mount.SandboxPath, hostRoot, ct).ConfigureAwait(false);
            totalBytes += staged.Bytes;
            totalFiles += staged.Files;
            if (totalBytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Runloop mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            if (totalFiles > opts.MaxStageFileCount)
            {
                throw new InvalidOperationException(
                    $"Runloop mount staging exceeds {opts.MaxStageFileCount} files in total; refusing to stage.");
            }

            if (!mount.ReadOnly)
            {
                writable.Add(new WritableMountSync(mount.SandboxPath, hostRoot));
            }
        }

        return writable;
    }

    private async Task<(long Bytes, int Files)> StageHostPathAsync(
        RunloopSandboxOptions opts,
        string token,
        RunloopSandbox sandbox,
        string guestRoot,
        string hostRoot,
        CancellationToken ct)
    {
        await sandbox.ExecAsync(
            new SandboxExec { Argv = ["mkdir", "-p", guestRoot], WorkingDirectory = "/" }, ct).ConfigureAwait(false);

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
                        $"Runloop mount staging exceeds {opts.MaxStageFileCount} files; refusing to stage.");
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
                    $"Runloop mount file '{relative}' is {fileInfo.Length} bytes (limit {opts.MaxStageFileBytes}); refusing to stage.");
            }

            var guestPath = guestRoot.TrimEnd('/') + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            RunloopGuestPath.ValidateAbsolute(guestPath);

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
                        throw new InvalidOperationException($"Runloop mount file '{relative}' exceeds the stage limit while reading.");
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
                throw new InvalidOperationException($"Runloop mount file '{relative}' cannot be read: {ex.GetType().Name}.", ex);
            }

            bytes += content.Length;
            if (bytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Runloop mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            try
            {
                if (IsText(content))
                {
                    await _client.WriteFileAsync(
                        opts.ApiBaseUrl, token, sandbox.Id, guestPath, Encoding.UTF8.GetString(content), opts.ApiTimeout, ct).ConfigureAwait(false);
                }
                else
                {
                    await _client.UploadFileAsync(opts.ApiBaseUrl, token, sandbox.Id, guestPath, content, opts.ApiTimeout, ct).ConfigureAwait(false);
                }
            }
            catch (RunloopApiException ex)
            {
                throw ToDeferred(ex, "stage-mount");
            }
        }

        return (bytes, files.Count);
    }

    private static bool IsText(byte[] content)
    {
        foreach (var b in content)
        {
            if (b == 0)
            {
                return false;
            }
        }

        try
        {
            Encoding.UTF8.GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsManaged(DevboxView devbox, string namePrefix)
    {
        if (devbox.Metadata is not null
            && devbox.Metadata.TryGetValue("codeybox-managed", out var managed)
            && string.Equals(managed, "true", StringComparison.Ordinal)
            && devbox.Metadata.TryGetValue("codeybox-provider", out var provider)
            && string.Equals(provider, RunloopSandboxOptions.ProviderKind, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(devbox.Name)
            && devbox.Name.StartsWith(namePrefix, StringComparison.Ordinal);
    }

    private async Task ShutdownBestEffortAsync(RunloopSandboxOptions opts, string token, string devboxId)
    {
        try
        {
            await _client.ShutdownAsync(opts.ApiBaseUrl, token, devboxId, force: false, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Runloop devbox {DevboxId}: best-effort shutdown after failed create failed.", devboxId);
        }
    }

    private static TimeSpan ToLongApiTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : configured;

    private SandboxProvisioningDeferredException ToDeferred(RunloopApiException ex, string operation)
    {
        var classification = RunloopFailureClassification.Classify(ex.StatusCode, operation);
        return new SandboxProvisioningDeferredException(
            RunloopSandboxOptions.ProviderKind,
            operation,
            ex.ErrorClass,
            TruncateDetail(ex.Detail),
            classification.RecheckIn,
            innerException: ex);
    }

    private static string TruncateDetail(string detail) =>
        detail.Length > 512 ? detail[..512] : detail;

    private sealed record ActiveSandboxEntry(WorkItemId? WorkItemId, RunloopSandbox Sandbox);
}
