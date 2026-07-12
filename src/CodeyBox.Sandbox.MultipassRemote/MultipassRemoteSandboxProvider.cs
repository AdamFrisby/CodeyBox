using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// Sandbox provider that drives <c>multipass</c> on a REMOTE host over SSH.
/// CHEAP-PATH distributed VMs: a single orchestrator brain keeping all state
/// in its local SQLite, with VM execution placed across one or more remote
/// executor machines.
///
/// <para><b>Architecture.</b> The orchestrator runs locally; the multipass
/// daemon and every per-work-item VM live on the remote host. Every
/// multipass command (<c>launch</c>, <c>exec</c>, <c>mount</c>,
/// <c>stop</c>, <c>delete</c>, <c>info</c>, <c>list</c>) is issued via an
/// <see cref="IRemoteHostTransport"/> — the OpenSSH-client implementation
/// streams stdout / stderr back chunk-by-chunk so AgentStreamCapture on the
/// orchestrator host sees the agent's output in real time.</para>
///
/// <para><b>Bind mounts.</b> Host-side bind-mount sources (e.g. the
/// orchestrator's per-item bare git repo) are staged to a per-sandbox
/// directory under <see cref="MultipassRemoteSandboxOptions.RemoteStagingRoot"/>
/// via <c>tar | ssh tar</c>, then attached to the remote VM with
/// <c>multipass mount</c>. Writable mounts are synced BACK on disposal
/// (host ← remote staging) so the merge phase on the orchestrator host sees
/// commits the in-VM agent pushed. Tmpfs mounts get an empty per-sandbox
/// directory on the remote host. See "Why staging" below.</para>
///
/// <para><b>Git remotes.</b> The orchestrator's <see cref="IGitHost"/>
/// supplies a <c>SandboxRepositoryAccess.CloneUrlInsideSandbox</c> of
/// <c>/repo</c>; for the remote provider, the per-item bare repo is staged
/// to the remote staging dir (read/write) and the VM clones from there
/// exactly like local multipass. After the work phase, the bare repo is
/// rsync'd back to the host so the merge phase can read it. This keeps the
/// orchestrator-host bare-repos directory authoritative without needing a
/// remote git-daemon or SSH reverse tunnel in this iteration.</para>
///
/// <para><b>Why staging, not direct host paths in <c>multipass mount</c>.</b>
/// The remote multipass daemon can only mount paths it can see on its own
/// filesystem (and, when snap-confined, paths inside
/// <c>~/snap/multipass/common</c>). The orchestrator's local
/// <c>/var/lib/codeybox/...</c> is meaningless on the remote — so the
/// provider tars the host source over, drops it under
/// <c>RemoteStagingRoot/&lt;vm&gt;/fs/...</c>, and points
/// <c>multipass mount</c> at the staged copy. The remote staging
/// directories are 0700 to the SSH user.</para>
///
/// <para><b>Failure classification.</b> An SSH transport failure
/// (connection refused, key rejected, network partition) surfaces as a
/// <see cref="RemoteSshTransportException"/>, which the orchestrator maps to
/// a sandbox-level failure — recoverable: re-pickup the work item. A
/// remote command running and returning non-zero is propagated as a normal
/// <see cref="SandboxExecResult"/> with that exit code, exactly like local
/// multipass.</para>
///
/// <para><b>Host pool.</b> Placement is capacity-aware across configured
/// executor hosts. It respects per-host caps, cordon/drain, configured
/// health, runtime SSH health backoff, network-profile allowlists, and the
/// orchestrator's global worker/sandbox admission gates.</para>
///
/// <para><b>Scope.</b> This provider supports cloning from an operator-baked
/// remote Multipass baseline when <see cref="SandboxSpec.BaselineImageRef"/>
/// is set. It intentionally does NOT implement: baseline image bake,
/// suspend/resume, shutdown teardown, disk-guard preflight, package-cache
/// seeding. Those host-side concerns
/// either don't translate cleanly to a remote host without further design
/// (suspend/resume needs network-stable VM identity across orchestrator
/// restarts; baselines need a per-remote-host cache) or remain
/// operator-tuning concerns. The provider implements the
/// <see cref="ISandboxProvider"/> + <see cref="ISandbox"/> contract with
/// the lifecycle (create / exec / stop / dispose / list / leak-dispose)
/// that the orchestrator needs.
/// </para>
/// </summary>
public sealed class MultipassRemoteSandboxProvider : ISandboxProvider, IActiveSandboxProvider, IActiveSandboxProgressProvider, ISandboxHostPoolSnapshot
{
    private const string PurposeMarkerFile = ".codeybox-purpose";

    private readonly Func<MultipassRemoteSandboxOptions> _optsAccessor;
    private readonly Func<MultipassRemoteSandboxOptions, IRemoteHostTransport> _transportFactory;
    private readonly ILogger<MultipassRemoteSandboxProvider> _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _heavyMultipassGates =
        new(StringComparer.Ordinal);
    private readonly object _placementLock = new();
    private readonly Dictionary<string, int> _hostReservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RemoteSandboxIdentity, HostReservation> _retainedReservations = new();
    private readonly ConcurrentDictionary<string, RemoteHostUnhealthyState> _runtimeUnhealthy =
        new(StringComparer.Ordinal);

    // Tracks sandboxes still owned by a currently-running phase in this process.
    // Used by ListAllManagedAsync to compute ManagedSandboxInfo.IsTrackedActive.
    private readonly ConcurrentDictionary<RemoteSandboxIdentity, MultipassRemoteSandbox> _active = new();

    public MultipassRemoteSandboxProvider(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        ILogger<MultipassRemoteSandboxProvider> log)
        : this(() => opts, transport, log)
    { }

    public MultipassRemoteSandboxProvider(
        Func<MultipassRemoteSandboxOptions> optsAccessor,
        IRemoteHostTransport transport,
        ILogger<MultipassRemoteSandboxProvider> log)
        : this(optsAccessor, _ => transport, log)
    { }

    public MultipassRemoteSandboxProvider(
        Func<MultipassRemoteSandboxOptions> optsAccessor,
        Func<MultipassRemoteSandboxOptions, IRemoteHostTransport> transportFactory,
        ILogger<MultipassRemoteSandboxProvider> log)
    {
        _optsAccessor = optsAccessor;
        _transportFactory = transportFactory;
        _log = log;
    }

    public string Name => "multipass-remote";

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        spec = SandboxConventions.WithTimingEnvironment(spec);
        var skippedHosts = new HashSet<string>(StringComparer.Ordinal);
        Exception? lastHostFailure = null;

