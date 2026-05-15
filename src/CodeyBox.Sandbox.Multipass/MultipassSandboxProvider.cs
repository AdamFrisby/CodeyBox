using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
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
public sealed class MultipassSandboxProvider : ISandboxProvider
{
    private readonly MultipassSandboxOptions _opts;
    private readonly ILogger<MultipassSandboxProvider> _log;
    private readonly IProcessRunner _runner;
    private readonly string _stagingRoot;
    private readonly ITimingStore? _timings;
    // Per-baseline-name semaphore: serialises bake operations so two
    // concurrent CreateAsync calls for the same profile don't both try to
    // launch the same baseline VM. Lazily populated.
    private readonly Dictionary<string, SemaphoreSlim> _baselineLocks = new();
    private readonly object _baselineLocksGuard = new();

    // Tracks sandboxes created by the current process that haven't been disposed.
    // Used by ListAllManagedAsync to compute ManagedSandboxInfo.IsTrackedActive.
    private readonly ConcurrentDictionary<string, bool> _activeSandboxNames = new(StringComparer.Ordinal);

    // Cache for ListAllManagedAsync results to avoid hammering multipassd.
    private IReadOnlyList<ManagedSandboxInfo>? _listCache;
    private DateTimeOffset _listCacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _listCacheTtl = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _listLock = new(1, 1);

    public MultipassSandboxProvider(MultipassSandboxOptions opts, ILogger<MultipassSandboxProvider> log,
        ITimingStore? timings = null)
        : this(opts, log, timings, new DefaultProcessRunner())
    {
    }

