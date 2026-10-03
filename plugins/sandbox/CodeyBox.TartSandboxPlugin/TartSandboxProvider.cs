using System.Collections.Concurrent;
using System.Security.Cryptography;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.TartSandboxPlugin;

/// <summary>
/// CodeyBox sandbox provider plugin for Tart: macOS (and Linux) VMs on Apple
/// Silicon hosts, driven over the local <c>tart</c> CLI with guest access over
/// SSH. Disabled unless the operator allowlists and enables
/// <c>codeybox.tart-sandbox</c>. This is the first credible path to macOS and
/// Xcode workers.
///
/// <para>Containment posture (host-owned, not claimed here): Tart runs on a
/// macOS host where the nftables egress guarantee cannot exist, so the host
/// classifies every plugin kind
/// <see cref="EgressEnforcementLocation.NotEnforced"/>. This provider may
/// only serve sandboxes with no named network profile; profiled work is
/// refused by placement before it reaches this provider, and
/// <see cref="CreateAsync"/> refuses it again at the sink. It must never be
/// described as isolation.</para>
///
/// <para>Credentials come from the credential chain (the guest SSH password
/// is resolved from <see cref="TartSandboxOptions.SshPasswordEnvVar"/> at
/// use time), never from configuration files. Admission and capacity stay
/// host-owned: member gates size admission from
/// <see cref="SandboxMember.Capacity"/> and the composition root wraps this
/// provider exactly like a built-in one.</para>
/// </summary>
[CodeyBoxPlugin(
    id: TartSandboxOptions.PluginId,
    displayName: "CodeyBox: Tart macOS VMs",
    minHostApiVersion: "1.0")]