        while (true)
        {
            HostReservation reservation;
            try
            {
                reservation = await ReserveHostAsync(spec, skippedHosts, ct).ConfigureAwait(false);
            }
            catch (SandboxProvisioningDeferredException ex) when (lastHostFailure is not null)
            {
                throw new SandboxProvisioningDeferredException(
                    ex.Provider,
                    ex.Operation,
                    "all-hosts-unavailable",
                    $"{ex.Detail}; last host failure: {lastHostFailure.Message}",
                    ex.RecheckIn,
                    innerException: lastHostFailure);
            }

            var opts = reservation.HostOptions;
            IRemoteHostTransport? transport = null;
            string? vmName = null;
            string? remoteSandboxRoot = null;
            try
            {
                transport = _transportFactory(opts);
                vmName = RemoteMultipassVmNames.NewVmName(opts);
                remoteSandboxRoot = RemoteMultipassVmNames.BuildRemoteSandboxRoot(opts.RemoteStagingRoot, vmName);
                var remoteFsRoot = JoinRemote(remoteSandboxRoot, "fs");
                var sandbox = await CreateOnReservedHostAsync(
                    spec,
                    opts,
                    transport,
                    vmName,
                    remoteSandboxRoot,
                    remoteFsRoot,
                    reservation,
                    ct).ConfigureAwait(false);

                MarkRuntimeHealthy(opts.HostId);
                return sandbox;
            }
            catch (RemoteSshTransportException ex) when (ex.IsHostTransportFailure)
            {
                lastHostFailure = ex;
                skippedHosts.Add(opts.HostId);
                MarkRuntimeUnhealthy(opts, ex);
                await RollBackCreateFailureAsync(
                    opts,
                    reservation,
                    transport,
                    vmName,
                    remoteSandboxRoot,
                    ex,
                    ct,
                    throwOnCleanupFailure: true).ConfigureAwait(false);

                _log.LogWarning(
                    ex,
                    "Remote multipass host {HostId} transport failed during CreateAsync; retrying placement on another eligible host",
                    opts.HostId);
            }
            catch (RemoteSshTransportException ex)
            {
                await RollBackCreateFailureAsync(opts, reservation, transport, vmName, remoteSandboxRoot, ex, ct).ConfigureAwait(false);
                _log.LogWarning(
                    ex,
                    "Remote multipass host {HostId} failed request-specific staging during CreateAsync; failing the sandbox request",
                    opts.HostId);
                throw;
            }
            catch (RemoteHostProvisioningException ex) when (ex.IsHostRuntimeFailure)
            {
                lastHostFailure = ex;
                skippedHosts.Add(opts.HostId);
                MarkRuntimeUnhealthy(opts, ex);
                await RollBackCreateFailureAsync(
                    opts,
                    reservation,
                    transport,
                    vmName,
                    remoteSandboxRoot,
                    ex,
                    ct,
                    throwOnCleanupFailure: true).ConfigureAwait(false);

                _log.LogWarning(
                    ex,
                    "Remote multipass host {HostId} failed host-level provisioning during CreateAsync; retrying placement on another eligible host",
                    opts.HostId);
            }
            catch (RemoteHostProvisioningException ex)
            {
                await RollBackCreateFailureAsync(opts, reservation, transport, vmName, remoteSandboxRoot, ex, ct).ConfigureAwait(false);
                _log.LogWarning(
                    ex,
                    "Remote multipass host {HostId} failed request-specific provisioning during CreateAsync; failing the sandbox request",
                    opts.HostId);
                throw;
            }
            catch (Exception ex)
            {
                await RollBackCreateFailureAsync(opts, reservation, transport, vmName, remoteSandboxRoot, ex, ct).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task RollBackCreateFailureAsync(
        MultipassRemoteSandboxOptions opts,
        HostReservation reservation,
        IRemoteHostTransport? transport,
        string? vmName,
        string? remoteSandboxRoot,
        Exception? cause,
        CancellationToken ct,
        bool throwOnCleanupFailure = true)
    {
        if (transport is null || string.IsNullOrWhiteSpace(vmName) || string.IsNullOrWhiteSpace(remoteSandboxRoot))
        {
            reservation.Dispose();
            return;
        }

        try
        {
            await DeleteRemoteStateOrThrowAsync(
                opts,
                transport,
                vmName,
                remoteSandboxRoot,
                CancellationToken.None).ConfigureAwait(false);
            reservation.Dispose();
        }
        catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            RetainReservation(vmName, reservation);
            _log.LogWarning(
                cleanupEx,
                "Retaining remote host reservation for {Vm} on host {HostId} because create rollback cleanup could not confirm deletion",
                vmName,
                opts.HostId);
            if (!throwOnCleanupFailure)
                return;

            var causeDetail = cause is null ? "" : $"create failure: {cause.Message}; ";
            throw new SandboxProvisioningDeferredException(
                provider: Name,
                operation: "create-rollback-cleanup",
                errorClass: "remote-cleanup-unconfirmed",
                detail: $"host={opts.HostId}; vm={vmName}; {causeDetail}{cleanupEx.Message}",
                recheckIn: opts.PlacementRecheckIn,
                retainedSandboxName: vmName,
                retainedSandboxHostId: opts.HostId,
                innerException: cleanupEx);
        }
    }

    private async Task<ISandbox> CreateOnReservedHostAsync(
        SandboxSpec spec,
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        string remoteSandboxRoot,
        string remoteFsRoot,
        HostReservation reservation,
        CancellationToken ct)
    {
        // 1) Prepare the per-sandbox staging directory on the remote host.
        await EnsureRemoteStagingDirAsync(opts, transport, remoteSandboxRoot, ct).ConfigureAwait(false);
        await WriteRemoteCreatedAtAsync(opts, transport, remoteSandboxRoot, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await WriteRemotePurposeMarkerAsync(opts, transport, remoteSandboxRoot, spec.Purpose, ct).ConfigureAwait(false);

        // 2) Stage each bind-mount source. Writable mounts get tracked so we
        //    can sync them back at dispose; tmpfs mounts get an empty remote
        //    directory.
        var stagedMounts = new List<StagedBindMount>(spec.Mounts.Count);
        foreach (var mount in spec.Mounts)
        {
            var safeSegment = SafeMountSegment(mount.SandboxPath);
            var remoteStaged = JoinRemote(remoteFsRoot, safeSegment);
            if (mount.Tmpfs)
            {
                await RunRemoteOrThrowAsync(transport, ["mkdir", "-p", remoteStaged], ct).ConfigureAwait(false);
                stagedMounts.Add(new StagedBindMount(
                    SandboxPath: mount.SandboxPath,
                    RemoteStagedPath: remoteStaged,
                    HostPath: null,
                    ReadOnly: false,
                    SyncBackHostPath: null));
            }
            else if (!string.IsNullOrWhiteSpace(mount.HostPath))
            {
                var hostPath = mount.HostPath!;
                if (!File.Exists(hostPath) && !Directory.Exists(hostPath))
                    throw new SandboxMountSourceMissingException(
                        hostPath,
                        $"Host bind-mount source missing for remote staging: {hostPath}");

                await RunRemoteOrThrowAsync(transport, ["mkdir", "-p", ParentOf(remoteStaged)], ct).ConfigureAwait(false);
                await transport.StageInAsync(hostPath, remoteStaged, ct).ConfigureAwait(false);
                stagedMounts.Add(new StagedBindMount(
                    SandboxPath: mount.SandboxPath,
                    RemoteStagedPath: remoteStaged,
                    HostPath: hostPath,
                    ReadOnly: mount.ReadOnly,
                    SyncBackHostPath: mount.ReadOnly ? null : hostPath));
            }
        }

        var sandbox = new MultipassRemoteSandbox(
            vmName,
            opts.HostId,
            spec,
            stagedMounts,
            remoteSandboxRoot,
            transport,
            (argv, token) => RunRemoteMaybeGatedAsync(opts, transport, argv, token),
            opts,
            _log,
            onTransportFailure: ex => MarkRuntimeUnhealthy(opts, ex),
            onDispose: (hostId, name) =>
            {
                _active.TryRemove(new RemoteSandboxIdentity(hostId, name), out _);
                reservation.Dispose();
            });

        // 3) Create the VM on the remote host. E2E replays pass
        //    BaselineImageRef and take the clone path so expensive setup is
        //    amortized into the remote image once. Legacy remote coding
        //    sandboxes without a baseline pin keep the launch path.
        var launchArgv = new List<string> { opts.RemoteMultipassPath, "launch", "--name", vmName };
        if (spec.Limits.CpuCount is { } cpu) { launchArgv.Add("--cpus"); launchArgv.Add(cpu.ToString(CultureInfo.InvariantCulture)); }
        if (spec.Limits.MemoryBytes is { } mem) { launchArgv.Add("--memory"); launchArgv.Add(((mem + (1L << 30) - 1) >> 30).ToString(CultureInfo.InvariantCulture) + "G"); }
        if (spec.Limits.DiskBytes is { } disk) { launchArgv.Add("--disk"); launchArgv.Add(((disk + (1L << 30) - 1) >> 30).ToString(CultureInfo.InvariantCulture) + "G"); }
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            if (!opts.NetworkProfiles.TryGetValue(spec.Network.ProfileName, out var bridge))
                throw new InvalidOperationException(
                    $"Network profile '{spec.Network.ProfileName}' is not configured in MultipassRemoteSandboxOptions.NetworkProfiles. " +
                    $"Configured profiles: [{string.Join(", ", opts.NetworkProfiles.Keys)}]. " +
                    "Either add the profile to CodeyBox:SandboxNetworkProfiles or configure matching bridges on the remote executor hosts.");
            launchArgv.AddRange(["--network", $"name={bridge},mode=auto"]);
        }
        if (!string.IsNullOrWhiteSpace(spec.ImageReference) && !string.Equals(spec.ImageReference, "ignored", StringComparison.Ordinal))
            launchArgv.Add(spec.ImageReference);
        else if (!string.IsNullOrWhiteSpace(opts.DefaultImage))
            launchArgv.Add(opts.DefaultImage!);

        try
        {
            // Track before clone/launch so an in-progress VM that appears in
            // multipass list is treated as active by leak sweeps.
            var activeKey = new RemoteSandboxIdentity(opts.HostId, vmName);
            _active[activeKey] = sandbox;

            if (!string.IsNullOrWhiteSpace(spec.BaselineImageRef))
            {
                await CloneRemoteBaselineAsync(opts, transport, spec.BaselineImageRef!, vmName, ct).ConfigureAwait(false);
                await RunRemoteOrThrowAsync(opts, transport, [opts.RemoteMultipassPath, "start", vmName], ct).ConfigureAwait(false);
            }
            else
            {
                await RunRemoteOrThrowAsync(opts, transport, launchArgv, ct).ConfigureAwait(false);
            }

            await WaitForVmStateAsync(opts, transport, vmName, "Running", opts.VmStartTimeout, ct).ConfigureAwait(false);

            // Publish the VM's IPv4 address to the sandbox so deployment
            // drivers can open an SSH local-forward endpoint into the guest.
            // The lookup is best-effort — a missing address leaves the sandbox
            // non-publishing rather than failing placement.
            var vmAddress = await ResolveRemoteVmAddressAsync(opts, transport, vmName, spec.Network.ProfileName, ct).ConfigureAwait(false);
            sandbox.RegisterVmAddress(vmAddress);

            // 4) Apply environment via a stamped /etc/environment fragment.
            //    multipass exec is per-call by default; environment from the
            //    spec needs to be visible to subsequent ExecAsync calls.
            if (spec.Environment.Count > 0)
                await ApplyVmEnvironmentAsync(opts, transport, vmName, spec.Environment, ct).ConfigureAwait(false);

            // 5) Apply mounts.
            foreach (var staged in stagedMounts)
            {
                var mountArgv = new List<string>
                {
                    opts.RemoteMultipassPath, "mount",
                    staged.RemoteStagedPath,
                    $"{vmName}:{staged.SandboxPath}",
                };
                await RunRemoteOrThrowAsync(opts, transport, mountArgv, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Drop the in-progress tracking entry; authoritative remote cleanup
            // (VM delete + staging removal + reservation handling) is owned by
            // the placement loop's RollBackCreateFailureAsync, which runs for
            // every exception path out of this method. Deleting here too would
            // double-issue `multipass delete`/`rm -rf` on the same target.
            _active.TryRemove(new RemoteSandboxIdentity(opts.HostId, vmName), out _);
            throw;
        }

        SandboxLiveCounter.Increment();
        CodeyBoxMeters.SandboxRemotePlacements.Add(
            1,
            new KeyValuePair<string, object?>("host_id", opts.HostId),
            new KeyValuePair<string, object?>("outcome", "created"));
        _log.LogInformation(
            "Remote multipass sandbox {Vm} created on host {HostId} ({Target}) via {Transport}; reservation {Reserved}/{Capacity}",
            vmName,
            opts.HostId,
            opts.SshTarget,
            transport.DiagnosticId,
            ReservedForHost(opts.HostId),
            FormatCapacity(MultipassRemoteSandboxOptions.EffectiveCapacity(opts)));
        return sandbox;
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        await ListManagedInventoryAsync(ct).ConfigureAwait(false);

    public async Task<ManagedSandboxInventory> ListManagedInventoryAsync(CancellationToken ct)
    {
        var infos = new List<ManagedSandboxInfo>();
        var hosts = ResolveHosts();
        var expectedHostIds = hosts
            .Select(static host => host.HostId)
            .ToHashSet(StringComparer.Ordinal);
        var inventoriedHostIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var opts in hosts)
        {
            if (string.IsNullOrWhiteSpace(opts.SshTarget))
                continue;

            var transport = _transportFactory(opts);
            // multipass list --format json is the documented machine-readable
            // shape. We parse only the fields we need.
            var argv = new[] { opts.RemoteMultipassPath, "list", "--format", "json" };
            ProcessRunResultLike result;
            try
            {
                result = await RunRemoteInventoryAsync(opts, transport, argv, ct).ConfigureAwait(false);
                MarkRuntimeHealthy(opts.HostId);
            }
            catch (RemoteSshTransportException ex)
            {
                // List is called by the leak reaper; a transport drop on one
                // host must not crash the sweep or hide other healthy hosts.
                MarkRuntimeUnhealthy(opts, ex);
                _log.LogWarning(ex,
                    "ListAllManagedAsync: SSH transport failure on remote host {HostId}; continuing with other hosts",
                    opts.HostId);
                continue;
            }
            catch (RemoteHostProvisioningException ex) when (ex.IsHostRuntimeFailure)
            {
                MarkRuntimeUnhealthy(opts, ex);
                _log.LogWarning(ex,
                    "ListAllManagedAsync: inventory command failed on remote host {HostId}; continuing with other hosts",
                    opts.HostId);
                continue;
            }

            if (result.ExitCode != 0)
            {
                MarkRuntimeUnhealthy(
                    opts,
                    $"multipass list exited {result.ExitCode}: {TruncateForLog(result.Stderr)}");
                _log.LogWarning("multipass list (remote host {HostId}) exited {Exit}: {Stderr}",
                    opts.HostId, result.ExitCode, TruncateForLog(result.Stderr));
                continue;
            }

            try
            {
                var createdAtByName = await ReadRemoteCreatedAtMetadataAsync(opts, transport, ct).ConfigureAwait(false);
                var purposeByName = await ReadRemotePurposeMarkersAsync(opts, transport, ct).ConfigureAwait(false);
                AddManagedFromListJson(infos, opts, result.Stdout, createdAtByName, purposeByName);
                inventoriedHostIds.Add(opts.HostId);
            }
            catch (RemoteSshTransportException ex)
            {
                // A host can drop between the primary inventory call and the
                // staging metadata scan. Keep the sweep partial instead of
                // aborting healthy hosts later in the pool.
                MarkRuntimeUnhealthy(opts, ex);
                _log.LogWarning(ex,
                    "ListAllManagedAsync: metadata scan transport failure on remote host {HostId}; continuing with other hosts",
                    opts.HostId);
            }
            catch (RemoteHostProvisioningException ex) when (ex.IsHostRuntimeFailure)
            {
                MarkRuntimeUnhealthy(opts, ex);
                _log.LogWarning(ex,
                    "ListAllManagedAsync: metadata scan failed on remote host {HostId}; continuing with other hosts",
                    opts.HostId);
            }
            catch (JsonException ex)
            {
                MarkRuntimeUnhealthy(opts, $"failed to parse multipass list JSON: {ex.Message}");
                _log.LogWarning(ex,
                    "Failed to parse remote multipass list JSON from host {HostId}; skipping host",
                    opts.HostId);
            }
        }

        ReleaseMissingRetainedReservations(inventoriedHostIds, infos);
        return new ManagedSandboxInventory(
            infos,
            isComplete: inventoriedHostIds.SetEquals(expectedHostIds),
            inventoriedHostIds: inventoriedHostIds);
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var activeMatches = _active
            .Where(kv => string.Equals(kv.Key.Name, name, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .ToArray();
        if (activeMatches.Length == 1)
        {
            await activeMatches[0].ForceDisposeLeakedAsync(ct).ConfigureAwait(false);
            return;
        }
        if (activeMatches.Length > 1)
            throw new InvalidOperationException(
                $"Refusing to dispose remote VM '{name}' by bare name because it is active on multiple executor hosts.");

        var retainedMatches = _retainedReservations
            .Where(kv => string.Equals(kv.Key.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (retainedMatches.Length == 1)
        {
            await DisposeLeakedOnHostAsync(retainedMatches[0].Value.HostOptions, name, ct).ConfigureAwait(false);
            return;
        }
        if (retainedMatches.Length > 1)
            throw new InvalidOperationException(
                $"Refusing to dispose remote VM '{name}' by bare name because retained reservations exist on multiple executor hosts.");

        var discovered = (await ListAllManagedAsync(ct).ConfigureAwait(false))
            .Where(info => string.Equals(info.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (discovered.Length == 1 && !string.IsNullOrWhiteSpace(discovered[0].HostId))
        {
            await DisposeLeakedAsync(discovered[0], ct).ConfigureAwait(false);
            return;
        }
        if (discovered.Length > 1)
        {
            throw new InvalidOperationException(
                $"Refusing to dispose remote VM '{name}' by bare name because it exists on multiple executor hosts.");
        }

        var hosts = ResolveHosts();
        var matchingHosts = hosts
            .Where(h => RemoteMultipassVmNames.IsManagedVmNameForPrefix(name, h.VmNamePrefix))
            .ToArray();
        if (matchingHosts.Length == 0)
        {
            _log.LogWarning(
                "Refusing to dispose VM '{Name}' — does not match any configured remote VM prefix ({Prefixes})",
                name,
                string.Join(", ", hosts.Select(h => h.VmNamePrefix).Distinct(StringComparer.Ordinal)));
            return;
        }
        if (matchingHosts.Length > 1)
        {
            throw new InvalidOperationException(
                $"Refusing to dispose remote VM '{name}' by bare name because {matchingHosts.Length} executor hosts share a matching prefix; use a managed sandbox record with HostId.");
        }

        // Honor the reaper's cancellation token: if the orchestrator is
        // shutting down, abandon this sweep — the leak isn't going anywhere
        // and the next sweep will retry. The CreateAsync rollback path uses a
        // different overload pinned to CancellationToken.None.
        await DisposeLeakedOnHostAsync(matchingHosts[0], name, ct).ConfigureAwait(false);
    }

    public async Task DisposeLeakedAsync(ManagedSandboxInfo sandbox, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sandbox.Name)) return;
        if (!string.IsNullOrWhiteSpace(sandbox.HostId)
            && _active.TryGetValue(new RemoteSandboxIdentity(sandbox.HostId!, sandbox.Name), out var active))
        {
            await active.ForceDisposeLeakedAsync(ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(sandbox.HostId))
        {
            await DisposeLeakedAsync(sandbox.Name, ct).ConfigureAwait(false);
            return;
        }

        var host = ResolveHosts().FirstOrDefault(h => string.Equals(h.HostId, sandbox.HostId, StringComparison.Ordinal));
        if (host is null)
            throw new InvalidOperationException(
                $"Refusing to dispose remote VM '{sandbox.Name}' because executor host '{sandbox.HostId}' is not configured.");
        if (!RemoteMultipassVmNames.IsManagedVmNameForPrefix(sandbox.Name, host.VmNamePrefix))
            throw new InvalidOperationException(
                $"Refusing to dispose remote VM '{sandbox.Name}' on host '{host.HostId}' because it does not match the safe managed VM name grammar or that host's prefix '{host.VmNamePrefix}'.");

        await DisposeLeakedOnHostAsync(host, sandbox.Name, ct).ConfigureAwait(false);
    }

    private async Task DisposeLeakedOnHostAsync(MultipassRemoteSandboxOptions opts, string name, CancellationToken ct)
    {
        var transport = _transportFactory(opts);
        await DeleteRemoteStateOrThrowAsync(
            opts,
            transport,
            name,
            JoinRemote(opts.RemoteStagingRoot, name),
            ct).ConfigureAwait(false);
        ReleaseRetainedReservation(opts.HostId, name);
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
    {
        var snap = new List<(WorkItemId, IShutdownTeardownSandbox)>(_active.Count);
        foreach (var (_, sb) in _active)
        {
            if (sb.OwningWorkItemId is { } id)
                snap.Add((id, sb));
        }
        return snap;
    }

    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress()
    {
        var snap = new List<ActiveSandboxProgress>(_active.Count);
        foreach (var (_, sb) in _active)
        {
            if (sb.OwningWorkItemId is { } id)
                snap.Add(new ActiveSandboxProgress(id, sb.Id, Status: $"running host={sb.HostId}"));
        }
        return snap;
    }

    public IReadOnlyList<SandboxHostPoolEntry> SnapshotHostPool()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<SandboxHostPoolEntry>();
        foreach (var host in ResolveHosts())
        {
            var runtimeHealthy = IsRuntimeHealthy(host.HostId, now, out var unhealthy, removeExpired: false);
            rows.Add(new SandboxHostPoolEntry(
                HostId: host.HostId,
                Capacity: MultipassRemoteSandboxOptions.EffectiveCapacity(host),
                Reserved: ReservedForHost(host.HostId),
                Cordoned: host.Cordoned,
                ConfiguredHealthy: host.Healthy,
                RuntimeHealthy: runtimeHealthy,
                RuntimeUnhealthyReason: unhealthy?.Reason,
                RuntimeUnhealthyUntil: unhealthy?.Until,
                AllowedNetworkProfiles: host.AllowedNetworkProfiles));
        }
        return rows;
    }

    internal async Task RunRemoteOrThrowAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var host = ResolveHosts()[0];
        await RunRemoteOrThrowAsync(host, _transportFactory(host), argv, ct).ConfigureAwait(false);
    }

    internal async Task RunRemoteOrThrowAsync(IRemoteHostTransport transport, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var host = ResolveHosts()[0];
        await RunRemoteOrThrowAsync(host, transport, argv, ct).ConfigureAwait(false);
    }

    internal async Task RunRemoteOrThrowAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        IReadOnlyList<string> argv,
        CancellationToken ct)
    {
        var r = await RunRemoteMaybeGatedAsync(opts, transport, argv, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new RemoteHostProvisioningException(
                opts.HostId,
                CommandName(argv),
                $"Remote command failed (exit {r.ExitCode}): argv=[{string.Join(' ', argv)}] stderr={TruncateForLog(r.Stderr)}",
                isHostRuntimeFailure: IsHostRuntimeCommand(argv));
    }

    internal Task<ProcessRunResultLike> RunRemoteAsync(IReadOnlyList<string> argv, CancellationToken ct) =>
        RunRemoteAsync(_transportFactory(ResolveHosts()[0]), argv, stdin: null, stdoutChunkCallback: null, stderrChunkCallback: null, ct);

    internal Task<ProcessRunResultLike> RunRemoteAsync(IRemoteHostTransport transport, IReadOnlyList<string> argv, CancellationToken ct) =>
        RunRemoteAsync(transport, argv, stdin: null, stdoutChunkCallback: null, stderrChunkCallback: null, ct);

    private async Task<ProcessRunResultLike> RunRemoteMaybeGatedAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var host = ResolveHosts()[0];
        return await RunRemoteMaybeGatedAsync(host, _transportFactory(host), argv, ct).ConfigureAwait(false);
    }

    private async Task<ProcessRunResultLike> RunRemoteMaybeGatedAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        IReadOnlyList<string> argv,
        CancellationToken ct)
    {
        if (!IsHeavyRemoteMultipassOperation(opts, argv))
            return await RunRemoteControlAsync(opts, transport, argv, ct).ConfigureAwait(false);

        var gate = _heavyMultipassGates.GetOrAdd(opts.HostId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunRemoteControlAsync(opts, transport, argv, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<ProcessRunResultLike> RunRemoteAsync(
        IRemoteHostTransport transport,
        IReadOnlyList<string> argv,
        string? stdin,
        Action<string>? stdoutChunkCallback,
        Action<string>? stderrChunkCallback,
        CancellationToken ct,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true)
    {
        var native = await transport.RunAsync(
            argv,
            stdin,
            ct,
            stdoutChunkCallback: stdoutChunkCallback,
            stderrChunkCallback: stderrChunkCallback,
            maxStdoutBytes: maxStdoutBytes,
            maxStderrBytes: maxStderrBytes,
            killOnOutputLimit: killOnOutputLimit).ConfigureAwait(false);
        return new ProcessRunResultLike(native.ExitCode, native.Stdout, native.Stderr, native.StdoutLimitExceeded, native.StderrLimitExceeded);
    }

    private async Task<ProcessRunResultLike> RunRemoteControlAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        IReadOnlyList<string> argv,
        CancellationToken ct,
        string? stdin = null)
    {
        var maxOutputBytes = opts.RemoteInventoryMaxOutputBytes;
        var result = await RunRemoteAsync(
            transport,
            argv,
            stdin,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            ct,
            maxStdoutBytes: maxOutputBytes,
            maxStderrBytes: maxOutputBytes,
            killOnOutputLimit: true).ConfigureAwait(false);

        if (!result.StdoutLimitExceeded && !result.StderrLimitExceeded)
            return result;

        var streams = result.StdoutLimitExceeded && result.StderrLimitExceeded
            ? "stdout/stderr"
            : result.StdoutLimitExceeded
                ? "stdout"
                : "stderr";
        throw new RemoteHostProvisioningException(
            opts.HostId,
            CommandName(argv),
            $"Remote control command exceeded {maxOutputBytes.ToString(CultureInfo.InvariantCulture)} byte {streams} cap: argv=[{string.Join(' ', argv)}]",
            isHostRuntimeFailure: IsHostRuntimeCommand(argv));
    }

    private async Task<ProcessRunResultLike> RunRemoteInventoryAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        IReadOnlyList<string> argv,
        CancellationToken ct)
    {
        var maxOutputBytes = opts.RemoteInventoryMaxOutputBytes;
        var result = await RunRemoteAsync(
            transport,
            argv,
            stdin: null,
            stdoutChunkCallback: null,
            stderrChunkCallback: null,
            ct,
            maxStdoutBytes: maxOutputBytes,
            maxStderrBytes: maxOutputBytes,
            killOnOutputLimit: true).ConfigureAwait(false);

        if (!result.StdoutLimitExceeded && !result.StderrLimitExceeded)
            return result;

        var streams = result.StdoutLimitExceeded && result.StderrLimitExceeded
            ? "stdout/stderr"
            : result.StdoutLimitExceeded
                ? "stdout"
                : "stderr";
        throw new RemoteHostProvisioningException(
            opts.HostId,
            CommandName(argv),
            $"Remote inventory command exceeded {maxOutputBytes.ToString(CultureInfo.InvariantCulture)} byte {streams} cap: argv=[{string.Join(' ', argv)}]",
            isHostRuntimeFailure: true);
    }

    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadRemoteCreatedAtMetadataAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        CancellationToken ct)
    {
        var root = OpenSshCliTransport.QuoteShellWord(opts.RemoteStagingRoot);
        var script =
            $"find {root} -mindepth 2 -maxdepth 2 -name .codeybox-created-at -type f -print 2>/dev/null " +
            "| while IFS= read -r f; do d=${f%/.codeybox-created-at}; n=${d##*/}; printf '%s\\t' \"$n\"; head -n 1 \"$f\"; done || true";
        ProcessRunResultLike result;
        try
        {
            result = await RunRemoteInventoryAsync(opts, transport, ["sh", "-c", script], ct).ConfigureAwait(false);
        }
        catch (RemoteSshTransportException ex)
        {
            MarkRuntimeUnhealthy(opts, ex);
            throw;
        }

        if (result.ExitCode != 0)
        {
            _log.LogWarning(
                "Remote sandbox metadata scan on host {HostId} exited {ExitCode}: {Stderr}",
                opts.HostId,
                result.ExitCode,
                TruncateForLog(result.Stderr));
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }

        var created = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var reader = new StringReader(result.Stdout);
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                continue;
            var name = line[..tab];
            var raw = line[(tab + 1)..].Trim();
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt))
            {
                created[name] = createdAt;
            }
        }

        return created;
    }

    private async Task EnsureRemoteStagingDirAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string remoteSandboxRoot,
        CancellationToken ct)
    {
        // 0700 on the per-sandbox dir: only the SSH user can list its
        // contents. Per-staged-source subdirs sit under here.
        var mkdirCmd = $"mkdir -p {OpenSshCliTransport.QuoteShellWord(remoteSandboxRoot)} && chmod 0700 {OpenSshCliTransport.QuoteShellWord(remoteSandboxRoot)}";
        var r = await RunRemoteControlAsync(opts, transport, ["sh", "-c", mkdirCmd], ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new RemoteHostProvisioningException(
                opts.HostId,
                "staging-dir",
                $"Failed to create remote sandbox staging dir '{remoteSandboxRoot}' (exit {r.ExitCode}): {TruncateForLog(r.Stderr)}",
                isHostRuntimeFailure: true);
    }

    private async Task CloneRemoteBaselineAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string baselineName,
        string vmName,
        CancellationToken ct)
    {
        await RunRemoteOrThrowAsync(opts, transport, [opts.RemoteMultipassPath, "stop", baselineName], ct).ConfigureAwait(false);
        await WaitForVmStateAsync(opts, transport, baselineName, "Stopped", opts.VmStopTimeout, ct).ConfigureAwait(false);
        await RunRemoteOrThrowAsync(opts, transport, [opts.RemoteMultipassPath, "clone", baselineName, "--name", vmName], ct).ConfigureAwait(false);
    }

    private async Task WriteRemoteCreatedAtAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string remoteSandboxRoot,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var metadataPath = JoinRemote(remoteSandboxRoot, ".codeybox-created-at");
        var timestamp = createdAt.ToString("O", CultureInfo.InvariantCulture);
        var script =
            $"printf '%s\\n' {OpenSshCliTransport.QuoteShellWord(timestamp)} > {OpenSshCliTransport.QuoteShellWord(metadataPath)}";
        var r = await RunRemoteControlAsync(opts, transport, ["sh", "-c", script], ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new RemoteHostProvisioningException(
                opts.HostId,
                "staging-metadata",
                $"Failed to write remote sandbox metadata '{metadataPath}' (exit {r.ExitCode}): {TruncateForLog(r.Stderr)}",
                isHostRuntimeFailure: true);
    }

    private async Task WriteRemotePurposeMarkerAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string remoteSandboxRoot,
        SandboxPurpose purpose,
        CancellationToken ct)
    {
        var path = JoinRemote(remoteSandboxRoot, PurposeMarkerFile);
        var cmd = $"printf %s {OpenSshCliTransport.QuoteShellWord(purpose.ToString())} > {OpenSshCliTransport.QuoteShellWord(path)}";
        var r = await RunRemoteControlAsync(opts, transport, ["sh", "-c", cmd], ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new RemoteHostProvisioningException(
                opts.HostId,
                "staging-purpose",
                $"Failed to write remote sandbox purpose marker '{path}' (exit {r.ExitCode}): {TruncateForLog(r.Stderr)}",
                isHostRuntimeFailure: true);
    }

    private async Task<IReadOnlyDictionary<string, SandboxPurpose>> ReadRemotePurposeMarkersAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        CancellationToken ct)
    {
        var root = OpenSshCliTransport.QuoteShellWord(opts.RemoteStagingRoot);
        var script =
            $"find {root} -mindepth 2 -maxdepth 2 -name {OpenSshCliTransport.QuoteShellWord(PurposeMarkerFile)} -type f -print 2>/dev/null " +
            $"| while IFS= read -r f; do d=${{f%/{PurposeMarkerFile}}}; n=${{d##*/}}; printf '%s\\t' \"$n\"; head -n 1 \"$f\"; printf '\\n'; done || true";
        ProcessRunResultLike result;
        try
        {
            result = await RunRemoteInventoryAsync(opts, transport, ["sh", "-c", script], ct).ConfigureAwait(false);
        }
        catch (RemoteSshTransportException ex)
        {
            MarkRuntimeUnhealthy(opts, ex);
            throw;
        }

        if (result.ExitCode != 0)
        {
            _log.LogWarning(
                "Remote sandbox purpose-marker scan on host {HostId} exited {ExitCode}: {Stderr}",
                opts.HostId,
                result.ExitCode,
                TruncateForLog(result.Stderr));
            return new Dictionary<string, SandboxPurpose>(StringComparer.Ordinal);
        }

        var purposes = new Dictionary<string, SandboxPurpose>(StringComparer.Ordinal);
        using var reader = new StringReader(result.Stdout);
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                continue;
            var name = line[..tab];
            var raw = line[(tab + 1)..].Trim();
            if (Enum.TryParse<SandboxPurpose>(raw, ignoreCase: true, out var purpose))
                purposes[name] = purpose;
        }