    internal MultipassSandboxProvider(MultipassSandboxOptions opts, ILogger<MultipassSandboxProvider> log,
        ITimingStore? timings, IProcessRunner runner)
    {
        _opts = opts;
        _log = log;
        _runner = runner;
        _timings = timings;
        _stagingRoot = ResolveStagingRoot(opts);
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

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        EnsureGraphicalProfileSelected(spec);
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
        var timingPhase = spec.TimingPhase ?? "work";

        try
        {
            // Choose between two boot paths:
            //   - Baseline-clone path (UseBaselineImages=true + profile is set):
            //     bake one VM per profile lazily on first use, then `multipass
            //     clone` from it for every subsequent sandbox. Pays the
            //     install runcmd cost once per profile instead of per sandbox.
            //   - Launch path (default): every VM goes through cloud-init.
            //     Slower per VM but works without prior baking.
            var useBaseline = _opts.UseBaselineImages
                && !string.IsNullOrWhiteSpace(spec.Network.ProfileName);

            // After this block the VM is in Stopped state, ready for native
            // mounts. The launch path goes through a stop-mount-start cycle;
            // the clone path skips the start (clone is born Stopped).
            if (useBaseline)
            {
                var baselineName = await EnsureBaselineForProfileAsync(spec.Network.ProfileName!, spec.Flavor, ct);
                await using var cloneScope = await TimingScope.BeginAsync(
                    timingStore, timingItemId, timingPhase, "vm.clone", log: _log);
                await CloneFromBaselineAsync(name, baselineName, ct);
                // Clone is Stopped after `multipass clone`; no start yet.
            }
            else
            {
                var cloudInit = BuildCloudInit(_opts.ExtraRuncmd, _opts.ExtraCloudInit, spec.Flavor);
                var cloudInitPath = Path.Combine(sandboxRoot, "cloud-init.yaml");
                await File.WriteAllTextAsync(cloudInitPath, cloudInit, ct);
                await using (var launchScope = await TimingScope.BeginAsync(
                    timingStore, timingItemId, timingPhase, "vm.launch", log: _log))
                {
                    await LaunchAsync(name, spec, cloudInitPath, ct);
                    await WaitForRunningAsync(name, ct);
                }
                // Stop the freshly-launched VM so we can mount (outside vm.launch scope).
                var stop = await RunAsync([_opts.MultipassBinary, "stop", name], stdin: null, ct: ct);
                if (stop.ExitCode != 0)
                    throw new InvalidOperationException($"multipass stop (for mount) failed: {stop.Stderr}");
                await WaitForStoppedAsync(name, ct);
            }

            // Apply native mounts while VM is Stopped, then start.
            await using (var mountScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.mount", log: _log))
            {
                await ApplyMountsAsync(name, bindMounts, ct);
            }

            await using (var startScope = await TimingScope.BeginAsync(
                timingStore, timingItemId, timingPhase, "vm.start", log: _log))
            {
                await StartAndWaitForRunningAsync(name, ct);
            }

            await TransferEnvAsync(name, spec.Environment, sandboxRoot, ct);
            AuditLog.SandboxCreated(name, spec.Network.ProfileName);
            // The exec wrapper is installed by cloud-init at boot
            // (see BuildCloudInit's write_files); on the clone path it's
            // already baked into the source VM's filesystem, so the clone
            // inherits it. The codeybox-route systemd service runs on every
            // boot in both paths.
            _activeSandboxNames[name] = true;
            // Invalidate the list cache so the next ListAllManagedAsync call reflects
            // the newly created sandbox immediately rather than serving stale data.
            _listCacheExpiry = DateTimeOffset.MinValue;
            return new MultipassSandbox(name, sandboxRoot, spec, _opts, _log, timingStore, timingItemId, timingPhase,
                onDisposed: n => { _activeSandboxNames.TryRemove(n, out _); _listCacheExpiry = DateTimeOffset.MinValue; },
                runner: _runner);
        }
        catch
        {
            // Best-effort cleanup if launch / mount / transfer half-succeeded.
            await TryDeleteVmAsync(name);
            try { Directory.Delete(sandboxRoot, recursive: true); } catch { }
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        await _listLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_listCache is not null && now < _listCacheExpiry)
                return _listCache;

            var result = await FetchManagedSandboxesAsync(ct);
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
    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        // Explicit allowlist before any filesystem or shell operation: VM names must
        // be alphanumeric-and-hyphen only. This blocks path-traversal strings such as
        // "codeybox-a/../../../sensitive" that start with the required prefix but
        // would escape _stagingRoot once Path.Combine resolves them.
        if (!IsValidSandboxName(name))
            throw new ArgumentException($"Sandbox name '{name}' contains invalid characters (only [a-z0-9-] allowed).", nameof(name));

        _log.LogInformation("SandboxLeakReaper: purging leaked VM {Name}", name);
        var run = await RunAsync([_opts.MultipassBinary, "delete", "--purge", name], stdin: null, ct: ct);
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

    private async Task<IReadOnlyList<ManagedSandboxInfo>> FetchManagedSandboxesAsync(CancellationToken ct)
    {
        var listRun = await RunAsync([_opts.MultipassBinary, "list", "--format", "json"], stdin: null, ct: ct);
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

        // Fetch disk usage for all discovered codeybox VMs in a single multipass info call.
        var diskByName = await FetchDiskInfoAsync(vmNames, ct);

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
            var isActive = _activeSandboxNames.ContainsKey(name);
            var hasPreemptMarker = File.Exists(Path.Combine(stagingDir, ".codeybox-preempt"));
            diskByName.TryGetValue(name, out var diskBytes);
            infos.Add(new ManagedSandboxInfo(name, createdAt, diskBytes > 0 ? diskBytes : null, isActive, hasPreemptMarker));
        }
        return infos;
    }

    /// <summary>
    /// Runs <c>multipass info --format json</c> for the given VM names and returns
    /// a map of VM name → disk-used bytes. Returns an empty dictionary on any failure
    /// so that missing disk info degrades gracefully to null in the caller.
    /// </summary>
    private async Task<Dictionary<string, long>> FetchDiskInfoAsync(List<string> names, CancellationToken ct)
    {
        var argv = new List<string> { _opts.MultipassBinary, "info", "--format", "json" };
        argv.AddRange(names);

        var run = await RunAsync(argv, stdin: null, ct: ct);
        if (run.ExitCode != 0)
        {
            _log.LogWarning("multipass info failed (exit {ExitCode}): {Stderr}", run.ExitCode, run.Stderr);
            return [];
        }

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(run.Stdout);
            if (!doc.RootElement.TryGetProperty("info", out var infoEl))
                return result;

            foreach (var vmEntry in infoEl.EnumerateObject())
            {
                if (!vmEntry.Value.TryGetProperty("disks", out var disksEl)) continue;
                long total = 0;
                foreach (var diskEntry in disksEl.EnumerateObject())
                {
                    if (diskEntry.Value.TryGetProperty("used", out var usedEl) &&
                        long.TryParse(usedEl.GetString(), out var used))
                        total += used;
                }
                if (total > 0)
                    result[vmEntry.Name] = total;
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse multipass info output; disk sizes will be omitted");
        }
        return result;
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
    /// Ensures the baseline VM for <paramref name="profileName"/> exists.
    /// Bakes it on first call (~5-10 min: launch with cloud-init, install
    /// agent CLIs and runtime, stop). Subsequent calls return the existing
    /// baseline name.
    ///
    /// We bake one baseline per profile because <c>multipass clone</c>
    /// inherits the source VM's network attachments — a baseline launched
    /// with <c>--network cb-net</c> can only produce clones attached to
    /// <c>cb-net</c>. Per-profile baselines also cleanly isolate "what each
    /// profile installed" if profiles ever need different toolchains.
    /// </summary>
    private async Task<string> EnsureBaselineForProfileAsync(
        string profileName,
        SandboxProfileFlavor flavor,
        CancellationToken ct)
    {
        var baselineProfile = flavor == SandboxProfileFlavor.Graphical
            ? SandboxConventions.GraphicalNetworkProfile
            : profileName;

        if (!_opts.NetworkProfiles.TryGetValue(baselineProfile, out _))
            throw new InvalidOperationException(
                $"Network profile '{baselineProfile}' is not configured in MultipassSandboxOptions.NetworkProfiles. " +
                $"Configured profiles: [{string.Join(", ", _opts.NetworkProfiles.Keys)}]");

        var baselineName = _opts.BaselineNamePrefix + baselineProfile;
        // multipass instance names cap at 24 chars; trim if a long profile
        // name pushes us over. We use a STABLE hash (SHA-256, first 6 hex
        // chars) for uniqueness so two long profile names don't collide.
        // string.GetHashCode() can't be used here — it's randomised per
        // process, so each orchestrator restart would produce a different
        // baseline name and re-bake unnecessarily.
        if (baselineName.Length > 24)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(baselineName);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            var hex = Convert.ToHexString(hash.AsSpan(0, 3)).ToLowerInvariant();
            baselineName = string.Concat(baselineName.AsSpan(0, 16), "-", hex);
        }

        var sem = GetBaselineLock(baselineName);
        await sem.WaitAsync(ct);
        try
        {
            if (await BaselineVmExistsAsync(baselineName, ct))
                return baselineName;
            await BakeBaselineAsync(baselineName, baselineProfile, flavor, ct);
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

    private async Task<bool> BaselineVmExistsAsync(string name, CancellationToken ct)
    {
        var info = await RunAsync([_opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct);
        return info.ExitCode == 0;
    }

    private async Task BakeBaselineAsync(
        string baselineName,
        string profileName,
        SandboxProfileFlavor flavor,
        CancellationToken ct)
    {
        var bridge = _opts.NetworkProfiles[profileName];
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
        var cloudInit = BuildCloudInit(extraRuncmd: null, _opts.ExtraCloudInit, flavor);
        var stagingDir = Path.Combine(_stagingRoot, "_baseline-" + baselineName);
        Directory.CreateDirectory(stagingDir);
        TryChmod0700(stagingDir);
        var cloudInitPath = Path.Combine(stagingDir, "cloud-init.yaml");
        await File.WriteAllTextAsync(cloudInitPath, cloudInit, ct);

        var argv = new List<string> {
            _opts.MultipassBinary, "launch", "--name", baselineName,
            "--cloud-init", cloudInitPath,
            "--network", $"name={bridge},mode=auto",
            // Multipass defaults (5G disk / 1G RAM / 1 vCPU) are tight for
            // a typical project install — language toolchain + agent CLI +
            // any auditor binaries. Defaults here are operator-tunable via
            // BaselineDiskGB / BaselineMemoryGB / BaselineCpus options;
            // raise them if your install runs OOM or run out of disk
            // mid-bake. qcow2 disks are sparse so unused disk space costs
            // nothing on the host until written.
            "--disk", $"{_opts.BaselineDiskGB}G",
            "--memory", $"{_opts.BaselineMemoryGB}G",
            "--cpus", _opts.BaselineCpus.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(_opts.DefaultImage))
            argv.Add(_opts.DefaultImage);

        try
        {
            var run = await RunAsync(argv, stdin: null, ct: ct);
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"baseline launch failed: {run.Stderr}");

            // Wait for the (now-minimal) cloud-init to finish — write_files
            // and the route service install. Doesn't include the heavy
            // installs, so should be fast.
            await WaitForRunningAsync(baselineName, ct);

            // Run the install commands now, via multipass exec under sudo.
            // Each entry in ExtraRuncmd is a single shell command.
            var installCommands = BuildFirstBootRuncmd(flavor);
            for (var i = 0; i < installCommands.Count; i++)
            {
                var cmd = installCommands[i];
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                _log.LogInformation("Baseline install step {N}/{Total}", i + 1, installCommands.Count);
                var execRun = await RunAsync(
                    [_opts.MultipassBinary, "exec", baselineName, "--", "sudo", "bash", "-c", cmd],
                    stdin: null, ct: ct);
                if (execRun.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"baseline install step {i + 1} failed (exit {execRun.ExitCode}):\n" +
                        $"stderr: {execRun.Stderr}\nstdout-tail: {(execRun.Stdout.Length > 1000 ? "…" + execRun.Stdout[^1000..] : execRun.Stdout)}");
            }

            // Stop the baseline so `multipass clone` can use it as a source
            // (clone requires source stopped). Wait for the state to flip
            // so a subsequent clone doesn't race a still-Stopping VM.
            var stop = await RunAsync([_opts.MultipassBinary, "stop", baselineName], stdin: null, ct: ct);
            if (stop.ExitCode != 0)
                throw new InvalidOperationException($"baseline stop failed: {stop.Stderr}");
            await WaitForStoppedAsync(baselineName, ct);

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

    private async Task CloneFromBaselineAsync(string newName, string baselineName, CancellationToken ct)
    {
        // Defensive: ensure source is fully stopped before clone. Multipass
        // clone requires it, but the baseline can get inadvertently
        // restarted (e.g. operator runs `multipass exec` against it, which
        // auto-starts stopped instances). Stop is idempotent — exits 0 if
        // already stopped — and we wait for the state to flip because
        // `multipass stop` returns when the request is queued.
        await RunAsync([_opts.MultipassBinary, "stop", baselineName], stdin: null, ct: ct);
        await WaitForStoppedAsync(baselineName, ct);

        _log.LogInformation("Cloning {New} from baseline {Baseline}", newName, baselineName);
        var clone = await RunAsync(
            [_opts.MultipassBinary, "clone", baselineName, "--name", newName],
            stdin: null, ct: ct);
        if (clone.ExitCode != 0)
            throw new InvalidOperationException($"multipass clone failed: {clone.Stderr}");

        // NOTE: deliberately do NOT start the clone here. multipass clone
        // creates the new VM in Stopped state, which is exactly what
        // SetUpMountsAsync's `mount --type=native` requires. Starting now
        // and stopping again later created a stop-state race where the
        // mount could fire before multipassd had fully released the VM.
    }

    internal IReadOnlyList<string> BuildLaunchArgv(string name, SandboxSpec spec, string cloudInitPath)
    {
        EnsureGraphicalProfileSelected(spec);
        var argv = new List<string> { _opts.MultipassBinary, "launch", "--name", name };
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
            if (!_opts.NetworkProfiles.TryGetValue(spec.Network.ProfileName, out var bridge))
                throw new InvalidOperationException(
                    $"Network profile '{spec.Network.ProfileName}' is not configured in MultipassSandboxOptions.NetworkProfiles. " +
                    $"Configured profiles: [{string.Join(", ", _opts.NetworkProfiles.Keys)}]. " +
                    "Either add the profile to options or run setup-host-networks.sh and update appsettings.");
            argv.AddRange(["--network", $"name={bridge},mode=auto"]);
        }

        // ImageReference: empty/null => multipass picks the default image.
        if (!string.IsNullOrWhiteSpace(spec.ImageReference) && spec.ImageReference != "ignored")
            argv.Add(spec.ImageReference);
        else if (!string.IsNullOrWhiteSpace(_opts.DefaultImage))
            argv.Add(_opts.DefaultImage);

        return argv;
    }

    private static void EnsureGraphicalProfileSelected(SandboxSpec spec)
    {
        if (spec.Flavor != SandboxProfileFlavor.Graphical)
            return;

        if (string.Equals(spec.Network.ProfileName, SandboxConventions.GraphicalNetworkProfile, StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            $"Graphical sandboxes must use network profile '{SandboxConventions.GraphicalNetworkProfile}'. " +
            $"The requested profile was '{spec.Network.ProfileName ?? "<none>"}'.");
    }

    private async Task LaunchAsync(string name, SandboxSpec spec, string cloudInitPath, CancellationToken ct)
    {
        var argv = BuildLaunchArgv(name, spec, cloudInitPath);
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
            _log.LogInformation("Sandbox {Name}: host-enforced network profile {Profile}", name, spec.Network.ProfileName);
        _log.LogInformation("Launching multipass VM {Name} (this takes 10-30s)", name);
        var run = await RunAsync(argv, stdin: null, ct: ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"multipass launch failed: {run.Stderr}");
    }

    private async Task WaitForRunningAsync(string name, CancellationToken ct)
    {
        // Two waits: first the VM enters "Running" state, then cloud-init
        // finishes applying runcmd (which installs the exec wrapper and
        // swaps the default route to the profile bridge). The exec
        // wrapper is needed before any ExecAsync; the route swap is
        // needed before any agent traffic actually leaves the VM via
        // the host-enforced bridge.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = await RunAsync([_opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct);
            if (info.ExitCode == 0 && info.Stdout.Contains("Running", StringComparison.Ordinal))
                break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (DateTime.UtcNow >= deadline)
            throw new InvalidOperationException($"multipass VM {name} did not reach Running state within 3 minutes");

        // `cloud-init status --wait` blocks until cloud-init has finished
        // (success or fail). Exit code is non-zero on failure; we don't
        // distinguish here because the post-launch verification (mount,
        // exec) will surface concrete problems.
        await RunAsync(
            [_opts.MultipassBinary, "exec", name, "--", "cloud-init", "status", "--wait"],
            stdin: null, ct: ct);
    }

    /// <summary>
    /// Polls `multipass info` until the VM's State is "Stopped". Multipass
    /// returns from `multipass stop` once the request is queued, but the
    /// State doesn't flip to Stopped until the QEMU process is fully gone
    /// — and `multipass mount --type=native` rejects any other state
    /// with "Please stop the instance ... before attempting native mounts".
    /// </summary>
    private async Task WaitForStoppedAsync(string name, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var info = await RunAsync([_opts.MultipassBinary, "info", name, "--format=csv"], stdin: null, ct: ct);
            if (info.ExitCode == 0 && info.Stdout.Contains("Stopped", StringComparison.Ordinal))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new InvalidOperationException($"multipass VM {name} did not reach Stopped state within 2 minutes");
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
    private async Task ApplyMountsAsync(string name, List<(string Host, string Sandbox)> binds, CancellationToken ct)
    {
        if (binds.Count == 0) return;
        await WaitForStoppedAsync(name, ct);
        foreach (var (host, sandbox) in binds)
        {
            var run = await RunAsync(
                [_opts.MultipassBinary, "mount", "--type=native", host, $"{name}:{sandbox}"],
                stdin: null, ct: ct);
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"multipass mount {host} -> {name}:{sandbox} failed: {run.Stderr}");
        }
    }

    private async Task StartAndWaitForRunningAsync(string name, CancellationToken ct)
    {
        var start = await RunAsync([_opts.MultipassBinary, "start", name], stdin: null, ct: ct);
        if (start.ExitCode != 0)
            throw new InvalidOperationException($"multipass start failed: {start.Stderr}");
        await WaitForRunningAsync(name, ct);
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
    private async Task<string> TransferEnvAsync(string name, IReadOnlyDictionary<string, string> env, string sandboxRoot, CancellationToken ct)
    {
        var envPath = Path.Combine(sandboxRoot, "env");
        await File.WriteAllTextAsync(envPath, BuildEnvironmentFileContent(env), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(envPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var tx = await MultipassRetry.RunWithRetryAsync(
            ctInner => RunAsync(
                [_opts.MultipassBinary, "transfer", envPath, $"{name}:.codeybox-env"],
                stdin: null, ct: ctInner),
            _log,
            description: $"multipass transfer env file -> {name}",
            ct);
        if (tx.ExitCode != 0)
            throw new InvalidOperationException($"multipass transfer env file failed: {tx.Stderr}");

        var perms = await RunAsync(
            [_opts.MultipassBinary, "exec", name, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"],
            stdin: null, ct: ct);
        if (perms.ExitCode != 0)
            throw new InvalidOperationException($"failed to chmod env file in VM: {perms.Stderr}");

        return "/home/ubuntu/.codeybox-env";
    }

    internal static string BuildEnvironmentFileContent(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in env)
        {
            if (k.Contains('=') || k.Contains('\n'))
                throw new ArgumentException($"Invalid env key: {k}");
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
    /// </summary>
    private const string ExecWrapperScript = """
        #!/bin/sh
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
        cd "$1" || exit 127
        shift
        if [ "${1:-}" = "--env-file" ]; then
            [ "$#" -ge 2 ] || exit 127
            set -a
            . "$2" || exit 126
            set +a
            shift 2
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
        listen_addr=$(ip -4 -o addr show | awk '/inet 10\.99\./{split($4,a,"/"); print a[1]; exit}')
        if [ -z "${listen_addr:-}" ]; then
            echo "codeybox-vnc: no 10.99.x.x interface present" >&2
            exit 1
        fi
        host_addr=$(printf '%s\n' "$listen_addr" | awk -F. '{print $1"."$2"."$3".1"}')
        exec /usr/bin/x11vnc -display :0 -rfbport {{SandboxConventions.GraphicalVncPort}} -forever -shared -nopw -listen "$listen_addr" -allow "$host_addr" -noxdamage -repeat
        """;

    private const string GraphicalInstallRuncmd = """
        set -eux
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y --no-install-recommends xvfb x11vnc xfce4 xfce4-terminal dbus-x11 xdotool scrot ffmpeg x11-utils
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
        SandboxProfileFlavor flavor = SandboxProfileFlavor.Headless)
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
        // first boot's traffic uses the profile bridge before any
        // extraRuncmd installs run.
        sb.AppendLine("  - systemctl daemon-reload");
        sb.AppendLine("  - systemctl enable --now codeybox-route.service");
        // Splice caller-supplied runcmd entries into the same block, AFTER
        // the route swap so they have working egress.
        if (extraRuncmd is not null)
        {
            var commands = flavor == SandboxProfileFlavor.Graphical
                ? new[] { GraphicalInstallRuncmd }.Concat(extraRuncmd)
                : extraRuncmd;
            foreach (var cmd in commands)
            {
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                // Each entry is a single shell command. We use the YAML
                // block-literal form (`- |`) so multi-line commands work
                // and we don't have to worry about escaping.
                sb.AppendLine("  - |");
                foreach (var line in cmd.Split('\n'))
                    sb.Append("      ").AppendLine(line);
            }
        }
        if (!string.IsNullOrWhiteSpace(extraCloudInit))
        {
            sb.AppendLine();
            sb.AppendLine("# --- extra cloud-init from MultipassSandboxOptions.ExtraCloudInit ---");
            sb.AppendLine(extraCloudInit);
        }
        return sb.ToString();
    }

    private IReadOnlyList<string> BuildFirstBootRuncmd(SandboxProfileFlavor flavor)
    {
        if (flavor != SandboxProfileFlavor.Graphical)
            return _opts.ExtraRuncmd;

        var commands = new List<string>(_opts.ExtraRuncmd.Count + 1)
        {
            GraphicalInstallRuncmd,
        };
        commands.AddRange(_opts.ExtraRuncmd);
        return commands;
    }

    private async Task<RunResult> RunAsync(IReadOnlyList<string> argv, string? stdin, CancellationToken ct)
    {
        return await _runner.RunAsync(argv, stdin, ct);
    }

    private async Task TryDeleteVmAsync(string name)
    {
        try
        {
            await RunAsync([_opts.MultipassBinary, "delete", "--purge", name], stdin: null, ct: CancellationToken.None);
        }
        catch { /* best-effort */ }
    }
}

internal interface IProcessRunner
{
    Task<RunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null);
}

internal sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<RunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var streamChunks = stdoutChunkCallback is not null || stderrChunkCallback is not null;
        if (streamChunks)
        {
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data + "\n";
                stdout.Append(line);
                stdoutChunkCallback?.Invoke(line);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data + "\n";
                stderr.Append(line);
                stderrChunkCallback?.Invoke(line);
            };
        }

        p.Start();
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        if (streamChunks)
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        else
        {
            stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            stderrTask = p.StandardError.ReadToEndAsync(ct);
        }
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (stdoutTask is not null && stderrTask is not null)
            return new RunResult(p.ExitCode, await stdoutTask, await stderrTask);
        return new RunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

internal readonly record struct RunResult(int ExitCode, string Stdout, string Stderr);

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
    /// indicates SSH-not-ready. Returns the final <see cref="RunResult"/> —
    /// the caller is responsible for translating a non-zero ExitCode into
    /// an exception. Non-retryable failures (any non-zero exit whose stderr
    /// is NOT a known SSH-not-ready signature) short-circuit immediately.
    ///
    /// <para>For tests, pass <paramref name="delay"/> and <paramref name="backoff"/>
    /// to avoid sleeping; production callers use the defaults.</para>
    /// </summary>
    internal static async Task<RunResult> RunWithRetryAsync(
        Func<CancellationToken, Task<RunResult>> action,
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

        RunResult result = default;
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
}

internal sealed class MultipassSandbox : IPreemptibleSandbox
{
    internal const int ArgvBytesWarningThreshold = 64 * 1024;

    private readonly string _name;
    private readonly string _sandboxRoot;
    private readonly SandboxSpec _spec;
    private readonly MultipassSandboxOptions _opts;
    private readonly ILogger _log;
    private readonly IProcessRunner _runner;
    private readonly ITimingStore? _timings;
    private readonly WorkItemId _timingItemId;
    private readonly string _timingPhase;
    private readonly Action<string>? _onDisposed;
    private int _firstExecEmitted;
    private bool _disposed;
    private bool _preserveOnDispose;

    public MultipassSandbox(string name, string sandboxRoot, SandboxSpec spec, MultipassSandboxOptions opts, ILogger log,
        ITimingStore? timings = null, WorkItemId timingItemId = default, string timingPhase = "work",
        Action<string>? onDisposed = null, IProcessRunner? runner = null)
    {
        _name = name;
        _sandboxRoot = sandboxRoot;
        _spec = spec;
        _opts = opts;
        _log = log;
        _runner = runner ?? new DefaultProcessRunner();
        _timings = timings;
        _timingItemId = timingItemId;
        _timingPhase = timingPhase;
        _onDisposed = onDisposed;
        Id = name;
    }

    public string Id { get; }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (exec.Argv.Count == 0) throw new ArgumentException("Argv must be non-empty", nameof(exec));

        var transferredVmPaths = new List<string>();
        var wrapped = BuildWrappedInvocation(exec, extraEnvFile: null);
        var argv = BuildMultipassExecArgv(wrapped);
        var argvBytes = EstimateArgvBytes(argv);
        if (argvBytes > ArgvBytesWarningThreshold)
        {
            _log.LogWarning(
                "Multipass exec argv for {Name} is {Bytes} bytes; routing through transferred files to avoid ARG_MAX",
                _name, argvBytes);

            if (exec.ExtraEnvironment is { Count: > 0 })
            {
                var envFile = await TransferExecEnvironmentAsync(exec.ExtraEnvironment, ct);
                transferredVmPaths.Add(envFile);
                wrapped = BuildWrappedInvocation(exec, envFile);
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
            var result = await _runner.RunAsync(
                argv,
                exec.Stdin,
                ct,
                exec.StdoutChunkCallback,
                exec.StderrChunkCallback);
            return new SandboxExecResult(result.ExitCode, result.Stdout, result.Stderr);
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
        var result = await ExecAsync(new SandboxExec
        {
            Argv =
            [
                "sh", "-lc",
                "tmp=$(mktemp --suffix=.png); trap 'rm -f \"$tmp\"' EXIT; DISPLAY=:0 scrot -z \"$tmp\"; base64 -w0 \"$tmp\"",
            ],
            WorkingDirectory = _spec.WorkingDirectory,
        }, ct);

        if (!result.Success)
            throw new InvalidOperationException($"graphical screenshot failed: {result.Stderr}");

        try
        {
            return Convert.FromBase64String(result.Stdout.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("graphical screenshot command returned invalid base64", ex);
        }
    }

    public async Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
    {
        EnsureGraphical();
        ArgumentNullException.ThrowIfNull(events);
        foreach (var inputEvent in events)
        {
            var argv = BuildXdotoolArgv(inputEvent);
            if (argv.Count == 0)
                continue;

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
            "env",
            $"DISPLAY={SandboxConventions.GraphicalDisplay}",
            "xdotool",
        };

        switch (inputEvent.Type)
        {
            case SandboxInputEventType.Click:
                if (inputEvent.X.HasValue != inputEvent.Y.HasValue)
                    throw new ArgumentException("Click events must provide both X and Y, or neither.");
                if (inputEvent.X is { } clickX && inputEvent.Y is { } clickY)
                    argv.AddRange(["mousemove", "--sync", clickX.ToString(), clickY.ToString()]);
                argv.AddRange(["click", "1"]);
                return argv;

            case SandboxInputEventType.Key:
                if (string.IsNullOrWhiteSpace(inputEvent.Key))
                    throw new ArgumentException("Key events require Key.");
                argv.AddRange(["key", "--clearmodifiers", inputEvent.Key]);
                return argv;

            case SandboxInputEventType.Move:
                if (inputEvent.X is not { } moveX || inputEvent.Y is not { } moveY)
                    throw new ArgumentException("Move events require X and Y.");
                argv.AddRange(["mousemove", "--sync", moveX.ToString(), moveY.ToString()]);
                return argv;

            case SandboxInputEventType.Scroll:
                return BuildScrollArgv(argv, inputEvent);

            case SandboxInputEventType.Type:
                if (inputEvent.Text is null)
                    throw new ArgumentException("Type events require Text.");
                argv.AddRange(["type", "--clearmodifiers", "--delay", "0", "--", inputEvent.Text]);
                return argv;

            default:
                throw new ArgumentOutOfRangeException(nameof(inputEvent), inputEvent.Type, "Unknown input event type.");
        }
    }

    private static IReadOnlyList<string> BuildScrollArgv(List<string> argv, SandboxInputEvent inputEvent)
    {
        var vertical = inputEvent.Y ?? 0;
        var horizontal = inputEvent.X ?? 0;
        if (vertical == 0 && horizontal == 0)
            return [];

        if (vertical != 0 && horizontal != 0)
            throw new ArgumentException("Scroll events support one axis at a time.");

        var amount = Math.Abs(vertical != 0 ? vertical : horizontal);
        if (amount > 1000)
            throw new ArgumentOutOfRangeException(nameof(inputEvent), "Scroll amount must be <= 1000.");

        var button = vertical switch
        {
            < 0 => "4",
            > 0 => "5",
            _ => horizontal < 0 ? "6" : "7",
        };
        argv.AddRange(["click", "--repeat", amount.ToString(), button]);
        return argv;
    }

    private List<string> BuildWrappedInvocation(SandboxExec exec, string? extraEnvFile)
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
        else if (exec.ExtraEnvironment is { Count: > 0 })
        {
            // env(1) takes KEY=VALUE pairs followed by the command. This
            // keeps the common case small and preserves historical ordering.
            wrapped.Add("env");
            foreach (var (k, v) in exec.ExtraEnvironment)
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
        var result = await _runner.RunAsync(
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
            _ = await _runner.RunAsync(
                [_opts.MultipassBinary, "exec", _name, "--", "rm", "-f", .. vmPaths],
                stdin: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to clean transferred multipass exec files for {Name}", _name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_preserveOnDispose) return;
        // Notify provider immediately so it removes the name from the active set and
        // invalidates the list cache before any subsequent leak scan runs.
        _onDisposed?.Invoke(_name);
        AuditLog.SandboxDisposed(_name);
        await using var disposeScope = await TimingScope.BeginAsync(
            _timings, _timingItemId, _timingPhase, "vm.dispose", log: _log);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = _opts.MultipassBinary,
                ArgumentList = { "delete", "--purge", _name },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is not null)
            {
                _ = await p.StandardOutput.ReadToEndAsync();
                _ = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete multipass VM {Name}", _name);
        }
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
            using var p = Process.Start(new ProcessStartInfo
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
}
