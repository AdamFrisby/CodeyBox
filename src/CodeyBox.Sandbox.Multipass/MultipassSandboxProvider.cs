using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Multipass;

/// <summary>
/// Sandbox provider backed by Canonical Multipass. Each sandbox is a real
/// Ubuntu VM with its own kernel — a kernel exploit in the agent escapes
/// into a VM that gets destroyed when the sandbox is disposed, never
/// reaching the host kernel.
///
/// <para><b>Why this exists:</b> the threat model includes "agent fetches
/// a webpage, gets prompt-injected, runs arbitrary commands." Bubblewrap
/// shares the host kernel; a kernel exploit in those commands would reach
/// the host. Multipass gives separate-kernel isolation with a single
/// <c>snap install multipass</c> on Ubuntu — the easiest kernel-isolation
/// path on this OS.</para>
///
/// <para><b>Trade-off:</b> VM launch is ~10-30 seconds. A work item with
/// audit phases launches multiple VMs in sequence and accrues that
/// overhead per phase. Pick this when the threat model justifies it; pick
/// <c>bubblewrap</c> when speed matters more than kernel isolation.</para>
///
/// <para><b>Network policy:</b> enforced ENTIRELY on the host via
/// nftables on per-profile bridges. The provider attaches the VM to the
/// bridge mapped from <c>SandboxNetworkPolicy.ProfileName</c>; the
/// bridge's host-side rules drop everything not on the profile's
/// allowlist. The provider deliberately installs NO in-VM firewall —
/// any in-guest enforcement is voluntary and a compromised agent with
/// sudo could flush it, so we don't pretend it's a boundary.
/// See <c>scripts/setup-host-networks.sh</c> and
/// <c>docs/host-firewall.md</c>.</para>
///
/// <para><b>Image:</b> defaults to Multipass's current LTS Ubuntu image.
/// The agent CLI binaries (claude, codex, etc.) need to be installed in
/// the VM. Operators provide an additional cloud-init fragment via
/// <see cref="MultipassSandboxOptions.ExtraCloudInit"/> to install agents
/// on first boot, OR build a Multipass image with agents pre-installed
/// and reference it via <see cref="SandboxSpec.ImageReference"/>.</para>
/// </summary>
public sealed class MultipassSandboxProvider : ISandboxProvider, IActiveSandboxProvider, IActiveSandboxProgressProvider, IDiskGuardedSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver, IBaselineImageProvisioner
{
    // Options are resolved through a delegate once per public operation so an
    // operator can edit ExtraRuncmd / ExtraCloudInit / NetworkProfiles /
    // UseBaselineImages in appsettings.json and have the change land on the next
    // sandbox launch without restarting CodeyBox. Each in-flight launch and each
    // constructed sandbox keep the snapshot they started with. Operators editing
    // immutable fields (StagingDirectory, MultipassBinary, etc.) still need to
    // restart — _stagingRoot below is fixed at provider construction.
    private readonly Func<MultipassSandboxOptions> _optsAccessor;
    private readonly ILogger<MultipassSandboxProvider> _log;
    private readonly IProcessRunner _runner;
    private readonly MultipassDaemonRetryPolicy _daemonRetryPolicy;
    private readonly string _stagingRoot;
    private readonly ITimingStore? _timings;
    private readonly IDiskSpaceProbe _diskProbe;
    // Per-baseline-name semaphore: serialises bake operations so two
    // concurrent CreateAsync calls for the same profile don't both try to
    // launch the same baseline VM. Lazily populated.
    private readonly Dictionary<string, SemaphoreSlim> _baselineLocks = new();
    private readonly object _baselineLocksGuard = new();
    private readonly ConcurrentDictionary<string, BaselineTarget> _baselineTargets = new(StringComparer.Ordinal);

    private readonly record struct BaselineTarget(string ProfileName, SandboxProfileFlavor Flavor);
    private sealed record BaselineTargetMetadata(string ProfileName, string Flavor);

    // Tracks sandboxes still owned by a currently-running phase in this process.
    // Used by ListAllManagedAsync to compute ManagedSandboxInfo.IsTrackedActive.
    private readonly ConcurrentDictionary<string, bool> _activeSandboxNames = new(StringComparer.Ordinal);

    // Parallel registry keyed by sandbox name with the owning work item and a
    // back-reference to the live MultipassSandbox object. Populated only when
    // CreateAsync receives a SandboxSpec carrying TimingWorkItemId — that field
    // is set for every real pipeline phase, so the registry covers everything
    // the orchestrator might want to handle during shutdown teardown. Tests that call
    // CreateAsync without a work item are intentionally absent.
    private readonly ConcurrentDictionary<string, ActiveSandboxOwnerEntry> _activeSandboxOwners = new(StringComparer.Ordinal);

    private sealed record ActiveSandboxOwnerEntry(WorkItemId WorkItemId, MultipassSandbox Sandbox);

    // Test seam: override the RAM-scaled Suspending-settle budget used by
    // WaitWhileSuspendingAsync. Production leaves this null and derives the
    // budget from SuspendTimeoutPolicy (floored at 10 min), which is far too
    // long for a unit test to wait out; tests set a tiny value to exercise the
    // deadline-expiry branch (proceed to `multipass start` while still
    // Suspending) without controlling wall-clock time.
    internal TimeSpan? SuspendSettleBudgetOverride { get; set; }

    // Test seam: override the WaitForAdoptedAgentCompletionAsync poll interval.
    // Production polls every 2s; tests set a small value so the loop is not
    // wall-clock-bound (a real 2s Task.Delay can drift well past a short test
    // deadline under thread-pool starvation, making the test flaky).
    internal TimeSpan? AdoptionPollIntervalOverride { get; set; }

    // Cache for ListAllManagedAsync results to avoid hammering multipassd.
    private IReadOnlyList<ManagedSandboxInfo>? _listCache;
    private DateTimeOffset _listCacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _listCacheTtl = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _listLock = new(1, 1);

    // Provisioning throttle: limits how many multipass launch/start
    // operations execute concurrently. Decoupled from
    // WorkerPool.MaxConcurrentWorkers so workers can be running while only
    // a few VMs boot at once, preventing host CPU/IO contention from
    // exceeding the 180 s 'reach Running' start timeout.
    private readonly object _bootGateGuard = new();
    private SemaphoreSlim? _bootGate;
    private int _bootGateCapacity;

    public MultipassSandboxProvider(MultipassSandboxOptions opts, ILogger<MultipassSandboxProvider> log,
        ITimingStore? timings = null)
        : this(() => opts, log, timings, new DefaultProcessRunner())
    {
    }

    public MultipassSandboxProvider(Func<MultipassSandboxOptions> optionsAccessor,
        ILogger<MultipassSandboxProvider> log, ITimingStore? timings = null)
        : this(optionsAccessor, log, timings, new DefaultProcessRunner())
    {
    }

    internal MultipassSandboxProvider(MultipassSandboxOptions opts, ILogger<MultipassSandboxProvider> log,
        ITimingStore? timings, IProcessRunner runner, MultipassDaemonRetryPolicy? daemonRetryPolicy = null,
        IDiskSpaceProbe? diskProbe = null)
        : this(() => opts, log, timings, runner, daemonRetryPolicy, diskProbe)
    {
    }

    internal MultipassSandboxProvider(Func<MultipassSandboxOptions> optionsAccessor,
        ILogger<MultipassSandboxProvider> log, ITimingStore? timings, IProcessRunner runner,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null,
        IDiskSpaceProbe? diskProbe = null)
    {
        _optsAccessor = optionsAccessor;
        _log = log;
        _runner = runner;
        _daemonRetryPolicy = daemonRetryPolicy ?? MultipassDaemonRetryPolicy.Default;
        _timings = timings;
        _diskProbe = diskProbe ?? new DefaultDiskSpaceProbe();
        // StagingDirectory is captured once: the provider keeps the directory open
        // for the lifetime of the process. Re-binding it at runtime would orphan
        // already-staged sandboxes.
        _stagingRoot = ResolveStagingRoot(ReadOptions());
        Directory.CreateDirectory(_stagingRoot);
        // 0700 on the staging root: only the orchestrator user can read or
        // list its contents. Per-sandbox subdirs sit under here, each
        // containing their own host bind-mount sources and cloud-init files.
        // Without this, default 0755 perms would let other host users walk
        // every sandbox's staging dir.
        TryChmod0700(_stagingRoot);
    }

