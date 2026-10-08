using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.GceSandboxPlugin;

/// <summary>
/// Resources that teardown could not confirm deleted, retained for reconciliation.
/// A cleanup failure never erases orphan identity and never claims deletion from a
/// merely accepted request: entries stay here until a later GET proves the resource
/// gone or a retry confirms its deletion.
/// </summary>
internal sealed record GcePendingCleanup(
    string InstanceName,
    string FirewallName,
    string? AddressName,
    string Reason);

/// <summary>
/// Sandbox provider plugin backed by Google Compute Engine VMs. Contributes the
/// <c>gce</c> provider kind through the plugin trust model: the host owns egress
/// classification, so this kind is always <c>NotEnforced</c> — the guest runs on
/// infrastructure CodeyBox does not control and the host nftables egress guarantee
/// cannot apply. Acquisitions requiring enforced egress (a named network profile)
/// are refused by placement before this provider is ever called, and again here if
/// one ever arrives.
///
/// <para>Off unless an operator enables it (the <c>codeybox.gce-sandbox</c> plugin
/// must be allowlisted AND its <c>Enabled</c> option set). The workload/machine
/// identity comes only from the host credential chain (environment); the token is
/// never logged, persisted, or sent to the guest. No managed instance groups,
/// autoscaling, or Windows guests: one pinned-Linux VM per work item.</para>
/// </summary>
[CodeyBoxPlugin(GceSandboxOptions.PluginId, "Google Compute Engine sandbox provider")]
public sealed class GceSandboxProvider :
    ISandboxProvider,
    IPluginInitializer,
    IActiveSandboxProvider,
    IDisposable
{
    private static readonly TimeSpan MinimumOperatorFixRecheck = TimeSpan.FromMinutes(5);

    private readonly Func<GceSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly IGceKeyGenerator _keys;
    private readonly IGceDnsResolver _dns;
    private readonly IGceTransportFactory _transports;
    private readonly IGceCredentialSource? _credentials;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GcePendingCleanup> _unreconciled = new(StringComparer.Ordinal);
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>.</summary>
    public GceSandboxProvider(TimeProvider? clock = null)
        : this(
            readOptions: null,
            http: null,
            keys: null,
            dns: null,
            transports: null,
            credentials: null,
            environment: null,
            clock: clock,
            log: null)
    {
    }

    /// <summary>Test seam: full constructor injection.</summary>
    internal GceSandboxProvider(
        Func<GceSandboxOptions>? readOptions,
        HttpClient? http,
        IGceKeyGenerator? keys,
        IGceDnsResolver? dns,
        IGceTransportFactory? transports,
        IGceCredentialSource? credentials,
        Func<string, string?>? environment,
        TimeProvider? clock,
        ILogger? log)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = environment ?? (name => Environment.GetEnvironmentVariable(name));
        var runner = new CodeyBox.HostProcess.DefaultProcessRunner();
        _keys = keys ?? new SshKeygenGceGenerator(runner);
        _dns = dns ?? new SystemGceDnsResolver();
        _transports = transports ?? new OpenSshGceTransportFactory(runner);
        _credentials = credentials;
        _http = http ?? new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = http is null;
        _readOptions = readOptions ?? (() => GceSandboxOptions.FromConfiguration(_host?.ScopedConfig));
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
            "GCE sandbox provider initialized (enabled={Enabled}, project={Project}, zone={Zone})",
            options.Enabled,
            string.IsNullOrEmpty(options.Project) ? "(unset)" : options.Project,
            string.IsNullOrEmpty(options.Zone) ? "(unset)" : options.Zone);
        return Task.CompletedTask;
    }

    public string Name => GceSandboxOptions.ProviderKind;

    /// <inheritdoc/>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
            return true;
        try
        {
            return name.StartsWith(ReadOptions().InstanceNamePrefix, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Real VMs with a dedicated guest kernel — including genuine RAM-backed tmpfs
    /// mounts. This is a guest-boundary claim only; egress stays NotEnforced
    /// (host-classified, the plugin cannot promote itself).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>
    /// Honest capability set: fresh VM per work item, torn down on disposal. The boot
    /// image is a configured immutable pin, not a baked baseline, so
    /// <c>baseline-bake</c> is deliberately not advertised.
    /// </summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.Teardown];

    /// <summary>Resources teardown could not confirm deleted, retained for reconciliation.</summary>
    internal IReadOnlyList<string> ListUnreconciled() =>
        _unreconciled.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToList();

    private GceSandboxOptions ReadOptions() => _readOptions();

    private GceApiClient CreateClient(GceSandboxOptions opts) =>
        new(_http, opts.ComputeBaseUrl, _clock, opts.ToClientLimits());

    private async Task<string> ResolveTokenAsync(GceSandboxOptions opts, CancellationToken ct)
    {
        if (_credentials is not null)
            return await _credentials.GetAccessTokenAsync(ct).ConfigureAwait(false);
        return GceCredentialChain.Resolve(opts, _environment);
    }

    // ------------------------------------------------------------------
    // Provisioning
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = ReadValidatedOptions();
        ct.ThrowIfCancellationRequested();

        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("gce sandbox provider does not support the graphical sandbox flavor.");

        SandboxEgressPolicy.EnsureEnforcedEgressForProfile(GceSandboxOptions.ProviderKind, spec.Network.ProfileName);

        var ownerId = ResolveOwnerId(opts);
        var region = RegionForZone(opts.Zone);
        var mounts = PlanMounts(spec);
        var token = await ResolveTokenAsync(opts, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var api = CreateClient(opts);
        var workItemId = spec.TimingWorkItemId?.ToString() ?? string.Empty;

        var sourceImage = ResolveSourceImage(spec.ImageReference, opts);
        var machineType = ResolveMachineType(opts);
        var network = ResolveNetwork(opts);
        var subnetwork = ResolveSubnetwork(opts, region);

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..24];
        var instanceName = opts.InstanceNamePrefix + suffix;
        var firewallName = instanceName + "-fw";
        var addressName = instanceName + "-ip";
        var requestId = NewRequestId();
        var createdAt = _clock.GetUtcNow();

        var sshTempDirectory = Path.Combine(Path.GetTempPath(), "codeybox-gce-" + suffix);
        Directory.CreateDirectory(sshTempDirectory);

        string? reservedAddress = null;
        var firewallReady = false;
        var instanceInserted = false;
        try
        {
            var clientKey = await _keys.GenerateClientKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, instanceName, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var hostKey = await _keys.GenerateHostKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, instanceName + "-host", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (opts.ReserveStaticAddress)
            {
                reservedAddress = await ReserveAddressAsync(
                    api, token, opts, region, addressName, ownerId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            await EnsureFirewallAsync(
                api, token, opts, network, instanceName, firewallName, ct).ConfigureAwait(false);
            firewallReady = true;
            ct.ThrowIfCancellationRequested();

            var userData = GceCloudInit.Build(new GceStartupScriptSpec(
                instanceName, opts.SshUser, hostKey.PublicKeyText,
                hostKey.PrivateKeyPem, mounts.TmpfsMounts));

            var labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GceNaming.ManagedLabelKey] = "true",
                [GceNaming.OwnerLabelKey] = ownerId,
                [GceNaming.WorkItemLabelKey] = string.IsNullOrWhiteSpace(workItemId) ? "none" : workItemId,
            };
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GceNaming.MetadataManagedKey] = "true",
                [GceNaming.MetadataOwnerKey] = ownerId,
                [GceNaming.MetadataWorkItemKey] = workItemId,
                [GceNaming.MetadataCreatedKey] = createdAt.ToString("o", CultureInfo.InvariantCulture),
                [GceNaming.MetadataFirewallKey] = firewallName,
                [GceNaming.MetadataAddressKey] = addressName,
            };
            var instanceSpec = new GceInstanceSpec(
                instanceName, machineType, sourceImage, network, subnetwork,
                opts.BootDiskSizeGb, opts.BootDiskAutoDelete,
                labels, metadata, [instanceName],
                $"{opts.SshUser}:{clientKey.PublicKeyText} codeybox-{instanceName}",
                userData, reservedAddress);
            await InsertWithReconcileAsync(
                api, token, opts, instanceSpec, requestId, ownerId, workItemId, ct).ConfigureAwait(false);
            instanceInserted = true;
            ct.ThrowIfCancellationRequested();

            var instance = await WaitForInstanceRunningAsync(api, token, opts, instanceName, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var address = ResolveExternalAddress(instance, reservedAddress, instanceName);

            var knownHostsPath = Path.Combine(sshTempDirectory, "known_hosts");
            await WriteKnownHostsAsync(knownHostsPath, address, hostKey.PublicKeyText, ct).ConfigureAwait(false);
            var transport = _transports.Create(new GceSshTransportSpec(
                $"{opts.SshUser}@{address}", opts.SshPort, clientKey.PrivateKeyPath,
                knownHostsPath, opts.SshBinary, opts.SshConnectTimeoutSeconds));
            await WaitForSshReadyAsync(transport, opts, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var sandbox = new GceSandbox(
                instanceName, spec, mounts.Staged, transport,
                () => DeleteCloudResourcesAsync(instanceName, firewallName, opts.ReserveStaticAddress ? addressName : null, region),
                () => MarkNoLongerActive(instanceName),
                sshTempDirectory, _log);
            _activeSandboxes[instanceName] = new ActiveSandboxEntry(
                spec.TimingWorkItemId ?? default, sandbox);
            SandboxLiveCounter.Increment();

            try
            {
                await PrepareGuestFilesystemAsync(transport, mounts, ct).ConfigureAwait(false);
                foreach (var staged in mounts.Staged)
                {
                    ct.ThrowIfCancellationRequested();
                    if (staged.HostPath is null)
                        continue;
                    await transport.StageInAsync(staged.HostPath, staged.RemotePath, ct).ConfigureAwait(false);
                }
            }
            catch (RemoteSshTransportException ex)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "stage", "staging-unavailable",
                    $"gce staging to {instanceName} failed: {ex.Message}",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }

            _log.LogInformation("Created gce sandbox {Name}", instanceName);
            return sandbox;
        }
        catch (SandboxProvisioningDeferredException)
        {
            await CleanupAfterFailureAsync(
                instanceName, firewallName, opts.ReserveStaticAddress ? addressName : null,
                region, firewallReady, instanceInserted, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailureAsync(
                instanceName, firewallName, opts.ReserveStaticAddress ? addressName : null,
                region, firewallReady, instanceInserted, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            await CleanupAfterFailureAsync(
                instanceName, firewallName, opts.ReserveStaticAddress ? addressName : null,
                region, firewallReady, instanceInserted, sshTempDirectory).ConfigureAwait(false);
            if (ex is GceApiException apiEx)
                throw ToDeferred(opts, apiEx, $"gce create failed for {instanceName}");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var ownerId = ResolveOwnerId(opts);
        var token = await ResolveTokenAsync(opts, ct).ConfigureAwait(false);
        var api = CreateClient(opts);
        var instances = await api.ListInstancesAsync(
            token, opts.Project, opts.Zone, OwnerFilter(ownerId), ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>(instances.Count);
        foreach (var instance in instances)
        {
            if (instance.Name is null || !instance.Name.StartsWith(opts.InstanceNamePrefix, StringComparison.Ordinal))
                continue;
            if (!IsOwnedBy(instance.Labels, ownerId))
                continue;
            DateTimeOffset? createdAt = null;
            if (instance.Metadata?.TryGetValue(GceNaming.MetadataCreatedKey, out var createdRaw) == true
                && DateTimeOffset.TryParse(createdRaw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }
            result.Add(new ManagedSandboxInfo(
                instance.Name, createdAt, DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(instance.Name)));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(opts.InstanceNamePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"GCE sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var ownerId = ResolveOwnerId(opts);
        var region = RegionForZone(opts.Zone);
        var token = await ResolveTokenAsync(opts, ct).ConfigureAwait(false);
        var api = CreateClient(opts);
        var instance = await api.GetInstanceAsync(token, opts.Project, opts.Zone, name, ct).ConfigureAwait(false);
        if (instance?.Name is not null && IsOwnedBy(instance.Labels, ownerId))
        {
            string? firewallLink = null;
            string? addressLink = null;
            instance.Metadata?.TryGetValue(GceNaming.MetadataFirewallKey, out firewallLink);
            instance.Metadata?.TryGetValue(GceNaming.MetadataAddressKey, out addressLink);
            await DeleteCloudResourcesAsync(name, firewallLink, addressLink, region).ConfigureAwait(false);
        }
        else
        {
            await DeleteCloudResourcesAsync(name, name + "-fw", name + "-ip", region).ConfigureAwait(false);
        }
        MarkNoLongerActive(name);
        await SweepOrphanResourcesAsync(api, token, opts, ownerId, region, ct).ConfigureAwait(false);
    }

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

    // ------------------------------------------------------------------
    // Internal lifecycle helpers
    // ------------------------------------------------------------------

    private async Task InsertWithReconcileAsync(
        GceApiClient api,
        string token,
        GceSandboxOptions opts,
        GceInstanceSpec spec,
        string requestId,
        string ownerId,
        string workItemId,
        CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            ct.ThrowIfCancellationRequested();
            GceOperation operation;
            try
            {
                operation = await api.InsertInstanceAsync(
                    token, opts.Project, opts.Zone, spec, requestId, ct).ConfigureAwait(false);
            }
            catch (GceApiException ex) when (ex.Kind == GceFailureKind.Conflict)
            {
                var existing = await api.GetInstanceAsync(token, opts.Project, opts.Zone, spec.Name, ct).ConfigureAwait(false);
                if (existing?.Name is not null && IsOwnedBy(existing.Labels, ownerId))
                    return;
                throw new InvalidOperationException(
                    $"GCE instance '{spec.Name}' already exists under another owner; refusing to adopt or delete it.");
            }
            catch (Exception ex) when (IsUnknownOutcome(ex) && attempts < opts.MaxInsertAttempts)
            {
                var existing = await ReconcileAfterUnknownInsertAsync(api, token, opts, spec.Name, ownerId, ct).ConfigureAwait(false);
                if (existing)
                    return;
                continue;
            }

            try
            {
                await api.WaitForZoneOperationAsync(
                    token, opts.Project, opts.Zone, operation.Name,
                    TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds), ct).ConfigureAwait(false);
                return;
            }
            catch (GceApiException ex) when (ex.Kind == GceFailureKind.Transient && attempts < opts.MaxInsertAttempts)
            {
                var existing = await ReconcileAfterUnknownInsertAsync(api, token, opts, spec.Name, ownerId, ct).ConfigureAwait(false);
                if (existing)
                    return;
            }
        }
    }

    private static bool IsUnknownOutcome(Exception ex) =>
        ex is GceApiException apiEx && (apiEx.Kind is GceFailureKind.Transient or GceFailureKind.Protocol);

    private static async Task<bool> ReconcileAfterUnknownInsertAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string name, string ownerId, CancellationToken ct)
    {
        var existing = await api.GetInstanceAsync(token, opts.Project, opts.Zone, name, ct).ConfigureAwait(false);
        if (existing?.Name is null)
            return false;
        if (!IsOwnedBy(existing.Labels, ownerId))
        {
            throw new InvalidOperationException(
                $"GCE instance '{name}' exists under another owner after an ambiguous create; " +
                "refusing to adopt, delete, or resubmit over it.");
        }
        return true;
    }

    private async Task<GceInstance> WaitForInstanceRunningAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string name, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var instance = await api.GetInstanceAsync(token, opts.Project, opts.Zone, name, ct).ConfigureAwait(false);
            if (instance?.Status is not null && instance.Status.Equals("RUNNING", StringComparison.OrdinalIgnoreCase))
                return instance;
            if (instance?.Status is not null
                && (instance.Status.Equals("TERMINATED", StringComparison.OrdinalIgnoreCase)
                    || instance.Status.Equals("STOPPED", StringComparison.OrdinalIgnoreCase)
                    || instance.Status.Equals("STOPPING", StringComparison.OrdinalIgnoreCase)))
            {
                throw new GceApiException(
                    GceFailureKind.Unexpected, "wait instance running",
                    $"instance '{name}' entered terminal status '{instance.Status}'; refusing to treat it as ready.");
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "instance-ready", "instance-unready",
                    $"gce instance {name} did not reach RUNNING before the readiness deadline",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private static string ResolveExternalAddress(GceInstance instance, string? reservedAddress, string name)
    {
        var natIp = instance.NetworkInterfaces?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.NatIp))?.NatIp;
        if (!string.IsNullOrWhiteSpace(natIp))
            return natIp.Trim();
        if (!string.IsNullOrWhiteSpace(reservedAddress))
        {
            throw new GceApiException(
                GceFailureKind.Unexpected, "resolve instance address",
                $"instance '{name}' exposes no external NAT IP although an address was reserved; " +
                "refusing to SSH at an unconfirmed target.");
        }
        throw new GceApiException(
            GceFailureKind.Unexpected, "resolve instance address",
            $"instance '{name}' exposes no external IP; refusing to SSH at an unconfirmed target.");
    }

    private async Task<string?> ReserveAddressAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string region,
        string addressName, string ownerId, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GceNaming.ManagedLabelKey] = "true",
            [GceNaming.OwnerLabelKey] = ownerId,
        };
        GceOperation operation;
        try
        {
            operation = await api.InsertAddressAsync(
                token, opts.Project, region, addressName, opts.AddressType, labels, NewRequestId(), ct).ConfigureAwait(false);
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.Conflict)
        {
            var existing = await api.GetAddressAsync(token, opts.Project, region, addressName, ct).ConfigureAwait(false);
            if (existing?.Name is not null && IsOwnedBy(existing.Labels, ownerId))
                return existing.Address;
            throw new InvalidOperationException(
                $"GCE address '{addressName}' already exists under another owner; refusing to adopt it.");
        }
        await api.WaitForRegionOperationAsync(
            token, opts.Project, region, operation.Name,
            TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds), ct).ConfigureAwait(false);
        var address = await api.GetAddressAsync(token, opts.Project, region, addressName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(address?.Address))
        {
            throw new GceApiException(
                GceFailureKind.Unexpected, "reserve address",
                $"address '{addressName}' reserved but reports no IP; refusing to treat it as usable.");
        }
        return address.Address;
    }

    private async Task EnsureFirewallAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string network,
        string instanceName, string firewallName, CancellationToken ct)
    {
        GceOperation operation;
        try
        {
            operation = await api.InsertFirewallAsync(
                token, opts.Project, firewallName, network,
                [.. opts.OrchestratorSshCidrs], [instanceName], opts.SshPort,
                NewRequestId(), ct).ConfigureAwait(false);
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.Conflict)
        {
            var existing = await api.GetFirewallAsync(token, opts.Project, firewallName, ct).ConfigureAwait(false);
            if (IsOwnedFirewall(existing, firewallName, network, instanceName))
                return;
            throw new InvalidOperationException(
                $"GCE firewall '{firewallName}' already exists outside this sandbox's ownership; " +
                "refusing to adopt or replace it.");
        }
        await api.WaitForGlobalOperationAsync(
            token, opts.Project, operation.Name,
            TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds), ct).ConfigureAwait(false);
    }

    internal static bool IsOwnedFirewall(
        GceFirewall? firewall, string firewallName, string network, string instanceName)
    {
        if (firewall?.Name is null || !string.Equals(firewall.Name, firewallName, StringComparison.Ordinal))
            return false;
        if (firewall.Network is null || !string.Equals(firewall.Network, network, StringComparison.Ordinal))
            return false;
        return firewall.TargetTags?.Contains(instanceName, StringComparer.Ordinal) == true;
    }

    private async Task WaitForSshReadyAsync(
        IRemoteHostTransport transport, GceSandboxOptions opts, CancellationToken ct)
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
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "ssh-ready", "ssh-unready",
                    "gce instance did not accept SSH before the readiness deadline",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan NextPollDelay(GceSandboxOptions opts, int attempt)
    {
        var baseMs = (double)Math.Max(200, opts.PollIntervalMilliseconds);
        var doubled = baseMs * (1L << Math.Min(attempt, 10));
        var capped = Math.Min(doubled, opts.MaxPollIntervalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(capped, 1));
    }

    private static async Task WriteKnownHostsAsync(
        string knownHostsPath, string address, string hostPublicKey, CancellationToken ct)
    {
        var line = address.Trim() + " " + hostPublicKey.Trim() + "\n";
        await File.WriteAllTextAsync(knownHostsPath, line, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task PrepareGuestFilesystemAsync(
        IRemoteHostTransport transport,
        GceMountPlan mounts,
        CancellationToken ct)
    {
        var parents = mounts.Staged
            .Select(m => ParentOf(m.RemotePath))
            .Append(SandboxConventions.WorkDir)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var script = "set -e; " + string.Join("; ", parents.Select(p => "mkdir -p " + GceSandbox.QuoteShellWord(p)));
        var run = await transport.RunAsync(["bash", "-c", script], stdin: null, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new GceApiException(
                GceFailureKind.Unexpected, "prepare guest filesystem",
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
        string instanceName,
        string firewallName,
        string? addressName,
        string region,
        bool firewallReady,
        bool instanceInserted,
        string sshTempDirectory)
    {
        MarkNoLongerActive(instanceName);
        if (instanceInserted || firewallReady || addressName is not null)
            await DeleteCloudResourcesAsync(instanceName, firewallName, addressName, region).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(sshTempDirectory))
                Directory.Delete(sshTempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE sandbox {Name}: failed to remove SSH key directory", instanceName);
        }
    }

    private async Task DeleteCloudResourcesAsync(
        string instanceName,
        string? firewallName,
        string? addressName,
        string region)
    {
        GceSandboxOptions opts;
        string token;
        GceApiClient api;
        try
        {
            opts = ReadValidatedOptions();
            token = await ResolveTokenAsync(opts, CancellationToken.None).ConfigureAwait(false);
            api = CreateClient(opts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE sandbox cleanup: cannot resolve credentials/options; cloud resources may leak");
            _unreconciled[instanceName] = new GcePendingCleanup(instanceName, firewallName ?? instanceName + "-fw", addressName, "cleanup without credentials");
            return;
        }

        var ownerId = ResolveOwnerId(opts);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600)));
        var ct = cts.Token;
        var instanceGone = await DeleteOwnedInstanceAsync(api, token, opts, instanceName, ownerId, ct).ConfigureAwait(false);
        if (!instanceGone)
        {
            _unreconciled[instanceName] = new GcePendingCleanup(instanceName, firewallName ?? instanceName + "-fw", addressName, "instance deletion unconfirmed");
            return;
        }
        if (!opts.BootDiskAutoDelete)
            await DeleteOwnedBootDiskAsync(api, token, opts, instanceName, ownerId, ct).ConfigureAwait(false);
        if (addressName is not null)
            await DeleteOwnedAddressAsync(api, token, opts, region, addressName, ownerId, ct).ConfigureAwait(false);
        if (firewallName is not null)
            await DeleteOwnedFirewallAsync(api, token, opts, instanceName, firewallName, ct).ConfigureAwait(false);
        _unreconciled.TryRemove(instanceName, out _);
    }

    private async Task<bool> DeleteOwnedInstanceAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string instanceName, string ownerId, CancellationToken ct)
    {
        var (readOk, existing) = await TryGetAsync(
            () => api.GetInstanceAsync(token, opts.Project, opts.Zone, instanceName, ct),
            "instance",
            instanceName,
            new GcePendingCleanup(instanceName, instanceName + "-fw", null, "instance status read failed; deletion unconfirmed")).ConfigureAwait(false);
        if (!readOk)
            return false;
        if (existing?.Name is null)
            return true;
        if (!IsOwnedBy(existing.Labels, ownerId))
        {
            _log.LogWarning(
                "GCE instance {Name}: ownership changed since provisioning; refusing to delete another owner's VM",
                instanceName);
            _unreconciled[instanceName] = new GcePendingCleanup(instanceName, instanceName + "-fw", null, "ownership mismatch at deletion");
            return false;
        }
        GceOperation operation;
        try
        {
            operation = await api.DeleteInstanceAsync(token, opts.Project, opts.Zone, instanceName, NewRequestId(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE instance {Name}: delete request failed; leak reaper will retry", instanceName);
            return false;
        }
        try
        {
            await api.WaitForZoneOperationAsync(
                token, opts.Project, opts.Zone, operation.Name,
                TimeSpan.FromSeconds(Math.Min(120, opts.ReadyTimeoutSeconds)), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE instance {Name}: deletion not confirmed; leak reaper will retry", instanceName);
            return false;
        }
        var (verifyOk, gone) = await TryGetAsync(
            () => api.GetInstanceAsync(token, opts.Project, opts.Zone, instanceName, ct),
            "instance",
            instanceName,
            new GcePendingCleanup(instanceName, instanceName + "-fw", null, "instance deletion unverified; status read failed")).ConfigureAwait(false);
        if (!verifyOk)
            return false;
        if (gone?.Name is not null)
        {
            _log.LogWarning("GCE instance {Name}: still present after delete confirmed; leak reaper will retry", instanceName);
            return false;
        }
        return true;
    }

    private async Task DeleteOwnedBootDiskAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string instanceName, string ownerId, CancellationToken ct)
    {
        var (diskReadOk, disk) = await TryGetAsync(
            () => api.GetDiskAsync(token, opts.Project, opts.Zone, instanceName, ct),
            "disk",
            instanceName,
            new GcePendingCleanup(instanceName, instanceName + "-fw", null, "disk status read failed; deletion unconfirmed")).ConfigureAwait(false);
        if (!diskReadOk)
            return;
        if (disk?.Name is null)
            return;
        if (!IsOwnedBy(disk.Labels, ownerId))
        {
            _log.LogWarning("GCE disk {Name}: not owned by this host; refusing to delete", disk.Name);
            _unreconciled[instanceName] = new GcePendingCleanup(instanceName, instanceName + "-fw", null, "disk ownership mismatch");
            return;
        }
        try
        {
            var operation = await api.DeleteDiskAsync(token, opts.Project, opts.Zone, disk.Name, NewRequestId(), ct).ConfigureAwait(false);
            await api.WaitForZoneOperationAsync(
                token, opts.Project, opts.Zone, operation.Name,
                TimeSpan.FromSeconds(Math.Min(120, opts.ReadyTimeoutSeconds)), ct).ConfigureAwait(false);
            var (diskVerifyOk, gone) = await TryGetAsync(
                () => api.GetDiskAsync(token, opts.Project, opts.Zone, disk.Name, ct),
                "disk",
                instanceName,
                new GcePendingCleanup(instanceName, instanceName + "-fw", null, "disk deletion unverified; status read failed")).ConfigureAwait(false);
            if (!diskVerifyOk)
                return;
            if (gone?.Name is not null)
            {
                _log.LogWarning("GCE disk {Name}: still present after delete confirmed; leak reaper will retry", disk.Name);
                _unreconciled[instanceName] = new GcePendingCleanup(instanceName, instanceName + "-fw", null, "disk deletion unconfirmed");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE disk {Name}: delete failed; leak reaper will retry", disk.Name);
            _unreconciled[instanceName] = new GcePendingCleanup(instanceName, instanceName + "-fw", null, "disk delete failed");
        }
    }

    private async Task DeleteOwnedAddressAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string region,
        string addressName, string ownerId, CancellationToken ct)
    {
        var (addressReadOk, existing) = await TryGetAsync(
            () => api.GetAddressAsync(token, opts.Project, region, addressName, ct),
            "address",
            addressName,
            new GcePendingCleanup(addressName, addressName, addressName, "address status read failed; deletion unconfirmed")).ConfigureAwait(false);
        if (!addressReadOk)
            return;
        if (existing?.Name is null)
            return;
        if (!IsOwnedBy(existing.Labels, ownerId))
        {
            _log.LogWarning("GCE address {Name}: not owned by this host; refusing to delete", addressName);
            _unreconciled[addressName] = new GcePendingCleanup(addressName, addressName, addressName, "address ownership mismatch");
            return;
        }
        try
        {
            var operation = await api.DeleteAddressAsync(token, opts.Project, region, addressName, NewRequestId(), ct).ConfigureAwait(false);
            await api.WaitForRegionOperationAsync(
                token, opts.Project, region, operation.Name,
                TimeSpan.FromSeconds(Math.Min(120, opts.ReadyTimeoutSeconds)), ct).ConfigureAwait(false);
            var (addressVerifyOk, gone) = await TryGetAsync(
                () => api.GetAddressAsync(token, opts.Project, region, addressName, ct),
                "address",
                addressName,
                new GcePendingCleanup(addressName, addressName, addressName, "address deletion unverified; status read failed")).ConfigureAwait(false);
            if (!addressVerifyOk)
                return;
            if (gone?.Name is not null)
            {
                _log.LogWarning("GCE address {Name}: still present after delete confirmed; leak reaper will retry", addressName);
                _unreconciled[addressName] = new GcePendingCleanup(addressName, addressName, addressName, "address deletion unconfirmed");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE address {Name}: delete failed; leak reaper will retry", addressName);
            _unreconciled[addressName] = new GcePendingCleanup(addressName, addressName, addressName, "address delete failed");
        }
    }

    private async Task DeleteOwnedFirewallAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string instanceName,
        string firewallName, CancellationToken ct)
    {
        var network = ResolveNetwork(opts);
        var (firewallReadOk, existing) = await TryGetAsync(
            () => api.GetFirewallAsync(token, opts.Project, firewallName, ct),
            "firewall",
            firewallName,
            new GcePendingCleanup(instanceName, firewallName, null, "firewall status read failed; deletion unconfirmed")).ConfigureAwait(false);
        if (!firewallReadOk)
            return;
        if (existing?.Name is null)
            return;
        if (!IsOwnedFirewall(existing, firewallName, network, instanceName))
        {
            _log.LogWarning("GCE firewall {Name}: linkage check failed; refusing to delete", firewallName);
            _unreconciled[firewallName] = new GcePendingCleanup(instanceName, firewallName, null, "firewall linkage mismatch");
            return;
        }
        try
        {
            var operation = await api.DeleteFirewallAsync(token, opts.Project, firewallName, NewRequestId(), ct).ConfigureAwait(false);
            await api.WaitForGlobalOperationAsync(
                token, opts.Project, operation.Name,
                TimeSpan.FromSeconds(Math.Min(120, opts.ReadyTimeoutSeconds)), ct).ConfigureAwait(false);
            var (firewallVerifyOk, gone) = await TryGetAsync(
                () => api.GetFirewallAsync(token, opts.Project, firewallName, ct),
                "firewall",
                firewallName,
                new GcePendingCleanup(instanceName, firewallName, null, "firewall deletion unverified; status read failed")).ConfigureAwait(false);
            if (!firewallVerifyOk)
                return;
            if (gone?.Name is not null)
            {
                _log.LogWarning("GCE firewall {Name}: still present after delete confirmed; leak reaper will retry", firewallName);
                _unreconciled[firewallName] = new GcePendingCleanup(instanceName, firewallName, null, "firewall deletion unconfirmed");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE firewall {Name}: delete failed; leak reaper will retry", firewallName);
            _unreconciled[firewallName] = new GcePendingCleanup(instanceName, firewallName, null, "firewall delete failed");
        }
    }

    /// <summary>
    /// Best-effort status read that preserves the client's 404-vs-failure distinction:
    /// a missing resource reports <c>(true, null)</c>, while any other read failure records
    /// the pending cleanup (so the leak reaper retries instead of forgetting the resource)
    /// and reports <c>(false, null)</c>. Callers must treat <c>ReadOk: false</c> as
    /// "deletion unconfirmed", never as "resource absent".
    /// </summary>
    private async Task<(bool ReadOk, T? Value)> TryGetAsync<T>(
        Func<Task<T?>> get, string kind, string reconcileKey, GcePendingCleanup readFailure) where T : class
    {
        try
        {
            return (true, await get().ConfigureAwait(false));
        }
        catch (GceApiException ex) when (ex.Kind == GceFailureKind.NotFound)
        {
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GCE cleanup: failed to read {Kind}; retaining {Resource} as unreconciled", kind, reconcileKey);
            _unreconciled[reconcileKey] = readFailure;
            return (false, null);
        }
    }

    private async Task SweepOrphanResourcesAsync(
        GceApiClient api, string token, GceSandboxOptions opts, string ownerId, string region, CancellationToken ct)
    {
        var instances = await api.ListInstancesAsync(
            token, opts.Project, opts.Zone, OwnerFilter(ownerId), ct).ConfigureAwait(false);
        var liveNames = new HashSet<string>(StringComparer.Ordinal);
        var linkedFirewalls = new HashSet<string>(StringComparer.Ordinal);
        var linkedAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            if (instance.Name is null || !IsOwnedBy(instance.Labels, ownerId))
                continue;
            liveNames.Add(instance.Name);
            if (instance.Metadata?.TryGetValue(GceNaming.MetadataFirewallKey, out var firewall) == true
                && !string.IsNullOrWhiteSpace(firewall))
            {
                linkedFirewalls.Add(firewall);
            }
            if (instance.Metadata?.TryGetValue(GceNaming.MetadataAddressKey, out var address) == true
                && !string.IsNullOrWhiteSpace(address))
            {
                linkedAddresses.Add(address);
            }
        }

        var network = ResolveNetwork(opts);
        var swept = 0;
        var budget = Math.Max(1, Math.Min(opts.MaxListItems, 100));
        // Firewalls carry no ownership labels, so the live-target veto must see every
        // owner's instances: the owner-scoped listing above cannot distinguish a foreign
        // owner's live rule from an orphan. Skip deletion whenever the target tag names
        // any existing managed instance, regardless of owner.
        var allOwnerInstances = await api.ListInstancesAsync(
            token, opts.Project, opts.Zone, ManagedFilter(), ct).ConfigureAwait(false);
        var anyOwnerInstanceNames = new HashSet<string>(
            allOwnerInstances.Where(i => i.Name is not null).Select(i => i.Name!), StringComparer.Ordinal);
        foreach (var firewall in await api.ListFirewallsAsync(token, opts.Project, filter: null, ct).ConfigureAwait(false))
        {
            if (swept >= budget)
                break;
            if (firewall.Name is null || !firewall.Name.StartsWith("codeybox-", StringComparison.Ordinal))
                continue;
            if (!string.Equals(firewall.Network, network, StringComparison.Ordinal))
                continue;
            var target = firewall.TargetTags?.FirstOrDefault();
            if (target is not null && anyOwnerInstanceNames.Contains(target))
                continue;
            if (linkedFirewalls.Contains(firewall.Name))
                continue;
            try
            {
                var operation = await api.DeleteFirewallAsync(token, opts.Project, firewall.Name, NewRequestId(), ct).ConfigureAwait(false);
                await api.WaitForGlobalOperationAsync(
                    token, opts.Project, operation.Name, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                swept++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orphan sweep: failed to delete firewall {Name}", firewall.Name);
            }
        }
        foreach (var address in await api.ListAddressesAsync(token, opts.Project, region, OwnerFilter(ownerId), ct).ConfigureAwait(false))
        {
            if (swept >= budget)
                break;
            if (address.Name is null || !IsOwnedBy(address.Labels, ownerId))
                continue;
            if (linkedAddresses.Contains(address.Name))
                continue;
            try
            {
                var operation = await api.DeleteAddressAsync(token, opts.Project, region, address.Name, NewRequestId(), ct).ConfigureAwait(false);
                await api.WaitForRegionOperationAsync(
                    token, opts.Project, region, operation.Name, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                swept++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orphan sweep: failed to delete address {Name}", address.Name);
            }
        }
        if (!opts.BootDiskAutoDelete)
        {
            foreach (var disk in await api.ListDisksAsync(token, opts.Project, opts.Zone, OwnerFilter(ownerId), ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                if (disk.Name is null || !IsOwnedBy(disk.Labels, ownerId))
                    continue;
                if (liveNames.Contains(disk.Name))
                    continue;
                try
                {
                    var operation = await api.DeleteDiskAsync(token, opts.Project, opts.Zone, disk.Name, NewRequestId(), ct).ConfigureAwait(false);
                    await api.WaitForZoneOperationAsync(
                        token, opts.Project, opts.Zone, operation.Name, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    swept++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Orphan sweep: failed to delete disk {Name}", disk.Name);
                }
            }
        }
        if (swept > 0)
            _log.LogInformation("Orphan sweep: released {Count} unreferenced GCE resource(s)", swept);
    }

    // ------------------------------------------------------------------
    // Provisioning guards
    // ------------------------------------------------------------------

    internal static bool IsOwnedBy(IReadOnlyDictionary<string, string>? labels, string ownerId)
    {
        if (labels is null)
            return false;
        return labels.TryGetValue(GceNaming.ManagedLabelKey, out var managed)
            && string.Equals(managed, "true", StringComparison.Ordinal)
            && labels.TryGetValue(GceNaming.OwnerLabelKey, out var owner)
            && string.Equals(owner, ownerId, StringComparison.Ordinal);
    }

    internal static string OwnerFilter(string ownerId) =>
        $"{GceNaming.ManagedLabelKey} = \"true\" AND {GceNaming.OwnerLabelKey} = \"{ownerId}\"";

    internal static string ManagedFilter() =>
        $"{GceNaming.ManagedLabelKey} = \"true\"";

    internal static string NewRequestId()
    {
        var id = Guid.NewGuid();
        return id == Guid.Empty ? Guid.NewGuid().ToString() : id.ToString();
    }

    internal static string RegionForZone(string zone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        var index = zone.LastIndexOf('-');
        if (index <= 0)
            throw new ArgumentException($"Zone '{zone}' does not look like a GCE zone (expected e.g. europe-west1-b).", nameof(zone));
        return zone[..index];
    }

    internal static string ResolveSourceImage(string? imageReference, GceSandboxOptions opts)
    {
        var reference = string.IsNullOrWhiteSpace(imageReference) ? opts.ImageName : imageReference.Trim();
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidOperationException(
                $"No GCE image configured: set CodeyBox:Plugins:{GceSandboxOptions.PluginId}:ImageName " +
                "to a pinned concrete image (self-link or projects/…/global/images/…).");
        }
        if (reference.Contains("/family/", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("families/", StringComparison.OrdinalIgnoreCase)
            || reference.EndsWith("/family", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"GCE image '{reference}' names an image family, which moves over time. " +
                "Pin a concrete image version instead (a self-link or projects/…/global/images/… path).");
        }
        if (reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            if (!reference.Contains("/global/images/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GCE image '{reference}' must name a concrete image under global/images (a self-link or " +
                    "projects/…/global/images/… path); bare names and family references are rejected.");
            }
            return reference;
        }
        throw new InvalidOperationException(
            $"GCE image '{reference}' must be a pinned concrete image (a self-link or " +
            "projects/…/global/images/… path); bare image names are rejected so the guest can never follow a rename.");
    }

    internal static void ValidateCidr(string cidr, string optionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidr);
        var slash = cidr.LastIndexOf('/');
        if (slash <= 0)
            throw new InvalidOperationException($"{optionName} entry '{cidr}' is not a CIDR (expected address/prefix).");
        var address = cidr[..slash].Trim();
        var prefix = cidr[(slash + 1)..].Trim();
        if (!IPAddress.TryParse(address, out var parsed))
            throw new InvalidOperationException($"{optionName} entry '{cidr}' is not a valid CIDR address.");
        var maxPrefix = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (!int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            || bits < 0 || bits > maxPrefix)
        {
            throw new InvalidOperationException($"{optionName} entry '{cidr}' has an invalid prefix length.");
        }
    }

    private static string ResolveMachineType(GceSandboxOptions opts)
    {
        if (opts.MachineType.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || opts.MachineType.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            return opts.MachineType;
        }
        return $"projects/{opts.Project}/zones/{opts.Zone}/machineTypes/{opts.MachineType}";
    }

    private static string ResolveNetwork(GceSandboxOptions opts)
    {
        if (opts.Network.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || opts.Network.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            return opts.Network;
        }
        return $"projects/{opts.Project}/global/networks/{opts.Network}";
    }

    private static string ResolveSubnetwork(GceSandboxOptions opts, string region)
    {
        if (opts.Subnetwork.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || opts.Subnetwork.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            return opts.Subnetwork;
        }
        return $"projects/{opts.Project}/regions/{region}/subnetworks/{opts.Subnetwork}";
    }

    private GceMountPlan PlanMounts(SandboxSpec spec)
    {
        var tmpfsRoots = new List<GceTmpfsMount>();
        foreach (var mount in spec.Mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.SandboxPath) || !mount.SandboxPath.StartsWith('/'))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");
            if (mount.Tmpfs)
            {
                GceCloudInit.ValidateTmpfsMount(new GceTmpfsMount(
                    mount.SandboxPath, mount.SizeBytes is > 0 ? mount.SizeBytes.Value : SandboxConventions.CredentialsTmpfsBytes));
                tmpfsRoots.Add(new GceTmpfsMount(
                    mount.SandboxPath, mount.SizeBytes is > 0 ? mount.SizeBytes.Value : SandboxConventions.CredentialsTmpfsBytes));
            }
        }
        if (!tmpfsRoots.Any(m => m.Path.TrimEnd('/').Equals(
                SandboxConventions.CredentialsDir, StringComparison.Ordinal)))
        {
            tmpfsRoots.Insert(0, new GceTmpfsMount(
                SandboxConventions.CredentialsDir, SandboxConventions.CredentialsTmpfsBytes));
        }
        var staged = new List<GceStagedMount>();
        foreach (var mount in spec.Mounts)
        {
            if (GceSandbox.IsCredentialPath(mount.SandboxPath))
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
                staged.Add(new GceStagedMount(mount.SandboxPath, HostPath: null, Writable: !mount.ReadOnly));
                continue;
            }
            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(hostPath, $"gce mount source path does not exist: {hostPath}");
            }
            staged.Add(new GceStagedMount(mount.SandboxPath, hostPath, Writable: !mount.ReadOnly));
        }
        return new GceMountPlan(tmpfsRoots, staged);
    }

    internal static bool IsUnderTmpfs(string sandboxPath, IReadOnlyList<GceTmpfsMount> tmpfsRoots)
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

    private GceSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The gce sandbox provider is disabled. Enable it via " +
                $"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:Enabled=true plus the plugin allowlist.");
        }
        if (string.IsNullOrWhiteSpace(opts.Project))
            throw new InvalidOperationException($"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:Project must be set.");
        if (string.IsNullOrWhiteSpace(opts.Zone))
            throw new InvalidOperationException($"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:Zone must be set.");
        _ = RegionForZone(opts.Zone);
        if (string.IsNullOrWhiteSpace(opts.MachineType))
            throw new InvalidOperationException($"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:MachineType must be set.");
        if (string.IsNullOrWhiteSpace(opts.Network))
            throw new InvalidOperationException($"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:Network must be set.");
        if (string.IsNullOrWhiteSpace(opts.Subnetwork))
            throw new InvalidOperationException($"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:Subnetwork must be set.");
        _ = ResolveSourceImage(imageReference: null, opts);
        if (string.IsNullOrWhiteSpace(opts.InstanceNamePrefix)
            || !opts.InstanceNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:InstanceNamePrefix must start with 'codeybox-'.");
        }
        var probe = opts.InstanceNamePrefix + new string('a', 24) + "-fw";
        if (!GceNaming.IsValidInstanceName(opts.InstanceNamePrefix + "a") || probe.Length > 63)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:InstanceNamePrefix must satisfy the GCE " +
                "instance-name grammar and leave room for the generated suffix and firewall name.");
        }
        if (!string.Equals(opts.AddressType, "EXTERNAL", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:AddressType must be EXTERNAL; " +
                "internal reservations are not supported.");
        }
        if (opts.OrchestratorSshCidrs.Count == 0)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:OrchestratorSshCidrs must name at least one CIDR — " +
                "without it neither the provider nor any operator could SSH into a sandbox.");
        }
        foreach (var cidr in opts.OrchestratorSshCidrs)
            ValidateCidr(cidr, nameof(opts.OrchestratorSshCidrs));
        if (string.IsNullOrWhiteSpace(opts.SshUser))
            throw new InvalidOperationException($"CodeyBox:Plugins:{GceSandboxOptions.PluginId}:SshUser must be set.");
        return opts;
    }

    private string ResolveOwnerId(GceSandboxOptions opts)
    {
        var raw = string.IsNullOrWhiteSpace(opts.OwnerId) ? Environment.MachineName : opts.OwnerId;
        return GceNaming.SanitizeLabelValue(raw);
    }

    private SandboxProvisioningDeferredException ToDeferred(
        GceSandboxOptions opts, GceApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            GceFailureKind.Auth or GceFailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            GceFailureKind.Quota =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            GceFailureKind.Conflict =>
                ("conflict", baseRecheck),
            GceFailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            GceFailureKind.Transient => ("unreachable", baseRecheck),
            _ => ("server-error", baseRecheck),
        };
        return new SandboxProvisioningDeferredException(
            Name, "create", errorClass, $"{context}: {ex.Message}", recheck);
    }

    private static TimeSpan MaxOf(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private void MarkNoLongerActive(string name)
    {
        if (_activeSandboxes.TryRemove(name, out var entry))
        {
            entry.Sandbox.ReleaseActiveTracking();
            SandboxLiveCounter.Decrement();
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, GceSandbox Sandbox);

    private sealed record GceMountPlan(
        IReadOnlyList<GceTmpfsMount> TmpfsMounts,
        IReadOnlyList<GceStagedMount> Staged);
}
