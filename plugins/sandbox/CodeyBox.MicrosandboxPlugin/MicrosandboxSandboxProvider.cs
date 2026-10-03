using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.MicrosandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by microsandbox local microVMs served from a
/// loopback server. Contributes the <c>microsandbox</c> provider kind through the
/// plugin trust model: the host owns egress classification, so this kind is
/// always <c>NotEnforced</c> — the server's per-sandbox network restriction is
/// defence in depth, not the host nftables egress guarantee, and no option or
/// return value can promote it. Deployments requiring enforced egress are
/// refused by placement before this provider is ever called.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.microsandbox-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set).</para>
///
/// <para>Live branching (see <see cref="BranchSandboxAsync"/>) maps onto the
/// existing sandbox concepts without a new pipeline concept: a branch is a
/// copy-on-write fork that becomes an ordinary <see cref="ISandbox"/> handle
/// with its own identity, tracked in the same active set and torn down by the
/// same dispose path. It is not a baseline image (baselines are named,
/// reusable snapshots via <see cref="IBaselineImageProvisioner"/>), not a
/// suspend/resume (the parent keeps running), and not a second member — one
/// branch, one handle, one admission permit.</para>
/// </summary>
[CodeyBoxPlugin(MicrosandboxSandboxOptions.PluginId, "microsandbox sandbox provider")]
public sealed class MicrosandboxSandboxProvider :
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

    private const long ExitMarkerMaxBytes = 256;
    private const int MaxImageRefLength = 512;
    private const int MinMemoryMiB = 256;
    private const int MinDiskGiB = 4;
    private const double BytesPerMiB = 1024 * 1024;
    private const double BytesPerGiB = 1024d * 1024 * 1024;
    private const string AgentLogAnchor = "/work/.codeybox/agent-logs/";

    private readonly Func<MicrosandboxSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly LazyCredentialClient<HttpClient> _clients;
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>; the section is re-read per call so hot reloads apply.</summary>
    public MicrosandboxSandboxProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = name => Environment.GetEnvironmentVariable(name);
        _readOptions = () => MicrosandboxSandboxOptions.FromConfiguration(_host?.ScopedConfig);
        _clients = new LazyCredentialClient<HttpClient>(
            () => TimeSpan.FromSeconds(ReadOptions().HttpTimeoutSeconds),
            http => http);
    }

    /// <summary>Test seam: full constructor injection.</summary>
    internal MicrosandboxSandboxProvider(
        Func<MicrosandboxSandboxOptions> readOptions,
        HttpClient httpClient,
        Func<string, string?> environment,
        TimeProvider clock,
        ILogger log)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
            "microsandbox sandbox provider initialized (enabled={Enabled}, server={ServerUrl})",
            options.Enabled, options.ServerUrl);
        return Task.CompletedTask;
    }

    public string Name => MicrosandboxSandboxOptions.ProviderKind;

    /// <inheritdoc/>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
            return true;
        try
        {
            return IsValidManagedName(name, ReadOptions().NamePrefix);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// The strongest isolation this provider can honestly claim. Microsandbox
    /// runs each sandbox as a hardware-isolated microVM with its own guest
    /// kernel — a real guest-boundary claim routed to workload-trust decisions.
    /// It says nothing about network-egress enforcement, which stays host-owned
    /// (<c>NotEnforced</c> for every plugin kind).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honest capability set: baseline bake (provision a sandbox, snapshot it,
    /// create later sandboxes from the snapshot), suspend/resume (pause/resume),
    /// and teardown (stop/preserve + delete). Not declared: disk-guard (the
    /// server reports no per-sandbox disk usage CodeyBox can enforce),
    /// cache-seeding (not implemented), port-publishing (the server exposes no
    /// host-port lease handshake the synchronous port-publisher contract can
    /// express).
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities =>
    [
        SandboxCapabilities.BaselineBake,
        SandboxCapabilities.SuspendResume,
        SandboxCapabilities.Teardown,
    ];

    private MicrosandboxSandboxOptions ReadOptions() => _readOptions();

    private HttpClient Http => _clients.Get();

    private MicrosandboxApiClient Api => new(Http);

    // ------------------------------------------------------------------
    // Provider lifecycle
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec = spec with { Environment = WithTimingEnvironment(spec.Environment, spec.TimingWorkItemId) };

        var opts = ReadValidatedOptions();
        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("microsandbox sandbox provider does not support the graphical sandbox flavor.");

        var endpoint = ResolveEndpoint(opts);

        if (spec.RecoveryLease is { } lease)
            return await AdoptRetainedAsync(spec, opts, endpoint, lease, ct).ConfigureAwait(false);

        var name = GenerateSandboxName(opts.NamePrefix);
        var mounts = ValidateAndPlanMounts(spec);
        var workItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var created = false;

        try
        {
            var request = BuildCreateRequest(spec, opts, name, workItemId);
            var createdDto = await Api.CreateSandboxAsync(endpoint, request, ct).ConfigureAwait(false);
            created = true;
            var sandboxId = createdDto.Name ?? name;

            var sandbox = new MicrosandboxSandbox(
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

            _log.LogInformation("Created microsandbox {Name} (id {SandboxId})", name, sandboxId);
            return sandbox;
        }
        catch (OperationCanceledException)
        {
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
                    new MicrosandboxApiException(MicrosandboxFailureKind.Unexpected, "create-cleanup", "delete after failure did not prove removal"),
                    $"create failed and best-effort delete did not prove sandbox {name} was removed: {ex.Message}");
            }

            if (ex is MicrosandboxApiException apiEx)
                throw ToDeferred(opts, apiEx, $"microsandbox create failed for {name}");
            throw;
        }
    }

    /// <summary>
    /// Live branch: fork a running managed sandbox into a new sandbox with its
    /// own identity for parallel contract-testing variants. The branch is an
    /// ordinary handle — same active tracking, same teardown — so placement
    /// and capacity accounting need no new concept.
    /// </summary>
    public async Task<ISandbox> BranchSandboxAsync(string parentName, SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(parentName))
            throw new ArgumentException("Parent sandbox name must be non-empty.", nameof(parentName));
        var opts = ReadValidatedOptions();
        var parent = parentName.Trim();
        if (!IsValidManagedName(parent, opts.NamePrefix))
            throw new InvalidOperationException($"Refusing to branch unmanaged microsandbox '{parentName}'.");
        var endpoint = ResolveEndpoint(opts);

        var existing = await Api.GetSandboxAsync(endpoint, parent, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"microsandbox '{parent}' does not exist; cannot branch it.");

        var labels = existing.Labels ?? new Dictionary<string, string>();
        if (!labels.TryGetValue(MicrosandboxSandboxOptions.ManagedLabelKey, out var managed)
            || !string.Equals(managed, MicrosandboxSandboxOptions.ManagedLabelValue, StringComparison.Ordinal))
            throw new InvalidOperationException($"microsandbox '{parent}' lacks the managed-ownership label; refusing to branch it.");

        var branchName = GenerateSandboxName(opts.NamePrefix);
        var workItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var created = false;
        try
        {
            await Api.BranchSandboxAsync(endpoint, parent, branchName, ct).ConfigureAwait(false);
            created = true;
            var sandbox = new MicrosandboxSandbox(
                branchName,
                spec,
                _readOptions,
                Api,
                () => ResolveEndpoint(ReadValidatedOptions()),
                ValidateAndPlanMounts(spec),
                MarkNoLongerActive,
                _clock,
                _log);
            _activeSandboxes[branchName] = new ActiveSandboxEntry(workItemId, sandbox);
            SandboxLiveCounter.Increment();
            await WaitForStateAsync(endpoint, branchName, "running", opts.ReadyTimeoutSeconds, opts, ct).ConfigureAwait(false);
            _log.LogInformation("Branched microsandbox {Parent} -> {Branch}", parent, branchName);
            return sandbox;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            MarkNoLongerActive(branchName);
            if (created)
                await TryDeleteAfterCreateFailureAsync(endpoint, branchName).ConfigureAwait(false);
            if (ex is MicrosandboxApiException apiEx)
                throw ToDeferred(opts, apiEx, $"microsandbox branch failed for {parent}");
            throw;
        }
    }

    private async Task<ISandbox> AdoptRetainedAsync(
        SandboxSpec spec,
        MicrosandboxSandboxOptions opts,
        MicrosandboxEndpoint endpoint,
        SandboxRecoveryLease lease,
        CancellationToken ct)
    {
        if (!string.Equals(lease.ProviderId, MicrosandboxSandboxOptions.ProviderKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recovery lease names provider '{lease.ProviderId}', not 'microsandbox'; refusing adoption.");
        }

        if (!IsValidManagedName(lease.SandboxId, opts.NamePrefix))
        {
            throw new InvalidOperationException(
                $"Recovery lease names sandbox '{lease.SandboxId}' outside the managed microsandbox namespace; refusing adoption.");
        }

        var existing = await Api.GetSandboxAsync(endpoint, lease.SandboxId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Retained microsandbox '{lease.SandboxId}' no longer exists; refusing adoption.");

        var labels = existing.Labels ?? new Dictionary<string, string>();
        if (!labels.TryGetValue(MicrosandboxSandboxOptions.ManagedLabelKey, out var managed)
            || !string.Equals(managed, MicrosandboxSandboxOptions.ManagedLabelValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"microsandbox '{lease.SandboxId}' lacks the managed-ownership label; refusing adoption.");
        }

        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Token)));
        if (!labels.TryGetValue(MicrosandboxSandboxOptions.RecoveryTokenHashLabelKey, out var hash)
            || !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Recovery token does not match the retained microsandbox '{lease.SandboxId}'; refusing adoption.");
        }

        try
        {
            await Api.TransitionAsync(endpoint, lease.SandboxId, "start", ct).ConfigureAwait(false);
        }
        catch (MicrosandboxApiException ex) when (ex.Kind == MicrosandboxFailureKind.Conflict)
        {
        }

        await WaitForStateAsync(endpoint, lease.SandboxId, "running", opts.ReadyTimeoutSeconds, opts, ct).ConfigureAwait(false);

        var handle = new MicrosandboxSandbox(
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
        _log.LogInformation("Adopted retained microsandbox {Name}", lease.SandboxId);
        return handle;
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var items = await Api.ListManagedAsync(endpoint, opts.NamePrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        return items
            .Where(static i => i.Name is not null)
            .Select(i => new ManagedSandboxInfo(
                i.Name!,
                null,
                null,
                IsTrackedActive: _activeSandboxes.ContainsKey(i.Name!),
                IsSuspendLifecycleOrFrozen: string.Equals(i.State, "paused", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i.State, "pausing", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (!IsValidManagedName(name, ReadValidatedOptions().NamePrefix))
            throw new InvalidOperationException($"Refusing to dispose unmanaged microsandbox '{name}'.");
        var opts = ReadValidatedOptions();
        await Api.DeleteSandboxAsync(ResolveEndpoint(opts), name, ct).ConfigureAwait(false);
    }

    public Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.LifecycleProviderId is not null || sandbox.HostId is not null)
        {
            throw new NotSupportedException(
                "This sandbox lifecycle cannot interpret a provider- or host-scoped disposal snapshot.");
        }

        return DisposeLeakedAsync(sandbox.Name, ct);
    }

    // ------------------------------------------------------------------
    // Suspend / resume (host shutdown + startup adoption)
    // ------------------------------------------------------------------

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sandbox name must be non-empty.", nameof(name));
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(name.Trim(), opts.NamePrefix))
            throw new InvalidOperationException($"Refusing to resume unmanaged microsandbox '{name}'.");
        var endpoint = ResolveEndpoint(opts);
        var existing = await Api.GetSandboxAsync(endpoint, name.Trim(), ct).ConfigureAwait(false);
        if (existing is null)
            return;
        if (string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            await Api.TransitionAsync(endpoint, name.Trim(), "resume", ct).ConfigureAwait(false);
        }
        catch (MicrosandboxApiException ex) when (ex.Kind == MicrosandboxFailureKind.Conflict)
        {
            await Api.TransitionAsync(endpoint, name.Trim(), "start", ct).ConfigureAwait(false);
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
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(vmName, opts.NamePrefix))
            return null;
        if (!IsValidAgentLogPath(agentLogPath))
            return null;
        var endpoint = ResolveEndpoint(opts);
        var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(opts.PollIntervalMilliseconds, 100, 60_000));
        var stopAt = deadline is { } d && d > TimeSpan.Zero ? _clock.GetUtcNow() + d : (DateTimeOffset?)null;
        var delivered = 0;
        while (!ct.IsCancellationRequested)
        {
            if (stopAt is { } stop && _clock.GetUtcNow() >= stop)
                return null;
            MicrosandboxFileDto? exit;
            try
            {
                exit = await Api.ReadFileAsync(endpoint, vmName, agentLogPath + ".exit", opts.MaxFileSyncBase64Bytes + 64 * 1024, ct)
                    .ConfigureAwait(false);
            }
            catch (MicrosandboxApiException ex)
            {
                _log.LogDebug(ex, "microsandbox adopt exit-marker read for {Name} failed", vmName);
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
                    _log.LogDebug("microsandbox adopt exit marker for {Name} is not valid base64", vmName);
                    return null;
                }

                if (bytes.Length > ExitMarkerMaxBytes)
                    return null;
                var text = Encoding.UTF8.GetString(bytes).Trim();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
            }

            try
            {
                var log = await Api.ReadFileAsync(endpoint, vmName, agentLogPath, opts.MaxFileSyncBase64Bytes + 64 * 1024, ct)
                    .ConfigureAwait(false);
                delivered = TryDeliverLogDelta(log?.ContentBase64, logSink, delivered, opts, vmName);
            }
            catch (MicrosandboxApiException ex)
            {
                _log.LogDebug(ex, "microsandbox adopt-log poll for {Name} failed", vmName);
            }

            try
            {
                await Task.Delay(pollDelay, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private int TryDeliverLogDelta(
        string? contentBase64, Action<string>? logSink, int delivered, MicrosandboxSandboxOptions opts, string vmName)
    {
        if (logSink is null || contentBase64 is null)
            return delivered;
        if (contentBase64.Length > opts.MaxFileSyncBase64Bytes)
        {
            _log.LogDebug("microsandbox adopt log for {Name} exceeds the per-read bound; skipping this tick", vmName);
            return delivered;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            _log.LogDebug("microsandbox adopt log for {Name} is not valid base64; skipping this tick", vmName);
            return delivered;
        }

        if (raw.LongLength > opts.MaxFileSyncBytes)
        {
            _log.LogDebug("microsandbox adopt log for {Name} exceeds the decoded bound; skipping this tick", vmName);
            return delivered;
        }

        var text = Encoding.UTF8.GetString(raw);
        if (text.Length > delivered)
        {
            logSink(text[delivered..]);
            return text.Length;
        }

        return delivered;
    }

    public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(liveSuspendedNames);
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var failures = new List<string>();
        IReadOnlyList<MicrosandboxListItem> items;
        try
        {
            items = await Api.ListManagedAsync(endpoint, opts.NamePrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        }
        catch (MicrosandboxApiException ex)
        {
            _log.LogWarning(ex, "microsandbox stuck-sandbox reconciliation could not list inventory");
            return [];
        }

        foreach (var item in items)
        {
            if (item.Name is null || liveSuspendedNames.Contains(item.Name))
                continue;
            if (!string.Equals(item.State, "pausing", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.State, "stopping", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.State, "unknown", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                try
                {
                    await Api.TransitionAsync(endpoint, item.Name, "stop", ct).ConfigureAwait(false);
                }
                catch (MicrosandboxApiException stopEx) when (stopEx.Kind == MicrosandboxFailureKind.NotFound)
                {
                    continue;
                }

                await Api.DeleteSandboxAsync(endpoint, item.Name, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "microsandbox stuck-sandbox recovery failed for {Name}", item.Name);
                failures.Add(item.Name);
            }
        }

        return failures;
    }

    public async Task<bool> PushSuspendedVmCheckpointRefAsync(
        string vmName, string workingDir, string refName, string commitMessage, CancellationToken ct)
    {
        if (!IsValidManagedName(vmName, ReadValidatedOptions().NamePrefix))
            return false;
        if (!MicrosandboxSandbox.IsValidAbsolutePath(workingDir) || string.IsNullOrWhiteSpace(refName))
            return false;
        if (commitMessage.Any(c => c is ';' or '&' or '|' or '$' or '`' or '\n' or '\r'))
            return false;
        var endpoint = ResolveEndpoint(ReadValidatedOptions());
        var existing = await Api.GetSandboxAsync(endpoint, vmName, ct).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
            return false;
        var handle = new MicrosandboxSandbox(
            vmName,
            new SandboxSpec { ImageReference = "checkpoint", WorkingDirectory = workingDir },
            _readOptions, Api, () => ResolveEndpoint(ReadValidatedOptions()),
            [], _ => { }, _clock, _log);
        var script = "git add -A && git commit --allow-empty -m \"$0\" && git push origin HEAD:\"$1\"";
        var result = await handle.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-lc", script, commitMessage, refName],
            WorkingDirectory = workingDir,
        }, ct).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.ExecutionUnavailable;
    }

    // ------------------------------------------------------------------
    // Baselines
    // ------------------------------------------------------------------

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var opts = ReadOptions();
        if (string.IsNullOrWhiteSpace(profileName))
            return null;
        var profile = profileName.Trim().ToLowerInvariant();
        return $"{opts.BaselineSnapshotPrefix}{profile}-{flavor.ToString().ToLowerInvariant()}";
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var snapshots = await Api.ListSnapshotsAsync(endpoint, ct).ConfigureAwait(false);
        return snapshots
            .Where(s => s.Snapshot is not null && s.Snapshot.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal))
            .Select(s => new BaselineImageInfo(s.Snapshot!, null, null))
            .ToList();
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (string.IsNullOrWhiteSpace(name)
            || !name.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to dispose non-baseline microsandbox snapshot '{name}'.");
        await Api.DeleteSnapshotAsync(ResolveEndpoint(opts), name, ct).ConfigureAwait(false);
    }

    public async Task<string?> EnsureBaselineImageAsync(
        string profileName, SandboxProfileFlavor flavor, string? pinnedBaselineRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (flavor != SandboxProfileFlavor.Headless)
            return null;
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var snapshot = string.IsNullOrWhiteSpace(pinnedBaselineRef)
            ? ResolveBaselineRef(profileName, flavor)
            : pinnedBaselineRef.Trim();
        if (string.IsNullOrWhiteSpace(snapshot))
            return null;
        if (!snapshot.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to bake microsandbox baseline outside '{opts.BaselineSnapshotPrefix}'.");
        var existing = await Api.ListSnapshotsAsync(endpoint, ct).ConfigureAwait(false);
        if (existing.Any(s => string.Equals(s.Snapshot, snapshot, StringComparison.Ordinal)))
            return snapshot;

        var bakeName = GenerateSandboxName(opts.NamePrefix);
        var image = string.IsNullOrWhiteSpace(opts.BaselineSourceImage) ? "microsandbox/base:latest" : opts.BaselineSourceImage;
        var created = false;
        try
        {
            await Api.CreateSandboxAsync(endpoint, new MicrosandboxCreateRequest(
                bakeName,
                image,
                opts.DefaultCpuCount, opts.DefaultMemoryMiB, opts.DefaultDiskGiB,
                new Dictionary<string, string>
                {
                    [MicrosandboxSandboxOptions.ManagedLabelKey] = MicrosandboxSandboxOptions.ManagedLabelValue,
                },
                new MicrosandboxNetworkDto("open", []),
                Snapshot: null), ct).ConfigureAwait(false);
            created = true;
            await WaitForStateAsync(endpoint, bakeName, "running", opts.BaselineBakeTimeoutSeconds, opts, ct).ConfigureAwait(false);
            await Api.CreateSnapshotAsync(endpoint, bakeName, snapshot, ct).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            if (created)
                await TryDeleteAfterCreateFailureAsync(endpoint, bakeName).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Active tracking / live load
    // ------------------------------------------------------------------

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Select(e => (e.WorkItemId, (IShutdownTeardownSandbox)e.Sandbox))
            .ToList();

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress() =>
        _activeSandboxes
            .Select(kvp => new ActiveSandboxProgress(kvp.Value.WorkItemId, kvp.Key))
            .ToList();

    public ValueTask<IReadOnlyList<ActiveSandboxProgress>> SnapshotActiveSandboxProgressAsync(CancellationToken ct = default)
    {
        _ = ct;
        return ValueTask.FromResult(SnapshotActiveSandboxProgress());
    }

    // ------------------------------------------------------------------
    // Options / endpoint / naming
    // ------------------------------------------------------------------

    private MicrosandboxSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
            throw new InvalidOperationException(
                "microsandbox sandbox provider is disabled; set CodeyBox:Plugins:codeybox.microsandbox-sandbox:Enabled=true to use it.");
        return opts;
    }

    private MicrosandboxEndpoint ResolveEndpoint(MicrosandboxSandboxOptions opts)
    {
        var apiKeyEnvVar = string.IsNullOrWhiteSpace(opts.ApiKeyEnvVar) ? "MICROSANDBOX_API_KEY" : opts.ApiKeyEnvVar.Trim();
        var apiKey = _environment(apiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"microsandbox API key is not configured: environment variable '{apiKeyEnvVar}' is empty. " +
                "Provision it from the host credential chain — never from a configuration file.");
        }

        if (!Uri.TryCreate(opts.ServerUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"microsandbox ServerUrl '{opts.ServerUrl}' is not an absolute URI.");
        if (!MicrosandboxApiClient.IsCleartextHttpPermitted(uri, opts.AllowUnsafeHttp))
        {
            throw new InvalidOperationException(
                $"microsandbox ServerUrl '{opts.ServerUrl}' is a non-loopback cleartext http URL; refusing to send the API key over it.");
        }

        return new MicrosandboxEndpoint(uri, apiKey, opts.AllowUnsafeHttp);
    }

    internal static string GenerateSandboxName(string prefix)
    {
        var safe = string.IsNullOrWhiteSpace(prefix) ? DefaultNamePrefix : prefix;
        return $"{safe}{Guid.NewGuid():N}";
    }

    internal static bool IsValidManagedName(string name, string prefix)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var safe = string.IsNullOrWhiteSpace(prefix) ? DefaultNamePrefix : prefix;
        if (!name.StartsWith(safe, StringComparison.Ordinal))
            return false;
        var suffix = name[safe.Length..];
        return suffix.Length == 32 && suffix.All(c => Uri.IsHexDigit(c));
    }

    private static bool IsValidAgentLogPath(string path)
    {
        if (!MicrosandboxSandbox.IsValidAbsolutePath(path))
            return false;
        return path.StartsWith(AgentLogAnchor, StringComparison.Ordinal);
    }

    private static MicrosandboxCreateRequest BuildCreateRequest(
        SandboxSpec spec, MicrosandboxSandboxOptions opts, string name, WorkItemId workItemId)
    {
        if (spec.Network.ProfileName is not null)
        {
            throw new InvalidOperationException(
                "microsandbox sandbox provider is classified 'NotEnforced' and cannot serve a named network profile; " +
                "placement refuses such specs before the provider is called.");
        }

        var image = string.IsNullOrWhiteSpace(spec.ImageReference) ? opts.DefaultImage : spec.ImageReference.Trim();
        if (string.IsNullOrWhiteSpace(image))
            throw new ArgumentException("Sandbox image reference is required (no DefaultImage configured).", nameof(spec));
        if (image.Length > MaxImageRefLength)
            throw new ArgumentException("Sandbox image reference exceeds the size limit.", nameof(spec));

        var cpu = Math.Clamp(spec.Limits.CpuCount ?? opts.DefaultCpuCount, 1, 64);
        var memoryMib = Math.Clamp(
            (int)Math.Ceiling((spec.Limits.MemoryBytes ?? opts.DefaultMemoryMiB * BytesPerMiB) / BytesPerMiB),
            MinMemoryMiB, 262144);
        var diskGib = Math.Clamp(
            (int)Math.Ceiling((spec.Limits.DiskBytes ?? opts.DefaultDiskGiB * BytesPerGiB) / BytesPerGiB),
            MinDiskGiB, 1024);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MicrosandboxSandboxOptions.ManagedLabelKey] = MicrosandboxSandboxOptions.ManagedLabelValue,
        };
        if (workItemId.Value != Guid.Empty)
            labels[MicrosandboxSandboxOptions.WorkItemLabelKey] = workItemId.Value.ToString("N");

        return new MicrosandboxCreateRequest(
            name, image, cpu, memoryMib, diskGib, labels, BuildNetworkRequest(spec.Network));
    }

    private static MicrosandboxNetworkDto BuildNetworkRequest(SandboxNetworkPolicy network) =>
        network.AllowedHosts.Count == 0
            ? new MicrosandboxNetworkDto("isolated", [])
            : new MicrosandboxNetworkDto(
                "restricted",
                network.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList());

    private static IReadOnlyList<MicrosandboxMountPlan> ValidateAndPlanMounts(SandboxSpec spec)
    {
        var plans = new List<MicrosandboxMountPlan>(spec.Mounts.Count);
        foreach (var mount in spec.Mounts)
        {
            if (mount is null)
                throw new ArgumentException("Sandbox mounts must not contain null entries.", nameof(spec));
            if (!MicrosandboxSandbox.IsValidAbsolutePath(mount.SandboxPath))
                throw new ArgumentException($"Mount sandbox path '{mount.SandboxPath}' is not an absolute contained path.", nameof(spec));
            if (mount.HostPath is null)
            {
                plans.Add(new MicrosandboxMountPlan(mount.SandboxPath, null, mount.ReadOnly, IsGuestDir: true));
                continue;
            }

            if (!File.Exists(mount.HostPath) && !Directory.Exists(mount.HostPath))
            {
                throw new SandboxMountSourceMissingException(mount.HostPath,
                    $"microsandbox mount source '{mount.HostPath}' does not exist.");
            }

            plans.Add(new MicrosandboxMountPlan(mount.SandboxPath, mount.HostPath, mount.ReadOnly, IsGuestDir: false));
        }

        return plans;
    }

    private async Task WaitForStateAsync(
        MicrosandboxEndpoint endpoint, string name, string wantState, int timeoutSeconds, MicrosandboxSandboxOptions opts, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        var pollDelay = TimeSpan.FromMilliseconds(Math.Clamp(opts.PollIntervalMilliseconds, 100, 60_000));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sandbox = await Api.GetSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
            if (sandbox is null)
                throw new MicrosandboxApiException(MicrosandboxFailureKind.Unexpected, "wait for sandbox", $"sandbox '{name}' vanished while waiting for '{wantState}'");
            if (string.Equals(sandbox.State, wantState, StringComparison.OrdinalIgnoreCase))
                return;
            if (sandbox.State is "failed" or "error")
                throw new MicrosandboxApiException(MicrosandboxFailureKind.ServerError, "wait for sandbox", $"sandbox '{name}' entered state '{sandbox.State}'");
            if (_clock.GetUtcNow() >= deadline)
                throw new MicrosandboxApiException(MicrosandboxFailureKind.ServerError, "wait for sandbox", $"sandbox '{name}' did not reach '{wantState}' in time (state '{sandbox.State}')");
            await Task.Delay(pollDelay, _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyNetworkPolicyAsync(
        MicrosandboxEndpoint endpoint, string name, SandboxNetworkPolicy network, CancellationToken ct)
    {
        await Api.SetNetworkAsync(endpoint, name, BuildNetworkRequest(network), ct).ConfigureAwait(false);
    }

    private async Task<bool> TryDeleteAfterCreateFailureAsync(MicrosandboxEndpoint endpoint, string name)
    {
        try
        {
            await Api.DeleteSandboxAsync(endpoint, name, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Best-effort delete of failed microsandbox {Name} did not complete", name);
            return false;
        }
    }

    private SandboxProvisioningDeferredException ToDeferred(
        MicrosandboxSandboxOptions opts, MicrosandboxApiException ex, string operation)
    {
        return new SandboxProvisioningDeferredException(
            MicrosandboxSandboxOptions.ProviderKind,
            operation,
            MicrosandboxApiException.ToErrorClass(ex.Kind),
            ex.Message,
            TimeSpan.FromSeconds(Math.Clamp(opts.ProvisioningRecheckSeconds, 5, 3600)),
            innerException: ex);
    }

    private void MarkNoLongerActive(string name)
    {
        if (_activeSandboxes.TryRemove(name, out _))
            SandboxLiveCounter.Decrement();
    }

    private static IReadOnlyDictionary<string, string> WithTimingEnvironment(
        IReadOnlyDictionary<string, string> environment, WorkItemId? workItemId)
    {
        if (workItemId is not { } id || id.Value == Guid.Empty)
            return environment;
        var env = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            ["CODEYBOX_WORK_ITEM_ID"] = id.ToString(),
        };
        return env;
    }

    public void Dispose() => _clients.Dispose();

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, MicrosandboxSandbox Sandbox);
}
