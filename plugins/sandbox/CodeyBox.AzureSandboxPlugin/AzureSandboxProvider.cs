using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.AzureSandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by Azure Virtual Machines ARM VMs.
/// Contributes the <c>azure</c> provider kind through the plugin trust model:
/// the host owns egress classification, so this kind is always
/// <c>NotEnforced</c> — the guest runs on infrastructure CodeyBox does not
/// control and the host nftables egress guarantee cannot apply. Acquisitions
/// requiring enforced egress (a named network profile) are refused by
/// placement before this provider is ever called, and again here if one ever
/// arrives.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.azure-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set). Credentials come only from the host credential
/// chain (environment); the secret is never logged, persisted, or sent into
/// guest metadata.</para>
/// </summary>
[CodeyBoxPlugin(AzureSandboxOptions.PluginId, "Azure Virtual Machines sandbox provider")]
public sealed class AzureSandboxProvider :
    ISandboxProvider,
    IPluginInitializer,
    IActiveSandboxProvider,
    IDisposable
{
    internal const string TagManagedKey = "codeybox.managed";
    internal const string TagOwnerKey = "codeybox.owner";
    internal const string TagWorkItemKey = "codeybox.work-item";
    internal const string TagCreatedKey = "codeybox.created";
    internal const string TagRequestKey = "codeybox.request-id";
    internal const string ManagedTagValue = "true";

    private static readonly TimeSpan MinimumOperatorFixRecheck = TimeSpan.FromMinutes(5);

    private readonly Func<AzureSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly IAzureKeyGenerator _keys;
    private readonly IAzureTransportFactory _transports;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _unreconciledResources = new(StringComparer.Ordinal);
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>.</summary>
    public AzureSandboxProvider(TimeProvider? clock = null)
        : this(
            readOptions: null,
            http: null,
            keys: null,
            transports: null,
            environment: null,
            clock: clock,
            log: null)
    {
    }

    /// <summary>Test seam: full constructor injection.</summary>
    internal AzureSandboxProvider(
        Func<AzureSandboxOptions>? readOptions,
        HttpClient? http,
        IAzureKeyGenerator? keys,
        IAzureTransportFactory? transports,
        Func<string, string?>? environment,
        TimeProvider? clock,
        ILogger? log)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = environment ?? (name => Environment.GetEnvironmentVariable(name));
        var runner = new CodeyBox.HostProcess.DefaultProcessRunner();
        _keys = keys ?? new SshKeygenAzureKeyGenerator(runner);
        _transports = transports ?? new OpenSshAzureTransportFactory(runner);
        _http = http ?? new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = http is null;
        _readOptions = readOptions ?? (() => AzureSandboxOptions.FromConfiguration(_host?.ScopedConfig));
        if (log is not null)
            _log = log;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = ct;
        _host = context.Host;
        _log = context.Logger;
        var options = ReadOptions();
        _log.LogInformation(
            "Azure sandbox provider initialized (enabled={Enabled}, location={Location})",
            options.Enabled,
            string.IsNullOrEmpty(options.Location) ? "(unset)" : options.Location);
        return Task.CompletedTask;
    }

    public string Name => AzureSandboxOptions.ProviderKind;

    /// <inheritdoc/>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
            return true;
        try
        {
            return name.StartsWith(ReadOptions().VmNamePrefix, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Real VMs with a dedicated guest kernel — including genuine RAM-backed
    /// tmpfs mounts. This is a guest-boundary claim only; egress stays
    /// NotEnforced (host-classified, the plugin cannot promote itself).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honest capability set: fresh VM per work item, torn down on disposal.
    /// No baseline bake/snapshot is advertised: the configured immutable image
    /// is sufficient for this target and no bake path is implemented.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.Teardown];

    /// <summary>
    /// Owned resource identities that cleanup could not confirm as deleted.
    /// Retained (never erased) so the leak reaper and operators can reconcile
    /// them; a merely accepted delete request never clears an entry.
    /// </summary>
    internal IReadOnlyDictionary<string, string> UnreconciledResources => _unreconciledResources;

    private AzureSandboxOptions ReadOptions() => _readOptions();

    private AzureApiClient CreateClient(AzureSandboxOptions opts) =>
        new(_http, _clock, opts.ToClientLimits());

    // ------------------------------------------------------------------
    // Provisioning
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = ReadValidatedOptions();

        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("azure sandbox provider does not support the graphical sandbox flavor.");

        // Host-owned egress rule, enforced as an in-provider backstop too:
        // a named profile needs host nftables that cannot exist on this kind.
        SandboxEgressPolicy.EnsureEnforcedEgressForProfile(AzureSandboxOptions.ProviderKind, spec.Network.ProfileName);

        if (!string.IsNullOrWhiteSpace(spec.BaselineImageRef))
            throw new InvalidOperationException(
                $"Pinned baseline '{spec.BaselineImageRef}' cannot be served: the azure provider boots " +
                "the configured immutable image and implements no baseline bake/snapshot path.");

        var ownerId = ResolveOwnerId(opts);
        var image = ResolveImageReference(opts, spec);
        var mounts = PlanMounts(spec, opts);
        var credentials = AzureCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var workItemId = spec.TimingWorkItemId?.ToString() ?? string.Empty;
        var requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var vmName = opts.VmNamePrefix + suffix;
        var nicName = vmName + "-nic";
        var nsgName = vmName + "-nsg";
        var pipName = vmName + "-pip";
        var diskName = vmName + "-osdisk";
        var createdAt = _clock.GetUtcNow();

        var sshTempDirectory = Path.Combine(Path.GetTempPath(), "codeybox-azure-" + suffix);
        Directory.CreateDirectory(sshTempDirectory);

        var ids = new AzureOwnedIds(
            AzureResourceIds.VmId(credentials, opts.ResourceGroupName, vmName),
            AzureResourceIds.NicId(credentials, opts.ResourceGroupName, nicName),
            AzureResourceIds.NsgId(credentials, opts.ResourceGroupName, nsgName),
            opts.AllocatePublicIp ? AzureResourceIds.PublicIpId(credentials, opts.ResourceGroupName, pipName) : null,
            AzureResourceIds.DiskId(credentials, opts.ResourceGroupName, diskName));

        try
        {
            ct.ThrowIfCancellationRequested();
            await RefuseNameCollisionAsync(api, credentials, opts, ids.VmId, vmName, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            var clientKey = await _keys.GenerateClientKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, vmName, ct).ConfigureAwait(false);
            var hostKey = await _keys.GenerateHostKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, vmName + "-host", ct).ConfigureAwait(false);

            var userData = AzureCloudInit.Build(new AzureCloudInitSpec(
                vmName, opts.AdminUsername, clientKey.PublicKeyText,
                hostKey.PrivateKeyPem, hostKey.PublicKeyText, mounts.TmpfsMounts));
            var userDataBytes = Encoding.UTF8.GetByteCount(userData);
            if (userDataBytes > opts.MaxUserDataBytes)
                throw new InvalidOperationException(
                    $"Cloud-init userData ({userDataBytes} bytes) exceeds MaxUserDataBytes ({opts.MaxUserDataBytes}).");
            var userDataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userData));

            var tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TagManagedKey] = ManagedTagValue,
                [TagOwnerKey] = ownerId,
                [TagWorkItemKey] = workItemId,
                [TagCreatedKey] = createdAt.ToString("o", CultureInfo.InvariantCulture),
                [TagRequestKey] = requestId,
            };

            ct.ThrowIfCancellationRequested();
            var waitTimeout = TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
            var nsg = await api.PutNsgAsync(
                credentials, ids.NsgId, opts.NetworkApiVersion,
                BuildNsgBody(opts, nsgName, tags), waitTimeout, ct).ConfigureAwait(false);
            _ = nsg;

            string? pipId = null;
            if (ids.PublicIpId is not null)
            {
                ct.ThrowIfCancellationRequested();
                await api.PutPublicIpAsync(
                    credentials, ids.PublicIpId, opts.NetworkApiVersion,
                    BuildPublicIpBody(opts, pipName, tags), waitTimeout, ct).ConfigureAwait(false);
                pipId = ids.PublicIpId;
            }

            ct.ThrowIfCancellationRequested();
            var subnetId = AzureResourceIds.SubnetId(
                credentials, opts.ResourceGroupName, opts.VirtualNetworkName, opts.SubnetName);
            await api.PutNicAsync(
                credentials, ids.NicId, opts.NetworkApiVersion,
                BuildNicBody(nicName, opts.Location, subnetId, ids.NsgId, pipId, tags),
                waitTimeout, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            AzureVm vm;
            try
            {
                vm = await api.PutVmAsync(
                    credentials, ids.VmId, opts.ComputeApiVersion,
                    BuildVmBody(opts, vmName, image, clientKey.PublicKeyText, userDataBase64, ids.NicId, tags),
                    waitTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unknown create outcome: the PUT may have succeeded server-side
                // while the response was lost. Reconcile by stable resource
                // identity before any resubmit — never blindly PUT a second VM.
                vm = await ReconcileUnknownVmCreateAsync(
                    api, credentials, opts, ids.VmId, requestId, ownerId, waitTimeout, ex, ct).ConfigureAwait(false);
            }
            _ = vm;

            ct.ThrowIfCancellationRequested();
            var address = await ResolveVmAddressAsync(
                api, credentials, opts, ids, waitTimeout, ct).ConfigureAwait(false);

            var knownHostsPath = Path.Combine(sshTempDirectory, "known_hosts");
            await WriteKnownHostsAsync(knownHostsPath, address, hostKey.PublicKeyText, ct).ConfigureAwait(false);
            var transport = _transports.Create(new AzureSshTransportSpec(
                $"{opts.AdminUsername}@{address}", opts.SshPort, clientKey.PrivateKeyPath,
                knownHostsPath, opts.SshBinary, opts.SshConnectTimeoutSeconds));
            await WaitForSshReadyAsync(transport, opts, ct).ConfigureAwait(false);

            var sandbox = new AzureSandbox(
                vmName, spec, mounts.Staged, transport,
                () => DeleteOwnedAsync(ids, vmName),
                () => MarkNoLongerActive(vmName),
                sshTempDirectory, _log);
            _activeSandboxes[vmName] = new ActiveSandboxEntry(
                spec.TimingWorkItemId ?? default, sandbox);
            SandboxLiveCounter.Increment();

            try
            {
                await PrepareGuestFilesystemAsync(transport, mounts, ct).ConfigureAwait(false);
                foreach (var staged in mounts.Staged)
                {
                    if (staged.HostPath is null)
                        continue;
                    await transport.StageInAsync(staged.HostPath, staged.RemotePath, ct).ConfigureAwait(false);
                }
            }
            catch (RemoteSshTransportException ex)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "stage", "staging-unavailable",
                    $"azure staging to {vmName} failed: {ex.Message}",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }

            _log.LogInformation("Created azure sandbox {Name}", vmName);
            return sandbox;
        }
        catch (SandboxProvisioningDeferredException)
        {
            await CleanupAfterFailureAsync(vmName, ids, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailureAsync(vmName, ids, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException and not OperationCanceledException)
        {
            await CleanupAfterFailureAsync(vmName, ids, sshTempDirectory).ConfigureAwait(false);
            if (ex is AzureApiException apiEx)
                throw ToDeferred(opts, apiEx, $"azure create failed for {vmName}");
            throw;
        }
    }

    private async Task<AzureVm> ReconcileUnknownVmCreateAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        string vmId,
        string requestId,
        string ownerId,
        TimeSpan waitTimeout,
        Exception createError,
        CancellationToken ct)
    {
        AzureVm? existing;
        try
        {
            existing = await api.GetVmAsync(credentials, vmId, opts.ComputeApiVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new AzureApiException(
                AzureFailureKind.Unexpected, "create virtual machine",
                $"create outcome unknown and reconciliation read failed ({TrimForMessage(ex.Message)}); " +
                $"original error: {TrimForMessage(createError.Message)}");
        }
        if (existing is null)
        {
            throw new AzureApiException(
                AzureFailureKind.Unexpected, "create virtual machine",
                $"create failed and no VM exists at the request identity; original error: {TrimForMessage(createError.Message)}");
        }
        if (!IsOwned(existing.Tags, ownerId) || !IsOurRequest(existing.Tags, requestId))
        {
            throw new InvalidOperationException(
                "Azure VM create failed ambiguously and the resource at the request identity " +
                "is not owned by this request; refusing to adopt or resubmit to protect live resources.");
        }
        var verified = await api.GetVmAsync(credentials, vmId, opts.ComputeApiVersion, ct).ConfigureAwait(false);
        if (verified is null || !string.Equals(verified.ProvisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureApiException(
                AzureFailureKind.Unexpected, "create virtual machine",
                $"reconciled VM at the request identity is in state '{verified?.ProvisioningState ?? "(deleted)"}'; " +
                "leftover resources retained for the leak reaper.");
        }
        _log.LogInformation("Reconciled ambiguous Azure VM create by request identity {VmId}", vmId);
        return verified;
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var ownerId = ResolveOwnerId(opts);
        var credentials = AzureCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var vms = await api.ListVmsAsync(credentials, opts.ResourceGroupName, opts.ComputeApiVersion, ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>();
        foreach (var vm in vms)
        {
            if (!vm.Name.StartsWith(opts.VmNamePrefix, StringComparison.Ordinal))
                continue;
            if (!IsOwned(vm.Tags, ownerId))
                continue;
            DateTimeOffset? createdAt = null;
            if (vm.Tags.TryGetValue(TagCreatedKey, out var createdRaw)
                && DateTimeOffset.TryParse(createdRaw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }
            result.Add(new ManagedSandboxInfo(
                vm.Name, createdAt, DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(vm.Name)));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(opts.VmNamePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"Azure sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var ownerId = ResolveOwnerId(opts);
        var credentials = AzureCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var ids = OwnedIdsFor(credentials, opts, name);
        var vm = await api.GetVmAsync(credentials, ids.VmId, opts.ComputeApiVersion, ct).ConfigureAwait(false);
        if (vm is not null)
        {
            if (!IsOwned(vm.Tags, ownerId))
                throw new InvalidOperationException(
                    $"Refusing to delete Azure VM '{name}': ownership tags do not match this host.");
            var nicId = vm.NicId ?? ids.NicId;
            var diskId = vm.OsDiskName is not null
                ? AzureResourceIds.DiskId(credentials, opts.ResourceGroupName, vm.OsDiskName)
                : ids.DiskId;
            await DeleteOwnedAsync(ids with { NicId = nicId, DiskId = diskId }, name).ConfigureAwait(false);
        }
        else
        {
            // No VM: delete only derived resources that still verify as ours.
            // The resource group, VNet, and subnet are caller-owned and never touched.
            await DeleteDerivedResourceIfOwnedAsync(api, credentials, opts, ownerId, ids.NicId, opts.NetworkApiVersion, "delete network interface", ct).ConfigureAwait(false);
            if (ids.PublicIpId is not null)
                await DeleteDerivedResourceIfOwnedAsync(api, credentials, opts, ownerId, ids.PublicIpId, opts.NetworkApiVersion, "delete public IP address", ct).ConfigureAwait(false);
            await DeleteDerivedResourceIfOwnedAsync(api, credentials, opts, ownerId, ids.NsgId, opts.NetworkApiVersion, "delete network security group", ct).ConfigureAwait(false);
            await DeleteDerivedResourceIfOwnedAsync(api, credentials, opts, ownerId, ids.DiskId, opts.ComputeApiVersion, "delete managed disk", ct).ConfigureAwait(false);
        }
        MarkNoLongerActive(name);
        await SweepOrphanResourcesAsync(api, credentials, opts, ownerId, ct).ConfigureAwait(false);
        if (!_unreconciledResources.IsEmpty)
        {
            throw new InvalidOperationException(
                "Azure leak disposal left unreconciled resources: "
                + string.Join("; ", _unreconciledResources.Select(kvp => kvp.Key + " (" + kvp.Value + ")"))
                + ". Identities retained for operator reconciliation.");
        }
    }

    // ------------------------------------------------------------------
    // Active-sandbox tracking
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

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    // ------------------------------------------------------------------
    // Internal lifecycle helpers
    // ------------------------------------------------------------------

    internal static AzureOwnedIds OwnedIdsFor(AzureCredentials credentials, AzureSandboxOptions opts, string vmName) =>
        new(
            AzureResourceIds.VmId(credentials, opts.ResourceGroupName, vmName),
            AzureResourceIds.NicId(credentials, opts.ResourceGroupName, vmName + "-nic"),
            AzureResourceIds.NsgId(credentials, opts.ResourceGroupName, vmName + "-nsg"),
            opts.AllocatePublicIp ? AzureResourceIds.PublicIpId(credentials, opts.ResourceGroupName, vmName + "-pip") : null,
            AzureResourceIds.DiskId(credentials, opts.ResourceGroupName, vmName + "-osdisk"));

    private async Task RefuseNameCollisionAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        string vmId,
        string vmName,
        CancellationToken ct)
    {
        var existing = await api.GetVmAsync(credentials, vmId, opts.ComputeApiVersion, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Azure VM name '{vmName}' already exists; refusing to adopt or overwrite a live resource. " +
                "The leak reaper owns cleanup of same-name leftovers.");
        }
    }

    private async Task<string> ResolveVmAddressAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        AzureOwnedIds ids,
        TimeSpan waitTimeout,
        CancellationToken ct)
    {
        if (ids.PublicIpId is not null)
        {
            var pip = await api.GetPublicIpAsync(credentials, ids.PublicIpId, opts.NetworkApiVersion, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(pip?.IpAddress))
                return ValidateVmAddress(pip.IpAddress);
            throw new AzureApiException(
                AzureFailureKind.Unexpected, "resolve sandbox address",
                "public IP has no address after provisioning succeeded.");
        }
        var deadline = _clock.GetUtcNow() + waitTimeout;
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var nic = await api.GetNicAsync(credentials, ids.NicId, opts.NetworkApiVersion, ct).ConfigureAwait(false);
            if (nic is null)
                throw new AzureApiException(
                    AzureFailureKind.Unexpected, "resolve sandbox address",
                    "network interface is missing after VM provisioning succeeded.");
            if (nic.PublicIpId is not null)
            {
                var pip = await api.GetPublicIpAsync(credentials, nic.PublicIpId, opts.NetworkApiVersion, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(pip?.IpAddress))
                    return ValidateVmAddress(pip.IpAddress);
            }
            if (!string.IsNullOrWhiteSpace(nic.PrivateIpAddress))
                return ValidateVmAddress(nic.PrivateIpAddress);
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new AzureApiException(
                    AzureFailureKind.Unexpected, "resolve sandbox address",
                    "network interface exposed no IP address before the deadline (eventual consistency not observed).");
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForSshReadyAsync(
        IRemoteHostTransport transport, AzureSandboxOptions opts, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.SshReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, opts.SshConnectTimeoutSeconds * 2)));
                var run = await transport.RunAsync(["true"], stdin: null, attemptCts.Token).ConfigureAwait(false);
                if (run.ExitCode == 0)
                    return;
            }
            catch (RemoteSshTransportException)
            {
                // Not ready yet — retry until the bounded deadline. A host-key
                // mismatch also surfaces here and is retried, not bypassed: the
                // deadline turns a persistent mismatch into a loud deferral and
                // cleanup, never an accept-any fallback.
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "ssh-ready", "ssh-unready",
                    "azure VM did not accept pinned-key SSH before the readiness deadline",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan NextPollDelay(AzureSandboxOptions opts, int attempt)
    {
        var baseMs = (double)Math.Max(200, opts.PollIntervalMilliseconds);
        var doubled = baseMs * (1L << Math.Min(attempt, 10));
        var capped = Math.Min(doubled, opts.MaxPollIntervalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(capped, 1));
    }

    private static async Task WriteKnownHostsAsync(
        string knownHostsPath, string address, string hostPublicKey, CancellationToken ct)
    {
        _ = ct;
        var validatedAddress = ValidateVmAddress(address);
        var key = (hostPublicKey ?? string.Empty).Trim();
        if (key.Length == 0
            || key.Any(ch => ch == '\n' || ch == '\r' || char.IsControl(ch)))
            throw new InvalidOperationException("Host public key is not a single-line OpenSSH key.");
        var line = validatedAddress + " " + key + "\n";
        await File.WriteAllTextAsync(knownHostsPath, line, CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Validates a cloud-returned VM address before it reaches the SSH target
    /// or the pinned known_hosts file. ARM JSON address fields are runtime
    /// dependency output: only an exact IP literal is accepted, so a
    /// newline-bearing or non-IP value fails provisioning loudly instead of
    /// injecting extra known_hosts entries or redirecting the connection.
    /// </summary>
    private static string ValidateVmAddress(string? raw)
    {
        var candidate = (raw ?? string.Empty).Trim();
        if (!IPAddress.TryParse(candidate, out var parsed))
            throw new AzureApiException(
                AzureFailureKind.Unexpected, "resolve sandbox address",
                "cloud returned an invalid IP address literal; refusing to target it.");
        return parsed.ToString();
    }

    private static async Task PrepareGuestFilesystemAsync(
        IRemoteHostTransport transport,
        AzureMountPlan mounts,
        CancellationToken ct)
    {
        var parents = mounts.Staged
            .Select(m => ParentOf(m.RemotePath))
            .Append(SandboxConventions.WorkDir)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var script = "set -e; " + string.Join("; ", parents.Select(p => "mkdir -p " + AzureSandbox.QuoteShellWord(p)));
        var run = await transport.RunAsync(["bash", "-c", script], stdin: null, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new AzureApiException(
                AzureFailureKind.Unexpected, "prepare guest filesystem",
                $"guest mkdir failed (exit {run.ExitCode})");
        }
    }

    private static string ParentOf(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private async Task CleanupAfterFailureAsync(
        string vmName, AzureOwnedIds ids, string sshTempDirectory)
    {
        MarkNoLongerActive(vmName);
        // Always attempt the full owned set: a failure after the PUT was sent
        // may have created resources the local tracker never confirmed.
        // Every delete revalidates exact ownership first, so absent or
        // foreign resources are never touched.
        await DeleteCreatedAsync(ids, CreatedTracker.All, vmName).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(sshTempDirectory))
                Directory.Delete(sshTempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure sandbox {Name}: failed to remove SSH key directory", vmName);
        }
    }

    private Task DeleteOwnedAsync(AzureOwnedIds ids, string vmName) =>
        DeleteCreatedAsync(ids, CreatedTracker.All, vmName);

    private async Task DeleteCreatedAsync(AzureOwnedIds ids, CreatedTracker created, string vmName)
    {
        AzureSandboxOptions opts;
        AzureCredentials credentials;
        AzureApiClient api;
        try
        {
            opts = ReadValidatedOptions();
            credentials = AzureCredentials.Resolve(opts, _environment);
            api = CreateClient(opts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure sandbox {Name}: cannot resolve credentials/options; cloud resources may leak", vmName);
            _unreconciledResources[ids.VmId] = "credentials-unavailable";
            return;
        }

        var ownerId = ResolveOwnerId(opts);
        var waitTimeout = TimeSpan.FromSeconds(Math.Min(120, Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600)));
        if (created.Vm)
            await DeleteIfOwnedAsync(api, credentials, opts, ownerId, ids.VmId, opts.ComputeApiVersion, "delete virtual machine", waitTimeout, vmName).ConfigureAwait(false);
        if (created.Nic)
            await DeleteIfOwnedAsync(api, credentials, opts, ownerId, ids.NicId, opts.NetworkApiVersion, "delete network interface", waitTimeout, vmName).ConfigureAwait(false);
        if (created.PublicIp && ids.PublicIpId is not null)
            await DeleteIfOwnedAsync(api, credentials, opts, ownerId, ids.PublicIpId, opts.NetworkApiVersion, "delete public IP address", waitTimeout, vmName).ConfigureAwait(false);
        if (created.Nsg)
            await DeleteIfOwnedAsync(api, credentials, opts, ownerId, ids.NsgId, opts.NetworkApiVersion, "delete network security group", waitTimeout, vmName).ConfigureAwait(false);
        if (created.Vm)
            await DeleteDiskIfOwnedAsync(api, credentials, opts, ownerId, ids.DiskId, waitTimeout, vmName).ConfigureAwait(false);
    }

    private async Task DeleteIfOwnedAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        string ownerId,
        string resourceId,
        string apiVersion,
        string operation,
        TimeSpan waitTimeout,
        string vmName)
    {
        _ = opts;
        try
        {
            // Revalidate exact ownership and scope at deletion: name prefixes
            // alone never authorize deletion.
            var owned = await IsResourceOwnedAsync(api, credentials, resourceId, apiVersion, ownerId, vmName).ConfigureAwait(false);
            if (!owned)
            {
                _log.LogWarning("Azure sandbox {Name}: {Operation} skipped — resource is absent or not owned", vmName, operation);
                return;
            }
            await api.DeleteByIdAsync(credentials, resourceId, apiVersion, operation, waitTimeout, CancellationToken.None).ConfigureAwait(false);
            _unreconciledResources.TryRemove(resourceId, out _);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure sandbox {Name}: {Operation} failed; identity retained for reconciliation", vmName, operation);
            _unreconciledResources[resourceId] = ex is AzureApiException apiEx ? apiEx.Kind.ToString() : ex.GetType().Name;
        }
    }

    private async Task DeleteDiskIfOwnedAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        string ownerId,
        string diskId,
        TimeSpan waitTimeout,
        string vmName)
    {
        try
        {
            var disk = await api.GetDiskAsync(credentials, diskId, opts.ComputeApiVersion, CancellationToken.None).ConfigureAwait(false);
            if (disk is null)
                return;
            if (!IsOwned(disk.Tags, ownerId))
            {
                _log.LogWarning("Azure sandbox {Name}: delete managed disk skipped — disk is not owned", vmName);
                return;
            }
            await api.DeleteByIdAsync(credentials, diskId, opts.ComputeApiVersion, "delete managed disk", waitTimeout, CancellationToken.None).ConfigureAwait(false);
            _unreconciledResources.TryRemove(diskId, out _);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure sandbox {Name}: delete managed disk failed; identity retained for reconciliation", vmName);
            _unreconciledResources[diskId] = ex is AzureApiException apiEx ? apiEx.Kind.ToString() : ex.GetType().Name;
        }
    }

    private async Task<bool> IsResourceOwnedAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        string resourceId,
        string apiVersion,
        string ownerId,
        string vmName)
    {
        try
        {
            if (resourceId.Contains("/virtualMachines/", StringComparison.OrdinalIgnoreCase))
            {
                var vm = await api.GetVmAsync(credentials, resourceId, apiVersion, CancellationToken.None).ConfigureAwait(false);
                return vm is not null && IsOwned(vm.Tags, ownerId);
            }
            if (resourceId.Contains("/networkInterfaces/", StringComparison.OrdinalIgnoreCase))
            {
                var nic = await api.GetNicAsync(credentials, resourceId, apiVersion, CancellationToken.None).ConfigureAwait(false);
                return nic is not null && IsOwned(nic.Tags, ownerId);
            }
            if (resourceId.Contains("/publicIPAddresses/", StringComparison.OrdinalIgnoreCase))
            {
                var pip = await api.GetPublicIpAsync(credentials, resourceId, apiVersion, CancellationToken.None).ConfigureAwait(false);
                return pip is not null && IsOwned(pip.Tags, ownerId);
            }
            if (resourceId.Contains("/networkSecurityGroups/", StringComparison.OrdinalIgnoreCase))
            {
                var nsg = await api.GetNsgAsync(credentials, resourceId, apiVersion, CancellationToken.None).ConfigureAwait(false);
                return nsg is not null && IsOwned(nsg.Tags, ownerId);
            }
            _log.LogWarning("Azure sandbox {Name}: unknown resource type for ownership check: {ResourceId}", vmName, resourceId);
            return false;
        }
        catch (AzureApiException ex) when (ex.Kind == AzureFailureKind.NotFound)
        {
            return false;
        }
    }

    private async Task DeleteDerivedResourceIfOwnedAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        string ownerId,
        string resourceId,
        string apiVersion,
        string operation,
        CancellationToken ct)
    {
        _ = ct;
        try
        {
            if (!await IsResourceOwnedAsync(api, credentials, resourceId, apiVersion, ownerId, resourceId).ConfigureAwait(false))
                return;
            await api.DeleteByIdAsync(
                credentials, resourceId, apiVersion, operation,
                TimeSpan.FromSeconds(Math.Min(120, Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600))),
                CancellationToken.None).ConfigureAwait(false);
            _unreconciledResources.TryRemove(resourceId, out _);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure leak disposal: {Operation} failed; identity retained", operation);
            _unreconciledResources[resourceId] = ex is AzureApiException apiEx ? apiEx.Kind.ToString() : ex.GetType().Name;
        }
    }

    private async Task SweepOrphanResourcesAsync(
        AzureApiClient api, AzureCredentials credentials, AzureSandboxOptions opts,
        string ownerId, CancellationToken ct)
    {
        var vms = await api.ListVmsAsync(credentials, opts.ResourceGroupName, opts.ComputeApiVersion, ct).ConfigureAwait(false);
        var liveVmNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vm in vms)
        {
            if (IsOwned(vm.Tags, ownerId))
                liveVmNames.Add(vm.Name);
        }

        var swept = 0;
        var budget = Math.Max(1, opts.MaxListItems);
        swept += await SweepUnattachedAsync(
            api, credentials, opts, ownerId, liveVmNames, "Microsoft.Network", "networkInterfaces",
            opts.NetworkApiVersion, static name => name.EndsWith("-nic", StringComparison.Ordinal) ? name[..^"-nic".Length] : null,
            static listed => listed.AttachedTo is not null, budget - swept, ct).ConfigureAwait(false);
        swept += await SweepUnattachedAsync(
            api, credentials, opts, ownerId, liveVmNames, "Microsoft.Network", "publicIPAddresses",
            opts.NetworkApiVersion, static name => name.EndsWith("-pip", StringComparison.Ordinal) ? name[..^"-pip".Length] : null,
            static listed => listed.AttachedTo is not null, budget - swept, ct).ConfigureAwait(false);
        swept += await SweepUnattachedAsync(
            api, credentials, opts, ownerId, liveVmNames, "Microsoft.Network", "networkSecurityGroups",
            opts.NetworkApiVersion, static name => name.EndsWith("-nsg", StringComparison.Ordinal) ? name[..^"-nsg".Length] : null,
            static _ => false, budget - swept, ct).ConfigureAwait(false);
        swept += await SweepUnattachedAsync(
            api, credentials, opts, ownerId, liveVmNames, "Microsoft.Compute", "disks",
            opts.ComputeApiVersion, static name => name.EndsWith("-osdisk", StringComparison.Ordinal) ? name[..^"-osdisk".Length] : null,
            static listed => !string.Equals(listed.DiskState, "Unattached", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(listed.DiskState), budget - swept, ct).ConfigureAwait(false);
        if (swept > 0)
            _log.LogInformation("Orphan sweep: released {Count} unreferenced Azure resource(s)", swept);
    }

    private async Task<int> SweepUnattachedAsync(
        AzureApiClient api,
        AzureCredentials credentials,
        AzureSandboxOptions opts,
        string ownerId,
        HashSet<string> liveVmNames,
        string providerNamespace,
        string resourceType,
        string apiVersion,
        Func<string, string?> stemOf,
        Func<AzureListedResource, bool> isAttached,
        int budget,
        CancellationToken ct)
    {
        if (budget <= 0)
            return 0;
        var swept = 0;
        var listed = await api.ListResourcesAsync(
            credentials, opts.ResourceGroupName, providerNamespace, resourceType, apiVersion, ct).ConfigureAwait(false);
        foreach (var resource in listed)
        {
            if (swept >= budget)
                break;
            ct.ThrowIfCancellationRequested();
            if (!resource.Name.StartsWith(opts.VmNamePrefix, StringComparison.Ordinal))
                continue;
            if (!IsOwned(resource.Tags, ownerId))
                continue;
            var stem = stemOf(resource.Name);
            if (stem is not null && liveVmNames.Contains(stem))
                continue;
            if (isAttached(resource))
                continue;
            try
            {
                if (await api.DeleteByIdAsync(
                    credentials, resource.Id, apiVersion, "sweep orphan " + resourceType,
                    TimeSpan.FromSeconds(Math.Min(60, Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600))),
                    CancellationToken.None).ConfigureAwait(false))
                {
                    swept++;
                    _unreconciledResources.TryRemove(resource.Id, out _);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orphan sweep: failed to delete {ResourceId}", resource.Id);
                _unreconciledResources[resource.Id] = ex is AzureApiException apiEx ? apiEx.Kind.ToString() : ex.GetType().Name;
            }
        }
        return swept;
    }

    internal static bool IsOwned(IReadOnlyDictionary<string, string> tags, string ownerId) =>
        tags.TryGetValue(TagManagedKey, out var managed)
        && string.Equals(managed, ManagedTagValue, StringComparison.Ordinal)
        && tags.TryGetValue(TagOwnerKey, out var owner)
        && string.Equals(owner, ownerId, StringComparison.Ordinal);

    internal static bool IsOurRequest(IReadOnlyDictionary<string, string> tags, string requestId) =>
        tags.TryGetValue(TagRequestKey, out var request)
        && string.Equals(request, requestId, StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // Request bodies
    // ------------------------------------------------------------------

    internal static string BuildNsgBody(AzureSandboxOptions opts, string nsgName, IReadOnlyDictionary<string, string> tags)
    {
        if (opts.OrchestratorSshCidrs.Count > opts.MaxSecurityRules)
            throw new InvalidOperationException(
                $"OrchestratorSshCidrs ({opts.OrchestratorSshCidrs.Count}) exceeds MaxSecurityRules ({opts.MaxSecurityRules}).");
        var rules = new List<object>();
        var priority = 100;
        foreach (var cidr in opts.OrchestratorSshCidrs)
        {
            rules.Add(new
            {
                name = "allow-ssh-" + priority.ToString(CultureInfo.InvariantCulture),
                properties = new
                {
                    priority,
                    direction = "Inbound",
                    access = "Allow",
                    protocol = "Tcp",
                    sourcePortRange = "*",
                    destinationPortRange = "22",
                    sourceAddressPrefix = cidr,
                    destinationAddressPrefix = "*",
                },
            });
            priority += 10;
        }
        return JsonSerializer.Serialize(new
        {
            location = opts.Location,
            tags,
            properties = new { securityRules = rules },
        }, AzureApiClient.Json);
    }

    internal static string BuildPublicIpBody(AzureSandboxOptions opts, string pipName, IReadOnlyDictionary<string, string> tags) =>
        JsonSerializer.Serialize(new
        {
            location = opts.Location,
            tags,
            sku = new { name = "Standard" },
            properties = new
            {
                publicIPAllocationMethod = "Static",
                dnsSettings = new { domainNameLabel = pipName.ToLowerInvariant().Replace('_', '-') },
            },
        }, AzureApiClient.Json);

    internal static string BuildNicBody(
        string nicName, string location, string subnetId, string nsgId, string? pipId,
        IReadOnlyDictionary<string, string> tags)
    {
        object? publicIp = pipId is null ? null : new { id = pipId };
        return JsonSerializer.Serialize(new
        {
            location,
            tags,
            properties = new
            {
                networkSecurityGroup = new { id = nsgId },
                ipConfigurations = new[]
                {
                    new
                    {
                        name = "ipconfig1",
                        properties = new
                        {
                            privateIPAllocationMethod = "Dynamic",
                            subnet = new { id = subnetId },
                            publicIPAddress = publicIp,
                        },
                    },
                },
            },
        }, AzureApiClient.Json);
    }

    internal static string BuildVmBody(
        AzureSandboxOptions opts,
        string vmName,
        AzureImageReference image,
        string clientPublicKey,
        string userDataBase64,
        string nicId,
        IReadOnlyDictionary<string, string> tags) =>
        JsonSerializer.Serialize(new
        {
            location = opts.Location,
            tags,
            properties = new
            {
                hardwareProfile = new { vmSize = opts.VmSize },
                storageProfile = new
                {
                    imageReference = new
                    {
                        publisher = image.Publisher,
                        offer = image.Offer,
                        sku = image.Sku,
                        version = image.Version,
                    },
                    osDisk = new
                    {
                        createOption = "FromImage",
                        diskSizeGB = opts.OsDiskSizeGiB,
                        managedDisk = new { storageAccountType = "Premium_LRS" },
                    },
                },
                osProfile = new
                {
                    computerName = vmName,
                    adminUsername = opts.AdminUsername,
                    linuxConfiguration = new
                    {
                        disablePasswordAuthentication = true,
                        ssh = new
                        {
                            publicKeys = new[]
                            {
                                new
                                {
                                    path = $"/home/{opts.AdminUsername}/.ssh/authorized_keys",
                                    keyData = clientPublicKey,
                                },
                            },
                        },
                    },
                    customData = userDataBase64,
                },
                networkProfile = new
                {
                    networkInterfaces = new[] { new { id = nicId, properties = new { primary = true } } },
                },
            },
        }, AzureApiClient.Json);

    // ------------------------------------------------------------------
    // Provisioning guards
    // ------------------------------------------------------------------

    internal static AzureImageReference ResolveImageReference(AzureSandboxOptions opts, SandboxSpec spec)
    {
        var reference = string.IsNullOrWhiteSpace(spec.ImageReference)
            ? $"{opts.ImagePublisher}:{opts.ImageOffer}:{opts.ImageSku}:{opts.ImageVersion}"
            : spec.ImageReference.Trim();
        var parts = reference.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || parts.Any(p => string.IsNullOrWhiteSpace(p) || p.Any(ch => ch == '/' || char.IsWhiteSpace(ch))))
        {
            throw new InvalidOperationException(
                "Azure image reference must be 'publisher:offer:sku:version' with an immutable version " +
                "(e.g. 'Canonical:0001-com-ubuntu-server-jammy:22_04-lts-gen2:22.04.20240101120000').");
        }
        if (string.Equals(parts[3], "latest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azure image version 'latest' is rejected: pin an immutable image version.");
        return new AzureImageReference(parts[0], parts[1], parts[2], parts[3]);
    }

    private AzureMountPlan PlanMounts(SandboxSpec spec, AzureSandboxOptions opts)
    {
        _ = opts;
        var tmpfsRoots = new List<AzureTmpfsMount>();
        foreach (var mount in spec.Mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.SandboxPath) || !mount.SandboxPath.StartsWith('/'))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");
            if (mount.Tmpfs)
            {
                AzureCloudInit.ValidateMountPath(mount.SandboxPath);
                tmpfsRoots.Add(new AzureTmpfsMount(
                    mount.SandboxPath, mount.SizeBytes is > 0 ? mount.SizeBytes.Value : SandboxConventions.CredentialsTmpfsBytes));
            }
        }
        if (!tmpfsRoots.Any(m => m.Path.TrimEnd('/').Equals(
                SandboxConventions.CredentialsDir, StringComparison.Ordinal)))
        {
            tmpfsRoots.Insert(0, new AzureTmpfsMount(
                SandboxConventions.CredentialsDir, SandboxConventions.CredentialsTmpfsBytes));
        }
        var staged = new List<AzureStagedMount>();
        foreach (var mount in spec.Mounts)
        {
            if (AzureSandbox.IsCredentialPath(mount.SandboxPath))
            {
                if (!mount.Tmpfs || !IsUnderTmpfs(mount.SandboxPath, tmpfsRoots))
                {
                    throw new NotSupportedException(
                        $"Refusing credential mount {mount.SandboxPath}: file-backed agent credentials must only " +
                        "ever be written to tmpfs, and this mount is not tmpfs-backed.");
                }
                if (mount.HostPath is null)
                    continue;
            }
            if (mount.Tmpfs && mount.HostPath is null)
                continue;
            if (mount.HostPath is null)
            {
                staged.Add(new AzureStagedMount(mount.SandboxPath, HostPath: null, Writable: !mount.ReadOnly));
                continue;
            }
            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(hostPath, $"azure mount source path does not exist: {hostPath}");
            }
            staged.Add(new AzureStagedMount(mount.SandboxPath, hostPath, Writable: !mount.ReadOnly));
        }
        return new AzureMountPlan(tmpfsRoots, staged);
    }

    internal static bool IsUnderTmpfs(string sandboxPath, IReadOnlyList<AzureTmpfsMount> tmpfsRoots)
    {
        var trimmed = sandboxPath.TrimEnd('/');
        foreach (var root in tmpfsRoots)
        {
            var rootPath = root.Path.TrimEnd('/');
            if (trimmed.Equals(rootPath, StringComparison.Ordinal)
                || trimmed.StartsWith(rootPath + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private AzureSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The azure sandbox provider is disabled. Enable it via " +
                $"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:Enabled=true plus the plugin allowlist.");
        }
        if (string.IsNullOrWhiteSpace(opts.SubscriptionId))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:SubscriptionId must be set.");
        if (string.IsNullOrWhiteSpace(opts.ResourceGroupName))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:ResourceGroupName must be set.");
        if (string.IsNullOrWhiteSpace(opts.Location))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:Location must be set.");
        if (string.IsNullOrWhiteSpace(opts.VmSize))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:VmSize must be set.");
        if (string.IsNullOrWhiteSpace(opts.VirtualNetworkName))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:VirtualNetworkName must be set.");
        if (string.IsNullOrWhiteSpace(opts.SubnetName))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:SubnetName must be set.");
        foreach (var (value, name) in new[]
                 {
                     (opts.ImagePublisher, "ImagePublisher"), (opts.ImageOffer, "ImageOffer"),
                     (opts.ImageSku, "ImageSku"), (opts.ImageVersion, "ImageVersion"),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:{name} must be set: the image is explicit and immutable.");
        }
        if (string.Equals(opts.ImageVersion.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:ImageVersion must be an immutable version, not 'latest'.");
        if (string.IsNullOrWhiteSpace(opts.VmNamePrefix) || !opts.VmNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:VmNamePrefix must start with 'codeybox-'.");
        if (opts.OrchestratorSshCidrs.Count == 0)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:OrchestratorSshCidrs must name at least one CIDR — " +
                "without it neither the provider nor any operator could SSH into a sandbox.");
        }
        foreach (var cidr in opts.OrchestratorSshCidrs)
            NormalizeCidr(cidr, nameof(opts.OrchestratorSshCidrs));
        if (string.IsNullOrWhiteSpace(opts.AdminUsername))
            throw new InvalidOperationException($"CodeyBox:Plugins:{AzureSandboxOptions.PluginId}:AdminUsername must be set.");
        return opts;
    }

    internal static string NormalizeCidr(string cidr, string optionName)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            throw new InvalidOperationException($"{optionName} contains a blank CIDR.");
        var trimmed = cidr.Trim();
        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0)
            throw new InvalidOperationException($"{optionName} entry '{cidr}' is not a CIDR (missing '/mask').");
        if (!IPAddress.TryParse(trimmed[..slash], out var address))
            throw new InvalidOperationException($"{optionName} entry '{cidr}' is not a valid CIDR address.");
        if (!int.TryParse(trimmed[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
            throw new InvalidOperationException($"{optionName} entry '{cidr}' has a non-numeric mask.");
        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (mask < 0 || mask > max)
            throw new InvalidOperationException($"{optionName} entry '{cidr}' has a mask outside 0–{max}.");
        return trimmed;
    }

    internal string ResolveOwnerId(AzureSandboxOptions opts)
    {
        var raw = string.IsNullOrWhiteSpace(opts.OwnerId) ? Environment.MachineName : opts.OwnerId;
        var slug = new string(raw.Trim().ToLowerInvariant().Select(ch =>
            (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length > 32)
            slug = slug[..32].TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? "host" : slug;
    }

    /// <summary>
    /// Maps a cloud failure onto a provisioning deferral: the cloud said no,
    /// which is an infrastructure signal — never a verdict on the work item's
    /// diff. Auth/quota failures get a longer recheck so an operator can fix
    /// credentials/capacity; throttling honours Retry-After.
    /// </summary>
    private SandboxProvisioningDeferredException ToDeferred(
        AzureSandboxOptions opts, AzureApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            AzureFailureKind.Unauthorized or AzureFailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            AzureFailureKind.QuotaExhausted =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            AzureFailureKind.Throttled =>
                ("throttled", ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : baseRecheck),
            AzureFailureKind.Conflict =>
                ("conflict", baseRecheck),
            AzureFailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            AzureFailureKind.Unreachable => ("unreachable", baseRecheck),
            AzureFailureKind.InvalidResponse => ("invalid-response", baseRecheck),
            _ => ("server-error", baseRecheck),
        };
        return new SandboxProvisioningDeferredException(
            Name, "create", errorClass, $"{context}: {ex.Message}", recheck);
    }

    private static TimeSpan MaxOf(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private static string TrimForMessage(string message)
    {
        var trimmed = (message ?? string.Empty).Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[..240];
    }

    private void MarkNoLongerActive(string name)
    {
        if (_activeSandboxes.TryRemove(name, out var entry))
        {
            entry.Sandbox.ReleaseActiveTracking();
            SandboxLiveCounter.Decrement();
        }
    }

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, AzureSandbox Sandbox);

    private sealed record AzureMountPlan(
        IReadOnlyList<AzureTmpfsMount> TmpfsMounts,
        IReadOnlyList<AzureStagedMount> Staged);

    internal sealed record AzureOwnedIds(
        string VmId,
        string NicId,
        string NsgId,
        string? PublicIpId,
        string DiskId);

    private sealed class CreatedTracker
    {
        public bool Vm;
        public bool Nic;
        public bool Nsg;
        public bool PublicIp;

        public bool Any => Vm || Nic || Nsg || PublicIp;

        public static CreatedTracker All => new() { Vm = true, Nic = true, Nsg = true, PublicIp = true };
    }
}

/// <summary>Explicit immutable platform image reference (publisher:offer:sku:version).</summary>
public sealed record AzureImageReference(string Publisher, string Offer, string Sku, string Version);
