using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.BoxLiteSandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by BoxLite embedded microVMs served from a
/// local daemon. Contributes the <c>boxlite</c> provider kind through the
/// plugin trust model: the host owns egress classification, so this kind is
/// always <c>NotEnforced</c> — the daemon's guest-side network restriction is
/// defence in depth, not the host nftables egress guarantee, and no option or
/// return value can promote it. Deployments requiring enforced egress are
/// refused by placement before this provider is ever called.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.boxlite-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set).</para>
/// </summary>
[CodeyBoxPlugin(BoxLiteSandboxOptions.PluginId, "BoxLite sandbox provider")]
public sealed class BoxLiteSandboxProvider :
    ISandboxProvider,
    IPluginInitializer,
    IActiveSandboxProvider,
    IActiveSandboxProgressProvider,
    ISuspendingSandboxProvider,
    IBaselineImageResolver,
    IBaselineImageProvisioner,
    IDisposable
{
    public const string DefaultNamePrefix = "codeybox-";

    private static readonly TimeSpan MinimumOperatorFixRecheck = TimeSpan.FromMinutes(5);

    // Ceiling on the exit-marker file (a small ASCII exit code); the marker
    // path is provider-constructed but its bytes are guest-influenced output.
    private const long ExitMarkerMaxBytes = 256;

    private readonly Func<BoxLiteSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly ITimingStore? _timings;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly LazyCredentialClient<HttpClient> _clients;
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>; the section is re-read per call so hot reloads apply.</summary>
    public BoxLiteSandboxProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = name => Environment.GetEnvironmentVariable(name);
        _readOptions = () => BoxLiteSandboxOptions.FromConfiguration(_host?.ScopedConfig);
        _clients = new LazyCredentialClient<HttpClient>(
            () => TimeSpan.FromSeconds(ReadOptions().HttpTimeoutSeconds),
            http => http);
    }

    /// <summary>Test seam: full constructor injection.</summary>
    internal BoxLiteSandboxProvider(
        Func<BoxLiteSandboxOptions> readOptions,
        HttpClient httpClient,
        Func<string, string?> environment,
        TimeProvider clock,
        ITimingStore? timings,
        ILogger log)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timings = timings;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clients = new LazyCredentialClient<HttpClient>(
            () => TimeSpan.FromSeconds(_readOptions().HttpTimeoutSeconds),
            http => http,
            httpClient);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _host = context.Host;
        _log = context.Logger;
        var options = ReadOptions();
        _log.LogInformation(
            "BoxLite sandbox provider initialized (enabled={Enabled}, daemon={DaemonUrl})",
            options.Enabled, options.DaemonUrl);
        return Task.CompletedTask;
    }

    public string Name => BoxLiteSandboxOptions.ProviderKind;

    /// <summary>
    /// The strongest isolation this provider can honestly claim. BoxLite runs
    /// each sandbox as a hardware-isolated microVM with its own guest kernel —
    /// a real guest-boundary claim routed to workload-trust decisions. It says
    /// nothing about network-egress enforcement, which stays host-owned
    /// (<c>NotEnforced</c> for every plugin kind).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honest capability set: baseline bake (provision a VM, snapshot it,
    /// create later VMs from the snapshot), suspend/resume (pause/resume),
    /// and teardown (stop/preserve + delete). Not declared: disk-guard (the
    /// daemon reports no per-VM disk usage CodeyBox can enforce),
    /// cache-seeding (not implemented), port-publishing (the daemon's port
    /// forwarding needs an async lease handshake the synchronous
    /// <see cref="ISandboxPortPublisher"/> contract cannot express).
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities =>
    [
        SandboxCapabilities.BaselineBake,
        SandboxCapabilities.SuspendResume,
        SandboxCapabilities.Teardown,
    ];

    private BoxLiteSandboxOptions ReadOptions() => _readOptions();

    /// <summary>Shared HttpClient carrying the API token.</summary>
    private HttpClient Http => _clients.Get();

    private BoxLiteApiClient Api => new(Http);

    // ------------------------------------------------------------------
    // Provider lifecycle
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec = spec with { Environment = WithTimingEnvironment(spec.Environment, spec.TimingWorkItemId) };

        var opts = ReadValidatedOptions();
        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("boxlite sandbox provider does not support the graphical sandbox flavor.");

        var endpoint = ResolveEndpoint(opts);

        if (spec.RecoveryLease is { } lease)
            return await AdoptRetainedVmAsync(spec, opts, endpoint, lease, ct).ConfigureAwait(false);

        var name = GenerateSandboxName(opts.NamePrefix);
        var mounts = ValidateAndPlanMounts(spec);
        var workItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var timingPhase = spec.TimingPhase ?? "work";
        var created = false;

        try
        {
            var request = BuildCreateRequest(spec, opts, name, workItemId);
            await using var provisionTiming = await TimingScope.BeginAsync(
                _timings, workItemId, timingPhase, "boxlite.create", log: _log).ConfigureAwait(false);
            var createdDto = await Api.CreateVmAsync(endpoint, request, ct).ConfigureAwait(false);
            created = true;
            var vmId = createdDto.Id ?? name;

            var sandbox = new BoxLiteSandbox(
                name,
                spec,
                _readOptions,
                Api,
                () => ResolveEndpoint(ReadValidatedOptions()),
                mounts,
                MarkNoLongerActive,
                _clock,
                _log);

            _activeSandboxes[name] = new ActiveSandboxEntry(workItemId, sandbox);
            SandboxLiveCounter.Increment();

            await WaitForStateAsync(endpoint, name, "running", opts.ReadyTimeoutSeconds, opts, ct).ConfigureAwait(false);
            await sandbox.RunSetupCommandsAsync(opts.SetupCommands, ct).ConfigureAwait(false);
            await ApplyNetworkPolicyAsync(endpoint, name, spec.Network, ct).ConfigureAwait(false);
            await sandbox.PrepareFilesystemAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Created boxlite VM {Name} (id {VmId})", name, vmId);
            return sandbox;
        }
        catch (OperationCanceledException)
        {
            // A cancelled create can still have committed the VM daemon-side —
            // never leave it running on the host's resources.
            MarkNoLongerActive(name);
            if (created)
                await TryDeleteAfterCreateFailureAsync(endpoint, name).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            MarkNoLongerActive(name);
            if (created && !await TryDeleteAfterCreateFailureAsync(endpoint, name).ConfigureAwait(false))
            {
                throw ToDeferred(
                    opts,
                    new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "create-cleanup", "delete after failure did not prove removal"),
                    $"create failed and best-effort delete did not prove VM {name} was removed: {ex.Message}");
            }
            if (ex is BoxLiteApiException apiEx)
                throw ToDeferred(opts, apiEx, $"boxlite create failed for {name}");
            throw;
        }
    }

    /// <summary>
    /// Adoption of a retained stopped VM via <see cref="SandboxSpec.RecoveryLease"/>.
    /// Ownership is re-verified at the moment of action: provider id, name
    /// prefix, managed label, and the recovery-token hash label must all match.
    /// </summary>
    private async Task<ISandbox> AdoptRetainedVmAsync(
        SandboxSpec spec,
        BoxLiteSandboxOptions opts,
        BoxLiteEndpoint endpoint,
        SandboxRecoveryLease lease,
        CancellationToken ct)
    {
        if (!string.Equals(lease.ProviderId, BoxLiteSandboxOptions.ProviderKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recovery lease names provider '{lease.ProviderId}', not 'boxlite'; refusing adoption.");
        }
        if (!IsValidManagedName(lease.SandboxId, opts.NamePrefix))
        {
            throw new InvalidOperationException(
                $"Recovery lease names VM '{lease.SandboxId}' outside the managed boxlite namespace; refusing adoption.");
        }

        var vm = await Api.GetVmAsync(endpoint, lease.SandboxId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Retained boxlite VM '{lease.SandboxId}' no longer exists; refusing adoption.");

        var labels = vm.Labels ?? new Dictionary<string, string>();
        if (!labels.TryGetValue(BoxLiteSandboxOptions.ManagedLabelKey, out var managed)
            || !string.Equals(managed, BoxLiteSandboxOptions.ManagedLabelValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"BoxLite VM '{lease.SandboxId}' lacks the managed-ownership label; refusing adoption.");
        }
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Token)));
        if (!labels.TryGetValue(BoxLiteSandboxOptions.RecoveryTokenHashLabelKey, out var hash)
            || !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Recovery token does not match the retained boxlite VM '{lease.SandboxId}'; refusing adoption.");
        }

        try
        {
            await Api.TransitionVmAsync(endpoint, lease.SandboxId, "start", ct).ConfigureAwait(false);
        }
        catch (BoxLiteApiException ex) when (ex.Kind == BoxLiteFailureKind.Conflict)
        {
            // Already started/starting — adopt waits for the running state below.
        }
        await WaitForStateAsync(endpoint, lease.SandboxId, "running", opts.ReadyTimeoutSeconds, opts, ct).ConfigureAwait(false);

        var handle = new BoxLiteSandbox(
            lease.SandboxId,
            spec,
            _readOptions,
            Api,
            () => ResolveEndpoint(ReadValidatedOptions()),
            mounts: [],
            MarkNoLongerActive,
            _clock,
            _log);
        _activeSandboxes[lease.SandboxId] = new ActiveSandboxEntry(spec.TimingWorkItemId.GetValueOrDefault(), handle);
        SandboxLiveCounter.Increment();
        _log.LogInformation("Adopted retained boxlite VM {Name}", lease.SandboxId);
        return handle;
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var items = await Api.ListManagedVmsAsync(endpoint, opts.NamePrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        return items
            .Where(static i => i.Name is not null)
            .Select(static i => new ManagedSandboxInfo(
                i.Name!,
                null,
                null,
                IsTrackedActive: false,
                IsSuspendLifecycleOrFrozen: string.Equals(i.State, "paused", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i.State, "pausing", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (!IsValidManagedName(name, ReadValidatedOptions().NamePrefix))
            throw new InvalidOperationException($"Refusing to dispose unmanaged boxlite VM '{name}'.");
        var opts = ReadValidatedOptions();
        await Api.DeleteVmAsync(ResolveEndpoint(opts), name, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Suspend / resume (host shutdown + startup adoption)
    // ------------------------------------------------------------------

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("VM name must be non-empty.", nameof(name));
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(name.Trim(), opts.NamePrefix))
            throw new InvalidOperationException($"Refusing to resume unmanaged boxlite VM '{name}'.");
        var endpoint = ResolveEndpoint(opts);
        var vm = await Api.GetVmAsync(endpoint, name.Trim(), ct).ConfigureAwait(false);
        if (vm is null)
            return;
        if (string.Equals(vm.State, "running", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            await Api.TransitionVmAsync(endpoint, name.Trim(), "resume", ct).ConfigureAwait(false);
        }
        catch (BoxLiteApiException ex) when (ex.Kind == BoxLiteFailureKind.Conflict)
        {
            await Api.TransitionVmAsync(endpoint, name.Trim(), "start", ct).ConfigureAwait(false);
        }
        await WaitForStateAsync(endpoint, name.Trim(), "running", opts.TransitionTimeoutSeconds, opts, ct).ConfigureAwait(false);
    }

    public async Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName,
        string agentLogPath,
        Action<string>? logSink,
        TimeSpan? deadline,
        CancellationToken ct)
    {
        if (!IsValidManagedName(vmName, ReadValidatedOptions().NamePrefix))
            return null;
        if (!IsValidAgentLogPath(agentLogPath))
            return null;
        var endpoint = ResolveEndpoint(ReadValidatedOptions());
        var stopAt = deadline is { } d && d > TimeSpan.Zero ? _clock.GetUtcNow() + d : (DateTimeOffset?)null;
        var delivered = 0;
        while (!ct.IsCancellationRequested)
        {
            if (stopAt is { } stop && _clock.GetUtcNow() >= stop)
                return null;
            BoxLiteFileDto? exit;
            try
            {
                exit = await Api.ReadFileAsync(endpoint, vmName, agentLogPath + ".exit", ct).ConfigureAwait(false);
            }
            catch (BoxLiteApiException)
            {
                return null;
            }
            if (exit?.ContentBase64 is not null)
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(exit.ContentBase64);
                }
                catch (FormatException)
                {
                    return null;
                }
                if (bytes.Length > ExitMarkerMaxBytes)
                    return null;
                try
                {
                    await TailLogAsync(endpoint, vmName, agentLogPath, logSink, ct).ConfigureAwait(false);
                }
                catch (BoxLiteApiException)
                {
                }
                var text = Encoding.UTF8.GetString(bytes).Trim();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
            }
            try
            {
                var log = await Api.ReadFileAsync(endpoint, vmName, agentLogPath, ct).ConfigureAwait(false);
                if (log?.ContentBase64 is not null)
                {
                    string text;
                    try
                    {
                        text = Encoding.UTF8.GetString(Convert.FromBase64String(log.ContentBase64));
                    }
                    catch (FormatException)
                    {
                        text = string.Empty;
                    }
                    if (text.Length > delivered)
                    {
                        logSink?.Invoke(text[delivered..]);
                        delivered = text.Length;
                    }
                }
            }
            catch (BoxLiteApiException)
            {
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private async Task TailLogAsync(
        BoxLiteEndpoint endpoint, string vmName, string agentLogPath, Action<string>? logSink, CancellationToken ct)
    {
        if (logSink is null)
            return;
        var log = await Api.ReadFileAsync(endpoint, vmName, agentLogPath, ct).ConfigureAwait(false);
        if (log?.ContentBase64 is null)
            return;
        try
        {
            logSink(Encoding.UTF8.GetString(Convert.FromBase64String(log.ContentBase64)));
        }
        catch (FormatException)
        {
        }
    }

    public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(liveSuspendedNames);
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var failures = new List<string>();
        IReadOnlyList<BoxLiteVmListItem> items;
        try
        {
            items = await Api.ListManagedVmsAsync(endpoint, opts.NamePrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        }
        catch (BoxLiteApiException ex)
        {
            return [$"list vms: {ex.Message}"];
        }
        foreach (var item in items)
        {
            if (item.Name is null)
                continue;
            var name = item.Name;
            if (liveSuspendedNames.Contains(name))
                continue;
            var wedged = string.Equals(item.State, "stopped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.State, "paused", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.State, "pausing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.State, "stopping", StringComparison.OrdinalIgnoreCase);
            if (!wedged)
                continue;
            try
            {
                if (_activeSandboxes.ContainsKey(name))
                    continue;
                await Api.DeleteVmAsync(endpoint, name, ct).ConfigureAwait(false);
                _log.LogInformation("Reconciled orphaned boxlite VM {Name} (state={State})", name, item.State);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
            }
        }
        return failures;
    }

    // ------------------------------------------------------------------
    // Baselines (OCI-image snapshots baked from setup commands)
    // ------------------------------------------------------------------

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var opts = ReadOptions();
        using var sha = SHA256.Create();
        var material = string.Join("\n", new[]
        {
            profileName?.Trim() ?? string.Empty,
            flavor.ToString(),
            opts.BaselineSourceImage,
            string.Join("\n", opts.SetupCommands),
            opts.DefaultCpuCount.ToString(CultureInfo.InvariantCulture),
            opts.DefaultMemoryMiB.ToString(CultureInfo.InvariantCulture),
        });
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..16];
        return opts.BaselineSnapshotPrefix + hash;
    }

    public async Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var name = pinnedBaselineRef ?? ResolveBaselineRef(profileName, flavor);
        if (string.IsNullOrWhiteSpace(name) || !IsValidBaselineName(name, opts))
            throw new InvalidOperationException($"Baseline name '{name}' is outside the managed boxlite snapshot namespace.");
        var endpoint = ResolveEndpoint(opts);

        var existing = await Api.ListSnapshotsAsync(endpoint, ct).ConfigureAwait(false);
        if (existing.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)
            && string.Equals(s.State, "active", StringComparison.OrdinalIgnoreCase)))
            return name;

        var bakeName = opts.NamePrefix + "bake-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var bakeCreated = false;
        try
        {
            var labels = new Dictionary<string, string>
            {
                [BoxLiteSandboxOptions.ManagedLabelKey] = BoxLiteSandboxOptions.ManagedLabelValue,
            };
            await Api.CreateVmAsync(endpoint, new BoxLiteCreateVmRequest(
                bakeName, opts.BaselineSourceImage, opts.DefaultCpuCount, opts.DefaultMemoryMiB,
                opts.DefaultDiskGiB, Persistent: false,
                new BoxLiteNetworkRequest("open", null), labels, Env: null), ct).ConfigureAwait(false);
            bakeCreated = true;
            await WaitForStateAsync(endpoint, bakeName, "running", opts.BaselineBakeTimeoutSeconds, opts, ct).ConfigureAwait(false);

            var bake = new BoxLiteSandbox(
                bakeName,
                new SandboxSpec { ImageReference = opts.BaselineSourceImage },
                _readOptions, Api, () => ResolveEndpoint(ReadValidatedOptions()),
                mounts: [], _ => { }, _clock, _log);
            await bake.RunSetupCommandsAsync(opts.SetupCommands, ct).ConfigureAwait(false);
            try
            {
                await Api.TransitionVmAsync(endpoint, bakeName, "stop", ct).ConfigureAwait(false);
            }
            catch (BoxLiteApiException ex) when (ex.Kind == BoxLiteFailureKind.Conflict)
            {
            }
            await Api.CreateSnapshotAsync(endpoint, name, bakeName, ct).ConfigureAwait(false);
            return name;
        }
        finally
        {
            if (bakeCreated)
            {
                try
                {
                    await Api.DeleteVmAsync(endpoint, bakeName, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Delete of boxlite bake VM {Name} failed", bakeName);
                }
            }
        }
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var snapshots = await Api.ListSnapshotsAsync(ResolveEndpoint(opts), ct).ConfigureAwait(false);
        return snapshots
            .Where(s => s.Name is not null && s.Name.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal))
            .Select(static s => new BaselineImageInfo(s.Name!, s.CreatedAt, null))
            .ToList();
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidBaselineName(name, opts))
            throw new InvalidOperationException($"Refusing to dispose unmanaged boxlite snapshot '{name}'.");
        await Api.DeleteSnapshotAsync(ResolveEndpoint(opts), name, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Active tracking (capacity cooperation + shutdown handling)
    // ------------------------------------------------------------------

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Where(static e => e.Sandbox is IShutdownTeardownSandbox)
            .Select(static e => (e.WorkItemId, (IShutdownTeardownSandbox)e.Sandbox))
            .ToList();

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
        _activeSandboxes.Select(static kvp =>
            new ActiveSandboxProgress(kvp.Value.WorkItemId, kvp.Key)).ToList();

    public ValueTask<IReadOnlyList<ActiveSandboxProgress>> SnapshotActiveSandboxProgressAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(SnapshotActiveSandboxProgress());

    // ------------------------------------------------------------------
    // Create internals
    // ------------------------------------------------------------------

    private BoxLiteCreateVmRequest BuildCreateRequest(
        SandboxSpec spec, BoxLiteSandboxOptions opts, string name, WorkItemId workItemId)
    {
        var image = !string.IsNullOrWhiteSpace(spec.BaselineImageRef)
            ? spec.BaselineImageRef.Trim()
            : !string.IsNullOrWhiteSpace(spec.ImageReference)
                ? spec.ImageReference.Trim()
                : opts.DefaultImage.Trim();
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException("BoxLite create requires SandboxSpec.ImageReference (or DefaultImage); no image was named.");
        if (image.Length > 512 || image.Any(char.IsControl) || image.Contains(' ', StringComparison.Ordinal))
            throw new InvalidOperationException($"BoxLite image reference '{image}' is not a valid OCI reference.");

        var network = BuildNetworkRequest(spec.Network);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BoxLiteSandboxOptions.ManagedLabelKey] = BoxLiteSandboxOptions.ManagedLabelValue,
        };
        if (workItemId.Value != Guid.Empty)
            labels[BoxLiteSandboxOptions.WorkItemLabelKey] = workItemId.Value.ToString("N", CultureInfo.InvariantCulture);

        return new BoxLiteCreateVmRequest(
            name,
            image,
            Cpu: spec.Limits.CpuCount ?? opts.DefaultCpuCount,
            MemoryMib: spec.Limits.MemoryBytes is { } bytes && bytes > 0
                ? Math.Max(256, (int)Math.Ceiling(bytes / (double)(1024 * 1024)))
                : opts.DefaultMemoryMiB,
            DiskGib: spec.Limits.DiskBytes is { } disk && disk > 0
                ? Math.Max(4, (int)Math.Ceiling(disk / (double)(1024L * 1024 * 1024)))
                : opts.DefaultDiskGiB,
            Persistent: opts.PersistentDisks,
            Network: network,
            Labels: labels,
            Env: null);
    }

    /// <summary>
    /// Maps the pipeline network policy onto the daemon's guest-side
    /// restriction. A named profile implies host-side nftables enforcement
    /// this embedded provider never attaches to, so it is refused here (and
    /// earlier, by placement) rather than silently downgraded.
    /// </summary>
    private static BoxLiteNetworkRequest BuildNetworkRequest(SandboxNetworkPolicy network)
    {
        if (!string.IsNullOrWhiteSpace(network.ProfileName))
        {
            throw new InvalidOperationException(
                $"BoxLite VM cannot serve network profile '{network.ProfileName.Trim()}': " +
                "the kind is classified 'NotEnforced' (no host-enforced egress filtering). " +
                "A 'NotEnforced' provider may only serve sandboxes with no named network profile.");
        }
        if (network.AllowedHosts.Count == 0)
            return new BoxLiteNetworkRequest("isolated", null);
        foreach (var host in network.AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"BoxLite network allowlist entry '{host}' is not a valid hostname.");
        }
        return new BoxLiteNetworkRequest("restricted", network.AllowedHosts.ToArray());
    }

    private async Task ApplyNetworkPolicyAsync(
        BoxLiteEndpoint endpoint, string name, SandboxNetworkPolicy network, CancellationToken ct)
    {
        // Bake-then-lock: setup commands ran on the create-time network, so
        // re-assert the intended restriction now that provisioning is done.
        await Api.SetNetworkAsync(endpoint, name, BuildNetworkRequest(network), ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<BoxLiteMountPlan> ValidateAndPlanMounts(SandboxSpec spec)
    {
        var plans = new List<BoxLiteMountPlan>(spec.Mounts.Count);
        foreach (var mount in spec.Mounts)
        {
            if (mount is null)
                throw new ArgumentException("Sandbox mounts must not contain null entries.", nameof(spec));
            if (!BoxLiteSandbox.IsValidAbsolutePath(mount.SandboxPath))
                throw new ArgumentException($"Mount sandbox path '{mount.SandboxPath}' is not an absolute contained path.", nameof(spec));
            if (mount.Tmpfs && mount.HostPath is null)
            {
                plans.Add(new BoxLiteMountPlan(mount.SandboxPath, null, mount.ReadOnly, IsGuestDir: true));
                continue;
            }
            if (mount.HostPath is null)
            {
                plans.Add(new BoxLiteMountPlan(mount.SandboxPath, null, mount.ReadOnly, IsGuestDir: true));
                continue;
            }
            if (!File.Exists(mount.HostPath) && !Directory.Exists(mount.HostPath))
            {
                throw new SandboxMountSourceMissingException(mount.HostPath,
                    $"BoxLite mount source '{mount.HostPath}' does not exist.");
            }
            plans.Add(new BoxLiteMountPlan(mount.SandboxPath, mount.HostPath, mount.ReadOnly, IsGuestDir: false));
        }
        return plans;
    }

    private async Task WaitForStateAsync(
        BoxLiteEndpoint endpoint, string name, string wantState, int timeoutSeconds, BoxLiteSandboxOptions opts, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(opts.PollIntervalMilliseconds, 100, 60_000));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            BoxLiteVmDto? vm;
            try
            {
                vm = await Api.GetVmAsync(endpoint, name, ct).ConfigureAwait(false);
            }
            catch (BoxLiteApiException ex) when (ex.Kind == BoxLiteFailureKind.NotFound)
            {
                throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "wait for vm", $"VM '{name}' vanished while waiting for '{wantState}'");
            }
            if (vm is null)
                throw new BoxLiteApiException(BoxLiteFailureKind.Unexpected, "wait for vm", $"VM '{name}' vanished while waiting for '{wantState}'");
            if (string.Equals(vm.State, wantState, StringComparison.OrdinalIgnoreCase))
                return;
            if (vm.State is "failed" or "error")
                throw new BoxLiteApiException(BoxLiteFailureKind.ServerError, "wait for vm", $"VM '{name}' entered state '{vm.State}'");
            if (_clock.GetUtcNow() >= deadline)
                throw new BoxLiteApiException(BoxLiteFailureKind.ServerError, "wait for vm", $"VM '{name}' did not reach '{wantState}' in time (state '{vm.State}')");
            await Task.Delay(pollDelay, _clock, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Options / credentials / failure mapping
    // ------------------------------------------------------------------

    private BoxLiteSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The boxlite sandbox provider is disabled. Enable it via " +
                "CodeyBox:Plugins:codeybox.boxlite-sandbox:Enabled=true plus the plugin allowlist.");
        }
        if (!Uri.TryCreate(opts.DaemonUrl, UriKind.Absolute, out var daemonUri)
            || (daemonUri.Scheme != Uri.UriSchemeHttps && daemonUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.boxlite-sandbox:DaemonUrl must be an absolute http(s) URL.");
        if (daemonUri.Scheme == Uri.UriSchemeHttp && !BoxLiteApiClient.IsCleartextHttpPermitted(daemonUri, opts.AllowUnsafeHttp))
            throw new InvalidOperationException(
                "CodeyBox:Plugins:codeybox.boxlite-sandbox:DaemonUrl must use https://. " +
                "AllowUnsafeHttp=true permits http only for loopback daemon URLs, never for remote hosts.");
        if (string.IsNullOrWhiteSpace(opts.ApiTokenEnvVar))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.boxlite-sandbox:ApiTokenEnvVar must be set.");
        if (string.IsNullOrWhiteSpace(opts.NamePrefix) || !opts.NamePrefix.All(IsNameChar))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.boxlite-sandbox:NamePrefix must contain only lowercase letters, numbers, and hyphens.");
        if (!opts.NamePrefix.StartsWith(DefaultNamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:codeybox.boxlite-sandbox:NamePrefix must start with '{DefaultNamePrefix}' so leak reaping can identify managed VMs.");
        if (!string.IsNullOrWhiteSpace(opts.BaselineSnapshotPrefix) && !opts.BaselineSnapshotPrefix.All(IsNameChar))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.boxlite-sandbox:BaselineSnapshotPrefix must contain only lowercase letters, numbers, and hyphens.");
        if (opts.MaxListPages <= 0)
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.boxlite-sandbox:MaxListPages must be greater than zero.");
        if (opts.MaxSyncArchiveBase64Bytes <= 0 || opts.MaxSyncArchiveBytes <= 0 ||
            opts.MaxSyncArchiveExpandedBytes <= 0 || opts.MaxSyncArchiveEntries <= 0 ||
            opts.MaxFileSyncBase64Bytes <= 0 || opts.MaxFileSyncBytes <= 0 || opts.MaxExecInputBytes <= 0)
            throw new InvalidOperationException("BoxLite sync/exec size limits must all be greater than zero.");
        if (opts.AllowUnsafeHttp)
            _log.LogWarning("The boxlite sandbox provider allows cleartext http: the API token rides every request, so enable AllowUnsafeHttp only for a loopback daemon.");
        return opts;
    }

    /// <summary>
    /// Resolves the API token from the credential chain (process environment)
    /// at call time — never from the options record, so a config file can
    /// never carry the secret and rotation propagates without a restart.
    /// </summary>
    private BoxLiteEndpoint ResolveEndpoint(BoxLiteSandboxOptions opts)
    {
        var token = _environment(opts.ApiTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"BoxLite API token environment variable '{opts.ApiTokenEnvVar}' is not set. " +
                "Provision it via the host credential chain (vault agent, container secrets) — never in configuration files.");
        }
        var baseUri = new Uri(opts.DaemonUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new BoxLiteEndpoint(baseUri, token.Trim(), opts.AllowUnsafeHttp);
    }

    /// <summary>
    /// Maps a service failure onto a provisioning deferral: the service said
    /// no, which is an infrastructure signal — never a verdict on the work
    /// item's diff. Auth/quota failures get a longer recheck so an operator
    /// can fix credentials/capacity; throttling honours Retry-After.
    /// </summary>
    private SandboxProvisioningDeferredException ToDeferred(
        BoxLiteSandboxOptions opts, BoxLiteApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            BoxLiteFailureKind.Unauthorized or BoxLiteFailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            BoxLiteFailureKind.QuotaExhausted =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            BoxLiteFailureKind.Throttled =>
                ("throttled", ex.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : baseRecheck),
            BoxLiteFailureKind.Conflict or BoxLiteFailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            BoxLiteFailureKind.Unreachable => ("unreachable", baseRecheck),
            _ => ("server-error", baseRecheck),
        };
        return new SandboxProvisioningDeferredException(
            Name, "create", errorClass, $"{context}: {ex.Message}", recheck);
    }

    private static TimeSpan MaxOf(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private async Task<bool> TryDeleteAfterCreateFailureAsync(BoxLiteEndpoint endpoint, string name)
    {
        try
        {
            await Api.DeleteVmAsync(endpoint, name, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete boxlite VM {Name} after create failure", name);
            return false;
        }
    }

    private void MarkNoLongerActive(BoxLiteSandbox sandbox) => MarkNoLongerActive(sandbox.Id);

    private void MarkNoLongerActive(string name)
    {
        if (_activeSandboxes.TryRemove(name, out _))
            SandboxLiveCounter.Decrement();
    }

    private static string GenerateSandboxName(string prefix)
    {
        var normalizedPrefix = prefix.EndsWith("-", StringComparison.Ordinal) ? prefix : prefix + "-";
        return normalizedPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static bool IsValidManagedName(string name, string namePrefix) =>
        !string.IsNullOrWhiteSpace(name)
        && name.StartsWith(namePrefix, StringComparison.Ordinal)
        && name.All(IsNameChar);

    private static bool IsNameChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';

    private static bool IsValidBaselineName(string name, BoxLiteSandboxOptions opts) =>
        !string.IsNullOrWhiteSpace(name)
        && name.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal)
        && name.All(c => IsNameChar(c) || c is '.' or '_' or '/' or ':' or '@');

    /// <summary>Anchors the adopted-agent log tail under the conventional agent-log dir.</summary>
    internal static bool IsValidAgentLogPath(string path)
    {
        if (!BoxLiteSandbox.IsValidAbsolutePath(path))
            return false;
        var anchor = BoxLiteSandboxConventions.AgentLogDir + "/";
        return path.StartsWith(anchor, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> WithTimingEnvironment(
        IReadOnlyDictionary<string, string> environment, WorkItemId? workItemId)
    {
        if (workItemId is not { } id || id.Value == Guid.Empty)
            return environment;
        var env = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            // Matches SandboxConventions.WorkItemIdEnvironmentVariable so the
            // watchdog can attribute in-guest work to the owning item.
            ["CODEYBOX_WORK_ITEM_ID"] = id.ToString(),
        };
        return env;
    }

    public void Dispose() => _clients.Dispose();

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, BoxLiteSandbox Sandbox);
}
