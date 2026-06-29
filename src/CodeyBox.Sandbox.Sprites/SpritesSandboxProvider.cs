using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
/// bind mounts, so host mounts are staged into the sprite and writable mounts
/// are synchronized back after execs.
/// </summary>
public sealed class SpritesSandboxProvider : ISandboxProvider, IActiveSandboxProvider, IActiveSandboxProgressProvider
{
    public const string DefaultNamePrefix = "codeybox-";

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
        var syncMounts = ValidateAndPlanMounts(spec);
        var workItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var activeEntry = new ActiveSandboxEntry(workItemId);

        try
        {
            await _client.CreateSpriteAsync(opts, name, ct).ConfigureAwait(false);
            await ApplyNetworkPolicyAsync(opts, name, spec.Network, ct).ConfigureAwait(false);

            var sandbox = new SpritesSandbox(
                name,
                spec,
                opts,
                _client,
                _webSocketFactory,
                syncMounts,
                () => _activeSandboxes.TryRemove(name, out _),
                _log);

            await sandbox.PrepareFilesystemAsync(ct).ConfigureAwait(false);
            _activeSandboxes[name] = activeEntry;
            SandboxLiveCounter.Increment();

            _log.LogInformation("Created sprites sandbox {Name}", name);
            return sandbox;
        }
        catch
        {
            _activeSandboxes.TryRemove(name, out _);
            try { await _client.DeleteSpriteAsync(opts, name, ct).ConfigureAwait(false); }
            catch (Exception deleteEx) when (deleteEx is not OperationCanceledException)
            {
                _log.LogWarning(deleteEx, "Failed to delete sprites sandbox {Name} after create failure", name);
            }
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
        if (!IsValidManagedName(name))
            throw new ArgumentException($"Sprites sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));

        var opts = ReadValidatedOptions();
        await _client.DeleteSpriteAsync(opts, name, ct).ConfigureAwait(false);
        _activeSandboxes.TryRemove(name, out _);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() => [];

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
        return opts;
    }

    private static string GenerateSandboxName(string prefix)
    {
        var normalizedPrefix = prefix.EndsWith("-", StringComparison.Ordinal) ? prefix : prefix + "-";
        return normalizedPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static bool IsValidManagedName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.StartsWith(DefaultNamePrefix, StringComparison.Ordinal)
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsValidNamePrefix(string prefix) =>
        prefix.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static IReadOnlyList<SpritesMountSync> ValidateAndPlanMounts(SandboxSpec spec)
    {
        var result = new List<SpritesMountSync>();
        foreach (var mount in spec.Mounts)
        {
            if (!mount.SandboxPath.StartsWith("/", StringComparison.Ordinal))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");

            if (mount.Tmpfs)
            {
                result.Add(new SpritesMountSync(mount.SandboxPath, HostPath: null, ReadOnly: false, IsTmpfsDirectory: true));
                continue;
            }

            if (IsCredentialPath(mount.SandboxPath))
            {
                throw new NotSupportedException(
                    "sprites.dev does not expose tmpfs mounts; refusing credential host-file mount " +
                    $"{mount.SandboxPath} because it would persist on the sprite ext4 filesystem. " +
                    "Use credential environment variables for sprites-backed sandboxes.");
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

            result.Add(new SpritesMountSync(mount.SandboxPath, hostPath, mount.ReadOnly, IsTmpfsDirectory: false));
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
        if (!string.IsNullOrWhiteSpace(network.ProfileName) &&
            opts.NetworkProfiles.TryGetValue(network.ProfileName, out var profileHosts))
        {
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

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId);
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

internal sealed class SpritesSandbox : ISandbox
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly SandboxSpec _spec;
    private readonly SpritesSandboxOptions _opts;
    private readonly SpritesApiClient _client;
    private readonly ISpritesWebSocketFactory _webSocketFactory;
    private readonly IReadOnlyList<SpritesMountSync> _mounts;
    private readonly Action _onDisposed;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, byte> _activeSessions = new();
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private bool _disposing;
    private bool _disposed;

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

    internal async Task PrepareFilesystemAsync(CancellationToken ct)
    {
        foreach (var mount in _mounts)
        {
            if (mount.IsTmpfsDirectory)
            {
                await ExecRawAsync(new SandboxExec
                {
                    Argv = ["mkdir", "-p", mount.SandboxPath],
                    WorkingDirectory = "/",
                }, syncWritableMounts: false, allowDuringDispose: false, ct: ct).ConfigureAwait(false);
                continue;
            }

            if (mount.HostPath is null)
                continue;

            if (Directory.Exists(mount.HostPath))
                await UploadDirectoryAsync(mount.HostPath, mount.SandboxPath, ct).ConfigureAwait(false);
            else
                await UploadFileAsync(mount.HostPath, mount.SandboxPath, ct).ConfigureAwait(false);

            if (mount.ReadOnly)
            {
                await ExecRawAsync(new SandboxExec
                {
                    Argv = ["chmod", "-R", "a-w", mount.SandboxPath],
                    WorkingDirectory = "/",
                }, syncWritableMounts: false, allowDuringDispose: false, ct: ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        await _execGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ExecRawAsync(exec, syncWritableMounts: true, allowDuringDispose: false, ct: ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        if (_disposed || (_disposing && !allowDuringDispose))
            throw new ObjectDisposedException(nameof(SpritesSandbox));
        if (exec.Argv.Count == 0)
            throw new ArgumentException("Argv must be non-empty", nameof(exec));
        if (WouldPersistCredentialFile(exec))
        {
            throw new NotSupportedException(
                "sprites.dev does not expose tmpfs credential storage; refusing to write credential file material " +
                $"under {SandboxConventions.CredentialsDir}. Use credential environment variables for sprites-backed sandboxes.");
        }

        int? sessionId = null;
        await using var webSocket = _webSocketFactory.Create();
        try
        {
            await webSocket.ConnectAsync(BuildExecWebSocketUri(exec), _opts.Token!, ct).ConfigureAwait(false);
            var stdout = new LimitedOutputCollector(exec.MaxStdoutBytes, exec.StdoutChunkCallback);
            var stderr = new LimitedOutputCollector(exec.MaxStderrBytes, exec.StderrChunkCallback);
            var exitCode = await ReadExecUntilExitAsync(
                webSocket,
                exec,
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
        catch (OperationCanceledException)
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
        LimitedOutputCollector stdout,
        LimitedOutputCollector stderr,
        Action<int> onSessionInfo,
        CancellationToken ct)
    {
        var sentStdin = false;
        int? exitCode = null;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (webSocket.State == WebSocketState.Open && exitCode is null)
            {
                var message = await ReceiveMessageAsync(webSocket, buffer, ct).ConfigureAwait(false);
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
                                    await SendStdinAsync(webSocket, exec.Stdin, ct).ConfigureAwait(false);
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

                if ((stdout.LimitExceeded || stderr.LimitExceeded) && exec.KillOnOutputLimit)
                    await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return exitCode;
    }

    private static async Task<ReceivedWebSocketMessage?> ReceiveMessageAsync(
        ISpritesWebSocket webSocket,
        byte[] buffer,
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

    private Uri BuildExecWebSocketUri(SandboxExec exec)
    {
        var baseUri = new Uri(_opts.ApiBaseUrl);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
            Path = CombinePath(baseUri.AbsolutePath, $"v1/sprites/{Uri.EscapeDataString(Id)}/exec"),
        };
        if (builder.Port == 80 || builder.Port == 443)
            builder.Port = -1;

        var query = new List<KeyValuePair<string, string>>();
        foreach (var arg in exec.Argv)
            query.Add(new KeyValuePair<string, string>("cmd", arg));

        var workingDirectory = exec.WorkingDirectory ?? _spec.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            query.Add(new KeyValuePair<string, string>("dir", workingDirectory));

        foreach (var (key, value) in BuildEffectiveEnvironment(exec))
            query.Add(new KeyValuePair<string, string>("env", $"{key}={value}"));

        builder.Query = string.Join('&', query.Select(q =>
            $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        return builder.Uri;
    }

    private Dictionary<string, string> BuildEffectiveEnvironment(SandboxExec exec)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = "/root",
        };
        foreach (var (key, value) in _spec.Environment)
            env[key] = value;
        if (exec.ExtraEnvironment is not null)
        {
            foreach (var (key, value) in exec.ExtraEnvironment)
                env[key] = value;
        }
        return env;
    }

    private static bool WouldPersistCredentialFile(SandboxExec exec)
    {
        if (exec.Stdin is null)
            return false;

        return exec.Argv.Any(arg =>
            arg.Equals(SandboxConventions.CredentialsDir, StringComparison.Ordinal) ||
            arg.StartsWith(SandboxConventions.CredentialsDir + "/", StringComparison.Ordinal));
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
                "set -eu; rm -rf \"$1\"; mkdir -p \"$1\"; base64 -d | tar -xzf - -C \"$1\"",
                "_",
                sandboxPath,
            ],
            WorkingDirectory = "/",
            Stdin = archive,
        }, syncWritableMounts: false, allowDuringDispose: false, ct: ct).ConfigureAwait(false);
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
        }, syncWritableMounts: false, allowDuringDispose: false, ct: ct).ConfigureAwait(false);
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
                "set -eu; test -d \"$1\"; tar -czf - -C \"$1\" . | base64 -w0",
                "_",
                sandboxPath,
            ],
            WorkingDirectory = "/",
            MaxStderrBytes = 64 * 1024,
        }, syncWritableMounts: false, allowDuringDispose: allowDuringDispose, ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to archive sprite directory {Id}:{sandboxPath}: {result.Stderr}");

        ReplaceHostDirectoryFromArchive(hostPath, result.Stdout.Trim());
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
            MaxStderrBytes = 64 * 1024,
        }, syncWritableMounts: false, allowDuringDispose: allowDuringDispose, ct: ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to read sprite file {Id}:{sandboxPath}: {result.Stderr}");

        var bytes = Convert.FromBase64String(result.Stdout.Trim());
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

    private static void ReplaceHostDirectoryFromArchive(string hostPath, string archiveBase64)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(hostPath))
            ?? throw new InvalidOperationException($"Unable to determine parent directory for {hostPath}");
        Directory.CreateDirectory(parent);

        var tempPath = Path.Combine(parent, $".codeybox-sprites-sync-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(parent, $".codeybox-sprites-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        try
        {
            var bytes = Convert.FromBase64String(archiveBase64);
            using (var input = new MemoryStream(bytes))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, tempPath, overwriteFiles: true);
            }

            var hadExisting = Directory.Exists(hostPath);
            if (hadExisting)
                Directory.Move(hostPath, backupPath);
            Directory.Move(tempPath, hostPath);
            if (hadExisting)
                Directory.Delete(backupPath, recursive: true);
        }
        catch
        {
            if (Directory.Exists(hostPath))
                Directory.Delete(hostPath, recursive: true);
            if (Directory.Exists(backupPath))
                Directory.Move(backupPath, hostPath);
            throw;
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
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
        if (_disposed || _disposing)
            return;
        _disposing = true;
        SandboxLiveCounter.Decrement();
        _onDisposed();

        await KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _execGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
            _disposed = true;
            _disposing = false;
        }
    }

    private static string CombinePath(string basePath, string relative)
    {
        var prefix = string.IsNullOrWhiteSpace(basePath) || basePath == "/"
            ? ""
            : basePath.TrimEnd('/');
        return $"{prefix}/{relative.TrimStart('/')}";
    }

    private readonly record struct ReceivedWebSocketMessage(WebSocketMessageType MessageType, byte[] Payload);
}

internal sealed record SpritesMountSync(
    string SandboxPath,
    string? HostPath,
    bool ReadOnly,
    bool IsTmpfsDirectory);

internal sealed class LimitedOutputCollector
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly int? _limit;
    private readonly Action<string>? _callback;
    private readonly MemoryStream _buffer = new();

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
        if (_callback is not null)
            _callback(Utf8.GetString(bytes[..allowed]));
    }

    public override string ToString() => Utf8.GetString(_buffer.ToArray());
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
