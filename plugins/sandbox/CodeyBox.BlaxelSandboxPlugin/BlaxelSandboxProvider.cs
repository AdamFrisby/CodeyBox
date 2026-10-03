using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BlaxelPlugin;

/// <summary>
/// Blaxel perpetual-sandbox provider plugin (<c>kind: blaxel</c>).
///
/// <para>Each work item gets a fresh hosted microVM created over the Blaxel
/// control plane (<c>POST /sandboxes</c>), driven through its data-plane
/// process API (<c>POST /process</c> et al.), and deleted at teardown
/// (<c>DELETE /sandboxes/{name}</c>). Idle sandboxes fall into Blaxel's
/// automatic scale-to-zero standby with memory, processes, and filesystem
/// preserved, and wake on the next request in milliseconds — so suspend is
/// "settle into standby" and resume is "wake and prove the guest answers"
/// with a post-resume exec probe. The provider declares suspend-resume only
/// because that resume probe runs on every resume; a guest that does not
/// answer fails the resume as infrastructure instead of serving broken work.
/// It deliberately does NOT declare baseline bake, disk guard, cache seeding,
/// or port publishing: Blaxel offers related platform features (templates,
/// volumes, preview URLs), but this provider does not implement them, and an
/// aspirational capability would become a runtime failure deep in a phase.
/// Network profiles are refused at the sink (hosted provider, host-owned
/// <c>NotEnforced</c> classification); any deployment requiring enforced
/// egress refuses this kind with a reason.</para>
/// </summary>
[CodeyBoxPlugin(
    id: BlaxelSandboxOptions.PluginId,
    displayName: "CodeyBox: Blaxel perpetual sandboxes",
    minHostApiVersion: "1.0")]
