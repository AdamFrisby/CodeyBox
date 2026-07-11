using System.Buffers;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Sandbox.Sprites;

/// <summary>
/// Sandbox provider backed by Fly.io Sprites. Sprites expose persistent
/// Firecracker microVMs through an HTTP/WebSocket API rather than host-local
/// bind mounts, so host mounts are staged into the sprite and writable host
/// mounts are synchronized back to the host ONCE, during sandbox disposal
/// (teardown). Per-exec sync-back is NOT performed: ExecAsync always runs with
/// the sync flag off, so a consumer that reads a writable host mount between
/// execs observes stale (pre-run) content until the sandbox is disposed.
/// </summary>
public sealed class SpritesSandboxProvider : ISandboxProvider, IActiveSandboxProvider, IActiveSandboxProgressProvider
{
    public const string DefaultNamePrefix = "codeybox-";
    private static readonly TimeSpan CreateCleanupRecheckIn = TimeSpan.FromMinutes(5);

    private readonly Func<SpritesSandboxOptions> _readOptions;
    private readonly SpritesApiClient _client;
    private readonly ISpritesWebSocketFactory _webSocketFactory;
    private readonly ILogger<SpritesSandboxProvider> _log;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);

    public SpritesSandboxProvider(
        Func<SpritesSandboxOptions> readOptions,
        ILogger<SpritesSandboxProvider> log)
        : this(readOptions, new HttpClient(), new ClientWebSocketSpritesWebSocketFactory(), log)
    {
    }

    internal SpritesSandboxProvider(
        Func<SpritesSandboxOptions> readOptions,
        HttpClient httpClient,
        ISpritesWebSocketFactory webSocketFactory,
        ILogger<SpritesSandboxProvider> log)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _client = new SpritesApiClient(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
        _webSocketFactory = webSocketFactory ?? throw new ArgumentNullException(nameof(webSocketFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "sprites";

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec = SandboxConventions.WithTimingEnvironment(spec);

        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("sprites sandbox provider does not support graphical sandbox flavor.");

        var opts = ReadValidatedOptions();
        LogUnsupportedSizingIfNeeded(spec, opts);

        var name = GenerateSandboxName(opts.NamePrefix);
        var syncMounts = ValidateAndPlanMounts(spec, opts);
        var workItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var created = false;

        try
        {
            await _client.CreateSpriteAsync(opts, name, ct).ConfigureAwait(false);
            created = true;

            var sandbox = new SpritesSandbox(
                name,
                spec,
                opts,
                _client,
                _webSocketFactory,
                syncMounts,
                () => MarkNoLongerActive(name),
                _log);

            _activeSandboxes[name] = new ActiveSandboxEntry(workItemId, sandbox);
            SandboxLiveCounter.Increment();

            // Run operator-supplied setup commands (toolchain / agent-CLI installs) BEFORE locking
            // egress down: rc30 sprites have no baseline image, so these installs are the only path
            // to provision the sprite and they need to reach package registries. Applying the
            // default-deny network policy first would refuse npm/apt/curl unless every mirror were
            // allow-listed. The natural bake-then-lock ordering provisions with open egress and then
            // clamps to the work item's allow-list before the agent runs. The setup commands are
            // operator-trusted and no agent code has executed yet, so the pre-lockdown window is safe.
            await sandbox.RunSetupCommandsAsync(ct).ConfigureAwait(false);
            await ApplyNetworkPolicyAsync(opts, name, spec.Network, ct).ConfigureAwait(false);
            await sandbox.PrepareFilesystemAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Created sprites sandbox {Name}", name);
            return sandbox;
        }
        catch (Exception ex)
        {
            MarkNoLongerActive(name);
            if (created && !await TryDeleteAfterCreateFailureAsync(opts, name).ConfigureAwait(false))
                throw new SandboxProvisioningDeferredException(
                    Name,
                    "create-cleanup",
                    "sprites-delete-failed",
                    $"create failed and best-effort delete did not prove sprite {name} was removed: {ex.Message}",
                    CreateCleanupRecheckIn,
                    retainedSandboxName: name,
                    innerException: ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var sprites = await _client.ListSpritesAsync(opts, opts.NamePrefix, ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>(sprites.Count);
        foreach (var sprite in sprites)
        {
            var createdAt = sprite.CreatedAt ?? sprite.UpdatedAt;
            result.Add(new ManagedSandboxInfo(
                sprite.Name,
                createdAt,
                DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(sprite.Name)));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(name, opts.NamePrefix))
            throw new ArgumentException($"Sprites sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));

        await _client.DeleteSpriteAsync(opts, name, ct).ConfigureAwait(false);
        MarkNoLongerActive(name);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
    {
        var result = new List<(WorkItemId, IShutdownTeardownSandbox)>(_activeSandboxes.Count);
        foreach (var entry in _activeSandboxes.Values)
        {
            if (entry.WorkItemId.Value == Guid.Empty)
                continue;
            result.Add((entry.WorkItemId, entry.Sandbox));
        }
        return result;
    }

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress()
    {
        var result = new List<ActiveSandboxProgress>();
        foreach (var (name, entry) in _activeSandboxes)
        {
            if (entry.WorkItemId.Value == Guid.Empty)
                continue;
            result.Add(new ActiveSandboxProgress(entry.WorkItemId, name));
        }
        return result;
    }

    private SpritesSandboxOptions ReadValidatedOptions()
    {
        var opts = _readOptions();
        if (opts.ApiBaseUrl is null || !Uri.TryCreate(opts.ApiBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("CodeyBox:Sprites:ApiBaseUrl must be an absolute http(s) URL.");
        }
        if (baseUri.Scheme == Uri.UriSchemeHttp && !opts.AllowUnsafeHttp)
        {
            throw new InvalidOperationException(
                "CodeyBox:Sprites:ApiBaseUrl must use https://. Set CodeyBox:Sprites:AllowUnsafeHttp=true only for local tests.");
        }
        if (string.IsNullOrWhiteSpace(opts.TokenEnvironmentVariable))
            throw new InvalidOperationException("CodeyBox:Sprites:TokenEnvironmentVariable must be set.");
        if (string.IsNullOrWhiteSpace(opts.Token))
        {
            var token = Environment.GetEnvironmentVariable(opts.TokenEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    $"Sprites bearer token environment variable '{opts.TokenEnvironmentVariable}' is not set.");
            opts = opts with { Token = token };
        }
        if (string.IsNullOrWhiteSpace(opts.NamePrefix) || !IsValidNamePrefix(opts.NamePrefix))
            throw new InvalidOperationException("CodeyBox:Sprites:NamePrefix must contain only lowercase letters, numbers, and hyphens.");
        if (!opts.NamePrefix.StartsWith(DefaultNamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Sprites:NamePrefix must start with '{DefaultNamePrefix}' so leak reaping can identify managed sprites.");
        if (opts.UrlAuth is not "sprite" and not "public")
            throw new InvalidOperationException("CodeyBox:Sprites:UrlAuth must be either 'sprite' or 'public'.");
        if (opts.MaxListPages <= 0)
            throw new InvalidOperationException("CodeyBox:Sprites:MaxListPages must be greater than zero.");
        if (opts.MaxSyncArchiveBase64Bytes <= 0 || opts.MaxSyncArchiveBytes <= 0 ||
            opts.MaxSyncArchiveExpandedBytes <= 0 || opts.MaxSyncArchiveEntries <= 0 ||
            opts.MaxFileSyncBase64Bytes <= 0 || opts.MaxFileSyncBytes <= 0)
        {
            throw new InvalidOperationException("CodeyBox:Sprites sync size limits must all be greater than zero.");
        }
        return opts;
    }

    private static string GenerateSandboxName(string prefix)
    {
        var normalizedPrefix = prefix.EndsWith("-", StringComparison.Ordinal) ? prefix : prefix + "-";
        return normalizedPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static bool IsValidManagedName(string name, string namePrefix) =>
        !string.IsNullOrWhiteSpace(name)
        && name.StartsWith(namePrefix, StringComparison.Ordinal)
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsValidNamePrefix(string prefix) =>
        prefix.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private IReadOnlyList<SpritesMountSync> ValidateAndPlanMounts(SandboxSpec spec, SpritesSandboxOptions opts)
    {
        var result = new List<SpritesMountSync>();
        foreach (var mount in spec.Mounts)
        {
            if (!mount.SandboxPath.StartsWith("/", StringComparison.Ordinal))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");

            if (IsCredentialPath(mount.SandboxPath))
            {
                if (mount.Tmpfs && mount.HostPath is null)
                    continue;

                throw new NotSupportedException(
                    "sprites.dev does not expose tmpfs mounts; refusing credential mount " +
                    $"{mount.SandboxPath} because it would persist on the sprite ext4 filesystem. " +
                    "Use credential environment variables for sprites-backed sandboxes.");
            }

            if (mount.Tmpfs)
            {
                // Credential tmpfs mounts are already rejected above, so anything reaching here is a
                // non-secret scratch path (e.g. the audit phase's /audit mount). sprites.dev has no
                // tmpfs backing, so we transparently downgrade to a persistent scratch directory
                // rather than throwing — throwing here breaks the work->audit end-to-end flow, which
                // always requests a /audit tmpfs scratch mount. The "fail loudly" contract is about
                // credentials, not scratch space. Surface the downgrade in the log unless the operator
                // has opted in via AllowPersistentTmpfsDowngrade.
                if (!opts.AllowPersistentTmpfsDowngrade &&
                    !mount.SandboxPath.Equals(SandboxConventions.WorkDir, StringComparison.Ordinal))
                {
                    _log.LogWarning(
                        "sprites.dev does not expose tmpfs mounts; downgrading non-secret scratch mount {Path} " +
                        "to a persistent directory (contents land on the sprite ext4 filesystem and are captured " +
                        "by checkpoints). Set CodeyBox:Sprites:AllowPersistentTmpfsDowngrade=true to silence this warning.",
                        mount.SandboxPath);
                }
                result.Add(new SpritesMountSync(mount.SandboxPath, HostPath: null, ReadOnly: false, IsPersistentTmpfsDirectory: true));
                continue;
            }

            if (mount.HostPath is null)
                continue;

            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(
                    hostPath,
                    $"sprites mount source path does not exist: {hostPath}");
            }

            result.Add(new SpritesMountSync(mount.SandboxPath, hostPath, mount.ReadOnly, IsPersistentTmpfsDirectory: false));
        }

        return result;
    }

    private static bool IsCredentialPath(string sandboxPath)
    {
        var trimmed = sandboxPath.TrimEnd('/');
        return trimmed.Equals(SandboxConventions.CredentialsDir, StringComparison.Ordinal)
            || trimmed.StartsWith(SandboxConventions.CredentialsDir + "/", StringComparison.Ordinal);
    }

    private void LogUnsupportedSizingIfNeeded(SandboxSpec spec, SpritesSandboxOptions opts)
    {
        var hasNonDefaultSpecLimits = spec.Limits != SandboxResourceLimits.Default;
        var hasConfiguredSizing = opts.DefaultCpuCount.HasValue || opts.DefaultMemoryBytes.HasValue ||
                                  !string.IsNullOrWhiteSpace(opts.Region);
        if (hasNonDefaultSpecLimits || hasConfiguredSizing)
        {
            _log.LogWarning(
                "Sprites API v0.0.1-rc30 does not accept image, CPU, RAM, disk, flavor, or region fields on create; " +
                "CodeyBox will create {Provider} sandboxes without provider-side sizing.",
                Name);
        }
    }

    private async Task ApplyNetworkPolicyAsync(
        SpritesSandboxOptions opts,
        string name,
        SandboxNetworkPolicy network,
        CancellationToken ct)
    {
        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in network.AllowedHosts)
            AddDomain(domains, host);
        if (!string.IsNullOrWhiteSpace(network.ProfileName))
        {
            if (!opts.NetworkProfiles.TryGetValue(network.ProfileName, out var profileHosts))
                throw new InvalidOperationException(
                    $"Sprites network profile '{network.ProfileName}' is not configured in CodeyBox:Sprites:NetworkProfiles.");
            foreach (var host in profileHosts)
                AddDomain(domains, host);
        }
        if (!string.IsNullOrWhiteSpace(network.HostGitEndpoint))
            AddDomain(domains, StripPort(network.HostGitEndpoint));

        var rules = domains
            .Select(domain => new SpritesNetworkPolicyRule(domain, "allow"))
            .Append(new SpritesNetworkPolicyRule("*", "deny"))
            .ToArray();
        await _client.SetNetworkPolicyAsync(opts, name, rules, ct).ConfigureAwait(false);
    }

    private static void AddDomain(ISet<string> domains, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var domain = value.Trim();
        if (Uri.TryCreate(domain, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            domain = uri.Host;
        domain = StripPort(domain);
        if (domain.Length == 0 || domain.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Invalid sprites network policy domain: '{value}'");
        domains.Add(domain);
    }

    private static string StripPort(string host)
    {
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var end = host.IndexOf(']', StringComparison.Ordinal);
            return end >= 0 ? host[1..end] : host;
        }

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon)
            return host[..colon];
        return host;
    }

    private async Task<bool> TryDeleteAfterCreateFailureAsync(SpritesSandboxOptions opts, string name)
    {
        try
        {
            await _client.DeleteSpriteAsync(opts, name, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception deleteEx)
        {
            _log.LogWarning(deleteEx, "Failed to delete sprites sandbox {Name} after create failure", name);
            return false;
        }
    }

    private void MarkNoLongerActive(string name)
    {
        if (_activeSandboxes.TryRemove(name, out _))
            SandboxLiveCounter.Decrement();
    }

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, SpritesSandbox Sandbox);
}

public sealed record SpritesSandboxOptions
{
    public string ApiBaseUrl { get; init; } = "https://api.sprites.dev";
    public string TokenEnvironmentVariable { get; init; } = "SPRITES_TOKEN";

    /// <summary>
    /// Resolved bearer token. Production wiring leaves this null and resolves
    /// <see cref="TokenEnvironmentVariable"/> at use time; tests may inject it.
    /// </summary>
    public string? Token { get; init; }

    public string NamePrefix { get; init; } = SpritesSandboxProvider.DefaultNamePrefix;
    public bool WaitForCapacity { get; init; }
    public string UrlAuth { get; init; } = "sprite";
    public int MaxListPages { get; init; } = 100;
    public bool AllowUnsafeHttp { get; init; }
    public bool AllowPersistentTmpfsDowngrade { get; init; }
    public IReadOnlyList<string> SetupCommands { get; init; } = [];
    public int MaxSyncArchiveBase64Bytes { get; init; } = 128 * 1024 * 1024;
    public int MaxSyncArchiveBytes { get; init; } = 96 * 1024 * 1024;
    public long MaxSyncArchiveExpandedBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxSyncArchiveEntries { get; init; } = 200_000;
    public int MaxFileSyncBase64Bytes { get; init; } = 64 * 1024 * 1024;
    public long MaxFileSyncBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>
    /// Optional sprites-specific profile-to-domain map used when
    /// <see cref="SandboxNetworkPolicy.ProfileName"/> is set.
    /// </summary>
    public Dictionary<string, List<string>> NetworkProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Present for operator config parity. Sprites rc30 has no create-time CPU field.
    /// </summary>
    public int? DefaultCpuCount { get; init; }

    /// <summary>
    /// Present for operator config parity. Sprites rc30 has no create-time RAM field.
    /// </summary>
    public long? DefaultMemoryBytes { get; init; }

    /// <summary>
    /// Present for operator config parity. Sprites rc30 has no create-time region field.
    /// </summary>
    public string? Region { get; init; }
}

internal sealed class SpritesSandbox : IShutdownTeardownSandbox, IRejectsFileBackedAgentCredentials
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Upper bound on how long teardown will wait for an in-flight exec to release the exec gate
    // before abandoning the final mount sync and proceeding to delete the sprite. Deletion must
    // always happen, so this must be finite.
    private static readonly TimeSpan DisposeGateWaitTimeout = TimeSpan.FromSeconds(30);

    // Slack added on top of the configured output cap when bounding a single WebSocket message, to
    // cover the 1-byte stream-ID prefix and WebSocket framing overhead for a legitimately cap-sized
    // payload delivered as one message.
    private const long MessageSizeSlackBytes = 1024 * 1024;

    // Hard per-stream ceiling applied to the accumulated stdout/stderr buffer AND the per-message
    // accumulation when the caller supplies no explicit MaxStdoutBytes/MaxStderrBytes. The primary
    // agent exec path (CliAgentRunnerBase) builds SandboxExec without output caps, so without this
    // floor an untrusted in-sprite process emitting unbounded output (e.g. `yes`) would let the host
    // buffer the full volume in ReceiveMessageAsync / LimitedOutputCollector and exhaust host memory,
    // defeating the resource ceiling the sandbox is meant to enforce. Chosen large enough not to
    // truncate legitimate build/agent output; the guard is a DoS backstop, not a functional limit.
    internal const int DefaultMaxStreamBytes = 64 * 1024 * 1024;

    private const string EnvironmentBootstrapScript =
        """
        set -eu
        __codeybox_env_bytes="$1"
        __codeybox_wd="$2"
        __codeybox_unset_count="$3"
        shift 3
        __codeybox_env_payload="$(dd bs=1 count="$__codeybox_env_bytes" 2>/dev/null || true)"
        while IFS= read -r __codeybox_env_line; do
          [ -n "$__codeybox_env_line" ] || continue
          __codeybox_env_key="${__codeybox_env_line%%=*}"
          __codeybox_env_value_b64="${__codeybox_env_line#*=}"
          __codeybox_env_value="$(printf '%s' "$__codeybox_env_value_b64" | base64 -d)"
          export "$__codeybox_env_key=$__codeybox_env_value"
        done <<__CODEYBOX_ENV__
        $__codeybox_env_payload
        __CODEYBOX_ENV__
        while [ "$__codeybox_unset_count" -gt 0 ]; do
          unset -- "$1"
          shift
          __codeybox_unset_count=$((__codeybox_unset_count - 1))
        done
        if [ -n "$__codeybox_wd" ]; then
          mkdir -p "$__codeybox_wd"
          cd "$__codeybox_wd"
        fi
        exec "$@"
        """;

    private readonly SandboxSpec _spec;
    private readonly SpritesSandboxOptions _opts;
    private readonly SpritesApiClient _client;
    private readonly ISpritesWebSocketFactory _webSocketFactory;
    private readonly IReadOnlyList<SpritesMountSync> _mounts;
    private readonly Action _onDisposed;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, byte> _activeSessions = new();
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private int _ownedByShutdownHandler;
    private int _disposing;
    private int _disposed;

    public SpritesSandbox(
        string id,
        SandboxSpec spec,
        SpritesSandboxOptions opts,
        SpritesApiClient client,
        ISpritesWebSocketFactory webSocketFactory,
        IReadOnlyList<SpritesMountSync> mounts,
        Action onDisposed,
        ILogger log)
    {
        Id = id;
        _spec = spec;
        _opts = opts;
        _client = client;
        _webSocketFactory = webSocketFactory;
        _mounts = mounts;
        _onDisposed = onDisposed;
        _log = log;
    }

    public string Id { get; }

    public string FileBackedAgentCredentialsUnsupportedReason =>
        "sprites.dev has no tmpfs-backed credential path; file-backed OAuth/subscription credentials would persist on the sprite ext4 filesystem.";

    public bool IsOwnedByShutdownHandler => Volatile.Read(ref _ownedByShutdownHandler) != 0;

    public void MarkOwnedByShutdownHandler() => Interlocked.Exchange(ref _ownedByShutdownHandler, 1);

    internal async Task RunSetupCommandsAsync(CancellationToken ct)
    {
        foreach (var command in _opts.SetupCommands)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;

            var result = await ExecRawAsync(new SandboxExec
            {
                Argv = ["bash", "-lc", command],
                WorkingDirectory = "/",
                // Setup commands are the sole provisioning path for rc30 sprites (no baseline image),
                // so apt/npm/curl installs land here — the noisiest phase. The 1 MiB hardcode that used
                // to bound this was inconsistent with the agent exec path's DefaultMaxStreamBytes
                // (64 MiB) and could strand provisioning when an install log exceeded 1 MiB (apt
                // unpack progress is emitted to stderr). Reuse the same DoS ceiling the agent path
                // uses; it is still hard-bounded and far larger than any legitimate install log.
                MaxStdoutBytes = DefaultMaxStreamBytes,
                MaxStderrBytes = DefaultMaxStreamBytes,
                KillOnOutputLimit = true,
            }, syncWritableMounts: false, allowDuringDispose: false, includeSpecEnvironment: false, ct: ct).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Sprites setup command failed in {Id} with exit {result.ExitCode}: {Tail(result.Stderr)}");
            }
        }
    }

    internal async Task PrepareFilesystemAsync(CancellationToken ct)
    {
        foreach (var mount in _mounts)
        {
            if (mount.IsPersistentTmpfsDirectory)
            {
                await ExecRawAsync(new SandboxExec
                {
                    Argv = ["mkdir", "-p", mount.SandboxPath],
                    WorkingDirectory = "/",
                }, syncWritableMounts: false, allowDuringDispose: false, includeSpecEnvironment: false, ct: ct).ConfigureAwait(false);
                continue;
            }

            if (mount.HostPath is null)
                continue;

            if (Directory.Exists(mount.HostPath))
                await UploadDirectoryAsync(mount.HostPath, mount.SandboxPath, ct).ConfigureAwait(false);
            else
                await UploadFileAsync(mount.HostPath, mount.SandboxPath, ct).ConfigureAwait(false);
        }
    }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        await _execGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ExecRawAsync(exec, syncWritableMounts: false, allowDuringDispose: false, includeSpecEnvironment: true, ct: ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _execGate.Release();
        }
    }

    private async Task<SandboxExecResult> ExecRawAsync(
        SandboxExec exec,
        bool syncWritableMounts,
        bool allowDuringDispose,
        bool includeSpecEnvironment,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            (Volatile.Read(ref _disposing) != 0 && !allowDuringDispose))
            throw new ObjectDisposedException(nameof(SpritesSandbox));
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Argv must be non-empty", nameof(exec));
        var effectiveEnvironment = BuildEffectiveEnvironment(exec, includeSpecEnvironment);
        if (WouldPersistCredentialFile(exec))
        {
            throw new NotSupportedException(
                "sprites.dev does not expose tmpfs credential storage; refusing to write credential file material " +
                "to the sprite ext4 filesystem. Use non-file credential environment variables for sprites-backed sandboxes.");
        }
        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(exec.WorkingDirectory)
            ? (_spec.WorkingDirectory ?? "/")
            : exec.WorkingDirectory;
        var wireExec = BuildWireExec(exec, effectiveEnvironment, effectiveWorkingDirectory);

        int? sessionId = null;
        await using var webSocket = _webSocketFactory.Create();
        try
        {
            await webSocket.ConnectAsync(BuildExecWebSocketUri(wireExec), _opts.Token!, ct).ConfigureAwait(false);
            var stdout = new LimitedOutputCollector(exec.MaxStdoutBytes ?? DefaultMaxStreamBytes, exec.StdoutChunkCallback);
            var stderr = new LimitedOutputCollector(exec.MaxStderrBytes ?? DefaultMaxStreamBytes, exec.StderrChunkCallback);
            var exitCode = await ReadExecUntilExitAsync(
                webSocket,
                exec,
                wireExec.Stdin,
                stdout,
                stderr,
                id =>
                {
                    sessionId = id;
                    _activeSessions[id] = 0;
                },
                ct).ConfigureAwait(false);

            var result = new SandboxExecResult(
                exitCode ?? 255,
                stdout.ToString(),
                stderr.ToString(),
                stdout.LimitExceeded,
                stderr.LimitExceeded,
                ExecutionUnavailable: exitCode is null);

            if (syncWritableMounts)
                await SyncWritableMountsToHostAsync(CancellationToken.None, allowDuringDispose: false).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (sessionId.HasValue)
                await KillExecAsync(sessionId.Value, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (sessionId.HasValue)
                _activeSessions.TryRemove(sessionId.Value, out _);
        }
    }

    private async Task<int?> ReadExecUntilExitAsync(
        ISpritesWebSocket webSocket,
        SandboxExec exec,
        string? stdin,
        LimitedOutputCollector stdout,
        LimitedOutputCollector stderr,
        Action<int> onSessionInfo,
        CancellationToken ct)
    {
        var sentStdin = false;
        var killRequested = false;
        int? exitCode = null;
        var maxMessageBytes = ComputeMaxMessageBytes(exec);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (webSocket.State == WebSocketState.Open && exitCode is null)
            {
                var message = await ReceiveMessageAsync(webSocket, buffer, maxMessageBytes, ct).ConfigureAwait(false);
                if (message is null)
                    break;

                if (message.Value.MessageType == WebSocketMessageType.Text)
                {
                    using var doc = JsonDocument.Parse(message.Value.Payload);
                    if (!doc.RootElement.TryGetProperty("type", out var typeElement) ||
                        typeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    switch (typeElement.GetString())
                    {
                        case "session_info":
                            if (doc.RootElement.TryGetProperty("session_id", out var idElement) &&
                                idElement.TryGetInt32(out var id))
                            {
                                onSessionInfo(id);
                                if (!sentStdin)
                                {
                                    await SendStdinAsync(webSocket, stdin, ct).ConfigureAwait(false);
                                    sentStdin = true;
                                }
                            }
                            break;
                        case "exit":
                            if (doc.RootElement.TryGetProperty("exit_code", out var exitElement) &&
                                exitElement.TryGetInt32(out var parsedExit))
                            {
                                exitCode = parsedExit;
                            }
                            break;
                    }
                    continue;
                }

                if (message.Value.Payload.Length == 0)
                    continue;
                var stream = message.Value.Payload[0];
                var payload = message.Value.Payload[1..];
                switch (stream)
                {
                    case 1:
                        stdout.Append(payload.AsSpan());
                        break;
                    case 2:
                        stderr.Append(payload.AsSpan());
                        break;
                    case 3:
                        if (payload.Length > 0)
                            exitCode = payload[0];
                        break;
                }

                // Once the output limit is exceeded the LimitExceeded flag stays latched while the
                // remaining buffered frames drain, so guard the kill with killRequested to issue the
                // kill POST at most once per session instead of re-POSTing on every subsequent frame.
                if (!killRequested && (stdout.LimitExceeded || stderr.LimitExceeded) && exec.KillOnOutputLimit)
                {
                    killRequested = true;
                    await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return exitCode;
    }

    // The output caps (MaxStdoutBytes/MaxStderrBytes) are enforced by LimitedOutputCollector only
    // AFTER a whole WebSocket message has been received, so an untrusted in-sprite process that emits
    // a single huge fragmented message would otherwise let the host buffer the full volume in
    // ReceiveMessageAsync before KillOnOutputLimit can fire — a memory-exhaustion DoS that defeats the
    // resource ceiling. Bound the accumulated per-message size to the larger configured output cap
    // (falling back to the hard DefaultMaxStreamBytes ceiling when the caller supplies no cap, so the
    // primary agent exec path is protected too) plus generous slack (WebSocket framing + the 1-byte
    // stream prefix); exceeding it aborts the exec, whose catch path kills the session. This never
    // returns null — an uncapped caller must still not make the host buffer unbounded bytes.
    private static long ComputeMaxMessageBytes(SandboxExec exec)
    {
        var cap = Math.Max(exec.MaxStdoutBytes ?? 0, exec.MaxStderrBytes ?? 0);
        if (cap <= 0)
            cap = DefaultMaxStreamBytes;
        return cap + MessageSizeSlackBytes;
    }

    private static async Task<ReceivedWebSocketMessage?> ReceiveMessageAsync(
        ISpritesWebSocket webSocket,
        byte[] buffer,
        long maxMessageBytes,
        CancellationToken ct)
    {
        using var payload = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            payload.Write(buffer, 0, result.Count);
            if (payload.Length > maxMessageBytes)
                throw new InvalidOperationException(
                    $"sprites exec message exceeded the {maxMessageBytes}-byte per-message ceiling " +
                    "(output-cap defeat / memory-exhaustion guard).");
        }
        while (!result.EndOfMessage);

        return new ReceivedWebSocketMessage(result.MessageType, payload.ToArray());
    }

    private static async Task SendStdinAsync(ISpritesWebSocket webSocket, string? stdin, CancellationToken ct)
    {
        if (stdin is not null)
        {
            var input = Utf8.GetBytes(stdin);
            var framed = new byte[input.Length + 1];
            framed[0] = 0;
            input.CopyTo(framed.AsSpan(1));
            await webSocket.SendAsync(framed, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
        await webSocket.SendAsync(new byte[] { 4 }, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private Uri BuildExecWebSocketUri(SpritesWireExec exec)
    {
        var baseUri = new Uri(_opts.ApiBaseUrl);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = CombinePath(baseUri.AbsolutePath, $"v1/sprites/{Uri.EscapeDataString(Id)}/exec"),
        };
        if (builder.Port == 80 || builder.Port == 443)
            builder.Port = -1;

        // The working directory is established by the in-sandbox bootstrap (mkdir -p <wd>; cd <wd>)
        // before exec "$@", rather than the documented `dir` query param. rc30 sprites have no baseline
        // image, so the spec working directory (default /work) does not exist until the bootstrap
        // creates it; sending `dir=/work` would have the sprite chdir into a nonexistent path before
        // any script runs (the work->audit flow's first exec is `git clone <url> /work`). The bootstrap
        // creates the directory lazily on the same exec, so the clone lands in /work.
        var query = new List<KeyValuePair<string, string>>();
        foreach (var arg in exec.Argv)
            query.Add(new KeyValuePair<string, string>("cmd", arg));
        query.Add(new KeyValuePair<string, string>("tty", "false"));

        builder.Query = string.Join('&', query.Select(q =>
            $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        return builder.Uri;
    }

    private static SpritesWireExec BuildWireExec(
        SandboxExec exec,
        IReadOnlyDictionary<string, string> effectiveEnvironment,
        string workingDirectory)
    {
        var envBlock = BuildEnvironmentBlock(effectiveEnvironment);
        var envBlockBytes = Utf8.GetByteCount(envBlock);
        var argv = new List<string>(exec.Argv.Count + 7 + exec.EnvironmentVariablesToUnset.Count)
        {
            "sh",
            "-c",
            EnvironmentBootstrapScript,
            "_",
            envBlockBytes.ToString(CultureInfo.InvariantCulture),
            workingDirectory,
            exec.EnvironmentVariablesToUnset.Count.ToString(CultureInfo.InvariantCulture),
        };
        argv.AddRange(exec.EnvironmentVariablesToUnset);
        argv.AddRange(exec.Argv);
        return new SpritesWireExec(argv, envBlock + (exec.Stdin ?? ""));
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (!IsValidEnvironmentKey(key))
                throw new ArgumentException($"Invalid environment variable name for sprites exec: {key}");
            builder
                .Append(key)
                .Append('=')
                .Append(Convert.ToBase64String(Utf8.GetBytes(value)))
                .Append('\n');
        }
        return builder.ToString();
    }

    private static bool IsValidEnvironmentKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        if (key[0] != '_' && !char.IsAsciiLetter(key[0]))
            return false;
        for (var i = 1; i < key.Length; i++)
        {
            var c = key[i];
            if (c != '_' && !char.IsAsciiLetterOrDigit(c))
                return false;
        }
        return true;
    }

    private Dictionary<string, string> BuildEffectiveEnvironment(SandboxExec exec, bool includeSpecEnvironment)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = "/root",
        };
        if (includeSpecEnvironment)
        {
            foreach (var (key, value) in _spec.Environment)
                env[key] = value;
        }
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in exec.ExtraEnvironment)
                env[key] = value;
        }
        exec.ApplyEnvironmentRemovals(name => env.Remove(name));
        return env;
    }

    /// <summary>
    /// Best-effort exec-level backstop that rejects commands writing a credential
    /// file into <see cref="SandboxConventions.CredentialsDir"/>. It only inspects
    /// a narrow shape (non-null Stdin + an argv element that names the credentials
    /// directory); a credential-writing exec built from a redirect inside a shell
    /// script, a here-doc, or an env-var-driven path would bypass it. The robust
    /// defense is the runner-level guard (<c>RejectUnsupportedFileBackedCredentials</c>,
    /// which walks the decorator chain to the SpritesSandbox and covers every
    /// file-materialising runner); this exec-level check is defence-in-depth only.
    /// </summary>
    private static bool WouldPersistCredentialFile(SandboxExec exec)
    {
        if (exec.Stdin is not null &&
            exec.Argv.Any(arg =>
                arg.Equals(SandboxConventions.CredentialsDir, StringComparison.Ordinal) ||
                arg.Contains(SandboxConventions.CredentialsDir + "/", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private async Task UploadDirectoryAsync(string hostPath, string sandboxPath, CancellationToken ct)
    {
        var archive = CreateDirectoryArchiveBase64(hostPath);
        var result = await ExecRawAsync(new SandboxExec
        {
            Argv =
            [
                "sh",
                "-c",
                // No pipe: `base64 -d | tar -xzf -` would let a failed base64
                // decode (truncated/corrupt payload) be masked by tar's exit, so
                // a bad upload could still extract a partial tree. Decode to a temp
                // file first so a base64 failure aborts before tar touches "$1".
                "set -eu; rm -rf \"$1\"; mkdir -p \"$1\"; tmp=$(mktemp); trap 'rm -f \"$tmp\"' EXIT; base64 -d > \"$tmp\"; tar -xzf \"$tmp\" -C \"$1\"",
                "_",
                sandboxPath,
            ],
            WorkingDirectory = "/",
            Stdin = archive,
        }, syncWritableMounts: false, allowDuringDispose: false, includeSpecEnvironment: false, ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to stage host directory {hostPath} into sprite {Id}:{sandboxPath}: {result.Stderr}");
    }

    private async Task UploadFileAsync(string hostPath, string sandboxPath, CancellationToken ct)
    {
        var payload = Convert.ToBase64String(await File.ReadAllBytesAsync(hostPath, ct).ConfigureAwait(false));
        var result = await ExecRawAsync(new SandboxExec
        {
            Argv =
            [
                "sh",
                "-c",
                "set -eu; mkdir -p \"$(dirname \"$1\")\"; base64 -d > \"$1\"",
                "_",
                sandboxPath,
            ],
            WorkingDirectory = "/",
            Stdin = payload,
        }, syncWritableMounts: false, allowDuringDispose: false, includeSpecEnvironment: false, ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to stage host file {hostPath} into sprite {Id}:{sandboxPath}: {result.Stderr}");
    }

    private async Task SyncWritableMountsToHostAsync(CancellationToken ct, bool allowDuringDispose)
    {
        foreach (var mount in _mounts)
        {
            if (mount.ReadOnly || mount.HostPath is null)
                continue;

            if (Directory.Exists(mount.HostPath))
                await SyncDirectoryToHostAsync(mount.SandboxPath, mount.HostPath, allowDuringDispose, ct).ConfigureAwait(false);
            else if (File.Exists(mount.HostPath))
                await SyncFileToHostAsync(mount.SandboxPath, mount.HostPath, allowDuringDispose, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncDirectoryToHostAsync(
        string sandboxPath,
        string hostPath,
        bool allowDuringDispose,
        CancellationToken ct)
    {
        var result = await ExecRawAsync(new SandboxExec
        {
            Argv =
            [
                "sh",
                "-c",
                // No pipe: POSIX sh (dash) lacks `pipefail` and `set -e` only
                // inspects the LAST pipe stage, so `tar ... | base64` would mask
                // a mid-stream tar read/permission failure behind base64's exit 0
                // and hand a truncated/empty archive to the host-overwrite path.
                // Stage tar to a temp file first so a tar failure aborts the sync.
                "set -eu; test -d \"$1\"; tmp=$(mktemp); trap 'rm -f \"$tmp\"' EXIT; tar -czf \"$tmp\" -C \"$1\" .; base64 -w0 \"$tmp\"",
                "_",
                sandboxPath,
            ],
            WorkingDirectory = "/",
            MaxStdoutBytes = _opts.MaxSyncArchiveBase64Bytes,
            MaxStderrBytes = 64 * 1024,
            KillOnOutputLimit = true,
        }, syncWritableMounts: false, allowDuringDispose: allowDuringDispose, includeSpecEnvironment: false, ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to archive sprite directory {Id}:{sandboxPath}: {result.Stderr}");

        ReplaceHostDirectoryFromArchive(hostPath, result.Stdout.Trim(), _opts);
    }

    private async Task SyncFileToHostAsync(
        string sandboxPath,
        string hostPath,
        bool allowDuringDispose,
        CancellationToken ct)
    {
        var result = await ExecRawAsync(new SandboxExec
        {
            Argv = ["base64", "-w0", sandboxPath],
            WorkingDirectory = "/",
            MaxStdoutBytes = _opts.MaxFileSyncBase64Bytes,
            MaxStderrBytes = 64 * 1024,
            KillOnOutputLimit = true,
        }, syncWritableMounts: false, allowDuringDispose: allowDuringDispose, includeSpecEnvironment: false, ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to read sprite file {Id}:{sandboxPath}: {result.Stderr}");

        var archiveBase64 = result.Stdout.Trim();
        if (archiveBase64.Length > _opts.MaxFileSyncBase64Bytes)
            throw new InvalidOperationException($"Sprite file sync exceeded base64 limit for {Id}:{sandboxPath}.");
        var bytes = Convert.FromBase64String(archiveBase64);
        if (bytes.LongLength > _opts.MaxFileSyncBytes)
            throw new InvalidOperationException($"Sprite file sync exceeded decoded-size limit for {Id}:{sandboxPath}.");
        var parent = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        File.WriteAllBytes(hostPath, bytes);
    }

    private static string CreateDirectoryArchiveBase64(string hostPath)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            TarFile.CreateFromDirectory(hostPath, gzip, includeBaseDirectory: false);
        return Convert.ToBase64String(output.ToArray());
    }

    private static void ReplaceHostDirectoryFromArchive(
        string hostPath,
        string archiveBase64,
        SpritesSandboxOptions opts)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(hostPath))
            ?? throw new InvalidOperationException($"Unable to determine parent directory for {hostPath}");
        Directory.CreateDirectory(parent);

        var tempPath = Path.Combine(parent, $".codeybox-sprites-sync-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(parent, $".codeybox-sprites-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        var movedExisting = false;
        try
        {
            if (archiveBase64.Length > opts.MaxSyncArchiveBase64Bytes)
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} exceeded base64 limit.");
            var bytes = Convert.FromBase64String(archiveBase64);
            if (bytes.LongLength > opts.MaxSyncArchiveBytes)
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} exceeded compressed-size limit.");
            ValidateTarGzipArchive(bytes, opts, hostPath);
            using (var input = new MemoryStream(bytes))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, tempPath, overwriteFiles: true);
            }

            var hadExisting = Directory.Exists(hostPath);
            if (hadExisting)
            {
                Directory.Move(hostPath, backupPath);
                movedExisting = true;
            }
            Directory.Move(tempPath, hostPath);
            if (hadExisting)
                Directory.Delete(backupPath, recursive: true);
        }
        catch
        {
            if (movedExisting && Directory.Exists(backupPath))
            {
                if (Directory.Exists(hostPath))
                    Directory.Delete(hostPath, recursive: true);
                Directory.Move(backupPath, hostPath);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
            if (Directory.Exists(backupPath) && !Directory.Exists(hostPath))
                Directory.Move(backupPath, hostPath);
        }
    }

    private static void ValidateTarGzipArchive(byte[] bytes, SpritesSandboxOptions opts, string hostPath)
    {
        var entries = 0;
        long expandedBytes = 0;
        var root = Path.GetFullPath(hostPath);
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            entries++;
            if (entries > opts.MaxSyncArchiveEntries)
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} exceeded file-count limit.");

            var destination = Path.GetFullPath(Path.Combine(root, entry.Name));
            if (!destination.Equals(root, StringComparison.Ordinal) &&
                !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} contains an unsafe path.");
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} contains a link entry.");
            if (entry.EntryType is not TarEntryType.Directory and not TarEntryType.RegularFile)
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} contains unsupported entry type {entry.EntryType}.");

            expandedBytes += entry.Length;
            if (expandedBytes > opts.MaxSyncArchiveExpandedBytes)
                throw new InvalidOperationException($"Sprite sync archive for {hostPath} exceeded expanded-size limit.");
        }
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        foreach (var sessionId in _activeSessions.Keys)
            await KillExecAsync(sessionId, ct).ConfigureAwait(false);
    }

    private async Task KillExecAsync(int sessionId, CancellationToken ct)
    {
        try
        {
            await _client.KillExecAsync(_opts, Id, sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to kill sprites exec session {SessionId} in {SpriteName}", sessionId, Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0)
            return;
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _disposing, 0);
            return;
        }
        _onDisposed();

        await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // The final mount sync needs exclusive exec access, but teardown MUST delete the sprite
            // regardless of state. If an in-flight exec still holds the gate (e.g. its WebSocket
            // ReceiveAsync is stalled and the best-effort kill above failed during a network-degraded
            // teardown), a blocking WaitAsync would hang disposal forever and leak the sprite. Bound
            // the wait: if we cannot acquire the gate in time, skip the sync and proceed to delete.
            var acquiredGate = await _execGate.WaitAsync(DisposeGateWaitTimeout, CancellationToken.None).ConfigureAwait(false);
            if (acquiredGate)
            {
                try
                {
                    await SyncWritableMountsToHostAsync(CancellationToken.None, allowDuringDispose: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Final sprites mount sync failed for {SpriteName}; proceeding with teardown", Id);
                }
                finally
                {
                    _execGate.Release();
                    // Safe to dispose now: we hold the gate exclusively and the _disposing flag blocks
                    // any new exec from acquiring it, so no late Release can hit a disposed semaphore.
                    // In the timeout path below a stalled exec still owns the gate, so we deliberately
                    // do NOT dispose there (accepting the minor handle leak over an ObjectDisposedException).
                    _execGate.Dispose();
                }
            }
            else
            {
                _log.LogWarning(
                    "Could not acquire exec gate within {Timeout} during teardown of {SpriteName}; " +
                    "skipping final mount sync and proceeding directly to sprite deletion.",
                    DisposeGateWaitTimeout, Id);
            }

            await _client.DeleteSpriteAsync(_opts, Id, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Deleted sprites sandbox {Name}", Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to delete sprites sandbox {Name}", Id);
        }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            Volatile.Write(ref _disposing, 0);
        }
    }

    private static string CombinePath(string basePath, string relative)
    {
        var prefix = string.IsNullOrWhiteSpace(basePath) || basePath == "/"
            ? ""
            : basePath.TrimEnd('/');
        return $"{prefix}/{relative.TrimStart('/')}";
    }

    private static string Tail(string text)
    {
        const int max = 500;
        return text.Length <= max ? text : text[^max..];
    }

    private readonly record struct ReceivedWebSocketMessage(WebSocketMessageType MessageType, byte[] Payload);

    private readonly record struct SpritesWireExec(
        IReadOnlyList<string> Argv,
        string? Stdin);
}

internal sealed record SpritesMountSync(
    string SandboxPath,
    string? HostPath,
    bool ReadOnly,
    bool IsPersistentTmpfsDirectory);

internal sealed class LimitedOutputCollector
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly int? _limit;
    private readonly Action<string>? _callback;
    private readonly MemoryStream _buffer = new();
    // Trailing bytes of the most recent Append that did not complete a full UTF-8 character. They are
    // already written to _buffer (so ToString decodes the whole buffer at once), but the live per-chunk
    // callback must NOT decode them in isolation: WebSocket binary frames (and the output-cap
    // truncation) can split a multi-byte UTF-8 sequence across two Append calls, and decoding the
    // partial bytes here emits U+FFFD replacement characters into the streamed callback — which for
    // the primary agent path feeds the structured stream-json parser and can corrupt a JSON event
    // (dropping the captured CLI session id / usage metric). They are prepended to the next chunk so
    // the callback only ever emits complete characters.
    private readonly List<byte> _pendingUtf8 = new();

    public LimitedOutputCollector(int? limit, Action<string>? callback)
    {
        _limit = limit;
        _callback = callback;
    }

    public bool LimitExceeded { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return;

        var allowed = bytes.Length;
        if (_limit.HasValue)
        {
            var remaining = _limit.Value - _buffer.Length;
            if (remaining <= 0)
            {
                LimitExceeded = true;
                return;
            }
            if (allowed > remaining)
            {
                allowed = (int)remaining;
                LimitExceeded = true;
            }
        }

        _buffer.Write(bytes[..allowed]);
        if (_callback is null)
            return;

        byte[] decodeBytes;
        if (_pendingUtf8.Count > 0)
        {
            decodeBytes = new byte[_pendingUtf8.Count + allowed];
            _pendingUtf8.CopyTo(decodeBytes, 0);
            bytes[..allowed].ToArray().CopyTo(decodeBytes, _pendingUtf8.Count);
            _pendingUtf8.Clear();
        }
        else
        {
            decodeBytes = bytes[..allowed].ToArray();
        }

        var complete = CountCompleteUtf8Bytes(decodeBytes);
        if (complete < decodeBytes.Length)
        {
            _pendingUtf8.AddRange(decodeBytes.AsSpan(complete));
            if (complete == 0)
                return;
            decodeBytes = decodeBytes[..complete];
        }

        _callback(Utf8.GetString(decodeBytes));
    }

    public override string ToString() => Utf8.GetString(_buffer.ToArray());

    // Returns the length of the longest valid UTF-8 prefix of <paramref name="bytes"/>; any trailing
    // bytes that start (but do not complete) a multi-byte sequence are left for the next Append.
    private static int CountCompleteUtf8Bytes(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            int seqLen;
            if (b < 0x80) seqLen = 1;
            else if ((b & 0xE0) == 0xC0) seqLen = 2;
            else if ((b & 0xF0) == 0xE0) seqLen = 3;
            else if ((b & 0xF8) == 0xF0) seqLen = 4;
            else return i; // invalid lead byte (stray continuation byte or 0xF8+); stop before it
            if (i + seqLen > bytes.Length) return i;
            for (var j = 1; j < seqLen; j++)
            {
                if ((bytes[i + j] & 0xC0) != 0x80) return i;
            }
            i += seqLen;
        }
        return i;
    }
}

internal sealed class SpritesApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public SpritesApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task CreateSpriteAsync(SpritesSandboxOptions opts, string name, CancellationToken ct)
    {
        var body = new SpriteCreateRequest(
            name,
            opts.WaitForCapacity,
            new SpriteUrlSettings(opts.UrlAuth));
        using var response = await SendJsonAsync(
            opts,
            HttpMethod.Post,
            "v1/sprites",
            body,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create sprite", ct).ConfigureAwait(false);
    }

    public async Task DeleteSpriteAsync(SpritesSandboxOptions opts, string name, CancellationToken ct)
    {
        using var response = await SendAsync(
            opts,
            HttpMethod.Delete,
            $"v1/sprites/{Uri.EscapeDataString(name)}",
            content: null,
            ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response, "delete sprite", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SpriteListItem>> ListSpritesAsync(
        SpritesSandboxOptions opts,
        string prefix,
        CancellationToken ct)
    {
        var result = new List<SpriteListItem>();
        string? continuation = null;
        for (var page = 0; page < opts.MaxListPages; page++)
        {
            var query = $"prefix={Uri.EscapeDataString(prefix)}&max_results=50";
            if (!string.IsNullOrEmpty(continuation))
                query += $"&continuation_token={Uri.EscapeDataString(continuation)}";
            using var response = await SendAsync(
                opts,
                HttpMethod.Get,
                $"v1/sprites?{query}",
                content: null,
                ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "list sprites", ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var pageResult = JsonSerializer.Deserialize<SpriteListResponse>(payload, JsonOptions)
                ?? new SpriteListResponse([], false, null);
            foreach (var sprite in pageResult.Sprites)
            {
                if (!sprite.Name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                result.Add(await FillCreatedAtAsync(opts, sprite, ct).ConfigureAwait(false));
            }
            if (!pageResult.HasMore || string.IsNullOrWhiteSpace(pageResult.NextContinuationToken))
                return result;
            continuation = pageResult.NextContinuationToken;
        }
        return result;
    }

    public async Task SetNetworkPolicyAsync(
        SpritesSandboxOptions opts,
        string name,
        IReadOnlyList<SpritesNetworkPolicyRule> rules,
        CancellationToken ct)
    {
        using var response = await SendJsonAsync(
            opts,
            HttpMethod.Post,
            $"v1/sprites/{Uri.EscapeDataString(name)}/policy/network",
            new SpritesNetworkPolicyRequest(rules),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "set sprite network policy", ct).ConfigureAwait(false);
    }

    public async Task KillExecAsync(SpritesSandboxOptions opts, string name, int sessionId, CancellationToken ct)
    {
        using var response = await SendAsync(
            opts,
            HttpMethod.Post,
            $"v1/sprites/{Uri.EscapeDataString(name)}/exec/{sessionId.ToString(CultureInfo.InvariantCulture)}/kill",
            content: null,
            ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response, "kill sprites exec", ct).ConfigureAwait(false);
        await WaitForKillCompleteAsync(response, name, sessionId, ct).ConfigureAwait(false);
    }

    private static async Task WaitForKillCompleteAsync(
        HttpResponseMessage response,
        string name,
        int sessionId,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sawAnyEvent = false;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            sawAnyEvent = true;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeElement) &&
                string.Equals(typeElement.GetString(), "complete", StringComparison.Ordinal))
            {
                return;
            }
        }

        if (sawAnyEvent)
        {
            throw new InvalidOperationException(
                $"Sprites kill for {name} session {sessionId.ToString(CultureInfo.InvariantCulture)} ended before a complete event.");
        }
    }

    private async Task<SpriteListItem> FillCreatedAtAsync(
        SpritesSandboxOptions opts,
        SpriteListItem sprite,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            opts,
            HttpMethod.Get,
            $"v1/sprites/{Uri.EscapeDataString(sprite.Name)}",
            content: null,
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return sprite;
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var detail = JsonSerializer.Deserialize<SpriteDetailResponse>(payload, JsonOptions);
        return detail?.CreatedAt is { } createdAt
            ? sprite with { CreatedAt = createdAt }
            : sprite;
    }

    private Task<HttpResponseMessage> SendJsonAsync<T>(
        SpritesSandboxOptions opts,
        HttpMethod method,
        string pathAndQuery,
        T body,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return SendAsync(opts, method, pathAndQuery, new StringContent(json, Encoding.UTF8, "application/json"), ct);
    }

    private Task<HttpResponseMessage> SendAsync(
        SpritesSandboxOptions opts,
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, BuildRestUri(opts.ApiBaseUrl, pathAndQuery))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.Token);
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length > 500)
            body = body[^500..];
        throw new InvalidOperationException(
            $"Sprites API {operation} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static Uri BuildRestUri(string apiBaseUrl, string pathAndQuery)
    {
        var baseUri = new Uri(apiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, pathAndQuery);
    }

    private sealed record SpriteCreateRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("wait_for_capacity")] bool WaitForCapacity,
        [property: JsonPropertyName("url_settings")] SpriteUrlSettings UrlSettings);

    private sealed record SpriteUrlSettings([property: JsonPropertyName("auth")] string Auth);

    private sealed record SpriteListResponse(
        [property: JsonPropertyName("sprites")] IReadOnlyList<SpriteListItem> Sprites,
        [property: JsonPropertyName("has_more")] bool HasMore,
        [property: JsonPropertyName("next_continuation_token")] string? NextContinuationToken);

    private sealed record SpriteDetailResponse([property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);
}

internal sealed record SpriteListItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt)
{
    public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed record SpritesNetworkPolicyRequest(
    [property: JsonPropertyName("rules")] IReadOnlyList<SpritesNetworkPolicyRule> Rules);

internal sealed record SpritesNetworkPolicyRule(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("action")] string Action);

internal interface ISpritesWebSocketFactory
{
    ISpritesWebSocket Create();
}

internal interface ISpritesWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, string bearerToken, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct);
    Task<WebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}

internal sealed class ClientWebSocketSpritesWebSocketFactory : ISpritesWebSocketFactory
{
    public ISpritesWebSocket Create() => new ClientWebSocketSpritesWebSocket();
}

internal sealed class ClientWebSocketSpritesWebSocket : ISpritesWebSocket
{
    private readonly ClientWebSocket _inner = new();

    public WebSocketState State => _inner.State;

    public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken ct)
    {
        _inner.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        return _inner.ConnectAsync(uri, ct);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
        await _inner.SendAsync(buffer, messageType, endOfMessage, ct).ConfigureAwait(false);

    public async Task<WebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var result = await _inner.ReceiveAsync(buffer, ct).ConfigureAwait(false);
        return new WebSocketReceiveResult(
            result.Count,
            result.MessageType,
            result.EndOfMessage);
    }

    public async ValueTask DisposeAsync()
    {
        _inner.Dispose();
        await ValueTask.CompletedTask;
    }
}