        return purposes;
    }

    private async Task ApplyVmEnvironmentAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct)
    {
        // /etc/environment is the per-VM persistent env file Multipass /
        // systemd both honour for subsequent exec'd shells. We append (don't
        // overwrite) to avoid stomping cloud-init defaults.
        var lines = new System.Text.StringBuilder();
        foreach (var (k, v) in env)
        {
            ValidateEnvKey(k);
            lines.Append(k).Append("=\"").Append(EscapeForDoubleQuotes(v)).Append("\"\n");
        }
        var script = "cat >> /etc/environment";
        var argv = new[] { opts.RemoteMultipassPath, "exec", vmName, "--", "sudo", "sh", "-c", script };
        var r = await RunRemoteControlAsync(opts, transport, argv, ct, stdin: lines.ToString()).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new RemoteHostProvisioningException(
                opts.HostId,
                "env",
                $"Failed to apply env to remote VM '{vmName}' (exit {r.ExitCode}): {TruncateForLog(r.Stderr)}",
                isHostRuntimeFailure: true);
    }

    internal async Task WaitForVmStateAsync(string vmName, string targetState, TimeSpan timeout, CancellationToken ct)
    {
        var opts = ResolveHosts()[0];
        await WaitForVmStateAsync(opts, _transportFactory(opts), vmName, targetState, timeout, ct).ConfigureAwait(false);
    }

    internal async Task WaitForVmStateAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        string targetState,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var r = await RunRemoteInventoryAsync(
                opts,
                transport,
                [opts.RemoteMultipassPath, "info", vmName, "--format", "json"],
                ct).ConfigureAwait(false);
            if (r.ExitCode == 0 && TryParseVmState(r.Stdout, vmName, out var state) && string.Equals(state, targetState, StringComparison.Ordinal))
                return;

            if (DateTime.UtcNow >= deadline)
                throw new RemoteHostProvisioningException(
                    opts.HostId,
                    "wait-state",
                    $"Remote VM '{vmName}' did not reach state '{targetState}' within {timeout}.",
                    isHostRuntimeFailure: true);

            try { await Task.Delay(opts.VmStateCheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
    }

    internal async Task<string?> ResolveRemoteVmAddressAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        string? networkProfile,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(networkProfile))
            return null;
        if (!opts.NetworkProfiles.TryGetValue(networkProfile, out var bridge))
            return null;

        ProcessRunResultLike r;
        try
        {
            r = await RunRemoteInventoryAsync(
                opts,
                transport,
                [opts.RemoteMultipassPath, "info", vmName, "--format", "json"],
                ct).ConfigureAwait(false);
        }
        catch (RemoteSshTransportException ex)
        {
            _log.LogWarning(ex,
                "Remote VM {Vm}: SSH transport failure reading multipass info for deployment endpoint publishing on host {HostId}",
                vmName, opts.HostId);
            return null;
        }
        catch (RemoteHostProvisioningException ex) when (ex.IsHostRuntimeFailure)
        {
            _log.LogWarning(ex,
                "Remote VM {Vm}: multipass info failed on host {HostId} for deployment endpoint publishing",
                vmName, opts.HostId);
            return null;
        }
        if (r.ExitCode != 0)
        {
            _log.LogWarning(
                "Remote VM {Vm}: multipass info exit {ExitCode} on host {HostId} for deployment endpoint publishing: {Stderr}",
                vmName, r.ExitCode, opts.HostId, TruncateForLog(r.Stderr));
            return null;
        }

        var addresses = TryParseVmAddresses(r.Stdout, vmName);
        if (addresses.Count == 0)
            return null;

        var bridgeSubnet = await ResolveRemoteBridgeSubnetAsync(bridge, ct).ConfigureAwait(false);
        if (bridgeSubnet is null)
        {
            _log.LogWarning(
                "Remote VM {Vm}: cannot identify IPv4 subnet for network profile {Profile} bridge {Bridge}; endpoint publishing disabled",
                vmName, networkProfile, bridge);
            return null;
        }

        return addresses.FirstOrDefault(bridgeSubnet.Value.Contains);
    }

    private async Task<Ipv4Subnet?> ResolveRemoteBridgeSubnetAsync(string bridge, CancellationToken ct)
    {
        var r = await RunRemoteAsync(["ip", "-4", "-o", "addr", "show", "dev", bridge], ct)
            .ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            _log.LogWarning(
                "Remote bridge {Bridge}: failed to read IPv4 address for endpoint publishing (exit {ExitCode}): {Stderr}",
                bridge, r.ExitCode, TruncateForLog(r.Stderr));
            return null;
        }

        return TryParseIpv4Subnet(r.Stdout);
    }

    private static bool TryParseVmState(string json, string vmName, out string state)
    {
        state = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("info", out var info)) return false;
            if (!info.TryGetProperty(vmName, out var entry)) return false;
            if (!entry.TryGetProperty("state", out var st)) return false;
            if (st.ValueKind != JsonValueKind.String) return false;
            var s = st.GetString();
            if (s is null) return false;
            state = s;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> TryParseVmAddresses(string json, string vmName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("info", out var info)) return [];
            if (!info.TryGetProperty(vmName, out var entry)) return [];
            if (!entry.TryGetProperty("ipv4", out var ipv4)) return [];
            if (ipv4.ValueKind == JsonValueKind.String)
                return ParseIpv4s(ipv4.GetString()).ToList();
            if (ipv4.ValueKind != JsonValueKind.Array) return [];

            var addresses = new List<string>();
            foreach (var item in ipv4.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    addresses.AddRange(ParseIpv4s(item.GetString()));
            }
            return addresses;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ParseIpv4s(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;
        foreach (var token in value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(token, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                yield return address.ToString();
        }
    }

    private static Ipv4Subnet? TryParseIpv4Subnet(string output)
    {
        foreach (var token in output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.Contains('/', StringComparison.Ordinal))
                continue;
            var parts = token.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 0 or > 32)
                continue;
            return new Ipv4Subnet(address, PrefixToMask(prefix));
        }

        return null;
    }

    private static IPAddress PrefixToMask(int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return new IPAddress(new[]
        {
            (byte)(mask >> 24),
            (byte)(mask >> 16),
            (byte)(mask >> 8),
            (byte)mask,
        });
    }

    private readonly record struct Ipv4Subnet(IPAddress Address, IPAddress Mask)
    {
        public bool Contains(string candidate)
            => IPAddress.TryParse(candidate, out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork
                && Contains(parsed);

        public bool Contains(IPAddress candidate)
        {
            if (candidate.AddressFamily != AddressFamily.InterNetwork)
                return false;
            var candidateBytes = candidate.GetAddressBytes();
            var addressBytes = Address.GetAddressBytes();
            var maskBytes = Mask.GetAddressBytes();
            if (candidateBytes.Length != 4 || addressBytes.Length != 4 || maskBytes.Length != 4)
                return false;
            for (var i = 0; i < 4; i++)
                if ((candidateBytes[i] & maskBytes[i]) != (addressBytes[i] & maskBytes[i]))
                    return false;
            return true;
        }
    }

    internal async Task DeleteRemoteStateOrThrowAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        string remoteSandboxRoot,
        CancellationToken ct)
    {
        await BuildCleanup(opts, transport)
            .DeleteVmAndStagingOrThrowAsync(vmName, remoteSandboxRoot, ct)
            .ConfigureAwait(false);
    }

    private RemoteMultipassCleanup BuildCleanup(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport) =>
        new(
            opts,
            transport,
            (argv, token) => RunRemoteMaybeGatedAsync(opts, transport, argv, token),
            ex => MarkRuntimeUnhealthy(opts, ex),
            _log);

    private IReadOnlyList<MultipassRemoteSandboxOptions> ResolveHosts()
    {
        var hosts = MultipassRemoteSandboxOptions.ResolveExecutorHosts(_optsAccessor());
        if (hosts.Count == 0)
            throw new InvalidOperationException("MultipassRemoteSandboxOptions must resolve at least one executor host.");

        foreach (var host in hosts)
        {
            if (string.IsNullOrWhiteSpace(host.HostId))
                throw new InvalidOperationException("Resolved remote executor host id must not be empty.");
            if (MultipassRemoteSandboxOptions.EffectiveCapacity(host) <= 0)
                throw new InvalidOperationException(
                    $"MultipassRemoteSandbox executor host '{host.HostId}' MaxConcurrentSandboxes must be > 0 when set.");
            if (host.PlacementRecheckIn <= TimeSpan.Zero)
                throw new InvalidOperationException("MultipassRemoteSandboxOptions.PlacementRecheckIn must be positive.");
            if (host.RuntimeUnhealthyBackoff <= TimeSpan.Zero)
                throw new InvalidOperationException("MultipassRemoteSandboxOptions.RuntimeUnhealthyBackoff must be positive.");
            if (host.RemoteInventoryMaxOutputBytes <= 0)
                throw new InvalidOperationException("MultipassRemoteSandboxOptions.RemoteInventoryMaxOutputBytes must be positive.");
            RemoteMultipassVmNames.ValidateVmNamePrefix(host.VmNamePrefix);
            RemoteMultipassVmNames.ValidateRemoteStagingRoot(host.RemoteStagingRoot);
        }

        return hosts;
    }

    private async Task<HostReservation> ReserveHostAsync(SandboxSpec spec, ISet<string> skippedHosts, CancellationToken ct)
    {
        var hosts = ResolveHosts();
        var inventoryCandidates = GetPlacementInventoryCandidates(spec, skippedHosts, hosts);
        if (inventoryCandidates.Count == 0)
            return ReserveHost(spec, skippedHosts, hosts, new Dictionary<string, int>(StringComparer.Ordinal));

        var inventory = await CountManagedByHostForPlacementAsync(inventoryCandidates, ct).ConfigureAwait(false);
        try
        {
            return ReserveHost(spec, skippedHosts, hosts, inventory.ManagedCounts);
        }
        catch (SandboxProvisioningDeferredException ex) when (inventory.LastFailure is not null)
        {
            throw new SandboxProvisioningDeferredException(
                ex.Provider,
                ex.Operation,
                "all-hosts-unavailable",
                $"{ex.Detail}; last host failure: {inventory.LastFailure.Message}",
                ex.RecheckIn,
                innerException: inventory.LastFailure);
        }
    }

    private IReadOnlyList<MultipassRemoteSandboxOptions> GetPlacementInventoryCandidates(
        SandboxSpec spec,
        ISet<string> skippedHosts,
        IReadOnlyList<MultipassRemoteSandboxOptions> hosts)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new List<MultipassRemoteSandboxOptions>(hosts.Count);
        foreach (var host in hosts)
        {
            if (skippedHosts.Contains(host.HostId))
                continue;
            if (!host.Healthy || host.Cordoned)
                continue;
            if (!HostAllowsNetworkProfile(host, spec.Network.ProfileName))
                continue;
            if (!IsRuntimeHealthy(host.HostId, now, out _, removeExpired: true))
                continue;
            candidates.Add(host);
        }

        return candidates;
    }

    private async Task<PlacementInventory> CountManagedByHostForPlacementAsync(
        IReadOnlyList<MultipassRemoteSandboxOptions> hosts,
        CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Exception? lastFailure = null;
        foreach (var opts in hosts)
        {
            var transport = _transportFactory(opts);
            try
            {
                var result = await RunRemoteInventoryAsync(
                    opts,
                    transport,
                    [opts.RemoteMultipassPath, "list", "--format", "json"],
                    ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    lastFailure = new RemoteHostProvisioningException(
                        opts.HostId,
                        "list",
                        $"multipass list exited {result.ExitCode}: {TruncateForLog(result.Stderr)}");
                    MarkRuntimeUnhealthy(opts, lastFailure);
                    continue;
                }

                counts[opts.HostId] = CountManagedFromListJson(opts, result.Stdout);
                MarkRuntimeHealthy(opts.HostId);
            }
            catch (RemoteSshTransportException ex)
            {
                lastFailure = ex;
                MarkRuntimeUnhealthy(opts, ex);
            }
            catch (RemoteHostProvisioningException ex) when (ex.IsHostRuntimeFailure)
            {
                lastFailure = ex;
                MarkRuntimeUnhealthy(opts, ex);
            }
            catch (JsonException ex)
            {
                lastFailure = ex;
                MarkRuntimeUnhealthy(opts, $"failed to parse multipass list JSON: {ex.Message}");
            }
        }

        return new PlacementInventory(counts, lastFailure);
    }

    private HostReservation ReserveHost(
        SandboxSpec spec,
        ISet<string> skippedHosts,
        IReadOnlyList<MultipassRemoteSandboxOptions> hosts,
        IReadOnlyDictionary<string, int> managedCounts)
    {
        var profile = NormalizeNetworkProfile(spec.Network.ProfileName);
        var now = DateTimeOffset.UtcNow;
        var blocked = new List<string>(hosts.Count);

        lock (_placementLock)
        {
            MultipassRemoteSandboxOptions? selected = null;
            int selectedReserved = 0;
            double selectedLoad = double.MaxValue;

            foreach (var host in hosts)
            {
                var reserved = _hostReservations.TryGetValue(host.HostId, out var count) ? count : 0;
                var activeForHost = _active.Values.Count(sb => string.Equals(sb.HostId, host.HostId, StringComparison.Ordinal));
                var retainedForHost = _retainedReservations.Values.Count(r => string.Equals(r.HostOptions.HostId, host.HostId, StringComparison.Ordinal));
                var managedCount = managedCounts.TryGetValue(host.HostId, out var managed) ? managed : 0;
                var untrackedManaged = Math.Max(0, managedCount - activeForHost - retainedForHost);
                var used = reserved + untrackedManaged;
                var capacity = MultipassRemoteSandboxOptions.EffectiveCapacity(host);

                if (skippedHosts.Contains(host.HostId))
                {
                    blocked.Add($"{host.HostId}=tried");
                    continue;
                }
                if (!host.Healthy)
                {
                    blocked.Add($"{host.HostId}=configured-unhealthy");
                    continue;
                }
                if (host.Cordoned)
                {
                    blocked.Add($"{host.HostId}=cordoned");
                    continue;
                }
                if (!HostAllowsNetworkProfile(host, spec.Network.ProfileName))
                {
                    blocked.Add($"{host.HostId}=profile");
                    continue;
                }
                if (!IsRuntimeHealthy(host.HostId, now, out var unhealthy, removeExpired: true))
                {
                    blocked.Add($"{host.HostId}=runtime-unhealthy-until-{unhealthy!.Until:O}");
                    continue;
                }
                if (used >= capacity)
                {
                    blocked.Add($"{host.HostId}=full({used}/{FormatCapacity(capacity)})");
                    continue;
                }

                var load = capacity == int.MaxValue ? 0.0d : (double)used / capacity;
                if (selected is null
                    || load < selectedLoad
                    || (Math.Abs(load - selectedLoad) < double.Epsilon
                        && used < selectedReserved)
                    || (Math.Abs(load - selectedLoad) < double.Epsilon
                        && used == selectedReserved
                        && string.CompareOrdinal(host.HostId, selected.HostId) < 0))
                {
                    selected = host;
                    selectedReserved = used;
                    selectedLoad = load;
                }
            }

            if (selected is null)
            {
                var reason = blocked.Count == 0 ? "no-hosts" : string.Join(", ", blocked);
                CodeyBoxMeters.SandboxRemotePlacementDeferrals.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "no-eligible-host"),
                    new KeyValuePair<string, object?>("network_profile", profile));
                _log.LogWarning(
                    "Remote sandbox placement deferred: no eligible executor host for network profile {NetworkProfile}. Host states: {HostStates}",
                    profile,
                    reason);
                throw new SandboxProvisioningDeferredException(
                    provider: Name,
                    operation: "placement",
                    errorClass: "no-eligible-host",
                    detail: $"networkProfile={profile}; hosts={reason}",
                    recheckIn: hosts[0].PlacementRecheckIn);
            }

            var currentReserved = _hostReservations.TryGetValue(selected.HostId, out var current) ? current : 0;
            _hostReservations[selected.HostId] = currentReserved + 1;
            CodeyBoxMeters.SandboxRemotePlacements.Add(
                1,
                new KeyValuePair<string, object?>("host_id", selected.HostId),
                new KeyValuePair<string, object?>("outcome", "reserved"));
            _log.LogDebug(
                "Remote sandbox placement reserved host {HostId}: {Reserved}/{Capacity} for network profile {NetworkProfile}",
                selected.HostId,
                currentReserved + 1,
                FormatCapacity(MultipassRemoteSandboxOptions.EffectiveCapacity(selected)),
                profile);
            return new HostReservation(this, selected);
        }
    }

    private void ReleaseHostReservation(string hostId)
    {
        lock (_placementLock)
        {
            if (!_hostReservations.TryGetValue(hostId, out var current))
                return;
            if (current <= 1)
                _hostReservations.Remove(hostId);
            else
                _hostReservations[hostId] = current - 1;
        }
    }

    private void RetainReservation(string vmName, HostReservation reservation)
    {
        var key = new RemoteSandboxIdentity(reservation.HostOptions.HostId, vmName);
        if (!_retainedReservations.TryAdd(key, reservation))
        {
            reservation.Dispose();
            return;
        }
    }

    private void ReleaseRetainedReservation(string hostId, string vmName)
    {
        if (_retainedReservations.TryRemove(new RemoteSandboxIdentity(hostId, vmName), out var reservation))
            reservation.Dispose();
    }

    private void ReleaseMissingRetainedReservations(
        IReadOnlyCollection<string> inventoriedHostIds,
        IReadOnlyCollection<ManagedSandboxInfo> managed)
    {
        if (_retainedReservations.IsEmpty || inventoriedHostIds.Count == 0)
            return;

        var present = managed
            .Where(static info => !string.IsNullOrWhiteSpace(info.HostId))
            .Select(static info => (info.HostId!, info.Name))
            .ToHashSet();

        foreach (var (key, reservation) in _retainedReservations)
        {
            if (!inventoriedHostIds.Contains(reservation.HostOptions.HostId))
                continue;
            if (present.Contains((key.HostId, key.Name)))
                continue;
            ReleaseRetainedReservation(key.HostId, key.Name);
        }
    }

    private int ReservedForHost(string hostId)
    {
        lock (_placementLock)
            return _hostReservations.TryGetValue(hostId, out var current) ? current : 0;
    }

    private void MarkRuntimeUnhealthy(MultipassRemoteSandboxOptions host, RemoteSshTransportException ex)
        => MarkRuntimeUnhealthy(host, ex.Message);

    private void MarkRuntimeUnhealthy(MultipassRemoteSandboxOptions host, Exception ex)
        => MarkRuntimeUnhealthy(host, ex.Message);

    private void MarkRuntimeUnhealthy(MultipassRemoteSandboxOptions host, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var until = now + host.RuntimeUnhealthyBackoff;
        var wasHealthy = !_runtimeUnhealthy.TryGetValue(host.HostId, out var previous)
            || previous.Until <= now;
        _runtimeUnhealthy[host.HostId] = new RemoteHostUnhealthyState(until, reason);
        if (wasHealthy)
        {
            CodeyBoxMeters.SandboxRemoteHostHealthTransitions.Add(
                1,
                new KeyValuePair<string, object?>("host_id", host.HostId),
                new KeyValuePair<string, object?>("state", "unhealthy"));
        }
        _log.LogWarning(
            "Remote executor host {HostId} marked runtime-unhealthy until {Until:O}: {Reason}",
            host.HostId,
            until,
            reason);
    }

    private void MarkRuntimeHealthy(string hostId)
    {
        if (!_runtimeUnhealthy.TryRemove(hostId, out _))
            return;

        CodeyBoxMeters.SandboxRemoteHostHealthTransitions.Add(
            1,
            new KeyValuePair<string, object?>("host_id", hostId),
            new KeyValuePair<string, object?>("state", "healthy"));
        _log.LogInformation("Remote executor host {HostId} runtime health restored", hostId);
    }

    private bool IsRuntimeHealthy(
        string hostId,
        DateTimeOffset now,
        out RemoteHostUnhealthyState? unhealthy,
        bool removeExpired)
    {
        unhealthy = null;
        if (!_runtimeUnhealthy.TryGetValue(hostId, out var state))
            return true;

        if (state.Until <= now)
        {
            if (removeExpired)
                MarkRuntimeHealthy(hostId);
            return true;
        }

        unhealthy = state;
        return false;
    }

    private static bool HostAllowsNetworkProfile(MultipassRemoteSandboxOptions host, string? profileName)
    {
        if (host.AllowedNetworkProfiles.Count == 0)
            return true;

        var profile = NormalizeNetworkProfile(profileName);
        foreach (var configured in host.AllowedNetworkProfiles)
        {
            if (string.IsNullOrWhiteSpace(configured))
                continue;
            var value = configured.Trim();
            if (value == "*")
                return true;
            if (string.Equals(value, profile, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeNetworkProfile(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName) ? "(default)" : profileName.Trim();

    private static string CommandName(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
            return "(none)";
        if (argv.Count > 1 && argv[0].Contains("multipass", StringComparison.OrdinalIgnoreCase))
            return argv[1];
        return argv[0];
    }

    private static bool IsHostRuntimeCommand(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
            return false;
        if (argv[0].Contains("multipass", StringComparison.OrdinalIgnoreCase))
            return true;
        return argv[0] is "mkdir" or "rm";
    }

    private void AddManagedFromListJson(
        List<ManagedSandboxInfo> infos,
        MultipassRemoteSandboxOptions opts,
        string json,
        IReadOnlyDictionary<string, DateTimeOffset> createdAtByName,
        IReadOnlyDictionary<string, SandboxPurpose> purposeByName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!RemoteMultipassVmNames.IsManagedVmNameForPrefix(name, opts.VmNamePrefix)) continue;

            var isTrackedActive = _active.TryGetValue(new RemoteSandboxIdentity(opts.HostId, name), out var active) && active.IsTrackedActive;
            var state = entry.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var isSuspendOrFreezing = state is "Suspended" or "Suspending" or "Freezing";
            createdAtByName.TryGetValue(name, out var createdAt);
            var purpose = purposeByName.TryGetValue(name, out var p) ? p : SandboxPurpose.WorkItem;

            infos.Add(new ManagedSandboxInfo(
                Name: name,
                CreatedAt: createdAt == default ? null : createdAt,
                DiskBytes: null,
                IsTrackedActive: isTrackedActive,
                HasPreemptMarker: false,
                IsSuspendLifecycleOrFrozen: isSuspendOrFreezing,
                HostId: opts.HostId,
                Purpose: purpose));
        }
    }

    private static int CountManagedFromListJson(MultipassRemoteSandboxOptions opts, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            if (RemoteMultipassVmNames.IsManagedVmNameForPrefix(name, opts.VmNamePrefix))
                count++;
        }

        return count;
    }

    private static string FormatCapacity(int capacity) =>
        capacity == int.MaxValue ? "unbounded" : capacity.ToString(CultureInfo.InvariantCulture);

    private sealed class HostReservation : IDisposable
    {
        private readonly MultipassRemoteSandboxProvider _owner;
        private int _disposed;

        public HostReservation(MultipassRemoteSandboxProvider owner, MultipassRemoteSandboxOptions hostOptions)
        {
            _owner = owner;
            HostOptions = hostOptions;
        }

        public MultipassRemoteSandboxOptions HostOptions { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.ReleaseHostReservation(HostOptions.HostId);
        }
    }

    private static string SafeMountSegment(string sandboxPath)
    {
        // Replace path separators with dashes and strip anything not safe
        // for a directory name. Mirrors the local MultipassSandboxProvider.
        var s = sandboxPath.Trim('/');
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                '/' => '-',
                _ when char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' => ch,
                _ => '_',
            });
        }
        var seg = sb.ToString();
        return string.IsNullOrEmpty(seg) ? "root" : seg;
    }

    private static string JoinRemote(string a, string b) =>
        a.TrimEnd('/') + "/" + b.TrimStart('/');

    private static bool IsHeavyRemoteMultipassOperation(
        MultipassRemoteSandboxOptions opts,
        IReadOnlyList<string> argv)
    {
        if (argv.Count < 2)
            return false;
        if (!string.Equals(argv[0], opts.RemoteMultipassPath, StringComparison.Ordinal))
            return false;

        return argv[1] is "launch" or "start" or "stop" or "clone" or "mount" or "delete";
    }

    private static string ParentOf(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    private static string EscapeForDoubleQuotes(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\"", "\\\"", StringComparison.Ordinal)
         .Replace("$", "\\$", StringComparison.Ordinal)
         .Replace("`", "\\`", StringComparison.Ordinal);

    private static void ValidateEnvKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Environment key cannot be empty.", nameof(key));
        foreach (var ch in key)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                throw new ArgumentException($"Environment key '{key}' contains invalid character '{ch}'.", nameof(key));
        }
    }

    private static string TruncateForLog(string s, int max = 200)
        => RemoteMultipassText.TruncateForLog(s, max);
}

