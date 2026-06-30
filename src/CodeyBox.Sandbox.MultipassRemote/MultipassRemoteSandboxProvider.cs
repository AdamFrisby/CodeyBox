using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
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
    private readonly Func<MultipassRemoteSandboxOptions> _optsAccessor;
    private readonly Func<MultipassRemoteSandboxOptions, IRemoteHostTransport> _transportFactory;
    private readonly ILogger<MultipassRemoteSandboxProvider> _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _heavyMultipassGates =
        new(StringComparer.Ordinal);
    private readonly object _placementLock = new();
    private readonly Dictionary<string, int> _hostReservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteHostUnhealthyState> _runtimeUnhealthy =
        new(StringComparer.Ordinal);

    // Tracks sandboxes still owned by a currently-running phase in this process.
    // Used by ListAllManagedAsync to compute ManagedSandboxInfo.IsTrackedActive.
    private readonly ConcurrentDictionary<string, MultipassRemoteSandbox> _active =
        new(StringComparer.Ordinal);

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
        RemoteSshTransportException? lastTransportFailure = null;

        while (true)
        {
            HostReservation reservation;
            try
            {
                reservation = ReserveHost(spec, skippedHosts);
            }
            catch (SandboxProvisioningDeferredException ex) when (lastTransportFailure is not null)
            {
                throw new SandboxProvisioningDeferredException(
                    ex.Provider,
                    ex.Operation,
                    "all-hosts-unreachable",
                    $"{ex.Detail}; last transport failure: {lastTransportFailure.Message}",
                    ex.RecheckIn,
                    innerException: lastTransportFailure);
            }

            var opts = reservation.HostOptions;
            var transport = _transportFactory(opts);
            var vmName = NewVmName(opts);
            var remoteSandboxRoot = JoinRemote(opts.RemoteStagingRoot, vmName);
            var remoteFsRoot = JoinRemote(remoteSandboxRoot, "fs");

            try
            {
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
            catch (RemoteSshTransportException ex)
            {
                lastTransportFailure = ex;
                skippedHosts.Add(opts.HostId);
                MarkRuntimeUnhealthy(opts, ex);
                reservation.Dispose();
                await BestEffortRemoteDeleteAsync(opts, transport, vmName, remoteSandboxRoot).ConfigureAwait(false);

                _log.LogWarning(
                    ex,
                    "Remote multipass host {HostId} transport failed during CreateAsync; retrying placement on another eligible host",
                    opts.HostId);
            }
            catch
            {
                reservation.Dispose();
                // Best-effort cleanup of any partial remote state. We DO NOT
                // log here at error level — the original exception is the real
                // story and is about to be rethrown by the caller's try.
                await BestEffortRemoteDeleteAsync(opts, transport, vmName, remoteSandboxRoot).ConfigureAwait(false);
                throw;
            }
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
        await EnsureRemoteStagingDirAsync(transport, remoteSandboxRoot, ct).ConfigureAwait(false);

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
            onDispose: name =>
            {
                _active.TryRemove(name, out _);
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
        if (!string.IsNullOrWhiteSpace(spec.ImageReference) && !string.Equals(spec.ImageReference, "ignored", StringComparison.Ordinal))
            launchArgv.Add(spec.ImageReference);
        else if (!string.IsNullOrWhiteSpace(opts.DefaultImage))
            launchArgv.Add(opts.DefaultImage!);

        try
        {
            // Track before clone/launch makes the VM visible to multipass list.
            // Otherwise a concurrent leak sweep can mistake an in-progress remote
            // clone with no CreatedAt metadata for an old orphan and purge it.
            _active[vmName] = sandbox;

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
            _active.TryRemove(vmName, out _);
            throw;
        }

        SandboxLiveCounter.Increment();
        reservation.TransferToSandbox();
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

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var infos = new List<ManagedSandboxInfo>();
        foreach (var opts in ResolveHosts())
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
                result = await RunRemoteAsync(transport, argv, ct).ConfigureAwait(false);
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

            if (result.ExitCode != 0)
            {
                _log.LogWarning("multipass list (remote host {HostId}) exited {Exit}: {Stderr}",
                    opts.HostId, result.ExitCode, result.Stderr);
                continue;
            }

            try
            {
                AddManagedFromListJson(infos, opts, result.Stdout);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex,
                    "Failed to parse remote multipass list JSON from host {HostId}; skipping host",
                    opts.HostId);
            }
        }

        return infos;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_active.TryGetValue(name, out var active))
        {
            await active.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var hosts = ResolveHosts();
        if (!hosts.Any(h => name.StartsWith(h.VmNamePrefix, StringComparison.Ordinal)))
        {
            _log.LogWarning(
                "Refusing to dispose VM '{Name}' — does not match any configured remote VM prefix ({Prefixes})",
                name,
                string.Join(", ", hosts.Select(h => h.VmNamePrefix).Distinct(StringComparer.Ordinal)));
            return;
        }

        // Honor the reaper's cancellation token: if the orchestrator is
        // shutting down, abandon this sweep — the leak isn't going anywhere
        // and the next sweep will retry. The CreateAsync rollback path uses a
        // different overload pinned to CancellationToken.None.
        foreach (var opts in hosts)
        {
            if (!name.StartsWith(opts.VmNamePrefix, StringComparison.Ordinal))
                continue;

            var transport = _transportFactory(opts);
            await BestEffortRemoteDeleteAsync(
                opts,
                transport,
                name,
                JoinRemote(opts.RemoteStagingRoot, name),
                ct).ConfigureAwait(false);
        }
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
        foreach (var (name, sb) in _active)
        {
            if (sb.OwningWorkItemId is { } id)
                snap.Add(new ActiveSandboxProgress(id, name, Status: $"running host={sb.HostId}"));
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
            throw new InvalidOperationException(
                $"Remote multipass command failed (exit {r.ExitCode}): " +
                $"argv=[{string.Join(' ', argv)}] stderr={TruncateForLog(r.Stderr)}");
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
            return await RunRemoteAsync(transport, argv, ct).ConfigureAwait(false);

        var gate = _heavyMultipassGates.GetOrAdd(opts.HostId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunRemoteAsync(transport, argv, ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        var native = await transport.RunAsync(
            argv,
            stdin,
            ct,
            stdoutChunkCallback: stdoutChunkCallback,
            stderrChunkCallback: stderrChunkCallback).ConfigureAwait(false);
        return new ProcessRunResultLike(native.ExitCode, native.Stdout, native.Stderr, native.StdoutLimitExceeded, native.StderrLimitExceeded);
    }

    private async Task EnsureRemoteStagingDirAsync(IRemoteHostTransport transport, string remoteSandboxRoot, CancellationToken ct)
    {
        // 0700 on the per-sandbox dir: only the SSH user can list its
        // contents. Per-staged-source subdirs sit under here.
        var mkdirCmd = $"mkdir -p {OpenSshCliTransport.QuoteShellWord(remoteSandboxRoot)} && chmod 0700 {OpenSshCliTransport.QuoteShellWord(remoteSandboxRoot)}";
        var r = await transport.RunAsync(["sh", "-c", mkdirCmd], stdin: null, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to create remote sandbox staging dir '{remoteSandboxRoot}' (exit {r.ExitCode}): {TruncateForLog(r.Stderr)}");
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
        var r = await transport.RunAsync(argv, stdin: lines.ToString(), ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to apply env to remote VM '{vmName}' (exit {r.ExitCode}): {TruncateForLog(r.Stderr)}");
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
            var r = await RunRemoteAsync(
                transport,
                [opts.RemoteMultipassPath, "info", vmName, "--format", "json"],
                ct).ConfigureAwait(false);
            if (r.ExitCode == 0 && TryParseVmState(r.Stdout, vmName, out var state) && string.Equals(state, targetState, StringComparison.Ordinal))
                return;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Remote VM '{vmName}' did not reach state '{targetState}' within {timeout}.");

            try { await Task.Delay(opts.VmStateCheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
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

    internal Task BestEffortRemoteDeleteAsync(string vmName, string remoteSandboxRoot)
    {
        var opts = ResolveHosts()[0];
        return BestEffortRemoteDeleteAsync(opts, _transportFactory(opts), vmName, remoteSandboxRoot, CancellationToken.None);
    }

    internal async Task BestEffortRemoteDeleteAsync(string vmName, string remoteSandboxRoot, CancellationToken ct)
    {
        var opts = ResolveHosts()[0];
        await BestEffortRemoteDeleteAsync(opts, _transportFactory(opts), vmName, remoteSandboxRoot, ct).ConfigureAwait(false);
    }

    internal Task BestEffortRemoteDeleteAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        string remoteSandboxRoot)
        => BestEffortRemoteDeleteAsync(opts, transport, vmName, remoteSandboxRoot, CancellationToken.None);

    internal async Task BestEffortRemoteDeleteAsync(
        MultipassRemoteSandboxOptions opts,
        IRemoteHostTransport transport,
        string vmName,
        string remoteSandboxRoot,
        CancellationToken ct)
    {
        // Callers pass CancellationToken.None when the cleanup MUST run
        // regardless of outer cancellation (e.g. CreateAsync rollback after a
        // partial launch); the leak reaper passes its own token so it can
        // abandon mid-sweep on orchestrator shutdown.
        try
        {
            await RunRemoteMaybeGatedAsync(
                opts,
                transport,
                [opts.RemoteMultipassPath, "delete", "--purge", vmName],
                ct).ConfigureAwait(false);
            await transport.RunAsync(
                ["rm", "-rf", remoteSandboxRoot],
                stdin: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honored cancellation — not a failure. Let it propagate so the
            // caller (reaper) can finish its shutdown promptly.
            throw;
        }
        catch (RemoteSshTransportException ex)
        {
            // We can't reach the remote host to clean up. Surface as a leak
            // — the next start-up sweep / reaper will retry.
            MarkRuntimeUnhealthy(opts, ex);
            _log.LogWarning(ex,
                "Best-effort remote cleanup of {Vm} on host {HostId} failed; leaving for future leak reaper sweep",
                vmName,
                opts.HostId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Best-effort remote cleanup of {Vm} on host {HostId} failed", vmName, opts.HostId);
        }
    }

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
        }

        return hosts;
    }

    private HostReservation ReserveHost(SandboxSpec spec, ISet<string> skippedHosts)
    {
        var hosts = ResolveHosts();
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
                if (reserved >= capacity)
                {
                    blocked.Add($"{host.HostId}=full({reserved}/{FormatCapacity(capacity)})");
                    continue;
                }

                var load = capacity == int.MaxValue ? 0.0d : (double)reserved / capacity;
                if (selected is null
                    || load < selectedLoad
                    || (Math.Abs(load - selectedLoad) < double.Epsilon
                        && reserved < selectedReserved)
                    || (Math.Abs(load - selectedLoad) < double.Epsilon
                        && reserved == selectedReserved
                        && string.CompareOrdinal(host.HostId, selected.HostId) < 0))
                {
                    selected = host;
                    selectedReserved = reserved;
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

            _hostReservations[selected.HostId] = selectedReserved + 1;
            CodeyBoxMeters.SandboxRemotePlacements.Add(
                1,
                new KeyValuePair<string, object?>("host_id", selected.HostId),
                new KeyValuePair<string, object?>("outcome", "reserved"));
            _log.LogDebug(
                "Remote sandbox placement reserved host {HostId}: {Reserved}/{Capacity} for network profile {NetworkProfile}",
                selected.HostId,
                selectedReserved + 1,
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

    private int ReservedForHost(string hostId)
    {
        lock (_placementLock)
            return _hostReservations.TryGetValue(hostId, out var current) ? current : 0;
    }

    private void MarkRuntimeUnhealthy(MultipassRemoteSandboxOptions host, RemoteSshTransportException ex)
    {
        var until = DateTimeOffset.UtcNow + host.RuntimeUnhealthyBackoff;
        _runtimeUnhealthy[host.HostId] = new RemoteHostUnhealthyState(until, ex.Message);
        CodeyBoxMeters.SandboxRemoteHostHealthTransitions.Add(
            1,
            new KeyValuePair<string, object?>("host_id", host.HostId),
            new KeyValuePair<string, object?>("state", "unhealthy"));
        _log.LogWarning(
            "Remote executor host {HostId} marked runtime-unhealthy until {Until:O}: {Reason}",
            host.HostId,
            until,
            ex.Message);
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
                _runtimeUnhealthy.TryRemove(hostId, out _);
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
            if (string.Equals(value, profile, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string NormalizeNetworkProfile(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName) ? "(default)" : profileName.Trim();

    private void AddManagedFromListJson(
        List<ManagedSandboxInfo> infos,
        MultipassRemoteSandboxOptions opts,
        string json)
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
            if (!name.StartsWith(opts.VmNamePrefix, StringComparison.Ordinal)) continue;

            var isTrackedActive = _active.ContainsKey(name);
            var state = entry.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var isSuspendOrFreezing = state is "Suspended" or "Suspending" or "Freezing";

            infos.Add(new ManagedSandboxInfo(
                Name: name,
                CreatedAt: null,
                DiskBytes: null,
                IsTrackedActive: isTrackedActive,
                HasPreemptMarker: false,
                IsSuspendLifecycleOrFrozen: isSuspendOrFreezing));
        }
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

        public void TransferToSandbox()
        {
            // Ownership moves to the sandbox's dispose callback; the same
            // HostReservation instance is still the release token.
        }

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

    private static string NewVmName(MultipassRemoteSandboxOptions opts)
    {
        // multipass instance name limit is 24 chars. Prefix + 22-char hex
        // gives 24 when prefix is "codeybox-r-" (11 chars) + ~13 hex chars,
        // so we trim accordingly.
        var prefix = opts.VmNamePrefix;
        var hex = Guid.NewGuid().ToString("N");
        var budget = 24 - prefix.Length;
        if (budget <= 0)
            throw new InvalidOperationException($"VmNamePrefix '{prefix}' leaves no budget for a 24-char VM name.");
        return prefix + hex[..Math.Min(budget, hex.Length)];
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
    {
        if (string.IsNullOrEmpty(s)) return "";
        var trimmed = s.Trim();
        if (trimmed.Length <= max) return trimmed;
        return trimmed[..max] + "…";
    }
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
