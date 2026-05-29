using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
public sealed class MultipassSandboxProvider : ISandboxProvider, IDiskGuardedSandboxProvider, ISuspendingSandboxProvider, IBaselineImageResolver
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

    // Tracks sandboxes still owned by a currently-running phase in this process.
    // Used by ListAllManagedAsync to compute ManagedSandboxInfo.IsTrackedActive.
    private readonly ConcurrentDictionary<string, bool> _activeSandboxNames = new(StringComparer.Ordinal);

    // Parallel registry keyed by sandbox name with the owning work item and a
    // back-reference to the live MultipassSandbox object. Populated only when
    // CreateAsync receives a SandboxSpec carrying TimingWorkItemId — that field
    // is set for every real pipeline phase, so the registry covers everything
    // the orchestrator might want to suspend on shutdown. Tests that call
    // CreateAsync without a work item are intentionally absent.
    private readonly ConcurrentDictionary<string, SuspendableOwnerEntry> _suspendableOwners = new(StringComparer.Ordinal);

    private sealed record SuspendableOwnerEntry(WorkItemId WorkItemId, MultipassSandbox Sandbox);

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
        var bindMounts = new List<(string Host, string Sandbox)>();
        foreach (var m in spec.Mounts)
        {
            if (m.Tmpfs)
            {
                var hostPath = Path.Combine(sandboxRoot, "fs" + m.SandboxPath.Replace('/', '-'));
                Directory.CreateDirectory(hostPath);
                TryChmod0700(hostPath);
                bindMounts.Add((hostPath, m.SandboxPath));
            }
            else if (m.HostPath is not null)
            {
                bindMounts.Add((m.HostPath, m.SandboxPath));
            }
        }

        // Resolve timing context from the spec (null → no timing emitted).
        var timingStore = _timings is not null && spec.TimingWorkItemId.HasValue ? _timings : null;
        var timingItemId = spec.TimingWorkItemId.GetValueOrDefault();
        var workItemId = spec.TimingWorkItemId;
        var timingPhase = spec.TimingPhase ?? "work";

        void MarkTrackedActive(string n)
        {
            _activeSandboxNames[n] = true;
            _listCacheExpiry = DateTimeOffset.MinValue;
        }

        void MarkNoLongerActive(string n)
        {
            _activeSandboxNames.TryRemove(n, out _);
            _suspendableOwners.TryRemove(n, out _);
            _listCacheExpiry = DateTimeOffset.MinValue;
        }

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
                using (await EnterBootGateAsync(opts, ct))
                {
                    await using (var launchScope = await TimingScope.BeginAsync(
                        timingStore, timingItemId, timingPhase, "vm.launch", log: _log))
                    {
                        await LaunchAsync(opts, name, spec, cloudInitPath, workItemId, ct);
                        await WaitForRunningAsync(opts, name, workItemId, ct);
                    }
                }
                // Stop the freshly-launched VM so we can mount (outside vm.launch scope).
                var stop = await RunAsync(opts, [opts.MultipassBinary, "stop", name], stdin: null, ct: ct);
                if (stop.ExitCode != 0)
                    throw new InvalidOperationException($"multipass stop (for mount) failed: {stop.Stderr}");
                await WaitForStoppedAsync(opts, name, workItemId, ct);
            }

            // Apply native mounts while VM is Stopped, then start.
            await using (var mountScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.mount", log: _log))
            {
                await ApplyMountsAsync(opts, name, bindMounts, workItemId, ct);
            }

            using (await EnterBootGateAsync(opts, ct))
            {
                await using (var startScope = await TimingScope.BeginAsync(
                    timingStore, timingItemId, timingPhase, "vm.start", log: _log))
                {
                    await StartAndWaitForRunningAsync(opts, name, workItemId, ct);
                }
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
                daemonRetryPolicy: _daemonRetryPolicy);
            // Register in the owner index ONLY when a work-item ID is present.
            // Sandboxes created without one (some tests) have no orchestrator-side
            // owner to suspend back into, so skip them.
            if (workItemId is { } owner)
                _suspendableOwners[name] = new SuspendableOwnerEntry(owner, sandbox);
            return sandbox;
        }
        catch
        {
            // Best-effort cleanup if launch / mount / transfer half-succeeded.
            try { await TryDeleteVmAsync(opts, name); }
            finally { MarkNoLongerActive(name); }
            try { Directory.Delete(sandboxRoot, recursive: true); } catch { }
            throw;
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
    public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive()
    {
        // ConcurrentDictionary enumeration is snapshot-safe; we materialise
        // immediately so the caller's parallel suspend loop sees a stable list
        // even if a sandbox disposes concurrently.
        var entries = _suspendableOwners.Values.ToList();
        var result = new List<(WorkItemId, ISuspendableSandbox)>(entries.Count);
        foreach (var entry in entries)
            result.Add((entry.WorkItemId, entry.Sandbox));
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

        ProcessRunResult run;
        using (await EnterBootGateAsync(opts, ct))
        {
            run = await RunAsync(opts, [opts.MultipassBinary, "start", name], stdin: null, ct: ct);
        }
        // Treat "instance not running" / "already started" as success: the goal
        // of ResumeSandboxAsync is "VM is Running afterwards", and multipass
        // start on an already-Running VM is the same desired postcondition. We
        // do not parse stderr for VM-not-found vs other failures here because
        // the orchestrator's caller treats any failure as "fall through to the
        // stranded-item recovery path" — preserving the explicit signal.
        if (run.ExitCode != 0)
            throw new InvalidOperationException(
                $"multipass start {name} failed (exit {run.ExitCode}): {run.Stderr}");
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
        // Remove from active set and invalidate cache.
        _activeSandboxNames.TryRemove(name, out _);
        _listCacheExpiry = DateTimeOffset.MinValue;
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

        // B1: when the caller pinned a baseline ref (work item carries one from
        // pickup), reuse it verbatim — even if live config now hashes to a
        // different value. That's the point: an in-flight item keeps its
        // baseline across an operator edit. When null (legacy / unsupported
        // path), compose from live config so behaviour matches pre-B1.
        var baselineName = pinnedBaselineRef ?? ComposeBaselineNameFromLiveConfig(opts, profileName, flavor);

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
        // The cloud-init body matches what BakeBaselineAsync writes:
        // extraRuncmd=null, extraCloudInit=opts.ExtraCloudInit, startRouteService=true,
        // includeGraphicalInstall=false. The install runcmds are listed
        // separately (they run via multipass exec, not via cloud-init).
        var cloudInit = BuildCloudInit(
            extraRuncmd: null,
            extraCloudInit: opts.ExtraCloudInit,
            flavor: flavor,
            startRouteService: true,
            includeGraphicalInstall: false);
        var firstBootRuncmd = BuildFirstBootRuncmd(opts, flavor);
        // Build a canonical, version-prefixed string. The 'v1' prefix lets
        // future schema changes invalidate every existing baseline without
        // ambiguity. Field separator is '|' which cannot appear in profile
        // names or flavor enum strings.
        var canon = string.Join("|", new[]
        {
            "v1",
            profileName,
            flavor.ToString(),
            cloudInit,
            string.Join("\n", firstBootRuncmd),
            opts.ExtraCloudInit ?? string.Empty,
        });
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canon));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <inheritdoc/>
    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        var opts = ReadOptions();
        if (!opts.UseBaselineImages) return null;
        if (!opts.NetworkProfiles.ContainsKey(profileName)) return null;
        return ComposeBaselineNameFromLiveConfig(opts, profileName, flavor);
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

        // Cloud-init for the baseline contains ONLY idempotent file-writes
        // (exec wrapper, route systemd service). Caller-supplied install
        // runcmds run via `multipass exec` AFTER launch instead. Why: when
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
            includeGraphicalInstall: false);
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
            using (await EnterBootGateAsync(opts, ct))
            {
                var run = await RunAsync(opts, argv, stdin: null, ct: ct, workItemId: workItemId);
                if (run.ExitCode != 0)
                    throw new InvalidOperationException($"baseline launch failed: {run.Stderr}");

                // Wait for the (now-minimal) cloud-init to finish — write_files
                // and the route service install. Doesn't include the heavy
                // installs, so should be fast.
                await WaitForRunningAsync(opts, baselineName, workItemId, ct);
            }

            // Run the install commands now, via multipass exec under sudo.
            // Each entry in ExtraRuncmd is a single shell command.
            var installCommands = BuildFirstBootRuncmd(opts, flavor);
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
                    throw new InvalidOperationException(
                        $"baseline install step {i + 1} failed (exit {execRun.ExitCode}):\n" +
                        $"stderr: {execRun.Stderr}\nstdout-tail: {(execRun.Stdout.Length > 1000 ? "…" + execRun.Stdout[^1000..] : execRun.Stdout)}");
            }

            // Stop the baseline so `multipass clone` can use it as a source
            // (clone requires source stopped). Wait for the state to flip
            // so a subsequent clone doesn't race a still-Stopping VM.
            var stop = await RunAsync(opts, [opts.MultipassBinary, "stop", baselineName], stdin: null, ct: ct);
            if (stop.ExitCode != 0)
                throw new InvalidOperationException($"baseline stop failed: {stop.Stderr}");
            await WaitForStoppedAsync(opts, baselineName, workItemId, ct);

            _log.LogInformation("Baseline {Name} baked and stopped, ready to clone", baselineName);
        }
        catch
        {
            // If bake half-succeeded, leave a partial baseline that the operator
            // can `multipass delete --purge` and we'll re-bake. Don't auto-purge
            // — losing partial install progress on transient errors is wasteful.
            _log.LogError("Baseline bake for {Name} failed; you may need to `multipass delete --purge {PurgeTarget}` and retry", baselineName, baselineName);
            throw;
        }
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
        await RunAsync(opts, [opts.MultipassBinary, "stop", baselineName], stdin: null, ct: ct);
        await WaitForStoppedAsync(opts, baselineName, workItemId, ct);

        _log.LogInformation("Cloning {New} from baseline {Baseline}", newName, baselineName);
        var clone = await RunAsync(
            opts,
            [opts.MultipassBinary, "clone", baselineName, "--name", newName],
            stdin: null, ct: ct, workItemId: workItemId);
        if (clone.ExitCode != 0)
            throw new InvalidOperationException($"multipass clone failed: {clone.Stderr}");

        // NOTE: deliberately do NOT start the clone here. multipass clone
        // creates the new VM in Stopped state, which is exactly what
        // SetUpMountsAsync's `mount --type=native` requires. Starting now
        // and stopping again later created a stop-state race where the
        // mount could fire before multipassd had fully released the VM.
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
            throw new InvalidOperationException($"multipass launch failed: {run.Stderr}");
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
            if (info.ExitCode == 0 && info.Stdout.Contains("Running", StringComparison.Ordinal))
                break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (DateTime.UtcNow >= deadline)
            throw new InvalidOperationException(
                $"multipass VM {name} did not reach Running state within {startTimeout}");

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
        //   2  = degraded done (recoverable warnings — e.g. schema-validation
        //        warnings on octal permissions that multipass re-emits as
        //        decimal integers). The VM is still functional.
        //   >2 = genuine error.
        // We accept 0 and 2. Exit 1 gets a bounded retry, then a marker probe
        // before we decide the VM is actually unusable.
        var attempts = Math.Max(1, opts.CloudInitReadyRetryAttempts);
        var retryDelay = opts.CloudInitReadyRetryDelay < TimeSpan.Zero
            ? TimeSpan.Zero
            : opts.CloudInitReadyRetryDelay;
        ProcessRunResult cloudInit = default;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cloudInit = await RunAsync(
                opts,
                [opts.MultipassBinary, "exec", name, "--", "cloud-init", "status", "--wait"],
                stdin: null, ct: ct, workItemId: workItemId);

            if (cloudInit.ExitCode is 0 or 2)
                return;

            if (cloudInit.ExitCode != 1)
                throw new InvalidOperationException(
                    $"cloud-init failed for multipass VM {name} (exit {cloudInit.ExitCode}): {cloudInit.Stderr}");

            if (attempt == attempts)
                break;

            _log.LogInformation(
                "cloud-init status returned exit 1 for multipass VM {Name} (attempt {Attempt}/{Attempts}); retrying after {Delay}. stderr: {Stderr}",
                name, attempt, attempts, retryDelay, DiagnosticText(cloudInit.Stderr));
            await Task.Delay(retryDelay, ct);
        }

        var probe = await ProbeCloudInitReadinessAsync(opts, name, workItemId, ct);
        if (probe.ExitCode == 0)
        {
            _log.LogWarning(
                "cloud-init status kept returning exit 1 for multipass VM {Name} after {Attempts} attempt(s), but readiness probe passed ({ProbeStdout}); proceeding. Last stderr: {Stderr}",
                name, attempts, DiagnosticText(probe.Stdout), DiagnosticText(cloudInit.Stderr));
            return;
        }

        throw new InvalidOperationException(
            $"cloud-init did not report ready for multipass VM {name} after {attempts} attempt(s) " +
            $"(last exit 1 stderr: {DiagnosticText(cloudInit.Stderr)}). " +
            $"readiness probe failed (exit {probe.ExitCode}; stdout: {DiagnosticText(probe.Stdout)}; stderr: {DiagnosticText(probe.Stderr)}). " +
            "Expected /work and /usr/local/bin/codeybox-exec to exist.");
    }

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
        var stopTimeout = opts.VmStopTimeout > TimeSpan.Zero
            ? opts.VmStopTimeout
            : MultipassSandboxOptions.DefaultVmStopTimeout;
        var deadline = DateTime.UtcNow + stopTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = await RunAsync(opts, [opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct, workItemId: workItemId);
            if (info.ExitCode == 0 && info.Stdout.Contains("Stopped", StringComparison.Ordinal))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
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
        List<(string Host, string Sandbox)> binds,
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        if (binds.Count == 0) return;
        await WaitForStoppedAsync(opts, name, workItemId, ct);
        foreach (var (host, sandbox) in binds)
            await MountSingleBindWithRetryAsync(opts, name, host, sandbox, workItemId, ct);
    }

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
        throw new InvalidOperationException(
            $"multipass mount {host} -> {name}:{sandbox} failed after {attemptsRun} attempt(s): " +
            $"{lastFailure?.Stderr.Trim()} (post-failure host source state: {lastFailureState})");
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
            throw new InvalidOperationException($"multipass start failed: {start.Stderr}");
        await WaitForRunningAsync(opts, name, workItemId, ct);
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
                stdin: null, ct: ctInner),
            _log,
            description: $"multipass transfer env file -> {name}",
            ct);
        if (tx.ExitCode != 0)
            throw new InvalidOperationException($"multipass transfer env file failed: {tx.Stderr}");

        var perms = await RunAsync(
            opts,
            [opts.MultipassBinary, "exec", name, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"],
            stdin: null, ct: ct, workItemId: workItemId);
        if (perms.ExitCode != 0)
            throw new InvalidOperationException($"failed to chmod env file in VM: {perms.Stderr}");

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
        # passes --keep-stdin when ExecAsync.Stdin is non-null.
        keep_stdin=0
        if [ "$1" = "--keep-stdin" ]; then
            keep_stdin=1
            shift
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
            set -a
            . "$2" 2>"$codeybox_err_file"
            codeybox_src_rc=$?
            set +a
            if [ "$codeybox_src_rc" -ne 0 ]; then
                echo "codeybox-exec: failed to source env file '$2' (exit $codeybox_src_rc):" >&2
                cat "$codeybox_err_file" >&2 2>/dev/null
                rm -f "$codeybox_err_file"
                exit 126
            fi
            shift 2
        fi
        rm -f "$codeybox_err_file"
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
            if [ "$keep_stdin" = "1" ]; then
                "$@" 2>&1 | tee -a "$CODEYBOX_AGENT_LOG_FILE"
                codeybox_user_rc=${PIPESTATUS[0]}
            else
                "$@" </dev/null 2>&1 | tee -a "$CODEYBOX_AGENT_LOG_FILE"
                codeybox_user_rc=${PIPESTATUS[0]}
            fi
            # Best-effort sidecar; the orchestrator treats missing file as
            # "not yet finished" so we never silently swallow a write error.
            printf '%s\n' "$codeybox_user_rc" > "$codeybox_exit_file" 2>/dev/null || true
            exit "$codeybox_user_rc"
        fi
        if [ "$keep_stdin" = "1" ]; then
            exec "$@"
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
    ///     install). Don't try to add a separate <c>runcmd:</c> via
    ///     <paramref name="extraCloudInit"/> — cloud-init uses PyYAML, which
    ///     keeps only the LAST occurrence of a duplicated top-level key,
    ///     so a second runcmd block would clobber the orchestrator's runcmd.
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
        bool includeGraphicalInstall = true)
    {
        var wrapperIndented = string.Join("\n      ", ExecWrapperScript.Split('\n'));
        var routeScriptIndented = string.Join("\n      ", RouteSwapScript.Split('\n'));
        var graphicalXvfbIndented = string.Join("\n      ", GraphicalXvfbService.Split('\n'));
        var graphicalXfceIndented = string.Join("\n      ", GraphicalXfceService.Split('\n'));
        var graphicalVncIndented = string.Join("\n      ", GraphicalVncService.Split('\n'));
        var graphicalVncScriptIndented = string.Join("\n      ", GraphicalVncScript.Split('\n'));

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
        sb.AppendLine("    permissions: '0644'");
        sb.AppendLine("    content: |");
        sb.AppendLine("      net.ipv4.tcp_keepalive_time = 30");
        sb.AppendLine("      net.ipv4.tcp_keepalive_intvl = 5");
        sb.AppendLine("      net.ipv4.tcp_keepalive_probes = 3");
        sb.AppendLine("  - path: /usr/local/bin/codeybox-exec");
        sb.AppendLine("    permissions: '0755'");
        sb.AppendLine("    content: |");
        sb.Append("      ").AppendLine(wrapperIndented);
        sb.AppendLine("  - path: /usr/local/sbin/codeybox-route");
        sb.AppendLine("    permissions: '0755'");
        sb.AppendLine("    content: |");
        sb.Append("      ").AppendLine(routeScriptIndented);
        sb.AppendLine("  - path: /etc/systemd/system/codeybox-route.service");
        sb.AppendLine("    permissions: '0644'");
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
            sb.AppendLine("    permissions: '0644'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalXvfbIndented);
            sb.AppendLine("  - path: /etc/systemd/system/codeybox-xfce.service");
            sb.AppendLine("    permissions: '0644'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalXfceIndented);
            sb.AppendLine("  - path: /etc/systemd/system/codeybox-vnc.service");
            sb.AppendLine("    permissions: '0644'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalVncIndented);
            sb.AppendLine("  - path: /usr/local/sbin/codeybox-vnc");
            sb.AppendLine("    permissions: '0755'");
            sb.AppendLine("    content: |");
            sb.Append("      ").AppendLine(graphicalVncScriptIndented);
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
    /// Acquires the provisioning gate, limiting concurrent multipass
    /// launch/start operations to <see cref="MultipassSandboxOptions.MaxConcurrentBoots"/>.
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
    internal async Task<IDisposable> EnterBootGateAsync(MultipassSandboxOptions opts, CancellationToken ct)
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

    private Task<ProcessRunResult> RunAsync(
        MultipassSandboxOptions opts,
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        WorkItemId? workItemId = null) =>
        MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            ctInner => _runner.RunAsync(argv, stdin, ctInner),
            ctInner => MultipassDaemonRetry.ProbeDaemonAsync(
                _runner, opts.MultipassBinary, _daemonRetryPolicy.HealthProbeTimeout, ctInner),
            _log,
            workItemId,
            ct,
            _daemonRetryPolicy);

    private async Task TryDeleteVmAsync(MultipassSandboxOptions opts, string name)
    {
        try
        {
            await RunAsync(opts, [opts.MultipassBinary, "delete", "--purge", name], stdin: null, ct: CancellationToken.None);
        }
        catch { /* best-effort */ }
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
            AuditTransientRetry(workItemId, retryOrdinal, errorClass);

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
        return result with { Stderr = message };
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
        if (stderr.Contains("socket", StringComparison.OrdinalIgnoreCase))
            return "multipass-socket-error";
        return null;
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

    private static void AuditTransientRetry(WorkItemId? workItemId, int attempt, string errorClass)
    {
        if (workItemId.HasValue)
            AuditLog.SandboxLaunchTransientRetry(workItemId.Value, attempt, errorClass);
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
        return result;
    }
}

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
    /// Extra cloud-init YAML appended after the orchestrator's own
    /// directives. Safe for non-runcmd keys (<c>packages:</c>,
    /// <c>write_files:</c>, <c>apt:</c>, etc.). Do NOT use this to add a
    /// <c>runcmd:</c> block — cloud-init's PyYAML parser keeps only the
    /// last occurrence of a duplicated top-level key, so a second runcmd
    /// would clobber the route swap. Use <see cref="ExtraRuncmd"/> for
    /// boot-time commands.
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
    /// to report the VM in the <c>Running</c> state. Defaults to 3 minutes.
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
    /// worker fan-out. Total host budget is
    /// <c>WorkerPool:MaxConcurrentWorkers × BaselineCpus</c> vCPUs and
    /// <c>... × BaselineMemoryGB</c> GiB at saturation.
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
    /// Maximum number of concurrent multipass launch/start operations.
    /// Independent of <c>WorkerPool.MaxConcurrentWorkers</c>: workers can be
    /// running (e.g. agent logic executing inside an already-booted VM) while
    /// only N VMs boot at once, so no individual boot exceeds the 180 s
    /// 'reach Running' timeout due to host CPU/IO contention.
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

internal sealed class MultipassSandbox : IPreemptibleSandbox, ISuspendableSandbox
{
    internal const int ArgvBytesWarningThreshold = 64 * 1024;
    internal const int MaxScreenshotPngBytes = 64 * 1024 * 1024;
    internal const int MaxScreenshotBase64StdoutBytes = ((MaxScreenshotPngBytes + 2) / 3 * 4) + 4096;
    internal const int MaxScreenshotStderrBytes = 64 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

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
    private readonly int _maxScreenshotPngBytes;
    private readonly int _maxScreenshotBase64StdoutBytes;
    private readonly int _maxScreenshotStderrBytes;
    private int _firstExecEmitted;
    private bool _disposed;
    private bool _preserveOnDispose;
    private bool _isSuspended;
    private bool _ownedByShutdownHandler;

    /// <summary>
    /// True once <see cref="SuspendAsync"/> has frozen this VM's RAM via
    /// <c>multipass suspend</c>. PipelineRunner reads this in its host-shutdown
    /// OCE catch block to short-circuit the preempt-checkpoint flow (whose
    /// in-VM <c>git add/commit/push</c> would hang against a frozen VM and
    /// stall the orchestrator's exit).
    /// </summary>
    public bool IsSuspended => _isSuspended;

    /// <summary>
    /// True once the suspend-on-shutdown handler has taken responsibility for
    /// this VM's teardown. Set implicitly when <see cref="SuspendAsync"/> flips
    /// <see cref="IsSuspended"/>; set explicitly by
    /// <see cref="MarkOwnedByShutdownHandler"/> for Stop / Dispose teardown
    /// modes whose teardown call does not go through SuspendAsync. Read by
    /// PipelineRunner to skip the legacy in-VM preempt-checkpoint flow against
    /// a stopped / disposed / frozen VM.
    /// </summary>
    public bool IsOwnedByShutdownHandler => _isSuspended || _ownedByShutdownHandler;

    /// <summary>
    /// Called by <c>SandboxSuspendOnShutdownService</c> before Stop / Dispose
    /// teardown modes begin so PipelineRunner's shutdown catch sees the
    /// "skip in-VM checkpoint" signal even though the suspend path was not
    /// taken. Idempotent; safe to call multiple times.
    /// </summary>
    public void MarkOwnedByShutdownHandler() => _ownedByShutdownHandler = true;

    /// <summary>
    /// RAM size this VM was provisioned with, surfaced so the suspend-on-shutdown
    /// service can scale its per-VM timeout: a larger VM has more RAM to flush to
    /// disk on <c>multipass suspend</c>.
    /// </summary>
    public long? MemoryBytes => _spec.Limits.MemoryBytes;

    public MultipassSandbox(string name, string sandboxRoot, SandboxSpec spec, MultipassSandboxOptions opts, ILogger log,
        ITimingStore? timings = null, WorkItemId timingItemId = default, string timingPhase = "work",
        Action<string>? onDisposed = null, Action<string>? onNoLongerTrackedActive = null, IProcessRunner? runner = null,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null,
        int? maxScreenshotPngBytes = null, int? maxScreenshotStderrBytes = null)
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

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        var result = await ExecRunAsync(exec, ct);
        return new SandboxExecResult(result.ExitCode, result.Stdout, result.Stderr);
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
        var effectiveEnvironment = BuildEffectiveExecEnvironment(exec);
        var wrapped = BuildWrappedInvocation(exec, effectiveEnvironment, extraEnvFile: null);
        var argv = BuildMultipassExecArgv(wrapped);
        var argvBytes = EstimateArgvBytes(argv);
        if (argvBytes > ArgvBytesWarningThreshold)
        {
            _log.LogWarning(
                "Multipass exec argv for {Name} is {Bytes} bytes; routing through transferred files to avoid ARG_MAX",
                _name, argvBytes);

            if (effectiveEnvironment is { Count: > 0 })
            {
                var envFile = await TransferExecEnvironmentAsync(effectiveEnvironment, ct);
                transferredVmPaths.Add(envFile);
                wrapped = BuildWrappedInvocation(exec, effectiveEnvironment, envFile);
                argv = BuildMultipassExecArgv(wrapped);
            }

            if (EstimateArgvBytes(argv) > ArgvBytesWarningThreshold)
            {
                var script = await TransferExecScriptAsync(wrapped, ct);
                transferredVmPaths.Add(script);
                argv = [_opts.MultipassBinary, "exec", _name, "--", "/bin/sh", script];
            }
        }

        var isFirstExec = Interlocked.CompareExchange(ref _firstExecEmitted, 1, 0) == 0;
        TimingScope? firstExecScope = isFirstExec
            ? await TimingScope.BeginAsync(_timings, _timingItemId, _timingPhase, "vm.exec_first", log: _log)
            : null;
        try
        {
            return await MultipassRetry.RunWithRetryAsync(
                action: innerCt => RunMultipassAsync(
                    argv,
                    exec.Stdin,
                    innerCt,
                    exec.StdoutChunkCallback,
                    exec.StderrChunkCallback,
                    maxStdoutBytes,
                    maxStderrBytes),
                log: _log,
                description: $"exec on {_name}",
                ct: ct);
        }
        finally
        {
            if (transferredVmPaths.Count > 0)
                await TryRemoveTransferredFilesAsync(transferredVmPaths);
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

    private List<string> BuildWrappedInvocation(
        SandboxExec exec,
        IReadOnlyDictionary<string, string>? effectiveEnvironment,
        string? extraEnvFile)
    {
        // The codeybox-exec wrapper closes stdin by default to prevent
        // tools that read stdin from hanging the sandbox. When the
        // orchestrator deliberately pipes stdin, pass --keep-stdin first.
        var wrapped = new List<string> { "/usr/local/bin/codeybox-exec" };
        if (exec.Stdin is not null)
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

    internal static string BuildExecScript(IReadOnlyList<string> wrapped)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\nexec");
        foreach (var arg in wrapped)
            sb.Append(' ').Append(MultipassSandboxProvider.ShellSingleQuote(arg));
        sb.Append('\n');
        return sb.ToString();
    }

    private async Task TransferFileToVmAsync(string hostPath, string vmRelativePath, string description, CancellationToken ct)
    {
        var tx = await MultipassRetry.RunWithRetryAsync(
            ctInner => _runner.RunAsync(
                [_opts.MultipassBinary, "transfer", hostPath, $"{_name}:{vmRelativePath}"],
                stdin: null,
                ct: ctInner),
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

    private Task<ProcessRunResult> RunMultipassAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null) =>
        MultipassDaemonRetry.RunWithRetryAsync(
            argv,
            ctInner => _runner.RunAsync(argv, stdin, ctInner, stdoutChunkCallback, stderrChunkCallback, maxStdoutBytes, maxStderrBytes),
            ctInner => MultipassDaemonRetry.ProbeDaemonAsync(
                _runner, _opts.MultipassBinary, _daemonRetryPolicy.HealthProbeTimeout, ctInner),
            _log,
            _workItemId,
            ct,
            _daemonRetryPolicy);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_preserveOnDispose)
        {
            _disposed = true;
            return;
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
            _disposed = true;
            try
            {
                _onNoLongerTrackedActive?.Invoke(_name);
            }
            catch (Exception callbackEx)
            {
                _log.LogWarning(callbackEx, "Failed to release active tracking for multipass VM {Name}", _name);
            }
            return;
        }
        _disposed = true;
        _onDisposed?.Invoke(_name);
        AuditLog.SandboxDisposed(_name);
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clean sandbox root {Root}", _sandboxRoot); }
    }

    public async Task StopAndPreserveAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        _preserveOnDispose = true;
        var markerPath = Path.Combine(_sandboxRoot, ".codeybox-preempt");
        try
        {
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write preempt marker for multipass VM {Name}", _name);
        }

        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _opts.MultipassBinary,
                ArgumentList = { "stop", _name },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is not null)
            {
                _ = await p.StandardOutput.ReadToEndAsync(ct);
                _ = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to stop multipass VM {Name} for preemption", _name);
        }
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
        var markerPath = Path.Combine(_sandboxRoot, ".codeybox-preempt");
        try
        {
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write preempt marker for suspended multipass VM {Name}", _name);
        }

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
            // (SandboxSuspendOnShutdownService.SuspendOneAsync) keeps the
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