/// <summary>
/// Cross-assembly value-type DTO mirroring <see cref="CodeyBox.HostProcess.ProcessRunResult"/>
/// fields the remote provider cares about. Keeping it internal avoids leaking
/// the HostProcess dependency into Core through this provider's public surface.
/// </summary>
internal readonly record struct ProcessRunResultLike(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutLimitExceeded = false,
    bool StderrLimitExceeded = false);

internal sealed record RemoteHostUnhealthyState(DateTimeOffset Until, string Reason);

internal sealed record PlacementInventory(
    IReadOnlyDictionary<string, int> ManagedCounts,
    Exception? LastFailure);

internal readonly record struct RemoteSandboxIdentity(string HostId, string Name);

internal sealed class RemoteHostProvisioningException : Exception
{
    public RemoteHostProvisioningException(
        string hostId,
        string operation,
        string message,
        bool isHostRuntimeFailure = false)
        : base(message)
    {
        HostId = hostId;
        Operation = operation;
        IsHostRuntimeFailure = isHostRuntimeFailure;
    }

    public string HostId { get; }
    public string Operation { get; }
    public bool IsHostRuntimeFailure { get; }
}

/// <summary>
/// One bind-mount that has been staged to the remote host's filesystem so
/// the remote multipass daemon can <c>mount</c> it into the VM. Tracks
/// whether the mount was writable so disposal can sync staged contents back
/// to the orchestrator host (e.g. the bare git repo after a successful
/// <c>git push</c>).
/// </summary>
internal sealed record StagedBindMount(
    string SandboxPath,
    string RemoteStagedPath,
    string? HostPath,
    bool ReadOnly,
    string? SyncBackHostPath);
