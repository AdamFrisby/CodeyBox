using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.OpenStackSandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by OpenStack Nova VMs (first target:
/// Infomaniak Public Cloud, a standard OpenStack deployment). Contributes the
/// <c>openstack</c> provider kind through the plugin trust model: the host
/// owns egress classification, so this kind is always <c>NotEnforced</c> —
/// the guest runs on infrastructure CodeyBox does not control and the host
/// nftables egress guarantee cannot apply. Acquisitions requiring enforced
/// egress (a named network profile) are refused by placement before this
/// provider is ever called, and again here if one ever arrives.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.openstack-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set). Credentials come only from the host credential
/// chain (environment); the secret is never logged or persisted.</para>
/// </summary>
[CodeyBoxPlugin(OpenStackSandboxOptions.PluginId, "OpenStack sandbox provider")]
public sealed class OpenStackSandboxProvider :
    ISandboxProvider,
    IPluginInitializer,
    IActiveSandboxProvider,
    IBaselineImageResolver,
    IBaselineImageProvisioner,
    IBaselineImageRetention,
    IDisposable
{
    internal const string ManagedTag = "codeybox";
    internal const string MetadataManagedKey = "codeybox.managed";
    internal const string MetadataOwnerKey = "codeybox.owner";
    internal const string MetadataWorkItemKey = "codeybox.work-item";
    internal const string MetadataCreatedKey = "codeybox.created";
    internal const string MetadataKeypairKey = "codeybox.keypair";
    internal const string MetadataSecurityGroupKey = "codeybox.sg";

    private static readonly TimeSpan MinimumOperatorFixRecheck = TimeSpan.FromMinutes(5);

    private readonly Func<OpenStackSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly IOpenStackKeyGenerator _keys;
    private readonly IOpenStackDnsResolver _dns;
    private readonly IOpenStackTransportFactory _transports;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>.</summary>
    public OpenStackSandboxProvider(TimeProvider? clock = null)
        : this(
            readOptions: null,
            http: null,
            keys: null,
            dns: null,
            transports: null,
            environment: null,
            clock: clock,
            log: null)
    {
    }

    /// <summary>Test seam: full constructor injection.</summary>
    internal OpenStackSandboxProvider(
        Func<OpenStackSandboxOptions>? readOptions,
        HttpClient? http,
        IOpenStackKeyGenerator? keys,
        IOpenStackDnsResolver? dns,
        IOpenStackTransportFactory? transports,
        Func<string, string?>? environment,
        TimeProvider? clock,
        ILogger? log)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = environment ?? (name => Environment.GetEnvironmentVariable(name));
        var runner = new CodeyBox.HostProcess.DefaultProcessRunner();
        _keys = keys ?? new SshKeygenGenerator(runner);
        _dns = dns ?? new SystemOpenStackDnsResolver();
        _transports = transports ?? new OpenSshOpenStackTransportFactory(runner);
        _http = http ?? new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = http is null;
        _readOptions = readOptions ?? (() => OpenStackSandboxOptions.FromConfiguration(_host?.ScopedConfig));
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
            "OpenStack sandbox provider initialized (enabled={Enabled}, region={Region})",
            options.Enabled,
            string.IsNullOrEmpty(options.Region) ? "(default)" : options.Region);
        return Task.CompletedTask;
    }

    public string Name => OpenStackSandboxOptions.ProviderKind;

    /// <inheritdoc/>
    public bool MightOwnSandbox(string name, string? hostId)
    {
        _ = hostId;
        if (string.IsNullOrWhiteSpace(name))
            return true;
        try
        {
            return name.StartsWith(ReadOptions().ServerNamePrefix, StringComparison.Ordinal);
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

    /// <summary>Honest capability set: fresh VM per work item, torn down on disposal; baseline bake when enabled.</summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.Teardown, SandboxCapabilities.BaselineBake];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _baselineBuildLocks = new(StringComparer.Ordinal);

    private OpenStackSandboxOptions ReadOptions() => _readOptions();

    private OpenStackApiClient CreateClient(OpenStackSandboxOptions opts) =>
        new(_http, _clock, opts.ToClientLimits());

    // ------------------------------------------------------------------
    // Provisioning
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = ReadValidatedOptions();

        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("openstack sandbox provider does not support the graphical sandbox flavor.");

        // Host-owned egress rule, enforced as an in-provider backstop too:
        // a named profile needs host nftables that cannot exist on this kind.
        SandboxEgressPolicy.EnsureEnforcedEgressForProfile(OpenStackSandboxOptions.ProviderKind, spec.Network.ProfileName);

        var ownerId = ResolveOwnerId(opts);
        var mounts = PlanMounts(spec, opts);
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var workItemId = spec.TimingWorkItemId?.ToString() ?? string.Empty;

        var flavor = await api.GetFlavorByNameAsync(credentials, opts.FlavorName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"OpenStack flavor '{opts.FlavorName}' not found. Set CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:FlavorName " +
                "to an exact visible flavor name.");
        await EnsureQuotaHeadroomAsync(api, credentials, opts, flavor, ct).ConfigureAwait(false);
        var imageId = await ResolveBootImageIdAsync(api, credentials, opts, spec, ct).ConfigureAwait(false);

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var serverName = opts.ServerNamePrefix + suffix;
        var keypairName = opts.KeypairNamePrefix + suffix;
        var securityGroupName = opts.SecurityGroupNamePrefix + suffix;
        var createdAt = _clock.GetUtcNow();

        var sshTempDirectory = Path.Combine(Path.GetTempPath(), "codeybox-openstack-" + suffix);
        Directory.CreateDirectory(sshTempDirectory);

        string? serverId = null;
        string? securityGroupId = null;
        string? floatingIpId = null;
        try
        {
            var clientKey = await _keys.GenerateClientKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, serverName, ct).ConfigureAwait(false);
            var hostKey = await _keys.GenerateHostKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, serverName + "-host", ct).ConfigureAwait(false);

            await api.CreateKeypairAsync(credentials, keypairName, clientKey.PublicKeyText, ct).ConfigureAwait(false);

            var egressIps = await ResolveEgressIpsAsync(spec.Network, ct).ConfigureAwait(false);
            var securityGroup = await api.CreateSecurityGroupAsync(
                credentials, securityGroupName, $"codeybox owner={ownerId} server={serverName}", ct).ConfigureAwait(false);
            securityGroupId = securityGroup.Id
                ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "create security group", "empty id");
            var rules = OpenStackSecurityGroupPolicy.BuildRules(
                securityGroupId, opts.OrchestratorSshCidrs, egressIps,
                opts.DnsServerIps, opts.NtpServerIps, opts.MaxEgressRules);
            foreach (var rule in rules)
                await api.CreateSecurityGroupRuleAsync(credentials, rule, ct).ConfigureAwait(false);

            var userData = OpenStackCloudInit.Build(new OpenStackCloudInitSpec(
                serverName, opts.SshUser, clientKey.PublicKeyText,
                hostKey.PrivateKeyPem, hostKey.PublicKeyText, mounts.TmpfsMounts));

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MetadataManagedKey] = "true",
                [MetadataOwnerKey] = ownerId,
                [MetadataWorkItemKey] = workItemId,
                [MetadataCreatedKey] = createdAt.ToString("o", CultureInfo.InvariantCulture),
                [MetadataKeypairKey] = keypairName,
                [MetadataSecurityGroupKey] = securityGroupId,
            };
            var created = await api.CreateServerAsync(credentials, new OpenStackServerSpec(
                serverName, flavor.Id!, imageId, [opts.NetworkId],
                KeyName: keypairName, UserData: userData, Metadata: metadata, Tags: [ManagedTag],
                SecurityGroupNames: [securityGroupName]), ct).ConfigureAwait(false);
            serverId = created.Id
                ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "create server", "empty id");

            if (!string.IsNullOrWhiteSpace(opts.FloatingNetworkId))
            {
                var floating = await api.CreateFloatingIpAsync(
                    credentials, opts.FloatingNetworkId, ct,
                    $"codeybox owner={ownerId} server={serverName}").ConfigureAwait(false);
                floatingIpId = floating.Id
                    ?? throw new OpenStackApiException(OpenStackFailureKind.Unexpected, "create floating IP", "empty id");
            }

            var active = await WaitForActiveAsync(api, credentials, opts, serverId, ct).ConfigureAwait(false);
            _ = active;

            var address = await ResolveServerAddressAsync(
                api, credentials, opts, serverId, floatingIpId, ct).ConfigureAwait(false);

            var knownHostsPath = Path.Combine(sshTempDirectory, "known_hosts");
            await WriteKnownHostsAsync(knownHostsPath, address, hostKey.PublicKeyText, ct).ConfigureAwait(false);
            var transport = _transports.Create(new OpenStackSshTransportSpec(
                $"{opts.SshUser}@{address}", opts.SshPort, clientKey.PrivateKeyPath,
                knownHostsPath, opts.SshBinary, opts.SshConnectTimeoutSeconds));
            await WaitForSshReadyAsync(transport, opts, ct).ConfigureAwait(false);

            var sandbox = new OpenStackSandbox(
                serverName, spec, mounts.Staged, transport,
                () => DeleteCloudResourcesAsync(serverId, keypairName, securityGroupId, floatingIpId),
                () => MarkNoLongerActive(serverName),
                sshTempDirectory, _log);            _activeSandboxes[serverName] = new ActiveSandboxEntry(
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
                    $"openstack staging to {serverName} failed: {ex.Message}",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }

            _log.LogInformation("Created openstack sandbox {Name} (server {ServerId})", serverName, serverId);
            return sandbox;
        }
        catch (SandboxProvisioningDeferredException)
        {
            // Thrown by the SSH/staging waits after cloud resources exist —
            // remove them before the item requeues, or the reaper owns them.
            await CleanupAfterFailureAsync(serverName, serverId, keypairName, securityGroupId, floatingIpId, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailureAsync(serverName, serverId, keypairName, securityGroupId, floatingIpId, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            await CleanupAfterFailureAsync(serverName, serverId, keypairName, securityGroupId, floatingIpId, sshTempDirectory).ConfigureAwait(false);
            if (ex is OpenStackApiException apiEx)
                throw ToDeferred(opts, apiEx, $"openstack create failed for {serverName}");
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
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var servers = await api.ListServersAsync(credentials, [ManagedTag], new Dictionary<string, string>
        {
            [MetadataManagedKey] = "true",
            [MetadataOwnerKey] = ownerId,
        }, ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>(servers.Count);
        foreach (var server in servers)
        {
            if (server.Name is null || !server.Name.StartsWith(opts.ServerNamePrefix, StringComparison.Ordinal))
                continue;
            DateTimeOffset? createdAt = null;
            if (server.Metadata?.TryGetValue(MetadataCreatedKey, out var createdRaw) == true
                && DateTimeOffset.TryParse(createdRaw, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }
            result.Add(new ManagedSandboxInfo(
                server.Name, createdAt, DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(server.Name)));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(opts.ServerNamePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"OpenStack sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var ownerId = ResolveOwnerId(opts);
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var servers = await api.ListServersAsync(credentials, [ManagedTag], new Dictionary<string, string>
        {
            [MetadataManagedKey] = "true",
            [MetadataOwnerKey] = ownerId,
        }, ct).ConfigureAwait(false);
        var match = servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        string? keypairName = null;
        string? securityGroupId = null;
        string? serverId = null;
        if (match?.Id is not null)
        {
            serverId = match.Id;
            match.Metadata?.TryGetValue(MetadataKeypairKey, out keypairName);
            match.Metadata?.TryGetValue(MetadataSecurityGroupKey, out securityGroupId);
            // Floating IPs are linked by description (server=<name>), not
            // metadata: the address is allocated after the server exists, and
            // Nova metadata has no patch path in this client.
            var floatingIpId = await FindFloatingIpIdByServerAsync(api, credentials, name, ct).ConfigureAwait(false);
            await DeleteCloudResourcesAsync(serverId, keypairName, securityGroupId, floatingIpId).ConfigureAwait(false);
        }
        else
        {
            var suffix = name.StartsWith(opts.ServerNamePrefix, StringComparison.Ordinal)
                ? name[opts.ServerNamePrefix.Length..]
                : name;
            await DeleteCloudResourcesAsync(
                serverId: null,
                keypairName: opts.KeypairNamePrefix + suffix,
                securityGroupId: await FindSecurityGroupIdByNameAsync(api, credentials, opts, opts.SecurityGroupNamePrefix + suffix, ct).ConfigureAwait(false),
                floatingIpId: await FindFloatingIpIdByServerAsync(api, credentials, name, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }
        MarkNoLongerActive(name);
        await SweepOrphanResourcesAsync(api, credentials, opts, ownerId, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Baseline images (Glance, content-hashed by the shared toolchain hash)
    // ------------------------------------------------------------------

    /// <summary>
    /// Deterministic scoped pin for the live toolchain: a content hash over
    /// the bake inputs (provisioning commands, staged executables,
    /// verification probes) shared with the Incus provider, so the same
    /// toolchain resolves to the same hash wherever it bakes. Baselines are
    /// profile-independent here — placement refuses profiled work on
    /// NotEnforced kinds before this provider is ever called.
    /// </summary>
    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        _ = profileName;
        var opts = ReadOptions();
        if (!opts.Enabled || !opts.UseBaselineImages)
            return null;
        if (flavor != SandboxProfileFlavor.Headless)
            return null;
        ThrowIfBaselineInvalid(opts);
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        return CreateBaselineBuilder(opts, credentials).ResolveBaselineRef();
    }

    public async Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        ThrowIfBaselineInvalid(opts);
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var images = await api.ListImagesAsync(credentials, name: null, tag: null, ct).ConfigureAwait(false);
        var result = new List<BaselineImageInfo>(images.Count);
        foreach (var image in images)
        {
            if (image.Id is null || image.Name is null)
                continue;
            var hash = OpenStackBaselineNaming.TryExtractHashFromName(image.Name, opts.BaselineImagePrefix)
                ?? OpenStackBaselineNaming.TryExtractHashFromTags(image.Tags);
            if (hash is null
                && !image.Name.StartsWith(opts.BaselineImagePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(new BaselineImageInfo(
                hash is null ? image.Name : OpenStackBaselineNaming.FormatScopedPin(hash, image.Name),
                image.CreatedAt,
                DiskBytes: image.Size));
        }
        return result;
    }

    public async Task DisposeBaselineImageAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        ThrowIfBaselineInvalid(opts);
        if (BaselinePin.TryParseScopedPin(name, out var scope, out _, out var scopeRef))
        {
            if (!string.Equals(scope, OpenStackSandboxOptions.ProviderKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete a baseline owned by provider '{scope}' through the openstack provider.");
            }
            name = scopeRef;
        }
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Baseline image name must not be blank.", nameof(name));
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var image = await ResolveOwnedImageAsync(api, credentials, opts, name, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"OpenStack baseline image '{name}' was not found among this provider's images.");
        await api.DeleteImageAsync(credentials, image.Id!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the baked image for the profile/flavor (or the pinned ref)
    /// exists: look up by toolchain hash, then bake by booting a builder
    /// server, provisioning over SSH, powering off, and snapshotting. The
    /// bake is single-flight per hash with a bounded timeout; a failed build
    /// leaves no half image and deletes the builder.
    /// </summary>
    public async Task<string?> EnsureBaselineImageAsync(
        string profileName, SandboxProfileFlavor flavor, string? pinnedBaselineRef, CancellationToken ct)
    {
        _ = profileName;
        var opts = ReadValidatedOptions();
        if (flavor != SandboxProfileFlavor.Headless)
            return null;
        ThrowIfBaselineInvalid(opts);
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        return await CreateBaselineBuilder(opts, credentials)
            .EnsureBaselineImageAsync(pinnedBaselineRef, _baselineBuildLocks, ct)
            .ConfigureAwait(false);
    }

    public async Task PruneRetainedImagesAsync(IReadOnlySet<string> livePins, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(livePins);
        var opts = ReadValidatedOptions();
        ThrowIfBaselineInvalid(opts);
        var credentials = OpenStackCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var images = await api.ListImagesAsync(credentials, name: null, tag: null, ct).ConfigureAwait(false);
        var retained = new List<OpenStackRetainedImage>(images.Count);
        var idsByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (image.Id is null || image.Name is null)
                continue;
            var hash = OpenStackBaselineNaming.TryExtractHashFromName(image.Name, opts.BaselineImagePrefix)
                ?? OpenStackBaselineNaming.TryExtractHashFromTags(image.Tags);
            if (hash is null
                && !image.Name.StartsWith(opts.BaselineImagePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            retained.Add(new OpenStackRetainedImage(
                image.Name, hash, ReadProjectProperty(image), image.CreatedAt));
            if (!idsByName.TryGetValue(image.Name, out var ids))
                idsByName[image.Name] = ids = [];
            ids.Add(image.Id);
        }
        var doomed = OpenStackBaselineRetention.SelectForDeletion(
            retained, livePins, opts.BaselineRetainedImageCount);
        foreach (var name in doomed)
        {
            ct.ThrowIfCancellationRequested();
            if (!idsByName.TryGetValue(name, out var ids))
                continue;
            foreach (var id in ids)
            {
                try
                {
                    await api.DeleteImageAsync(credentials, id, ct).ConfigureAwait(false);
                    _log.LogInformation("Pruned retained OpenStack baseline image {Image} ({Id})", name, id);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to prune retained OpenStack baseline image {Image} ({Id})", name, id);
                }
            }
        }
    }

    private OpenStackBaselineBuilder CreateBaselineBuilder(
        OpenStackSandboxOptions opts, OpenStackCredentials credentials) =>
        new(opts, credentials, CreateClient(opts), _keys, _transports, _environment, _clock, _log);

    private async Task<OpenStackImage?> ResolveOwnedImageAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string name, CancellationToken ct)
    {
        var byId = await api.GetImageAsync(credentials, name, ct).ConfigureAwait(false);
        if (byId?.Id is not null && byId.Name is not null && IsOwnedBaselineImage(byId, opts))
            return byId;
        var matches = await api.ListImagesAsync(credentials, name, tag: null, ct).ConfigureAwait(false);
        var exact = matches
            .Where(image => image.Id is not null
                && string.Equals(image.Name, name, StringComparison.Ordinal)
                && IsOwnedBaselineImage(image, opts))
            .ToList();
        return exact.Count == 1 ? exact[0] : null;
    }

    private static bool IsOwnedBaselineImage(OpenStackImage image, OpenStackSandboxOptions opts)
    {
        if (image.Name is not null
            && image.Name.StartsWith(opts.BaselineImagePrefix, StringComparison.Ordinal))
        {
            return true;
        }
        return OpenStackBaselineNaming.TryExtractHashFromTags(image.Tags) is not null;
    }

    private static string? ReadProjectProperty(OpenStackImage image)
    {
        if (image.AdditionalProperties is not null
            && image.AdditionalProperties.TryGetValue("codeybox_project", out var raw)
            && raw.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var project = raw.GetString();
            return string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        }
        return null;
    }

    private static void ThrowIfBaselineInvalid(OpenStackSandboxOptions opts)
    {
        var errors = opts.ValidateBaseline();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "OpenStack baseline options are invalid: " + string.Join("; ", errors));
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

    // ------------------------------------------------------------------
    // Internal lifecycle helpers
    // ------------------------------------------------------------------

    private async Task<OpenStackServer> WaitForActiveAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string serverId, CancellationToken ct)
    {
        try
        {
            return await api.WaitForServerStatusAsync(
                credentials, serverId, ["ACTIVE"], ct,
                TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds)).ConfigureAwait(false);
        }
        catch (OpenStackApiException ex)
        {
            var status = await TryGetServerStatusAsync(api, credentials, serverId).ConfigureAwait(false);
            if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                await TryDeleteServerAsync(api, credentials, serverId).ConfigureAwait(false);
                throw ToDeferred(opts, ex, $"openstack server {serverId} entered ERROR and was deleted");
            }
            throw;
        }
    }

    private async Task<string?> TryGetServerStatusAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, string serverId)
    {
        try
        {
            var server = await api.GetServerAsync(credentials, serverId, CancellationToken.None).ConfigureAwait(false);
            return server?.Status;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string> ResolveServerAddressAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string serverId, string? floatingIpId, CancellationToken ct)
    {
        if (floatingIpId is not null)
        {
            var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
            var attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ports = await api.ListPortsByDeviceAsync(credentials, serverId, ct).ConfigureAwait(false);
                var port = ports.FirstOrDefault(p => p.FixedIps is { Count: > 0 });
                if (port?.Id is not null)
                {
                    await api.AssociateFloatingIpAsync(credentials, floatingIpId, port.Id, ct).ConfigureAwait(false);
                    var floating = (await api.ListFloatingIpsAsync(credentials, "codeybox ", ct).ConfigureAwait(false))
                        .FirstOrDefault(f => string.Equals(f.Id, floatingIpId, StringComparison.Ordinal));
                    if (!string.IsNullOrWhiteSpace(floating?.Address))
                        return floating.Address.Trim();
                    throw new OpenStackApiException(
                        OpenStackFailureKind.Unexpected, "associate floating IP",
                        "floating IP has no address after association");
                }
                if (_clock.GetUtcNow() >= deadline)
                {
                    throw new OpenStackApiException(
                        OpenStackFailureKind.Unexpected, "associate floating IP",
                        $"server '{serverId}' exposed no port in time");
                }
                await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
            }
        }

        {
            var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
            var attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ports = await api.ListPortsByDeviceAsync(credentials, serverId, ct).ConfigureAwait(false);
                foreach (var port in ports)
                {
                    var fixedIp = port.FixedIps?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.IpAddress));
                    if (fixedIp?.IpAddress is not null)
                        return fixedIp.IpAddress.Trim();
                }
                if (_clock.GetUtcNow() >= deadline)
                {
                    throw new OpenStackApiException(
                        OpenStackFailureKind.Unexpected, "resolve server address",
                        $"server '{serverId}' exposed no fixed IP in time");
                }
                await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitForSshReadyAsync(
        IRemoteHostTransport transport, OpenStackSandboxOptions opts, CancellationToken ct)
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
                // Not ready yet — retry until the bounded deadline.
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "ssh-ready", "ssh-unready",
                    "openstack server did not accept SSH before the readiness deadline",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan NextPollDelay(OpenStackSandboxOptions opts, int attempt)
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
        var line = address.Trim() + " " + hostPublicKey.Trim() + "\n";
        await File.WriteAllTextAsync(knownHostsPath, line, CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task PrepareGuestFilesystemAsync(
        IRemoteHostTransport transport,
        OpenStackMountPlan mounts,
        CancellationToken ct)
    {
        var parents = mounts.Staged
            .Select(m => ParentOf(m.RemotePath))
            .Append(SandboxConventions.WorkDir)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var script = "set -e; " + string.Join("; ", parents.Select(p => "mkdir -p " + OpenStackSandbox.QuoteShellWord(p)));
        var run = await transport.RunAsync(["bash", "-c", script], stdin: null, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new OpenStackApiException(
                OpenStackFailureKind.Unexpected, "prepare guest filesystem",
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
        string serverName, string? serverId, string keypairName, string? securityGroupId,
        string? floatingIpId, string sshTempDirectory)
    {
        MarkNoLongerActive(serverName);
        if (serverId is not null || securityGroupId is not null || floatingIpId is not null)
            await DeleteCloudResourcesAsync(serverId, keypairName, securityGroupId, floatingIpId).ConfigureAwait(false);
        else
            await TryDeleteKeypairAsync(keypairName).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(sshTempDirectory))
                Directory.Delete(sshTempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenStack sandbox {Name}: failed to remove SSH key directory", serverName);
        }
    }

    private async Task DeleteCloudResourcesAsync(
        string? serverId, string? keypairName, string? securityGroupId, string? floatingIpId)
    {
        OpenStackSandboxOptions opts;
        OpenStackCredentials credentials;
        OpenStackApiClient api;
        try
        {
            opts = ReadValidatedOptions();
            credentials = OpenStackCredentials.Resolve(opts, _environment);
            api = CreateClient(opts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenStack sandbox cleanup: cannot resolve credentials/options; cloud resources may leak");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600)));
        var ct = cts.Token;
        if (serverId is not null)
        {
            await TryDeleteServerAsync(api, credentials, serverId).ConfigureAwait(false);
            try
            {
                await api.WaitForServerDeletedAsync(
                    credentials, serverId, ct, TimeSpan.FromSeconds(Math.Min(120, opts.ReadyTimeoutSeconds))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "OpenStack server {ServerId}: deletion not confirmed; leak reaper will retry", serverId);
            }
        }
        if (floatingIpId is not null)
        {
            try { await api.DeleteFloatingIpAsync(credentials, floatingIpId, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "OpenStack floating IP {FloatingIpId}: delete failed; leak reaper will retry", floatingIpId);
            }
        }
        if (securityGroupId is not null)
        {
            try { await api.DeleteSecurityGroupAsync(credentials, securityGroupId, ct).ConfigureAwait(false); }
            catch (OpenStackApiException ex) when (ex.Kind == OpenStackFailureKind.Conflict)
            {
                _log.LogWarning("OpenStack security group {SecurityGroupId}: still attached; leak reaper will retry", securityGroupId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "OpenStack security group {SecurityGroupId}: delete failed; leak reaper will retry", securityGroupId);
            }
        }
        if (!string.IsNullOrWhiteSpace(keypairName))
            await TryDeleteKeypairAsync(keypairName).ConfigureAwait(false);
    }

    private async Task TryDeleteKeypairAsync(string keypairName)
    {
        try
        {
            var opts = ReadValidatedOptions();
            var credentials = OpenStackCredentials.Resolve(opts, _environment);
            await CreateClient(opts).DeleteKeypairAsync(credentials, keypairName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OpenStack keypair {Keypair}: delete failed; leak reaper will retry", keypairName);
        }
    }

    private static async Task TryDeleteServerAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, string serverId)
    {
        try
        {
            await api.DeleteServerAsync(credentials, serverId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the leak reaper retries anything left behind.
        }
    }

    private async Task SweepOrphanResourcesAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string ownerId, CancellationToken ct)
    {
        var servers = await api.ListServersAsync(credentials, [ManagedTag], new Dictionary<string, string>
        {
            [MetadataManagedKey] = "true",
            [MetadataOwnerKey] = ownerId,
        }, ct).ConfigureAwait(false);
        var referencedKeypairs = new HashSet<string>(StringComparer.Ordinal);
        var referencedGroups = new HashSet<string>(StringComparer.Ordinal);
        var liveServerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            if (server.Metadata?.TryGetValue(MetadataKeypairKey, out var keypair) == true)
                referencedKeypairs.Add(keypair);
            if (server.Metadata?.TryGetValue(MetadataSecurityGroupKey, out var group) == true)
                referencedGroups.Add(group);
            // Floating IPs are linked by description (server=<name>), so the
            // sweep compares against live server names instead of metadata.
            if (server.Name is not null)
                liveServerNames.Add(server.Name);
        }

        var swept = 0;
        var budget = Math.Max(1, opts.MaxListItems);
        foreach (var keypair in await api.ListKeypairsAsync(credentials, opts.KeypairNamePrefix, ct).ConfigureAwait(false))
        {
            if (swept >= budget)
                break;
            if (keypair.Name is null || referencedKeypairs.Contains(keypair.Name))
                continue;
            try
            {
                if (await api.DeleteKeypairAsync(credentials, keypair.Name, ct).ConfigureAwait(false))
                    swept++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orphan sweep: failed to delete keypair {Keypair}", keypair.Name);
            }
        }
        foreach (var group in await api.ListSecurityGroupsAsync(credentials, opts.SecurityGroupNamePrefix, ct).ConfigureAwait(false))
        {
            if (swept >= budget)
                break;
            if (group.Id is null || referencedGroups.Contains(group.Id))
                continue;
            try
            {
                if (await api.DeleteSecurityGroupAsync(credentials, group.Id, ct).ConfigureAwait(false))
                    swept++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orphan sweep: failed to delete security group {SecurityGroupId}", group.Id);
            }
        }
        foreach (var floating in await api.ListFloatingIpsAsync(credentials, "codeybox ", ct).ConfigureAwait(false))
        {
            if (swept >= budget)
                break;
            if (floating.Id is null)
                continue;
            if (!IsOwnerFloatingIp(floating.Description, ownerId))
                continue;
            // Never release an address still serving a live owner server, or
            // one attached to any port: the sweep only frees fully orphaned
            // addresses.
            if (!string.IsNullOrWhiteSpace(floating.PortId))
                continue;
            if (FloatingIpServesLiveServer(floating.Description, liveServerNames))
                continue;
            try
            {
                if (await api.DeleteFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false))
                    swept++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orphan sweep: failed to delete floating IP {FloatingIpId}", floating.Id);
            }
        }
        if (swept > 0)
            _log.LogInformation("Orphan sweep: released {Count} unreferenced OpenStack resource(s)", swept);
    }

    private async Task<string?> FindSecurityGroupIdByNameAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string name, CancellationToken ct)
    {
        try
        {
            var groups = await api.ListSecurityGroupsAsync(credentials, opts.SecurityGroupNamePrefix, ct).ConfigureAwait(false);
            return groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal))?.Id;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to list security groups while disposing leaked sandbox resources");
            return null;
        }
    }

    private async Task<string?> FindFloatingIpIdByServerAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, string serverName, CancellationToken ct)
    {
        try
        {
            var floatingIps = await api.ListFloatingIpsAsync(credentials, "codeybox ", ct).ConfigureAwait(false);
            return floatingIps.FirstOrDefault(f => ServerTokenMatches(f.Description, serverName))?.Id;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to list floating IPs while disposing leaked sandbox resources");
            return null;
        }
    }

    internal static bool FloatingIpServesLiveServer(string? description, ISet<string> liveServerNames)
    {
        if (string.IsNullOrEmpty(description))
            return false;
        foreach (var token in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("server=", StringComparison.Ordinal)
                && liveServerNames.Contains(token["server=".Length..]))
                return true;
        }
        return false;
    }

    internal static bool ServerTokenMatches(string? description, string serverName)
    {
        if (string.IsNullOrEmpty(description))
            return false;
        foreach (var token in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("server=", StringComparison.Ordinal)
                && string.Equals(token["server=".Length..], serverName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static bool IsOwnerFloatingIp(string? description, string ownerId)
    {
        if (string.IsNullOrEmpty(description))
            return false;
        foreach (var token in description.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("owner=", StringComparison.Ordinal)
                && string.Equals(token["owner=".Length..], ownerId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Provisioning guards
    // ------------------------------------------------------------------

    private async Task EnsureQuotaHeadroomAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        OpenStackFlavor flavor, CancellationToken ct)
    {
        OpenStackLimits limits;
        try
        {
            limits = await api.GetLimitsAsync(credentials, ct).ConfigureAwait(false);
        }
        catch (OpenStackApiException ex) when (ex.Kind == OpenStackFailureKind.NotFound)
        {
            _log.LogWarning("Nova limits API unavailable; skipping quota headroom check");
            return;
        }
        var reasons = new List<string>();
        if (limits.MaxTotalInstances is { } maxInstances
            && (limits.TotalInstancesUsed ?? 0) >= maxInstances)
            reasons.Add($"instances {limits.TotalInstancesUsed}/{maxInstances}");
        if (flavor.Vcpus is { } vcpus
            && limits.MaxTotalCores is { } maxCores
            && (limits.TotalCoresUsed ?? 0) + vcpus > maxCores)
            reasons.Add($"cores {(limits.TotalCoresUsed ?? 0) + vcpus}/{maxCores}");
        if (flavor.RamMb is { } ramMb
            && limits.MaxTotalRamMb is { } maxRam
            && (limits.TotalRamUsedMb ?? 0) + ramMb > maxRam)
            reasons.Add($"RAM {(limits.TotalRamUsedMb ?? 0) + ramMb}/{maxRam}MiB");
        if (reasons.Count > 0)
        {
            throw new SandboxProvisioningDeferredException(
                Name, "quota-headroom", "quota-exhausted",
                $"Nova quota headroom exhausted ({string.Join(", ", reasons)}); deferring instead of failing the item",
                MaxOf(TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds), MinimumOperatorFixRecheck));
        }
    }

    private async Task<string> ResolveImageIdAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        SandboxSpec spec, CancellationToken ct)
    {
        var reference = string.IsNullOrWhiteSpace(spec.ImageReference) ? opts.ImageName : spec.ImageReference.Trim();
        return await ResolveImageReferenceAsync(api, credentials, opts, reference, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the boot image for one acquisition. A pinned baseline ref wins
    /// over <c>ImageReference</c>: scoped pins resolve by toolchain hash to the
    /// equivalent local image (building it when the pin is live), legacy refs
    /// resolve by image id or exact name exactly as before.
    /// </summary>
    private async Task<string> ResolveBootImageIdAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        SandboxSpec spec, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(spec.BaselineImageRef))
        {
            return await ResolvePinnedBaselineImageIdAsync(
                api, credentials, opts, spec.BaselineImageRef.Trim(), ct).ConfigureAwait(false);
        }
        return await ResolveImageIdAsync(api, credentials, opts, spec, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolvePinnedBaselineImageIdAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string pin, CancellationToken ct)
    {
        if (BaselinePin.TryParseScopedPin(pin, out _, out var pinHash, out _))
        {
            var builder = CreateBaselineBuilder(opts, credentials);
            var live = builder.Plan();
            if (!string.Equals(pinHash, live.ShortHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pinned OpenStack baseline '{pin}' names toolchain tc-{pinHash}, " +
                    $"but the live configuration resolves tc-{live.ShortHash}; " +
                    "refusing to boot current configuration under a stale ref.");
            }
            if (!opts.UseBaselineImages)
            {
                throw new InvalidOperationException(
                    $"Pinned OpenStack baseline '{pin}' cannot be served while baseline images are disabled.");
            }
            var ensured = await builder.EnsureBaselineImageAsync(pin, _baselineBuildLocks, ct).ConfigureAwait(false);
            _ = ensured;
            var image = await builder.FindImageByHashAsync(live.ShortHash, live.ImageName, ct).ConfigureAwait(false);
            if (image?.Id is not null)
                return image.Id;
            throw new InvalidOperationException(
                $"Pinned OpenStack baseline '{pin}' was ensured but no active image was found.");
        }
        return await ResolveImageReferenceAsync(api, credentials, opts, pin, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveImageReferenceAsync(
        OpenStackApiClient api, OpenStackCredentials credentials, OpenStackSandboxOptions opts,
        string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidOperationException(
                $"No OpenStack image configured: set CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:ImageName " +
                "or provide SandboxSpec.ImageReference.");
        }
        var byId = await api.GetImageAsync(credentials, reference, ct).ConfigureAwait(false);
        if (byId?.Id is not null)
            return byId.Id;
        var matches = await api.ListImagesAsync(credentials, reference, tag: null, ct).ConfigureAwait(false);
        var exact = matches
            .Where(image => string.Equals(image.Name, reference, StringComparison.Ordinal) && image.Id is not null)
            .ToList();
        if (exact.Count == 1)
            return exact[0].Id!;
        if (exact.Count == 0)
        {
            throw new InvalidOperationException(
                $"OpenStack image '{reference}' not found by id or exact name. " +
                $"Set CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:ImageName to a visible image id or exact name.");
        }
        throw new InvalidOperationException(
            $"OpenStack image name '{reference}' is ambiguous ({exact.Count} matches); use an image id.");
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveEgressIpsAsync(
        SandboxNetworkPolicy network, CancellationToken ct)
    {
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in network.AllowedHosts)
        {
            var normalized = NormalizeEgressHost(host);
            if (!string.IsNullOrEmpty(normalized))
                hosts.Add(normalized);
        }
        if (!string.IsNullOrWhiteSpace(network.HostGitEndpoint))
        {
            var normalized = NormalizeEgressHost(network.HostGitEndpoint);
            if (!string.IsNullOrEmpty(normalized))
                hosts.Add(normalized);
        }
        var ips = new List<IPAddress>();
        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(ReadOptions().DnsTimeoutSeconds));
                var resolved = await _dns.ResolveAsync(host, attemptCts.Token).ConfigureAwait(false);
                ips.AddRange(resolved.Where(ip =>
                    ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("DNS resolution of egress host {Host} timed out; omitting from security-group egress", host);
            }
            catch (Exception ex)
            {
                // Best-effort defence in depth: an unresolvable allowlist name
                // narrows egress rather than failing the item — placement has
                // already refused anything needing enforced egress.
                _log.LogWarning(ex, "DNS resolution of egress host {Host} failed; omitting from security-group egress", host);
            }
        }
        return ips;
    }

    internal static string? NormalizeEgressHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var host = value.Trim();
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            host = uri.Host;
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            host = end >= 0 ? host[1..end] : host;
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && !IPAddress.TryParse(host, out _))
                host = host[..colon];
        }
        host = host.Trim().TrimEnd('.');
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Invalid egress host: '{value}'.");
        return host;
    }

    private OpenStackMountPlan PlanMounts(SandboxSpec spec, OpenStackSandboxOptions opts)
    {
        _ = opts;
        var tmpfsRoots = new List<OpenStackTmpfsMount>();
        foreach (var mount in spec.Mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.SandboxPath) || !mount.SandboxPath.StartsWith('/'))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");
            if (mount.Tmpfs)
            {
                OpenStackCloudInit.ValidateMountPath(mount.SandboxPath);
                tmpfsRoots.Add(new OpenStackTmpfsMount(
                    mount.SandboxPath, mount.SizeBytes is > 0 ? mount.SizeBytes.Value : SandboxConventions.CredentialsTmpfsBytes));
            }
        }
        if (!tmpfsRoots.Any(m => m.Path.TrimEnd('/').Equals(
                SandboxConventions.CredentialsDir, StringComparison.Ordinal)))
        {
            // The agent credential writer always targets CredentialsDir, so
            // the guest always carries a RAM-backed mount there — even when
            // the spec stages no credential mount of its own.
            tmpfsRoots.Insert(0, new OpenStackTmpfsMount(
                SandboxConventions.CredentialsDir, SandboxConventions.CredentialsTmpfsBytes));
        }
        var staged = new List<OpenStackStagedMount>();
        foreach (var mount in spec.Mounts)
        {
            if (OpenStackSandbox.IsCredentialPath(mount.SandboxPath))
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
                staged.Add(new OpenStackStagedMount(mount.SandboxPath, HostPath: null, Writable: !mount.ReadOnly));
                continue;
            }
            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(hostPath, $"openstack mount source path does not exist: {hostPath}");
            }
            staged.Add(new OpenStackStagedMount(mount.SandboxPath, hostPath, Writable: !mount.ReadOnly));
        }
        return new OpenStackMountPlan(tmpfsRoots, staged);
    }

    internal static bool IsUnderTmpfs(string sandboxPath, IReadOnlyList<OpenStackTmpfsMount> tmpfsRoots)
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

    private OpenStackSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The openstack sandbox provider is disabled. Enable it via " +
                $"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:Enabled=true plus the plugin allowlist.");
        }
        if (string.IsNullOrWhiteSpace(opts.FlavorName))
            throw new InvalidOperationException($"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:FlavorName must be set.");
        if (string.IsNullOrWhiteSpace(opts.NetworkId))
            throw new InvalidOperationException($"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:NetworkId must be set.");
        if (string.IsNullOrWhiteSpace(opts.ServerNamePrefix) || !opts.ServerNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:ServerNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.SecurityGroupNamePrefix) || !opts.SecurityGroupNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:SecurityGroupNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.KeypairNamePrefix) || opts.KeypairNamePrefix.Any(ch => ch == '/' || char.IsWhiteSpace(ch)))
            throw new InvalidOperationException($"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:KeypairNamePrefix must be a single path-safe token.");
        if (opts.OrchestratorSshCidrs.Count == 0)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:OrchestratorSshCidrs must name at least one CIDR — " +
                "without it neither the provider nor any operator could SSH into a sandbox.");
        }
        foreach (var cidr in opts.OrchestratorSshCidrs)
            OpenStackSecurityGroupPolicy.NormalizeCidr(cidr, nameof(opts.OrchestratorSshCidrs));
        foreach (var ip in opts.DnsServerIps.Concat(opts.NtpServerIps))
        {
            if (!IPAddress.TryParse(ip.Trim(), out _))
                throw new InvalidOperationException($"OpenStack DNS/NTP server IP is not an IP address: '{ip}'.");
        }
        if (string.IsNullOrWhiteSpace(opts.SshUser))
            throw new InvalidOperationException($"CodeyBox:Plugins:{OpenStackSandboxOptions.PluginId}:SshUser must be set.");
        return opts;
    }

    internal string ResolveOwnerId(OpenStackSandboxOptions opts)
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
        OpenStackSandboxOptions opts, OpenStackApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            OpenStackFailureKind.Unauthorized or OpenStackFailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            OpenStackFailureKind.QuotaExhausted =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            OpenStackFailureKind.Throttled =>
                ("throttled", ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : baseRecheck),
            OpenStackFailureKind.Conflict =>
                ("conflict", baseRecheck),
            OpenStackFailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            OpenStackFailureKind.Unreachable => ("unreachable", baseRecheck),
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

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, OpenStackSandbox Sandbox);

    private sealed record OpenStackMountPlan(
        IReadOnlyList<OpenStackTmpfsMount> TmpfsMounts,
        IReadOnlyList<OpenStackStagedMount> Staged);
}
