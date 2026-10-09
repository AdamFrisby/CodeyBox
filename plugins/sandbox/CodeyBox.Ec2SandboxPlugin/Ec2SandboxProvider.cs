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

namespace CodeyBox.Ec2SandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by AWS EC2 instances (the OpenStack
/// remote-VM provider is the concrete analogue). Contributes the
/// <c>ec2</c> provider kind through the plugin trust model: the host owns
/// egress classification, so this kind is always <c>NotEnforced</c> — the
/// guest runs on infrastructure CodeyBox does not control and the host
/// nftables egress guarantee cannot apply. Acquisitions requiring enforced
/// egress (a named network profile) are refused by placement before this
/// provider is ever called, and again here if one ever arrives.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.ec2-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set). Signing credentials come only from the host
/// credential chain (environment); they stay on the host and are never copied
/// into guest user-data, logs, or prompts.</para>
///
/// <para>One work-item-owned instance per acquisition, booting the configured
/// pinned AMI via a single <c>RunInstances</c> call (MinCount=MaxCount=1,
/// stable ClientToken, owned tags at creation). No baseline bake/snapshot
/// capability is advertised: acquisitions always boot that AMI. Instances
/// launch on-demand (never spot), with no IAM instance profile, and IMDSv2
/// required.</para>
/// </summary>
[CodeyBoxPlugin(Ec2SandboxOptions.PluginId, "AWS EC2 sandbox provider")]
public sealed class Ec2SandboxProvider :
    ISandboxProvider,
    IPluginInitializer,
    IActiveSandboxProvider,
    IDisposable
{
    internal const string OwnedTag = "codeybox-owned";
    internal const string OwnerTag = "codeybox-owner";
    internal const string WorkItemTag = "codeybox-work-item";
    internal const string RequestTag = "codeybox-request";
    internal const string CreatedTag = "codeybox-created";
    internal const string OwnedValue = "true";
    internal const string NoWorkItemValue = "none";

    private static readonly TimeSpan MinimumOperatorFixRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeleteConfirmTimeout = TimeSpan.FromSeconds(120);

    private readonly Func<Ec2SandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly IEc2KeyGenerator _keys;
    private readonly IEc2DnsResolver _dns;
    private readonly IEc2TransportFactory _transports;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>.</summary>
    public Ec2SandboxProvider(TimeProvider? clock = null)
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
    internal Ec2SandboxProvider(
        Func<Ec2SandboxOptions>? readOptions,
        HttpClient? http,
        IEc2KeyGenerator? keys,
        IEc2DnsResolver? dns,
        IEc2TransportFactory? transports,
        Func<string, string?>? environment,
        TimeProvider? clock,
        ILogger? log)
    {
        _clock = clock ?? TimeProvider.System;
        _http = http ?? new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = http is null;
        var runner = new CodeyBox.HostProcess.DefaultProcessRunner();
        _keys = keys ?? new SshKeygenGenerator(runner);
        _dns = dns ?? new SystemEc2DnsResolver();
        _transports = transports ?? new OpenSshEc2TransportFactory(runner);
        _environment = environment ?? (name => Environment.GetEnvironmentVariable(name));
        _readOptions = readOptions ?? (() => Ec2SandboxOptions.FromConfiguration(_host?.ScopedConfig));
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
            "EC2 sandbox provider initialized (enabled={Enabled}, region={Region}, instanceType={InstanceType})",
            options.Enabled,
            string.IsNullOrEmpty(options.Region) ? "(unset)" : options.Region,
            string.IsNullOrEmpty(options.InstanceType) ? "(unset)" : options.InstanceType);
        return Task.CompletedTask;
    }

    public string Name => Ec2SandboxOptions.ProviderKind;

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
    /// Real instances with a dedicated guest kernel — including genuine
    /// RAM-backed tmpfs mounts. This is a guest-boundary claim only; egress
    /// stays NotEnforced (host-classified, the plugin cannot promote itself).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>Honest capability set: fresh instance per work item, torn down on disposal. No baseline bake.</summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.Teardown];

    private Ec2SandboxOptions ReadOptions() => _readOptions();

    private Ec2ApiClient CreateClient(Ec2SandboxOptions opts) =>
        new(_http, _clock, opts.ToClientLimits());

    // ------------------------------------------------------------------
    // Provisioning
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = ReadValidatedOptions();

        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("ec2 sandbox provider does not support the graphical sandbox flavor.");

        // Host-owned egress rule, enforced as an in-provider backstop too:
        // a named profile needs host nftables that cannot exist on this kind.
        SandboxEgressPolicy.EnsureEnforcedEgressForProfile(Ec2SandboxOptions.ProviderKind, spec.Network.ProfileName);

        var ownerId = ResolveOwnerId(opts);
        var mounts = PlanMounts(spec, opts);
        var credentials = Ec2Credentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var workItemId = spec.TimingWorkItemId?.ToString() ?? string.Empty;

        var amiId = ResolveAmiId(spec.ImageReference, opts);
        var image = await api.DescribeImageAsync(credentials, amiId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"EC2 AMI '{amiId}' not found. Set CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:AmiId " +
                "to an approved available AMI id.");

        ct.ThrowIfCancellationRequested();

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestId = Guid.NewGuid().ToString();
        var instanceName = opts.InstanceNamePrefix + suffix;
        var keyName = opts.KeyNamePrefix + suffix;
        var securityGroupName = opts.SecurityGroupNamePrefix + suffix;
        var createdAt = _clock.GetUtcNow();
        var tags = OwnershipTags(ownerId, workItemId, requestId, createdAt);

        var sshTempDirectory = Path.Combine(Path.GetTempPath(), "codeybox-ec2-" + suffix);
        Directory.CreateDirectory(sshTempDirectory);

        var tracked = new ProvisionedResources
        {
            KeyName = keyName,
            SecurityGroupName = securityGroupName,
            RequestId = requestId,
        };
        try
        {
            var clientKey = await _keys.GenerateClientKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, instanceName, ct).ConfigureAwait(false);
            var hostKey = await _keys.GenerateHostKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, instanceName + "-host", ct).ConfigureAwait(false);

            await api.ImportKeyPairAsync(credentials, keyName, clientKey.PublicKeyText, tags, ct).ConfigureAwait(false);
            tracked.KeyImported = true;

            string? createdGroupId = null;
            if (opts.CreateSecurityGroup)
            {
                createdGroupId = await CreateSandboxSecurityGroupAsync(
                    api, credentials, opts, securityGroupName, tags, spec.Network, ct).ConfigureAwait(false);
                tracked.SecurityGroupId = createdGroupId;
            }
            var groupIds = opts.SecurityGroupIds
                .Concat(createdGroupId is null ? [] : [createdGroupId])
                .ToList();
            if (groupIds.Count == 0)
            {
                throw new InvalidOperationException(
                    $"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId} attaches no security group: " +
                    "set CreateSecurityGroup=true (with VpcId) or name explicit SecurityGroupIds. " +
                    "The provider never falls back to an implicit default security group.");
            }

            var userData = Ec2CloudInit.Build(new Ec2CloudInitSpec(
                instanceName, opts.SshUser, clientKey.PublicKeyText,
                hostKey.PrivateKeyPem, hostKey.PublicKeyText, mounts.TmpfsMounts));

            var instanceId = await RunInstanceWithReconcileAsync(
                api, credentials, opts, ownerId, requestId,
                new Ec2RunSpec(
                    image.ImageId ?? amiId, opts.InstanceType, opts.SubnetId, groupIds,
                    keyName, userData, instanceName, tags,
                    opts.VolumeSizeGb, opts.VolumeType, opts.RootDeviceName,
                    opts.DeleteOnTermination, opts.AssociatePublicIp,
                    opts.EnableInstanceMetadata,
                    string.IsNullOrWhiteSpace(opts.Zone) ? null : opts.Zone.Trim()),
                instanceName, ct).ConfigureAwait(false);
            tracked.InstanceId = instanceId;

            string? elasticIp = null;
            if (opts.AllocateElasticIp)
            {
                var allocation = await api.AllocateAddressAsync(credentials, tags, ct).ConfigureAwait(false);
                tracked.AllocationId = allocation.AllocationId;
                var associationId = await api.AssociateAddressAsync(
                    credentials, allocation.AllocationId!, instanceId, ct).ConfigureAwait(false);
                tracked.AssociationId = associationId;
                elasticIp = allocation.PublicIp;
            }

            var running = await WaitForRunningAsync(api, credentials, opts, instanceId, ct).ConfigureAwait(false);

            var address = await ResolveInstanceAddressAsync(
                api, credentials, opts, running, elasticIp, ct).ConfigureAwait(false);

            var knownHostsPath = Path.Combine(sshTempDirectory, "known_hosts");
            await WriteKnownHostsAsync(knownHostsPath, address, hostKey.PublicKeyText, ct).ConfigureAwait(false);
            var transport = _transports.Create(new Ec2SshTransportSpec(
                $"{opts.SshUser}@{address}", opts.SshPort, clientKey.PrivateKeyPath,
                knownHostsPath, opts.SshBinary, opts.SshConnectTimeoutSeconds));
            await WaitForSshReadyAsync(transport, opts, ct).ConfigureAwait(false);

            var sandbox = new Ec2Sandbox(
                instanceName, spec, mounts.Staged, transport,
                () => DeleteCloudResourcesAsync(
                    tracked.Snapshot(), ownerId),
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
                    if (staged.HostPath is null)
                        continue;
                    await transport.StageInAsync(staged.HostPath, staged.RemotePath, ct).ConfigureAwait(false);
                }
            }
            catch (RemoteSshTransportException ex)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "stage", "staging-unavailable",
                    $"ec2 staging to {instanceName} failed: {ex.Message}",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }

            _log.LogInformation("Created ec2 sandbox {Name} (instance {InstanceId})", instanceName, instanceId);
            return sandbox;
        }
        catch (SandboxProvisioningDeferredException)
        {
            // Thrown by the SSH/staging waits after cloud resources exist —
            // remove them before the item requeues, or the reaper owns them.
            await CleanupAfterFailureAsync(instanceName, tracked, ownerId, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailureAsync(instanceName, tracked, ownerId, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            await CleanupAfterFailureAsync(instanceName, tracked, ownerId, sshTempDirectory).ConfigureAwait(false);
            if (ex is Ec2ApiException apiEx)
                throw ToDeferred(opts, apiEx, $"ec2 create failed for {instanceName}");
            throw;
        }
    }

    internal static string ResolveAmiId(string? imageReference, Ec2SandboxOptions opts)
    {
        var reference = string.IsNullOrWhiteSpace(imageReference) ? opts.AmiId : imageReference.Trim();
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:AmiId must be set to an approved AMI id.");
        }
        if (!Ec2Placement.IsValidAmiId(reference))
        {
            throw new InvalidOperationException(
                $"EC2 image '{reference}' is not a concrete AMI id pin (ami- plus hex). " +
                "Names, aliases, and SSM aliases move over time; only pinned ami- ids are accepted.");
        }
        return reference.Trim();
    }

    private async Task<string> CreateSandboxSecurityGroupAsync(
        Ec2ApiClient api, Ec2Credentials credentials, Ec2SandboxOptions opts,
        string securityGroupName, Dictionary<string, string> tags,
        SandboxNetworkPolicy network, CancellationToken ct)
    {
        var egressIps = await ResolveEgressIpsAsync(network, ct).ConfigureAwait(false);
        var ingress = Ec2SecurityGroupPolicy.BuildIngress(opts.OrchestratorSshCidrs, opts.MaxSecurityGroupRules);
        var egress = Ec2SecurityGroupPolicy.BuildEgress(
            egressIps, opts.DnsServerIps, opts.NtpServerIps, opts.MaxSecurityGroupRules);
        if (ingress.Count + egress.Count > opts.MaxSecurityGroupRules)
        {
            throw new InvalidOperationException(
                $"Security-group plan needs {ingress.Count + egress.Count} rules but MaxSecurityGroupRules={opts.MaxSecurityGroupRules}: " +
                "narrow AllowedHosts or raise the bound.");
        }
        var groupId = await api.CreateSecurityGroupAsync(
            credentials, securityGroupName,
            $"codeybox sandbox {securityGroupName} (orchestrator SSH only)",
            opts.VpcId, tags, ct).ConfigureAwait(false);
        await api.AuthorizeIngressAsync(credentials, groupId, ingress, ct).ConfigureAwait(false);
        await api.RevokeDefaultEgressAsync(credentials, groupId, ct).ConfigureAwait(false);
        await api.AuthorizeEgressAsync(credentials, groupId, egress, ct).ConfigureAwait(false);
        return groupId;
    }

    /// <summary>
    /// Runs the instance, reconciling ambiguous failures by stable
    /// client-token identity before anything is resubmitted: when the run
    /// call may have acted despite failing (transport cut, timeout,
    /// throttle, 5xx — or an uninterpretable response to a create that was
    /// already sent), the provider describes for the exact request tag and
    /// adopts the instance it finds instead of launching a duplicate.
    /// Bounded by <c>MaxRunAttempts</c>; quota exhaustion is infrastructure,
    /// not a code failure, and surfaces as a deferral.
    /// </summary>
    private async Task<string> RunInstanceWithReconcileAsync(
        Ec2ApiClient api, Ec2Credentials credentials, Ec2SandboxOptions opts,
        string ownerId, string requestId, Ec2RunSpec spec, string instanceName, CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            ct.ThrowIfCancellationRequested();
            try
            {
                return await api.RunInstancesAsync(credentials, spec, requestId, ct).ConfigureAwait(false);
            }
            catch (Ec2ApiException ex) when (IsUnknownCreateOutcome(ex) && attempts < opts.MaxRunAttempts)
            {
                Ec2Instance? reconciled = null;
                try
                {
                    reconciled = await ReconcileInstanceByRequestAsync(
                        api, credentials, ownerId, requestId, opts, ct).ConfigureAwait(false);
                }
                catch (Exception reconcileEx)
                {
                    _log.LogWarning(reconcileEx,
                        "EC2 run failed ambiguously and reconciliation also failed; deferring without resubmit");
                }
                if (reconciled?.InstanceId is not null)
                {
                    VerifyOwnedInstance(reconciled, ownerId, instanceName);
                    _log.LogInformation(
                        "EC2 run outcome was ambiguous but the instance was reconciled by request tag (instance {InstanceId}); adopting it",
                        reconciled.InstanceId);
                    return reconciled.InstanceId;
                }
            }
        }
    }

    internal static bool IsUnknownCreateOutcome(Ec2ApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // MayHaveCreated covers transport/throttle/5xx. Unexpected covers a
        // response that arrived but could not be interpreted (malformed,
        // truncated, oversized): the RunInstances request was already sent,
        // so the instance may exist despite the parse failure. Any other kind
        // (auth, forbidden, not-found, conflict, quota) is a conclusive no.
        return ex.MayHaveCreated || ex.Kind == Ec2FailureKind.Unexpected;
    }

    /// <summary>
    /// Finds the instance carrying the exact ownership triple
    /// (owned/owner/request). Returns null when there is none; throws when
    /// several match — an ambiguous match must never silently pick one. The
    /// listing is retried a bounded number of times: tag-index reads lag
    /// behind creates under eventual consistency, and one blind read must
    /// not declare the instance missing.
    /// </summary>
    private async Task<Ec2Instance?> ReconcileInstanceByRequestAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string requestId, Ec2SandboxOptions opts, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await api.DescribeInstancesByTagAsync(credentials, new Dictionary<string, string>
            {
                [OwnedTag] = OwnedValue,
                [OwnerTag] = ownerId,
                [RequestTag] = requestId,
            }, ct).ConfigureAwait(false);
            var owned = candidates
                .Where(c => IsOwnedBy(c.Tags, ownerId)
                    && string.Equals(c.Tags![RequestTag], requestId, StringComparison.Ordinal))
                .ToList();
            if (owned.Count > 1)
            {
                throw new Ec2ApiException(
                    Ec2FailureKind.Unexpected, "reconcile instance",
                    $"request tag '{requestId}' matched {owned.Count} instances; refusing to pick one");
            }
            if (owned.Count == 1)
                return owned[0];
            if (attempt + 1 < maxAttempts)
                await Task.Delay(NextPollDelay(opts, attempt), _clock, ct).ConfigureAwait(false);
        }
        return null;
    }

    internal static bool IsOwnedBy(IReadOnlyDictionary<string, string>? tags, string ownerId)
    {
        if (tags is null)
            return false;
        return tags.TryGetValue(OwnedTag, out var owned)
            && string.Equals(owned, OwnedValue, StringComparison.Ordinal)
            && tags.TryGetValue(OwnerTag, out var owner)
            && string.Equals(owner, ownerId, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var ownerId = ResolveOwnerId(opts);
        var credentials = Ec2Credentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var instances = await api.DescribeInstancesByTagAsync(credentials, new Dictionary<string, string>
        {
            [OwnedTag] = OwnedValue,
            [OwnerTag] = ownerId,
        }, ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>(instances.Count);
        foreach (var instance in instances)
        {
            if (!IsOwnedBy(instance.Tags, ownerId))
                continue;
            var name = instance.Tags!.TryGetValue("Name", out var nameTag) ? nameTag : instance.InstanceId;
            if (string.IsNullOrWhiteSpace(name)
                || !name.StartsWith(opts.InstanceNamePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            DateTimeOffset? createdAt = null;
            if (instance.Tags!.TryGetValue(CreatedTag, out var createdRaw)
                && long.TryParse(createdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                try
                {
                    createdAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
            result.Add(new ManagedSandboxInfo(
                name, createdAt, DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(name)));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(opts.InstanceNamePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"EC2 sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var ownerId = ResolveOwnerId(opts);
        var credentials = Ec2Credentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var instances = await api.DescribeInstancesByTagAsync(credentials, new Dictionary<string, string>
        {
            [OwnedTag] = OwnedValue,
            [OwnerTag] = ownerId,
        }, ct).ConfigureAwait(false);
        // Name alone gates candidacy; the exact tag triple below authorizes
        // every deletion. A same-named instance owned by someone else is never
        // touched — it fails closed instead.
        var match = instances.FirstOrDefault(i =>
            IsOwnedBy(i.Tags, ownerId)
            && string.Equals(i.Tags!.TryGetValue("Name", out var nameTag) ? nameTag : i.InstanceId,
                name, StringComparison.Ordinal));
        var suffix = SuffixOf(name, opts.InstanceNamePrefix);
        var tracked = new ProvisionedResources
        {
            KeyName = opts.KeyNamePrefix + suffix,
            SecurityGroupName = opts.SecurityGroupNamePrefix + suffix,
        };
        if (match?.InstanceId is not null)
        {
            VerifyOwnedInstance(match, ownerId, name);
            tracked.InstanceId = match.InstanceId;
            tracked.RequestId = match.Tags!.TryGetValue(RequestTag, out var request) ? request : null;
        }
        await DeleteOwnedInstanceSetAsync(
            api, credentials, opts, ownerId, tracked, throwOnFailure: true, ct).ConfigureAwait(false);
        MarkNoLongerActive(name);
        await SweepOrphanResourcesAsync(api, credentials, opts, ownerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-verifies exact ownership at the moment of deletion: the
    /// <c>codeybox-owned</c>/<c>codeybox-owner</c> tags must match this
    /// provider and this host exactly. Name prefixes alone never authorize
    /// deletion.
    /// </summary>
    internal static void VerifyOwnedInstance(Ec2Instance instance, string ownerId, string name)
    {
        if (!IsOwnedBy(instance.Tags, ownerId))
        {
            throw new InvalidOperationException(
                $"Refusing to delete instance '{name}': ownership tags do not prove this host owns it.");
        }
    }

    private static string SuffixOf(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;

    /// <summary>
    /// Deletes one sandbox's full resource set: instance terminate with
    /// bounded deletion confirm, Elastic IP disassociate+release, security
    /// group delete, key pair delete, and owned EBS volumes when they were
    /// not deleted on termination. The request tag finds every member of the
    /// set; the name-derived fallbacks only match exact names carrying exact
    /// ownership tags. Failures are retained, never thrown away: the orphan
    /// identity survives for the reaper.
    /// </summary>
    private async Task DeleteOwnedInstanceSetAsync(
        Ec2ApiClient api, Ec2Credentials credentials, Ec2SandboxOptions opts,
        string ownerId, ProvisionedResources tracked,
        bool throwOnFailure, CancellationToken ct)
    {
        var failures = new List<Exception>();
        if (!string.IsNullOrWhiteSpace(tracked.InstanceId))
        {
            var instanceId = tracked.InstanceId!;
            try
            {
                var current = await api.DescribeInstanceAsync(credentials, instanceId, ct).ConfigureAwait(false);
                if (current is not null)
                    VerifyOwnedInstance(current, ownerId, instanceId);
                await api.TerminateInstanceAsync(credentials, instanceId, ct).ConfigureAwait(false);
                await api.WaitForInstanceTerminatedAsync(
                    credentials, instanceId, DeleteConfirmTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never claim deletion from a merely accepted request: the
                // tags stay on the instance, so the reaper retries by tag.
                failures.Add(new InvalidOperationException(
                    $"EC2 instance {instanceId} deletion was not confirmed; orphan identity retained for the reaper.", ex));
            }
            if (!opts.DeleteOnTermination)
                await DeleteOwnedVolumesAsync(api, credentials, ownerId, instanceId, failures, ct).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(tracked.AllocationId))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tracked.AssociationId))
                    await api.DisassociateAddressAsync(credentials, tracked.AssociationId!, ct).ConfigureAwait(false);
                await api.ReleaseAddressAsync(credentials, tracked.AllocationId!, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"EC2 address {tracked.AllocationId} release failed; orphan identity retained.", ex));
            }
        }
        else if (!string.IsNullOrWhiteSpace(tracked.RequestId))
        {
            await DeleteTaggedAddressesAsync(api, credentials, ownerId, tracked.RequestId!, failures, ct).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(tracked.SecurityGroupId))
        {
            await DeleteVerifiedSecurityGroupAsync(
                api, credentials, ownerId, tracked.SecurityGroupId!, expectedName: null, failures, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(tracked.SecurityGroupName))
        {
            await DeleteNamedSecurityGroupAsync(
                api, credentials, ownerId, tracked.SecurityGroupName!, failures, ct).ConfigureAwait(false);
        }
        if (tracked.KeyImported && !string.IsNullOrWhiteSpace(tracked.KeyName))
        {
            await DeleteVerifiedKeyPairAsync(api, credentials, ownerId, tracked.KeyName!, failures, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(tracked.RequestId))
        {
            await DeleteTaggedKeyPairsAsync(api, credentials, ownerId, tracked.RequestId!, failures, ct).ConfigureAwait(false);
        }
        if (failures.Count > 0 && throwOnFailure)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        foreach (var failure in failures)
            _log.LogWarning(failure, "EC2 cleanup: resource deletion not confirmed; leak reaper will retry");
    }

    private async Task DeleteOwnedVolumesAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string instanceId, List<Exception> failures, CancellationToken ct)
    {
        IReadOnlyList<Ec2Volume> volumes;
        try
        {
            volumes = await api.DescribeVolumesByTagAsync(credentials, new Dictionary<string, string>
            {
                [OwnedTag] = OwnedValue,
                [OwnerTag] = ownerId,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                "EC2 cleanup: could not list owned volumes; orphan identity retained for the reaper.", ex));
            return;
        }
        foreach (var volume in volumes)
        {
            if (!IsOwnedBy(volume.Tags, ownerId))
                continue;
            if (!string.Equals(volume.InstanceId, instanceId, StringComparison.Ordinal))
                continue;
            if (volume.VolumeId is null)
                continue;
            try
            {
                await api.DeleteVolumeAsync(credentials, volume.VolumeId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"EC2 volume {volume.VolumeId} delete failed; orphan identity retained.", ex));
            }
        }
    }

    private async Task DeleteTaggedAddressesAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string requestId, List<Exception> failures, CancellationToken ct)
    {
        IReadOnlyList<Ec2Address> addresses;
        try
        {
            addresses = await api.ListAddressesByTagAsync(credentials, new Dictionary<string, string>
            {
                [OwnedTag] = OwnedValue,
                [OwnerTag] = ownerId,
                [RequestTag] = requestId,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                "EC2 cleanup: could not list request-tagged addresses; orphan identity retained for the reaper.", ex));
            return;
        }
        foreach (var address in addresses)
        {
            if (!IsOwnedBy(address.Tags, ownerId) || address.AllocationId is null)
                continue;
            try
            {
                if (!string.IsNullOrWhiteSpace(address.AssociationId))
                    await api.DisassociateAddressAsync(credentials, address.AssociationId!, ct).ConfigureAwait(false);
                await api.ReleaseAddressAsync(credentials, address.AllocationId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"EC2 address {address.AllocationId} release failed; orphan identity retained.", ex));
            }
        }
    }

    private async Task DeleteVerifiedSecurityGroupAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string groupId, string? expectedName,
        List<Exception> failures, CancellationToken ct)
    {
        try
        {
            var group = await api.DescribeSecurityGroupAsync(credentials, groupId, ct).ConfigureAwait(false);
            if (group is null)
                return;
            if (!IsOwnedBy(group.Tags, ownerId)
                || (expectedName is not null
                    && !string.Equals(group.GroupName, expectedName, StringComparison.Ordinal)))
            {
                failures.Add(new InvalidOperationException(
                    $"Refusing to delete security group '{groupId}': ownership tags do not prove this host owns it."));
                return;
            }
            await api.DeleteSecurityGroupAsync(credentials, groupId, ct).ConfigureAwait(false);
            if (await api.DescribeSecurityGroupAsync(credentials, groupId, ct).ConfigureAwait(false) is not null)
            {
                failures.Add(new InvalidOperationException(
                    $"EC2 security group {groupId} still present after delete; orphan identity retained."));
            }
        }
        catch (Ec2ApiException ex) when (ex.Kind == Ec2FailureKind.NotFound)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"EC2 security group {groupId} delete failed; orphan identity retained.", ex));
        }
    }

    private async Task DeleteNamedSecurityGroupAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string groupName, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            var groups = await api.ListSecurityGroupsByTagAsync(credentials, new Dictionary<string, string>
            {
                [OwnedTag] = OwnedValue,
                [OwnerTag] = ownerId,
            }, ct).ConfigureAwait(false);
            var match = groups.FirstOrDefault(g =>
                string.Equals(g.GroupName, groupName, StringComparison.Ordinal));
            if (match?.GroupId is null)
                return;
            await DeleteVerifiedSecurityGroupAsync(
                api, credentials, ownerId, match.GroupId, groupName, failures, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"EC2 security group '{groupName}' delete failed; orphan identity retained.", ex));
        }
    }

    private async Task DeleteVerifiedKeyPairAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string keyName, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            var key = await api.DescribeKeyPairAsync(credentials, keyName, ct).ConfigureAwait(false);
            if (key is null)
                return;
            if (!IsOwnedBy(key.Tags, ownerId))
            {
                failures.Add(new InvalidOperationException(
                    $"Refusing to delete key pair '{keyName}': ownership tags do not prove this host owns it."));
                return;
            }
            await api.DeleteKeyPairAsync(credentials, keyName, ct).ConfigureAwait(false);
            if (await api.DescribeKeyPairAsync(credentials, keyName, ct).ConfigureAwait(false) is not null)
            {
                failures.Add(new InvalidOperationException(
                    $"EC2 key pair {keyName} still present after delete; orphan identity retained."));
            }
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"EC2 key pair '{keyName}' delete failed; orphan identity retained.", ex));
        }
    }

    private async Task DeleteTaggedKeyPairsAsync(
        Ec2ApiClient api, Ec2Credentials credentials,
        string ownerId, string requestId, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            var keys = await api.ListKeyPairsByTagAsync(credentials, new Dictionary<string, string>
            {
                [OwnedTag] = OwnedValue,
                [OwnerTag] = ownerId,
                [RequestTag] = requestId,
            }, ct).ConfigureAwait(false);
            foreach (var key in keys)
            {
                if (!IsOwnedBy(key.Tags, ownerId) || key.KeyName is null)
                    continue;
                await DeleteVerifiedKeyPairAsync(api, credentials, ownerId, key.KeyName, failures, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                "EC2 cleanup: could not list request-tagged key pairs; orphan identity retained for the reaper.", ex));
        }
    }

    /// <summary>
    /// Ownership-scoped orphan sweep: deletes this host's key pairs,
    /// security groups, Elastic IPs, and (non-delete-on-termination) volumes
    /// whose request tag matches no live owned instance. Only exact
    /// owner-tag matches are candidates, and only unreferenced ones are
    /// deleted — live resources and other owners are never touched.
    /// Budget-bounded; failures are logged, never thrown.
    /// </summary>
    private async Task SweepOrphanResourcesAsync(
        Ec2ApiClient api, Ec2Credentials credentials, Ec2SandboxOptions opts,
        string ownerId, CancellationToken ct)
    {
        var ownerSelector = new Dictionary<string, string>
        {
            [OwnedTag] = OwnedValue,
            [OwnerTag] = ownerId,
        };
        IReadOnlyList<Ec2Instance> instances;
        try
        {
            instances = await api.DescribeInstancesByTagAsync(credentials, ownerSelector, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Orphan sweep: instance inventory failed; skipping sweep rather than guessing");
            return;
        }
        var liveRequests = new HashSet<string>(StringComparer.Ordinal);
        var liveInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        var liveGroupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            if (!IsOwnedBy(instance.Tags, ownerId))
                continue;
            if (instance.InstanceId is not null
                && !string.Equals(instance.State, "terminated", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(instance.State, "shutting-down", StringComparison.OrdinalIgnoreCase))
            {
                liveInstanceIds.Add(instance.InstanceId);
                if (instance.SecurityGroupIds is not null)
                {
                    foreach (var groupId in instance.SecurityGroupIds)
                        liveGroupIds.Add(groupId);
                }
            }
            if (instance.Tags!.TryGetValue(RequestTag, out var request) && !string.IsNullOrEmpty(request))
                liveRequests.Add(request);
        }
        var swept = 0;
        var budget = Math.Max(1, Math.Min(opts.MaxListItems, 1000));
        try
        {
            foreach (var key in await api.ListKeyPairsByTagAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                if (key.KeyName is not null && IsUnreferenced(key.Tags, liveRequests))
                {
                    var failures = new List<Exception>();
                    await DeleteVerifiedKeyPairAsync(api, credentials, ownerId, key.KeyName, failures, ct).ConfigureAwait(false);
                    if (failures.Count == 0)
                        swept++;
                    else
                        _log.LogWarning("Orphan sweep: failed to delete key pair {KeyName}", key.KeyName);
                }
            }
            foreach (var group in await api.ListSecurityGroupsByTagAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                // Never delete a group still attached to a live owned
                // instance: the sweep only frees fully orphaned groups.
                if (group.GroupId is not null
                    && IsUnreferenced(group.Tags, liveRequests)
                    && !liveGroupIds.Contains(group.GroupId))
                {
                    var failures = new List<Exception>();
                    await DeleteVerifiedSecurityGroupAsync(
                        api, credentials, ownerId, group.GroupId, expectedName: null, failures, ct).ConfigureAwait(false);
                    if (failures.Count == 0)
                        swept++;
                    else
                        _log.LogWarning("Orphan sweep: failed to delete security group {GroupId}", group.GroupId);
                }
            }
            foreach (var address in await api.ListAddressesByTagAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                // Never release an address still associated: the sweep only
                // frees fully orphaned addresses.
                if (address.InstanceId is not null || address.AssociationId is not null)
                    continue;
                if (address.AllocationId is not null && IsUnreferenced(address.Tags, liveRequests))
                {
                    try
                    {
                        await api.ReleaseAddressAsync(credentials, address.AllocationId, ct).ConfigureAwait(false);
                        swept++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Orphan sweep: failed to release address {AllocationId}", address.AllocationId);
                    }
                }
            }
            if (!opts.DeleteOnTermination)
            {
                foreach (var volume in await api.DescribeVolumesByTagAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
                {
                    if (swept >= budget)
                        break;
                    if (volume.VolumeId is null || !IsOwnedBy(volume.Tags, ownerId))
                        continue;
                    if (volume.InstanceId is not null && liveInstanceIds.Contains(volume.InstanceId))
                        continue;
                    if (!string.Equals(volume.State, "available", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        await api.DeleteVolumeAsync(credentials, volume.VolumeId, ct).ConfigureAwait(false);
                        swept++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Orphan sweep: failed to delete volume {VolumeId}", volume.VolumeId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Orphan sweep: resource listing failed partway; stopping rather than guessing");
        }
        if (swept > 0)
            _log.LogInformation("Orphan sweep: released {Count} unreferenced EC2 resource(s)", swept);
    }

    private static bool IsUnreferenced(
        IReadOnlyDictionary<string, string>? tags, HashSet<string> liveRequests)
    {
        if (tags is null)
            return false;
        if (!tags.TryGetValue(OwnedTag, out var owned)
            || !string.Equals(owned, OwnedValue, StringComparison.Ordinal))
        {
            return false;
        }
        if (!tags.TryGetValue(RequestTag, out var request) || string.IsNullOrEmpty(request))
            return false;
        return !liveRequests.Contains(request);
    }

    // ------------------------------------------------------------------
    // Readiness / address / staging
    // ------------------------------------------------------------------

    private static readonly IReadOnlyList<string> RunningWanted = ["running"];
    private static readonly IReadOnlyList<string> RunningFaults =
        ["shutting-down", "terminated", "stopping", "stopped"];

    private async Task<Ec2Instance> WaitForRunningAsync(
        Ec2ApiClient api, Ec2Credentials credentials, Ec2SandboxOptions opts,
        string instanceId, CancellationToken ct)
    {
        try
        {
            return await api.WaitForInstanceStateAsync(
                credentials, instanceId, RunningWanted, RunningFaults,
                TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (Ec2ApiException ex)
        {
            var status = await TryGetInstanceStateAsync(api, credentials, instanceId).ConfigureAwait(false);
            if (status is not null && RunningFaults.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                await TryTerminateInstanceAsync(api, credentials, instanceId).ConfigureAwait(false);
                throw ToDeferred(opts, ex, $"ec2 instance {instanceId} entered {status} and was terminated");
            }
            throw;
        }
    }

    private async Task<string?> TryGetInstanceStateAsync(
        Ec2ApiClient api, Ec2Credentials credentials, string instanceId)
    {
        try
        {
            var instance = await api.DescribeInstanceAsync(credentials, instanceId, CancellationToken.None).ConfigureAwait(false);
            return instance?.State;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string> ResolveInstanceAddressAsync(
        Ec2ApiClient api, Ec2Credentials credentials, Ec2SandboxOptions opts,
        Ec2Instance running, string? elasticIp, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(elasticIp))
            return ValidateElasticIpAddress(elasticIp);
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var address = RunningInstanceAddress(running);
            if (address is not null)
                return address;
            var refreshed = await api.DescribeInstanceAsync(credentials, running.InstanceId!, ct).ConfigureAwait(false);
            if (refreshed is not null)
            {
                running = refreshed;
                address = RunningInstanceAddress(running);
                if (address is not null)
                    return address;
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new Ec2ApiException(
                    Ec2FailureKind.Unexpected, "resolve instance address",
                    $"instance '{running.InstanceId}' exposed no usable public address in time");
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validates an Elastic-IP string from the EC2 API response before it
    /// reaches the SSH target or the known_hosts trust file. The API value
    /// is dependency runtime output (less-trusted): a newline/space-bearing
    /// value would forge known_hosts entries and redirect the outbound SSH
    /// connection, so anything that is not a plain IP literal is refused.
    /// </summary>
    internal static string ValidateElasticIpAddress(string raw)
    {
        var candidate = raw.Trim();
        if (IPAddress.TryParse(candidate, out var parsed)
            && (parsed.AddressFamily == AddressFamily.InterNetwork
                || parsed.AddressFamily == AddressFamily.InterNetworkV6))
        {
            return candidate;
        }

        throw new Ec2ApiException(
            Ec2FailureKind.Unexpected, "resolve instance address",
            "Elastic IP response carried no usable public IP address");
    }

    internal static string? RunningInstanceAddress(Ec2Instance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.PublicIpAddress))
        {
            var address = instance.PublicIpAddress.Trim();
            if (IPAddress.TryParse(address, out var parsed)
                && (parsed.AddressFamily == AddressFamily.InterNetwork
                    || parsed.AddressFamily == AddressFamily.InterNetworkV6))
            {
                return address;
            }
        }
        return null;
    }

    private async Task WaitForSshReadyAsync(
        IRemoteHostTransport transport, Ec2SandboxOptions opts, CancellationToken ct)
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
                // mismatch also surfaces here and fails closed at the deadline
                // (the known_hosts file pins exactly one key; accept-any is
                // never used), never by trusting the key on sight.
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new SandboxProvisioningDeferredException(
                    Name, "ssh-ready", "ssh-unready",
                    "ec2 instance did not accept SSH before the readiness deadline",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan NextPollDelay(Ec2SandboxOptions opts, int attempt)
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
        var host = address.Trim();
        if (!IPAddress.TryParse(host, out _))
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "write known_hosts",
                "refused to pin a host key for a non-IP address");
        }

        var line = host + " " + hostPublicKey.Trim() + "\n";
        await File.WriteAllTextAsync(knownHostsPath, line, CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task PrepareGuestFilesystemAsync(
        IRemoteHostTransport transport,
        Ec2MountPlan mounts,
        CancellationToken ct)
    {
        var parents = mounts.Staged
            .Select(m => ParentOf(m.RemotePath))
            .Append(SandboxConventions.WorkDir)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var script = "set -e; " + string.Join("; ", parents.Select(p => "mkdir -p " + Ec2Sandbox.QuoteShellWord(p)));
        var run = await transport.RunAsync(["bash", "-c", script], stdin: null, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new Ec2ApiException(
                Ec2FailureKind.Unexpected, "prepare guest filesystem",
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
        string instanceName, ProvisionedResources tracked, string ownerId, string sshTempDirectory)
    {
        MarkNoLongerActive(instanceName);
        await DeleteCloudResourcesAsync(tracked.Snapshot(), ownerId).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(sshTempDirectory))
                Directory.Delete(sshTempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EC2 sandbox {Name}: failed to remove SSH key directory", instanceName);
        }
    }

    /// <summary>
    /// Handle-disposal and provisioning-failure cloud cleanup. Never throws:
    /// deletion is confirmed where possible, and anything unconfirmed stays
    /// tagged for the leak reaper and is logged. Disposal itself must not
    /// fail because the cloud did.
    /// </summary>
    private async Task DeleteCloudResourcesAsync(ProvisionedResources tracked, string ownerId)
    {
        Ec2SandboxOptions opts;
        Ec2Credentials credentials;
        Ec2ApiClient api;
        try
        {
            opts = ReadValidatedOptions();
            credentials = Ec2Credentials.Resolve(opts, _environment);
            api = CreateClient(opts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EC2 sandbox cleanup: cannot resolve credentials/options; cloud resources may leak");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600)));
        await DeleteOwnedInstanceSetAsync(
            api, credentials, opts, ownerId, tracked, throwOnFailure: false, cts.Token).ConfigureAwait(false);
    }

    private static async Task TryTerminateInstanceAsync(
        Ec2ApiClient api, Ec2Credentials credentials, string instanceId)
    {
        try
        {
            await api.TerminateInstanceAsync(credentials, instanceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the leak reaper retries anything left behind.
        }
    }

    // ------------------------------------------------------------------
    // Provisioning guards
    // ------------------------------------------------------------------

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

    private Ec2MountPlan PlanMounts(SandboxSpec spec, Ec2SandboxOptions opts)
    {
        _ = opts;
        var tmpfsRoots = new List<Ec2TmpfsMount>();
        foreach (var mount in spec.Mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.SandboxPath) || !mount.SandboxPath.StartsWith('/'))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");
            if (mount.Tmpfs)
            {
                Ec2CloudInit.ValidateMountPath(mount.SandboxPath);
                tmpfsRoots.Add(new Ec2TmpfsMount(
                    mount.SandboxPath, mount.SizeBytes is > 0 ? mount.SizeBytes.Value : SandboxConventions.CredentialsTmpfsBytes));
            }
        }
        if (!tmpfsRoots.Any(m => m.Path.TrimEnd('/').Equals(
                SandboxConventions.CredentialsDir, StringComparison.Ordinal)))
        {
            // The agent credential writer always targets CredentialsDir, so
            // the guest always carries a RAM-backed mount there — even when
            // the spec stages no credential mount of its own.
            tmpfsRoots.Insert(0, new Ec2TmpfsMount(
                SandboxConventions.CredentialsDir, SandboxConventions.CredentialsTmpfsBytes));
        }
        var staged = new List<Ec2StagedMount>();
        foreach (var mount in spec.Mounts)
        {
            if (Ec2Sandbox.IsCredentialPath(mount.SandboxPath))
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
                staged.Add(new Ec2StagedMount(mount.SandboxPath, HostPath: null, Writable: !mount.ReadOnly));
                continue;
            }
            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(hostPath, $"ec2 mount source path does not exist: {hostPath}");
            }
            staged.Add(new Ec2StagedMount(mount.SandboxPath, hostPath, Writable: !mount.ReadOnly));
        }
        return new Ec2MountPlan(tmpfsRoots, staged);
    }

    internal static bool IsUnderTmpfs(string sandboxPath, IReadOnlyList<Ec2TmpfsMount> tmpfsRoots)
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

    private Ec2SandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The ec2 sandbox provider is disabled. Enable it via " +
                $"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:Enabled=true plus the plugin allowlist.");
        }
        _ = ResolveAmiId(imageReference: null, opts);
        if (!Ec2Placement.IsValidInstanceType(opts.InstanceType))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:InstanceType must be a valid EC2 instance type (e.g. t3.medium).");
        if (!Ec2Placement.IsValidRegion(opts.Region))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:Region must be a valid AWS region (e.g. us-east-1).");
        if (!string.IsNullOrWhiteSpace(opts.Zone) && !Ec2Placement.IsValidZone(opts.Zone.Trim(), opts.Region.Trim()))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:Zone must be a zone in region '{opts.Region.Trim()}'.");
        if (!Ec2Placement.IsValidSubnetId(opts.SubnetId))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:SubnetId must be an explicit subnet id (no default-VPC fallback).");
        if (opts.CreateSecurityGroup && !Ec2Placement.IsValidVpcId(opts.VpcId))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:VpcId must be set when CreateSecurityGroup is true (no default-VPC assumption).");
        if (!opts.CreateSecurityGroup && opts.SecurityGroupIds.Count == 0)
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:CreateSecurityGroup=false with no SecurityGroupIds would launch with an implicit default group; refusing.");
        foreach (var groupId in opts.SecurityGroupIds)
        {
            if (!Ec2Placement.IsValidSecurityGroupId(groupId))
                throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:SecurityGroupIds entry '{groupId}' is not a valid security group id.");
        }
        if (!Ec2Placement.AllowedVolumeTypes.Contains(opts.VolumeType))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:VolumeType must be one of {string.Join("/", Ec2Placement.AllowedVolumeTypes)}.");
        if (string.IsNullOrWhiteSpace(opts.RootDeviceName))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:RootDeviceName must be set.");
        if (!opts.AssociatePublicIp && !opts.AllocateElasticIp)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId} disables launch-time public IPv4 with no Elastic IP: " +
                "the provider would have no SSH path into the sandbox.");
        }
        if (string.IsNullOrWhiteSpace(opts.InstanceNamePrefix) || !opts.InstanceNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:InstanceNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.KeyNamePrefix) || !opts.KeyNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:KeyNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.SecurityGroupNamePrefix) || !opts.SecurityGroupNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:SecurityGroupNamePrefix must start with 'codeybox-'.");
        if (opts.CreateSecurityGroup)
        {
            if (opts.OrchestratorSshCidrs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:OrchestratorSshCidrs must name at least one CIDR — " +
                    "without it neither the provider nor any operator could SSH into a sandbox.");
            }
            try
            {
                foreach (var cidr in opts.OrchestratorSshCidrs)
                    Ec2SecurityGroupPolicy.NormalizeCidr(cidr, nameof(opts.OrchestratorSshCidrs));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:OrchestratorSshCidrs is invalid: {ex.Message}", ex);
            }
        }
        foreach (var ip in opts.DnsServerIps.Concat(opts.NtpServerIps))
        {
            if (!IPAddress.TryParse(ip.Trim(), out _))
                throw new InvalidOperationException($"EC2 DNS/NTP server IP is not an IP address: '{ip}'.");
        }
        if (string.IsNullOrWhiteSpace(opts.SshUser))
            throw new InvalidOperationException($"CodeyBox:Plugins:{Ec2SandboxOptions.PluginId}:SshUser must be set.");
        return opts;
    }

    internal string ResolveOwnerId(Ec2SandboxOptions opts)
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
    /// Builds the ownership tag set stamped on every instance, volume,
    /// address, key pair, and security group at creation. The request tag
    /// doubles as the <c>RunInstances</c> client token: each provisioning
    /// attempt carries a stable identity for ambiguous-create reconciliation.
    /// The created tag is unix epoch seconds; the instance Name tag carries
    /// the sandbox name for operator readability.
    /// </summary>
    internal static Dictionary<string, string> OwnershipTags(
        string ownerId, string workItemId, string requestId, DateTimeOffset createdAt)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OwnedTag] = OwnedValue,
            [OwnerTag] = ownerId,
            [WorkItemTag] = SanitizeTagValue(string.IsNullOrWhiteSpace(workItemId) ? NoWorkItemValue : workItemId),
            [RequestTag] = requestId,
            [CreatedTag] = createdAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
    }

    internal static string SanitizeTagValue(string value)
    {
        var chars = value.Trim().Select(ch =>
            (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
            || ch is '-' or '_' or '.' or '/' or '+' or '=' or '@' ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-', '_', '.', '/', '+', '=', '@');
        return sanitized.Length switch
        {
            0 => NoWorkItemValue,
            > 63 => sanitized[..63].TrimEnd('-', '_', '.', '/', '+', '=', '@'),
            _ => sanitized,
        };
    }

    /// <summary>
    /// Maps a cloud failure onto a provisioning deferral: the cloud said no,
    /// which is an infrastructure signal — never a verdict on the work item's
    /// diff. Auth/quota failures get a longer recheck so an operator can fix
    /// credentials/capacity; throttling uses the base recheck.
    /// </summary>
    private SandboxProvisioningDeferredException ToDeferred(
        Ec2SandboxOptions opts, Ec2ApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            Ec2FailureKind.Unauthorized or Ec2FailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            Ec2FailureKind.QuotaExhausted =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            Ec2FailureKind.Throttled => ("throttled", baseRecheck),
            Ec2FailureKind.Conflict =>
                ("conflict", baseRecheck),
            Ec2FailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            Ec2FailureKind.Unreachable => ("unreachable", baseRecheck),
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

    /// <inheritdoc/>
    public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes() =>
        _activeSandboxes.Values
            .Where(entry => entry.Sandbox.IsTrackedActive)
            .Select(entry => (entry.WorkItemId, (IShutdownTeardownSandbox)entry.Sandbox))
            .ToList();

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    /// <summary>Cloud resource identity collected during one provisioning attempt.</summary>
    private sealed class ProvisionedResources
    {
        public string? InstanceId { get; set; }

        public string? KeyName { get; set; }

        public bool KeyImported { get; set; }

        public string? SecurityGroupName { get; set; }

        public string? SecurityGroupId { get; set; }

        public string? AllocationId { get; set; }

        public string? AssociationId { get; set; }

        public string? RequestId { get; set; }

        public ProvisionedResources Snapshot() => new()
        {
            InstanceId = InstanceId,
            KeyName = KeyName,
            KeyImported = KeyImported,
            SecurityGroupName = SecurityGroupName,
            SecurityGroupId = SecurityGroupId,
            AllocationId = AllocationId,
            AssociationId = AssociationId,
            RequestId = RequestId,
        };
    }

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, Ec2Sandbox Sandbox);

    private sealed record Ec2MountPlan(
        IReadOnlyList<Ec2TmpfsMount> TmpfsMounts,
        IReadOnlyList<Ec2StagedMount> Staged);
}