public sealed class BlaxelSandboxProvider : ISandboxProvider, ISuspendingSandboxProvider,
    IActiveSandboxProvider, IPluginInitializer
{
    internal const string CredentialMountPath = "/run/codeybox/creds";

    private readonly Func<BlaxelSandboxOptions> _readOptions;
    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly BlaxelControlPlaneClient _control;
    private readonly BlaxelSandboxApiClient _dataPlane;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _active = new(StringComparer.Ordinal);

    public BlaxelSandboxProvider(
        Func<BlaxelSandboxOptions> readOptions,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger log)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _control = new BlaxelControlPlaneClient(_http);
        _dataPlane = new BlaxelSandboxApiClient(_http);
    }

    public string Name => BlaxelSandboxOptions.ProviderKind;

    public bool MightOwnSandbox(ManagedSandboxInfo managed) =>
        string.Equals(managed.LifecycleProviderId, Name, StringComparison.Ordinal);

    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honestly declared capabilities: suspend/resume (standby preserves
    /// memory, processes, and filesystem; every resume is exec-probed) and
    /// teardown (delete, plus stop-and-preserve into standby). Nothing else:
    /// no baseline bake, disk guard, cache seeding, or port publishing.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities =>
        [SandboxCapabilities.SuspendResume, SandboxCapabilities.Teardown];

    internal int ActiveSandboxCount => _active.Count;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.PluginId, BlaxelSandboxOptions.PluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Blaxel plugin initialized with unexpected plugin id '{context.PluginId}'.");
        }

        var options = _readOptions();
        ValidateBaseUrl(options.ApiBaseUrl, options.AllowUnsafeHttp);
        _log.LogInformation(
            "Blaxel sandbox provider initialised (kind '{Kind}', image '{Image}', memory {MemoryMb} MiB).",
            Name, options.Image, options.MemoryMb);
        return Task.CompletedTask;
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = _readOptions();
        var credentials = ResolveCredentials(opts);
        ValidateOptions(opts);
        ValidateSpec(spec);

        var environment = ValidateEnvironment(spec.Environment);
        ValidateMountsForCreate(opts, spec);
        var name = BuildSandboxName(opts.NamePrefix);
        var createRequest = new BlaxelSandboxCreateRequest(
            new BlaxelMetadata(name, new Dictionary<string, string>
            {
                ["codeybox-managed"] = "true",
                ["codeybox-provider"] = BlaxelSandboxOptions.ProviderKind,
            }),
            new BlaxelSandboxSpecBody(
                new BlaxelRuntimeSpec(
                    string.IsNullOrWhiteSpace(opts.Image) ? "blaxel/base-image:latest" : opts.Image,
                    opts.MemoryMb,
                    environment.Count == 0 ? null : environment.Select(kvp => new BlaxelEnvVar(kvp.Key, kvp.Value)).ToList(),
                    string.IsNullOrWhiteSpace(opts.Ttl) ? null : opts.Ttl),
                string.IsNullOrWhiteSpace(opts.Region) ? null : opts.Region));

        BlaxelSandboxView created;
        try
        {
            created = await _control.CreateSandboxAsync(opts.ApiBaseUrl, credentials, createRequest, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex)
        {
            throw ToDeferred(ex, "create-sandbox");
        }

        var sandboxName = string.IsNullOrWhiteSpace(created.Name) ? name : created.Name;
        BlaxelSandbox sandbox;
        try
        {
            var sandboxUrl = ResolveSandboxUrl(created, credentials.Workspace, opts.AllowUnsafeHttp);
            sandbox = new BlaxelSandbox(
                sandboxName,
                sandboxUrl,
                _control,
                _dataPlane,
                _readOptions,
                () => ResolveCredentials(_readOptions()),
                spec,
                _timeProvider,
                _log,
                untrackName => _active.TryRemove(untrackName, out _));
            if (!_active.TryAdd(sandboxName, new ActiveSandboxEntry(spec.TimingWorkItemId, sandbox)))
            {
                throw new InvalidOperationException($"Blaxel sandbox name collision: '{sandboxName}'.");
            }
        }
        catch (BlaxelApiException ex)
        {
            await DeleteBestEffortAsync(opts, credentials, sandboxName).ConfigureAwait(false);
            throw ToDeferred(ex, "resolve-sandbox-url");
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            await DeleteBestEffortAsync(opts, credentials, sandboxName).ConfigureAwait(false);
            throw;
        }

        try
        {
            await WaitForUsableAsync(opts, credentials, sandbox, ct).ConfigureAwait(false);
            var writable = await StageMountsAsync(opts, sandbox, spec, ct).ConfigureAwait(false);
            sandbox.SetWritableMounts(writable);
            return sandbox;
        }
        catch
        {
            await sandbox.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _active.Values
            .Where(entry => entry.WorkItemId is not null)
            .Select(entry => (entry.WorkItemId!.Value, (IShutdownTeardownSandbox)entry.Sandbox))
            .ToList();

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = _readOptions();
        var credentials = ResolveCredentials(opts);
        var managed = new List<ManagedSandboxInfo>();
        string? cursor = null;

        for (var page = 0; page < opts.MaxListPages; page++)
        {
            BlaxelSandboxListPage list;
            try
            {
                list = await _control.ListSandboxesAsync(opts.ApiBaseUrl, credentials, limit: 200, cursor, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (BlaxelApiException ex)
            {
                throw ToDeferred(ex, "list-sandboxes");
            }

            foreach (var view in list.Sandboxes)
            {
                if (IsManaged(view, opts.NamePrefix))
                {
                    managed.Add(new ManagedSandboxInfo(
                        view.Name,
                        CreatedAt: null,
                        DiskBytes: null,
                        IsTrackedActive: _active.ContainsKey(view.Name),
                        IsSuspendLifecycleOrFrozen: string.Equals(view.State, "STANDBY", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(view.Status, "ARCHIVED", StringComparison.OrdinalIgnoreCase),
                        LifecycleProviderId: BlaxelSandboxOptions.ProviderKind));
                }
            }

            if (!list.HasMore || string.IsNullOrWhiteSpace(list.Cursor))
            {
                break;
            }

            cursor = list.Cursor;
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
            await _control.DeleteSandboxAsync(opts.ApiBaseUrl, credentials, name, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
        {
        }
        catch (BlaxelApiException ex)
        {
            throw ToDeferred(ex, "delete-sandbox");
        }
    }

    /// <summary>
    /// Best-effort resume of a previously suspended (standby) sandbox by name.
    /// Wakes the sandbox with a data-plane touch, waits for <c>RUNNING</c>,
    /// refreshes the endpoint, then runs an exec probe: only a guest that
    /// genuinely answers completes the resume. "Not found" and "already
    /// running" are non-fatal so the startup handler can clear stale
    /// bookkeeping.
    /// </summary>
    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        var credentials = ResolveCredentials(opts);

        BlaxelSandboxView view;
        try
        {
            view = await _control.GetSandboxAsync(opts.ApiBaseUrl, credentials, name, opts.ApiTimeout, ct).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
        {
            return;
        }
        catch (BlaxelApiException ex)
        {
            throw ToDeferred(ex, "get-sandbox");
        }

        if (string.Equals(view.State, "RUNNING", StringComparison.OrdinalIgnoreCase)
            && string.Equals(view.Status, "DEPLOYED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string sandboxUrl;
        try
        {
            sandboxUrl = ResolveSandboxUrl(view, credentials.Workspace, opts.AllowUnsafeHttp);
        }
        catch (BlaxelApiException ex)
        {
            throw ToDeferred(ex, "resume-wake");
        }

        try
        {
            await _dataPlane.GetProcessAsync(sandboxUrl, credentials, "codeybox-resume-touch", opts.ApiTimeout, ct, BlaxelSandboxApiClient.MaxStartResponseBytes).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
        {
            // Expected: the touch process does not exist. Any data-plane
            // request wakes a standby sandbox; the 404 itself proves it answered.
        }
        catch (BlaxelApiException ex)
        {
            throw ToDeferred(ex, "resume-wake");
        }

        var deadline = _timeProvider.GetUtcNow() + opts.WaitForRunningTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            BlaxelSandboxView current;
            try
            {
                current = await _control.GetSandboxAsync(opts.ApiBaseUrl, credentials, name, opts.ApiTimeout, ct).ConfigureAwait(false);
            }
            catch (BlaxelApiException ex)
            {
                throw ToDeferred(ex, "resume-wait");
            }

            if (string.Equals(current.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    sandboxUrl = ResolveSandboxUrl(current, credentials.Workspace, opts.AllowUnsafeHttp);
                }
                catch (BlaxelApiException ex)
                {
                    throw ToDeferred(ex, "resume-wait");
                }

                break;
            }

            if (string.Equals(current.Status, "FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.Status, "TERMINATED", StringComparison.OrdinalIgnoreCase))
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "terminal-state", $"sandbox {name} entered '{current.Status}' while resuming"),
                    "resume-wait");
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "timeout", $"sandbox {name} did not resume in time"),
                    "resume-wait");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        await ProveGuestAnswersAsync(opts, credentials, name, sandboxUrl, ct).ConfigureAwait(false);
    }

    private async Task ProveGuestAnswersAsync(
        BlaxelSandboxOptions opts, BlaxelCredentials credentials, string name, string sandboxUrl, CancellationToken ct)
    {
        var nonce = $"resume-probe-{Guid.NewGuid():N}";
        var probeCap = BlaxelSandboxApiClient.ResponseCap(64 * 1024, 64 * 1024);
        BlaxelProcessView started;
        try
        {
            started = await _dataPlane.StartProcessAsync(
                sandboxUrl,
                credentials,
                new BlaxelProcessRequest(
                    $"printf %s {BlaxelShellCommand.Quote(nonce)}",
                    "/",
                    false,
                    $"codeybox-resume-probe-{Guid.NewGuid():N}",
                    new Dictionary<string, string>()),
                opts.ApiTimeout,
                ct).ConfigureAwait(false);
        }
        catch (BlaxelApiException ex)
        {
            throw ToDeferred(ex, "resume-probe");
        }

        var processId = !string.IsNullOrWhiteSpace(started.Pid) ? started.Pid : started.Name ?? string.Empty;
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(2);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "timeout", $"sandbox {name} resume probe exceeded its budget"),
                    "resume-probe");
            }

            BlaxelProcessView view;
            try
            {
                view = await _dataPlane.GetProcessAsync(sandboxUrl, credentials, processId, opts.ApiTimeout, ct, probeCap).ConfigureAwait(false);
            }
            catch (BlaxelApiException ex)
            {
                throw ToDeferred(ex, "resume-probe");
            }

            if (!view.IsTerminal)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }

                continue;
            }

            string output;
            try
            {
                var logs = await _dataPlane.GetProcessLogsAsync(sandboxUrl, credentials, processId, opts.ApiTimeout, ct, probeCap).ConfigureAwait(false);
                output = logs.Stdout ?? view.Stdout ?? string.Empty;
            }
            catch (BlaxelApiException ex) when (string.Equals(ex.ErrorClass, "not-found", StringComparison.Ordinal))
            {
                output = view.Stdout ?? string.Empty;
            }
            catch (BlaxelApiException ex)
            {
                throw ToDeferred(ex, "resume-probe");
            }

            if (!string.Equals(view.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || (view.ExitCode ?? 1) != 0
                || !string.Equals(output, nonce, StringComparison.Ordinal))
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "resume-unverified", $"sandbox {name} resumed but its guest did not answer the probe"),
                    "resume-probe");
            }

            return;
        }
    }

    internal static void ValidateBaseUrl(string baseUrl, bool allowUnsafeHttp)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Blaxel API base URL '{baseUrl}' is not absolute; configure ApiBaseUrl with an absolute https URL.");
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            && !IsCleartextHttpPermitted(uri, allowUnsafeHttp))
        {
            throw new InvalidOperationException(
                $"Blaxel API base URL '{baseUrl}' must use https (AllowUnsafeHttp permits http only for loopback test URLs, never for remote hosts).");
        }
    }

    /// <summary>
    /// Cleartext http is permitted only for loopback test URLs under the
    /// dev-only <c>AllowUnsafeHttp</c> opt-in: credentials ride every request,
    /// so one operator edit must never send them cleartext to a remote host.
    /// </summary>
    internal static bool IsCleartextHttpPermitted(Uri uri, bool allowUnsafeHttp) =>
        allowUnsafeHttp
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback;

    /// <summary>
    /// The sandbox data-plane endpoint arrives in the Blaxel API response field
    /// <c>metadata.url</c> — service output, untrusted input to an
    /// outbound-request sink carrying workload code and secret-bearing env
    /// values. Only an absolute https URI (http solely when
    /// <see cref="IsCleartextHttpPermitted"/> holds) is accepted; anything
    /// else fails closed instead of redirecting requests to an arbitrary target.
    /// </summary>
    internal static string ValidateSandboxUrl(string raw, bool allowUnsafeHttp)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new BlaxelApiException(
                null, "malformed-response",
                $"service returned a non-absolute or non-http(s) sandbox URL: '{raw?.Trim()}'");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsCleartextHttpPermitted(uri, allowUnsafeHttp))
        {
            throw new BlaxelApiException(
                null, "malformed-response",
                "service returned a cleartext http sandbox URL that is not permitted; " +
                "refusing to send credentials over cleartext. " +
                "AllowUnsafeHttp permits http only for loopback test URLs, never for remote hosts.");
        }

        return raw.Trim();
    }

    internal static void ValidateOptions(BlaxelSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ValidateBaseUrl(opts.ApiBaseUrl, opts.AllowUnsafeHttp);

        if (string.IsNullOrWhiteSpace(opts.NamePrefix)
            || opts.NamePrefix.Length > 26
            || !char.IsLetter(opts.NamePrefix[0])
            || opts.NamePrefix.Any(static c => !char.IsLower(c) && !char.IsDigit(c) && c != '-'))
        {
            throw new InvalidOperationException(
                $"Blaxel NamePrefix '{opts.NamePrefix}' must be letter-led, lowercase [a-z0-9-], and at most 26 characters (sandbox names cap at 49).");
        }

        if (opts.MemoryMb < 512 || opts.MemoryMb > 65536)
        {
            throw new InvalidOperationException(
                $"Blaxel MemoryMb '{opts.MemoryMb}' must be within 512..65536 MB.");
        }

        if (!string.IsNullOrWhiteSpace(opts.Region) && opts.Region.Length > 64)
        {
            throw new InvalidOperationException("Blaxel Region must be at most 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(opts.Image) || opts.Image.Length > 256)
        {
            throw new InvalidOperationException("Blaxel Image must be a non-empty reference of at most 256 characters.");
        }
    }

    internal static void ValidateSpec(SandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
        {
            throw new NotSupportedException(
                "The blaxel provider does not support graphical sandboxes: microVMs expose no display or VNC server.");
        }

        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            throw new InvalidOperationException(
                $"Sandbox provider kind '{BlaxelSandboxOptions.ProviderKind}' cannot serve network profile " +
                $"'{spec.Network.ProfileName.Trim()}': the kind is classified 'NotEnforced' (no host-enforced egress filtering). " +
                "A 'NotEnforced' provider may only serve sandboxes with no named network profile.");
        }
    }

    /// <summary>
    /// Fails closed before any billable VM exists: guest paths must be absolute,
    /// credential tmpfs mounts are refused, non-secret tmpfs needs the explicit
    /// downgrade, and host-path sources must exist (re-checked at stage time
    /// against TOCTOU races).
    /// </summary>
    internal static void ValidateMountsForCreate(BlaxelSandboxOptions opts, SandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(spec);
        foreach (var mount in spec.Mounts)
        {
            BlaxelGuestPath.ValidateAbsolute(mount.SandboxPath);

            if (mount.Tmpfs)
            {
                if (string.Equals(mount.SandboxPath, CredentialMountPath, StringComparison.Ordinal)
                    || mount.SandboxPath.StartsWith(CredentialMountPath + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Blaxel sandboxes snapshot the whole filesystem: credential mount '{mount.SandboxPath}' would persist " +
                        "in standby snapshots. Pass credentials as environment variables instead.");
                }

                if (!opts.AllowPersistentTmpfsDowngrade)
                {
                    throw new InvalidOperationException(
                        $"Blaxel sandboxes have no tmpfs: mount '{mount.SandboxPath}' needs AllowPersistentTmpfsDowngrade " +
                        "to downgrade to a persistent guest directory.");
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(mount.HostPath))
            {
                var hostRoot = Path.GetFullPath(mount.HostPath);
                if (!File.Exists(hostRoot) && !Directory.Exists(hostRoot))
                {
                    throw new SandboxMountSourceMissingException(
                        mount.HostPath,
                        $"Blaxel mount source '{mount.HostPath}' does not exist at stage time.");
                }
            }
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

    internal BlaxelCredentials ResolveCredentials(BlaxelSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var apiKey = opts.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable(opts.ApiKeyEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Blaxel API key environment variable '{opts.ApiKeyEnvironmentVariable}' is not set. " +
                "Set it in the orchestrator process environment (never in a configuration file).");
        }

        var workspace = opts.Workspace;
        if (string.IsNullOrWhiteSpace(workspace))
        {
            workspace = Environment.GetEnvironmentVariable(opts.WorkspaceEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new InvalidOperationException(
                $"Blaxel workspace environment variable '{opts.WorkspaceEnvironmentVariable}' is not set. " +
                "Set it in the orchestrator process environment (never in a configuration file).");
        }

        return new BlaxelCredentials(apiKey, workspace);
    }

    internal static string ResolveSandboxUrl(BlaxelSandboxView view, string workspace, bool allowUnsafeHttp = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!string.IsNullOrWhiteSpace(view.Url))
        {
            return ValidateSandboxUrl(view.Url, allowUnsafeHttp);
        }

        // Older records may predate the auto-generated endpoint: reconstruct
        // the documented sandbox host pattern from name, workspace, and region.
        if (string.IsNullOrWhiteSpace(view.Name)
            || string.IsNullOrWhiteSpace(workspace)
            || string.IsNullOrWhiteSpace(view.Region))
        {
            throw new InvalidOperationException(
                $"Blaxel sandbox '{view.Name}' reports no endpoint URL and its region is unknown; cannot address its data plane.");
        }

        return ValidateSandboxUrl($"https://sbx-{view.Name}-{workspace}.{view.Region}.bl.run", allowUnsafeHttp);
    }

    private static string BuildSandboxName(string prefix)
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        var suffix = Convert.ToHexString(random).ToLowerInvariant();
        return $"{prefix.TrimEnd('-').ToLowerInvariant()}-{suffix}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000}";
    }

    private async Task WaitForUsableAsync(
        BlaxelSandboxOptions opts, BlaxelCredentials credentials, BlaxelSandbox sandbox, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + opts.WaitForRunningTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "timeout", $"sandbox {sandbox.Name} did not become usable in time"),
                    "wait-for-usable");
            }

            BlaxelSandboxView view;
            try
            {
                view = await _control.GetSandboxAsync(opts.ApiBaseUrl, credentials, sandbox.Name, BlaxelControlPlaneClient.WaitCallTimeout(opts.ApiTimeout), ct).ConfigureAwait(false);
            }
            catch (BlaxelApiException ex)
            {
                throw ToDeferred(ex, "wait-for-usable");
            }

            if (string.Equals(view.State, "RUNNING", StringComparison.OrdinalIgnoreCase)
                && string.Equals(view.Status, "DEPLOYED", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(view.Url))
                {
                    try
                    {
                        sandbox.RefreshSandboxUrl(view.Url);
                    }
                    catch (BlaxelApiException ex)
                    {
                        throw ToDeferred(ex, "wait-for-usable");
                    }
                }

                return;
            }

            if (string.Equals(view.Status, "FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(view.Status, "TERMINATED", StringComparison.OrdinalIgnoreCase))
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "terminal-state", $"sandbox {sandbox.Name} entered '{view.Status}' while starting"),
                    "wait-for-usable");
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
        BlaxelSandboxOptions opts, BlaxelSandbox sandbox, SandboxSpec spec, CancellationToken ct)
    {
        var writable = new List<WritableMountSync>();
        long totalBytes = 0;
        var totalFiles = 0;

        foreach (var mount in spec.Mounts)
        {
            ct.ThrowIfCancellationRequested();
            BlaxelGuestPath.ValidateAbsolute(mount.SandboxPath);

            if (mount.Tmpfs)
            {
                if (string.Equals(mount.SandboxPath, CredentialMountPath, StringComparison.Ordinal)
                    || mount.SandboxPath.StartsWith(CredentialMountPath + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Blaxel sandboxes snapshot the whole filesystem: credential mount '{mount.SandboxPath}' would persist " +
                        "in standby snapshots. Pass credentials as environment variables instead.");
                }

                if (!opts.AllowPersistentTmpfsDowngrade)
                {
                    throw new InvalidOperationException(
                        $"Blaxel sandboxes have no tmpfs: mount '{mount.SandboxPath}' needs AllowPersistentTmpfsDowngrade " +
                        "to downgrade to a persistent guest directory.");
                }

                _log.LogWarning(
                    "Blaxel sandbox {SandboxName}: tmpfs mount {Path} downgraded to a persistent guest directory.",
                    sandbox.Name, mount.SandboxPath);
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
                    $"Blaxel mount source '{mount.HostPath}' does not exist at stage time.");
            }

            var staged = await StageHostPathAsync(opts, sandbox, mount.SandboxPath, hostRoot, ct).ConfigureAwait(false);
            totalBytes += staged.Bytes;
            totalFiles += staged.Files;
            if (totalBytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Blaxel mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            if (totalFiles > opts.MaxStageFileCount)
            {
                throw new InvalidOperationException(
                    $"Blaxel mount staging exceeds {opts.MaxStageFileCount} files in total; refusing to stage.");
            }

            if (!mount.ReadOnly)
            {
                writable.Add(new WritableMountSync(mount.SandboxPath, hostRoot));
            }
        }

        return writable;
    }

    private async Task<(long Bytes, int Files)> StageHostPathAsync(
        BlaxelSandboxOptions opts,
        BlaxelSandbox sandbox,
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
                        $"Blaxel mount staging exceeds {opts.MaxStageFileCount} files; refusing to stage.");
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
                    $"Blaxel mount file '{relative}' is {fileInfo.Length} bytes (limit {opts.MaxStageFileBytes}); refusing to stage.");
            }

            var guestPath = guestRoot.TrimEnd('/') + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            BlaxelGuestPath.ValidateAbsolute(guestPath);

            string content;
            try
            {
                content = await File.ReadAllTextAsync(hostFull, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Blaxel mount file '{relative}' cannot be read: {ex.GetType().Name}.", ex);
            }

            bytes += Encoding.UTF8.GetByteCount(content);
            if (bytes > opts.MaxStageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Blaxel mount staging exceeds {opts.MaxStageTotalBytes} bytes in total; refusing to stage.");
            }

            try
            {
                await sandbox.WriteFileAsync(guestPath, content, ct).ConfigureAwait(false);
            }
            catch (SandboxExecutionUnavailableException ex)
            {
                throw ToDeferred(
                    new BlaxelApiException(null, "stage-mount", $"staging '{relative}' failed with exit {ex.ExitCode}"),
                    "stage-mount");
            }
        }

        return (bytes, files.Count);
    }

    private static bool IsManaged(BlaxelSandboxView view, string namePrefix)
    {
        if (view.Metadata?.Labels is not null
            && view.Metadata.Labels.TryGetValue("codeybox-managed", out var managed)
            && string.Equals(managed, "true", StringComparison.Ordinal)
            && view.Metadata.Labels.TryGetValue("codeybox-provider", out var provider)
            && string.Equals(provider, BlaxelSandboxOptions.ProviderKind, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(view.Name)
            && view.Name.StartsWith(namePrefix, StringComparison.Ordinal);
    }

    private async Task DeleteBestEffortAsync(BlaxelSandboxOptions opts, BlaxelCredentials credentials, string name)
    {
        try
        {
            await _control.DeleteSandboxAsync(opts.ApiBaseUrl, credentials, name, opts.ApiTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Blaxel sandbox {SandboxName}: best-effort delete after failed create failed.", name);
        }
    }

    private SandboxProvisioningDeferredException ToDeferred(BlaxelApiException ex, string operation)
    {
        var classification = BlaxelFailureClassification.Classify(ex.StatusCode, operation);
        return new SandboxProvisioningDeferredException(
            BlaxelSandboxOptions.ProviderKind,
            operation,
            ex.ErrorClass,
            TruncateDetail(ex.Detail),
            classification.RecheckIn,
            innerException: ex);
    }

    private static string TruncateDetail(string detail) =>
        detail.Length > 512 ? detail[..512] : detail;

    private sealed record ActiveSandboxEntry(WorkItemId? WorkItemId, BlaxelSandbox Sandbox);
}