public sealed class TartSandboxProvider : ISandboxProvider, ISuspendingSandboxProvider,
    IActiveSandboxProvider, IPluginInitializer
{
    /// <summary>Default VM name prefix. Managed VMs never collide with foreign ones.</summary>
    public const string DefaultNamePrefix = "codeybox-";

    /// <summary>
    /// Guest path reserved for credential files. Must match the host's
    /// <c>SandboxConventions.CredentialsDir</c> (the plugin boundary forbids
    /// referencing that assembly; equality is enforced by test).
    /// </summary>
    internal const string CredentialMountPath = "/run/codeybox/creds";

    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly ITartProcessRunner _runner;
    private readonly Func<bool> _isMacOS;
    private readonly TimeProvider _timeProvider;

    private Func<TartSandboxOptions> _readOptions;
    private ILogger _log;

    /// <summary>DI constructor used by the plugin host.</summary>
    public TartSandboxProvider(IConfiguration configuration, ILogger<TartSandboxProvider> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = logger;
        _isMacOS = OperatingSystem.IsMacOS;
        var section = configuration.GetSection($"CodeyBox:Plugins:{TartSandboxOptions.PluginId}");
        _readOptions = () => TartSandboxOptions.FromConfiguration(section);
        _runner = new SystemTartProcessRunner();
    }

    /// <summary>Test constructor with full control over options, transport, platform, and clock.</summary>
    internal TartSandboxProvider(
        Func<TartSandboxOptions> readOptions,
        ITartProcessRunner runner,
        Func<bool>? isMacOS,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _isMacOS = isMacOS ?? throw new ArgumentNullException(nameof(isMacOS));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Provider kind. Normalised (trimmed, lowercase) and matched by exact ordinal equality.</summary>
    public string Name => TartSandboxOptions.ProviderKind;

    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
            return true;
        try
        {
            return name.StartsWith(_readOptions().NamePrefix, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Tart guests are VMs with a separate guest kernel (Apple Virtualization.framework).</summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honestly declared capabilities: suspend/resume (stop preserves the
    /// clone directory; resume re-runs it — running processes do not survive)
    /// and teardown (stop-and-preserve, delete). Baseline bake, cache seeding,
    /// disk guard, and port publishing are deliberately absent — placement
    /// refuses work requiring them rather than failing deep inside a phase.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.SuspendResume, SandboxCapabilities.Teardown];

    /// <summary>Live VM count for observability and tests.</summary>
    internal int ActiveSandboxCount => _activeSandboxes.Count;

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = ct;
        _log = context.Logger;
        var section = context.ScopedConfig;
        _readOptions = () => TartSandboxOptions.FromConfiguration(section);

        // Fail the host fast on operator misconfiguration that can be checked
        // without spawning a process.
        var opts = _readOptions();
        ValidateOptions(opts);

        _log.LogWarning(
            "Tart sandbox provider '{Kind}': egress NOT enforced — no host network isolation on macOS. " +
            "It may only serve sandboxes with no named network profile and must never be described as isolation.",
            TartSandboxOptions.ProviderKind);
        return Task.CompletedTask;
    }

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = _readOptions();
        ValidateOptions(opts);
        EnsureEnabled(opts);
        EnsureSupportedHost(opts);
        ValidateSpec(spec, opts);

        var image = string.IsNullOrWhiteSpace(spec.ImageReference) ? opts.DefaultImage.Trim() : spec.ImageReference.Trim();
        if (image.Length == 0)
            throw new InvalidOperationException(
                $"Tart provider '{TartSandboxOptions.ProviderKind}' has no image: set the work spec's ImageReference or " +
                $"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:DefaultImage.");

        // Fail fast on missing guest credentials before cloning anything.
        if (!TartCredentialChain.UsesKeyAuth(opts))
            TartCredentialChain.RequireSshPassword(opts, Environment.GetEnvironmentVariable);

        var vmName = BuildVmName(opts.NamePrefix);
        var transport = new TartSshGuestTransport(_runner, _readOptions, () => ResolvePassword(opts));

        try
        {
            await CloneAsync(opts, image, vmName, ct).ConfigureAwait(false);
        }
        catch (TartCliException ex)
        {
            throw TartFailureClassification.ToDeferred(ex, "clone-vm", Recheck(opts));
        }

        var sandbox = new TartSandbox(vmName, "0.0.0.0", _runner, transport, _readOptions, spec, _timeProvider, _log, id => _activeSandboxes.TryRemove(id, out _));
        var entry = new ActiveSandboxEntry(spec.TimingWorkItemId, sandbox);
        _activeSandboxes[vmName] = entry;

        try
        {
            await SizeVmAsync(opts, vmName, spec, ct).ConfigureAwait(false);
            var runProcess = StartVm(opts, vmName);
            sandbox.AttachVmProcess(runProcess);
            var ip = await transport.WaitForSshAsync(vmName, ct).ConfigureAwait(false);
            sandbox.RefreshIp(ip);
            await RunSetupCommandsAsync(opts, sandbox, ct).ConfigureAwait(false);
            var writableMounts = await StageMountsAsync(opts, transport, ip, spec, ct).ConfigureAwait(false);
            sandbox.SetWritableMounts(writableMounts);
            await EnsureWorkDirAsync(opts, transport, ip, spec, ct).ConfigureAwait(false);
            return sandbox;
        }
        catch (Exception ex)
        {
            await TeardownBestEffortAsync(opts, sandbox).ConfigureAwait(false);
            _activeSandboxes.TryRemove(vmName, out _);
            if (ex is TartCliException cliEx)
                throw TartFailureClassification.ToDeferred(cliEx, "create-vm", Recheck(opts));
            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = _readOptions();
        ValidateOptions(opts);

        IReadOnlyList<TartVmListEntry> entries;
        try
        {
            entries = await ListVmsAsync(opts, ct).ConfigureAwait(false);
        }
        catch (TartCliException ex)
        {
            throw TartFailureClassification.ToDeferred(ex, "list-vms", Recheck(opts));
        }

        var managed = new List<ManagedSandboxInfo>();
        foreach (var vm in entries)
        {
            if (!vm.Name.StartsWith(opts.NamePrefix, StringComparison.Ordinal))
                continue;
            managed.Add(new ManagedSandboxInfo(
                vm.Name,
                CreatedAt: null,
                DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(vm.Name),
                HasPreemptMarker: false,
                IsSuspendLifecycleOrFrozen: !string.Equals(vm.State, "running", StringComparison.OrdinalIgnoreCase)));
        }
        return managed;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);
        if (!name.StartsWith(opts.NamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to dispose '{name}': outside the Tart provider's '{opts.NamePrefix}' namespace.");

        var stop = await RunTartAsync(opts, ["stop", name], TimeSpan.FromSeconds(opts.TransitionTimeoutSeconds), ct).ConfigureAwait(false);
        if (stop.ExitCode != 0 && !IsNotFound(stop.Stderr))
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["stop", name], stop.ExitCode, stop.Stderr);
        var delete = await RunTartAsync(opts, ["delete", name], TimeSpan.FromSeconds(opts.TransitionTimeoutSeconds), ct).ConfigureAwait(false);
        if (delete.ExitCode != 0 && !IsNotFound(delete.Stderr))
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["delete", name], delete.ExitCode, delete.Stderr);
    }

    public async Task ResumeSandboxAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var opts = _readOptions();
        ValidateOptions(opts);

        IReadOnlyList<TartVmListEntry> entries;
        try
        {
            entries = await ListVmsAsync(opts, ct).ConfigureAwait(false);
        }
        catch (TartCliException ex)
        {
            throw TartFailureClassification.ToDeferred(ex, "resume-vm", Recheck(opts));
        }

        var match = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (match is null)
            return;
        if (string.Equals(match.State, "running", StringComparison.OrdinalIgnoreCase))
            return;

        var transport = new TartSshGuestTransport(_runner, _readOptions, () => ResolvePassword(opts));
        try
        {
            // The detached `tart run` handle is released without killing:
            // the VM must keep running after resume. Later teardown goes
            // through the CLI, never this handle.
            StartVm(opts, name).Dispose();
            await transport.WaitForSshAsync(name, ct).ConfigureAwait(false);
        }
        catch (TartCliException ex)
        {
            throw TartFailureClassification.ToDeferred(ex, "resume-vm", Recheck(opts));
        }
    }

    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Where(static entry => entry.WorkItemId is not null)
            .Select(static entry => (entry.WorkItemId!.Value, (IShutdownTeardownSandbox)entry.Sandbox))
            .ToList();

    internal string ResolvePassword(TartSandboxOptions opts) =>
        TartCredentialChain.ResolveSshPassword(opts, Environment.GetEnvironmentVariable) ?? string.Empty;

    internal static void ValidateOptions(TartSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (string.IsNullOrWhiteSpace(opts.TartBinaryPath))
            throw new InvalidOperationException($"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:TartBinaryPath must be non-empty.");
        if (string.IsNullOrWhiteSpace(opts.NamePrefix) || !opts.NamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:NamePrefix must start with 'codeybox-'.");
        if (opts.SshPort is < 1 or > 65535)
            throw new InvalidOperationException($"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:SshPort must be 1-65535.");
        if (string.IsNullOrWhiteSpace(opts.SshUsername))
            throw new InvalidOperationException($"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:SshUsername must be non-empty.");
        if (opts.SshUsername.Contains('@', StringComparison.Ordinal) || opts.SshUsername.Contains(' ', StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:SshUsername must be a bare username.");
    }

    internal void EnsureEnabled(TartSandboxOptions opts)
    {
        if (!opts.Enabled)
            throw new InvalidOperationException(
                $"Tart sandbox provider '{TartSandboxOptions.ProviderKind}' is not enabled. " +
                $"Allowlist '{TartSandboxOptions.PluginId}' and set " +
                $"CodeyBox:Plugins:{TartSandboxOptions.PluginId}:Enabled=true.");
    }

    internal void EnsureSupportedHost(TartSandboxOptions opts)
    {
        if (opts.RequireMacOSHost && !_isMacOS())
            throw TartFailureClassification.ToDeferred(
                new TartCliException(opts.TartBinaryPath, ["run"], null, "unsupported-host", "tart runs only on a macOS Apple Silicon host"),
                "check-host",
                Recheck(opts));
    }

    internal static void ValidateSpec(SandboxSpec spec, TartSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(opts);
        if (spec.RecoveryLease is not null)
            throw new InvalidOperationException(
                $"Tart provider '{TartSandboxOptions.ProviderKind}' does not implement recovery-lease adopt; refusing instead of provisioning a different VM.");
        if (!string.IsNullOrWhiteSpace(spec.Network.ProfileName))
            throw new InvalidOperationException(
                $"Sandbox provider kind '{TartSandboxOptions.ProviderKind}' cannot serve network profile '{spec.Network.ProfileName!.Trim()}': " +
                $"the kind is classified '{EgressEnforcementLocation.NotEnforced}' (no host-enforced egress filtering on macOS). " +
                $"A '{EgressEnforcementLocation.NotEnforced}' provider may only serve sandboxes with no named network profile.");
        if (spec.Flavor == SandboxProfileFlavor.Graphical)
            throw new NotSupportedException($"Tart provider '{TartSandboxOptions.ProviderKind}' does not serve graphical sandboxes (no display/VNC capture).");
        if (string.IsNullOrWhiteSpace(spec.WorkingDirectory))
            throw new InvalidOperationException("Sandbox working directory must be non-empty.");
        TartGuestPath.ValidateAbsolute(spec.WorkingDirectory, nameof(spec.WorkingDirectory));
        foreach (var mount in spec.Mounts)
            ValidateMount(mount, opts);
        ValidateEnvironment(spec.Environment);
    }

    internal static void ValidateMount(SandboxMount mount, TartSandboxOptions opts)
    {
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(opts);
        TartGuestPath.ValidateAbsolute(mount.SandboxPath, nameof(mount.SandboxPath));
        if (mount.Tmpfs && !string.IsNullOrWhiteSpace(mount.HostPath))
            throw new InvalidOperationException($"Tmpfs mount '{mount.SandboxPath}' must not carry a host path.");
        if (mount.Tmpfs && string.Equals(mount.SandboxPath, CredentialMountPath, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Credential tmpfs mount '{CredentialMountPath}' is refused: the Tart guest has no tmpfs semantics — pass credentials as environment variables instead.");
        if (mount.Tmpfs && !opts.AllowPersistentTmpfsDowngrade)
            throw new InvalidOperationException(
                $"Tmpfs mount '{mount.SandboxPath}' is refused: the Tart provider stages a persistent guest directory. " +
                $"Set CodeyBox:Plugins:{TartSandboxOptions.PluginId}:AllowPersistentTmpfsDowngrade=true to accept that downgrade explicitly.");
    }

    internal static IReadOnlyDictionary<string, string> ValidateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var validated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            SandboxEnvironmentVariableName.Validate(key, nameof(environment));
            validated[key] = value ?? string.Empty;
        }
        return validated;
    }

    internal static string BuildVmName(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{prefix}{random}-{stamp}";
        if (name.Length > 128)
            name = name[..128];
        return name;
    }

    internal async Task CloneAsync(TartSandboxOptions opts, string image, string vmName, CancellationToken ct)
    {
        var result = await RunTartAsync(opts, ["clone", image, vmName], TimeSpan.FromSeconds(opts.CliTimeoutSeconds), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["clone", image, vmName], result.ExitCode, result.Stderr);
    }

    internal async Task SizeVmAsync(TartSandboxOptions opts, string vmName, SandboxSpec spec, CancellationToken ct)
    {
        var cpu = spec.Limits.CpuCount ?? opts.DefaultCpuCount;
        cpu = Math.Clamp(cpu, 1, 32);
        var memoryGiB = spec.Limits.MemoryBytes.HasValue
            ? Math.Clamp((int)Math.Ceiling(spec.Limits.MemoryBytes.Value / (1024.0 * 1024 * 1024)), 1, 128)
            : opts.DefaultMemoryGiB;
        var result = await RunTartAsync(
            opts,
            ["set", vmName, "--cpu", cpu.ToString(System.Globalization.CultureInfo.InvariantCulture), "--memory", memoryGiB.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(opts.CliTimeoutSeconds),
            ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw TartFailureClassification.ForExit(opts.TartBinaryPath, ["set", vmName], result.ExitCode, result.Stderr);
    }

    internal ITartDetachedProcess StartVm(TartSandboxOptions opts, string vmName)
    {
        var argv = new List<string> { "run" };
        argv.AddRange(opts.ExtraRunArgs.Where(static arg => !string.IsNullOrWhiteSpace(arg)));
        argv.Add(vmName);
        return _runner.StartDetached(new TartProcessSpec(opts.TartBinaryPath, argv, Stdin: null, Timeout: Timeout.InfiniteTimeSpan));
    }

    internal async Task<IReadOnlyList<TartVmListEntry>> ListVmsAsync(TartSandboxOptions opts, CancellationToken ct)
    {
        var json = await RunTartAsync(opts, ["list", "--format", "json"], TimeSpan.FromSeconds(opts.CliTimeoutSeconds), ct).ConfigureAwait(false);
        if (json.ExitCode == 0)
        {
            try
            {
                return TartShellCommand.ParseListJson(json.Stdout);
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to plain-text parsing: older tart binaries may
                // ignore --format and print the table anyway.
            }
        }
        var text = json.ExitCode == 0
            ? json
            : await RunTartAsync(opts, ["list"], TimeSpan.FromSeconds(opts.CliTimeoutSeconds), ct).ConfigureAwait(false);
        if (text.ExitCode != 0)
            throw new TartCliException(opts.TartBinaryPath, ["list"], text.ExitCode, "list-failed", LastLine(text.Stderr));
        return TartShellCommand.ParseListText(text.Stdout);
    }

    private async Task RunSetupCommandsAsync(TartSandboxOptions opts, TartSandbox sandbox, CancellationToken ct)
    {
        foreach (var command in opts.SetupCommands.Where(static c => !string.IsNullOrWhiteSpace(c)))
        {
            var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["bash", "-lc", command] }, ct).ConfigureAwait(false);
            if (!result.Success)
                throw new SandboxProvisioningDeferredException(
                    TartSandboxOptions.ProviderKind,
                    "setup-guest",
                    "setup-failed",
                    $"operator setup command failed with exit {result.ExitCode}: {LastLine(result.Stderr)}",
                    Recheck(opts));
        }
    }

    private async Task<List<TartWritableMountSync>> StageMountsAsync(
        TartSandboxOptions opts,
        TartSshGuestTransport transport,
        string ip,
        SandboxSpec spec,
        CancellationToken ct)
    {
        var writable = new List<TartWritableMountSync>();
        var stagedFiles = 0;
        long stagedBytes = 0;
        var timeout = TimeSpan.FromSeconds(opts.CliTimeoutSeconds);

        foreach (var mount in spec.Mounts)
        {
            ct.ThrowIfCancellationRequested();
            if (mount.Tmpfs)
            {
                var made = await transport.ExecRawAsync(ip, TartShellCommand.BuildMkdirCommand(mount.SandboxPath), stdin: null, timeout, null, null, 4096, ct).ConfigureAwait(false);
                if (made.ExitCode != 0)
                    throw TartFailureClassification.ForExit("ssh", ["mkdir", mount.SandboxPath], made.ExitCode, made.Stderr);
                continue;
            }
            if (string.IsNullOrWhiteSpace(mount.HostPath))
            {
                var created = await transport.ExecRawAsync(ip, TartShellCommand.BuildMkdirCommand(mount.SandboxPath), stdin: null, timeout, null, null, 4096, ct).ConfigureAwait(false);
                if (created.ExitCode != 0)
                    throw TartFailureClassification.ForExit("ssh", ["mkdir", mount.SandboxPath], created.ExitCode, created.Stderr);
                continue;
            }

            var hostPath = mount.HostPath.Trim();
            if (File.Exists(hostPath))
            {
                var info = new FileInfo(hostPath);
                if (info.Length > opts.MaxStageFileBytes)
                    throw new InvalidOperationException($"Staged file '{hostPath}' exceeds the {opts.MaxStageFileBytes}-byte bound.");
                stagedFiles++;
                stagedBytes += info.Length;
                EnforceStageTotals(opts, stagedFiles, stagedBytes);
                var bytes = await File.ReadAllBytesAsync(hostPath, ct).ConfigureAwait(false);
                await transport.WriteFileBytesAsync(ip, mount.SandboxPath, bytes, timeout, ct).ConfigureAwait(false);
                if (!mount.ReadOnly)
                    throw new InvalidOperationException($"Single-file mount '{mount.SandboxPath}' must be read-only: sync-back needs a directory pair.");
                continue;
            }

            if (!Directory.Exists(hostPath))
                throw new SandboxMountSourceMissingException(hostPath, $"Mount source '{hostPath}' does not exist; the orchestrator may recreate it and retry.");

            foreach (var file in Directory.EnumerateFiles(hostPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (info.Length > opts.MaxStageFileBytes)
                    throw new InvalidOperationException($"Staged file '{file}' exceeds the {opts.MaxStageFileBytes}-byte bound.");
                stagedFiles++;
                stagedBytes += info.Length;
                EnforceStageTotals(opts, stagedFiles, stagedBytes);
                var relative = Path.GetRelativePath(hostPath, file).Replace(Path.DirectorySeparatorChar, '/');
                var guestPath = TartGuestPath.Join(mount.SandboxPath, relative);
                var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                await transport.WriteFileBytesAsync(ip, guestPath, bytes, timeout, ct).ConfigureAwait(false);
            }

            if (!mount.ReadOnly)
                writable.Add(new TartWritableMountSync(Path.GetFullPath(hostPath), mount.SandboxPath));
        }

        return writable;
    }

    private async Task EnsureWorkDirAsync(TartSandboxOptions opts, TartSshGuestTransport transport, string ip, SandboxSpec spec, CancellationToken ct)
    {
        var workDir = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "/work" : spec.WorkingDirectory;
        var covered = spec.Mounts.Any(m => string.Equals(m.SandboxPath, workDir, StringComparison.Ordinal));
        if (covered)
            return;
        // Raw control-plane call, not a scripted user exec: the directory
        // must exist regardless of what an execution responder would say.
        var timeout = TimeSpan.FromSeconds(opts.CliTimeoutSeconds);
        var made = await transport.ExecRawAsync(ip, TartShellCommand.BuildMkdirCommand(workDir), stdin: null, timeout, null, null, 4096, ct).ConfigureAwait(false);
        if (made.ExitCode != 0)
            throw TartFailureClassification.ForExit("ssh", ["mkdir", workDir], made.ExitCode, made.Stderr);
    }

    private static void EnforceStageTotals(TartSandboxOptions opts, int files, long bytes)
    {
        if (files > opts.MaxStageFileCount)
            throw new InvalidOperationException($"Staging exceeds the {opts.MaxStageFileCount}-file bound.");
        if (bytes > opts.MaxStageTotalBytes)
            throw new InvalidOperationException($"Staging exceeds the {opts.MaxStageTotalBytes}-byte bound.");
    }

    private async Task TeardownBestEffortAsync(TartSandboxOptions opts, TartSandbox sandbox)
    {
        try
        {
            await sandbox.StopVmAsync(opts, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tart VM '{Vm}': best-effort stop failed during create rollback.", sandbox.VmName);
        }
        try
        {
            await sandbox.DeleteVmAsync(opts, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tart VM '{Vm}': best-effort delete failed during create rollback.", sandbox.VmName);
        }
    }

    private Task<TartProcessResult> RunTartAsync(TartSandboxOptions opts, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
        _runner.RunAsync(
            new TartProcessSpec(opts.TartBinaryPath, argv, Stdin: null, timeout),
            stdoutChunk: null, stderrChunk: null, maxOutputBytes: 1024 * 1024, ct);

    private static TimeSpan Recheck(TartSandboxOptions opts) =>
        TimeSpan.FromSeconds(Math.Clamp(opts.ProvisioningRecheckSeconds, 5, 3600));

    private static bool IsNotFound(string stderr) =>
        stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no such", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static string LastLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
    }

    private sealed record ActiveSandboxEntry(WorkItemId? WorkItemId, TartSandbox Sandbox);
}