    internal static void TryChmod0700(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best-effort: not all filesystems honour mode bits */ }
    }

    /// <summary>
    /// When Multipass is installed as a snap (the standard path on Ubuntu),
    /// the daemon is AppArmor-confined and CANNOT read arbitrary paths like
    /// /tmp. Files passed to <c>--cloud-init</c> and bind-mount sources both
    /// need to live under <c>~/snap/multipass/common/</c>, which is in
    /// Multipass's allowed read set.
    ///
    /// We auto-detect: prefer <c>~/snap/multipass/common/codeybox-staging</c>
    /// if it exists (snap install); fall back to <c>/tmp</c> otherwise
    /// (non-snap installs, e.g. on macOS). Operators can override via
    /// <see cref="MultipassSandboxOptions.StagingDirectory"/>.
    /// </summary>
    private static string ResolveStagingRoot(MultipassSandboxOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.StagingDirectory))
            return opts.StagingDirectory;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var snapCommon = Path.Combine(home, "snap", "multipass", "common");
            if (Directory.Exists(snapCommon))
                return Path.Combine(snapCommon, "codeybox-staging");
        }
        return Path.Combine(Path.GetTempPath(), "codeybox-mp-staging");
    }

    public string Name => "multipass";
    // HTTP ingest and detached launch are spec-dependent: the concrete sandbox
    // advertises them only when its network profile resolves to a host bridge.
    public SandboxAgentOutputTransportKind AgentOutputTransportKind => SandboxAgentOutputTransportKind.ExecPipe;
    public SandboxBatchLaunchMode BatchLaunchMode => SandboxBatchLaunchMode.Attached;

    private void MarkTrackedActive(string name)
    {
        _activeSandboxNames[name] = true;
        _listCacheExpiry = DateTimeOffset.MinValue;
    }

    private void MarkNoLongerActive(string name)
    {
        _activeSandboxNames.TryRemove(name, out _);
        _activeSandboxOwners.TryRemove(name, out _);
        _listCacheExpiry = DateTimeOffset.MinValue;
    }

    private IReadOnlyList<string> DiskGuardPaths
    {
        get
        {
            // Resolve through ReadOptions so live-edits to DiskGuard config land
            // on the next call without restart, matching the rest of the provider.
            if (ReadOptions().DiskGuard is not { } guard) return [];
            var result = new List<string> { guard.MultipassDataPath };
            foreach (var extra in guard.AdditionalPaths)
            {
                if (!string.IsNullOrWhiteSpace(extra) && !result.Contains(extra, StringComparer.Ordinal))
                    result.Add(extra);
            }
            return result;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiskGuardSample> SampleDiskGuardState()
    {
        if (ReadOptions().DiskGuard is not { } guard) return [];
        var paths = DiskGuardPaths;
        var result = new List<DiskGuardSample>(paths.Count);
        foreach (var p in paths)
            result.Add(new DiskGuardSample(p, _diskProbe.GetFreeBytes(p), guard.MinFreeBytes));
        return result;
    }

    private void PreflightDiskOrThrow()
    {
        if (ReadOptions().DiskGuard is not { } guard) return;
        foreach (var path in DiskGuardPaths)
        {
            var free = _diskProbe.GetFreeBytes(path);
            if (free is null) continue; // inconclusive — don't block on a missing mount
            if (free.Value < guard.MinFreeBytes)
            {
                _log.LogWarning(
                    "Disk preflight: {Path} has {FreeBytes:N0} free, below threshold {Threshold:N0}; deferring sandbox launch",
                    path, free.Value, guard.MinFreeBytes);
                throw new SandboxDiskDeferredException(path, free.Value, guard.MinFreeBytes, guard.RecheckIn);
            }
        }
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        spec = SandboxConventions.WithTimingEnvironment(spec);

        // Preflight: refuse to launch when the host is out of disk. Without
        // this check, multipass / qemu happily start the VM, the install
        // runcmd or the orchestrator's first big artifact write hits
        // ENOSPC, and the work item burns tokens before failing in a way
        // the orchestrator can't gracefully recover from. The deferred
        // exception bubbles to the worker loop, which schedules a re-pickup
        // — same machinery as the budget cap path.
        PreflightDiskOrThrow();

        var opts = ReadOptions();
        var name = $"codeybox-{Guid.NewGuid():N}"[..23]; // multipass max name length is 24
        var sandboxRoot = Path.Combine(_stagingRoot, name);
        Directory.CreateDirectory(sandboxRoot);
        // Lock down per-sandbox dir to operator-only. Defence in depth:
        // even if the orchestrator's path handling has a bug that crosses
        // sandbox roots, OS perms prevent another user (or another
        // process not running as us) from reading another sandbox's data.
        TryChmod0700(sandboxRoot);

        // Pre-create host directories for tmpfs-equivalent mounts so we can
        // bind-mount them into the VM after launch.
        var bindMounts = new List<BindMount>();
        foreach (var m in spec.Mounts)
        {
            if (m.Tmpfs)
            {
                var hostPath = Path.Combine(sandboxRoot, "fs" + m.SandboxPath.Replace('/', '-'));
                Directory.CreateDirectory(hostPath);
                TryChmod0700(hostPath);
                bindMounts.Add(new BindMount(hostPath, m.SandboxPath, ReadOnly: false));
            }
            else if (m.HostPath is not null)
            {
                var hostPath = m.SnapshotForIsolation
                    ? StageReadOnlyBindSnapshot(sandboxRoot, m.HostPath, m.SandboxPath)
                    : m.HostPath;
                bindMounts.Add(new BindMount(hostPath, m.SandboxPath, m.ReadOnly));
            }
        }

        // Resolve timing context from the spec (null → no timing emitted).
        var timingStore = _timings is not null && spec.TimingWorkItemId.HasValue ? _timings : null;
        var timingItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var workItemId = spec.TimingWorkItemId;
        var timingPhase = spec.TimingPhase ?? "work";

        try
        {
            // Track ownership before the VM becomes host-visible. A slow launch,
            // clone, cloud-init wait, mount, or environment transfer can overlap a
            // leak-reaper sweep; once multipass lists this name, it must already be
            // protected as an in-flight phase sandbox.
            MarkTrackedActive(name);

            // Choose between two boot paths:
            //   - Baseline-clone path (UseBaselineImages=true + profile is set):
            //     bake one VM per profile lazily on first use, then `multipass
            //     clone` from it for every subsequent sandbox. Pays the
            //     install runcmd cost once per profile instead of per sandbox.
            //   - Launch path (default): every VM goes through cloud-init.
            //     Slower per VM but works without prior baking.
            var useBaseline = opts.UseBaselineImages
                && !string.IsNullOrWhiteSpace(spec.Network.ProfileName);

            // After this block the VM is in Stopped state, ready for native
            // mounts. The launch path goes through a stop-mount-start cycle;
            // the clone path skips the start (clone is born Stopped).
            if (useBaseline)
            {
                var baselineName = await EnsureBaselineForProfileAsync(opts, spec.Network.ProfileName!, spec.Flavor, workItemId, spec.BaselineImageRef, ct);
                await using var cloneScope = await TimingScope.BeginAsync(
                    timingStore, timingItemId, timingPhase, "vm.clone", log: _log);
                await CloneFromBaselineAsync(opts, name, baselineName, workItemId, ct);
                // Clone is Stopped after `multipass clone`; no start yet.
            }
            else
            {
                var cloudInit = BuildCloudInit(opts.ExtraRuncmd, opts.ExtraCloudInit, spec.Flavor);
                var cloudInitPath = Path.Combine(sandboxRoot, "cloud-init.yaml");
                await File.WriteAllTextAsync(cloudInitPath, cloudInit, ct);
                // The op-gate inside RunAsync now bounds the `launch` CLI
                // invocation against the heavy-op semaphore; the subsequent
                // `info` polls in WaitForRunningAsync classify as light and
                // run uncontended. No outer wrap needed.
                await using (var launchScope = await TimingScope.BeginAsync(
                    timingStore, timingItemId, timingPhase, "vm.launch", log: _log))
                {
                    await LaunchAsync(opts, name, spec, cloudInitPath, workItemId, ct);
                    await WaitForRunningAsync(opts, name, workItemId, ct);
                }
                // Stop the freshly-launched VM so we can mount (outside vm.launch scope).
                var stop = await RunAsync(opts, [opts.MultipassBinary, "stop", name], stdin: null, ct: ct, workItemId: workItemId);
                if (stop.ExitCode != 0)
                {
                    ThrowIfProvisioningRetryExhausted("stop", stop);
                    throw new InvalidOperationException($"multipass stop (for mount) failed: {stop.Stderr}");
                }
                await WaitForStoppedAsync(opts, name, workItemId, ct);
            }

            // Apply native mounts while VM is Stopped, then start.
            await using (var mountScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.mount", log: _log))
            {
                await ApplyMountsAsync(opts, name, bindMounts, workItemId, ct);
            }

            // StartAndWaitForRunningAsync issues `multipass start` (heavy,
            // gated inside RunAsync) followed by `info` polls (light,
            // ungated). No outer wrap needed.
            await using (var startScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.start", log: _log))
            {
                await StartAndWaitForRunningAsync(opts, name, workItemId, ct);
            }

            // Native (virtiofs) mounts are registered while the VM is
            // Stopped, but multipass returns from `start` as soon as
            // QEMU is Running — the guest-side mount attach can lag by
            // seconds under audit-parallelism load. Without this gate
            // the first in-VM mount consumer (typically
            // `git clone /repo /work` on the work-item pickup path)
            // races the attach and exits 128 with
            // "fatal: repository '/repo' does not exist". Poll each
            // declared bind mount inside the VM before handing the
            // sandbox back to the pipeline.
            await using (var readinessScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.mount-readiness", log: _log))
            {
                await WaitForMountsVisibleAsync(opts, name, bindMounts.ConvertAll(b => (b.Host, b.Sandbox)), workItemId, ct);
            }

            await using (var readOnlyMountScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.mount.readonly", log: _log))
            {
                await ApplyReadOnlyMountsAsync(opts, name, bindMounts, workItemId, ct);
            }

            await TransferEnvAsync(opts, name, spec.Environment, sandboxRoot, workItemId, ct);
            AuditLog.SandboxCreated(name, spec.Network.ProfileName);
            // The exec wrapper is installed by cloud-init at boot
            // (see BuildCloudInit's write_files); on the clone path it's
            // already baked into the source VM's filesystem, so the clone
            // inherits it. The codeybox-route systemd service runs on every
            // boot in both paths.
            var sandbox = new MultipassSandbox(name, sandboxRoot, spec, opts, _log, timingStore, timingItemId, timingPhase,
                onDisposed: MarkNoLongerActive,
                onNoLongerTrackedActive: MarkNoLongerActive,
                runner: _runner,
                daemonRetryPolicy: _daemonRetryPolicy,
                // Share the provider-wide heavy-op gate with the
                // sandbox-instance lifecycle ops (transfer / stop /
                // delete / suspend), so VM lifecycle calls and orchestrator
                // provisioning calls compete for the same semaphore slots
                // rather than each opening its own unbounded path to the
                // multipass daemon.
                opGateAcquirer: (argv, ct) => EnterMultipassOpGateAsync(ReadOptions(), argv, ct));
            // Register in the owner index ONLY when a work-item ID is present.
            // Sandboxes created without one (some tests) have no orchestrator-side
            // owner to suspend back into, so skip them.
            if (workItemId is { } owner)
                _activeSandboxOwners[name] = new ActiveSandboxOwnerEntry(owner, sandbox);
            return sandbox;
        }
        catch (Exception ex)
        {
            // Best-effort cleanup if launch / mount / transfer half-succeeded.
            var deleted = false;
            try { deleted = await TryDeleteVmAsync(opts, name); }
            finally { MarkNoLongerActive(name); }
            try { Directory.Delete(sandboxRoot, recursive: true); } catch { }
            if (!deleted && await SandboxMayStillExistAfterFailedDeleteAsync(opts, name))
            {
                throw new SandboxProvisioningDeferredException(
                    Name,
                    "create-cleanup",
                    "multipass-delete-purge-failed",
                    $"create failed and best-effort delete --purge did not prove sandbox {name} was removed: {ex.Message}",
                    _daemonRetryPolicy.ExhaustedRequeueDelay,
                    retainedSandboxName: name,
                    innerException: ex);
            }
            throw;
        }
    }

    internal static string StageReadOnlyBindSnapshot(string sandboxRoot, string sourcePath, string sandboxPath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var snapshotRoot = Path.Combine(sandboxRoot, "readonly-binds");
        Directory.CreateDirectory(snapshotRoot);
        TryChmod0700(snapshotRoot);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceFullPath + "\0" + sandboxPath)))[..16];
        var destination = Path.Combine(snapshotRoot, hash);
        CopyFileSystemSnapshot(sourceFullPath, destination);
        return destination;
    }

    private static void CopyFileSystemSnapshot(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectorySnapshot(new DirectoryInfo(sourcePath), new DirectoryInfo(destinationPath));
            return;
        }

        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return;
        }

        throw new DirectoryNotFoundException($"Read-only sandbox mount source '{sourcePath}' does not exist.");
    }

    private static void CopyDirectorySnapshot(DirectoryInfo source, DirectoryInfo destination)
    {
        Directory.CreateDirectory(destination.FullName);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination.FullName, entry.Name);
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                if (entry is DirectoryInfo)
                    Directory.CreateSymbolicLink(target, entry.LinkTarget);
                else
                    File.CreateSymbolicLink(target, entry.LinkTarget);
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                CopyDirectorySnapshot(directory, new DirectoryInfo(target));
                continue;
            }

            File.Copy(entry.FullName, target, overwrite: true);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadOptions();
        await _listLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_listCache is not null && now < _listCacheExpiry)
                return _listCache;

            var result = await FetchManagedSandboxesAsync(opts, ct);
            _listCache = result;
            _listCacheExpiry = now + _listCacheTtl;
            return result;
        }
        finally
        {
            _listLock.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
    {
        // ConcurrentDictionary enumeration is snapshot-safe; we materialise
        // immediately so the caller's parallel teardown loop sees a stable list
        // even if a sandbox disposes concurrently.
        var entries = _activeSandboxOwners.Values.ToList();
        var result = new List<(WorkItemId, IShutdownTeardownSandbox)>(entries.Count);
        foreach (var entry in entries)
            result.Add((entry.WorkItemId, entry.Sandbox));
        return result;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress()
    {
        var entries = _activeSandboxOwners.Values.ToList();
        var result = new List<ActiveSandboxProgress>(entries.Count);
        foreach (var entry in entries)
            result.Add(entry.Sandbox.SnapshotActiveProgress(entry.WorkItemId));
        return result;
    }

    /// <inheritdoc/>
    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        var opts = ReadOptions();
        if (!IsValidSandboxName(name))
            throw new ArgumentException($"Sandbox name '{name}' contains invalid characters (only [a-z0-9-] allowed).", nameof(name));

        // The previous process may have abandoned `multipass suspend` mid-flight
        // (per-VM suspend timeout fired, then SIGKILL), leaving multipassd still
        // writing the RAM snapshot — the VM lingers in `Suspending`. `multipass
        // start` against a `Suspending` instance fails, which would send the work
        // item to stranded recovery even though the snapshot is about to finish.
        // Wait for the transitional state to settle before starting.
        await WaitWhileSuspendingAsync(opts, name, ct);

        // `multipass start` classifies as heavy and is gated by RunAsync.
        var run = await RunAsync(opts, [opts.MultipassBinary, "start", name], stdin: null, ct: ct);
        // Treat "already running" / "already started" as success: the goal of
        // ResumeSandboxAsync is "VM is Running afterwards", and multipass start
        // on an already-Running VM is the same desired postcondition. Exhausted
        // transient start retries are promoted to SandboxProvisioningDeferredException
        // so startup resume uses the same delayed requeue path as provisioning.
        if (run.ExitCode != 0)
        {
            if (IsStartAlreadyRunning(run))
            {
                _log.LogInformation("multipass start {Name} reported already-running; treating resume as successful", name);
            }
            else
            {
                ThrowIfProvisioningRetryExhausted("start", run);
                throw new InvalidOperationException(
                    $"multipass start {name} failed (exit {run.ExitCode}): {run.Stderr}");
            }
        }
        _listCacheExpiry = DateTimeOffset.MinValue;
        _log.LogInformation("Resumed suspended multipass VM {Name}", name);
    }

    /// <inheritdoc/>
    public async Task<int?> WaitForAdoptedAgentCompletionAsync(
        string vmName,
        string agentLogPath,
        Action<string>? logSink,
        TimeSpan? deadline,
        CancellationToken ct)
    {
        var opts = ReadOptions();
        if (!IsValidSandboxName(vmName))
            throw new ArgumentException($"Sandbox name '{vmName}' contains invalid characters (only [a-z0-9-] allowed).", nameof(vmName));
        if (string.IsNullOrWhiteSpace(agentLogPath))
            return null;
        // Reject paths that contain shell metacharacters or relative segments
        // so the subsequent `multipass exec` arguments cannot be coerced into
        // running an unintended command. Absolute paths only.
        if (!IsValidAgentLogPath(agentLogPath))
            throw new ArgumentException($"Agent log path '{agentLogPath}' is not an allowed in-VM path.", nameof(agentLogPath));

        var exitMarker = agentLogPath + ".exit";
        var startedAt = DateTimeOffset.UtcNow;
        // Track how many bytes of the log file we have already forwarded so a
        // poll round only ships the newly-appended bytes. Reset on transient
        // read failure so we do not silently drop output if the file was
        // rotated by some VM-side tool.
        long offset = 0;
        var pollInterval = AdoptionPollIntervalOverride ?? TimeSpan.FromSeconds(2);
        var maxStdoutBytes = 1024 * 1024;

        while (!ct.IsCancellationRequested)
        {
            if (deadline is { } d && DateTimeOffset.UtcNow - startedAt > d)
            {
                _log.LogWarning(
                    "WaitForAdoptedAgentCompletionAsync({VmName}): exit marker {Marker} did not appear within {Deadline}; giving up — work item will fall through to stranded-item recovery",
                    vmName, exitMarker, d);
                return null;
            }

            // Stream what's new in the log file via `tail -c +offset`. Failure
            // (e.g. file does not exist yet) is non-fatal: keep polling.
            var (newOffset, chunk) = await ReadLogTailAsync(opts, vmName, agentLogPath, offset, maxStdoutBytes, ct);
            if (chunk.Length > 0)
            {
                logSink?.Invoke(chunk);
                offset = newOffset;
            }

            var (exitPresent, exitCode) = await TryReadExitMarkerAsync(opts, vmName, exitMarker, ct);
            if (exitPresent)
            {
                // One final tail to flush bytes appended between the last read
                // and the wrapper's exit-code write.
                var (_, finalChunk) = await ReadLogTailAsync(opts, vmName, agentLogPath, offset, maxStdoutBytes, ct);
                if (finalChunk.Length > 0)
                    logSink?.Invoke(finalChunk);
                _log.LogInformation(
                    "Adopted agent in {VmName} (log={LogPath}) exited with code {ExitCode}",
                    vmName, agentLogPath, exitCode);
                return exitCode;
            }

            try { await Task.Delay(pollInterval, ct); }
            catch (OperationCanceledException) { return null; }
        }

        return null;
    }

    /// <summary>
    /// Tail bytes from <paramref name="agentLogPath"/> starting at byte offset
    /// <paramref name="offset"/>. Returns the new offset (post-read) and the
    /// chunk that was read. Returns empty on any in-VM failure — the caller
    /// keeps polling.
    /// </summary>
    private async Task<(long Offset, string Chunk)> ReadLogTailAsync(
        MultipassSandboxOptions opts,
        string vmName,
        string agentLogPath,
        long offset,
        int maxBytes,
        CancellationToken ct)
    {
        // tail -c +N is 1-indexed: byte N is the first read. Adjust accordingly.
        var byteOffset = (offset + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var argv = new[]
            {
                opts.MultipassBinary, "exec", vmName, "--",
                "sh", "-c", $"tail -c +{byteOffset} -- {ShellSingleQuote(agentLogPath)} 2>/dev/null | head -c {maxBytes}",
            };
            var result = await _runner.RunAsync(argv, stdin: null, ct);
            if (result.ExitCode != 0)
                return (offset, string.Empty);
            return (offset + System.Text.Encoding.UTF8.GetByteCount(result.Stdout), result.Stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "ReadLogTailAsync({VmName}, {LogPath}) threw; will retry next tick", vmName, agentLogPath);
            return (offset, string.Empty);
        }
    }

    private async Task<(bool Present, int ExitCode)> TryReadExitMarkerAsync(
        MultipassSandboxOptions opts,
        string vmName,
        string exitMarker,
        CancellationToken ct)
    {
        try
        {
            var argv = new[]
            {
                opts.MultipassBinary, "exec", vmName, "--",
                "sh", "-c", $"test -f {ShellSingleQuote(exitMarker)} && cat -- {ShellSingleQuote(exitMarker)}",
            };
            var result = await _runner.RunAsync(argv, stdin: null, ct);
            if (result.ExitCode != 0)
                return (false, 0);
            var trimmed = result.Stdout.Trim();
            if (int.TryParse(trimmed, out var code))
                return (true, code);
            // Marker present but unparseable. Treat as completion with an
            // unknown-code sentinel so the orchestrator can act without
            // looping forever on a corrupted file.
            return (true, -1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "TryReadExitMarkerAsync({VmName}, {Marker}) threw; treating as not-yet-present", vmName, exitMarker);
            return (false, 0);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> PushSuspendedVmCheckpointRefAsync(
        string vmName,
        string workingDir,
        string refName,
        string commitMessage,
        CancellationToken ct)
    {
        var opts = ReadOptions();
        if (!IsValidSandboxName(vmName))
            throw new ArgumentException($"Sandbox name '{vmName}' contains invalid characters (only [a-z0-9-] allowed).", nameof(vmName));
        if (!IsValidAbsolutePath(workingDir))
            throw new ArgumentException($"Working directory '{workingDir}' contains invalid characters.", nameof(workingDir));
        if (!IsValidPreemptCheckpointRef(refName))
            throw new ArgumentException($"Ref '{refName}' is not a permitted preempt-checkpoint ref shape.", nameof(refName));
        if (!IsValidCheckpointCommitMessage(commitMessage))
            throw new ArgumentException("Commit message contains invalid characters.", nameof(commitMessage));

        // Single sh -c so a mid-script failure short-circuits via `set -e`
        // instead of (e.g.) pushing a stale HEAD when the commit failed. The
        // scratchpad touch mirrors CheckpointPreemptAsync so the resumable
        // agent runner always finds a non-empty .codeybox/preempt-scratchpad.md
        // to restore from. `--allow-empty` lets the push succeed even when
        // the agent had no dirty changes left after its in-VM exit.
        var script = $@"set -e
cd {ShellSingleQuote(workingDir)}
mkdir -p .codeybox
test -f .codeybox/preempt-scratchpad.md || printf '%s\n' 'No CLI scratchpad was captured before suspend-resume.' > .codeybox/preempt-scratchpad.md
git add -A
git commit --allow-empty -m {ShellSingleQuote(commitMessage)}
git push origin HEAD:{refName}";

        try
        {
            var argv = new[]
            {
                opts.MultipassBinary, "exec", vmName, "--",
                "sh", "-c", script,
            };
            var result = await _runner.RunAsync(argv, stdin: null, ct);
            if (result.ExitCode != 0)
            {
                _log.LogWarning(
                    "PushSuspendedVmCheckpointRefAsync({VmName}, {RefName}): in-VM git push failed (exit {ExitCode}): {Stderr}",
                    vmName, refName, result.ExitCode, result.Stderr);
                return false;
            }
            _log.LogInformation(
                "Pushed adopted-VM HEAD to checkpoint {RefName} from {VmName}",
                refName, vmName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "PushSuspendedVmCheckpointRefAsync({VmName}, {RefName}) threw; treating as push failure",
                vmName, refName);
            return false;
        }
    }

    /// <summary>
    /// Restricts the working-directory argument passed into the in-VM sh -c so a
    /// DB-tamper attacker who flips a recorded path cannot smuggle shell
    /// metacharacters or relative segments through the suspend-checkpoint flow.
    /// Absolute path only; rejects metacharacters that survive single-quoting.
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

    /// <summary>
    /// Enforces the fully-qualified <c>refs/heads/codeybox/preempt/&lt;guid&gt;</c>
    /// shape that the orchestrator uses for preempt checkpoints. The ref is
    /// inlined into <c>git push origin HEAD:&lt;ref&gt;</c> unquoted (because git
    /// rejects single-quoted refs as ambiguous on push), so the validator must
    /// guarantee the suffix contains no shell metacharacters or whitespace.
    /// </summary>
    internal static bool IsValidPreemptCheckpointRef(string refName)
    {
        const string prefix = "refs/heads/codeybox/preempt/";
        if (string.IsNullOrEmpty(refName)) return false;
        if (!refName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = refName[prefix.Length..];
        if (suffix.Length == 0) return false;
        foreach (var ch in suffix)
        {
            // Restrict to [A-Za-z0-9-] which covers the Guid shape and is a
            // strict subset of git's valid ref characters AND of shell-safe
            // characters.
            var ok = (ch >= 'a' && ch <= 'z')
                  || (ch >= 'A' && ch <= 'Z')
                  || (ch >= '0' && ch <= '9')
                  || ch == '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Defence-in-depth: keep the commit message free of characters that could
    /// break out of the single-quoted sh -c argument or smuggle control bytes
    /// into the in-VM git invocation. The caller composes the message from
    /// known-safe components (literal string + WorkItemId guid), so this
    /// check is a guardrail against a future refactor that interpolates an
    /// operator-supplied string.
    /// </summary>
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

    /// <summary>
    /// Allows <c>/work/.codeybox/agent-logs/&lt;name&gt;.log</c>-style absolute
    /// paths and rejects anything with shell metacharacters or relative
    /// segments. Path must be anchored under
    /// <see cref="SandboxConventions.AgentLogDir"/>: defence-in-depth against
    /// a write-only DB-tamper attacker who flips <c>work_items.agent_log_path</c>
    /// to (say) <c>/etc/passwd</c> or <c>/home/ubuntu/.ssh/id_ed25519</c> to
    /// coerce the resume handler into streaming the contents back through the
    /// adopted-agent log forwarder.
    /// </summary>
    internal static bool IsValidAgentLogPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] != '/') return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var ch in path)
        {
            if (ch < 0x20 || ch == 0x7f) return false;
            // Reject shell metacharacters even though we already quote the
            // value: the wrapper's `sh -c` would still expand `$var` /
            // `$(...)` substrings if they survived quoting.
            if (ch is '\'' or '"' or '`' or '$' or '\\' or '\n' or '\r' or '\0') return false;
        }
        // Anchor under AgentLogDir/. Trailing '/' ensures '/work/.codeybox/agent-logs-other'
        // is not accepted by accident.
        const string anchor = SandboxConventions.AgentLogDir + "/";
        if (!path.StartsWith(anchor, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <inheritdoc/>
    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadOptions();
        // Explicit allowlist before any filesystem or shell operation: VM names must
        // be alphanumeric-and-hyphen only. This blocks path-traversal strings such as
        // "codeybox-a/../../../sensitive" that start with the required prefix but
        // would escape _stagingRoot once Path.Combine resolves them.
        if (!IsValidSandboxName(name))
            throw new ArgumentException($"Sandbox name '{name}' contains invalid characters (only [a-z0-9-] allowed).", nameof(name));

        _log.LogInformation("SandboxLeakReaper: purging leaked VM {Name}", name);

        // R8.1 (incident 2026-05-29): if the VM is in a transitional / suspend
        // lifecycle state, qemu is likely holding the disk-image write-lock and
        // `multipass delete --purge` will fail with "Failed to get shared
        // 'write' lock". Try `multipass stop` first to release the lock cleanly
        // when multipassd is still responsive — this is the recovery path the
        // post-incident review documented as "do this first before delete".
        var (state, _) = await TryReadStateAndMemoryAsync(opts, name, ct);
        if (NeedsStopBeforePurge(state))
        {
            _log.LogInformation(
                "DisposeLeakedAsync({Name}): VM is in transitional state '{State}'; attempting stop to release qemu disk-image lock before delete --purge",
                name, state);
            try
            {
                var stop = await RunAsync(opts, [opts.MultipassBinary, "stop", name], stdin: null, ct: ct);
                if (stop.ExitCode != 0)
                    _log.LogWarning(
                        "DisposeLeakedAsync({Name}): multipass stop pre-purge returned exit {ExitCode}: {Stderr}",
                        name, stop.ExitCode, stop.Stderr);
            }
            catch (Exception stopEx) when (stopEx is not OperationCanceledException)
            {
                _log.LogWarning(stopEx,
                    "DisposeLeakedAsync({Name}): multipass stop pre-purge threw; proceeding to delete --purge anyway",
                    name);
            }
        }

        var run = await RunAsync(opts, [opts.MultipassBinary, "delete", "--purge", name], stdin: null, ct: ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"multipass delete --purge {name} failed (exit {run.ExitCode}): {run.Stderr}");
        // Clean up staging dir if it still exists.
        var stagingDir = Path.Combine(_stagingRoot, name);
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
        catch { /* best-effort */ }
        // Remove from active indexes and invalidate cache.
        MarkNoLongerActive(name);
    }

    /// <summary>
    /// True when the VM's state is one in which qemu is likely still holding
    /// the disk-image write-lock — Suspending (mid-snapshot) or Unknown
    /// (qemu present but multipassd cannot describe it, classic symptom of the
    /// wedge from incident 2026-05-29). In those states a clean
    /// <c>multipass stop</c> should precede <c>delete --purge</c>.
    /// </summary>
    internal static bool NeedsStopBeforePurge(string? state) =>
        state is not null &&
        (state.Equals("Suspending", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("Unknown", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
        IReadOnlySet<string> liveSuspendedNames,
        CancellationToken ct)
    {
        // Startup reconciliation: enumerate every managed VM, identify the ones
        // in suspend lifecycle / transitional state with NO live mapping (i.e.
        // orphans from a prior unclean shutdown), and try to bring each back to
        // a clean state via DisposeLeakedAsync. DisposeLeakedAsync now does
        // stop-then-purge for transitional VMs, which is what releases the
        // qemu lock for the wedge case from incident 2026-05-29. VMs the
        // resume handler will reattach (those in liveSuspendedNames) are
        // intentionally untouched.
        IReadOnlyList<ManagedSandboxInfo> managed;
        try
        {
            managed = await ListAllManagedAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "ReconcileStuckSandboxesAsync: failed to list managed sandboxes; nothing to reconcile");
            return [];
        }

        var opts = ReadOptions();
        var unrecoverable = new List<string>();
        foreach (var info in managed)
        {
            if (ct.IsCancellationRequested) break;
            // Live mapping → resume handler owns this VM; do NOT touch.
            if (liveSuspendedNames.Contains(info.Name)) continue;
            // Not in a transitional / suspend-lifecycle state → not the wedge case
            // this reconciler exists for; the leak reaper handles ordinary stale VMs.
            if (!info.IsSuspendLifecycleOrFrozen) continue;
            // A still-tracked-active VM means this very process created it during
            // the current boot — leave it alone, it is not stale.
            if (info.IsTrackedActive) continue;

            // Sample the VM state once so the audit event reports what actually
            // ran. ManagedSandboxInfo.IsSuspendLifecycleOrFrozen is true for
            // both Suspending and Suspended; only the former needs the stop
            // preamble (DisposeLeakedAsync gates on NeedsStopBeforePurge).
            // Without this lookup the audit event hard-codes 'stop+purge' even
            // on the Suspended path that ran purge alone — misleading anyone
            // triaging a future leak from this code path.
            var (state, _) = await TryReadStateAndMemoryAsync(opts, info.Name, ct);
            var action = NeedsStopBeforePurge(state) ? "stop+purge" : "purge";

            _log.LogInformation(
                "Startup reconciler: recovering orphaned VM {Name} (suspend-lifecycle state={State}, no live mapping)",
                info.Name, state);
            try
            {
                await DisposeLeakedAsync(info.Name, ct);
                AuditLog.SandboxStartupReconciled(info.Name, action);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Startup reconciler: could not recover orphaned VM {Name}; operator/root cleanup likely required",
                    info.Name);
                unrecoverable.Add(info.Name);
            }
        }

        return unrecoverable;
    }

    private async Task<IReadOnlyList<ManagedSandboxInfo>> FetchManagedSandboxesAsync(MultipassSandboxOptions opts, CancellationToken ct)
    {
        var listRun = await RunAsync(opts, [opts.MultipassBinary, "list", "--format", "json"], stdin: null, ct: ct);
        if (listRun.ExitCode != 0)
        {
            _log.LogWarning("multipass list failed (exit {ExitCode}): {Stderr}", listRun.ExitCode, listRun.Stderr);
            return [];
        }

        List<string> vmNames;
        try
        {
            using var doc = JsonDocument.Parse(listRun.Stdout);
            if (!doc.RootElement.TryGetProperty("list", out var listEl))
                return [];

            vmNames = [];
            foreach (var item in listEl.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                // Only sandboxes with the codeybox-* prefix (not cb-baseline-*).
                if (!name.StartsWith("codeybox-", StringComparison.Ordinal)) continue;
                vmNames.Add(name);
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse multipass list output");
            return [];
        }

        if (vmNames.Count == 0) return [];

        // Fetch host-visible VM details in a single multipass info call. Disk usage
        // is best-effort; created-at is a fallback when staging metadata was deleted
        // by a previous failed dispose or an older staging-root configuration.
        var detailsByName = await FetchSandboxDetailsAsync(opts, vmNames, ct);

        var infos = new List<ManagedSandboxInfo>(vmNames.Count);
        foreach (var name in vmNames)
        {
            // Validate before using the name in Path.Combine, mirroring the check in
            // DisposeLeakedAsync. Multipass enforces DNS-label naming so this should
            // never fire, but we must not rely on an external tool's input validation
            // for a filesystem path operation.
            if (!IsValidSandboxName(name)) continue;

            DateTimeOffset? createdAt = null;
            var stagingDir = Path.Combine(_stagingRoot, name);
            if (Directory.Exists(stagingDir))
            {
                var created = Directory.GetCreationTimeUtc(stagingDir);
                if (created != DateTime.MinValue)
                    createdAt = new DateTimeOffset(created, TimeSpan.Zero);
            }
            if (!createdAt.HasValue &&
                detailsByName.TryGetValue(name, out var details) &&
                details.CreatedAt.HasValue)
                createdAt = details.CreatedAt;
            var isActive = _activeSandboxNames.ContainsKey(name);
            var hasPreemptMarker = File.Exists(Path.Combine(stagingDir, ".codeybox-preempt"));
            var diskBytes = detailsByName.TryGetValue(name, out details) ? details.DiskBytes : null;
            var state = detailsByName.TryGetValue(name, out details) ? details.State : null;
            infos.Add(new ManagedSandboxInfo(
                name, createdAt, diskBytes > 0 ? diskBytes : null, isActive, hasPreemptMarker,
                IsSuspendLifecycleState(state)));
        }
        return infos;
    }

    /// <summary>
    /// Maps a multipass lifecycle state string to the provider-agnostic
    /// "suspend lifecycle or frozen" flag exposed on <see cref="ManagedSandboxInfo"/>.
    /// True for <c>Suspending</c> (snapshot in progress) and <c>Suspended</c>
    /// (snapshot complete); the multipass state vocabulary stays inside this
    /// provider so Core / the leak reaper see only the boolean.
    /// </summary>
    private static bool IsSuspendLifecycleState(string? state) =>
        state is not null &&
        (state.Equals("Suspending", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("Suspended", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs <c>multipass info --format json</c> for the given VM names and returns
    /// best-effort metadata. Returns an empty dictionary on any failure so missing
    /// metadata degrades gracefully in the caller.
    /// </summary>
    private async Task<Dictionary<string, MultipassSandboxDetails>> FetchSandboxDetailsAsync(
        MultipassSandboxOptions opts,
        List<string> names,
        CancellationToken ct)
    {
        var argv = new List<string> { opts.MultipassBinary, "info", "--format", "json" };
        argv.AddRange(names);

        var run = await RunAsync(opts, argv, stdin: null, ct: ct);
        if (run.ExitCode != 0)
        {
            _log.LogWarning("multipass info failed (exit {ExitCode}): {Stderr}", run.ExitCode, run.Stderr);
            return [];
        }

        var result = new Dictionary<string, MultipassSandboxDetails>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(run.Stdout);
            if (!doc.RootElement.TryGetProperty("info", out var infoEl))
                return result;

            foreach (var vmEntry in infoEl.EnumerateObject())
            {
                long? diskBytes = null;
                if (!vmEntry.Value.TryGetProperty("disks", out var disksEl))
                    disksEl = default;
                long total = 0;
                if (disksEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var diskEntry in disksEl.EnumerateObject())
                    {
                        if (diskEntry.Value.TryGetProperty("used", out var usedEl) &&
                            long.TryParse(usedEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var used))
                            total += used;
                    }
                }
                if (total > 0)
                    diskBytes = total;

                string? state = null;
                if (vmEntry.Value.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String)
                    state = stateEl.GetString();

                result[vmEntry.Name] = new MultipassSandboxDetails(
                    diskBytes,
                    TryReadCreatedAt(vmEntry.Value),
                    state);
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse multipass info output; sandbox details will be omitted");
        }
        return result;
    }

    private static DateTimeOffset? TryReadCreatedAt(JsonElement vmInfo)
    {
        foreach (var propertyName in new[] { "created", "created_at", "creation_time", "creationTimestamp" })
        {
            if (!vmInfo.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            if (DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
                return parsed;
        }

        return null;
    }

    private static bool IsValidSandboxName(string name)
    {
        // VM names must be alphanumeric-and-hyphen only (DNS-label style).
        // This blocks path-traversal characters (/, ., \) before any filesystem use.
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }
        return name.Length > 0;
    }

    /// <summary>
    /// Ensures the baseline VM for <paramref name="profileName"/> and
    /// <paramref name="flavor"/> exists.
    /// Bakes it on first call (~5-10 min: launch with cloud-init, install
    /// agent CLIs and runtime, stop). Subsequent calls return the existing
    /// baseline name.
    ///
    /// We bake one baseline per profile/flavor because <c>multipass clone</c>
    /// inherits the source VM's network attachments — a baseline launched
    /// with <c>--network cb-net</c> can only produce clones attached to
    /// <c>cb-net</c>. Graphical baselines also carry a desktop/VNC toolchain,
    /// so they must not be shared with headless baselines for the same egress
    /// profile.
    /// </summary>
    private async Task<string> EnsureBaselineForProfileAsync(
        MultipassSandboxOptions opts,
        string profileName,
        SandboxProfileFlavor flavor,
        WorkItemId? workItemId,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        if (!opts.NetworkProfiles.TryGetValue(profileName, out _))
            throw new InvalidOperationException(
                $"Network profile '{profileName}' is not configured in MultipassSandboxOptions.NetworkProfiles. " +
                $"Configured profiles: [{string.Join(", ", opts.NetworkProfiles.Keys)}]");

        // B1 pins are VM names, but Multipass clones inherit the source VM's
        // network attachment. ResolveBaselineRef persists the profile/flavor that
        // produced each ref so a restarted provider can still accept a same-target
        // stale pin after baseline-contributing config drift. Unknown stale pins
        // fail closed instead of cloning a work-profile baseline into an
        // audit/rework phase.
        var liveBaselineName = ComposeBaselineNameFromLiveConfig(opts, profileName, flavor);
        var baselineName = liveBaselineName;
        if (!string.IsNullOrWhiteSpace(pinnedBaselineRef))
        {
            EnsurePinnedBaselineMatchesTarget(pinnedBaselineRef, liveBaselineName, profileName, flavor);
            baselineName = pinnedBaselineRef;
        }
        RememberBaselineTarget(baselineName, profileName, flavor);

        var sem = GetBaselineLock(baselineName);
        await sem.WaitAsync(ct);
        try
        {
            if (await BaselineVmExistsAsync(opts, baselineName, workItemId, ct))
                return baselineName;
            await BakeBaselineAsync(opts, baselineName, profileName, flavor, workItemId, ct);
            return baselineName;
        }
        finally
        {
            sem.Release();
        }
    }

    private void EnsurePinnedBaselineMatchesTarget(
        string pinnedBaselineRef,
        string liveBaselineName,
        string profileName,
        SandboxProfileFlavor flavor)
    {
        var requested = new BaselineTarget(profileName, flavor);
        if (_baselineTargets.TryGetValue(pinnedBaselineRef, out var pinnedTarget))
        {
            if (pinnedTarget == requested)
                return;

            throw new InvalidOperationException(
                $"Pinned baseline '{pinnedBaselineRef}' was resolved for network profile '{pinnedTarget.ProfileName}' / flavor '{pinnedTarget.Flavor}', " +
                $"but this sandbox requested network profile '{profileName}' / flavor '{flavor}'. Refusing to clone a baseline with a different network attachment.");
        }

        if (TryReadPersistedBaselineTarget(pinnedBaselineRef, out pinnedTarget))
        {
            if (pinnedTarget == requested)
                return;

            throw new InvalidOperationException(
                $"Pinned baseline '{pinnedBaselineRef}' was persisted for network profile '{pinnedTarget.ProfileName}' / flavor '{pinnedTarget.Flavor}', " +
                $"but this sandbox requested network profile '{profileName}' / flavor '{flavor}'. Refusing to clone a baseline with a different network attachment.");
        }

        if (string.Equals(pinnedBaselineRef, liveBaselineName, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"Pinned baseline '{pinnedBaselineRef}' is not bound to requested network profile '{profileName}' / flavor '{flavor}' " +
            $"(current ref for that target is '{liveBaselineName}'). Refusing to clone a baseline with an unknown network attachment.");
    }

    private void RememberBaselineTarget(string baselineName, string profileName, SandboxProfileFlavor flavor)
    {
        if (string.IsNullOrWhiteSpace(baselineName))
            return;
        _baselineTargets.TryAdd(baselineName, new BaselineTarget(profileName, flavor));
        TryPersistBaselineTarget(baselineName, profileName, flavor);
    }

    private bool TryGetBaselineTargetMetadataPath(string baselineName, out string path)
    {
        path = string.Empty;
        if (!IsValidSandboxName(baselineName))
            return false;

        var dir = Path.Combine(_stagingRoot, "_baseline-targets");
        path = Path.Combine(dir, baselineName + ".json");
        return true;
    }

    private void TryPersistBaselineTarget(string baselineName, string profileName, SandboxProfileFlavor flavor)
    {
        if (!TryGetBaselineTargetMetadataPath(baselineName, out var path))
            return;

        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            TryChmod0700(dir);
            var temp = Path.Combine(dir, "." + baselineName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var metadata = new BaselineTargetMetadata(profileName, flavor.ToString());
            File.WriteAllText(temp, JsonSerializer.Serialize(metadata));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Could not persist baseline target metadata for {Baseline}; future restarts may reject stale pinned refs",
                baselineName);
        }
    }

    private bool TryReadPersistedBaselineTarget(string baselineName, out BaselineTarget target)
    {
        target = default;
        if (!TryGetBaselineTargetMetadataPath(baselineName, out var path))
            return false;

        try
        {
            if (!File.Exists(path))
                return false;

            var metadata = JsonSerializer.Deserialize<BaselineTargetMetadata>(File.ReadAllText(path));
            if (metadata is null
                || string.IsNullOrWhiteSpace(metadata.ProfileName)
                || !Enum.TryParse<SandboxProfileFlavor>(metadata.Flavor, ignoreCase: false, out var flavor))
                return false;

            target = new BaselineTarget(metadata.ProfileName, flavor);
            _baselineTargets.TryAdd(baselineName, target);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not read baseline target metadata for {Baseline}", baselineName);
            return false;
        }
    }

    private SemaphoreSlim GetBaselineLock(string baselineName)
    {
        lock (_baselineLocksGuard)
        {
            if (!_baselineLocks.TryGetValue(baselineName, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _baselineLocks[baselineName] = sem;
            }
            return sem;
        }
    }

    /// <summary>
    /// Composes the multipass baseline VM name from live config. The hash
    /// covers every operator-tunable input that contributes to baseline
    /// contents — profile, flavor, the baseline cloud-init body, the install
    /// runcmd list, and any operator-supplied extra cloud-init — so an edit
    /// to any of them produces a fresh ref and the old baseline becomes
    /// orphaned (eligible for the reaper after the grace window).
    /// </summary>
    internal static string ComposeBaselineNameFromLiveConfig(
        MultipassSandboxOptions opts,
        string profileName,
        SandboxProfileFlavor flavor)
    {
        var hash = ComputeBaselineHash(opts, profileName, flavor);
        var baselineName = opts.BaselineNamePrefix + hash;
        // multipass instance names cap at 24 chars; the prefix + 12-char hash
        // already fits comfortably under that with the default prefix
        // ("cb-baseline-" = 12 chars → total 24). If the operator picked a
        // longer prefix and we overflow, trim deterministically.
        if (baselineName.Length > 24)
        {
            baselineName = baselineName[..24];
        }
        return baselineName;
    }

    /// <summary>
    /// Computes a 12-hex-char content hash over every input that contributes
    /// to baseline contents. Deterministic across processes (SHA-256, not
    /// <c>string.GetHashCode()</c>) so the same config always produces the
    /// same ref. 12 hex chars = 48 bits; collision probability between two
    /// distinct configs is astronomically small at the scale this is used
    /// (handfuls of baselines per host) and the hash falls inside the
    /// 24-char multipass name limit with the default prefix.
    /// </summary>
    internal static string ComputeBaselineHash(
        MultipassSandboxOptions opts,
        string profileName,
        SandboxProfileFlavor flavor)
    {
        var firstBootRuncmd = BuildFirstBootRuncmd(opts, flavor);
        // The cloud-init body matches what BakeBaselineAsync writes:
        // extraRuncmd=null, extraCloudInit=opts.ExtraCloudInit, startRouteService=true,
        // includeGraphicalInstall=false, baselineInstallCommands=opts.ExtraRuncmd.
        // The install commands are still listed separately because they run via
        // multipass exec, not cloud-init runcmd.
        var cloudInit = BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: opts.ExtraCloudInit,
            flavor: flavor,
            startRouteService: true,
            includeGraphicalInstall: false,
            baselineInstallCommands: opts.ExtraRuncmd);
        var seedsStr = string.Join(";", opts.PackageCacheSeeds.Select(s => $"{s.HostSourcePath}->{s.VmDestPath}({s.MaxSizeMB})"));
        var execsStr = string.Join(";", opts.ExecutableProvisions.Select(RenderExecutableProvisionForHash));
        // Build a canonical, version-prefixed string. The 'v4' prefix lets
        // future schema changes invalidate every existing baseline without
        // ambiguity (bumped from v3 when executable content fingerprints were added).
        // Field separator is '|' which cannot appear in profile names or
        // flavor enum strings.
        var canon = string.Join("|", new[]
        {
            "v4",
            profileName,
            flavor.ToString(),
            cloudInit,
            string.Join("\n", firstBootRuncmd),
            string.Join("\n", opts.BaselineVerificationCommands.Select(RenderBaselineVerificationCommandForHash)),
            opts.ExtraCloudInit ?? string.Empty,
            seedsStr,
            execsStr,
        });
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canon));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string RenderExecutableProvisionForHash(ExecutableProvisionOptions exe)
    {
        var hostPath = ResolvePath(exe.HostSourcePath);
        return string.Join("\u001f", new[]
        {
            exe.HostSourcePath,
            hostPath,
            FingerprintExecutableProvisionHostFile(hostPath),
            exe.VmDestPath,
            string.Join("\u001e", exe.VmSymlinks),
        });
    }

    private static string FingerprintExecutableProvisionHostFile(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
            return "empty-path";
        if (!File.Exists(hostPath))
            return "missing";

        using var stream = File.OpenRead(hostPath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string RenderBaselineVerificationCommandForHash(MultipassBaselineVerificationCommand cmd) =>
        string.Join("\u001f", new[]
        {
            cmd.Label,
            string.Join("\u001e", cmd.Argv),
            cmd.FailureHint ?? string.Empty,
        });

    /// <inheritdoc/>
    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        var opts = ReadOptions();
        if (!opts.UseBaselineImages) return null;
        if (!opts.NetworkProfiles.ContainsKey(profileName)) return null;
        var baselineName = ComposeBaselineNameFromLiveConfig(opts, profileName, flavor);
        RememberBaselineTarget(baselineName, profileName, flavor);
        return baselineName;
    }

    /// <inheritdoc/>
    public async Task<string?> EnsureBaselineImageAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        string? pinnedBaselineRef,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        var opts = ReadOptions();
        if (!opts.UseBaselineImages) return null;
        return await EnsureBaselineForProfileAsync(opts, profileName, flavor, workItemId: null, pinnedBaselineRef, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var opts = ReadOptions();
        var prefix = opts.BaselineNamePrefix;
        var listRun = await RunAsync(opts, [opts.MultipassBinary, "list", "--format=json"], stdin: null, ct: ct);
        if (listRun.ExitCode != 0)
        {
            _log.LogWarning("multipass list failed (exit {Exit}): {Stderr}", listRun.ExitCode, listRun.Stderr);
            return [];
        }
        var results = new List<BaselineImageInfo>();
        try
        {
            using var doc = JsonDocument.Parse(listRun.Stdout);
            if (!doc.RootElement.TryGetProperty("list", out var list)) return results;
            foreach (var entry in list.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var nameProp)) continue;
                var name = nameProp.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                // multipass list doesn't expose created-at in --format=json;
                // mtime of the disk image is the next-best signal but is provider-
                // internal — leave null and let the reaper apply the grace window
                // based on its own bookkeeping (first-seen on a sweep).
                results.Add(new BaselineImageInfo(name, CreatedAt: null, DiskBytes: null));
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse multipass list JSON output");
            return [];
        }
        return results;
    }

    /// <inheritdoc/>
    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var opts = ReadOptions();
        if (!name.StartsWith(opts.BaselineNamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to dispose '{name}': name must start with baseline prefix '{opts.BaselineNamePrefix}'");
        var run = await RunAsync(opts, [opts.MultipassBinary, "delete", "--purge", name], stdin: null, ct: ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"multipass delete --purge {name} failed: {run.Stderr}");
    }

    private async Task<bool> BaselineVmExistsAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var info = await RunAsync(opts, [opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct, workItemId: workItemId);
        ThrowIfProvisioningRetryExhausted("info", info);
        return info.ExitCode == 0;
    }

    private async Task BakeBaselineAsync(
        MultipassSandboxOptions opts,
        string baselineName,
        string profileName,
        SandboxProfileFlavor flavor,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var bridge = opts.NetworkProfiles[profileName];
        _log.LogInformation(
            "Baking Multipass baseline {Name} for profile {Profile} on bridge {Bridge} — one-time, ~5-10 minutes",
            baselineName, profileName, bridge);

        var installCommands = BuildFirstBootRuncmd(opts, flavor);
        // Cloud-init for the baseline contains idempotent file-writes
        // (exec wrapper, route systemd service, plus a rendered operator
        // install-command manifest for diagnostics). Install runcmds run via
        // `multipass exec` AFTER launch instead. Why: when
        // we `multipass clone` the baseline, multipass assigns the clone a
        // fresh instance-id, so cloud-init thinks it's a brand-new instance
        // and re-runs every per-instance module including runcmd. Putting
        // installs in runcmd would mean re-running them on every clone —
        // slow, and possibly disk-filling.
        var cloudInit = BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: opts.ExtraCloudInit,
            flavor: flavor,
            startRouteService: true,
            includeGraphicalInstall: false,
            baselineInstallCommands: opts.ExtraRuncmd);
        var stagingDir = Path.Combine(_stagingRoot, "_baseline-" + baselineName);
        Directory.CreateDirectory(stagingDir);
        TryChmod0700(stagingDir);
        var cloudInitPath = Path.Combine(stagingDir, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInitPath, cloudInit, ct);

        var argv = new List<string> {
            opts.MultipassBinary, "launch", "--name", baselineName,
            "--cloud-init", cloudInitPath,
            "--network", $"name={bridge},mode=auto",
            // Multipass defaults (5G disk / 1G RAM / 1 vCPU) are tight for
            // a typical project install — language toolchain + agent CLI +
            // any auditor binaries. Defaults here are operator-tunable via
            // BaselineDiskGB / BaselineMemoryGB / BaselineCpus options;
            // raise them if your install runs OOM or run out of disk
            // mid-bake. qcow2 disks are sparse so unused disk space costs
            // nothing on the host until written.
            "--disk", $"{opts.BaselineDiskGB}G",
            "--memory", $"{opts.BaselineMemoryGB}G",
            "--cpus", opts.BaselineCpus.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(opts.DefaultImage))
            argv.Add(opts.DefaultImage);

        try
        {
            // baseline-launch is heavy and gated inside RunAsync; the
            // follow-up WaitForRunning info polls are light and ungated.
            var run = await RunAsync(opts, argv, stdin: null, ct: ct, workItemId: workItemId);
            if (run.ExitCode != 0)
            {
                ThrowIfProvisioningRetryExhausted("baseline-launch", run);
                throw new InvalidOperationException($"baseline launch failed: {run.Stderr}");
            }

            // Wait for the (now-minimal) cloud-init to finish — write_files
            // and the route service install. Doesn't include the heavy
            // installs, so should be fast.
            await WaitForRunningAsync(opts, baselineName, workItemId, ct);

            // Run the install commands now, via multipass exec under sudo.
            // Each entry in ExtraRuncmd is a single shell command.
            for (var i = 0; i < installCommands.Count; i++)
            {
                var cmd = installCommands[i];
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                _log.LogInformation("Baseline install step {N}/{Total}", i + 1, installCommands.Count);
                var execRun = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "exec", baselineName, "--", "sudo", "bash", "-c", cmd],
                    stdin: null, ct: ct, workItemId: workItemId);
                if (execRun.ExitCode != 0)
                {
                    ThrowIfProvisioningRetryExhausted("exec", execRun);
                    throw new InvalidOperationException(
                        $"baseline install step {i + 1} failed (exit {execRun.ExitCode}):\n" +
                        $"stderr: {execRun.Stderr}\nstdout-tail: {(execRun.Stdout.Length > 1000 ? "…" + execRun.Stdout[^1000..] : execRun.Stdout)}");
                }
            }

            // Provision host-staged executable binaries (e.g. the agy CLI) into the
            // baseline before the binary-verification gate runs. Verification expects
            // the binary on PATH with the executable bit set; doing this here keeps the
            // missing-binary failure mode loud (verify exit 127) instead of dispatch-time.
            await ProvisionExecutablesAsync(opts, baselineName, workItemId, ct).ConfigureAwait(false);

            await VerifyBaselineRequiredBinariesAsync(opts, baselineName, workItemId, ct);

            // Seed package caches into the baseline VM before stopping it
            await SeedPackageCachesAsync(opts, baselineName, stagingDir, workItemId, ct).ConfigureAwait(false);

            // Stop the baseline so `multipass clone` can use it as a source
            // (clone requires source stopped). Wait for the state to flip
            // so a subsequent clone doesn't race a still-Stopping VM.
            var stop = await RunAsync(opts, [opts.MultipassBinary, "stop", baselineName], stdin: null, ct: ct, workItemId: workItemId);
            if (stop.ExitCode != 0)
            {
                ThrowIfProvisioningRetryExhausted("stop", stop);
                throw new InvalidOperationException($"baseline stop failed: {stop.Stderr}");
            }
            await WaitForStoppedAsync(opts, baselineName, workItemId, ct);

            _log.LogInformation("Baseline {Name} baked and stopped, ready to clone", baselineName);
        }
        catch (Exception bakeEx)
        {
            // A failed bake may have already launched a VM. Purge it before the
            // admission decorator releases its baseline-provisioning token; a
            // half-created running baseline must not escape the global VM cap.
            var deleted = await TryDeleteVmAsync(opts, baselineName);
            if (deleted)
            {
                _log.LogWarning(
                    "Baseline bake for {Name} failed; purged partial baseline VM before retry",
                    baselineName);
                throw;
            }

            // delete --purge failed: surface the retained baseline name so the
            // admission decorator keeps the token reserved until
            // ListBaselineImagesAsync / DisposeBaselineImageAsync proves the
            // partial baseline VM is actually gone. Without this, a stuck
            // baseline VM would escape MaxConcurrentSandboxes.
            if (await SandboxMayStillExistAfterFailedDeleteAsync(opts, baselineName))
            {
                _log.LogError(
                    "Baseline bake for {Name} failed and automatic purge did not complete; retaining sandbox admission until baseline is proven gone (operator may need to `multipass delete --purge {PurgeTarget}`)",
                    baselineName,
                    baselineName);
                throw new SandboxProvisioningDeferredException(
                    Name,
                    "baseline-bake-cleanup",
                    "multipass-delete-purge-failed",
                    $"baseline bake failed and best-effort delete --purge did not prove baseline {baselineName} was removed: {bakeEx.Message}",
                    _daemonRetryPolicy.ExhaustedRequeueDelay,
                    retainedSandboxName: baselineName,
                    innerException: bakeEx);
            }

            _log.LogWarning(
                "Baseline bake for {Name} failed; delete --purge reported failure but inventory confirms the baseline is absent",
                baselineName);
            throw;
        }
    }

    private async Task VerifyBaselineRequiredBinariesAsync(
        MultipassSandboxOptions opts,
        string baselineName,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        if (opts.BaselineVerificationCommands.Count == 0)
            return;

        for (var i = 0; i < opts.BaselineVerificationCommands.Count; i++)
        {
            var check = opts.BaselineVerificationCommands[i];
            if (check.Argv.Count == 0)
                throw new InvalidOperationException(
                    $"baseline verification command {i + 1} (label '{check.Label}') has empty argv");

            var argv = new List<string> { opts.MultipassBinary, "exec", baselineName, "--" };
            argv.AddRange(check.Argv);
            _log.LogInformation(
                "Baseline verification step {N}/{Total}: {Label} ({Command})",
                i + 1,
                opts.BaselineVerificationCommands.Count,
                check.Label,
                string.Join(" ", check.Argv));

            var run = await RunAsync(opts, argv, stdin: null, ct: ct, workItemId: workItemId);
            if (run.ExitCode != 0)
            {
                ThrowIfProvisioningRetryExhausted("exec", run);
                var hint = string.IsNullOrWhiteSpace(check.FailureHint)
                    ? "required baseline binary not runnable on sandbox PATH"
                    : check.FailureHint;
                throw new InvalidOperationException(
                    $"baseline verification command (label '{check.Label}') failed (exit {run.ExitCode}): {hint}; " +
                    $"argv: {string.Join(" ", check.Argv)}; stderr: {DiagnosticText(run.Stderr)}; " +
                    $"stdout-tail: {DiagnosticText(Tail(run.Stdout, 1000))}");
            }
        }
    }

    private async Task ProvisionExecutablesAsync(
        MultipassSandboxOptions opts,
        string baselineName,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        if (opts.ExecutableProvisions == null || opts.ExecutableProvisions.Count == 0)
            return;

        for (var i = 0; i < opts.ExecutableProvisions.Count; i++)
        {
            var exe = opts.ExecutableProvisions[i];
            var label = string.IsNullOrWhiteSpace(exe.Label)
                ? (string.IsNullOrWhiteSpace(exe.VmDestPath) ? $"exe-{i + 1}" : Path.GetFileName(exe.VmDestPath))
                : exe.Label!;

            if (string.IsNullOrWhiteSpace(exe.HostSourcePath) || string.IsNullOrWhiteSpace(exe.VmDestPath))
                throw new InvalidOperationException(
                    $"ExecutableProvisions[{i}] (label '{label}') is invalid: HostSourcePath and VmDestPath are both required.");

            if (!Path.IsPathRooted(exe.VmDestPath))
                throw new InvalidOperationException(
                    $"ExecutableProvisions[{i}] (label '{label}') VmDestPath must be absolute, got '{exe.VmDestPath}'.");

            var hostPath = ResolvePath(exe.HostSourcePath);
            if (!File.Exists(hostPath))
                throw new InvalidOperationException(
                    $"ExecutableProvisions[{i}] (label '{label}') host file '{hostPath}' does not exist; cannot ship binary into baseline {baselineName}.");

            // Transfer to a predictable temp path in /home/ubuntu first. Multipass
            // transfers run as the default `ubuntu` user; from there we use sudo
            // install to relocate with mode/owner deterministically. Doing it this
            // way avoids relying on `multipass transfer` preserving the executable
            // bit (undocumented) and lets the destination live in root-owned
            // directories like /usr/local/bin.
            var uniqueId = Guid.NewGuid().ToString("N");
            var vmStageName = $"codeybox-exe-{uniqueId}";
            var vmStagePath = $"/home/ubuntu/{vmStageName}";

            _log.LogInformation(
                "Provisioning executable {Label} into {Baseline}: host {HostPath} -> VM {VmDestPath}",
                label, baselineName, hostPath, exe.VmDestPath);

            var transfer = await RunAsync(
                opts,
                [opts.MultipassBinary, "transfer", hostPath, $"{baselineName}:{vmStagePath}"],
                stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
            if (transfer.ExitCode != 0)
            {
                ThrowIfProvisioningRetryExhausted("transfer", transfer);
                throw new InvalidOperationException(
                    $"ExecutableProvisions[{i}] (label '{label}') transfer failed (exit {transfer.ExitCode}): {transfer.Stderr}");
            }

            var destDir = Path.GetDirectoryName(exe.VmDestPath);
            if (string.IsNullOrEmpty(destDir))
                throw new InvalidOperationException(
                    $"ExecutableProvisions[{i}] (label '{label}') VmDestPath has no directory component: '{exe.VmDestPath}'.");

            // Single in-VM shell step (atomic from the bake's perspective): mkdir
            // the parent, install with mode 0755 and root ownership, drop any
            // requested symlinks, and clean the staging copy. `install` is used
            // instead of `mv` + `chmod` because it sets mode and ownership in one
            // syscall sequence and is portable across the targets we ship to.
            var script = new System.Text.StringBuilder();
            script.Append("set -euo pipefail; ");
            AppendCreateDirectoryCommand(script, destDir!);
            script.Append("install -m 0755 -o root -g root ")
                .Append(ShellSingleQuote(vmStagePath))
                .Append(' ')
                .Append(ShellSingleQuote(exe.VmDestPath))
                .Append("; ");
            if (exe.VmSymlinks is not null)
            {
                foreach (var link in exe.VmSymlinks)
                {
                    if (string.IsNullOrWhiteSpace(link)) continue;
                    if (!Path.IsPathRooted(link))
                        throw new InvalidOperationException(
                            $"ExecutableProvisions[{i}] (label '{label}') VmSymlink must be absolute, got '{link}'.");
                    var linkDir = Path.GetDirectoryName(link);
                    if (!string.IsNullOrEmpty(linkDir))
                        AppendCreateDirectoryCommand(script, linkDir!);
                    script.Append("ln -sf ")
                        .Append(ShellSingleQuote(exe.VmDestPath))
                        .Append(' ')
                        .Append(ShellSingleQuote(link))
                        .Append("; ");
                }
            }
            script.Append("rm -f ").Append(ShellSingleQuote(vmStagePath));

            var execRun = await RunAsync(
                opts,
                [opts.MultipassBinary, "exec", baselineName, "--", "sudo", "bash", "-c", script.ToString()],
                stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
            if (execRun.ExitCode != 0)
            {
                ThrowIfProvisioningRetryExhausted("exec", execRun);
                throw new InvalidOperationException(
                    $"ExecutableProvisions[{i}] (label '{label}') install failed (exit {execRun.ExitCode}): {execRun.Stderr}");
            }
        }
    }

    private static void AppendCreateDirectoryCommand(StringBuilder script, string vmDirectory)
    {
        if (IsUnderDefaultUbuntuHome(vmDirectory))
        {
            script.Append("install -d -m 0755 -o ubuntu -g ubuntu ")
                .Append(ShellSingleQuote(vmDirectory))
                .Append("; ");
            return;
        }

        script.Append("mkdir -p ")
            .Append(ShellSingleQuote(vmDirectory))
            .Append("; ");
    }

    private static bool IsUnderDefaultUbuntuHome(string vmPath)
    {
        var normalized = vmPath.Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalized, "/home/ubuntu", StringComparison.Ordinal)
            || normalized.StartsWith("/home/ubuntu/", StringComparison.Ordinal);
    }

    private async Task SeedPackageCachesAsync(
        MultipassSandboxOptions opts,
        string baselineName,
        string stagingDir,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        if (opts.PackageCacheSeeds == null || opts.PackageCacheSeeds.Count == 0)
        {
            return;
        }

        _log.LogInformation("Seeding package caches into baseline VM {Name}...", baselineName);

        foreach (var seed in opts.PackageCacheSeeds)
        {
            if (string.IsNullOrWhiteSpace(seed.HostSourcePath) || string.IsNullOrWhiteSpace(seed.VmDestPath))
            {
                continue;
            }

            var hostPath = ResolvePath(seed.HostSourcePath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                _log.LogWarning("Package cache seed host path '{HostPath}' does not exist. Skipping.", hostPath);
                continue;
            }

            var vmDestPath = seed.VmDestPath;
            _log.LogInformation("Seeding cache: host {HostPath} -> VM {VmDestPath}", hostPath, vmDestPath);

            var uniqueId = Guid.NewGuid().ToString("N");
            var seedStagingDir = Path.Combine(stagingDir, $"cache-seed-{uniqueId}");
            Directory.CreateDirectory(seedStagingDir);

            var hostTarPath = Path.Combine(stagingDir, $"cache-seed-{uniqueId}.tar");

            try
            {
                bool hasFiles = false;
                if (Directory.Exists(hostPath))
                {
                    long maxSizeBytes = -1;
                    if (seed.MaxSizeMB.HasValue && seed.MaxSizeMB.Value > 0)
                    {
                        maxSizeBytes = (long)(seed.MaxSizeMB.Value * 1024 * 1024);
                    }

                    if (maxSizeBytes > 0)
                    {
                        CopyDirectoryWithCap(hostPath, seedStagingDir, maxSizeBytes);
                    }
                    else
                    {
                        CopyDirectoryRecursively(hostPath, seedStagingDir);
                    }
                    hasFiles = Directory.EnumerateFileSystemEntries(seedStagingDir).Any();
                }
                else if (File.Exists(hostPath))
                {
                    var destFilePath = Path.Combine(seedStagingDir, Path.GetFileName(hostPath));
                    File.Copy(hostPath, destFilePath, overwrite: true);
                    hasFiles = true;
                }

                if (!hasFiles)
                {
                    _log.LogWarning("No files to transfer for cache seed {HostPath}", hostPath);
                    continue;
                }

                // 1. Tar the staging directory on the host
                var tarArgv = new[] { "tar", "-cf", hostTarPath, "-C", seedStagingDir, "." };
                var tarResult = await RunHostProcessAsync(tarArgv, ct, workItemId).ConfigureAwait(false);
                if (tarResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to tar package cache seed on host: {tarResult.Stderr}");
                }

                // 2. Create the destination directory in the VM
                var mkdirResult = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "exec", baselineName, "--", "mkdir", "-p", vmDestPath],
                    stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
                if (mkdirResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to create VM directory '{vmDestPath}': {mkdirResult.Stderr}");
                }

                // 3. Transfer the tarball
                var vmTarName = $"cache-seed-{uniqueId}.tar";
                var tx = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "transfer", hostTarPath, $"{baselineName}:{vmTarName}"],
                    stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
                if (tx.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to transfer package cache seed tarball to VM: {tx.Stderr}");
                }

                // 4. Extract the tarball inside the VM
                var extractResult = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "exec", baselineName, "--", "tar", "-xf", $"/home/ubuntu/{vmTarName}", "-C", vmDestPath],
                    stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
                if (extractResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to extract package cache seed tarball inside VM: {extractResult.Stderr}");
                }

                // 5. Change ownership of the VM directory
                var chownResult = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "exec", baselineName, "--", "sudo", "chown", "-R", "ubuntu:ubuntu", vmDestPath],
                    stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
                if (chownResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to change VM directory ownership: {chownResult.Stderr}");
                }

                // 6. Clean up the tarball inside the VM
                var rmResult = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "exec", baselineName, "--", "rm", "-f", $"/home/ubuntu/{vmTarName}"],
                    stdin: null, ct: ct, workItemId: workItemId).ConfigureAwait(false);
                if (rmResult.ExitCode != 0)
                {
                    _log.LogWarning("Failed to clean up VM tarball {TarName}: {Stderr}", vmTarName, rmResult.Stderr);
                }
            }
            finally
            {
                // Clean up local temp files and directories
                try
                {
                    if (Directory.Exists(seedStagingDir))
                    {
                        Directory.Delete(seedStagingDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete staging subdirectory {Dir}", seedStagingDir);
                }

                try
                {
                    if (File.Exists(hostTarPath))
                    {
                        File.Delete(hostTarPath);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete staging tarfile {File}", hostTarPath);
                }
            }
        }
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME") ?? "/home/ubuntu";
            }
            path = Path.Combine(home, path[1..].TrimStart('/', '\\'));
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    private void CopyDirectoryRecursively(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        try
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectoryRecursively(dir, destSubDir);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error copying subdirectory {SourceDir} -> {DestDir}", sourceDir, destDir);
        }
    }

    private void CopyDirectoryWithCap(string sourceDir, string destDir, long maxSizeBytes)
    {
        Directory.CreateDirectory(destDir);
        try
        {
            var entries = new DirectoryInfo(sourceDir).GetFileSystemInfos();
            var sortedEntries = entries.OrderByDescending(e => e.LastWriteTimeUtc).ToList();

            long currentSize = 0;
            foreach (var entry in sortedEntries)
            {
                long entrySize = GetSize(entry);
                if (currentSize + entrySize > maxSizeBytes)
                {
                    _log.LogInformation("Size cap of {Max} bytes reached for cache seed {SourceDir}. Stopping copy.", maxSizeBytes, sourceDir);
                    break;
                }

                var targetPath = Path.Combine(destDir, entry.Name);
                if (entry is DirectoryInfo dirInfo)
                {
                    CopyDirectoryRecursively(dirInfo.FullName, targetPath);
                }
                else if (entry is FileInfo fileInfo)
                {
                    File.Copy(fileInfo.FullName, targetPath, overwrite: true);
                }
                currentSize += entrySize;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error while copying directory with cap from {SourceDir} to {DestDir}", sourceDir, destDir);
        }
    }

    private long GetSize(FileSystemInfo entry)
    {
        if (entry is FileInfo fileInfo)
        {
            return fileInfo.Length;
        }
        if (entry is DirectoryInfo dirInfo)
        {
            try
            {
                long size = 0;
                foreach (var file in Directory.EnumerateFiles(dirInfo.FullName, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        size += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Ignore files that are deleted or inaccessible
                    }
                }
                return size;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error calculating size for directory {Dir}", dirInfo.FullName);
                return 0;
            }
        }
        return 0;
    }

    private async Task<ProcessRunResult> RunHostProcessAsync(
        IReadOnlyList<string> argv,
        CancellationToken ct,
        WorkItemId? workItemId = null)
    {
        var environment = BuildHostProcessEnvironment(workItemId);
        return await _runner.RunAsync(argv, stdin: null, ct, environment: environment).ConfigureAwait(false);
    }
    private async Task CloneFromBaselineAsync(
        MultipassSandboxOptions opts,
        string newName,
        string baselineName,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        // Defensive: ensure source is fully stopped before clone. Multipass
        // clone requires it, but the baseline can get inadvertently
        // restarted (e.g. operator runs `multipass exec` against it, which
        // auto-starts stopped instances). Stop is idempotent — exits 0 if
        // already stopped — and we wait for the state to flip because
        // `multipass stop` returns when the request is queued.
        var stop = await RunAsync(opts, [opts.MultipassBinary, "stop", baselineName], stdin: null, ct: ct, workItemId: workItemId);
        if (stop.ExitCode != 0)
            ThrowIfProvisioningRetryExhausted("stop", stop);
        await WaitForStoppedAsync(opts, baselineName, workItemId, ct);

        _log.LogInformation("Cloning {New} from baseline {Baseline}", newName, baselineName);
        var clone = await RunAsync(
            opts,
            [opts.MultipassBinary, "clone", baselineName, "--name", newName],
            stdin: null, ct: ct, workItemId: workItemId);
        if (clone.ExitCode != 0)
        {
            if (IsCloneTargetAlreadyExists(clone, newName)
                && await TryRecoverCloneTargetAlreadyExistsAsync(opts, newName, baselineName, workItemId, ct))
                return;

            ThrowIfProvisioningRetryExhausted("clone", clone);
            throw new InvalidOperationException($"multipass clone failed: {clone.Stderr}");
        }

        // NOTE: deliberately do NOT start the clone here. multipass clone
        // creates the new VM in Stopped state, which is exactly what
        // SetUpMountsAsync's `mount --type=native` requires. Starting now
        // and stopping again later created a stop-state race where the
        // mount could fire before multipassd had fully released the VM.
    }

    private async Task<bool> TryRecoverCloneTargetAlreadyExistsAsync(
        MultipassSandboxOptions opts,
        string newName,
        string baselineName,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var info = await RunAsync(
            opts,
            [opts.MultipassBinary, "info", newName, "--format=csv"],
            stdin: null,
            ct: ct,
            workItemId: workItemId);
        if (info.ExitCode == 0 && info.Stdout.Contains("Stopped", StringComparison.Ordinal))
        {
            _log.LogWarning(
                "multipass clone target {Name} already exists in Stopped state after clone failure; treating clone as successful partial completion",
                newName);
            return true;
        }

        if (info.ExitCode == 0)
        {
            _log.LogWarning(
                "multipass clone target {Name} already exists but is not Stopped; purging stale target before retry. info={Info}",
                newName, SingleLine(info.Stdout));
            await TryDeleteVmAsync(opts, newName);
        }
        else
        {
            ThrowIfProvisioningRetryExhausted("info", info);
            _log.LogWarning(
                "multipass clone reported target {Name} already exists, but info could not read it; retrying clone once. stderr={Stderr}",
                newName, SingleLine(info.Stderr));
        }

        var retry = await RunAsync(
            opts,
            [opts.MultipassBinary, "clone", baselineName, "--name", newName],
            stdin: null,
            ct: ct,
            workItemId: workItemId);
        if (retry.ExitCode == 0)
            return true;

        if (IsCloneTargetAlreadyExists(retry, newName))
        {
            var retryInfo = await RunAsync(
                opts,
                [opts.MultipassBinary, "info", newName, "--format=csv"],
                stdin: null,
                ct: ct,
                workItemId: workItemId);
            if (retryInfo.ExitCode == 0 && retryInfo.Stdout.Contains("Stopped", StringComparison.Ordinal))
            {
                _log.LogWarning(
                    "multipass clone target {Name} still reports already-exists but is now Stopped; treating clone as successful partial completion",
                    newName);
                return true;
            }

            ThrowIfProvisioningRetryExhausted("info", retryInfo);
            var infoDetail = retryInfo.ExitCode == 0
                ? $"target info after retry: {SingleLine(retryInfo.Stdout)}"
                : $"target info after retry was unreadable: {SingleLine(retryInfo.Stderr)}";
            ThrowProvisioningDeferred(
                "clone",
                "multipass-clone-target-already-exists",
                $"multipass clone target {newName} still reported already-exists after stale-target recovery; " +
                $"{infoDetail}; stderr={retry.Stderr.Trim()}");
        }

        ThrowIfProvisioningRetryExhausted("clone", retry);
        throw new InvalidOperationException($"multipass clone failed after already-exists recovery: {retry.Stderr}");
    }

    internal IReadOnlyList<string> BuildLaunchArgv(string name, SandboxSpec spec, string cloudInitPath)
        => BuildLaunchArgv(ReadOptions(), name, spec, cloudInitPath);

    private static IReadOnlyList<string> BuildLaunchArgv(
        MultipassSandboxOptions opts,
        string name,
        SandboxSpec spec,
        string cloudInitPath)
    {
        var argv = new List<string> { opts.MultipassBinary, "launch", "--name", name };
        if (spec.Limits.CpuCount is { } cpus) argv.AddRange(["--cpus", cpus.ToString()]);
        if (spec.Limits.MemoryBytes is { } mem) argv.AddRange(["--memory", $"{mem / (1024 * 1024)}M"]);
        if (spec.Limits.DiskBytes is { } disk) argv.AddRange(["--disk", $"{disk / (1024 * 1024)}M"]);
        argv.AddRange(["--cloud-init", cloudInitPath]);

        // Host-enforced egress profile. When the spec names a profile and
        // the provider has a bridge mapped for it, attach the VM to that
        // bridge as a SECONDARY network. The agent's only viable internet
        // path is via this bridge — the operator's host-side nftables on
        // the bridge enforces the allowlist; the agent cannot subvert it
        // because the rules live in the host kernel, not the VM.
        // Multipass's default mpqemubr0 is still attached (control plane
        // needs it), but setup-host-networks.sh blocks all forwarding on
        // it so it doesn't carry user traffic.
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
        {
            if (!opts.NetworkProfiles.TryGetValue(spec.Network.ProfileName, out var bridge))
                throw new InvalidOperationException(
                    $"Network profile '{spec.Network.ProfileName}' is not configured in MultipassSandboxOptions.NetworkProfiles. " +
                    $"Configured profiles: [{string.Join(", ", opts.NetworkProfiles.Keys)}]. " +
                    "Either add the profile to options or run setup-host-networks.sh and update appsettings.");
            argv.AddRange(["--network", $"name={bridge},mode=auto"]);
        }

        // ImageReference: empty/null => multipass picks the default image.
        if (!string.IsNullOrWhiteSpace(spec.ImageReference) && spec.ImageReference != "ignored")
            argv.Add(spec.ImageReference);
        else if (!string.IsNullOrWhiteSpace(opts.DefaultImage))
            argv.Add(opts.DefaultImage);

        return argv;
    }

    private async Task LaunchAsync(
        MultipassSandboxOptions opts,
        string name,
        SandboxSpec spec,
        string cloudInitPath,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var argv = BuildLaunchArgv(opts, name, spec, cloudInitPath);
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
            _log.LogInformation("Sandbox {Name}: host-enforced network profile {Profile}", name, spec.Network.ProfileName);
        _log.LogInformation("Launching multipass VM {Name} (this takes 10-30s)", name);
        var run = await RunAsync(opts, argv, stdin: null, ct: ct, workItemId: workItemId);
        if (run.ExitCode != 0)
        {
            ThrowIfProvisioningRetryExhausted("launch", run);
            throw new InvalidOperationException($"multipass launch failed: {run.Stderr}");
        }
    }

    private async Task WaitForRunningAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        // Two waits: first the VM enters "Running" state, then cloud-init
        // finishes applying runcmd (which installs the exec wrapper and
        // swaps the default route to the profile bridge). The exec
        // wrapper is needed before any ExecAsync; the route swap is
        // needed before any agent traffic actually leaves the VM via
        // the host-enforced bridge.
        var startTimeout = opts.VmStartTimeout > TimeSpan.Zero
            ? opts.VmStartTimeout
            : MultipassSandboxOptions.DefaultVmStartTimeout;
        var deadline = DateTime.UtcNow + startTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = await RunAsync(opts, [opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct, workItemId: workItemId);
            ThrowIfProvisioningRetryExhausted("info", info);
            if (info.ExitCode == 0 && info.Stdout.Contains("Running", StringComparison.Ordinal))
                break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (DateTime.UtcNow >= deadline)
        {
            ThrowProvisioningDeferred(
                "vm-start",
                "multipass-vm-start-timeout",
                $"multipass VM {name} did not reach Running state within {startTimeout}");
        }

        await WaitForCloudInitReadyAsync(opts, name, workItemId, ct);
    }

    private async Task WaitForCloudInitReadyAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        // `cloud-init status --wait` blocks until cloud-init has finished.
        // Exit codes (from cloud-init docs):
        //   0  = done
        //   1  = not run / status unavailable. This can be transient, and on
        //        some images the status command bails out even though userdata
        //        has been applied.
        //   2  = degraded done. This is often a benign schema warning on an
        //        otherwise usable VM, but schema validation failures can also
        //        drop user-data blocks. Use the same marker probe as the exit-1
        //        fallback before deciding whether the VM is usable.
        //   >2 = genuine error.
        // We accept only 0 directly. Exit 1 gets a bounded retry, then a marker
        // probe before we decide the VM is actually unusable. Exit 2 is terminal
        // from cloud-init's perspective, so skip retrying and probe immediately.
        var attempts = Math.Max(1, opts.CloudInitReadyRetryAttempts);
        var retryDelay = opts.CloudInitReadyRetryDelay < TimeSpan.Zero
            ? TimeSpan.Zero
            : opts.CloudInitReadyRetryDelay;
        ProcessRunResult cloudInit = default;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cloudInit = await RunCloudInitStatusWaitAsync(
                opts,
                name,
                workItemId,
                ct);
            ThrowIfProvisioningRetryExhausted("exec", cloudInit);

            if (cloudInit.ExitCode == 0)
                return;

            if (cloudInit.ExitCode == 2)
                break;

            if (cloudInit.ExitCode != 1)
                ThrowProvisioningDeferred(
                    "cloud-init",
                    "multipass-cloud-init-failed",
                    $"cloud-init failed for multipass VM {name} (exit {cloudInit.ExitCode}): " +
                    $"{await ReadCloudInitLongStatusAsync(opts, name, workItemId, ct)}");

            if (attempt == attempts)
                break;

            _log.LogInformation(
                "cloud-init status returned exit 1 for multipass VM {Name} (attempt {Attempt}/{Attempts}); retrying after {Delay}. stderr: {Stderr}",
                name, attempt, attempts, retryDelay, DiagnosticText(cloudInit.Stderr));
            await Task.Delay(retryDelay, ct);
        }

        var probe = await ProbeCloudInitReadinessAsync(opts, name, workItemId, ct);
        ThrowIfProvisioningRetryExhausted("exec", probe);
        if (probe.ExitCode == 0)
        {
            if (cloudInit.ExitCode == 2)
            {
                _log.LogWarning(
                    "cloud-init status returned degraded (exit 2) for multipass VM {Name}, but readiness probe passed ({ProbeStdout}); proceeding. Stderr: {Stderr}",
                    name, DiagnosticText(probe.Stdout), DiagnosticText(cloudInit.Stderr));
            }
            else
            {
                _log.LogWarning(
                    "cloud-init status kept returning exit 1 for multipass VM {Name} after {Attempts} attempt(s), but readiness probe passed ({ProbeStdout}); proceeding. Last stderr: {Stderr}",
                    name, attempts, DiagnosticText(probe.Stdout), DiagnosticText(cloudInit.Stderr));
            }

            return;
        }

        if (cloudInit.ExitCode == 2)
        {
            ThrowProvisioningDeferred(
                "cloud-init",
                "multipass-cloud-init-degraded",
                $"cloud-init degraded for multipass VM {name}, and readiness probe failed " +
                $"(exit {probe.ExitCode}; stdout: {DiagnosticText(probe.Stdout)}; stderr: {DiagnosticText(probe.Stderr)}). " +
                $"cloud-init stderr: {DiagnosticText(cloudInit.Stderr)}. " +
                $"{await ReadCloudInitLongStatusAsync(opts, name, workItemId, ct)}. " +
                "Expected /work and /usr/local/bin/codeybox-exec to exist.");
        }

        ThrowProvisioningDeferred(
            "cloud-init",
            "multipass-cloud-init-not-ready",
            $"cloud-init did not report ready for multipass VM {name} after {attempts} attempt(s) " +
            $"(last exit 1 stderr: {DiagnosticText(cloudInit.Stderr)}). " +
            $"readiness probe failed (exit {probe.ExitCode}; stdout: {DiagnosticText(probe.Stdout)}; stderr: {DiagnosticText(probe.Stderr)}). " +
            "Expected /work and /usr/local/bin/codeybox-exec to exist.");
    }

    private async Task<ProcessRunResult> RunCloudInitStatusWaitAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var timeout = ResolveCloudInitStatusWaitTimeout(opts);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return await RunAsync(
                opts,
                [opts.MultipassBinary, "exec", name, "--", "cloud-init", "status", "--wait"],
                stdin: null,
                ct: linkedCts.Token,
                workItemId: workItemId);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            ThrowProvisioningDeferred(
                "cloud-init",
                "multipass-cloud-init-timeout",
                $"cloud-init status --wait for multipass VM {name} did not complete within {timeout}");
            throw;
        }
    }

    private static TimeSpan ResolveCloudInitStatusWaitTimeout(MultipassSandboxOptions opts) =>
        opts.VmStartTimeout > TimeSpan.Zero
            ? opts.VmStartTimeout
            : MultipassSandboxOptions.DefaultVmStartTimeout;

    private Task<ProcessRunResult> ProbeCloudInitReadinessAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        const string script = """
work=missing
exec_wrapper=missing
if test -e /work; then work=present; fi
if test -e /usr/local/bin/codeybox-exec; then exec_wrapper=present; fi
printf '/work=%s /usr/local/bin/codeybox-exec=%s\n' "$work" "$exec_wrapper"
test "$work" = present && test "$exec_wrapper" = present
""";

        return RunAsync(
            opts,
            [opts.MultipassBinary, "exec", name, "--", "bash", "-c", script],
            stdin: null, ct: ct, workItemId: workItemId);
    }

    private async Task<string> ReadCloudInitLongStatusAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var detail = await RunAsync(
            opts,
            [opts.MultipassBinary, "exec", name, "--", "cloud-init", "status", "--long"],
            stdin: null, ct: ct, workItemId: workItemId);
        ThrowIfProvisioningRetryExhausted("exec", detail);
        var stdout = DiagnosticText(detail.Stdout);
        var stderr = DiagnosticText(detail.Stderr);
        return detail.ExitCode == 0
            ? stdout
            : $"cloud-init status --long failed (exit {detail.ExitCode}); stdout: {stdout}; stderr: {stderr}";
    }

    private static string SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Trim().Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string DiagnosticText(string? value)
    {
        var singleLine = SingleLine(value);
        return singleLine.Length == 0 ? "<empty>" : singleLine;
    }

    private static string Tail(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;
        return value[^maxChars..];
    }

    /// <summary>
    /// Polls `multipass info` until the VM's State is "Stopped". Multipass
    /// returns from `multipass stop` once the request is queued, but the
    /// State doesn't flip to Stopped until the QEMU process is fully gone
    /// — and `multipass mount --type=native` rejects any other state
    /// with "Please stop the instance ... before attempting native mounts".
    /// </summary>
    private async Task WaitForStoppedAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var stopTimeout = ResolveVmStopTimeout(opts);
        await WaitForStoppedCoreAsync(
            name,
            stopTimeout,
            ctInner => RunProvisioningAsync(
                opts,
                [opts.MultipassBinary, "info", name, "--format=csv"],
                operation: "info",
                stdin: null,
                ct: ctInner,
                workItemId: workItemId),
            ct);
    }

    internal static TimeSpan ResolveVmStopTimeout(MultipassSandboxOptions opts) =>
        opts.VmStopTimeout > TimeSpan.Zero
            ? opts.VmStopTimeout
            : MultipassSandboxOptions.DefaultVmStopTimeout;

    internal static async Task WaitForStoppedCoreAsync(
        string name,
        TimeSpan stopTimeout,
        Func<CancellationToken, Task<ProcessRunResult>> readInfoAsync,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + stopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = await readInfoAsync(ct);
            if (info.ExitCode == 0 && info.Stdout.Contains("Stopped", StringComparison.Ordinal))
                return;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500),
                ct);
        }
        throw new InvalidOperationException(
            $"multipass VM {name} did not reach Stopped state within {stopTimeout}");
    }

    /// <summary>
    /// Polls `multipass info` while the VM is in the transitional <c>Suspending</c>
    /// state and returns once it has settled (<c>Suspended</c>/<c>Stopped</c>/etc.)
    /// so a subsequent <c>multipass start</c> does not fail against a half-frozen
    /// instance. Best-effort: a non-zero `info` exit (VM gone) or an unreadable
    /// state returns immediately and lets the caller's `start` surface the real
    /// error.
    ///
    /// <para>The wait deadline is the SAME RAM-scaled budget the shutdown suspend
    /// handler used (<see cref="SuspendTimeoutPolicy"/>), keyed off the VM's own
    /// reported RAM (falling back to the default VM profile when info can't report
    /// it). The previous process may have been writing the snapshot for up to that
    /// budget; a shorter fixed cap here would `multipass start` against a still-
    /// Suspending VM, fail, and drive the work item into stranded recovery — the
    /// exact failure mode R8-core exists to prevent. If the snapshot is still
    /// being written when the deadline elapses we proceed and let `start` surface
    /// the error into the standard recovery path.</para>
    /// </summary>
    private async Task WaitWhileSuspendingAsync(
        MultipassSandboxOptions opts,
        string name,
        CancellationToken ct)
    {
        var (state, memoryBytes) = await TryReadStateAndMemoryAsync(opts, name, ct);
        // VM not found / info failed / unreadable: nothing to wait on — let start
        // decide. Already settled (not Suspending): proceed immediately.
        if (state is null || !state.Equals("Suspending", StringComparison.OrdinalIgnoreCase))
            return;

        // Unknown RAM → assume the default VM profile so the cap still covers the
        // documented worst case (30 min for the 12 GiB default) rather than
        // collapsing to the bare floor.
        var budget = SuspendSettleBudgetOverride
            ?? SuspendTimeoutPolicy.For(memoryBytes ?? SandboxResourceLimits.Default.MemoryBytes);
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            ct.ThrowIfCancellationRequested();
            (state, _) = await TryReadStateAndMemoryAsync(opts, name, ct);
            if (state is null || !state.Equals("Suspending", StringComparison.OrdinalIgnoreCase))
                return;
        }
        _log.LogWarning(
            "multipass VM {Name} was still Suspending after {Budget}; attempting start anyway", name, budget);
    }

    /// <summary>
    /// Reads a single VM's lifecycle state and total RAM from
    /// <c>multipass info &lt;name&gt; --format=json</c>. Returns
    /// <c>(null, null)</c> when info fails, the VM is absent, or the JSON can't be
    /// parsed — callers treat that as "nothing to wait on". Parsing the JSON
    /// <c>state</c>/<c>memory.total</c> fields (rather than substring-matching CSV)
    /// avoids false positives/negatives on the critical resume path.
    /// </summary>
    private async Task<(string? State, long? MemoryBytes)> TryReadStateAndMemoryAsync(
        MultipassSandboxOptions opts,
        string name,
        CancellationToken ct)
    {
        var info = await RunAsync(opts, [opts.MultipassBinary, "info", name, "--format=json"], stdin: null, ct: ct);
        ThrowIfProvisioningRetryExhausted("info", info);
        if (info.ExitCode != 0) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(info.Stdout);
            if (!doc.RootElement.TryGetProperty("info", out var infoEl) || infoEl.ValueKind != JsonValueKind.Object)
                return (null, null);
            foreach (var vmEntry in infoEl.EnumerateObject())
            {
                string? state = null;
                if (vmEntry.Value.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String)
                    state = stateEl.GetString();

                long? memoryBytes = null;
                if (vmEntry.Value.TryGetProperty("memory", out var memEl) && memEl.ValueKind == JsonValueKind.Object &&
                    memEl.TryGetProperty("total", out var totalEl))
                {
                    if (totalEl.ValueKind == JsonValueKind.Number && totalEl.TryGetInt64(out var totalNum))
                        memoryBytes = totalNum;
                    else if (totalEl.ValueKind == JsonValueKind.String &&
                             long.TryParse(totalEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalStr))
                        memoryBytes = totalStr;
                }
                return (state, memoryBytes > 0 ? memoryBytes : null);
            }
            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Adds native (virtiofs/9p passthrough) mounts to a Stopped VM.
    /// We wait for State=Stopped on entry as defence-in-depth: callers
    /// give us a freshly-cloned or freshly-stopped VM, but `multipass
    /// info` can briefly report State=Unknown right after the operation
    /// returns, and `multipass mount --type=native` rejects any
    /// non-Stopped state with "Please stop the instance ..."
    /// We use --type=native rather than the default sshfs-based "classic"
    /// mount because classic requires the multipass-sshfs snap inside
    /// the guest, and our host firewall typically blocks the snap store.
    /// </summary>
    private async Task ApplyMountsAsync(
        MultipassSandboxOptions opts,
        string name,
        List<BindMount> binds,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        if (binds.Count == 0) return;
        await WaitForStoppedAsync(opts, name, workItemId, ct);
        foreach (var bind in binds)
        {
            await MountSingleBindWithRetryAsync(opts, name, bind.Host, bind.Sandbox, workItemId, ct);
        }
    }

    private async Task ApplyReadOnlyMountsAsync(
        MultipassSandboxOptions opts,
        string name,
        List<BindMount> binds,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        foreach (var bind in binds)
        {
            if (!bind.ReadOnly)
            {
                continue;
            }

            var remount = await RunAsync(
                opts,
                [opts.MultipassBinary, "exec", name, "--", "sudo", "mount", "-o", "remount,ro", bind.Sandbox],
                stdin: null,
                ct: ct,
                workItemId: workItemId);
            if (remount.ExitCode != 0)
            {
                ThrowIfProvisioningRetryExhausted("mount-readonly", remount);
                throw new InvalidOperationException(
                    $"multipass readonly remount {name}:{bind.Sandbox} failed: {remount.Stderr.Trim()}");
            }
        }
    }

    private readonly record struct BindMount(string Host, string Sandbox, bool ReadOnly);

    /// <summary>
    /// Mounts one host directory into the VM. Stats the source path before
    /// and after a failed attempt to attribute "Source path does not exist"
    /// failures correctly: the source can be missing (orchestrator bug,
    /// racing cleanup), present-but-unreadable to the snap-confined daemon
    /// (AppArmor profile denying access outside <c>~/snap/multipass/common/</c>),
    /// or transiently invisible (FS sync race during a fresh
    /// <c>git clone --bare</c>). A bounded retry covers the transient case
    /// without papering over the structural ones.
    /// </summary>
    // Exposed as internal so a unit test driving a fake IProcessRunner can
    // exercise the mount retry + source-stat branches end-to-end without
    // launching a real multipass VM.
    // Mount retry budget. Three attempts with linear backoff (500ms * attempt)
    // covers the realistic transient-FS-visibility race between a fresh
    // git clone --bare and the snap-confined multipass daemon picking it up
    // without forcing operators to wait on a permanent failure for long.
    internal const int MountMaxAttempts = 3;
    internal static TimeSpan MountAttemptBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * attempt);

    /// <summary>
    /// Mounts one host directory into the VM with bounded retry and per-attempt
    /// host-state diagnostics. When the post-failure orchestrator-side stat of
    /// the source path returns <c>exists=no</c>, a missing source is treated as
    /// terminal: no number of retries can heal it from inside the provider, so
    /// the call fails fast with a <see cref="SandboxMountSourceMissingException"/>
    /// that the orchestrator can selectively recover from (e.g. re-clone the
    /// merge-phase isolated bare clone and retry <see cref="ISandboxProvider.CreateAsync"/>).
    /// Routing recovery through the orchestrator keeps merge-staging knowledge
    /// out of the sandbox-provider layer.
    ///
    /// <para><b>Visibility-class failures.</b> When the orchestrator can stat
    /// the path fine but multipass cannot (e.g. snap-confined daemon's
    /// AppArmor profile only allows reads under <c>~/snap/multipass/common/</c>)
    /// the post-failure state is <c>exists=dir</c>; re-cloning would not
    /// change that. The structural fix lives in
    /// <see cref="IGitHost.GetMergeStagingRoot"/>: route the bind source under
    /// a provider-readable root.</para>
    /// </summary>
    internal async Task MountSingleBindWithRetryAsync(
        MultipassSandboxOptions opts,
        string name,
        string host,
        string sandbox,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        ProcessRunResult? lastFailure = null;
        string? lastFailureState = null;
        var attemptsRun = 0;
        for (var attempt = 1; attempt <= MountMaxAttempts; attempt++)
        {
            attemptsRun = attempt;
            // Stat the host source immediately before each mount attempt so a
            // "Source path does not exist" can be attributed to host state at
            // mount time rather than ambiguous pre-mount state.
            var sourceState = await DescribeMountSourceStateAsync(host, ct);
            _log.LogInformation(
                "multipass mount source state (attempt {Attempt}/{Max}): {Host} -> {Vm}:{Sandbox} state={State}",
                attempt, MountMaxAttempts, host, name, sandbox, sourceState);

            var run = await RunAsync(
                opts,
                [opts.MultipassBinary, "mount", "--type=native", host, $"{name}:{sandbox}"],
                stdin: null, ct: ct, workItemId: workItemId);
            if (run.ExitCode == 0)
                return;
            if (IsMountAlreadyMounted(run, sandbox)
                && await TryRecoverAlreadyMountedAsync(opts, name, host, sandbox, workItemId, ct))
                return;

            var postFailureState = await DescribeMountSourceStateAsync(host, ct);
            lastFailure = run;
            lastFailureState = postFailureState;

            var isMissing = postFailureState.StartsWith("exists=no", StringComparison.Ordinal);
            if (isMissing)
            {
                // Source is definitively missing — the provider cannot heal
                // this from inside the mount loop. Surface the typed
                // exception so the orchestrator can decide whether the path
                // is one it knows how to recreate.
                _log.LogWarning(
                    "multipass mount failed and host source is missing — surfacing typed exception ({Host} -> {Vm}:{Sandbox}, attempt {Attempt}): {Stderr}",
                    host, name, sandbox, attempt, run.Stderr.Trim());
                throw new SandboxMountSourceMissingException(
                    host,
                    $"multipass mount {host} -> {name}:{sandbox} failed after {attemptsRun} attempt(s): " +
                    $"{run.Stderr.Trim()} (post-failure host source state: {postFailureState})");
            }

            if (attempt == MountMaxAttempts)
                break;

            var backoff = MountAttemptBackoff(attempt);
            _log.LogWarning(
                "multipass mount failed (attempt {Attempt}/{Max}, state={State}); retrying in {DelayMs}ms: {Stderr}",
                attempt, MountMaxAttempts, postFailureState, backoff.TotalMilliseconds, run.Stderr.Trim());
            await Task.Delay(backoff, ct);
        }

        // lastFailureState is the post-failure orchestrator-side stat snapshot,
        // not the pre-mount state; label it so a reader of the exception text
        // doesn't misread it as "this is what we saw before issuing mount".
        if (lastFailure is { } exhausted)
        {
            ThrowIfProvisioningRetryExhausted("mount", exhausted);
            ThrowProvisioningDeferred(
                "mount",
                "multipass-mount-retry-exhausted",
                $"multipass mount {host} -> {name}:{sandbox} failed after {attemptsRun} attempt(s): " +
                $"{exhausted.Stderr.Trim()} (post-failure host source state: {lastFailureState})");
        }
        throw new InvalidOperationException(
            $"multipass mount {host} -> {name}:{sandbox} failed after {attemptsRun} attempt(s): " +
            $"{lastFailure?.Stderr.Trim()} (post-failure host source state: {lastFailureState})");
    }

    private async Task<bool> TryRecoverAlreadyMountedAsync(
        MultipassSandboxOptions opts,
        string name,
        string host,
        string sandbox,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var match = await TryReadExistingMountMatchesAsync(opts, name, host, sandbox, workItemId, ct);
        if (match is true)
        {
            _log.LogWarning(
                "multipass mount {Host} -> {Name}:{Sandbox} reported already-mounted; treating mount as successful partial completion",
                host, name, sandbox);
            return true;
        }

        _log.LogWarning(
            match is false
                ? "multipass mount target {Name}:{Sandbox} is already mounted from a different source; unmounting stale target before remount"
                : "multipass mount target {Name}:{Sandbox} reported already-mounted but existing source could not be verified; unmounting before remount",
            name,
            sandbox);
        var unmount = await RunAsync(
            opts,
            [opts.MultipassBinary, "umount", $"{name}:{sandbox}"],
            stdin: null,
            ct: ct,
            workItemId: workItemId);
        if (unmount.ExitCode != 0)
        {
            ThrowIfProvisioningRetryExhausted("umount", unmount);
            _log.LogWarning(
                "multipass umount {Name}:{Sandbox} failed while repairing already-mounted target; mount will retry. stderr={Stderr}",
                name, sandbox, SingleLine(unmount.Stderr));
            return false;
        }

        var remount = await RunAsync(
            opts,
            [opts.MultipassBinary, "mount", "--type=native", host, $"{name}:{sandbox}"],
            stdin: null,
            ct: ct,
            workItemId: workItemId);
        if (remount.ExitCode == 0)
            return true;

        if (IsMountAlreadyMounted(remount, sandbox))
        {
            var remountMatch = await TryReadExistingMountMatchesAsync(opts, name, host, sandbox, workItemId, ct);
            if (remountMatch is true)
                return true;

            _log.LogWarning(
                "multipass remount {Host} -> {Name}:{Sandbox} still reports already-mounted without a verified source match; mount will retry",
                host, name, sandbox);
            return false;
        }

        ThrowIfProvisioningRetryExhausted("mount", remount);
        return false;
    }

    private async Task<bool?> TryReadExistingMountMatchesAsync(
        MultipassSandboxOptions opts,
        string name,
        string host,
        string sandbox,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var info = await RunAsync(
            opts,
            [opts.MultipassBinary, "info", name, "--format=json"],
            stdin: null,
            ct: ct,
            workItemId: workItemId);
        if (info.ExitCode != 0)
        {
            ThrowIfProvisioningRetryExhausted("info", info);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(info.Stdout);
            if (!doc.RootElement.TryGetProperty("info", out var infoEl)
                || infoEl.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var vmEntry in infoEl.EnumerateObject())
            {
                if (!string.Equals(vmEntry.Name, name, StringComparison.Ordinal))
                    continue;
                if (!vmEntry.Value.TryGetProperty("mounts", out var mountsEl)
                    || mountsEl.ValueKind != JsonValueKind.Object)
                    return null;

                foreach (var mount in mountsEl.EnumerateObject())
                {
                    var source = TryGetStringProperty(mount.Value, "source_path")
                        ?? TryGetStringProperty(mount.Value, "source")
                        ?? TryGetStringProperty(mount.Value, "SourcePath");
                    var target = TryGetStringProperty(mount.Value, "target_path")
                        ?? TryGetStringProperty(mount.Value, "target")
                        ?? TryGetStringProperty(mount.Value, "mount_point")
                        ?? TryGetStringProperty(mount.Value, "path");

                    var keyIsSandbox = string.Equals(mount.Name, sandbox, StringComparison.Ordinal);
                    var targetIsSandbox = string.Equals(target, sandbox, StringComparison.Ordinal);
                    var keyIsHost = string.Equals(mount.Name, host, StringComparison.Ordinal);
                    var sourceIsHost = string.Equals(source, host, StringComparison.Ordinal);

                    var targetMatches = keyIsSandbox || targetIsSandbox;
                    var sourceMatches = keyIsHost || sourceIsHost;

                    if (targetMatches)
                        return sourceMatches;
                    if (sourceMatches)
                        return targetMatches;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    /// <summary>
    /// Best-effort description of the host path that a <c>multipass mount</c>
    /// is about to read. Surface enough information to debug a
    /// "Source path does not exist" failure (existence, type, owner, mtime)
    /// without hammering the path with expensive operations. The owner field
    /// matters because the snap-confined multipass daemon runs under a
    /// specific UID; a path the orchestrator can stat fine but the daemon
    /// cannot read presents as "source missing" — owner/mode in the log are
    /// usually enough to distinguish AppArmor denial from racing cleanup.
    ///
    /// Exposed as <c>internal</c> so unit tests can pin the wire format —
    /// regressions in the format are not caught by production callers,
    /// which only read it back as opaque text in log lines.
    ///
    /// The owner/mode lookup goes through the injected <see cref="IProcessRunner"/>
    /// so test doubles intercept every host process spawned at the mount
    /// boundary; otherwise the mount path would have two parallel
    /// process-spawn implementations (one mockable, one not).
    /// </summary>
    internal async Task<string> DescribeMountSourceStateAsync(string hostPath, CancellationToken ct)
    {
        // Catch only the typed exceptions Directory.Exists / new DirectoryInfo
        // can throw for malformed inputs (NUL bytes, invalid chars). Any
        // unexpected exception type indicates a real fault and should
        // propagate so the mount loop fails loudly rather than masking it
        // behind opaque "stat-failed=" diagnostics.
        try
        {
            if (Directory.Exists(hostPath))
            {
                var info = new DirectoryInfo(hostPath);
                return $"exists=dir mtime={info.LastWriteTimeUtc:O}{await TryReadUnixStatAsync(hostPath, ct)}";
            }
            if (File.Exists(hostPath))
            {
                var info = new FileInfo(hostPath);
                return $"exists=file size={info.Length} mtime={info.LastWriteTimeUtc:O}{await TryReadUnixStatAsync(hostPath, ct)}";
            }
            return "exists=no";
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"stat-failed={ex.GetType().Name}:{ex.Message}";
        }
    }

    /// <summary>
    /// Returns a space-prefixed " owner=user:group(uid:gid) mode=NNN" string
    /// for the path on Linux/macOS, or empty when stat is unavailable or
    /// fails. The leading space makes string concatenation safe when this
    /// method returns nothing on non-Unix platforms. Routed through
    /// <see cref="IProcessRunner"/> so unit tests can drive deterministic
    /// stat output without spawning a real subprocess.
    /// </summary>
    private async Task<string> TryReadUnixStatAsync(string hostPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return string.Empty;
        try
        {
            // -c (Linux GNU coreutils) prints the format; macOS uses -f and a
            // different placeholder syntax. multipass-snap is Linux-only, so
            // GNU coreutils' format is the production path.
            var argv = OperatingSystem.IsLinux()
                ? new[] { "stat", "-c", "%U:%G(%u:%g) mode=%a", hostPath }
                : new[] { "stat", "-f", "%Su:%Sg(%u:%g) mode=%Lp", hostPath };
            // Bound the stat at 2s: a hung filesystem during diagnostics
            // should not block the mount loop's outer cancellation budget.
            using var statCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, statCts.Token);
            var run = await _runner.RunAsync(argv, stdin: null, ct: linked.Token);
            if (run.ExitCode != 0)
                return $" owner=stat-rc={run.ExitCode}";
            return " owner=" + run.Stdout.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return " owner=stat-timeout";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $" owner=stat-err:{ex.GetType().Name}";
        }
    }

    private async Task StartAndWaitForRunningAsync(
        MultipassSandboxOptions opts,
        string name,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var start = await RunAsync(opts, [opts.MultipassBinary, "start", name], stdin: null, ct: ct, workItemId: workItemId);
        if (start.ExitCode != 0)
        {
            if (IsStartAlreadyRunning(start))
            {
                _log.LogInformation("multipass start {Name} reported already-running; treating start as successful", name);
            }
            else
            {
                ThrowIfProvisioningRetryExhausted("start", start);
                throw new InvalidOperationException($"multipass start failed: {start.Stderr}");
            }
        }
        await WaitForRunningAsync(opts, name, workItemId, ct);
    }

    // Mount-readiness retry budget. Up to 10 attempts with linear backoff
    // (500ms * attempt) bounds the wait at ~22s per mount — comfortably
    // longer than the observed virtiofs attach lag under audit-parallelism
    // load, short enough that a genuinely broken mount surfaces as a
    // retryable deferred long before the work item's outer time budget.
    internal const int MountReadinessMaxAttempts = 10;
    internal static TimeSpan MountReadinessAttemptBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * attempt);

    /// <summary>
    /// Verifies that every declared bind mount on <paramref name="binds"/>
    /// is actually visible inside the just-started VM. Multipass returns
    /// from <c>start</c> as soon as the VM is in Running state, but a
    /// native (virtiofs) mount registered while the VM was Stopped can
    /// take additional seconds to attach to the guest filesystem under
    /// audit-parallelism load — same class of attach-lag race that
    /// motivates the <see cref="TransferEnvAsync"/> SCP/SFTP retry budget.
    /// Without this gate the first consumer of the mount (most commonly
    /// <c>git clone /repo /work</c>) sees a missing <c>/repo</c> and the
    /// work item fails terminally with exit 128.
    ///
    /// <para>The probe is <c>multipass exec &lt;vm&gt; -- test -e
    /// &lt;path&gt;</c>. For the bare-repo mount specifically
    /// (<c>/repo</c>), a content probe (<c>test -e /repo/HEAD</c>) is used
    /// rather than the mountpoint itself: a stale mountpoint directory can
    /// exist on the guest filesystem even when the virtiofs mount has not
    /// yet attached, so requiring <c>HEAD</c> is a strictly stronger
    /// liveness signal.</para>
    ///
    /// <para>On persistent absence the call throws a retryable
    /// <see cref="SandboxProvisioningDeferredException"/> via
    /// <see cref="ThrowProvisioningDeferred"/> so the orchestrator
    /// re-queues the work item rather than letting the first clone fail
    /// terminally — and the half-built sandbox is cleaned up by the
    /// existing <see cref="CreateAsync"/> catch block.</para>
    ///
    /// Exposed as <c>internal</c> so unit tests driving a fake
    /// <see cref="IProcessRunner"/> can exercise both the transient-
    /// self-heal path and the persistent-deferred path without launching
    /// a real VM.
    /// </summary>
    internal async Task WaitForMountsVisibleAsync(
        MultipassSandboxOptions opts,
        string name,
        IReadOnlyList<(string Host, string Sandbox)> binds,
        WorkItemId? workItemId,
        CancellationToken ct,
        int? maxAttempts = null,
        Func<int, TimeSpan>? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (binds.Count == 0) return;

        var attempts = maxAttempts ?? MountReadinessMaxAttempts;
        if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var computeBackoff = backoff ?? MountReadinessAttemptBackoff;
        var delayFn = delay ?? Task.Delay;

        foreach (var (_, sandbox) in binds)
        {
            var probePath = string.Equals(sandbox, "/repo", StringComparison.Ordinal)
                ? "/repo/HEAD"
                : sandbox;

            ProcessRunResult lastProbe = default;
            var attemptsRun = 0;
            var visible = false;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                attemptsRun = attempt;
                ct.ThrowIfCancellationRequested();
                lastProbe = await RunAsync(
                    opts,
                    [opts.MultipassBinary, "exec", name, "--", "test", "-e", probePath],
                    stdin: null,
                    ct: ct,
                    workItemId: workItemId);
                if (lastProbe.ExitCode == 0)
                {
                    visible = true;
                    break;
                }

                if (attempt == attempts)
                    break;

                var wait = computeBackoff(attempt);
                _log.LogInformation(
                    "multipass mount target {Sandbox} not yet visible inside VM {Name} (attempt {Attempt}/{Max}, probe={Probe}); retrying in {DelayMs}ms",
                    sandbox, name, attempt, attempts, probePath, wait.TotalMilliseconds);
                await delayFn(wait, ct);
            }

            if (visible) continue;

            _log.LogWarning(
                "multipass mount target {Sandbox} did not become visible inside VM {Name} after {Attempts} attempt(s); deferring (probe={Probe}, last exit={Exit}, stderr={Stderr})",
                sandbox, name, attemptsRun, probePath, lastProbe.ExitCode, SingleLine(lastProbe.Stderr));
            ThrowProvisioningDeferred(
                "mount-readiness",
                "multipass-mount-not-visible",
                $"mount {sandbox} on VM {name} did not become visible after {attemptsRun} attempt(s); " +
                $"probe `test -e {probePath}` exited {lastProbe.ExitCode}: {SingleLine(lastProbe.Stderr)}");
        }
    }

    /// <summary>
    /// Transfers the environment file into the VM at <c>~ubuntu/.codeybox-env</c>.
    /// The exec wrapper sources this before running each command, so secret
    /// values never appear on a <c>multipass exec</c> argv (which would
    /// land them on the host's process listing via /proc).
    ///
    /// The file is owned by the <c>ubuntu</c> user (multipass's default exec
    /// identity) with mode 0600 — readable by the agent's process, not by
    /// other VM users. We avoid /run/codeybox/ because that dir is owned by
    /// root and would force a sudo dance to install the file readable by
    /// the non-root exec user.
    ///
    /// The transfer call is wrapped in <see cref="MultipassRetry.RunWithRetryAsync"/>
    /// because multipass returns from <c>launch</c> / <c>start</c> as soon
    /// as the VM is in Running state, but the in-VM <c>sshd</c> can take a
    /// few more seconds to bind its listener — under audit-parallelism load
    /// this race is likely enough to break sandbox creation, and the fix is
    /// to retry the underlying SCP/SFTP transfer rather than block every
    /// healthy creation on a fixed sleep.
    /// </summary>
    private async Task<string> TransferEnvAsync(
        MultipassSandboxOptions opts,
        string name,
        IReadOnlyDictionary<string, string> env,
        string sandboxRoot,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        var envPath = Path.Combine(sandboxRoot, "env");
        await File.WriteAllTextAsync(envPath, BuildEnvironmentFileContent(env), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(envPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var tx = await MultipassRetry.RunWithRetryAsync(
            ctInner => RunAsync(
                opts,
                [opts.MultipassBinary, "transfer", envPath, $"{name}:.codeybox-env"],
                stdin: null, ct: ctInner, workItemId: workItemId),
            _log,
            description: $"multipass transfer env file -> {name}",
            ct);
        if (tx.ExitCode != 0)
        {
            ThrowIfProvisioningRetryExhausted("transfer", tx);
            throw new InvalidOperationException($"multipass transfer env file failed: {tx.Stderr}");
        }

        var perms = await RunAsync(
            opts,
            [opts.MultipassBinary, "exec", name, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"],
            stdin: null, ct: ct, workItemId: workItemId);
        if (perms.ExitCode != 0)
        {
            ThrowIfProvisioningRetryExhausted("exec", perms);
            throw new InvalidOperationException($"failed to chmod env file in VM: {perms.Stderr}");
        }

        return "/home/ubuntu/.codeybox-env";
    }

    internal static string BuildEnvironmentFileContent(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in env)
        {
            if (k.Contains('=') || k.Contains('\n') || k.Contains('\0'))
                throw new ArgumentException($"Invalid env key: {k}");
            // /bin/sh dot-source has undefined behaviour on NUL bytes —
            // some implementations truncate the file at the NUL, others
            // fail with "syntax error". Either way the wrapper would
            // exit 126 with no useful diagnostic, so reject up front.
            if (v.Contains('\0'))
                throw new ArgumentException($"Env value for '{k}' contains NUL byte");
            sb.Append(k).Append('=').Append(ShellSingleQuote(v)).Append('\n');
        }

        return sb.ToString();
    }

    internal static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The exec wrapper script content. Sources the env file (if present),
    /// cds to the target working directory, exec's the user command. Lives
    /// at /usr/local/bin/codeybox-exec inside the VM, owned by root with
    /// mode 0755 so the agent (running as the unprivileged ubuntu user
    /// without sudo) can run but cannot modify it.
    ///
    /// <para>R8-core: when <c>CODEYBOX_AGENT_LOG_FILE</c> is set in the user
    /// environment, both stdout and stderr are tee'd to that path inside the
    /// VM so the host can re-tail it after a multipass suspend/start cycle.
    /// On exit, an <c>.exit</c> sidecar file is written containing the
    /// command's exit code so the startup resume handler can recover the
    /// outcome that the host stream missed when it went away. Both files
    /// are created relative to the directory of CODEYBOX_AGENT_LOG_FILE; the
    /// caller picks a path inside a writable mount (typically /work/.codeybox/).
    /// </para>
    /// </summary>
    internal const string ExecWrapperScript = """
        #!/bin/bash
        # bash, not sh: the tee branch below needs `set -o pipefail` so the
        # pipeline's exit code reflects the agent's exit code rather than tee's
        # (always 0). Ubuntu's /bin/sh is dash, which has neither pipefail nor
        # PIPESTATUS, so an sh shebang would silently report success for every
        # failed agent invocation when CODEYBOX_AGENT_LOG_FILE is set.
        set -o pipefail
        # Non-interactive defaults. Set BEFORE sourcing user env so user env
        # can override if needed. CI=true is respected by many CLIs to skip
        # prompts; DEBIAN_FRONTEND=noninteractive disables apt's tty prompts;
        # GIT_TERMINAL_PROMPT=0 stops git asking for credentials interactively.
        export CI=true
        export DEBIAN_FRONTEND=noninteractive
        export GIT_TERMINAL_PROMPT=0
        set -a
        [ -r "$HOME/.codeybox-env" ] && . "$HOME/.codeybox-env"
        set +a
        # First positional may be a sentinel asking to preserve stdin. By
        # default we redirect stdin from /dev/null so any tool that reads
        # stdin (e.g. dotnet fsi with no args, vim, ssh confirmation, apt
        # without -y, an unterminated heredoc) hits EOF immediately and
        # exits, instead of blocking the agent forever. The orchestrator
        # passes --keep-stdin when an ExecAsync.Stdin pipe is required, or
        # --stdin-file for trusted VM-local payload files.
        keep_stdin=0
        stdin_file=
        if [ "$1" = "--keep-stdin" ]; then
            keep_stdin=1
            shift
        elif [ "$1" = "--stdin-file" ]; then
            if [ "$#" -lt 2 ]; then
                echo "codeybox-exec: --stdin-file requires a path argument" >&2
                exit 127
            fi
            stdin_file=$2
            shift 2
        fi
        # Capture stderr from cd / --env-file source into a tempfile so that
        # a bare exit 126/127 is never silent: without this, the wrapper
        # exits with no context and the orchestrator's lastError is useless
        # for root-causing the failure.
        codeybox_err_file=$(mktemp 2>/dev/null) || codeybox_err_file="/tmp/codeybox-exec-err.$$"
        cd "$1" 2>"$codeybox_err_file"
        codeybox_cd_rc=$?
        if [ "$codeybox_cd_rc" -ne 0 ]; then
            echo "codeybox-exec: failed to cd to '$1' (exit $codeybox_cd_rc):" >&2
            cat "$codeybox_err_file" >&2 2>/dev/null
            rm -f "$codeybox_err_file"
            exit 127
        fi
        shift
        if [ "${1:-}" = "--env-file" ]; then
            if [ "$#" -lt 2 ]; then
                echo "codeybox-exec: --env-file requires a path argument" >&2
                rm -f "$codeybox_err_file"
                exit 127
            fi
            codeybox_env_file_path=$2
            set -a
            . "$codeybox_env_file_path" 2>"$codeybox_err_file"
            codeybox_src_rc=$?
            set +a
            if [ "$codeybox_src_rc" -ne 0 ]; then
                echo "codeybox-exec: failed to source env file '$codeybox_env_file_path' (exit $codeybox_src_rc):" >&2
                cat "$codeybox_err_file" >&2 2>/dev/null
                rm -f "$codeybox_err_file"
                exit 126
            fi
            shift 2
            rm -f "$codeybox_env_file_path" 2>/dev/null || true
        fi
        rm -f "$codeybox_err_file"
        # Optional agent-output HTTP transport. The host injects these
        # variables only for agent CLI invocations that opted into the
        # transport. Copy them into shell locals and remove them from the
        # exported environment before launching the agent. The agent process
        # inherits neither the stream token, completion token, nor the run id.
        # Detached runs authenticate completion through a separate HTTP token;
        # VM-side sidecars are diagnostic only because the agent has sudo inside
        # the sandbox.
        codeybox_output_url="${CODEYBOX_AGENT_OUTPUT_URL:-}"
        codeybox_output_token="${CODEYBOX_AGENT_OUTPUT_TOKEN:-}"
        codeybox_output_exit_token="${CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN:-}"
        codeybox_output_run_id="${CODEYBOX_AGENT_OUTPUT_RUN_ID:-}"
        codeybox_exit_file_path="${CODEYBOX_AGENT_EXIT_FILE:-}"
        unset CODEYBOX_AGENT_OUTPUT_URL CODEYBOX_AGENT_OUTPUT_TOKEN CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN CODEYBOX_AGENT_OUTPUT_RUN_ID CODEYBOX_AGENT_EXIT_FILE
        codeybox_http_post() {
            local codeybox_stream_name="$1"
            local codeybox_stream_seq="$2"
            local codeybox_post_token="$3"
            { printf '%s\n' "$codeybox_post_token"; cat; } | CODEYBOX_AGENT_OUTPUT_URL="$codeybox_output_url" \
            CODEYBOX_AGENT_OUTPUT_RUN_ID="$codeybox_output_run_id" \
            python3 -c '
        import os, sys, time, urllib.error, urllib.parse, urllib.request
        base = os.environ["CODEYBOX_AGENT_OUTPUT_URL"].rstrip("/")
        run_id = os.environ["CODEYBOX_AGENT_OUTPUT_RUN_ID"]
        token = sys.stdin.buffer.readline().decode("utf-8").rstrip("\n")
        stream = sys.argv[1]
        seq = sys.argv[2]
        data = sys.stdin.buffer.read()
        url = base + "/" + urllib.parse.quote(run_id, safe="") + "/" + urllib.parse.quote(stream, safe="") + "/" + str(seq)
        deadline = time.monotonic() + 300.0
        attempt = 0
        while True:
            req = urllib.request.Request(
                url,
                data=data,
                method="POST",
                headers={"Authorization": "Bearer " + token, "Content-Type": "application/octet-stream"})
            try:
                with urllib.request.urlopen(req, timeout=10.0) as resp:
                    code = resp.getcode()
                    if 200 <= code < 300:
                        sys.exit(0)
                    if code in (401, 403, 409, 413):
                        sys.exit(70)
            except urllib.error.HTTPError as exc:
                if exc.code in (401, 403, 409, 413):
                    sys.exit(70)
            except Exception:
                pass
            if time.monotonic() >= deadline:
                sys.exit(75)
            time.sleep(min(5.0, 0.1 * (2 ** min(attempt, 5))))
            attempt += 1
        ' "$codeybox_stream_name" "$codeybox_stream_seq"
        }
        codeybox_http_stream() {
            local codeybox_stream_name="$1"
            CODEYBOX_AGENT_OUTPUT_URL="$codeybox_output_url" \
            CODEYBOX_AGENT_OUTPUT_TOKEN="$codeybox_output_token" \
            CODEYBOX_AGENT_OUTPUT_RUN_ID="$codeybox_output_run_id" \
            python3 -c '
        import os, sys, time, urllib.error, urllib.parse, urllib.request
        base = os.environ["CODEYBOX_AGENT_OUTPUT_URL"].rstrip("/")
        run_id = os.environ["CODEYBOX_AGENT_OUTPUT_RUN_ID"]
        token = os.environ["CODEYBOX_AGENT_OUTPUT_TOKEN"]
        stream = sys.argv[1]
        seq = 0
        max_chunk = 65536
        deadline_seconds = 300.0
        buf = bytearray()

        def post(data):
            global seq
            url = base + "/" + urllib.parse.quote(run_id, safe="") + "/" + urllib.parse.quote(stream, safe="") + "/" + str(seq)
            deadline = time.monotonic() + deadline_seconds
            attempt = 0
            payload = bytes(data)
            while True:
                req = urllib.request.Request(
                    url,
                    data=payload,
                    method="POST",
                    headers={"Authorization": "Bearer " + token, "Content-Type": "application/octet-stream"})
                try:
                    with urllib.request.urlopen(req, timeout=10.0) as resp:
                        code = resp.getcode()
                        if 200 <= code < 300:
                            seq += 1
                            return
                        if code in (401, 403, 409, 413):
                            sys.exit(70)
                except urllib.error.HTTPError as exc:
                    if exc.code in (401, 403, 409, 413):
                        sys.exit(70)
                except Exception:
                    pass
                if time.monotonic() >= deadline:
                    sys.exit(75)
                time.sleep(min(5.0, 0.1 * (2 ** min(attempt, 5))))
                attempt += 1

        while True:
            piece = sys.stdin.buffer.readline(max(1, max_chunk - len(buf)))
            if not piece:
                if buf:
                    post(buf)
                sys.exit(0)
            buf.extend(piece)
            if piece.endswith(b"\n") or len(buf) >= max_chunk:
                post(buf)
                buf.clear()
        ' "$codeybox_stream_name"
        }
        codeybox_http_ready() {
            codeybox_http_post ready 0 "$codeybox_output_token" </dev/null
        }
        codeybox_http_exit() {
            if [ -z "$codeybox_output_exit_token" ]; then
                return 0
            fi
            printf '%s\n' "$1" | codeybox_http_post exit 0 "$codeybox_output_exit_token"
        }
        # Best-effort sidecar write for diagnostic / resume paths. Detached HTTP
        # runs never trust this value as authoritative because the sandbox user
        # can sudo-write VM files.
        codeybox_write_exit_file() {
            if [ -z "$codeybox_exit_file_path" ]; then
                return 0
            fi
            codeybox_exit_dir=$(dirname "$codeybox_exit_file_path")
            mkdir -p "$codeybox_exit_dir" 2>/dev/null || true
            codeybox_exit_tmp="${codeybox_exit_file_path}.tmp"
            { umask 077; printf '%s\n' "$1" > "$codeybox_exit_tmp"; } 2>/dev/null
            mv -f "$codeybox_exit_tmp" "$codeybox_exit_file_path" 2>/dev/null || true
        }
        codeybox_run_user_command() {
            if [ "$keep_stdin" = "1" ]; then
                "$@"
            elif [ -n "$stdin_file" ]; then
                "$@" < "$stdin_file"
            else
                "$@" </dev/null
            fi
        }
        if [ -n "$codeybox_output_url$codeybox_output_token$codeybox_output_run_id" ]; then
            if [ -z "$codeybox_output_url" ] || [ -z "$codeybox_output_token" ] || [ -z "$codeybox_output_run_id" ]; then
                echo "codeybox-exec: agent output HTTP ingest unavailable before launch" >&2
                exit 86
            fi
            if ! codeybox_http_ready; then
                echo "codeybox-exec: agent output HTTP ingest unavailable before launch" >&2
                exit 86
            fi

            if [ -n "${CODEYBOX_AGENT_LOG_FILE:-}" ]; then
                codeybox_log_dir=$(dirname "$CODEYBOX_AGENT_LOG_FILE")
                mkdir -p "$codeybox_log_dir" 2>/dev/null || true
                codeybox_exit_file="${CODEYBOX_AGENT_LOG_FILE}.exit"
                rm -f "$codeybox_exit_file" 2>/dev/null || true
                codeybox_run_user_command "$@" 2>&1 | tee -a "$CODEYBOX_AGENT_LOG_FILE" | codeybox_http_stream stdout
                codeybox_status=("${PIPESTATUS[@]}")
                codeybox_user_rc=${codeybox_status[0]}
                codeybox_stream_rc=${codeybox_status[2]}
                if [ "$codeybox_stream_rc" -ne 0 ]; then
                    echo "codeybox-exec: agent output HTTP ingest failed during run" >&2
                    printf '%s\n' 87 > "$codeybox_exit_file" 2>/dev/null || true
                    codeybox_write_exit_file 87
                    codeybox_http_exit 87 || true
                    exit 87
                fi
                printf '%s\n' "$codeybox_user_rc" > "$codeybox_exit_file" 2>/dev/null || true
                codeybox_write_exit_file "$codeybox_user_rc"
                if ! codeybox_http_exit "$codeybox_user_rc"; then
                    echo "codeybox-exec: agent output HTTP completion failed during run" >&2
                    printf '%s\n' 87 > "$codeybox_exit_file" 2>/dev/null || true
                    codeybox_write_exit_file 87
                    exit 87
                fi
                exit "$codeybox_user_rc"
            fi

            codeybox_out_fifo=$(mktemp -u "${TMPDIR:-/tmp}/codeybox-stdout.XXXXXX")
            codeybox_err_fifo=$(mktemp -u "${TMPDIR:-/tmp}/codeybox-stderr.XXXXXX")
            if ! mkfifo "$codeybox_out_fifo" "$codeybox_err_fifo"; then
                echo "codeybox-exec: agent output HTTP ingest unavailable before launch" >&2
                exit 86
            fi
            codeybox_cleanup_fifos() { rm -f "$codeybox_out_fifo" "$codeybox_err_fifo"; }
            trap codeybox_cleanup_fifos EXIT
            codeybox_http_stream stdout < "$codeybox_out_fifo" &
            codeybox_out_stream_pid=$!
            codeybox_http_stream stderr < "$codeybox_err_fifo" &
            codeybox_err_stream_pid=$!
            codeybox_run_user_command "$@" > "$codeybox_out_fifo" 2> "$codeybox_err_fifo"
            codeybox_user_rc=$?
            wait "$codeybox_out_stream_pid"; codeybox_out_stream_rc=$?
            wait "$codeybox_err_stream_pid"; codeybox_err_stream_rc=$?
            if [ "$codeybox_out_stream_rc" -ne 0 ] || [ "$codeybox_err_stream_rc" -ne 0 ]; then
                echo "codeybox-exec: agent output HTTP ingest failed during run" >&2
                codeybox_write_exit_file 87
                codeybox_http_exit 87 || true
                exit 87
            fi
            codeybox_write_exit_file "$codeybox_user_rc"
            if ! codeybox_http_exit "$codeybox_user_rc"; then
                echo "codeybox-exec: agent output HTTP completion failed during run" >&2
                codeybox_write_exit_file 87
                exit 87
            fi
            exit "$codeybox_user_rc"
        fi
        # Tee path is opt-in via CODEYBOX_AGENT_LOG_FILE. When set, both
        # stdout and stderr stream live to the host AND get tee'd into the
        # log file so a suspend/resume cycle can recover output the host
        # stream lost. The .exit sidecar is overwritten on each invocation
        # with the command's exit code so the orchestrator can poll for
        # completion after a resume without having to read the agent's PID.
        if [ -n "${CODEYBOX_AGENT_LOG_FILE:-}" ]; then
            codeybox_log_dir=$(dirname "$CODEYBOX_AGENT_LOG_FILE")
            mkdir -p "$codeybox_log_dir" 2>/dev/null || true
            codeybox_exit_file="${CODEYBOX_AGENT_LOG_FILE}.exit"
            # Drop any stale exit marker from a previous run so a resumed
            # poller cannot mistake the previous outcome for the current one.
            rm -f "$codeybox_exit_file" 2>/dev/null || true
            # With `set -o pipefail` above, the pipeline's exit code is the
            # rightmost non-zero status — i.e. the agent's exit code, not tee's.
            # ${PIPESTATUS[0]} is the agent process specifically, which is what
            # we want regardless of whether tee itself failed (it never does in
            # practice but we still prefer the agent's true exit code).
            codeybox_run_user_command "$@" 2>&1 | tee -a "$CODEYBOX_AGENT_LOG_FILE"
            codeybox_user_rc=${PIPESTATUS[0]}
            # Best-effort sidecar; the orchestrator treats missing file as
            # "not yet finished" so we never silently swallow a write error.
            printf '%s\n' "$codeybox_user_rc" > "$codeybox_exit_file" 2>/dev/null || true
            codeybox_write_exit_file "$codeybox_user_rc"
            exit "$codeybox_user_rc"
        fi
        if [ -n "$codeybox_exit_file_path" ]; then
            codeybox_run_user_command "$@"
            codeybox_user_rc=$?
            codeybox_write_exit_file "$codeybox_user_rc"
            exit "$codeybox_user_rc"
        fi
        if [ "$keep_stdin" = "1" ]; then
            exec "$@"
        elif [ -n "$stdin_file" ]; then
            exec "$@" < "$stdin_file"
        else
            exec "$@" </dev/null
        fi
        """;

    /// <summary>
    /// Standalone shell script run by codeybox-route.service on every boot
    /// to point the default route at the profile bridge. Idempotent — if
    /// the default route is already correct, the del+add is a no-op
    /// modulo a brief flap.
    /// </summary>
    private const string RouteSwapScript = """
        #!/bin/sh
        set -e
        iface=$(ip -4 -o addr show | awk '/inet 10\.99\./{print $2; exit}')
        if [ -z "$iface" ]; then
            echo "codeybox-route: no 10.99.x.x interface present; nothing to do"
            exit 0
        fi
        gw=$(ip -4 -o addr show "$iface" | awk '{print $4}' | awk -F. '{print $1"."$2"."$3".1"}')
        ip route del default 2>/dev/null || true
        ip route add default via "$gw" dev "$iface"
        echo "codeybox-route: default via $gw dev $iface"
        """;

    private const string GraphicalXvfbService = """
        [Unit]
        Description=CodeyBox graphical X server
        After=network-online.target

        [Service]
        ExecStart=/usr/bin/Xvfb :0 -screen 0 1280x800x24 -nolisten tcp
        Restart=always
        RestartSec=2

        [Install]
        WantedBy=multi-user.target
        """;

    private const string GraphicalXfceService = """
        [Unit]
        Description=CodeyBox XFCE desktop session
        After=codeybox-xvfb.service
        Requires=codeybox-xvfb.service

        [Service]
        User=ubuntu
        Environment=DISPLAY=:0
        Environment=XDG_RUNTIME_DIR=/run/user/1000
        PermissionsStartOnly=true
        ExecStartPre=/bin/mkdir -p /run/user/1000
        ExecStartPre=/bin/chown ubuntu:ubuntu /run/user/1000
        ExecStartPre=/bin/chmod 0700 /run/user/1000
        ExecStart=/usr/bin/dbus-run-session /usr/bin/startxfce4
        Restart=on-failure
        RestartSec=2

        [Install]
        WantedBy=multi-user.target
        """;

    private const string GraphicalVncService = """
        [Unit]
        Description=CodeyBox x11vnc bridge
        After=network-online.target codeybox-xvfb.service
        Wants=network-online.target
        Requires=codeybox-xvfb.service

        [Service]
        ExecStart=/usr/local/sbin/codeybox-vnc
        Restart=on-failure
        RestartSec=2

        [Install]
        WantedBy=multi-user.target
        """;

    private static readonly string GraphicalVncScript = $$"""
        #!/bin/sh
        set -eu
        password_dir=/etc/codeybox
        password_file="${password_dir}/x11vnc.pass"
        plain_password_file=/home/ubuntu/.codeybox-vnc-password
        install -d -m 0700 "$password_dir"
        password=$(dd if=/dev/urandom bs=18 count=1 2>/dev/null | base64 | tr -dc 'A-Za-z0-9' | head -c 12)
        if [ -z "$password" ]; then
            echo "codeybox-vnc: failed to generate VNC password" >&2
            exit 1
        fi
        umask 077
        printf '%s\n' "$password" > "$plain_password_file"
        chown ubuntu:ubuntu "$plain_password_file" || true
        /usr/bin/x11vnc -storepasswd "$password" "$password_file" >/dev/null 2>&1
        chmod 0600 "$password_file"
        listen_addr=$(ip -4 -o addr show | awk '/inet 10\.99\./{split($4,a,"/"); print a[1]; exit}')
        if [ -z "$listen_addr" ]; then
            echo "codeybox-vnc: no profile bridge address found" >&2
            exit 1
        fi
        host_addr=$(printf '%s\n' "$listen_addr" | awk -F. '{print $1"."$2"."$3".1"}')
        exec /usr/bin/x11vnc -display :0 -rfbport {{SandboxConventions.GraphicalVncPort}} -forever -shared -rfbauth "$password_file" -listen "$listen_addr" -allow "$host_addr" -noxdamage -repeat
        """;

    private const string GraphicalInstallRuncmd = """
        set -eux
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y --no-install-recommends xvfb x11vnc xfce4 xfce4-terminal dbus-x11 xdotool scrot ffmpeg x11-utils socat
        systemctl daemon-reload
        systemctl enable codeybox-xvfb.service codeybox-xfce.service codeybox-vnc.service
        systemctl restart codeybox-xvfb.service codeybox-xfce.service codeybox-vnc.service
        """;

    /// <summary>
    /// Builds a cloud-init document that:
    ///   - Installs the exec wrapper at /usr/local/bin/codeybox-exec (root-
    ///     owned, mode 0755) so the agent can execute but not modify it.
    ///   - Installs a systemd service (codeybox-route.service) that swaps
    ///     the VM's default route to the secondary NIC (the profile bridge)
    ///     on EVERY boot. Without this, Linux defaults to eth0 (mpqemubr0),
    ///     whose forwarding is dropped at the host and the agent's traffic
    ///     dies there. We use a systemd service rather than cloud-init's
    ///     runcmd because the orchestrator stop/starts the VM to add native
    ///     mounts, and cloud-init runcmd only runs on the FIRST boot — the
    ///     route would be lost on the post-mount restart.
    ///   - Splices any caller-supplied <paramref name="extraRuncmd"/>
    ///     entries INTO our runcmd block (first-boot one-shots like apt
    ///     install). Don't try to add a separate generated top-level block
    ///     such as <c>runcmd:</c> or <c>write_files:</c> via
    ///     <paramref name="extraCloudInit"/> — duplicate top-level keys can
    ///     clobber generated user-data, so they are rejected before launch.
    ///
    /// Egress filtering is NOT installed in the guest. It lives entirely
    /// on the host (nftables on the profile bridge — see
    /// <c>scripts/setup-host-networks.sh</c>). An in-guest firewall would
    /// be voluntary: a compromised agent with sudo could flush it. The
    /// host bridge enforcement is in the host kernel, where the agent has
    /// no view, and is the only egress boundary we treat as load-bearing.
    /// </summary>
    internal static string BuildCloudInit(
        IReadOnlyList<string>? extraRuncmd,
        string? extraCloudInit,
        SandboxProfileFlavor flavor = SandboxProfileFlavor.Headless,
        bool startRouteService = true,
        bool includeGraphicalInstall = true,
        IReadOnlyList<string>? baselineInstallCommands = null)
    {
        ValidateExtraCloudInitFragment(extraCloudInit);
        var wrapperIndented = string.Join("\n      ", ExecWrapperScript.Split('\n'));
        var routeScriptIndented = string.Join("\n      ", RouteSwapScript.Split('\n'));
        var graphicalXvfbIndented = string.Join("\n      ", GraphicalXvfbService.Split('\n'));
        var graphicalXfceIndented = string.Join("\n      ", GraphicalXfceService.Split('\n'));
        var graphicalVncIndented = string.Join("\n      ", GraphicalVncService.Split('\n'));
        var graphicalVncScriptIndented = string.Join("\n      ", GraphicalVncScript.Split('\n'));
        var installManifestIndented = baselineInstallCommands is not null && baselineInstallCommands.Any(c => !string.IsNullOrWhiteSpace(c))
            ? string.Join("\n      ", BuildBaselineInstallManifest(baselineInstallCommands).Split('\n'))
            : null;

        var sb = new StringBuilder();
        sb.AppendLine("#cloud-config");
        sb.AppendLine("write_files:");
        // Aggressive TCP keepalive defaults so the in-VM agent's connections to
        // api.anthropic.com / github.com / etc. discover dead peers quickly
        // after a multipass suspend/start cycle. The peer doesn't know the
        // suspend happened; without these, the agent's read/write can hang
        // until the OS default (~2h) before retrying. 30s/5s/3 probes detects
        // a dead conn within ~45s of resume in the worst case.
        sb.AppendLine("  - path: /etc/sysctl.d/99-codeybox-keepalive.conf");
        sb.AppendLine("    permissions: '0o644'");
        sb.AppendLine("    content: |");
        sb.AppendLine("      net.ipv4.tcp_keepalive_time = 30");
        sb.AppendLine("      net.ipv4.tcp_keepalive_intvl = 5");
        sb.AppendLine("      net.ipv4.tcp_keepalive_probes = 3");
        sb.AppendLine("  - path: /usr/local/bin/codeybox-exec");
        sb.AppendLine("    permissions: '0o755'");
        sb.AppendLine("    content: |");
        sb.Append("      ").AppendLine(wrapperIndented);
        sb.AppendLine("  - path: /usr/local/sbin/codeybox-route");
        sb.AppendLine("    permissions: '0o755'");
        sb.AppendLine("    content: |");
        sb.Append("      ").AppendLine(routeScriptIndented);
        sb.AppendLine("  - path: /etc/systemd/system/codeybox-route.service");
        sb.AppendLine("    permissions: '0o644'");
        sb.AppendLine("    content: |");
        sb.AppendLine("      [Unit]");
        sb.AppendLine("      Description=CodeyBox default-route swap to profile bridge");
        sb.AppendLine("      After=network-online.target");
        sb.AppendLine("      Wants=network-online.target");
        sb.AppendLine("      [Service]");
        sb.AppendLine("      Type=oneshot");
        sb.AppendLine("      ExecStart=/usr/local/sbin/codeybox-route");
        sb.AppendLine("      RemainAfterExit=yes");
        sb.AppendLine("      [Install]");
        sb.AppendLine("      WantedBy=multi-user.target");
        if (flavor == SandboxProfileFlavor.Graphical)
        {
            sb.AppendLine("  - path: /etc/systemd/system/codeybox-xvfb.service");
            sb.AppendLine("    permissions: '0o644'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalXvfbIndented);
            sb.AppendLine("  - path: /etc/systemd/system/codeybox-xfce.service");
            sb.AppendLine("    permissions: '0o644'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalXfceIndented);
            sb.AppendLine("  - path: /etc/systemd/system/codeybox-vnc.service");
            sb.AppendLine("    permissions: '0o644'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalVncIndented);
            sb.AppendLine("  - path: /usr/local/sbin/codeybox-vnc");
            sb.AppendLine("    permissions: '0o755'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalVncScriptIndented);
        }
        if (installManifestIndented is not null)
        {
            sb.AppendLine("  - path: /var/lib/codeybox/baseline-install-commands.sh");
            sb.AppendLine("    permissions: '0o700'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(installManifestIndented);
        }
        sb.AppendLine("runcmd:");
        // Enable the route service. --now runs it once immediately so the
        // first boot's traffic uses the profile bridge before any package
        // installation or caller extraRuncmd runs.
        sb.AppendLine("  - systemctl daemon-reload");
        sb.AppendLine("  - mkdir -p /work");
        // Apply the keepalive sysctl now so first-boot agent runs benefit,
        // not just post-reboot ones. sysctl --system re-reads every conf
        // under /etc/sysctl.d including the one we just wrote.
        sb.AppendLine("  - sysctl --system");
        sb.AppendLine(startRouteService
            ? "  - systemctl enable --now codeybox-route.service"
            : "  - systemctl enable codeybox-route.service");
        if (flavor == SandboxProfileFlavor.Graphical && includeGraphicalInstall)
            AppendRuncmdCommand(sb, GraphicalInstallRuncmd);
        // Splice caller-supplied runcmd entries into the same block, after the
        // route swap so project/tool installs obey the selected profile.
        if (extraRuncmd is not null)
        {
            foreach (var cmd in extraRuncmd)
                AppendRuncmdCommand(sb, cmd);
        }
        if (!string.IsNullOrWhiteSpace(extraCloudInit))
        {
            sb.AppendLine();
            sb.AppendLine("# --- extra cloud-init from MultipassSandboxOptions.ExtraCloudInit ---");
            sb.AppendLine(extraCloudInit);
        }
        return sb.ToString();
    }

    private static void ValidateExtraCloudInitFragment(string? extraCloudInit)
    {
        if (string.IsNullOrWhiteSpace(extraCloudInit))
            return;

        using var reader = new StringReader(extraCloudInit);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (char.IsWhiteSpace(line[0]))
                continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed is "---" or "...")
                continue;

            if (!TryExtractTopLevelKey(trimmed, out var key))
                continue;

            if (key is "runcmd" or "write_files")
            {
                throw new InvalidOperationException(
                    $"MultipassExtraCloudInit declares top-level '{key}' at line {lineNumber}; this would override " +
                    $"CodeyBox's generated '{key}' block in cloud-init user-data. Put boot commands in " +
                    "MultipassExtraRuncmd instead.");
            }
        }
    }

    /// <summary>
    /// Pulls the top-level YAML mapping key from <paramref name="trimmed"/> (a left-trimmed
    /// line). Strips a single layer of double or single quotes so that the bare,
    /// double-quoted (<c>"runcmd":</c>), and single-quoted (<c>'runcmd':</c>) spellings
    /// all reduce to the same plain key before duplicate-block detection compares them.
    /// Lines whose first quoted segment is not closed before the colon, or that have no
    /// colon at all, are ignored (the duplicate check intentionally only flags plain
    /// scalar keys — every cloud-init top-level block CodeyBox generates is one).
    /// </summary>
    private static bool TryExtractTopLevelKey(string trimmed, out string key)
    {
        key = string.Empty;
        if (trimmed.Length == 0)
            return false;

        var first = trimmed[0];
        if (first is '"' or '\'')
        {
            var close = trimmed.IndexOf(first, 1);
            if (close <= 1)
                return false;
            var afterQuote = trimmed.AsSpan(close + 1).TrimStart();
            if (afterQuote.Length == 0 || afterQuote[0] != ':')
                return false;
            key = trimmed.Substring(1, close - 1);
            return true;
        }

        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return false;

        key = trimmed[..colon].Trim();
        return key.Length > 0;
    }

    /// <summary>
    /// Build the baseline-bake diagnostic manifest written into the baked
    /// baseline image at <c>/var/lib/codeybox/baseline-install-commands.sh</c>.
    /// <para>
    /// The manifest carries ONLY step ordering and a SHA-256 fingerprint of
    /// each configured command — it does NOT persist the command text. Raw
    /// <see cref="MultipassSandboxOptions.ExtraRuncmd"/> entries routinely
    /// carry operator-supplied registry tokens, basic-auth URLs, and
    /// env-assigned API keys (including quoted forms like
    /// <c>GITHUB_TOKEN="ghp_…"</c> or <c>--token 'npm_…'</c> that a regex
    /// redactor cannot reliably scrub). The baked image is inherited by every
    /// LLM-controlled clone, which has shell / tool access; a secret in the
    /// manifest is recoverable from inside the VM. Persisting only hashes
    /// removes the leak surface entirely while preserving the diagnostic value
    /// — operators can hash their configured <c>MultipassExtraRuncmd</c>
    /// entries with <c>sha256sum</c> and confirm step ordering by index.
    /// </para>
    /// </summary>
    private static string BuildBaselineInstallManifest(IReadOnlyList<string> commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("# Rendered by CodeyBox for baseline-bake diagnostics.");
        sb.AppendLine("# Install commands run later via `multipass exec`, not from this file.");
        sb.AppendLine("# Command text is intentionally NOT persisted: install commands can");
        sb.AppendLine("# carry registry tokens / basic-auth URLs / API keys, and this file is");
        sb.AppendLine("# inherited by every cloned (LLM-controlled) agent VM. Each step lists");
        sb.AppendLine("# only the SHA-256 of the configured command; cross-check against");
        sb.AppendLine("# `sha256sum` of MultipassExtraRuncmd entries in your CodeyBox config.");
        var rendered = 0;
        for (var i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            if (string.IsNullOrWhiteSpace(cmd))
                continue;
            rendered++;
            sb.AppendLine();
            sb.AppendLine($"# step {rendered} (configured index {i + 1}) sha256={ComputeSha256Hex(cmd)}");
        }
        return sb.ToString();
    }

    private static string ComputeSha256Hex(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AppendRuncmdCommand(StringBuilder sb, string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return;
        // Each entry is a single shell command. We use the YAML block-literal
        // form (`- |`) so multi-line commands work without escaping.
        sb.AppendLine("  - |");
        foreach (var line in cmd.Split('\n'))
            sb.Append("      ").AppendLine(line);
    }

    private MultipassSandboxOptions ReadOptions() => _optsAccessor();

    /// <summary>
    /// Classifies a multipass argv as a "heavy" daemon/filesystem operation
    /// that should be gated by the provisioning semaphore. Returns true for
    /// the operations that exercise the daemon's MOUNT / lifecycle paths
    /// (where the snap-confined multipassd has been observed stat-timing-out
    /// under concurrent fan-out, breaking <c>git clone /repo /work</c> in the
    /// sandbox with exit 128). Returns false for in-VM <c>multipass exec</c>
    /// and read-only status calls (<c>info</c>, <c>list</c>, <c>version</c>,
    /// …) — those are dispatched at fleet-wide concurrency, since serialising
    /// them would gut throughput without addressing the contention source.
    /// </summary>
    internal static bool IsHeavyMultipassOperation(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2) return false;
        return argv[1] switch
        {
            // Lifecycle ops on the host VM (touch the daemon's instance store
            // and qemu lifecycle): serialise.
            "launch" or "start" or "stop" or "restart" or "suspend"
            or "delete" or "purge"
            // Filesystem ops (sshfs / native mounts, host↔VM file transfer,
            // qcow2 clone): the verified contention point. SERIALISE.
            or "mount" or "umount"
            or "transfer" or "clone"
            // Config writes (rare, but daemon-mutating).
            or "set"
                => true,
            // exec: light. Hundreds per agent-run against an already-booted
            // VM; gating these would cripple fleet throughput. info / list /
            // version: read-only status polls used by WaitForRunning and
            // friends. Anything else (networks, get, find, alias, shell, ...)
            // is non-contention and falls through to ungated.
            _ => false,
        };
    }

    /// <summary>
    /// True iff this operation should incur the inter-boot stagger
    /// (<see cref="MultipassSandboxOptions.BootLaunchDelay"/>) on gate
    /// acquisition. Only <c>launch</c> and <c>start</c> need staggering —
    /// that delay was added to space out qemu spin-up; applying it to
    /// every transfer/stop/delete would needlessly slow down teardown
    /// without changing the IO-contention shape.
    /// </summary>
    private static bool ShouldApplyLaunchDelay(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2) return false;
        return argv[1] is "launch" or "start";
    }

    /// <summary>
    /// Acquires the provisioning gate for an operation classified as
    /// "heavy" by <see cref="IsHeavyMultipassOperation"/> (mount / launch /
    /// start / stop / transfer / delete / clone / …) — these are the
    /// daemon/filesystem operations whose concurrent fan-out has been
    /// observed to stat-time-out the snap-confined multipassd and fail
    /// the in-sandbox <c>git clone /repo /work</c> with exit 128.
    /// <para>
    /// Light operations (<c>multipass exec</c>, status polls) return a
    /// no-op disposable so they run at unbounded concurrency — gating
    /// exec would cripple agent throughput without addressing the
    /// observed contention source.
    /// </para>
    /// <para>
    /// The gate is keyed off <see cref="MultipassSandboxOptions.MaxConcurrentBoots"/>
    /// (kept for back-compat with the original boot-only semantic) and
    /// is hot-reloadable: the semaphore is recreated when the configured
    /// limit changes between acquisitions, under a lock guard.
    /// </para>
    /// </summary>
    internal Task<IDisposable> EnterMultipassOpGateAsync(
        MultipassSandboxOptions opts,
        IReadOnlyList<string> argv,
        CancellationToken ct)
    {
        if (!IsHeavyMultipassOperation(argv))
            return Task.FromResult<IDisposable>(NoOpDisposable.Instance);
        return AcquireGateSlotAsync(opts, ShouldApplyLaunchDelay(argv), ct);
    }

    /// <summary>
    /// Acquires the provisioning gate, limiting concurrent multipass
    /// launch/start operations to <see cref="MultipassSandboxOptions.MaxConcurrentBoots"/>.
    /// <para>
    /// Kept as the public/internal entry point for tests that exercise the
    /// boot-stagger primitive directly. Always applies
    /// <see cref="MultipassSandboxOptions.BootLaunchDelay"/>. For routing
    /// arbitrary multipass operations through the same semaphore, prefer
    /// <see cref="EnterMultipassOpGateAsync"/> which gates only heavy ops
    /// and skips the delay for non-boot heavy operations.
    /// </para>
    /// <para>
    /// The gate is hot-reloadable: if the configured limit changes between
    /// acquisitions the semaphore is recreated at the new size (lock-guarded).
    /// Callers MUST dispose the returned handle to release the slot.
    /// </para>
    /// <para>
    /// Downward reconfiguration transiently exceeds the new limit: in-flight
    /// holders still reference the old semaphore and do not count against
    /// the new capacity until they release. This window is bounded by the
    /// duration of the longest in-flight boot operation and is the safe
    /// trade-off vs. disposing the old semaphore (which would crash
    /// in-flight holders that later call Release() on a disposed object).
    /// </para>
    /// </summary>
    internal Task<IDisposable> EnterBootGateAsync(MultipassSandboxOptions opts, CancellationToken ct)
        => AcquireGateSlotAsync(opts, applyLaunchDelay: true, ct);

    private async Task<IDisposable> AcquireGateSlotAsync(
        MultipassSandboxOptions opts,
        bool applyLaunchDelay,
        CancellationToken ct)
    {
        var desired = opts.MaxConcurrentBoots;
        if (desired < 1)
        {
            _log.LogWarning(
                "MaxConcurrentBoots is {ConfiguredValue}; clamping to 1. " +
                "Negative or zero values are invalid and are ignored.",
                opts.MaxConcurrentBoots);
            desired = 1;
        }

        SemaphoreSlim sem;
        lock (_bootGateGuard)
        {
            if (_bootGate is null || _bootGateCapacity != desired)
            {
                // Do NOT dispose the old gate: in-flight BootGateReleaser
                // instances still reference it and will Release() in their
                // Dispose(). The old semaphore becomes unreferenced once all
                // in-flight holders complete and will be GC'd normally.
                _bootGate = new SemaphoreSlim(desired, desired);
                if (_bootGateCapacity == 0)
                    _log.LogDebug("Provisioning gate created with capacity {Capacity}", desired);
                else
                    _log.LogDebug(
                        "Provisioning gate size changed from {OldCapacity} to {NewCapacity}",
                        _bootGateCapacity, desired);
                _bootGateCapacity = desired;
            }
            sem = _bootGate;
        }

        await sem.WaitAsync(ct);

        if (!applyLaunchDelay)
            return new BootGateReleaser(sem);

        var delay = opts.BootLaunchDelay;
        if (delay < TimeSpan.Zero)
        {
            _log.LogWarning(
                "BootLaunchDelay is negative ({ConfiguredDelay}); ignoring. " +
                "Use zero or a positive duration to enable the inter-boot delay.",
                delay);
        }
        else if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                sem.Release();
                throw;
            }
        }

        return new BootGateReleaser(sem);
    }

    private sealed class BootGateReleaser(SemaphoreSlim sem) : IDisposable
    {
        private SemaphoreSlim? _sem = sem;

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        internal static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }

    private static IReadOnlyList<string> BuildFirstBootRuncmd(
        MultipassSandboxOptions opts,
        SandboxProfileFlavor flavor)
    {
        if (flavor != SandboxProfileFlavor.Graphical)
            return opts.ExtraRuncmd;

        var commands = new List<string>(opts.ExtraRuncmd.Count + 1)
        {
            GraphicalInstallRuncmd,
        };
        commands.AddRange(opts.ExtraRuncmd);
        return commands;
    }

    private async Task<ProcessRunResult> RunAsync(
        MultipassSandboxOptions opts,
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        WorkItemId? workItemId = null)
    {
        var environment = BuildHostProcessEnvironment(workItemId);
        // The op-gate is the choke point that bounds concurrent daemon /
        // filesystem operations (mount / launch / start / stop / transfer
        // / delete / clone) to MaxConcurrentBoots. Light operations
        // (multipass exec, info polls, version) classify as non-heavy and
        // get a no-op disposable so they run at unbounded concurrency —
        // gating exec would cripple agent throughput, and the contention
        // source verified on 2026-06-10/11 was the heavy ops, not exec.
        // The gate is held for the full retry budget so a transient-failure
        // retry doesn't release-and-reacquire (which would let other heavy
        // ops barge in and re-amplify the concurrent-mount problem).
        using var gate = await EnterMultipassOpGateAsync(opts, argv, ct).ConfigureAwait(false);
        return await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            ctInner => _runner.RunAsync(argv, stdin, ctInner, environment: environment),
            ctInner => MultipassDaemonRetry.ProbeDaemonAsync(
                _runner, opts.MultipassBinary, _daemonRetryPolicy.HealthProbeTimeout, ctInner),
            _log,
            workItemId,
            ct,
            _daemonRetryPolicy).ConfigureAwait(false);
    }

    internal static IReadOnlyDictionary<string, string>? BuildHostProcessEnvironment(WorkItemId? workItemId)
    {
        if (workItemId is not { } owner)
            return null;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                environment[key] = value;
        }

        environment[SandboxConventions.WorkItemIdEnvironmentVariable] = owner.ToString();
        return environment;
    }

    private async Task<ProcessRunResult> RunProvisioningAsync(
        MultipassSandboxOptions opts,
        IReadOnlyList<string> argv,
        string operation,
        string? stdin,
        CancellationToken ct,
        WorkItemId? workItemId = null)
    {
        var result = await RunAsync(opts, argv, stdin, ct, workItemId);
        ThrowIfProvisioningRetryExhausted(operation, result);
        return result;
    }

    private void ThrowIfProvisioningRetryExhausted(string operation, ProcessRunResult result)
    {
        if (!MultipassDaemonRetry.TryGetRetryExhaustedErrorClass(result, out var errorClass))
            return;

        ThrowProvisioningDeferred(
            operation,
            string.IsNullOrWhiteSpace(errorClass) ? "multipass-transient" : errorClass,
            result.Stderr.Trim());
    }

    private void ThrowProvisioningDeferred(string operation, string errorClass, string detail)
    {
        throw new SandboxProvisioningDeferredException(
            Name,
            operation,
            errorClass,
            detail.Trim(),
            _daemonRetryPolicy.ExhaustedRequeueDelay);
    }

    private static bool IsCloneTargetAlreadyExists(ProcessRunResult result, string name) =>
        result.ExitCode != 0
        && result.Stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase)
        && result.Stderr.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsStartAlreadyRunning(ProcessRunResult result) =>
        result.ExitCode != 0
        && (result.Stderr.Contains("already running", StringComparison.OrdinalIgnoreCase)
            || result.Stderr.Contains("already started", StringComparison.OrdinalIgnoreCase));

    private static bool IsMountAlreadyMounted(ProcessRunResult result, string sandboxPath) =>
        result.ExitCode != 0
        && result.Stderr.Contains("already mounted", StringComparison.OrdinalIgnoreCase)
        && (result.Stderr.Contains(sandboxPath, StringComparison.Ordinal)
            || result.Stderr.Contains("is already mounted", StringComparison.OrdinalIgnoreCase));

    private async Task<bool> TryDeleteVmAsync(MultipassSandboxOptions opts, string name)
    {
        try
        {
            var result = await RunAsync(
                opts,
                [opts.MultipassBinary, "delete", "--purge", name],
                stdin: null,
                ct: CancellationToken.None);
            if (result.ExitCode == 0)
                return true;

            _log.LogWarning(
                "Best-effort multipass delete --purge {Name} failed (exit {ExitCode}): {Stderr}",
                name,
                result.ExitCode,
                result.Stderr);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Best-effort multipass delete --purge {Name} threw", name);
            return false;
        }
    }

    private async Task<bool> SandboxMayStillExistAfterFailedDeleteAsync(
        MultipassSandboxOptions opts,
        string name)
    {
        try
        {
            var info = await RunAsync(
                opts,
                [opts.MultipassBinary, "info", name, "--format=json"],
                stdin: null,
                ct: CancellationToken.None);
            if (info.ExitCode == 0)
                return true;
            if (IsInstanceNotFound(info.Stderr))
                return false;

            _log.LogWarning(
                "Could not prove failed-create sandbox {Name} was absent after delete --purge failed (info exit {ExitCode}): {Stderr}",
                name,
                info.ExitCode,
                info.Stderr);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not prove failed-create sandbox {Name} was absent after delete --purge failed",
                name);
            return true;
        }
    }

    private static bool IsInstanceNotFound(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        return stderr.Contains("argument not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("instance not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct MultipassSandboxDetails(long? DiskBytes, DateTimeOffset? CreatedAt, string? State = null);

internal sealed class MultipassDaemonRetryPolicy
{
    public static MultipassDaemonRetryPolicy Default { get; } = new();

    public int MaxAttempts { get; init; } = 3;
    public IReadOnlyList<TimeSpan> Backoffs { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    public TimeSpan HealthProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ExhaustedRequeueDelay { get; init; } = TimeSpan.FromSeconds(30);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);
}

internal readonly record struct MultipassDaemonHealthProbeResult(bool IsHealthy, string Error)
{
    public static MultipassDaemonHealthProbeResult Healthy() => new(true, "");
    public static MultipassDaemonHealthProbeResult Unhealthy(string error) => new(false, error);
}

/// <summary>
/// Retries multipass CLI operations when multipassd itself is temporarily
/// unreachable or reports a qemu crash. These failures can occur while the
/// daemon restarts after a host-side crash; retrying here keeps recoverable
/// sandbox launches from immediately failing the work item.
/// </summary>
internal static class MultipassDaemonRetry
{
    private static readonly HashSet<string> RetryableCommands = new(StringComparer.Ordinal)
    {
        "launch",
        "start",
        "exec",
        "info",
        "clone",
        "mount",
        "umount",
        "stop",
        "transfer",
    };

    internal static async Task<ProcessRunResult> RunWithRetryAsync(
        IReadOnlyList<string> argv,
        Func<CancellationToken, Task<ProcessRunResult>> action,
        Func<CancellationToken, Task<MultipassDaemonHealthProbeResult>> healthProbe,
        ILogger log,
        WorkItemId? workItemId,
        CancellationToken ct,
        MultipassDaemonRetryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(healthProbe);
        ArgumentNullException.ThrowIfNull(log);

        policy ??= MultipassDaemonRetryPolicy.Default;
        if (policy.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(policy.MaxAttempts));
        if (policy.Backoffs.Count < policy.MaxAttempts - 1)
            throw new ArgumentException("Backoffs must contain one delay per retry.", nameof(policy));

        ProcessRunResult result = default;
        string? errorClass = null;
        var description = Describe(argv);
        var operation = argv.Count >= 2 ? argv[1] : "multipass";

        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            result = await action(ct).ConfigureAwait(false);
            errorClass = ClassifyTransient(argv, result);
            if (errorClass is null)
                return result;

            if (attempt == policy.MaxAttempts)
                break;

            var retryOrdinal = attempt;
            var probe = await healthProbe(ct).ConfigureAwait(false);
            var delay = policy.Backoffs[attempt - 1];
            AuditTransientRetry(workItemId, operation, retryOrdinal, errorClass);

            if (retryOrdinal == 1)
            {
                log.LogInformation(
                    "{Description}: transient multipass daemon error ({ErrorClass}) on attempt {Attempt}/{MaxAttempts}; healthProbe={HealthProbe}; retrying after {Delay}",
                    description, errorClass, attempt, policy.MaxAttempts, FormatProbe(probe), delay);
            }
            else
            {
                log.LogWarning(
                    "{Description}: transient multipass daemon error ({ErrorClass}) on attempt {Attempt}/{MaxAttempts}; healthProbe={HealthProbe}; retrying after {Delay}",
                    description, errorClass, attempt, policy.MaxAttempts, FormatProbe(probe), delay);
            }

            await policy.Delay(delay, ct).ConfigureAwait(false);
        }

        var retries = policy.MaxAttempts - 1;
        var finalProbe = await healthProbe(ct).ConfigureAwait(false);
        var stderr = result.Stderr.Trim();
        var message = finalProbe.IsHealthy
            ? $"multipass transient daemon error after {retries} retries ({errorClass}) during {description}: {stderr}"
            : $"multipass daemon unreachable after {retries} retries ({errorClass}) during {description}; health probe failed: {finalProbe.Error}; last stderr: {stderr}";
        log.LogError("{Message}", message);
        return result with { Stderr = message, ExecutionUnavailable = true };
    }

    internal static string? ClassifyTransient(IReadOnlyList<string> argv, ProcessRunResult result)
    {
        if (result.ExitCode == 0 || argv.Count < 2)
            return null;
        var command = argv[1];
        if (!RetryableCommands.Contains(command))
            return null;

        var stderr = result.Stderr ?? "";
        if (stderr.Contains("qemu-system-x86_64; error: Process crashed", StringComparison.OrdinalIgnoreCase))
            return "qemu-process-crashed";
        if (stderr.Contains("Could not acquire lock", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("multipassd-vm-instances", StringComparison.OrdinalIgnoreCase))
            return "multipass-instance-lock-contention";
        if (stderr.Contains("cannot connect to the multipass socket", StringComparison.OrdinalIgnoreCase))
            return "multipass-socket-unreachable";
        if ((command == "launch" || command == "start")
            && stderr.Contains("cannot connect to", StringComparison.OrdinalIgnoreCase))
            return "multipass-daemon-unreachable";
        if (command == "start"
            && stderr.Contains("argument not found", StringComparison.OrdinalIgnoreCase))
            return "multipass-start-argument-not-found";
        if (stderr.Contains("socket", StringComparison.OrdinalIgnoreCase))
            return "multipass-socket-error";
        return null;
    }

    internal static bool TryGetRetryExhaustedErrorClass(ProcessRunResult result, out string errorClass)
    {
        errorClass = "";
        if (result.ExitCode == 0)
            return false;

        var stderr = result.Stderr ?? "";
        var exhausted =
            stderr.StartsWith("multipass transient daemon error after ", StringComparison.Ordinal)
            || stderr.StartsWith("multipass daemon unreachable after ", StringComparison.Ordinal);
        if (!exhausted)
            return false;

        var open = stderr.IndexOf(" retries (", StringComparison.Ordinal);
        if (open >= 0)
        {
            open += " retries (".Length;
            var close = stderr.IndexOf(')', open);
            if (close > open)
            {
                errorClass = stderr[open..close];
                return true;
            }
        }

        errorClass = "multipass-transient";
        return true;
    }

    /// <summary>
    /// Probes <c>multipass version</c> with a bounded deadline to decide whether
    /// multipassd is reachable. Used by <see cref="RunWithRetryAsync"/> between
    /// retries to attribute a failure to the daemon vs a transient flap.
    /// Exposed for direct unit testing of each branch (healthy, non-zero exit,
    /// probe timeout, caller cancellation, generic exception).
    /// </summary>
    internal static async Task<MultipassDaemonHealthProbeResult> ProbeDaemonAsync(
        IProcessRunner runner,
        string multipassBinary,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var probe = await runner.RunAsync([multipassBinary, "version"], stdin: null, timeoutCts.Token)
                .ConfigureAwait(false);
            if (probe.ExitCode == 0)
                return MultipassDaemonHealthProbeResult.Healthy();
            return MultipassDaemonHealthProbeResult.Unhealthy(
                $"multipass version failed (exit {probe.ExitCode}): {probe.Stderr.Trim()}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return MultipassDaemonHealthProbeResult.Unhealthy(
                $"multipass version timed out after {timeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex)
        {
            return MultipassDaemonHealthProbeResult.Unhealthy(
                $"multipass version probe threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AuditTransientRetry(WorkItemId? workItemId, string operation, int attempt, string errorClass)
    {
        if (workItemId.HasValue)
            AuditLog.SandboxProvisioningTransientRetry(workItemId.Value, operation, attempt, errorClass);
    }

    private static string Describe(IReadOnlyList<string> argv)
    {
        if (argv.Count >= 2)
            return $"{Path.GetFileName(argv[0])} {argv[1]}";
        return argv.Count == 1 ? Path.GetFileName(argv[0]) : "multipass";
    }

    private static string FormatProbe(MultipassDaemonHealthProbeResult probe) =>
        probe.IsHealthy ? "healthy" : "unreachable: " + probe.Error;
}

/// <summary>
/// Retries a multipass-CLI call when its stderr indicates the in-VM SSH
/// daemon hasn't yet bound to its listener. After <c>multipass launch</c>
/// (or <c>start</c>) returns, the VM shows as Running but <c>sshd</c> can
/// still take a few seconds to come up; SCP/SFTP-based operations
/// (<c>multipass transfer</c>) race that window and fail with "Connection
/// refused" or "Connection reset by peer". This race is more likely under
/// audit-parallelism load when several VMs are starting at once.
///
/// We retry on those specific stderr signatures with exponential backoff
/// and a bounded wall-clock budget. Persistent SSH refusal, or any other
/// multipass error ("instance not found", auth failure, etc.) fails fast
/// — the bug we're papering over is *transient*; a stuck VM still needs
/// to surface.
/// </summary>
internal static class MultipassRetry
{
    /// <summary>Number of attempts including the first try. 6 attempts → up to ~23s of delay.</summary>
    internal const int DefaultMaxAttempts = 6;
    /// <summary>Initial delay before the first retry.</summary>
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(1);
    /// <summary>Maximum delay between any two retries.</summary>
    internal static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(8);

    /// <summary>
    /// True when <paramref name="stderr"/> matches one of the transient
    /// SSH-not-ready signatures we retry on. Anything else — including
    /// "instance not found", auth errors, or unrelated multipass failures
    /// — is treated as non-retryable so the caller can fail fast.
    /// </summary>
    internal static bool IsSshNotReady(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Connection reset by peer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Exponential backoff: <c>initial * 2^attempt</c>, capped at <paramref name="max"/>.
    /// <paramref name="attempt"/> is 0-indexed (the delay BEFORE retry attempt N+1).
    /// </summary>
    internal static TimeSpan ComputeBackoff(int attempt, TimeSpan initial, TimeSpan max)
    {
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (max < initial) throw new ArgumentOutOfRangeException(nameof(max));
        // Cap the shift exponent to keep arithmetic well-defined even for
        // pathologically large attempt numbers. Anything past ~30 is already
        // saturated against `max` anyway.
        var shift = Math.Min(attempt, 30);
        var multiplier = 1L << shift;
        // Use checked arithmetic: even with shift capped at 30, initial.Ticks * multiplier
        // could overflow for a very large initial. Saturate to max on overflow.
        long ticks;
        try { ticks = checked(initial.Ticks * multiplier); }
        catch (OverflowException) { return max; }
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// Runs <paramref name="action"/>, retrying when its result's stderr
    /// indicates SSH-not-ready. Returns the final <see cref="ProcessRunResult"/> —
    /// the caller is responsible for translating a non-zero ExitCode into
    /// an exception. Non-retryable failures (any non-zero exit whose stderr
    /// is NOT a known SSH-not-ready signature) short-circuit immediately.
    ///
    /// <para>For tests, pass <paramref name="delay"/> and <paramref name="backoff"/>
    /// to avoid sleeping; production callers use the defaults.</para>
    /// </summary>
    internal static async Task<ProcessRunResult> RunWithRetryAsync(
        Func<CancellationToken, Task<ProcessRunResult>> action,
        ILogger log,
        string description,
        CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts,
        Func<int, TimeSpan>? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(log);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        backoff ??= attempt => ComputeBackoff(attempt, DefaultInitialDelay, DefaultMaxDelay);
        delay ??= static (d, t) => Task.Delay(d, t);

        ProcessRunResult result = default;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            result = await action(ct).ConfigureAwait(false);
            if (result.ExitCode == 0) return result;
            // Non-retryable error: fail fast so callers see the real diagnostic
            // (e.g. "instance not found") rather than a 30-second stall on what
            // was never going to recover.
            if (!IsSshNotReady(result.Stderr)) return result;
            // Last attempt — no delay, return so caller can throw.
            if (attempt == maxAttempts - 1) break;
            var d = backoff(attempt);
            log.LogDebug(
                "{Description}: SSH not ready (attempt {Attempt}/{Max}); retrying after {Delay}. stderr: {Stderr}",
                description, attempt + 1, maxAttempts, d, result.Stderr.Trim());
            await delay(d, ct).ConfigureAwait(false);
        }
        log.LogWarning(
            "{Description}: SSH still refusing after {Max} attempts; surfacing failure. stderr: {Stderr}",
            description, maxAttempts, result.Stderr.Trim());
        return result with { ExecutionUnavailable = true };
    }
}

/// <summary>
/// A provider-neutral baseline-bake validation command: a one-shot in-VM command the
/// provider runs against a freshly-baked baseline before it is stopped and reused as a
/// clone source. <see cref="Label"/> is a human-readable identifier used only in log lines
/// and error messages; the provider does not interpret it. Mapping from agent (or any
/// other producer) to validation command lives in the composition layer — this record
/// keeps the sandbox infrastructure layer free of agent-catalog semantics.
/// </summary>
public sealed record MultipassBaselineVerificationCommand(
    string Label,
    IReadOnlyList<string> Argv,
    string? FailureHint = null);

public sealed record MultipassSandboxOptions
{
    public const int DefaultCloudInitReadyRetryAttempts = 3;
    public static readonly TimeSpan DefaultCloudInitReadyRetryDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultVmStartTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromMinutes(2);
    public const int DefaultMaxConcurrentBoots = 2;
    public static readonly TimeSpan DefaultBootLaunchDelay = TimeSpan.Zero;

    public string MultipassBinary { get; init; } = "multipass";

    /// <summary>
    /// Default image alias when SandboxSpec.ImageReference is empty / "ignored".
    /// E.g. "24.04". Empty → multipass picks the current LTS.
    /// </summary>
    public string? DefaultImage { get; init; }

    /// <summary>
    /// Shell commands to run inside the sandbox VM at first boot, after
    /// the default-route swap (so they have working egress). Use for
    /// one-shot setup the project needs in the sandbox — installing the
    /// agent CLI, the language toolchain, any auditor tools the project's
    /// audit policy expects to be present. Each entry is a single shell
    /// command (multi-line OK).
    ///
    /// Prefer this over <see cref="ExtraCloudInit"/> when you need to run
    /// commands at boot — extra cloud-init that adds its own
    /// <c>runcmd:</c> would clobber the orchestrator's route swap.
    /// </summary>
    public IReadOnlyList<string> ExtraRuncmd { get; init; } = [];

    /// <summary>
    /// Commands that must pass inside a freshly baked baseline before it is
    /// stopped and reused as a clone source. The composition layer (e.g. the API)
    /// is responsible for deciding what to validate; for the agent-CLI use case it
    /// derives this list from configured AgentClass members so enabling a CLI-backed
    /// agent fails the bake immediately if its binary is missing from PATH. The
    /// sandbox layer itself only executes the commands and surfaces failures by
    /// <see cref="MultipassBaselineVerificationCommand.Label"/>.
    /// </summary>
    public IReadOnlyList<MultipassBaselineVerificationCommand> BaselineVerificationCommands { get; init; } = [];

    /// <summary>
    /// Extra cloud-init YAML appended after the orchestrator's own
    /// directives. Safe for top-level keys CodeyBox does not generate
    /// (<c>packages:</c>, <c>apt:</c>, etc.). Do NOT use this to add
    /// <c>runcmd:</c> or <c>write_files:</c>; those would duplicate generated
    /// top-level keys and are rejected before launch. Use
    /// <see cref="ExtraRuncmd"/> for boot-time commands.
    /// </summary>
    public string? ExtraCloudInit { get; init; }

    /// <summary>
    /// Where to stage cloud-init files and tmpfs-backing directories.
    /// Defaults to <c>~/snap/multipass/common/codeybox-staging</c> when the
    /// snap install is detected; falls back to /tmp otherwise. Override
    /// only if your Multipass install reads a different prefix.
    /// </summary>
    public string? StagingDirectory { get; init; }

    /// <summary>
    /// Number of <c>cloud-init status --wait</c> attempts before falling back
    /// to the VM readiness probe when the status command returns exit 1.
    /// </summary>
    public int CloudInitReadyRetryAttempts { get; init; } = DefaultCloudInitReadyRetryAttempts;

    /// <summary>
    /// Delay between retries when <c>cloud-init status --wait</c> returns exit 1.
    /// </summary>
    public TimeSpan CloudInitReadyRetryDelay { get; init; } = DefaultCloudInitReadyRetryDelay;

    /// <summary>
    /// Deadline for the post-launch poll that waits for <c>multipass info</c>
    /// to report the VM in the <c>Running</c> state, and for each blocking
    /// <c>cloud-init status --wait</c> readiness probe. Defaults to 3 minutes.
    /// Bump on hosts that observe boot contention (concurrent launches starving
    /// disk/CPU) — a healthy VM can exceed the default when 6+ VMs boot at once
    /// even though a clean isolated launch completes in ~106s.
    /// </summary>
    public TimeSpan VmStartTimeout { get; init; } = DefaultVmStartTimeout;

    /// <summary>
    /// Deadline for the post-stop poll that waits for <c>multipass info</c> to
    /// report the VM in the <c>Stopped</c> state. Defaults to 2 minutes.
    /// </summary>
    public TimeSpan VmStopTimeout { get; init; } = DefaultVmStopTimeout;

    /// <summary>
    /// Maps logical network-profile names (selected via
    /// <c>SandboxNetworkPolicy.ProfileName</c>) to host bridge interface
    /// names. The bridges must already exist on the host with their
    /// nftables egress rules — operators set this up once via
    /// <c>scripts/setup-host-networks.sh</c>.
    ///
    /// Example:
    /// <code>
    /// new Dictionary&lt;string, string&gt; {
    ///     ["isolated"]  = "cb-iso",
    ///     ["claude"]    = "cb-claude",
    ///     ["multi-llm"] = "cb-multi-llm",
    /// }
    /// </code>
    /// Bridge names are limited to 15 characters by Linux IFNAMSIZ.
    ///
    /// When a sandbox spec selects a profile not in this map, the
    /// provider throws at launch time — it never silently falls back to
    /// "no enforcement."
    /// </summary>
    public IReadOnlyDictionary<string, string> NetworkProfiles { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// When true, the detached agent-output HTTP-ingest transport (#290) is
    /// disabled and all agent execs fall back to the attached <c>multipass exec</c>
    /// pipe — the pre-#290 behaviour. Repair switch for the merge-phase regression
    /// where the detached transport drops a freshly-created sandbox's agent stdout
    /// and exit code (0 output / exit=null / "no merge commit"); see the
    /// fresh-sandbox readiness fix tracked separately. Trade-off: the attached
    /// exec pump busy-loops a CPU core per run (the cost #290 set out to fix).
    /// </summary>
    public bool DisableAgentOutputHttpIngest { get; init; }

    /// <summary>
    /// When true, the provider bakes a per-profile baseline VM
    /// (<c>{BaselineNamePrefix}{profile}</c>) on first use of each profile
    /// by launching with cloud-init, running the install runcmds, and
    /// stopping. Subsequent sandboxes for that profile use
    /// <c>multipass clone</c> from the baseline (~10s) instead of relaunching
    /// + reinstalling (~5-10 min). The baseline VM stays stopped at rest.
    ///
    /// Operator caveats:
    ///  - The bake needs egress to wherever the install runcmd reaches
    ///    (apt archive, package registries, …). Profiles with a strict
    ///    hostname allowlist that doesn't cover those destinations will
    ///    fail to bake — pick a wider profile, or extend the allowlist
    ///    in scripts/setup-host-networks.sh.
    ///  - If <see cref="ExtraRuncmd"/> changes (e.g. a new tool added),
    ///    delete the baseline VMs (<c>multipass delete --purge {prefix}*</c>)
    ///    so they get re-baked with the new install commands.
    /// </summary>
    public bool UseBaselineImages { get; init; } = false;

    /// <summary>
    /// Prefix for baseline VM names. Default <c>cb-baseline-</c>; final
    /// names are <c>{prefix}{profile}</c> (e.g. <c>cb-baseline-internet-only</c>).
    /// Names are truncated to 24 chars (multipass instance-name limit) with
    /// a hash suffix if the full name would overflow.
    /// </summary>
    public string BaselineNamePrefix { get; init; } = "cb-baseline-";

    /// <summary>
    /// Disk allocation (gibibytes) for the baseline VM. Default 12 GiB.
    /// Multipass's default of 5 GiB is enough for a base Ubuntu cloud
    /// image but tight once a project install adds language toolchains,
    /// agent CLIs, and auditor binaries. qcow2 disks are sparse, so
    /// unused space costs nothing on the host until written. Lower
    /// freely if your install set is small.
    /// </summary>
    public int BaselineDiskGB { get; init; } = 12;

    /// <summary>
    /// Memory (gibibytes) for the baseline VM. Default 16 GiB. Sized to
    /// <see cref="BaselineCpus"/>: MSBuild spawns one worker per core at
    /// ~1 GiB peak each, plus baseline OS / agent / NuGet overhead.
    /// Bumping CPUs without bumping memory will OOM and surface as a CLR
    /// fatal during MSBuild's project enumeration. Long-running agent
    /// sessions also keep their conversation history in memory.
    /// </summary>
    public int BaselineMemoryGB { get; init; } = 16;

    /// <summary>
    /// vCPU count for the baseline VM. Default 6. Multipass's default is 1;
    /// bumping speeds up build / scan / install cold-starts when the
    /// underlying tools parallelise. Keep <see cref="BaselineMemoryGB"/>
    /// at roughly 2-3× this value or builds OOM under MSBuild's per-core
    /// worker fan-out. Total host VM budget is
    /// <c>WorkerPool:MaxConcurrentSandboxes × BaselineCpus</c> vCPUs and
    /// <c>... × BaselineMemoryGB</c> GiB at sandbox saturation.
    /// </summary>
    public int BaselineCpus { get; init; } = 6;

    /// <summary>
    /// Disk-guard configuration. When set, <see cref="MultipassSandboxProvider"/>
    /// checks free space on the configured mounts before every VM launch and
    /// throws <see cref="CodeyBox.Core.SandboxDiskDeferredException"/> when any
    /// mount is below <see cref="MultipassDiskGuardOptions.MinFreeBytes"/>.
    /// Null (default) disables the preflight entirely.
    /// </summary>
    public MultipassDiskGuardOptions? DiskGuard { get; init; }

    /// <summary>
    /// Maximum number of concurrent "heavy" multipass daemon / filesystem
    /// operations. Heavy = lifecycle (launch / start / stop / restart /
    /// suspend / delete / purge), filesystem (mount / umount / transfer /
    /// clone), and config (set). Light operations — <c>multipass exec</c>
    /// (an agent run issues hundreds against an already-booted VM) and
    /// status polls (<c>info</c>, <c>list</c>, <c>version</c>) — are NOT
    /// gated and run at unbounded concurrency: serialising them would
    /// cripple fleet throughput, and the verified contention source on the
    /// snap-confined multipassd is the heavy ops, not exec.
    /// <para>
    /// Originally bounded only launch/start (hence the name); the gate now
    /// also bounds mount / transfer / stop / delete / clone — without this,
    /// audit-phase fan-out has been observed to overwhelm multipassd with
    /// ~90+ concurrent mount calls in a 4-minute window, producing
    /// <c>owner=stat-timeout</c> on the mount filesystem and downstream
    /// <c>git clone /repo /work</c> exit-128 failures.
    /// </para>
    /// <para>
    /// Independent of <c>WorkerPool.MaxConcurrentWorkers</c>: many workers
    /// can be running agent logic inside already-booted VMs while only N
    /// heavy multipass ops execute at once. Hot-reloadable.
    /// </para>
    /// </summary>
    public int MaxConcurrentBoots { get; init; } = DefaultMaxConcurrentBoots;

    /// <summary>
    /// Optional delay applied after acquiring the provisioning gate and
    /// before the actual multipass launch/start. Each holder incurs this
    /// delay individually; up to <see cref="MaxConcurrentBoots"/> holders
    /// can be in the delay phase concurrently. Acquiers beyond
    /// MaxConcurrentBoots are gated behind releasing slots, producing
    /// inter-boot stagger so that CPU/IO spikes don't align. 0 means no
    /// delay.
    /// </summary>
    public TimeSpan BootLaunchDelay { get; init; } = DefaultBootLaunchDelay;

    /// <summary>
    /// Configurable list of package cache seeds to copy from the host to the baseline VM at bake time.
    /// </summary>
    public IReadOnlyList<PackageCacheSeedOptions> PackageCacheSeeds { get; init; } = [];

    /// <summary>
    /// Host-staged executable binaries to ship into the baseline VM at bake time.
    /// Each entry copies one host file to an absolute VM path with mode 0755,
    /// optionally creating symlinks (e.g. into <c>/usr/local/bin</c>) so the
    /// binary lands on the non-login sandbox PATH.
    /// <para>
    /// Use this instead of a <see cref="ExtraRuncmd"/> <c>curl … | bash</c> step
    /// when the upstream installer is not durable (URL or shape may drift) or
    /// when the operator already has a vetted binary on disk and wants the bake
    /// to fail loudly if the file is missing rather than silently proceed.
    /// </para>
    /// </summary>
    public IReadOnlyList<ExecutableProvisionOptions> ExecutableProvisions { get; init; } = [];
}

/// <summary>
/// Configuration for a package cache seed to be copied into the baseline VM.
/// </summary>
public sealed record PackageCacheSeedOptions
{
    public string HostSourcePath { get; init; } = string.Empty;
    public string VmDestPath { get; init; } = string.Empty;
    public double? MaxSizeMB { get; init; }
}

/// <summary>
/// Configuration for a host-staged executable binary that the baseline bake
/// must ship into the VM at a known absolute path with mode 0755.
/// <para>
/// Provisioning uses <c>multipass transfer</c> for the bytes followed by an
/// in-VM <c>install -m 0755 -o root -g root</c>, so the executable bit and
/// file ownership are set deterministically — neither <c>File.Copy</c> on the
/// host staging side nor <c>multipass transfer</c> documents preservation of
/// the executable bit, and depending on it has produced silent breakage.
/// </para>
/// </summary>
public sealed record ExecutableProvisionOptions
{
    /// <summary>Host path to the executable file. Tilde-expansion is honoured.</summary>
    public string HostSourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Absolute VM path where the executable must land (e.g.
    /// <c>/home/ubuntu/.local/bin/agy</c>). The parent directory is created if it
    /// does not already exist; parents below <c>/home/ubuntu</c> are kept owned by
    /// the unprivileged <c>ubuntu</c> user even though the executable file itself
    /// is root-owned.
    /// </summary>
    public string VmDestPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional absolute VM paths at which to create symlinks pointing to
    /// <see cref="VmDestPath"/>. Use to put a binary installed under the
    /// non-login user's home onto a system PATH (e.g.
    /// <c>/usr/local/bin/agy</c>).
    /// </summary>
    public IReadOnlyList<string> VmSymlinks { get; init; } = [];

    /// <summary>
    /// Diagnostic label used in log lines and bake-failure messages. Defaults to
    /// the destination filename when unset.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>
/// Disk-guard preflight tunables. The provider checks free bytes on
/// <see cref="MultipassDataPath"/> (Multipass's storage backing) and on
/// any additional paths registered via <see cref="AdditionalPaths"/>;
/// any single mount below <see cref="MinFreeBytes"/> causes the launch
/// to defer rather than proceed.
/// </summary>
public sealed record MultipassDiskGuardOptions
{
    /// <summary>
    /// Minimum free bytes on each monitored mount. Defaults to 10 GiB —
    /// roughly enough headroom that a single sandbox launch followed by a
    /// modest agent run won't push the host into a "no space left on device"
    /// state, but small enough that operators don't get surprise deferrals
    /// on healthy hosts. Tune up if your work items routinely write large
    /// artifacts.
    /// </summary>
    public long MinFreeBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Path under which Multipass stores its VM images. On the snap install
    /// this is <c>/var/snap/multipass/common/data</c>; non-snap installs
    /// use a different layout and operators should override here.
    /// </summary>
    public string MultipassDataPath { get; init; } = "/var/snap/multipass/common/data";

    /// <summary>
    /// Extra paths to check before each launch — typically the state
    /// database directory so we refuse to launch new work when the SQLite
    /// volume is about to fill. Each entry is checked independently against
    /// <see cref="MinFreeBytes"/>; the first one to fail is the one
    /// reported in the deferral reason.
    /// </summary>
    public IReadOnlyList<string> AdditionalPaths { get; init; } = [];

    /// <summary>
    /// How long to wait before re-attempting pickup of a deferred item.
    /// Defaults to 5 minutes — short enough that a transient cleanup
    /// elsewhere on the host (a deploy log rotation, an audit-report
    /// retention sweep) lets work resume promptly, long enough that we
    /// don't stampede the dispatch loop while the host is still full.
    /// </summary>
    public TimeSpan RecheckIn { get; init; } = TimeSpan.FromMinutes(5);
}

internal sealed class MultipassSandbox : IPreemptibleSandbox, IPreserveOnDisposeSandbox, ISuspendableSandbox, IShutdownTeardownSandbox
{
    internal const int ArgvBytesWarningThreshold = 64 * 1024;
    internal const int MaxScreenshotPngBytes = 64 * 1024 * 1024;
    internal const int MaxScreenshotBase64StdoutBytes = ((MaxScreenshotPngBytes + 2) / 3 * 4) + 4096;
    internal const int MaxScreenshotStderrBytes = 64 * 1024;
    internal const int AgentOutputHttpSetupFailedExitCode = 86;
    private const int DetachedProcessGroupMalformedExitCode = 73;
    private const int DetachedSupervisorSetupFailedExitCode = 88;
    private const int DetachedPollFailureLimit = 5;
    private const string DetachedSupervisorDirectory = "/run/codeybox-exec";
    internal const string AgentOutputHttpSetupFailureMarker =
        "codeybox-exec: agent output HTTP ingest unavailable before launch";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal delegate Task<MultipassAgentOutputHttpIngestSession?> AgentOutputHttpIngestSessionStarter(
        System.Net.IPAddress bindAddress,
        string runId,
        ILogger log,
        Action<string>? stdoutChunkCallback,
        Action<string>? stderrChunkCallback,
        CancellationToken ct);

    private readonly string _name;
    private readonly string _sandboxRoot;
    private readonly SandboxSpec _spec;
    private readonly MultipassSandboxOptions _opts;
    private readonly ILogger _log;
    private readonly IProcessRunner _runner;
    private readonly MultipassDaemonRetryPolicy _daemonRetryPolicy;
    private readonly ITimingStore? _timings;
    private readonly WorkItemId _timingItemId;
    private readonly WorkItemId? _workItemId;
    private readonly string _timingPhase;
    private readonly Action<string>? _onDisposed;
    private readonly Action<string>? _onNoLongerTrackedActive;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IDisposable>>? _opGateAcquirer;
    private readonly AgentOutputHttpIngestSessionStarter _agentOutputIngestSessionStarter;
    private readonly int _maxScreenshotPngBytes;
    private readonly int _maxScreenshotBase64StdoutBytes;
    private readonly int _maxScreenshotStderrBytes;
    private int _firstExecEmitted;
    private bool _disposed;
    private bool _preserveOnDispose;
    private bool _isSuspended;
    private bool _ownedByShutdownHandler;
    private long _activeProgressVersion;
    private string _activeProgressStatus = "active";

    /// <summary>
    /// True once <see cref="SuspendAsync"/> has frozen this VM's RAM via
    /// <c>multipass suspend</c>. PipelineRunner reads this in its host-shutdown
    /// OCE catch block to short-circuit the preempt-checkpoint flow (whose
    /// in-VM <c>git add/commit/push</c> would hang against a frozen VM and
    /// stall the orchestrator's exit).
    /// </summary>
    public bool IsSuspended => _isSuspended;

    /// <summary>
    /// True once the shutdown teardown handler has taken responsibility for
    /// this VM's teardown. Set implicitly when <see cref="SuspendAsync"/> flips
    /// <see cref="IsSuspended"/>; set explicitly by
    /// <see cref="MarkOwnedByShutdownHandler"/> for teardown modes whose
    /// recovery path does not go through SuspendAsync but still cannot run the
    /// in-VM checkpoint flow after the lifecycle service has stopped or deleted
    /// the VM.
    /// </summary>
    public bool IsOwnedByShutdownHandler => _isSuspended || _ownedByShutdownHandler;

    /// <summary>
    /// Called by <c>SandboxShutdownTeardownService</c> when non-suspend
    /// teardown becomes authoritative: after successful Stop/preserve, or
    /// before destructive Dispose. Idempotent; safe to call multiple times.
    /// </summary>
    public void MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    /// <summary>
    /// RAM size this VM was provisioned with, surfaced so the shutdown teardown
    /// service can scale its per-VM timeout: a larger VM has more RAM to flush to
    /// disk on <c>multipass suspend</c>.
    /// </summary>
    public long? MemoryBytes => _spec.Limits.MemoryBytes;

    public MultipassSandbox(string name, string sandboxRoot, SandboxSpec spec, MultipassSandboxOptions opts, ILogger log,
        ITimingStore? timings = null, WorkItemId timingItemId = default, string timingPhase = "work",
        Action<string>? onDisposed = null, Action<string>? onNoLongerTrackedActive = null, IProcessRunner? runner = null,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null,
        int? maxScreenshotPngBytes = null, int? maxScreenshotStderrBytes = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<IDisposable>>? opGateAcquirer = null,
        AgentOutputHttpIngestSessionStarter? agentOutputIngestSessionStarter = null)
    {
        _name = name;
        _sandboxRoot = sandboxRoot;
        _spec = spec;
        _opts = opts;
        _log = log;
        _runner = runner ?? new DefaultProcessRunner();
        _daemonRetryPolicy = daemonRetryPolicy ?? MultipassDaemonRetryPolicy.Default;
        _timings = timings;
        _timingItemId = timingItemId;
        _workItemId = timingItemId.Value == Guid.Empty ? null : timingItemId;
        _timingPhase = timingPhase;
        _onDisposed = onDisposed;
        _onNoLongerTrackedActive = onNoLongerTrackedActive;
        // When the provider creates this sandbox it wires opGateAcquirer to
        // the shared heavy-op semaphore. Tests that instantiate
        // MultipassSandbox directly typically leave it null — every multipass
        // call then runs ungated (existing behaviour preserved).
        _opGateAcquirer = opGateAcquirer;
        _agentOutputIngestSessionStarter = agentOutputIngestSessionStarter
            ?? MultipassAgentOutputHttpIngestSession.TryStartAsync;
        _maxScreenshotPngBytes = maxScreenshotPngBytes ?? MaxScreenshotPngBytes;
        _maxScreenshotStderrBytes = maxScreenshotStderrBytes ?? MaxScreenshotStderrBytes;
        if (_maxScreenshotPngBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxScreenshotPngBytes), "Screenshot PNG limit must be positive.");
        if (_maxScreenshotStderrBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxScreenshotStderrBytes), "Screenshot stderr limit must be positive.");
        _maxScreenshotBase64StdoutBytes = ((_maxScreenshotPngBytes + 2) / 3 * 4) + 4096;
        Id = name;
    }

    public string Id { get; }

    internal ActiveSandboxProgress SnapshotActiveProgress(WorkItemId workItemId)
    {
        var version = Volatile.Read(ref _activeProgressVersion);
        var status = Volatile.Read(ref _activeProgressStatus);
        return new ActiveSandboxProgress(
            workItemId,
            Id,
            version == 0
                ? status
                : status + ":" + version.ToString(CultureInfo.InvariantCulture));
    }

    public SandboxAgentOutputTransportKind AgentOutputTransportKind =>
        TryResolveAgentOutputBindAddress() is null
            ? SandboxAgentOutputTransportKind.ExecPipe
            : SandboxAgentOutputTransportKind.HttpIngest;
    public SandboxBatchLaunchMode BatchLaunchMode =>
        TryResolveAgentOutputBindAddress() is null
            ? SandboxBatchLaunchMode.Attached
            : SandboxBatchLaunchMode.Detached;

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        var result = await ExecRunAsync(
            exec,
            ct,
            exec.MaxStdoutBytes,
            exec.MaxStderrBytes);
        return new SandboxExecResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.StdoutLimitExceeded,
            result.StderrLimitExceeded,
            result.StartFailed || result.ExecutionUnavailable);
    }

    private async Task<ProcessRunResult> ExecRunAsync(
        SandboxExec exec,
        CancellationToken ct,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (exec.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(exec));

        var transferredVmPaths = new List<string>();
        MultipassAgentOutputHttpIngestSession? agentOutputIngest = null;
        var stdoutChunkCallback = exec.StdoutChunkCallback;
        var stderrChunkCallback = exec.StderrChunkCallback;
        var forceEnvironmentFile = false;
        var detachedHttpIngest = false;
        string? detachedExitToken = null;
        string? detachedProcessGroupMarker = null;
        string? detachedStdinFile = null;
        string? multipassStdin = exec.Stdin;
        var effectiveEnvironment = BuildEffectiveExecEnvironment(exec);
        var pipeEnvironment = effectiveEnvironment;
        var prefersDetachedHttpIngest = ShouldDetachAgentOutputHttpIngest(exec)
            && maxStdoutBytes is null
            && maxStderrBytes is null;
        if (ShouldUseAgentOutputHttpIngest(exec, maxStdoutBytes, maxStderrBytes))
        {
            if (TryResolveAgentOutputBindAddress() is not { } bindAddress)
            {
                if (prefersDetachedHttpIngest)
                {
                    return BuildAgentOutputHttpSetupFailureResult(
                        "agent output HTTP ingest has no bind address for detached launch");
                }
            }
            else
            {
                agentOutputIngest = await _agentOutputIngestSessionStarter(
                    bindAddress,
                    Guid.NewGuid().ToString("N"),
                    _log,
                    exec.StdoutChunkCallback,
                    exec.StderrChunkCallback,
                    ct).ConfigureAwait(false);
                if (agentOutputIngest is not null)
                {
                    detachedHttpIngest = ShouldDetachAgentOutputHttpIngest(exec);
                    effectiveEnvironment = MergeExecEnvironment(
                        RemoveAgentOutputExitToken(effectiveEnvironment),
                        agentOutputIngest.BuildEnvironment(includeExitToken: false));
                    if (detachedHttpIngest)
                    {
                        detachedExitToken = agentOutputIngest.ExitToken;
                    }
                    stdoutChunkCallback = null;
                    stderrChunkCallback = null;
                    forceEnvironmentFile = true;
                    if (detachedHttpIngest)
                    {
                        // Keep detached completion state outside the ubuntu
                        // user's writable home. The generated launch script
                        // writes this marker and its exit sidecar through
                        // sudo from the detached supervisor process; the
                        // wrapper/agent never receive the path.
                        detachedProcessGroupMarker = $"{DetachedSupervisorDirectory}/detached-{Guid.NewGuid():N}.pgid";
                    }
                }
                else if (prefersDetachedHttpIngest)
                {
                    return BuildAgentOutputHttpSetupFailureResult(
                        "agent output HTTP ingest listener could not start for detached launch");
                }
            }
        }

        List<string> wrapped;
        IReadOnlyList<string> argv;
        if (forceEnvironmentFile && effectiveEnvironment is { Count: > 0 })
        {
            try
            {
                var envFile = await TransferExecEnvironmentAsync(effectiveEnvironment, ct);
                transferredVmPaths.Add(envFile);
                if (detachedHttpIngest)
                {
                    detachedStdinFile = exec.Stdin is null
                        ? null
                        : detachedProcessGroupMarker + ".stdin";
                }
                wrapped = BuildWrappedInvocation(exec, effectiveEnvironment, envFile, stdinFile: null);
                argv = detachedHttpIngest
                    ? await BuildDetachedMultipassExecArgvAsync(
                            wrapped,
                            envFile,
                            detachedProcessGroupMarker!,
                            detachedStdinFile,
                            transferredVmPaths,
                            ct)
                        .ConfigureAwait(false)
                    : BuildMultipassExecArgv(wrapped);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (detachedHttpIngest)
                {
                    _log.LogWarning(ex,
                        "Detached agent output HTTP ingest setup failed before launch for {Name}",
                        _name);
                    if (agentOutputIngest is not null)
                        await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
                    if (transferredVmPaths.Count > 0)
                        await TryRemoveTransferredFilesAsync(transferredVmPaths).ConfigureAwait(false);
                    return BuildAgentOutputHttpSetupFailureResult(ex.Message);
                }

                _log.LogWarning(ex,
                    "Agent output HTTP ingest setup failed before launch for {Name}; falling back to multipass exec pipe",
                    _name);
                if (agentOutputIngest is not null)
                    await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
                agentOutputIngest = null;
                stdoutChunkCallback = exec.StdoutChunkCallback;
                stderrChunkCallback = exec.StderrChunkCallback;
                effectiveEnvironment = pipeEnvironment;
                forceEnvironmentFile = false;
                detachedHttpIngest = false;
                detachedExitToken = null;
                detachedProcessGroupMarker = null;
                detachedStdinFile = null;
                multipassStdin = exec.Stdin;
                wrapped = BuildWrappedInvocation(exec, effectiveEnvironment, extraEnvFile: null, stdinFile: null);
                argv = BuildMultipassExecArgv(wrapped);
            }
        }
        else
        {
            wrapped = BuildWrappedInvocation(exec, effectiveEnvironment, extraEnvFile: null, stdinFile: null);
            argv = BuildMultipassExecArgv(wrapped);
        }

        if (detachedHttpIngest)
            multipassStdin = BuildDetachedLaunchStdin(detachedExitToken ?? "", exec.Stdin);

        var argvBytes = EstimateArgvBytes(argv);
        if (!forceEnvironmentFile && argvBytes > ArgvBytesWarningThreshold)
        {
            _log.LogWarning(
                "Multipass exec argv for {Name} is {Bytes} bytes; routing through transferred files to avoid ARG_MAX",
                _name, argvBytes);

            if (effectiveEnvironment is { Count: > 0 })
            {
                var envFile = await TransferExecEnvironmentAsync(effectiveEnvironment, ct);
                transferredVmPaths.Add(envFile);
                wrapped = BuildWrappedInvocation(exec, effectiveEnvironment, envFile, stdinFile: null);
                argv = BuildMultipassExecArgv(wrapped);
            }
        }

        if (!detachedHttpIngest && EstimateArgvBytes(argv) > ArgvBytesWarningThreshold)
        {
            var script = await TransferExecScriptAsync(wrapped, ct);
            transferredVmPaths.Add(script);
            argv = [_opts.MultipassBinary, "exec", _name, "--", "/bin/sh", script];
        }

        var isFirstExec = Interlocked.CompareExchange(ref _firstExecEmitted, 1, 0) == 0;
        TimingScope? firstExecScope = isFirstExec
            ? await TimingScope.BeginAsync(_timings, _timingItemId, _timingPhase, "vm.exec_first", log: _log)
            : null;
        try
        {
            var result = await MultipassRetry.RunWithRetryAsync(
                action: innerCt => RunMultipassAsync(
                    argv,
                    multipassStdin,
                    innerCt,
                    stdoutChunkCallback,
                    stderrChunkCallback,
                    maxStdoutBytes,
                    maxStderrBytes,
                    exec.KillOnOutputLimit),
                log: _log,
                description: $"exec on {_name}",
                ct: ct).ConfigureAwait(false);

            if (agentOutputIngest is null)
                return result;

            if (IsAgentOutputHttpSetupFailure(result, agentOutputIngest))
            {
                if (detachedHttpIngest)
                {
                    _log.LogWarning(
                        "Detached agent output HTTP ingest was unreachable from sandbox {Name}; refusing attached fallback",
                        _name);
                    await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
                    agentOutputIngest = null;
                    return result;
                }

                _log.LogWarning(
                    "Agent output HTTP ingest was unreachable from sandbox {Name}; falling back to multipass exec pipe",
                    _name);
                await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
                agentOutputIngest = null;
                return await ExecRunAsync(
                    exec with { AgentOutputTransport = SandboxAgentOutputTransportPreference.ExecPipe },
                    ct,
                    maxStdoutBytes,
                    maxStderrBytes).ConfigureAwait(false);
            }

            if (detachedHttpIngest)
            {
                if (result.ExitCode != 0)
                {
                    await TryTerminateDetachedProcessGroupAsync(detachedProcessGroupMarker!).ConfigureAwait(false);
                    return result;
                }

                var detachedExit = await WaitForDetachedCompletionAsync(detachedProcessGroupMarker!, agentOutputIngest, ct)
                    .ConfigureAwait(false);
                var detachedOutput = await ReadDetachedOutputSidecarsAsync(detachedProcessGroupMarker!, ct)
                    .ConfigureAwait(false);
                await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
                var detachedIngested = agentOutputIngest;
                agentOutputIngest = null;
                var detachedExitCode = detachedExit.Error is not null && detachedExit.ExitCode == 0
                    ? 1
                    : detachedExit.ExitCode;
                return result with
                {
                    ExitCode = detachedExitCode,
                    Stdout = detachedOutput.Stdout + detachedIngested.Stdout + result.Stdout,
                    Stderr = detachedOutput.Stderr
                             + detachedIngested.Stderr
                             + AppendDetachedMarkerError(result.Stderr, detachedExit.Error)
                             + detachedOutput.Error,
                };
            }

            await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
            var ingested = agentOutputIngest;
            agentOutputIngest = null;
            return result with
            {
                Stdout = ingested.Stdout + result.Stdout,
                Stderr = ingested.Stderr + result.Stderr,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (detachedHttpIngest && detachedProcessGroupMarker is not null)
                await TryTerminateDetachedProcessGroupAsync(detachedProcessGroupMarker).ConfigureAwait(false);
            throw;
        }
        catch (Exception) when (detachedHttpIngest && detachedProcessGroupMarker is not null)
        {
            await TryTerminateDetachedProcessGroupAsync(detachedProcessGroupMarker).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (agentOutputIngest is not null)
                await agentOutputIngest.DisposeAsync().ConfigureAwait(false);
            if (transferredVmPaths.Count > 0)
                await TryRemoveTransferredFilesAsync(transferredVmPaths);
            if (detachedHttpIngest && detachedProcessGroupMarker is not null)
                await TryRemoveDetachedSupervisorFilesAsync(detachedProcessGroupMarker).ConfigureAwait(false);
            if (firstExecScope is not null)
                await firstExecScope.DisposeAsync();
        }
    }

    public async Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
    {
        EnsureGraphical();
        var result = await ExecRunAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-lc",
                "tmp=$(mktemp --suffix=.png); trap 'rm -f \"$tmp\"' EXIT; DISPLAY=:0 scrot -z \"$tmp\"; base64 -w0 \"$tmp\"",
            ],
            WorkingDirectory = _spec.WorkingDirectory,
        }, ct, maxStdoutBytes: _maxScreenshotBase64StdoutBytes, maxStderrBytes: _maxScreenshotStderrBytes);

        if (result.StdoutLimitExceeded)
            throw new InvalidOperationException("graphical screenshot output exceeded the maximum capture size");
        if (result.StderrLimitExceeded)
            throw new InvalidOperationException("graphical screenshot stderr exceeded the maximum capture size");
        if (!result.Success)
            throw new InvalidOperationException($"graphical screenshot failed: {result.Stderr}");

        try
        {
            var screenshot = Convert.FromBase64String(result.Stdout.Trim());
            if (screenshot.Length > _maxScreenshotPngBytes)
                throw new InvalidOperationException("graphical screenshot PNG exceeded the maximum capture size");
            if (!HasPngSignature(screenshot))
                throw new InvalidOperationException("graphical screenshot command returned non-PNG data");
            return screenshot;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("graphical screenshot command returned invalid base64", ex);
        }
    }

    private static bool HasPngSignature(byte[] bytes)
        => bytes.Length >= PngSignature.Length
            && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    public async Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
    {
        EnsureGraphical();
        SandboxInputEventValidation.Validate(events);
        foreach (var inputEvent in events)
        {
            var argv = BuildXdotoolArgv(inputEvent);

            var result = await ExecAsync(new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = _spec.WorkingDirectory,
            }, ct);
            if (!result.Success)
                throw new InvalidOperationException(
                    $"graphical input event '{inputEvent.Type}' failed: {result.Stderr}");
        }
    }

    private void EnsureGraphical()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_spec.Flavor != SandboxProfileFlavor.Graphical)
            throw new NotSupportedException("This Multipass sandbox was not created with the graphical flavor.");
    }

    private static IReadOnlyList<string> BuildXdotoolArgv(SandboxInputEvent inputEvent)
    {
        var argv = new List<string>
        {
            "xdotool",
        };

        switch (inputEvent.Type)
        {
            case SandboxInputEventType.Click:
                if (inputEvent.X is { } clickX && inputEvent.Y is { } clickY)
                    argv.AddRange(["mousemove", "--sync", clickX.ToString(), clickY.ToString()]);
                argv.AddRange(["click", "1"]);
                return argv;

            case SandboxInputEventType.Key:
                argv.AddRange(["key", "--clearmodifiers", inputEvent.Key!]);
                return argv;

            case SandboxInputEventType.Move:
                var moveX = inputEvent.X!.Value;
                var moveY = inputEvent.Y!.Value;
                argv.AddRange(["mousemove", "--sync", moveX.ToString(), moveY.ToString()]);
                return argv;

            case SandboxInputEventType.Scroll:
                return BuildScrollArgv(argv, inputEvent);

            case SandboxInputEventType.Type:
                argv.AddRange(["type", "--clearmodifiers", "--delay", "0", "--", inputEvent.Text!]);
                return argv;

            default:
                throw new ArgumentOutOfRangeException(nameof(inputEvent), inputEvent.Type, "Unknown input event type.");
        }
    }

    private static IReadOnlyList<string> BuildScrollArgv(List<string> argv, SandboxInputEvent inputEvent)
    {
        var vertical = inputEvent.Y ?? 0;
        var horizontal = inputEvent.X ?? 0;
        var amount = Math.Abs((long)(vertical != 0 ? vertical : horizontal));
        var button = vertical switch
        {
            < 0 => "4",
            > 0 => "5",
            _ => horizontal < 0 ? "6" : "7",
        };
        argv.AddRange(["click", "--repeat", amount.ToString(), button]);
        return argv;
    }

    private IReadOnlyDictionary<string, string>? BuildEffectiveExecEnvironment(SandboxExec exec)
    {
        if (_spec.Flavor != SandboxProfileFlavor.Graphical)
            return exec.ExtraEnvironment;

        if (exec.ExtraEnvironment is null || exec.ExtraEnvironment.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DISPLAY"] = SandboxConventions.GraphicalDisplay,
            };
        }

        if (exec.ExtraEnvironment.ContainsKey("DISPLAY"))
            return exec.ExtraEnvironment;

        var merged = new Dictionary<string, string>(exec.ExtraEnvironment, StringComparer.Ordinal)
        {
            ["DISPLAY"] = SandboxConventions.GraphicalDisplay,
        };
        return merged;
    }

    private bool ShouldUseAgentOutputHttpIngest(
        SandboxExec exec,
        int? maxStdoutBytes,
        int? maxStderrBytes)
        => exec.AgentOutputTransport == SandboxAgentOutputTransportPreference.PreferHttpIngest
            && maxStdoutBytes is null
            && maxStderrBytes is null;

    private static bool ShouldDetachAgentOutputHttpIngest(SandboxExec exec)
        => exec.AgentOutputTransport == SandboxAgentOutputTransportPreference.PreferHttpIngest
            && exec.LaunchMode == SandboxExecLaunchMode.DetachedBatch;

    private void MarkActiveSandboxProgress(string status)
    {
        Volatile.Write(ref _activeProgressStatus, status);
        Interlocked.Increment(ref _activeProgressVersion);
    }

    private System.Net.IPAddress? TryResolveAgentOutputBindAddress()
    {
        // Repair switch: null bind address forces ExecPipe/Attached transport
        // (pre-#290), bypassing the detached HTTP-ingest path that drops a fresh
        // merge sandbox's agent output + exit code.
        if (_opts.DisableAgentOutputHttpIngest)
            return null;
        if (string.IsNullOrWhiteSpace(_spec.Network.ProfileName))
            return null;
        if (!_opts.NetworkProfiles.TryGetValue(_spec.Network.ProfileName, out var bridge))
            return null;
        return MultipassAgentOutputHttpIngestSession.TryResolveBridgeAddress(bridge);
    }

    private static IReadOnlyDictionary<string, string> MergeExecEnvironment(
        IReadOnlyDictionary<string, string>? environment,
        IReadOnlyDictionary<string, string> additional)
    {
        var merged = environment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(environment, StringComparer.Ordinal);
        foreach (var (key, value) in additional)
            merged[key] = value;
        return merged;
    }

    private static IReadOnlyDictionary<string, string>? RemoveAgentOutputExitToken(
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null
            || !environment.ContainsKey(MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable))
        {
            return environment;
        }

        var scrubbed = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        scrubbed.Remove(MultipassAgentOutputHttpIngestSession.ExitTokenEnvironmentVariable);
        return scrubbed;
    }

    private static bool IsAgentOutputHttpSetupFailure(
        ProcessRunResult result,
        MultipassAgentOutputHttpIngestSession ingest)
        => result.ExitCode == AgentOutputHttpSetupFailedExitCode
            && !ingest.ReceivedAgentBytes
            && result.Stderr.Contains(AgentOutputHttpSetupFailureMarker, StringComparison.Ordinal);

    private List<string> BuildWrappedInvocation(
        SandboxExec exec,
        IReadOnlyDictionary<string, string>? effectiveEnvironment,
        string? extraEnvFile,
        string? stdinFile)
    {
        // The codeybox-exec wrapper closes stdin by default to prevent
        // tools that read stdin from hanging the sandbox. When the
        // orchestrator deliberately pipes stdin, pass --keep-stdin first.
        // Detached batch invocations still use --keep-stdin. The short launch
        // exec receives the payload over its pipe and the detached supervisor
        // bridges a root-owned sidecar into the wrapper's stdin.
        var wrapped = new List<string> { "/usr/local/bin/codeybox-exec" };
        if (stdinFile is not null)
            wrapped.AddRange(["--stdin-file", stdinFile]);
        else if (exec.Stdin is not null)
            wrapped.Add("--keep-stdin");
        wrapped.Add(exec.WorkingDirectory ?? _spec.WorkingDirectory);
        if (extraEnvFile is not null)
        {
            wrapped.AddRange(["--env-file", extraEnvFile]);
        }
        else if (effectiveEnvironment is { Count: > 0 })
        {
            // env(1) takes KEY=VALUE pairs followed by the command. This
            // keeps the common case small and preserves historical ordering.
            wrapped.Add("env");
            foreach (var (k, v) in effectiveEnvironment)
                wrapped.Add($"{k}={v}");
        }
        wrapped.AddRange(exec.Argv);
        return wrapped;
    }

    private List<string> BuildMultipassExecArgv(IReadOnlyList<string> wrapped) =>
        [_opts.MultipassBinary, "exec", _name, "--", .. wrapped];

    internal static int EstimateArgvBytes(IReadOnlyList<string> argv)
    {
        var total = 0;
        foreach (var arg in argv)
            total += Encoding.UTF8.GetByteCount(arg) + 1;
        return total;
    }

    private async Task<string> TransferExecEnvironmentAsync(IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var fileName = $"env-{Guid.NewGuid():N}";
        var hostDir = Path.Combine(_sandboxRoot, "exec-env");
        Directory.CreateDirectory(hostDir);
        MultipassSandboxProvider.TryChmod0700(hostDir);
        var hostPath = Path.Combine(hostDir, fileName);
        await File.WriteAllTextAsync(hostPath, MultipassSandboxProvider.BuildEnvironmentFileContent(env), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hostPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        const string vmDir = "/home/ubuntu/.codeybox-exec-env";
        await RunVmCommandAsync(["mkdir", "-p", vmDir], ct);
        await TransferFileToVmAsync(hostPath, $".codeybox-exec-env/{fileName}", "multipass transfer exec env file", ct);
        var vmPath = $"{vmDir}/{fileName}";
        await RunVmCommandAsync(["chmod", "0600", vmPath], ct);
        return vmPath;
    }

    private async Task<string> TransferExecScriptAsync(IReadOnlyList<string> wrapped, CancellationToken ct)
    {
        var fileName = $"exec-{Guid.NewGuid():N}.sh";
        var hostDir = Path.Combine(_sandboxRoot, "exec-scripts");
        Directory.CreateDirectory(hostDir);
        MultipassSandboxProvider.TryChmod0700(hostDir);
        var hostPath = Path.Combine(hostDir, fileName);
        await File.WriteAllTextAsync(hostPath, BuildExecScript(wrapped), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hostPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        const string vmDir = "/home/ubuntu/.codeybox-exec";
        await RunVmCommandAsync(["mkdir", "-p", vmDir], ct);
        await TransferFileToVmAsync(hostPath, $".codeybox-exec/{fileName}", "multipass transfer exec script", ct);
        var vmPath = $"{vmDir}/{fileName}";
        await RunVmCommandAsync(["chmod", "0700", vmPath], ct);
        return vmPath;
    }

    private async Task<IReadOnlyList<string>> BuildDetachedMultipassExecArgvAsync(
        IReadOnlyList<string> wrapped,
        string envFile,
        string processGroupMarker,
        string? stdinFile,
        List<string> transferredVmPaths,
        CancellationToken ct)
    {
        var commandScript = await TransferExecScriptAsync(wrapped, ct).ConfigureAwait(false);
        transferredVmPaths.Add(commandScript);
        var exitTokenFile = processGroupMarker + ".exit-token";
        var launchScript = await TransferDetachedLaunchScriptAsync(
            envFile,
            processGroupMarker,
            exitTokenFile,
            stdinFile,
            ["/bin/sh", commandScript],
            ct).ConfigureAwait(false);
        transferredVmPaths.Add(launchScript);
        return [_opts.MultipassBinary, "exec", _name, "--", "/bin/bash", launchScript];
    }

    private static string BuildDetachedLaunchStdin(string exitToken, string? agentStdin)
        => exitToken + "\n" + (agentStdin ?? "");

    private async Task<string> TransferDetachedLaunchScriptAsync(
        string envFile,
        string processGroupMarker,
        string exitTokenFile,
        string? stdinFile,
        IReadOnlyList<string> command,
        CancellationToken ct)
    {
        var fileName = $"detached-{Guid.NewGuid():N}.sh";
        var hostDir = Path.Combine(_sandboxRoot, "exec-scripts");
        Directory.CreateDirectory(hostDir);
        MultipassSandboxProvider.TryChmod0700(hostDir);
        var hostPath = Path.Combine(hostDir, fileName);
        await File.WriteAllTextAsync(hostPath, BuildDetachedLaunchScript(envFile, processGroupMarker, stdinFile, command, exitTokenFile: exitTokenFile), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hostPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        const string vmDir = "/home/ubuntu/.codeybox-exec";
        await RunVmCommandAsync(["mkdir", "-p", vmDir], ct);
        await TransferFileToVmAsync(hostPath, $".codeybox-exec/{fileName}", "multipass transfer detached exec script", ct);
        var vmPath = $"{vmDir}/{fileName}";
        await RunVmCommandAsync(["chmod", "0700", vmPath], ct);
        return vmPath;
    }

    internal static string BuildExecScript(IReadOnlyList<string> wrapped)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\nexec");
        foreach (var arg in wrapped)
            sb.Append(' ').Append(MultipassSandboxProvider.ShellSingleQuote(arg));
        sb.Append('\n');
        return sb.ToString();
    }

    internal static string BuildDetachedLaunchScript(
        string envFile,
        string processGroupMarker,
        string? stdinFile,
        IReadOnlyList<string> command,
        int launchLockAttempts = 300,
        string? exitTokenFile = null,
        int markerWaitSeconds = 30)
    {
        if (launchLockAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(launchLockAttempts), "Launch lock attempts must be non-negative.");
        if (markerWaitSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(markerWaitSeconds), "Marker wait seconds must be positive.");

        const string detachedHttpExitPosterScript = """
import os, sys, time, urllib.error, urllib.parse, urllib.request
base = os.environ.get('CODEYBOX_AGENT_OUTPUT_URL', '').rstrip('/')
run_id = os.environ.get('CODEYBOX_AGENT_OUTPUT_RUN_ID', '')
token = sys.stdin.buffer.readline().decode('utf-8').rstrip('\n')
data = sys.stdin.buffer.read()
if not base or not run_id or not token or not data:
    sys.exit(0)
url = base + '/' + urllib.parse.quote(run_id, safe='') + '/exit/0'
deadline = time.monotonic() + 300.0
attempt = 0
while True:
    req = urllib.request.Request(
        url,
        data=data,
        method='POST',
        headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'text/plain; charset=utf-8'})
    try:
        with urllib.request.urlopen(req, timeout=10.0) as resp:
            code = resp.getcode()
            if 200 <= code < 300:
                sys.exit(0)
            if code in (401, 403, 409, 413):
                sys.exit(70)
    except urllib.error.HTTPError as exc:
        if exc.code in (401, 403, 409, 413):
            sys.exit(70)
    except Exception:
        pass
    if time.monotonic() >= deadline:
        sys.exit(75)
    time.sleep(min(5.0, 0.1 * (2 ** min(attempt, 5))))
    attempt += 1
""";

        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.Append("codeybox_env_file=").Append(MultipassSandboxProvider.ShellSingleQuote(envFile)).Append('\n');
        sb.Append("codeybox_pgid_marker=").Append(MultipassSandboxProvider.ShellSingleQuote(processGroupMarker)).Append('\n');
        sb.Append("codeybox_stdin_file=").Append(MultipassSandboxProvider.ShellSingleQuote(stdinFile ?? "")).Append('\n');
        sb.Append("codeybox_exit_token_file=").Append(MultipassSandboxProvider.ShellSingleQuote(exitTokenFile ?? "")).Append('\n');
        sb.Append("codeybox_lock_max=").Append(launchLockAttempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("codeybox_marker_wait_seconds=").Append(markerWaitSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.AppendLine("codeybox_supervisor_dir=$(dirname \"$codeybox_pgid_marker\")");
        sb.AppendLine("codeybox_lock_dir=\"$codeybox_pgid_marker.lock\"");
        sb.AppendLine("codeybox_root_sh() {");
        sb.AppendLine("    local codeybox_script=\"$1\"");
        sb.AppendLine("    shift");
        sb.AppendLine("    sudo -n sh -c \"$codeybox_script\" codeybox-root-sh \"$@\"");
        sb.AppendLine("}");
        sb.AppendLine("codeybox_release_lock() {");
        sb.AppendLine("    codeybox_root_sh 'rmdir \"$1\" 2>/dev/null || true' \"$codeybox_lock_dir\" || true");
        sb.AppendLine("}");
        sb.AppendLine("codeybox_drain_stdin() {");
        sb.AppendLine("    cat >/dev/null 2>/dev/null || true");
        sb.AppendLine("}");
        sb.AppendLine("if ! codeybox_root_sh 'mkdir -p \"$1\" && { chown root:root \"$1\" 2>/dev/null || true; } && chmod 0700 \"$1\"' \"$codeybox_supervisor_dir\"; then");
        sb.AppendLine("    echo \"codeybox-detached: failed to prepare supervisor directory\" >&2");
        sb.AppendLine("    codeybox_drain_stdin");
        sb.AppendLine($"    exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("fi");
        sb.AppendLine("if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then codeybox_drain_stdin; exit 0; fi");
        sb.AppendLine("codeybox_lock_i=0");
        sb.AppendLine("while ! codeybox_root_sh 'mkdir \"$1\" 2>/dev/null' \"$codeybox_lock_dir\"; do");
        sb.AppendLine("    if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then codeybox_drain_stdin; exit 0; fi");
        sb.AppendLine("    if [ \"$codeybox_lock_i\" -ge \"$codeybox_lock_max\" ]; then");
        sb.AppendLine("        echo \"codeybox-detached: timed out waiting for launch lock\" >&2");
        sb.AppendLine("        codeybox_drain_stdin");
        sb.AppendLine($"        exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("    fi");
        sb.AppendLine("    sleep 0.1");
        sb.AppendLine("    codeybox_lock_i=$((codeybox_lock_i + 1))");
        sb.AppendLine("done");
        sb.AppendLine("trap codeybox_release_lock EXIT");
        sb.AppendLine("if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then codeybox_drain_stdin; exit 0; fi");
        sb.AppendLine("codeybox_root_sh 'rm -f \"$1.tmp\" \"$1.exit.tmp\" \"$1.exit-token.tmp\" \"$1.stdin\" \"$1.stdin.tmp\"' \"$codeybox_pgid_marker\" || true");
        sb.AppendLine("if [ -n \"$codeybox_exit_token_file\" ]; then");
        sb.AppendLine("    codeybox_output_exit_token=");
        sb.AppendLine("    IFS= read -r codeybox_output_exit_token || true");
        sb.AppendLine("    if [ -n \"$codeybox_output_exit_token\" ] && ! codeybox_root_sh 'test -f \"$1\"' \"$codeybox_exit_token_file\"; then");
        sb.AppendLine("        if ! printf '%s' \"$codeybox_output_exit_token\" | codeybox_root_sh 'token_file=$1");
        sb.AppendLine("token_tmp=\"${token_file}.tmp\"");
        sb.AppendLine("umask 077");
        sb.AppendLine("cat > \"$token_tmp\" || exit 1");
        sb.AppendLine("{ chown root:root \"$token_tmp\" 2>/dev/null || true; }");
        sb.AppendLine("chmod 0600 \"$token_tmp\" || exit 1");
        sb.AppendLine("mv -f \"$token_tmp\" \"$token_file\" || exit 1' \"$codeybox_exit_token_file\"; then");
        sb.AppendLine("            echo \"codeybox-detached: failed to publish exit token sidecar\" >&2");
        sb.AppendLine("            codeybox_drain_stdin");
        sb.AppendLine($"            exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("        fi");
        sb.AppendLine("    fi");
        sb.AppendLine("fi");
        sb.AppendLine("if [ -n \"$codeybox_stdin_file\" ]; then");
        sb.AppendLine("    if ! codeybox_root_sh 'stdin_file=$1; stdin_tmp=\"${stdin_file}.tmp\"; umask 077; cat > \"$stdin_tmp\" && mv -f \"$stdin_tmp\" \"$stdin_file\" && { chown root:root \"$stdin_file\" 2>/dev/null || true; } && chmod 0600 \"$stdin_file\"' \"$codeybox_stdin_file\"; then");
        sb.AppendLine("        echo \"codeybox-detached: failed to publish stdin sidecar\" >&2");
        sb.AppendLine("        codeybox_drain_stdin");
        sb.AppendLine($"        exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("    fi");
        sb.AppendLine("fi");
        sb.AppendLine("set -a");
        sb.AppendLine(". \"$codeybox_env_file\"");
        sb.AppendLine("set +a");
        sb.AppendLine("codeybox_output_url=\"${CODEYBOX_AGENT_OUTPUT_URL:-}\"");
        sb.AppendLine("codeybox_output_token=\"${CODEYBOX_AGENT_OUTPUT_TOKEN:-}\"");
        sb.AppendLine("codeybox_output_run_id=\"${CODEYBOX_AGENT_OUTPUT_RUN_ID:-}\"");
        // Keep HTTP transport credentials out of the detached agent
        // environment. CODEYBOX_AGENT_RUN_ID, if present in the caller's
        // environment, is a separate work-item preemption hint and is left
        // untouched by this output-transport scrub.
        sb.AppendLine("unset CODEYBOX_AGENT_OUTPUT_URL CODEYBOX_AGENT_OUTPUT_TOKEN CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN CODEYBOX_AGENT_OUTPUT_RUN_ID");
        sb.AppendLine("codeybox_http_ready() {");
        sb.AppendLine("CODEYBOX_AGENT_OUTPUT_URL=\"$codeybox_output_url\" \\");
        sb.AppendLine("CODEYBOX_AGENT_OUTPUT_TOKEN=\"$codeybox_output_token\" \\");
        sb.AppendLine("CODEYBOX_AGENT_OUTPUT_RUN_ID=\"$codeybox_output_run_id\" \\");
        sb.AppendLine("python3 - <<'PY'");
        sb.AppendLine("import os, sys, urllib.error, urllib.parse, urllib.request");
        sb.AppendLine("base = os.environ.get('CODEYBOX_AGENT_OUTPUT_URL', '').rstrip('/')");
        sb.AppendLine("run_id = os.environ.get('CODEYBOX_AGENT_OUTPUT_RUN_ID', '')");
        sb.AppendLine("token = os.environ.get('CODEYBOX_AGENT_OUTPUT_TOKEN', '')");
        sb.AppendLine("if not base or not run_id or not token:");
        sb.AppendLine("    sys.exit(1)");
        sb.AppendLine("url = base + '/' + urllib.parse.quote(run_id, safe='') + '/ready/0'");
        sb.AppendLine("req = urllib.request.Request(");
        sb.AppendLine("    url,");
        sb.AppendLine("    data=b'',");
        sb.AppendLine("    method='POST',");
        sb.AppendLine("    headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/octet-stream'})");
        sb.AppendLine("try:");
        sb.AppendLine("    with urllib.request.urlopen(req, timeout=1.0) as resp:");
        sb.AppendLine("        code = resp.getcode()");
        sb.AppendLine("        sys.exit(0 if 200 <= code < 300 else 1)");
        sb.AppendLine("except urllib.error.HTTPError:");
        sb.AppendLine("    sys.exit(1)");
        sb.AppendLine("except Exception:");
        sb.AppendLine("    sys.exit(1)");
        sb.AppendLine("PY");
        sb.AppendLine("}");
        sb.AppendLine("if [ -z \"$codeybox_output_url\" ] || [ -z \"$codeybox_output_token\" ] || [ -z \"$codeybox_output_run_id\" ]; then");
        sb.Append("    echo ")
            .Append(MultipassSandboxProvider.ShellSingleQuote(AgentOutputHttpSetupFailureMarker))
            .AppendLine(" >&2");
        sb.AppendLine($"    exit {AgentOutputHttpSetupFailedExitCode}");
        sb.AppendLine("fi");
        sb.AppendLine("if ! codeybox_http_ready; then");
        sb.Append("    echo ")
            .Append(MultipassSandboxProvider.ShellSingleQuote(AgentOutputHttpSetupFailureMarker))
            .AppendLine(" >&2");
        sb.AppendLine($"    exit {AgentOutputHttpSetupFailedExitCode}");
        sb.AppendLine("fi");
        sb.AppendLine("codeybox_child_script=$(mktemp \"${TMPDIR:-/tmp}/codeybox-detached-child.XXXXXX\")");
        sb.AppendLine("cat > \"$codeybox_child_script\" <<'CODEYBOX_DETACHED_CHILD'");
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set +e");
        sb.AppendLine("exec </dev/null >/dev/null 2>/dev/null");
        sb.AppendLine("rm -f \"$0\" 2>/dev/null || true");
        sb.AppendLine("codeybox_pgid_marker=$1");
        sb.AppendLine("codeybox_stdin_file=$2");
        sb.AppendLine("codeybox_env_file=$3");
        sb.AppendLine("codeybox_exit_token_file=$4");
        sb.AppendLine("shift");
        sb.AppendLine("shift");
        sb.AppendLine("shift");
        sb.AppendLine("shift");
        sb.AppendLine("codeybox_root_sh() {");
        sb.AppendLine("    local codeybox_script=\"$1\"");
        sb.AppendLine("    shift");
        sb.AppendLine("    sudo -n sh -c \"$codeybox_script\" codeybox-root-sh \"$@\"");
        sb.AppendLine("}");
        sb.AppendLine("codeybox_output_url=");
        sb.AppendLine("codeybox_output_run_id=");
        sb.AppendLine("codeybox_output_exit_token=");
        sb.AppendLine("if [ -n \"$codeybox_exit_token_file\" ]; then");
        sb.AppendLine("    codeybox_output_exit_token=$(codeybox_root_sh 'cat -- \"$1\" 2>/dev/null || true' \"$codeybox_exit_token_file\" || true)");
        sb.AppendLine("    codeybox_root_sh 'rm -f -- \"$1\"' \"$codeybox_exit_token_file\" || true");
        sb.AppendLine("fi");
        sb.AppendLine("if [ -r \"$codeybox_env_file\" ]; then");
        sb.AppendLine("    . \"$codeybox_env_file\"");
        sb.AppendLine("    codeybox_output_url=\"${CODEYBOX_AGENT_OUTPUT_URL:-}\"");
        sb.AppendLine("    codeybox_output_run_id=\"${CODEYBOX_AGENT_OUTPUT_RUN_ID:-}\"");
        sb.AppendLine("    unset CODEYBOX_AGENT_OUTPUT_URL CODEYBOX_AGENT_OUTPUT_TOKEN CODEYBOX_AGENT_OUTPUT_EXIT_TOKEN CODEYBOX_AGENT_OUTPUT_RUN_ID");
        sb.AppendLine("fi");
        sb.AppendLine("codeybox_http_exit() {");
        sb.AppendLine("local codeybox_exit_code=\"$1\"");
        sb.AppendLine("local codeybox_exit_diagnostic=\"${2:-}\"");
        sb.AppendLine("if [ -z \"$codeybox_output_exit_token\" ]; then");
        sb.AppendLine("    return 0");
        sb.AppendLine("fi");
        sb.AppendLine("{");
        sb.AppendLine("    printf '%s\\n' \"$codeybox_output_exit_token\"");
        sb.AppendLine("    printf '%s\\n' \"$codeybox_exit_code\"");
        sb.AppendLine("    if [ -n \"$codeybox_exit_diagnostic\" ]; then");
        sb.AppendLine("        printf '%s\\n' \"$codeybox_exit_diagnostic\"");
        sb.AppendLine("    fi");
        sb.AppendLine("} | \\");
        sb.AppendLine("CODEYBOX_AGENT_OUTPUT_URL=\"$codeybox_output_url\" \\");
        sb.AppendLine("CODEYBOX_AGENT_OUTPUT_RUN_ID=\"$codeybox_output_run_id\" \\");
        sb.Append("python3 -c ")
            .Append(MultipassSandboxProvider.ShellSingleQuote(detachedHttpExitPosterScript))
            .Append('\n');
        sb.AppendLine("}");
        sb.AppendLine("codeybox_pgid=$$");
        sb.AppendLine("if ! codeybox_root_sh 'marker=$1; pgid=$2; tmp=\"${marker}.tmp\"; umask 077; printf \"%s\\n\" \"$pgid\" > \"$tmp\" && mv -f \"$tmp\" \"$marker\" && { chown root:root \"$marker\" 2>/dev/null || true; } && chmod 0600 \"$marker\"' \"$codeybox_pgid_marker\" \"$codeybox_pgid\"; then");
        sb.AppendLine($"    exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("fi");
        sb.AppendLine($"codeybox_stdout_file=$(mktemp \"${{TMPDIR:-/tmp}}/codeybox-detached-stdout.XXXXXX\") || exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine($"codeybox_stderr_file=$(mktemp \"${{TMPDIR:-/tmp}}/codeybox-detached-stderr.XXXXXX\") || {{ rm -f \"$codeybox_stdout_file\"; exit {DetachedSupervisorSetupFailedExitCode}; }}");
        sb.AppendLine("if [ -n \"$codeybox_stdin_file\" ]; then");
        sb.AppendLine("    set -o pipefail");
        sb.AppendLine("    set +e");
        sb.AppendLine("    codeybox_root_sh 'cat -- \"$1\"' \"$codeybox_stdin_file\" 2>>\"$codeybox_stderr_file\" | \"$@\" >\"$codeybox_stdout_file\" 2>>\"$codeybox_stderr_file\"");
        sb.AppendLine("    codeybox_status=(\"${PIPESTATUS[@]}\")");
        sb.AppendLine("    set -e");
        sb.AppendLine("    codeybox_stdin_rc=${codeybox_status[0]}");
        sb.AppendLine("    codeybox_wrapper_rc=${codeybox_status[1]}");
        sb.AppendLine("    if [ \"$codeybox_stdin_rc\" -ne 0 ] && [ \"$codeybox_stdin_rc\" -ne 141 ] && ! grep -qi 'broken pipe' \"$codeybox_stderr_file\"; then");
        sb.AppendLine("        printf 'codeybox-detached: failed to read stdin sidecar (exit %s)\\n' \"$codeybox_stdin_rc\" >>\"$codeybox_stderr_file\"");
        sb.AppendLine($"        codeybox_wrapper_rc={DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("    fi");
        sb.AppendLine("else");
        sb.AppendLine("    \"$@\" </dev/null >\"$codeybox_stdout_file\" 2>\"$codeybox_stderr_file\"");
        sb.AppendLine("    codeybox_wrapper_rc=$?");
        sb.AppendLine("fi");
        sb.AppendLine("codeybox_http_exit \"$codeybox_wrapper_rc\" || true");
        sb.AppendLine("if ! codeybox_root_sh 'marker=$1");
        sb.AppendLine("stdout_file=$2");
        sb.AppendLine("stderr_file=$3");
        sb.AppendLine("exit_code=$4");
        sb.AppendLine("stdout_tmp=\"${marker}.stdout.tmp\"");
        sb.AppendLine("stderr_tmp=\"${marker}.stderr.tmp\"");
        sb.AppendLine("exit_tmp=\"${marker}.exit.tmp\"");
        sb.AppendLine("umask 077");
        sb.AppendLine(": > \"$stdout_tmp\" || exit 1");
        sb.AppendLine("dd if=\"$stdout_file\" of=\"$stdout_tmp\" bs=65536 count=1 2>/dev/null || true");
        sb.AppendLine(": > \"$stderr_tmp\" || exit 1");
        sb.AppendLine("dd if=\"$stderr_file\" of=\"$stderr_tmp\" bs=65536 count=1 2>/dev/null || true");
        sb.AppendLine("printf \"%s\\n\" \"$exit_code\" > \"$exit_tmp\" || exit 1");
        sb.AppendLine("mv -f \"$stdout_tmp\" \"${marker}.stdout\" || exit 1");
        sb.AppendLine("mv -f \"$stderr_tmp\" \"${marker}.stderr\" || exit 1");
        sb.AppendLine("mv -f \"$exit_tmp\" \"${marker}.exit\" || exit 1");
        sb.AppendLine("{ chown root:root \"${marker}.stdout\" \"${marker}.stderr\" \"${marker}.exit\" 2>/dev/null || true; }");
        sb.AppendLine("chmod 0600 \"${marker}.stdout\" \"${marker}.stderr\" \"${marker}.exit\"' \"$codeybox_pgid_marker\" \"$codeybox_stdout_file\" \"$codeybox_stderr_file\" \"$codeybox_wrapper_rc\"; then");
        sb.AppendLine("    rm -f \"$codeybox_stdout_file\" \"$codeybox_stderr_file\"");
        sb.AppendLine("    codeybox_http_exit \"$codeybox_wrapper_rc\" \"codeybox-detached: failed to publish output sidecars\" || true");
        sb.AppendLine("    exit \"$codeybox_wrapper_rc\"");
        sb.AppendLine("fi");
        sb.AppendLine("rm -f \"$codeybox_stdout_file\" \"$codeybox_stderr_file\"");
        sb.AppendLine("exit \"$codeybox_wrapper_rc\"");
        sb.AppendLine("CODEYBOX_DETACHED_CHILD");
        sb.AppendLine("chmod 0700 \"$codeybox_child_script\"");
        sb.Append("setsid /bin/bash \"$codeybox_child_script\" \"$codeybox_pgid_marker\" \"$codeybox_stdin_file\" \"$codeybox_env_file\" \"$codeybox_exit_token_file\"");
        foreach (var arg in command)
            sb.Append(' ').Append(MultipassSandboxProvider.ShellSingleQuote(arg));
        sb.AppendLine(" </dev/null >/dev/null 2>/dev/null &");
        sb.AppendLine("codeybox_detached_pid=$!");
        sb.AppendLine("codeybox_marker_deadline=$((SECONDS + codeybox_marker_wait_seconds))");
        sb.AppendLine("while ! codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; do");
        sb.AppendLine("    if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then");
        sb.AppendLine("        break");
        sb.AppendLine("    fi");
        sb.AppendLine("    if [ \"$SECONDS\" -ge \"$codeybox_marker_deadline\" ]; then");
        sb.AppendLine("        if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then");
        sb.AppendLine("            break");
        sb.AppendLine("        fi");
        sb.AppendLine("        kill -TERM \"-$codeybox_detached_pid\" 2>/dev/null || true");
        sb.AppendLine("        codeybox_term_i=0");
        sb.AppendLine("        while kill -0 \"-$codeybox_detached_pid\" 2>/dev/null && [ \"$codeybox_term_i\" -lt 20 ]; do");
        sb.AppendLine("            sleep 0.05");
        sb.AppendLine("            codeybox_term_i=$((codeybox_term_i + 1))");
        sb.AppendLine("        done");
        sb.AppendLine("        if kill -0 \"-$codeybox_detached_pid\" 2>/dev/null; then");
        sb.AppendLine("            kill -KILL \"-$codeybox_detached_pid\" 2>/dev/null || true");
        sb.AppendLine("        fi");
        sb.AppendLine("        set +e");
        sb.AppendLine("        wait \"$codeybox_detached_pid\" 2>/dev/null");
        sb.AppendLine("        set -e");
        sb.AppendLine("        echo \"codeybox-detached: timed out waiting for process group marker\" >&2");
        sb.AppendLine($"        exit {DetachedSupervisorSetupFailedExitCode}");
        sb.AppendLine("    fi");
        sb.AppendLine("    if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then");
        sb.AppendLine("        break");
        sb.AppendLine("    fi");
        sb.AppendLine("    if ! kill -0 \"$codeybox_detached_pid\" 2>/dev/null; then");
        sb.AppendLine("        if codeybox_root_sh 'test -f \"$1\"' \"$codeybox_pgid_marker\"; then");
        sb.AppendLine("            break");
        sb.AppendLine("        fi");
        sb.AppendLine("        set +e");
        sb.AppendLine("        wait \"$codeybox_detached_pid\"");
        sb.AppendLine("        codeybox_child_rc=$?");
        sb.AppendLine("        set -e");
        sb.AppendLine("        echo \"codeybox-detached: detached child exited before publishing process group marker (exit $codeybox_child_rc)\" >&2");
        sb.AppendLine("        exit \"$codeybox_child_rc\"");
        sb.AppendLine("    fi");
        sb.AppendLine("    sleep 0.1");
        sb.AppendLine("done");
        sb.AppendLine("disown \"$codeybox_detached_pid\" 2>/dev/null || true");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    private async Task TransferFileToVmAsync(string hostPath, string vmRelativePath, string description, CancellationToken ct)
    {
        var environment = MultipassSandboxProvider.BuildHostProcessEnvironment(_workItemId);
        // `transfer` is a heavy op (sshfs / scp under the hood, which
        // exercises the same daemon path that stat-times-out under
        // concurrent mount load). Acquire the op-gate around the whole
        // SSH-not-ready retry loop so transient retries don't release the
        // slot mid-flight.
        var transferArgv = new[]
        {
            _opts.MultipassBinary, "transfer", hostPath, $"{_name}:{vmRelativePath}",
        };
        using var gate = await AcquireOpGateAsync(transferArgv, ct).ConfigureAwait(false);
        var tx = await MultipassRetry.RunWithRetryAsync(
            ctInner => _runner.RunAsync(
                transferArgv,
                stdin: null,
                ct: ctInner,
                environment: environment),
            _log,
            description,
            ct);
        if (tx.ExitCode != 0)
            throw new InvalidOperationException($"{description} failed: {tx.Stderr}");
    }

    private async Task RunVmCommandAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var result = await RunMultipassAsync(
            [_opts.MultipassBinary, "exec", _name, "--", .. command],
            stdin: null,
            ct: ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"multipass exec setup command failed: {result.Stderr}");
    }

    private async Task<DetachedExitResult> WaitForDetachedCompletionAsync(
        string processGroupMarker,
        MultipassAgentOutputHttpIngestSession ingest,
        CancellationToken ct)
    {
        // Trust only host-observed liveness plus the authenticated HTTP exit
        // completion. VM-side sidecars are diagnostic only: the agent can sudo
        // inside the sandbox and must not be able to forge a successful exit.
        var pollFailures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            var state = await ProbeDetachedProcessGroupAsync(processGroupMarker, ct).ConfigureAwait(false);
            if (state.Status != DetachedProcessGroupStatus.PollFailed)
                MarkActiveSandboxProgress("detached-" + state.Status.ToString().ToLowerInvariant());
            if (state.Status == DetachedProcessGroupStatus.PollFailed)
            {
                pollFailures++;
                _log.LogDebug(
                    "Detached exec process group poll {Attempt}/{Limit} for {Name} failed while waiting for completion: {Error}",
                    pollFailures,
                    DetachedPollFailureLimit,
                    _name,
                    state.Error?.Trim());
                if (pollFailures >= DetachedPollFailureLimit)
                {
                    await TryTerminateDetachedProcessGroupAsync(processGroupMarker).ConfigureAwait(false);
                    return new DetachedExitResult(1, state.Error);
                }
                continue;
            }
            pollFailures = 0;
            if (state.Error is not null)
            {
                await TryTerminateDetachedProcessGroupAsync(processGroupMarker).ConfigureAwait(false);
                return new DetachedExitResult(1, state.Error);
            }
            if (state.Status == DetachedProcessGroupStatus.Missing)
                return new DetachedExitResult(
                    1,
                    $"detached exec process group marker {processGroupMarker} was not written\n");
            if (state.Status == DetachedProcessGroupStatus.Alive)
            {
                if (ingest.TryGetExitCode(out var authenticatedExitCode))
                {
                    var reapError = await EnsureDetachedProcessGroupReapedAsync(processGroupMarker, ct).ConfigureAwait(false);
                    return new DetachedExitResult(authenticatedExitCode, reapError);
                }

                continue;
            }
            if (state.Status == DetachedProcessGroupStatus.Exited)
            {
                if (ingest.TryGetExitCode(out var exitCode))
                {
                    var reapError = await EnsureDetachedProcessGroupReapedAsync(processGroupMarker, ct).ConfigureAwait(false);
                    return new DetachedExitResult(exitCode, reapError);
                }

                if (state.ProcessGroupAlive)
                    continue;

                // PG is gone but the wrapper never delivered the host-authenticated
                // completion. The wrapper crashed, was killed, or could not reach
                // the ingest listener — surface a diagnostic rather than guessing.
                return new DetachedExitResult(
                    1,
                    $"detached exec process group {state.ProcessGroupId} exited without authenticated exit completion\n");
            }
        }
    }

    private static ProcessRunResult BuildAgentOutputHttpSetupFailureResult(string detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}";
        return new ProcessRunResult(
            AgentOutputHttpSetupFailedExitCode,
            "",
            AgentOutputHttpSetupFailureMarker + suffix + "\n");
    }

    private async Task<DetachedOutputSidecars> ReadDetachedOutputSidecarsAsync(
        string processGroupMarker,
        CancellationToken ct)
    {
        var stdout = await ReadDetachedOutputSidecarAsync(processGroupMarker + ".stdout", ct).ConfigureAwait(false);
        var stderr = await ReadDetachedOutputSidecarAsync(processGroupMarker + ".stderr", ct).ConfigureAwait(false);
        return new DetachedOutputSidecars(
            stdout.Output,
            stderr.Output,
            stdout.Error + stderr.Error);
    }

    private async Task<DetachedOutputSidecar> ReadDetachedOutputSidecarAsync(
        string vmPath,
        CancellationToken ct)
    {
        const string readCommand = """
            codeybox_output_file=$1
            if ! test -f "$codeybox_output_file"; then
                exit 0
            fi
            dd if="$codeybox_output_file" bs=65536 count=1 2>/dev/null || true
            exit 0
            """;
        var result = await RunMultipassAsync(
            [_opts.MultipassBinary, "exec", _name, "--", "sudo", "-n", "sh", "-c", readCommand, "codeybox-detached-output", vmPath],
            stdin: null,
            ct: ct,
            maxStdoutBytes: 65536,
            maxStderrBytes: 4096,
            killOnOutputLimit: false).ConfigureAwait(false);
        if (result.ExitCode == 0)
            return new DetachedOutputSidecar(result.Stdout, "");

        return new DetachedOutputSidecar(
            "",
            $"failed to read detached exec output sidecar {vmPath} (exit {result.ExitCode}): {result.Stderr.TrimEnd()}\n");
    }

    private async Task<string?> EnsureDetachedProcessGroupReapedAsync(string processGroupMarker, CancellationToken ct)
    {
        DetachedProcessGroupProbe state = default;
        DetachedProcessGroupProbe? lastPollFailure = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            state = await ProbeDetachedProcessGroupAsync(processGroupMarker, ct).ConfigureAwait(false);
            if (state.Status == DetachedProcessGroupStatus.PollFailed)
            {
                lastPollFailure = state;
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                continue;
            }
            if (state.Error is not null)
                return state.Error;
            if (state.Status == DetachedProcessGroupStatus.Exited && !state.ProcessGroupAlive)
                return null;
            if (state.Status == DetachedProcessGroupStatus.Missing)
                return $"detached exec process group marker {processGroupMarker} disappeared before cleanup\n";

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        await TryTerminateDetachedProcessGroupAsync(processGroupMarker).ConfigureAwait(false);
        lastPollFailure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            state = await ProbeDetachedProcessGroupAsync(processGroupMarker, ct).ConfigureAwait(false);
            if (state.Status == DetachedProcessGroupStatus.PollFailed)
            {
                lastPollFailure = state;
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                continue;
            }
            if (state.Error is not null)
                return state.Error;
            return state.Status == DetachedProcessGroupStatus.Alive
                   || (state.Status == DetachedProcessGroupStatus.Exited && state.ProcessGroupAlive)
                ? $"detached exec process group {state.ProcessGroupId} remained alive after termination\n"
                : null;
        }

        return lastPollFailure?.Error;
    }

    private async Task<DetachedProcessGroupProbe> ProbeDetachedProcessGroupAsync(
        string processGroupMarker,
        CancellationToken ct)
    {
        // The exit sidecar lives at <marker>.exit. We read it inline so a
        // single multipass exec yields both liveness and (when available)
        // the wrapper-written exit code — no separate `cat` round-trip and no
        // additional host-side multipass spin.
        const string pollCommand = """
            codeybox_pgid_marker=$1
            if ! test -f "$codeybox_pgid_marker"; then
                printf 'missing\n'
                exit 0
            fi
            codeybox_pgid=$(cat -- "$codeybox_pgid_marker" 2>/dev/null || true)
            case "$codeybox_pgid" in
                ''|*[!0-9]*|0)
                    printf 'detached exec process group marker %s was malformed: %s\n' "$codeybox_pgid_marker" "$codeybox_pgid" >&2
                    exit 73
                    ;;
            esac
            codeybox_alive=gone
            if kill -0 "-$codeybox_pgid" 2>/dev/null; then
                codeybox_alive=alive
            fi
            codeybox_exit_file="${codeybox_pgid_marker}.exit"
            if test -f "$codeybox_exit_file"; then
                codeybox_exit_code=$(cat -- "$codeybox_exit_file" 2>/dev/null || true)
                case "$codeybox_exit_code" in
                    ''|*[!0-9-]*)
                        printf 'exited %s\n' "$codeybox_pgid"
                        ;;
                    *)
                        printf 'exited %s %s %s\n' "$codeybox_pgid" "$codeybox_exit_code" "$codeybox_alive"
                        ;;
                esac
            elif [ "$codeybox_alive" = "alive" ]; then
                printf 'alive %s\n' "$codeybox_pgid"
            else
                printf 'exited %s\n' "$codeybox_pgid"
            fi
            exit 0
            """;
        var result = await RunMultipassAsync(
            [_opts.MultipassBinary, "exec", _name, "--", "sudo", "-n", "sh", "-c", pollCommand, "codeybox-detached-poll", processGroupMarker],
            stdin: null,
            ct: ct,
            maxStdoutBytes: 128,
            maxStderrBytes: 4096,
            killOnOutputLimit: false).ConfigureAwait(false);

        if (result.ExitCode == DetachedProcessGroupMalformedExitCode)
            return new DetachedProcessGroupProbe(
                DetachedProcessGroupStatus.Malformed,
                ProcessGroupId: null,
                ProcessGroupAlive: false,
                Error: result.Stderr.TrimEnd() + "\n");

        if (result.ExitCode != 0)
            return new DetachedProcessGroupProbe(
                DetachedProcessGroupStatus.PollFailed,
                ProcessGroupId: null,
                ProcessGroupAlive: false,
                Error: $"detached exec process group poll failed (exit {result.ExitCode}): {result.Stderr.TrimEnd()}\n");

        var text = result.Stdout.Trim();
        if (string.Equals(text, "missing", StringComparison.Ordinal))
            return new DetachedProcessGroupProbe(DetachedProcessGroupStatus.Missing, ProcessGroupId: null, ProcessGroupAlive: false, Error: null);
        if (text.StartsWith("alive ", StringComparison.Ordinal)
            && int.TryParse(text["alive ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var alivePgid))
            return new DetachedProcessGroupProbe(DetachedProcessGroupStatus.Alive, alivePgid, ProcessGroupAlive: true, Error: null);
        if (text.StartsWith("exited ", StringComparison.Ordinal))
        {
            var rest = text["exited ".Length..];
            var spaceIdx = rest.IndexOf(' ', StringComparison.Ordinal);
            if (spaceIdx < 0)
            {
                if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var exitedPgid))
                    return new DetachedProcessGroupProbe(DetachedProcessGroupStatus.Exited, exitedPgid, ProcessGroupAlive: false, Error: null);
            }
            else
            {
                var pgidText = rest[..spaceIdx];
                var exitAndStateText = rest[(spaceIdx + 1)..];
                var stateIdx = exitAndStateText.IndexOf(' ', StringComparison.Ordinal);
                var aliveText = stateIdx < 0 ? "gone" : exitAndStateText[(stateIdx + 1)..];
                if (int.TryParse(pgidText, NumberStyles.None, CultureInfo.InvariantCulture, out var exitedPgid))
                {
                    return new DetachedProcessGroupProbe(
                        DetachedProcessGroupStatus.Exited,
                        exitedPgid,
                        ProcessGroupAlive: string.Equals(aliveText, "alive", StringComparison.Ordinal),
                        Error: null);
                }
            }
        }

        return new DetachedProcessGroupProbe(
            DetachedProcessGroupStatus.Malformed,
            ProcessGroupId: null,
            ProcessGroupAlive: false,
            Error: $"detached exec process group poll returned malformed output: {text}\n");
    }

    private async Task TryTerminateDetachedProcessGroupAsync(string processGroupMarker)
    {
        const string killCommand = """
            codeybox_pgid_marker=$1
            codeybox_wait_i=0
            while ! test -f "$codeybox_pgid_marker"; do
                if [ "$codeybox_wait_i" -ge 50 ]; then
                    exit 0
                fi
                sleep 0.1
                codeybox_wait_i=$((codeybox_wait_i + 1))
            done
            codeybox_pgid=$(cat -- "$codeybox_pgid_marker" 2>/dev/null || true)
            case "$codeybox_pgid" in
                ''|*[!0-9]*|0) exit 0 ;;
            esac
            kill -TERM "-$codeybox_pgid" 2>/dev/null || true
            codeybox_i=0
            while [ "$codeybox_i" -lt 20 ]; do
                if ! kill -0 "-$codeybox_pgid" 2>/dev/null; then
                    exit 0
                fi
                sleep 0.1
                codeybox_i=$((codeybox_i + 1))
            done
            kill -KILL "-$codeybox_pgid" 2>/dev/null || true
            exit 0
            """;
        try
        {
            using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await RunMultipassAsync(
                [_opts.MultipassBinary, "exec", _name, "--", "sudo", "-n", "sh", "-c", killCommand, "codeybox-detached-kill", processGroupMarker],
                stdin: null,
                ct: killCts.Token,
                maxStdoutBytes: 4096,
                maxStderrBytes: 4096,
                killOnOutputLimit: false).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _log.LogWarning(
                    "Failed to terminate detached exec process group for {Name}: exit {ExitCode}: {Stderr}",
                    _name,
                    result.ExitCode,
                    result.Stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to terminate detached exec process group for {Name}", _name);
        }
    }

    private static string AppendDetachedMarkerError(string stderr, string? markerError)
    {
        if (string.IsNullOrEmpty(markerError))
            return stderr;
        var normalized = markerError.EndsWith('\n')
            ? markerError
            : markerError + "\n";
        return string.IsNullOrEmpty(stderr)
            ? normalized
            : stderr + normalized;
    }

    private readonly record struct DetachedExitResult(int ExitCode, string? Error);
    private readonly record struct DetachedOutputSidecars(string Stdout, string Stderr, string Error);
    private readonly record struct DetachedOutputSidecar(string Output, string Error);
    private readonly record struct DetachedProcessGroupProbe(
        DetachedProcessGroupStatus Status,
        int? ProcessGroupId,
        bool ProcessGroupAlive,
        string? Error);

    private enum DetachedProcessGroupStatus
    {
        Missing = 0,
        Alive = 1,
        Exited = 2,
        Malformed = 3,
        PollFailed = 4,
    }

    private async Task TryRemoveTransferredFilesAsync(IReadOnlyList<string> vmPaths)
    {
        try
        {
            _ = await RunMultipassAsync(
                [_opts.MultipassBinary, "exec", _name, "--", "rm", "-f", .. vmPaths],
                stdin: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to clean transferred multipass exec files for {Name}", _name);
        }
    }

    private async Task TryRemoveDetachedSupervisorFilesAsync(string processGroupMarker)
    {
        const string cleanupCommand = """
            codeybox_pgid_marker=$1
            rm -f -- "$codeybox_pgid_marker" \
                "${codeybox_pgid_marker}.tmp" \
                "${codeybox_pgid_marker}.exit" \
                "${codeybox_pgid_marker}.exit.tmp" \
                "${codeybox_pgid_marker}.exit-token" \
                "${codeybox_pgid_marker}.exit-token.tmp" \
                "${codeybox_pgid_marker}.stdout" \
                "${codeybox_pgid_marker}.stdout.tmp" \
                "${codeybox_pgid_marker}.stderr" \
                "${codeybox_pgid_marker}.stderr.tmp" \
                "${codeybox_pgid_marker}.stdin" \
                "${codeybox_pgid_marker}.stdin.tmp"
            rmdir "${codeybox_pgid_marker}.lock" 2>/dev/null || true
            exit 0
            """;
        try
        {
            _ = await RunMultipassAsync(
                [_opts.MultipassBinary, "exec", _name, "--", "sudo", "-n", "sh", "-c", cleanupCommand, "codeybox-detached-cleanup", processGroupMarker],
                stdin: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to clean detached supervisor files for {Name}", _name);
        }
    }

    private async Task<ProcessRunResult> RunMultipassAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true)
    {
        var environment = MultipassSandboxProvider.BuildHostProcessEnvironment(_workItemId);
        // Route every multipass CLI call through the provider's heavy-op
        // gate. The classifier returns a no-op disposable for `exec` and
        // status polls so VM-internal calls (hundreds per agent run) stay
        // unbounded — only mount / launch / start / stop / transfer /
        // delete / clone / suspend / restart contend for the gate's
        // MaxConcurrentBoots slots. The gate is acquired BEFORE the retry
        // loop so transient-failure retries don't release-and-reacquire.
        using var gate = await AcquireOpGateAsync(argv, ct).ConfigureAwait(false);
        return await MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            ctInner => _runner.RunAsync(
                argv,
                stdin,
                ctInner,
                stdoutChunkCallback,
                stderrChunkCallback,
                maxStdoutBytes,
                maxStderrBytes,
                environment,
                killOnOutputLimit),
            ctInner => MultipassDaemonRetry.ProbeDaemonAsync(
                _runner, _opts.MultipassBinary, _daemonRetryPolicy.HealthProbeTimeout, ctInner),
            _log,
            _workItemId,
            ct,
            _daemonRetryPolicy).ConfigureAwait(false);
    }

    private Task<IDisposable> AcquireOpGateAsync(IReadOnlyList<string> argv, CancellationToken ct)
        => _opGateAcquirer is { } acquirer
            ? acquirer(argv, ct)
            : Task.FromResult<IDisposable>(NoOpGate.Instance);

    private sealed class NoOpGate : IDisposable
    {
        internal static readonly NoOpGate Instance = new();
        public void Dispose() { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_preserveOnDispose)
        {
            return;
        }
        try
        {
            _onNoLongerTrackedActive?.Invoke(_name);
        }
        catch (Exception callbackEx)
        {
            _log.LogWarning(callbackEx, "Failed to release active tracking for multipass VM {Name}", _name);
        }
        await using var disposeScope = await TimingScope.BeginAsync(
            _timings, _timingItemId, _timingPhase, "vm.dispose", log: _log);
        try
        {
            var result = await RunMultipassAsync(
                [_opts.MultipassBinary, "delete", "--purge", _name],
                stdin: null,
                ct: CancellationToken.None);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"multipass delete --purge {_name} failed (exit {result.ExitCode}): {result.Stderr}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete multipass VM {Name}", _name);
            if (_ownedByShutdownHandler)
                throw;
            return;
        }
        _onDisposed?.Invoke(_name);
        AuditLog.SandboxDisposed(_name);
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clean sandbox root {Root}", _sandboxRoot); }
    }

    public async Task KillActiveExecsAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        try
        {
            var stop = await RunMultipassAsync(
                [_opts.MultipassBinary, "stop", _name],
                stdin: null,
                ct: ct).ConfigureAwait(false);
            if (stop.ExitCode != 0)
            {
                _log.LogWarning(
                    "Best-effort stop for timed-out multipass sandbox {Name} returned exit {ExitCode}: {Stderr}",
                    _name,
                    stop.ExitCode,
                    stop.Stderr);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Best-effort stop for timed-out multipass sandbox {Name} failed", _name);
        }
    }

    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        if (_disposed) return;

        // Stop/preserve is called only after the orchestrator has either
        // created a preempt checkpoint or decided the active state is
        // recoverable without one. From this point, DisposeAsync must not
        // delete the VM even if multipass stop fails, times out, or shutdown
        // cancellation abandons the wait.
        _preserveOnDispose = true;
        await TryWritePreemptMarkerAsync(CancellationToken.None);

        var stop = await RunMultipassAsync(
            [_opts.MultipassBinary, "stop", _name],
            stdin: null,
            ct: ct);
        if (stop.ExitCode != 0)
            throw new InvalidOperationException(
                $"multipass stop {_name} failed (exit {stop.ExitCode}): {stop.Stderr}");

        await WaitForStoppedAfterPreserveAsync(ct);
    }

    public void DisablePreserveOnDispose() => _preserveOnDispose = false;

    private async Task TryWritePreemptMarkerAsync(CancellationToken ct)
    {
        var markerPath = Path.Combine(_sandboxRoot, ".codeybox-preempt");
        try
        {
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write preempt marker for multipass VM {Name}", _name);
        }
    }

    private async Task WaitForStoppedAfterPreserveAsync(CancellationToken ct)
    {
        var stopTimeout = MultipassSandboxProvider.ResolveVmStopTimeout(_opts);
        await MultipassSandboxProvider.WaitForStoppedCoreAsync(
            _name,
            stopTimeout,
            ctInner => RunMultipassAsync(
                [_opts.MultipassBinary, "info", _name, "--format=csv"],
                stdin: null,
                ct: ctInner),
            ct);
    }

    /// <summary>
    /// Freezes the VM's RAM to disk via <c>multipass suspend</c>. Sets
    /// <c>_preserveOnDispose</c> so the host-side handle's DisposeAsync becomes
    /// a no-op — the orchestrator owns destruction via the startup resume
    /// handler (which will <c>multipass start</c> the same VM) or the leak
    /// reaper (if the persisted bookkeeping has expired). The flag is also set
    /// when the call is abandoned by <see cref="OperationCanceledException"/>
    /// (per-VM suspend timeout): multipassd keeps writing the snapshot after we
    /// give up, so the VM must not be purged on dispose even though we never
    /// observed the suspend exit cleanly.
    ///
    /// <para>Writes the same <c>.codeybox-preempt</c> marker as
    /// <see cref="StopAndPreserveAsync"/> so the SandboxLeakReaper applies the
    /// PreemptRetention grace window to suspended VMs even if the SuspendedVmName
    /// row gets cleared (e.g. operator manually edits the DB).</para>
    /// </summary>
    public async Task SuspendAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        await TryWritePreemptMarkerAsync(ct);

        ProcessRunResult result;
        try
        {
            result = await RunMultipassAsync(
                [_opts.MultipassBinary, "suspend", _name],
                stdin: null,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            // The per-VM suspend timeout fired (or host shutdown cancelled the
            // call) while `multipass suspend` was still running. multipassd
            // keeps writing the RAM snapshot after our call is abandoned, so the
            // VM still reaches Suspended on disk. DisposeAsync MUST NOT
            // `delete --purge` it: the orchestrator owns destruction via the
            // startup resume handler (which retries `multipass start`) or the
            // leak reaper (which honours the .codeybox-preempt marker grace
            // window written above). The caller
            // (SandboxShutdownTeardownService.SuspendOneAsync) keeps the
            // persisted SuspendedVmName mapping on this OCE so the next startup
            // can reattach. We deliberately do NOT set _isSuspended: the VM is
            // not confirmed frozen yet, so PipelineRunner falls back to the
            // persisted-mapping gate rather than asserting IsSuspended.
            _preserveOnDispose = true;
            throw;
        }
        if (result.ExitCode != 0)
        {
            // Do NOT set _preserveOnDispose on a non-zero exit: a genuine
            // suspend failure leaves the VM Running, so DisposeAsync must
            // destroy it rather than leak a still-Running but un-bookkept VM.
            // The caller persists the SuspendedVmName mapping BEFORE awaiting
            // this method, then CLEARS it again on this non-cancellation
            // exception so the failed VM has no resume bookkeeping; the next
            // DisposeAsync (triggered by the orchestrator's host stopping) then
            // cleanly tears the VM down. (A per-VM timeout, by contrast, is the
            // OperationCanceledException handled above: it keeps the mapping and
            // preserves the VM so the next startup can resume the snapshot
            // multipassd is still writing.)
            throw new InvalidOperationException(
                $"multipass suspend {_name} failed (exit {result.ExitCode}): {result.Stderr}");
        }
        // Only flip the preserve flag once we know the VM is suspended on disk.
        // From here on, DisposeAsync MUST be a no-op so multipass start can
        // resume the same VM on the next orchestrator startup. _isSuspended
        // is observed by PipelineRunner's host-shutdown OCE catch so it can
        // skip its preempt-checkpoint flow (whose in-VM git push would hang
        // against the now-frozen VM).
        _preserveOnDispose = true;
        _isSuspended = true;
        _log.LogInformation("Suspended multipass VM {Name} for orchestrator restart", _name);
    }
}
