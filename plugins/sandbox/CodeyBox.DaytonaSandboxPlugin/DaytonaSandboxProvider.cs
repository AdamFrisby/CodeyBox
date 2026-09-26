using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.DaytonaSandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by hosted <a href="https://www.daytona.io">Daytona</a>
/// sandboxes. Contributes the <c>daytona</c> provider kind through the plugin
/// trust model: the host owns egress classification, so this kind is always
/// <c>NotEnforced</c> — the guest runs on infrastructure CodeyBox does not
/// control and the host nftables egress guarantee cannot apply. Deployments
/// requiring enforced egress are refused by placement before this provider is
/// ever called.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.daytona-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set).</para>
/// </summary>
[CodeyBoxPlugin(DaytonaSandboxOptions.PluginId, "Daytona sandbox provider")]
public sealed class DaytonaSandboxProvider :
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

    private readonly Func<DaytonaSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly IDaytonaWebSocketFactory _webSocketFactory;
    private readonly TimeProvider _clock;
    private readonly ITimingStore? _timings;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly LazyCredentialClient<HttpClient> _clients;
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>; the section is re-read per call so hot reloads apply.</summary>
    public DaytonaSandboxProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = name => Environment.GetEnvironmentVariable(name);
        _webSocketFactory = new ClientWebSocketDaytonaWebSocketFactory();
        _readOptions = () => DaytonaSandboxOptions.FromConfiguration(_host?.ScopedConfig);
        _clients = new LazyCredentialClient<HttpClient>(
            () => TimeSpan.FromSeconds(ReadOptions().HttpTimeoutSeconds),
            http => http);
    }

    /// <summary>Test seam: full constructor injection.</summary>
    internal DaytonaSandboxProvider(
        Func<DaytonaSandboxOptions> readOptions,
        HttpClient httpClient,
        IDaytonaWebSocketFactory webSocketFactory,
        Func<string, string?> environment,
        TimeProvider clock,
        ITimingStore? timings,
        ILogger log)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _webSocketFactory = webSocketFactory ?? throw new ArgumentNullException(nameof(webSocketFactory));
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
            "Daytona sandbox provider initialized (enabled={Enabled}, api={ApiUrl}, organization={Org})",
            options.Enabled, options.ApiUrl,
            string.IsNullOrEmpty(options.OrganizationId) ? "default" : "configured");
        return Task.CompletedTask;
    }

    public string Name => DaytonaSandboxOptions.ProviderKind;

    /// <summary>
    /// The strongest isolation this provider can honestly claim. Daytona's
    /// default sandbox class is container-based (shared runner kernel);
    /// operators who configure <c>SandboxClass=linux-vm</c> get VM isolation —
    /// the claim follows that configuration and never asserts more.
    /// </summary>
    public SandboxIsolationLevel IsolationLevel =>
        string.Equals(ReadOptions().SandboxClass, "linux-vm", StringComparison.OrdinalIgnoreCase)
            ? SandboxIsolationLevel.DedicatedKernel
            : SandboxIsolationLevel.SharedKernel;

    /// <summary>
    /// Honest capability set: baseline bake (provision a sandbox, snapshot it,
    /// create later sandboxes from the snapshot), suspend/resume (pause/start),
    /// and teardown (stop/preserve + delete). Not declared: disk-guard (a
    /// host-filesystem concept that cannot apply to a hosted service),
    /// cache-seeding (not implemented), port-publishing (the synchronous
    /// <see cref="ISandboxPortPublisher"/> contract cannot mint a usable Daytona
    /// preview URL — the access token requires an async fetch).
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities =>
    [
        SandboxCapabilities.BaselineBake,
        SandboxCapabilities.SuspendResume,
        SandboxCapabilities.Teardown,
    ];

    private DaytonaSandboxOptions ReadOptions() => _readOptions();

    /// <summary>Shared redirect-free HttpClient carrying the API key.</summary>
    private HttpClient Http => _clients.Get();

    private DaytonaApiClient Api => new(Http);

    // ------------------------------------------------------------------
    // Provider lifecycle
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec = spec with { Environment = WithTimingEnvironment(spec.Environment, spec.TimingWorkItemId) };

        var opts = ReadValidatedOptions();
        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("daytona sandbox provider does not support the graphical sandbox flavor.");

        var endpoint = ResolveEndpoint(opts);

        if (spec.RecoveryLease is { } lease)
            return await AdoptRetainedSandboxAsync(spec, opts, endpoint, lease, ct).ConfigureAwait(false);

        var name = GenerateSandboxName(opts.NamePrefix);
        var mounts = ValidateAndPlanMounts(spec);
        var workItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var timingPhase = spec.TimingPhase ?? "work";
        var created = false;

        try
        {
            var request = BuildCreateRequest(spec, opts, name, workItemId);
            await using var provisionTiming = await TimingScope.BeginAsync(
                _timings, workItemId, timingPhase, "daytona.create", log: _log).ConfigureAwait(false);
            var createdDto = await Api.CreateSandboxAsync(endpoint, request, ct).ConfigureAwait(false);
            created = true;
            var sandboxId = createdDto.Id ?? name;

            var sandbox = new DaytonaSandbox(
                name,
                spec,
                _readOptions,
                Api,
                () => ResolveEndpoint(ReadValidatedOptions()),
                () => ToolboxBaseFromDto(createdDto, endpoint, sandboxId),
                _webSocketFactory,
                Http,
                mounts,
                MarkNoLongerActive,
                _clock,
                _log);

            _activeSandboxes[name] = new ActiveSandboxEntry(workItemId, sandbox);
            SandboxLiveCounter.Increment();

            await WaitForStartedAsync(endpoint, name, opts, ct).ConfigureAwait(false);
            await sandbox.RunSetupCommandsAsync(opts.SetupCommands, ct).ConfigureAwait(false);
            await ApplyNetworkPolicyAsync(endpoint, name, spec.Network, ct).ConfigureAwait(false);
            await sandbox.PrepareFilesystemAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Created daytona sandbox {Name} (id {SandboxId})", name, sandboxId);
            return sandbox;
        }
        catch (OperationCanceledException)
        {
            // A cancelled create can still have committed the sandbox
            // service-side — never leave it running on the service's dime.
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
                    new DaytonaApiException(DaytonaFailureKind.Unexpected, "create-cleanup", "delete after failure did not prove removal"),
                    $"create failed and best-effort delete did not prove sandbox {name} was removed: {ex.Message}");
            }
            if (ex is DaytonaApiException apiEx)
                throw ToDeferred(opts, apiEx, $"daytona create failed for {name}");
            throw;
        }
    }

    /// <summary>
    /// Adoption of a retained stopped sandbox via <see cref="SandboxSpec.RecoveryLease"/>.
    /// Ownership is re-verified at the moment of action: provider id, name
    /// prefix, managed label, and the recovery-token hash label must all match.
    /// </summary>
    private async Task<ISandbox> AdoptRetainedSandboxAsync(
        SandboxSpec spec,
        DaytonaSandboxOptions opts,
        DaytonaEndpoint endpoint,
        SandboxRecoveryLease lease,
        CancellationToken ct)
    {
        if (!string.Equals(lease.ProviderId, DaytonaSandboxOptions.ProviderKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recovery lease names provider '{lease.ProviderId}', not 'daytona'; refusing adoption.");
        }
        if (!IsValidManagedName(lease.SandboxId, opts.NamePrefix))
        {
            throw new InvalidOperationException(
                $"Recovery lease names sandbox '{lease.SandboxId}' outside the managed daytona namespace; refusing adoption.");
        }

        var sandbox = await Api.GetSandboxAsync(endpoint, lease.SandboxId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Retained daytona sandbox '{lease.SandboxId}' no longer exists; refusing adoption.");

        var labels = sandbox.Labels ?? new Dictionary<string, string>();
        if (!labels.TryGetValue(DaytonaSandboxOptions.ManagedLabelKey, out var managed)
            || !string.Equals(managed, DaytonaSandboxOptions.ManagedLabelValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Daytona sandbox '{lease.SandboxId}' lacks the managed-ownership label; refusing adoption.");
        }
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Token)));
        if (!labels.TryGetValue(DaytonaSandboxOptions.RecoveryTokenHashLabelKey, out var hash)
            || !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Recovery token does not match the retained daytona sandbox '{lease.SandboxId}'; refusing adoption.");
        }

        try
        {
            await Api.StartSandboxAsync(endpoint, lease.SandboxId, ct).ConfigureAwait(false);
        }
        catch (DaytonaApiException ex) when (ex.Kind == DaytonaFailureKind.Conflict)
        {
            // Already started/starting — adopt waits for the running state below.
        }
        await WaitForStartedAsync(endpoint, lease.SandboxId, opts, ct).ConfigureAwait(false);

        var toolboxBase = ToolboxBaseFromDto(sandbox, endpoint, sandbox.Id ?? lease.SandboxId);
        var handle = new DaytonaSandbox(
            lease.SandboxId,
            spec,
            _readOptions,
            Api,
            () => ResolveEndpoint(ReadValidatedOptions()),
            () => toolboxBase,
            _webSocketFactory,
            Http,
            mounts: [],
            MarkNoLongerActive,
            _clock,
            _log);
        _activeSandboxes[lease.SandboxId] = new ActiveSandboxEntry(spec.TimingWorkItemId.GetValueOrDefault(), handle);
        SandboxLiveCounter.Increment();
        _log.LogInformation("Adopted retained daytona sandbox {Name}", lease.SandboxId);
        return handle;
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var items = await Api.ListManagedSandboxesAsync(endpoint, opts.NamePrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>(items.Count);
        foreach (var item in items)
        {
            var state = item.State ?? string.Empty;
            var retained = item.Labels is not null
                && item.Labels.ContainsKey(DaytonaSandboxOptions.RecoveryTokenHashLabelKey);
            result.Add(new ManagedSandboxInfo(
                item.Name!,
                item.CreatedAt ?? item.UpdatedAt,
                DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(item.Name!),
                HasPreemptMarker: retained,
                IsSuspendLifecycleOrFrozen: state is "paused" or "pausing"));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(name, opts.NamePrefix))
            throw new ArgumentException($"Daytona sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var endpoint = ResolveEndpoint(opts);
        var sandbox = await Api.GetSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
        if (sandbox is null)
            return;
        if (!string.Equals(sandbox.State, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            try { await Api.StopSandboxAsync(endpoint, name, force: true, ct).ConfigureAwait(false); }
            catch (DaytonaApiException ex) when (ex.Kind is DaytonaFailureKind.Conflict or DaytonaFailureKind.NotFound) { }
        }
        await Api.DeleteSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
        MarkNoLongerActive(name);
    }

    // ------------------------------------------------------------------
    // Active-sandbox tracking / live-load reporting
    // ------------------------------------------------------------------

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
    {
        var result = new List<(WorkItemId, IShutdownTeardownSandbox)>(_activeSandboxes.Count);
        foreach (var entry in _activeSandboxes.Values)
        {
            if (entry.WorkItemId.Value == Guid.Empty || !entry.Sandbox.IsTrackedActive)
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
            if (entry.WorkItemId.Value == Guid.Empty || !entry.Sandbox.IsTrackedActive)
                continue;
            result.Add(new ActiveSandboxProgress(entry.WorkItemId, name));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Suspend / resume (host shutdown + startup adoption)
    // ------------------------------------------------------------------

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(name, opts.NamePrefix))
            throw new ArgumentException($"Sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var endpoint = ResolveEndpoint(opts);

        var sandbox = await Api.GetSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
        if (sandbox is null)
            return; // gone — the startup handler clears its bookkeeping
        if (string.Equals(sandbox.State, "started", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            await Api.StartSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
        }
        catch (DaytonaApiException ex) when (ex.Kind is DaytonaFailureKind.Conflict or DaytonaFailureKind.NotFound)
        {
            // already starting or gone — observe below
        }
        await WaitForStartedAsync(endpoint, name, opts, ct).ConfigureAwait(false);
        _log.LogInformation("Resumed daytona sandbox {Name}", name);
    }

    /// <summary>
    /// Waits out the adopted agent by watching the exec wrapper's
    /// <c>&lt;log&gt;.exit</c> marker through the toolbox files endpoint, and
    /// streams newly appended log bytes to <paramref name="logSink"/>.
    /// </summary>
    public async Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName, string agentLogPath, Action<string>? logSink, TimeSpan? deadline, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(vmName, opts.NamePrefix))
            throw new ArgumentException($"Sandbox name '{vmName}' is not a managed codeybox sandbox name.", nameof(vmName));
        if (!IsValidAgentLogPath(agentLogPath))
            throw new ArgumentException($"Agent log path '{agentLogPath}' is outside the agent-log anchor.", nameof(agentLogPath));

        var endpoint = ResolveEndpoint(opts);
        var sandbox = await Api.GetSandboxAsync(endpoint, vmName, ct).ConfigureAwait(false);
        if (sandbox is null)
            return null;
        var toolbox = new DaytonaToolboxClient(Http, endpoint, ToolboxBaseFromDto(sandbox, endpoint, sandbox.Id ?? vmName));
        var exitMarker = agentLogPath + ".exit";
        var logOffset = 0L;
        var stopAt = deadline is { } d ? _clock.GetUtcNow() + d : (DateTimeOffset?)null;

        while (true)
        {
            if (ct.IsCancellationRequested)
                return null;
            if (stopAt is { } end && _clock.GetUtcNow() >= end)
                return null;

            if (logSink is not null)
                logOffset = await TryTailLogAsync(toolbox, agentLogPath, logSink, logOffset, opts, ct).ConfigureAwait(false);

            var exitInfo = await toolbox.GetFileInfoAsync(exitMarker, ct).ConfigureAwait(false);
            if (exitInfo is not null)
            {
                var markerBytes = await toolbox.DownloadFileAsync(exitMarker, ExitMarkerMaxBytes, ct).ConfigureAwait(false);
                if (markerBytes is not null &&
                    int.TryParse(Encoding.UTF8.GetString(markerBytes).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
                {
                    return exitCode;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(opts.PollIntervalMilliseconds), _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task<long> TryTailLogAsync(
        DaytonaToolboxClient toolbox, string agentLogPath, Action<string> logSink, long offset,
        DaytonaSandboxOptions opts, CancellationToken ct)
    {
        try
        {
            var bytes = await toolbox.DownloadFileAsync(agentLogPath, opts.MaxFileSyncBytes, ct).ConfigureAwait(false);
            if (bytes is null || bytes.LongLength <= offset)
                return offset;
            logSink(Encoding.UTF8.GetString(bytes, (int)offset, (int)(bytes.LongLength - offset)));
            return bytes.LongLength;
        }
        catch (DaytonaApiException ex)
        {
            _log.LogDebug(ex, "Skipped daytona agent-log tail read at offset {Offset}", offset);
            return offset; // log may not exist yet — the exit marker drives completion
        }
    }

    /// <summary>
    /// Pushes the resumed sandbox's work tree to the checkpoint ref via an
    /// in-sandbox git pipeline — the same ref shape the built-in providers use
    /// (<c>refs/heads/codeybox/preempt/&lt;id&gt;</c>).
    /// </summary>
    public async Task<bool> PushSuspendedVmCheckpointRefAsync(
        string vmName, string workingDir, string refName, string commitMessage, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidManagedName(vmName, opts.NamePrefix))
            throw new ArgumentException($"Sandbox name '{vmName}' is not a managed codeybox sandbox name.", nameof(vmName));
        if (!IsValidAbsolutePath(workingDir))
            throw new ArgumentException($"Working directory '{workingDir}' contains invalid characters.", nameof(workingDir));
        if (!IsValidPreemptCheckpointRef(refName))
            throw new ArgumentException($"Ref '{refName}' is not a permitted preempt-checkpoint ref shape.", nameof(refName));
        if (!IsValidCheckpointCommitMessage(commitMessage))
            throw new ArgumentException("Commit message contains invalid characters.", nameof(commitMessage));

        var endpoint = ResolveEndpoint(opts);
        var sandbox = await Api.GetSandboxAsync(endpoint, vmName, ct).ConfigureAwait(false);
        if (sandbox is null)
            return false;

        // Strip and positively guard provider-scratchpad paths before pushing —
        // they are private and never belong in a git ref.
        var script = $$"""
            set -euo pipefail
            cd {{DaytonaSandbox.ShellSingleQuote(workingDir)}}
            git add -A
            git rm -r --cached --ignore-unmatch -- ':(glob).codeybox/preempt-scratchpad*' ':(glob).codeybox/preempt-scratchpad*/**' ':(glob).codeybox/resume-scratchpad*' ':(glob).codeybox/resume-scratchpad*/**' 2>/dev/null || true
            test -z "$(git ls-files --cached -- ':(glob).codeybox/preempt-scratchpad*' ':(glob).codeybox/resume-scratchpad*')"
            git commit --allow-empty -m {{DaytonaSandbox.ShellSingleQuote(commitMessage)}}
            git push origin HEAD:{{refName}}
            """;

        try
        {
            var result = await ExecOnNamedSandboxAsync(endpoint, sandbox, vmName,
                ["sh", "-c", script], workingDir: "/", ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _log.LogWarning(
                    "PushSuspendedVmCheckpointRefAsync({VmName}, {RefName}): in-sandbox git push failed (exit {ExitCode}): {Stderr}",
                    vmName, refName, result.ExitCode, DaytonaTextUtil.Tail(result.Stderr));
                return false;
            }
            _log.LogInformation("Pushed adopted daytona sandbox {Name} HEAD to checkpoint {RefName}", vmName, refName);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PushSuspendedVmCheckpointRefAsync({VmName}, {RefName}) threw; treating as push failure", vmName, refName);
            return false;
        }
    }

    /// <summary>
    /// Startup reconciliation: managed sandboxes in suspend-lifecycle or wedged
    /// states that no live orchestrator mapping claims are stopped and deleted.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var managed = await Api.ListManagedSandboxesAsync(endpoint, opts.NamePrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        var unrecoverable = new List<string>();
        foreach (var item in managed)
        {
            var name = item.Name!;
            if (liveSuspendedNames.Contains(name))
                continue;
            var state = item.State ?? string.Empty;
            if (state is not ("paused" or "pausing" or "stopped" or "stopping" or "error" or "archiving" or "archived"))
                continue;
            try
            {
                if (state is "paused" or "pausing" or "stopping")
                {
                    try { await Api.StopSandboxAsync(endpoint, name, force: true, ct).ConfigureAwait(false); }
                    catch (DaytonaApiException ex) when (ex.Kind is DaytonaFailureKind.Conflict or DaytonaFailureKind.NotFound) { }
                }
                await Api.DeleteSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
                _log.LogInformation("Reconciled orphaned daytona sandbox {Name} (state={State})", name, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not reconcile orphaned daytona sandbox {Name} (state={State})", name, state);
                unrecoverable.Add(name);
            }
        }
        return unrecoverable;
    }

    // ------------------------------------------------------------------
    // Baseline images (Daytona snapshots)
    // ------------------------------------------------------------------

    /// <summary>
    /// Deterministic baseline name for (profile, flavor): a content hash over
    /// the bake inputs so a config change yields a fresh image rather than a
    /// silent drift.
    /// </summary>
    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        var opts = ReadOptions();
        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.BaselineSourceImage))
            return null;
        var hashInput = string.Join('\n',
            opts.BaselineSourceImage,
            opts.SandboxClass,
            profileName ?? string.Empty,
            flavor.ToString(),
            string.Join("\x1f", opts.SetupCommands));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));
        return opts.BaselineSnapshotPrefix + hash[..16].ToLowerInvariant();
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var endpoint = ResolveEndpoint(opts);
        var snapshots = await Api.ListSnapshotsAsync(endpoint, opts.BaselineSnapshotPrefix, opts.MaxListPages, ct).ConfigureAwait(false);
        return snapshots
            .Where(s => s.Name is not null && s.Name.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal))
            .Select(s => new BaselineImageInfo(s.Name!, s.CreatedAt, DiskBytes: null))
            .ToList();
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (!IsValidBaselineName(name, opts))
            throw new ArgumentException($"Baseline snapshot name '{name}' is not a managed codeybox baseline.", nameof(name));
        var endpoint = ResolveEndpoint(opts);
        var snapshot = await Api.GetSnapshotAsync(endpoint, name, ct).ConfigureAwait(false);
        if (snapshot is null)
            return;
        await Api.DeleteSnapshotAsync(endpoint, snapshot.Id ?? name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures a baseline snapshot exists: look up by name, then bake by
    /// creating a scratch sandbox from the source image, running the
    /// provisioning commands, and snapshotting it. The bake sandbox is always
    /// deleted afterward.
    /// </summary>
    public async Task<string?> EnsureBaselineImageAsync(
        string profileName, SandboxProfileFlavor flavor, string? pinnedBaselineRef, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var name = pinnedBaselineRef ?? ResolveBaselineRef(profileName, flavor);
        if (name is null)
            return null;
        var endpoint = ResolveEndpoint(opts);

        var existing = await Api.GetSnapshotAsync(endpoint, name, ct).ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.State, "active", StringComparison.OrdinalIgnoreCase))
            return name;
        if (existing is not null && existing.State is "building" or "pending" or "pulling" or "snapshotting")
        {
            await WaitForSnapshotActiveAsync(endpoint, existing.Id ?? name, opts, ct).ConfigureAwait(false);
            return name;
        }

        var bakeName = opts.NamePrefix + "bake-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var create = new DaytonaCreateSandboxRequest
            {
                Name = bakeName,
                Snapshot = opts.BaselineSourceImage,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DaytonaSandboxOptions.ManagedLabelKey] = DaytonaSandboxOptions.ManagedLabelValue,
                },
                AutoDeleteInterval = opts.AutoDeleteIntervalMinutes,
                AutoStopInterval = opts.AutoStopIntervalMinutes,
                Public = false,
                SandboxClass = string.IsNullOrWhiteSpace(opts.SandboxClass) ? null : opts.SandboxClass,
            };
            var dto = await Api.CreateSandboxAsync(endpoint, create, ct).ConfigureAwait(false);
            var bakeId = dto.Id ?? bakeName;
            await WaitForStartedAsync(endpoint, bakeName, opts, ct).ConfigureAwait(false);

            var bakeSandbox = new DaytonaSandbox(
                bakeName,
                new SandboxSpec { ImageReference = opts.BaselineSourceImage, WorkingDirectory = "/" },
                _readOptions,
                Api,
                () => ResolveEndpoint(ReadValidatedOptions()),
                () => ToolboxBaseFromDto(dto, endpoint, bakeId),
                _webSocketFactory,
                Http,
                mounts: [],
                _ => { },
                _clock,
                _log);
            await bakeSandbox.RunSetupCommandsAsync(opts.SetupCommands, ct).ConfigureAwait(false);

            await Api.CreateSnapshotFromSandboxAsync(endpoint, bakeId, name, includeMemory: false, ct).ConfigureAwait(false);
        }
        finally
        {
            try { await Api.DeleteSandboxAsync(endpoint, bakeName, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete daytona bake sandbox {Name}", bakeName); }
        }
        await WaitForSnapshotActiveAsync(endpoint, name, opts, ct).ConfigureAwait(false);
        return name;
    }

    private async Task WaitForSnapshotActiveAsync(
        DaytonaEndpoint endpoint, string idOrName, DaytonaSandboxOptions opts, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.BaselineBakeTimeoutSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await Api.GetSnapshotAsync(endpoint, idOrName, ct).ConfigureAwait(false)
                ?? throw new DaytonaApiException(DaytonaFailureKind.NotFound, "wait snapshot", $"snapshot {idOrName} disappeared");
            var state = snapshot.State ?? string.Empty;
            if (string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
                return;
            if (state is "error" or "build_failed")
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.ServerError, "wait snapshot",
                    $"snapshot {idOrName} failed to build: {snapshot.ErrorReason ?? "no reason reported"}");
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.ServerError, "wait snapshot",
                    $"snapshot {idOrName} did not reach 'active' within {opts.BaselineBakeTimeoutSeconds}s (state={state})");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(opts.PollIntervalMilliseconds), _clock, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /// <summary>Runs argv inside an arbitrary managed sandbox via a one-shot toolbox session.</summary>
    private async Task<SandboxExecResult> ExecOnNamedSandboxAsync(
        DaytonaEndpoint endpoint, DaytonaSandboxDto sandbox, string name,
        IReadOnlyList<string> argv, string workingDir, CancellationToken ct)
    {
        var toolboxBase = ToolboxBaseFromDto(sandbox, endpoint, sandbox.Id ?? name);
        var handle = new DaytonaSandbox(
            name,
            new SandboxSpec { ImageReference = name, WorkingDirectory = workingDir },
            _readOptions,
            Api,
            () => ResolveEndpoint(ReadValidatedOptions()),
            () => toolboxBase,
            _webSocketFactory,
            Http,
            mounts: [],
            _ => { },
            _clock,
            _log);
        return await handle.ExecInternalAsync(
            new SandboxExec { Argv = argv, WorkingDirectory = workingDir },
            includeSpecEnvironment: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Toolbox base URI for one sandbox: the service-returned
    /// <c>toolboxProxyUrl</c> when present (untrusted — must be absolute
    /// http(s)), else the configured proxy base; either way the sandbox id is
    /// appended as one escaped path segment.
    /// </summary>
    private static Uri ToolboxBaseFromDto(DaytonaSandboxDto dto, DaytonaEndpoint endpoint, string sandboxId)
    {
        var proxy = DaytonaApiClient.TryParseAbsoluteUrl(dto.ToolboxProxyUrl, "sandbox toolboxProxyUrl")
            ?? endpoint.ToolboxProxyBaseUri;
        return new Uri(proxy, Uri.EscapeDataString(sandboxId) + "/");
    }

    private async Task WaitForStartedAsync(DaytonaEndpoint endpoint, string name, DaytonaSandboxOptions opts, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sandbox = await Api.GetSandboxAsync(endpoint, name, ct).ConfigureAwait(false);
            if (sandbox is null)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.NotFound, "wait sandbox start",
                    $"sandbox {name} disappeared during provisioning");
            }
            var state = sandbox.State ?? string.Empty;
            if (string.Equals(state, "started", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.ServerError, "wait sandbox start",
                    $"sandbox {name} entered error state: {sandbox.ErrorReason ?? "no reason reported"}");
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new DaytonaApiException(
                    DaytonaFailureKind.ServerError, "wait sandbox start",
                    $"sandbox {name} did not reach 'started' within {opts.ReadyTimeoutSeconds}s (state={state})");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(opts.PollIntervalMilliseconds), _clock, ct).ConfigureAwait(false);
        }
    }

    private static DaytonaCreateSandboxRequest BuildCreateRequest(
        SandboxSpec spec, DaytonaSandboxOptions opts, string name, WorkItemId workItemId)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DaytonaSandboxOptions.ManagedLabelKey] = DaytonaSandboxOptions.ManagedLabelValue,
        };
        if (workItemId.Value != Guid.Empty)
            labels[DaytonaSandboxOptions.WorkItemLabelKey] = workItemId.Value.ToString("N");

        var snapshot = !string.IsNullOrWhiteSpace(spec.BaselineImageRef)
            ? spec.BaselineImageRef
            : !string.IsNullOrWhiteSpace(spec.ImageReference)
                ? spec.ImageReference
                : opts.DefaultSnapshot;
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            throw new InvalidOperationException(
                "daytona provider needs a snapshot to create from: set CodeyBox:SandboxImageReference " +
                "or CodeyBox:Plugins:codeybox.daytona-sandbox:DefaultSnapshot to a snapshot Daytona can boot.");
        }

        // No egress policy on create: setup commands run first with default
        // egress, then ApplyNetworkPolicyAsync locks it down — the bake-then-
        // lock ordering (a host profile can never be applied to this kind
        // anyway: placement refuses profiled work on NotEnforced providers).
        return new DaytonaCreateSandboxRequest
        {
            Name = name,
            Snapshot = snapshot,
            Labels = labels,
            Public = false,
            Target = string.IsNullOrWhiteSpace(opts.Target) ? null : opts.Target,
            // The requested isolation class travels with the create: IsolationLevel
            // only reports DedicatedKernel when this field is configured.
            SandboxClass = string.IsNullOrWhiteSpace(opts.SandboxClass) ? null : opts.SandboxClass,
            Cpu = spec.Limits.CpuCount,
            Memory = ToWholeGiB(spec.Limits.MemoryBytes),
            Disk = ToWholeGiB(spec.Limits.DiskBytes),
            AutoDeleteInterval = opts.AutoDeleteIntervalMinutes,
            AutoStopInterval = opts.AutoStopIntervalMinutes,
        };
    }

    /// <summary>
    /// Applies the work item's egress intent as a provider-side domain
    /// allowlist AFTER setup commands run (so provisioning still sees egress).
    /// This is defence-in-depth ONLY: it is a service-side control on
    /// infrastructure CodeyBox does not own — the host still classifies this
    /// kind NotEnforced. A named network profile implies host-enforced egress
    /// (placement already refuses those on this kind; this is the in-provider
    /// backstop), and an empty allowlist blocks all egress.
    /// </summary>
    private async Task ApplyNetworkPolicyAsync(
        DaytonaEndpoint endpoint, string name, SandboxNetworkPolicy network, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(network.ProfileName))
        {
            throw new NotSupportedException(
                $"Daytona is a hosted provider classified NotEnforced; network profile " +
                $"'{network.ProfileName}' requires host-side nftables enforcement that cannot " +
                "exist on infrastructure CodeyBox does not control. Select an enforced provider kind.");
        }

        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in network.AllowedHosts)
            AddDomain(domains, host);
        if (!string.IsNullOrWhiteSpace(network.HostGitEndpoint))
            AddDomain(domains, StripPort(network.HostGitEndpoint));

        var settings = domains.Count == 0
            ? new DaytonaNetworkSettings { NetworkBlockAll = true }
            : new DaytonaNetworkSettings { DomainAllowList = domains.ToList(), NetworkBlockAll = false };
        await Api.SetNetworkSettingsAsync(endpoint, name, settings, ct).ConfigureAwait(false);
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
            throw new ArgumentException($"Invalid daytona network policy domain: '{value}'");
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

    private IReadOnlyList<DaytonaMountPlan> ValidateAndPlanMounts(SandboxSpec spec)
    {
        var result = new List<DaytonaMountPlan>();
        foreach (var mount in spec.Mounts)
        {
            if (!mount.SandboxPath.StartsWith("/", StringComparison.Ordinal))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");

            if (IsCredentialPath(mount.SandboxPath))
            {
                if (mount.Tmpfs && mount.HostPath is null)
                    continue; // bare credentials tmpfs placeholder — nothing to stage
                throw new NotSupportedException(
                    "daytona hosted sandboxes do not expose tmpfs mounts; refusing credential mount " +
                    $"{mount.SandboxPath} because it would persist on service-side storage CodeyBox does not control. " +
                    "Use credential environment variables for daytona-backed sandboxes.");
            }

            if (mount.Tmpfs)
            {
                // Non-secret scratch (e.g. the audit /audit mount): Daytona has
                // no tmpfs, so downgrade to a persistent directory rather than
                // throwing — the fail-closed contract covers credentials only.
                _log.LogWarning(
                    "Daytona does not expose tmpfs mounts; downgrading scratch mount {Path} to a persistent directory " +
                    "(contents land on the service-side sandbox filesystem).",
                    mount.SandboxPath);
                result.Add(new DaytonaMountPlan(mount.SandboxPath, HostPath: null, ReadOnly: false, IsPersistentTmpfsDirectory: true));
                continue;
            }

            if (mount.HostPath is null)
                continue;
            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(hostPath, $"daytona mount source path does not exist: {hostPath}");
            }
            result.Add(new DaytonaMountPlan(mount.SandboxPath, hostPath, mount.ReadOnly, IsPersistentTmpfsDirectory: false));
        }
        return result;
    }

    private static bool IsCredentialPath(string sandboxPath)
    {
        var trimmed = sandboxPath.TrimEnd('/');
        return trimmed.Equals(DaytonaSandboxConventions.CredentialsDir, StringComparison.Ordinal)
            || trimmed.StartsWith(DaytonaSandboxConventions.CredentialsDir + "/", StringComparison.Ordinal);
    }

    private DaytonaSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The daytona sandbox provider is disabled. Enable it via " +
                "CodeyBox:Plugins:codeybox.daytona-sandbox:Enabled=true plus the plugin allowlist.");
        }
        if (!Uri.TryCreate(opts.ApiUrl, UriKind.Absolute, out var apiUri)
            || (apiUri.Scheme != Uri.UriSchemeHttps && apiUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.daytona-sandbox:ApiUrl must be an absolute http(s) URL.");
        if (apiUri.Scheme == Uri.UriSchemeHttp && !opts.AllowUnsafeHttp)
            throw new InvalidOperationException(
                "CodeyBox:Plugins:codeybox.daytona-sandbox:ApiUrl must use https://. " +
                "Set AllowUnsafeHttp=true only for local tests.");
        if (!Uri.TryCreate(opts.ToolboxProxyUrl, UriKind.Absolute, out var toolboxUri)
            || (toolboxUri.Scheme != Uri.UriSchemeHttps && toolboxUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.daytona-sandbox:ToolboxProxyUrl must be an absolute http(s) URL.");
        if (toolboxUri.Scheme == Uri.UriSchemeHttp && !opts.AllowUnsafeHttp)
            throw new InvalidOperationException(
                "CodeyBox:Plugins:codeybox.daytona-sandbox:ToolboxProxyUrl must use https://. " +
                "Set AllowUnsafeHttp=true only for local tests.");
        if (string.IsNullOrWhiteSpace(opts.ApiKeyEnvVar))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.daytona-sandbox:ApiKeyEnvVar must be set.");
        if (string.IsNullOrWhiteSpace(opts.NamePrefix) || !opts.NamePrefix.All(IsNameChar))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.daytona-sandbox:NamePrefix must contain only lowercase letters, numbers, and hyphens.");
        if (!opts.NamePrefix.StartsWith(DefaultNamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:codeybox.daytona-sandbox:NamePrefix must start with '{DefaultNamePrefix}' so leak reaping can identify managed sandboxes.");
        if (!string.IsNullOrWhiteSpace(opts.BaselineSnapshotPrefix) && !opts.BaselineSnapshotPrefix.All(IsNameChar))
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.daytona-sandbox:BaselineSnapshotPrefix must contain only lowercase letters, numbers, and hyphens.");
        if (opts.MaxListPages <= 0)
            throw new InvalidOperationException("CodeyBox:Plugins:codeybox.daytona-sandbox:MaxListPages must be greater than zero.");
        if (opts.MaxSyncArchiveBase64Bytes <= 0 || opts.MaxSyncArchiveBytes <= 0 ||
            opts.MaxSyncArchiveExpandedBytes <= 0 || opts.MaxSyncArchiveEntries <= 0 ||
            opts.MaxFileSyncBase64Bytes <= 0 || opts.MaxFileSyncBytes <= 0 || opts.MaxExecInputBytes <= 0)
            throw new InvalidOperationException("Daytona sync/exec size limits must all be greater than zero.");
        if (opts.AllowUnsafeHttp)
            _log.LogWarning("The daytona sandbox provider allows cleartext http: the API key rides every request, so enable AllowUnsafeHttp only for local tests.");
        return opts;
    }

    /// <summary>
    /// Resolves the API key from the credential chain (process environment) at
    /// call time — never from the options record, so a config file can never
    /// carry the secret and rotation propagates without a restart.
    /// </summary>
    private DaytonaEndpoint ResolveEndpoint(DaytonaSandboxOptions opts)
    {
        var apiKey = _environment(opts.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Daytona API key environment variable '{opts.ApiKeyEnvVar}' is not set. " +
                "Provision it via the host credential chain (vault agent, container secrets) — never in configuration files.");
        }
        var apiUri = new Uri(opts.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var toolboxUri = new Uri(opts.ToolboxProxyUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new DaytonaEndpoint(apiUri, toolboxUri, apiKey.Trim(),
            string.IsNullOrWhiteSpace(opts.OrganizationId) ? null : opts.OrganizationId);
    }

    /// <summary>
    /// Maps a service failure onto a provisioning deferral: the service said
    /// no, which is an infrastructure signal — never a verdict on the work
    /// item's diff. Auth/quota failures get a longer recheck so an operator can
    /// fix credentials/capacity; throttling honours Retry-After.
    /// </summary>
    private SandboxProvisioningDeferredException ToDeferred(
        DaytonaSandboxOptions opts, DaytonaApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            DaytonaFailureKind.Unauthorized or DaytonaFailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            DaytonaFailureKind.QuotaExhausted =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            DaytonaFailureKind.Throttled =>
                ("throttled", ex.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : baseRecheck),
            DaytonaFailureKind.Conflict or DaytonaFailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            DaytonaFailureKind.Unreachable => ("unreachable", baseRecheck),
            _ => ("server-error", baseRecheck),
        };
        return new SandboxProvisioningDeferredException(
            Name, "create", errorClass, $"{context}: {ex.Message}", recheck);
    }

    private static TimeSpan MaxOf(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private async Task<bool> TryDeleteAfterCreateFailureAsync(DaytonaEndpoint endpoint, string name)
    {
        try
        {
            await Api.DeleteSandboxAsync(endpoint, name, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete daytona sandbox {Name} after create failure", name);
            return false;
        }
    }

    private void MarkNoLongerActive(DaytonaSandbox sandbox) => MarkNoLongerActive(sandbox.Id);

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

    private static bool IsValidBaselineName(string name, DaytonaSandboxOptions opts) =>
        !string.IsNullOrWhiteSpace(name)
        && name.StartsWith(opts.BaselineSnapshotPrefix, StringComparison.Ordinal)
        && name.All(c => IsNameChar(c) || c is '.' or '_' or '/' or ':' or '@');

    /// <summary>
    /// Restricts the working-directory argument so a tampered recovery record
    /// cannot smuggle shell metacharacters through the checkpoint-push script.
    /// </summary>
    internal static bool IsValidAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] != '/') return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var ch in path)
        {
            if (ch < 0x20 || ch == 0x7f) return false;
            if (ch is '\'' or '"' or '`' or '$' or '\\' or '\n' or '\r' or '\0') return false;
        }
        return true;
    }

    /// <summary>Enforces the <c>refs/heads/codeybox/preempt/&lt;id&gt;</c> shape used for preempt checkpoints.</summary>
    internal static bool IsValidPreemptCheckpointRef(string refName)
    {
        const string prefix = "refs/heads/codeybox/preempt/";
        if (string.IsNullOrEmpty(refName)) return false;
        if (!refName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = refName[prefix.Length..];
        if (suffix.Length == 0) return false;
        foreach (var ch in suffix)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-';
            if (!ok) return false;
        }
        return true;
    }

    internal static bool IsValidCheckpointCommitMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (message.Length > 1024) return false;
        foreach (var ch in message)
        {
            if (ch == '\0' || ch == '\r') return false;
            if (ch < 0x20 && ch != '\n' && ch != '\t') return false;
            if (ch == 0x7f) return false;
        }
        return true;
    }

    /// <summary>Anchors the adopted-agent log tail under the conventional agent-log dir.</summary>
    internal static bool IsValidAgentLogPath(string path)
    {
        if (!IsValidAbsolutePath(path)) return false;
        var anchor = DaytonaSandboxConventions.AgentLogDir + "/";
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
            // watchdog can attribute in-sandbox work to the owning item.
            ["CODEYBOX_WORK_ITEM_ID"] = id.ToString(),
        };
        return env;
    }

    private static int? ToWholeGiB(long? bytes) =>
        bytes is { } b && b > 0 ? (int)Math.Ceiling(b / (double)(1024L * 1024 * 1024)) : null;

    public void Dispose() => _clients.Dispose();

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, DaytonaSandbox Sandbox);
}
