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

namespace CodeyBox.HetznerSandboxPlugin;

/// <summary>
/// Sandbox provider plugin backed by Hetzner Cloud servers (the OpenStack
/// remote-VM provider is the concrete analogue). Contributes the
/// <c>hetzner</c> provider kind through the plugin trust model: the host owns
/// egress classification, so this kind is always <c>NotEnforced</c> — the
/// guest runs on infrastructure CodeyBox does not control and the host
/// nftables egress guarantee cannot apply. Acquisitions requiring enforced
/// egress (a named network profile) are refused by placement before this
/// provider is ever called, and again here if one ever arrives.
///
/// <para>Off unless an operator enables it (the
/// <c>codeybox.hetzner-sandbox</c> plugin must be allowlisted AND its
/// <c>Enabled</c> option set). The API token comes only from the host
/// credential chain (environment); it stays on the host and is never copied
/// into guest user-data, logs, or prompts.</para>
///
/// <para>One work-item-owned server per acquisition, booting the configured
/// approved image pinned by id or exact name. No baseline bake/snapshot
/// capability is advertised: acquisitions always boot that image.</para>
/// </summary>
[CodeyBoxPlugin(HetznerSandboxOptions.PluginId, "Hetzner Cloud sandbox provider")]
public sealed class HetznerSandboxProvider :
    ISandboxProvider,
    IPluginInitializer,
    IActiveSandboxProvider,
    IDisposable
{
    internal const string OwnedLabel = "codeybox-owned";
    internal const string OwnerLabel = "codeybox-owner";
    internal const string WorkItemLabel = "codeybox-work-item";
    internal const string RequestLabel = "codeybox-request";
    internal const string CreatedLabel = "codeybox-created";
    internal const string OwnedValue = "true";
    internal const string NoWorkItemValue = "none";

    private static readonly TimeSpan MinimumOperatorFixRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeleteConfirmTimeout = TimeSpan.FromSeconds(120);

    private readonly Func<HetznerSandboxOptions> _readOptions;
    private readonly Func<string, string?> _environment;
    private readonly IHetznerKeyGenerator _keys;
    private readonly IHetznerDnsResolver _dns;
    private readonly IHetznerTransportFactory _transports;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, ActiveSandboxEntry> _activeSandboxes = new(StringComparer.Ordinal);
    private IPluginHost? _host;
    private ILogger _log = NullLogger.Instance;

    /// <summary>DI entry point. Options arrive via <see cref="IPluginInitializer.InitializeAsync"/>.</summary>
    public HetznerSandboxProvider(TimeProvider? clock = null)
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
    internal HetznerSandboxProvider(
        Func<HetznerSandboxOptions>? readOptions,
        HttpClient? http,
        IHetznerKeyGenerator? keys,
        IHetznerDnsResolver? dns,
        IHetznerTransportFactory? transports,
        Func<string, string?>? environment,
        TimeProvider? clock,
        ILogger? log)
    {
        _clock = clock ?? TimeProvider.System;
        _environment = environment ?? (name => Environment.GetEnvironmentVariable(name));
        var runner = new CodeyBox.HostProcess.DefaultProcessRunner();
        _keys = keys ?? new SshKeygenGenerator(runner);
        _dns = dns ?? new SystemHetznerDnsResolver();
        _transports = transports ?? new OpenSshHetznerTransportFactory(runner);
        _http = http ?? new HttpClient() { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = http is null;
        _readOptions = readOptions ?? (() => HetznerSandboxOptions.FromConfiguration(_host?.ScopedConfig));
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
            "Hetzner sandbox provider initialized (enabled={Enabled}, location={Location}, serverType={ServerType})",
            options.Enabled,
            string.IsNullOrEmpty(options.Location) ? "(unset)" : options.Location,
            string.IsNullOrEmpty(options.ServerType) ? "(unset)" : options.ServerType);
        return Task.CompletedTask;
    }

    public string Name => HetznerSandboxOptions.ProviderKind;

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
    /// Real servers with a dedicated guest kernel — including genuine
    /// RAM-backed tmpfs mounts. This is a guest-boundary claim only; egress
    /// stays NotEnforced (host-classified, the plugin cannot promote itself).
    /// </summary>
    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    /// <summary>Honest capability set: fresh server per work item, torn down on disposal. No baseline bake.</summary>
    public IReadOnlyList<string> DeclaredCapabilities => [SandboxCapabilities.Teardown];

    private HetznerSandboxOptions ReadOptions() => _readOptions();

    private HetznerApiClient CreateClient(HetznerSandboxOptions opts) =>
        new(_http, _clock, opts.ToClientLimits());

    // ------------------------------------------------------------------
    // Provisioning
    // ------------------------------------------------------------------

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var opts = ReadValidatedOptions();

        if (spec.Flavor != SandboxProfileFlavor.Headless)
            throw new NotSupportedException("hetzner sandbox provider does not support the graphical sandbox flavor.");

        // Host-owned egress rule, enforced as an in-provider backstop too:
        // a named profile needs host nftables that cannot exist on this kind.
        SandboxEgressPolicy.EnsureEnforcedEgressForProfile(HetznerSandboxOptions.ProviderKind, spec.Network.ProfileName);

        var ownerId = ResolveOwnerId(opts);
        var mounts = PlanMounts(spec, opts);
        var credentials = HetznerCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var workItemId = spec.TimingWorkItemId?.ToString() ?? string.Empty;

        var serverType = await api.GetServerTypeByNameAsync(credentials, opts.ServerType, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Hetzner server type '{opts.ServerType}' not found. Set CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:ServerType " +
                "to an exact visible server type name.");
        var image = await ResolveImageAsync(api, credentials, opts, spec, ct).ConfigureAwait(false);
        var location = await api.GetLocationByNameAsync(credentials, opts.Location, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Hetzner location '{opts.Location}' not found. Set CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:Location " +
                "to an exact visible location name.");

        ct.ThrowIfCancellationRequested();

        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var serverName = opts.ServerNamePrefix + suffix;
        var sshKeyName = opts.SshKeyNamePrefix + suffix;
        var firewallName = opts.FirewallNamePrefix + suffix;
        var floatingIpName = opts.FloatingIpNamePrefix + suffix;
        var createdAt = _clock.GetUtcNow();
        var labels = OwnershipLabels(ownerId, workItemId, requestId, createdAt);

        var sshTempDirectory = Path.Combine(Path.GetTempPath(), "codeybox-hetzner-" + suffix);
        Directory.CreateDirectory(sshTempDirectory);

        var tracked = new ProvisionedResources();
        try
        {
            var clientKey = await _keys.GenerateClientKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, serverName, ct).ConfigureAwait(false);
            var hostKey = await _keys.GenerateHostKeyAsync(
                opts.SshKeygenBinary, sshTempDirectory, serverName + "-host", ct).ConfigureAwait(false);

            var sshKey = await api.CreateSshKeyAsync(
                credentials, sshKeyName, clientKey.PublicKeyText, labels, ct).ConfigureAwait(false);
            tracked.SshKeyId = sshKey.Id;

            var egressIps = await ResolveEgressIpsAsync(spec.Network, ct).ConfigureAwait(false);
            var rules = HetznerFirewallPolicy.BuildRules(
                opts.OrchestratorSshCidrs, egressIps,
                opts.DnsServerIps, opts.NtpServerIps, opts.MaxFirewallRules);
            var firewall = await api.CreateFirewallAsync(
                credentials, firewallName, rules, labels, ct).ConfigureAwait(false);
            tracked.FirewallId = firewall.Id;

            var userData = HetznerCloudInit.Build(new HetznerCloudInitSpec(
                serverName, opts.SshUser, clientKey.PublicKeyText,
                hostKey.PrivateKeyPem, hostKey.PublicKeyText, mounts.TmpfsMounts));

            var server = await CreateServerWithReconcileAsync(
                api, credentials, opts, ownerId, requestId,
                new HetznerServerSpec(
                    serverName, serverType.Name!, image.Id, location.Name!,
                    [sshKeyName],
                    opts.NetworkId > 0 ? [opts.NetworkId] : [],
                    userData, labels, [firewall.Id],
                    opts.EnablePublicIpv4, opts.EnablePublicIpv6),
                ct).ConfigureAwait(false);
            tracked.ServerId = server.Id;

            string? floatingIpAddress = null;
            if (!string.IsNullOrWhiteSpace(opts.FloatingIpHomeLocation))
            {
                var floating = await api.CreateFloatingIpAsync(
                    credentials, floatingIpName, opts.FloatingIpHomeLocation.Trim(),
                    server.Id, labels, ct).ConfigureAwait(false);
                tracked.FloatingIpId = floating.Id;
                floatingIpAddress = floating.Ip;
            }

            var running = await WaitForRunningAsync(api, credentials, opts, server.Id, ct).ConfigureAwait(false);

            var address = await ResolveServerAddressAsync(
                api, credentials, opts, running, floatingIpAddress, ct).ConfigureAwait(false);

            var knownHostsPath = Path.Combine(sshTempDirectory, "known_hosts");
            await WriteKnownHostsAsync(knownHostsPath, address, hostKey.PublicKeyText, ct).ConfigureAwait(false);
            var transport = _transports.Create(new HetznerSshTransportSpec(
                $"{opts.SshUser}@{address}", opts.SshPort, clientKey.PrivateKeyPath,
                knownHostsPath, opts.SshBinary, opts.SshConnectTimeoutSeconds));
            await WaitForSshReadyAsync(transport, opts, ct).ConfigureAwait(false);

            var sandbox = new HetznerSandbox(
                serverName, spec, mounts.Staged, transport,
                () => DeleteCloudResourcesAsync(
                    tracked.Snapshot(), ownerId),
                () => MarkNoLongerActive(serverName),
                sshTempDirectory, _log);
            _activeSandboxes[serverName] = new ActiveSandboxEntry(
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
                    $"hetzner staging to {serverName} failed: {ex.Message}",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }

            _log.LogInformation("Created hetzner sandbox {Name} (server {ServerId})", serverName, server.Id);
            return sandbox;
        }
        catch (SandboxProvisioningDeferredException)
        {
            // Thrown by the SSH/staging waits after cloud resources exist —
            // remove them before the item requeues, or the reaper owns them.
            await CleanupAfterFailureAsync(serverName, tracked, ownerId, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailureAsync(serverName, tracked, ownerId, sshTempDirectory).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SandboxProvisioningDeferredException)
        {
            await CleanupAfterFailureAsync(serverName, tracked, ownerId, sshTempDirectory).ConfigureAwait(false);
            if (ex is HetznerApiException apiEx)
                throw ToDeferred(opts, apiEx, $"hetzner create failed for {serverName}");
            throw;
        }
    }

    /// <summary>
    /// Creates the server, reconciling ambiguous failures by stable request
    /// identity before anything is resubmitted: when the create call may have
    /// acted despite failing (transport cut, timeout, throttle, 5xx), the
    /// provider lists for the exact request label and adopts the server it
    /// finds instead of creating a duplicate. There is no vendor idempotency
    /// key on this call — the label IS the idempotency mechanism.
    /// </summary>
    private async Task<HetznerServer> CreateServerWithReconcileAsync(
        HetznerApiClient api, HetznerCredentials credentials, HetznerSandboxOptions opts,
        string ownerId, string requestId, HetznerServerSpec spec, CancellationToken ct)
    {
        try
        {
            var created = await api.CreateServerAsync(credentials, spec, ct).ConfigureAwait(false);
            if (created.Id <= 0)
            {
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, "create server", "empty server id");
            }
            return created;
        }
        catch (HetznerApiException ex) when (ex.MayHaveCreated)
        {
            HetznerServer? reconciled = null;
            try
            {
                reconciled = await ReconcileServerByRequestAsync(
                    api, credentials, opts, ownerId, requestId, ct).ConfigureAwait(false);
            }
            catch (Exception reconcileEx)
            {
                _log.LogWarning(reconcileEx,
                    "Hetzner create failed ambiguously and reconciliation also failed; deferring without resubmit");
            }
            if (reconciled is not null)
            {
                _log.LogInformation(
                    "Hetzner create outcome was ambiguous but the server was reconciled by request label (server {ServerId}); adopting it",
                    reconciled.Id);
                return reconciled;
            }
            throw;
        }
    }

    /// <summary>
    /// Finds the server carrying the exact ownership triple
    /// (owned/owner/request). Returns null when there is none; throws when
    /// several match — an ambiguous match must never silently pick one. The
    /// list is retried a bounded number of times: label-selector reads lag
    /// behind creates under eventual consistency, and one blind read must
    /// not declare the server missing.
    /// </summary>
    private async Task<HetznerServer?> ReconcileServerByRequestAsync(
        HetznerApiClient api, HetznerCredentials credentials,
        HetznerSandboxOptions opts,
        string ownerId, string requestId, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = await api.ListServersAsync(credentials, new Dictionary<string, string>
            {
                [OwnedLabel] = OwnedValue,
                [OwnerLabel] = ownerId,
                [RequestLabel] = requestId,
            }, ct).ConfigureAwait(false);
            if (candidates.Count > 1)
            {
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, "reconcile server",
                    $"request label '{requestId}' matched {candidates.Count} servers; refusing to pick one");
            }
            if (candidates.Count == 1)
                return candidates[0];
            if (attempt + 1 < maxAttempts)
                await Task.Delay(NextPollDelay(opts, attempt), _clock, ct).ConfigureAwait(false);
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Managed inventory / leak disposal
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        var ownerId = ResolveOwnerId(opts);
        var credentials = HetznerCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var servers = await api.ListServersAsync(credentials, new Dictionary<string, string>
        {
            [OwnedLabel] = OwnedValue,
            [OwnerLabel] = ownerId,
        }, ct).ConfigureAwait(false);
        var result = new List<ManagedSandboxInfo>(servers.Count);
        foreach (var server in servers)
        {
            if (server.Name is null || !server.Name.StartsWith(opts.ServerNamePrefix, StringComparison.Ordinal))
                continue;
            DateTimeOffset? createdAt = null;
            if (server.Labels?.TryGetValue(CreatedLabel, out var createdRaw) == true
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
                server.Name, createdAt, DiskBytes: null,
                IsTrackedActive: _activeSandboxes.ContainsKey(server.Name)));
        }
        return result;
    }

    public async Task DisposeLeakedAsync(string name, CancellationToken ct)
    {
        var opts = ReadValidatedOptions();
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(opts.ServerNamePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"Hetzner sandbox name '{name}' is not a managed codeybox sandbox name.", nameof(name));
        var ownerId = ResolveOwnerId(opts);
        var credentials = HetznerCredentials.Resolve(opts, _environment);
        var api = CreateClient(opts);
        var servers = await api.ListServersAsync(credentials, new Dictionary<string, string>
        {
            [OwnedLabel] = OwnedValue,
            [OwnerLabel] = ownerId,
        }, ct).ConfigureAwait(false);
        // Name alone gates candidacy; the exact label triple below authorizes
        // every deletion. A same-named server owned by someone else is never
        // touched — it fails closed instead.
        var match = servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (match is not null)
        {
            VerifyOwnedServer(match, ownerId, name);
            var requestId = match.Labels!.TryGetValue(RequestLabel, out var request) ? request : null;
            await DeleteOwnedServerSetAsync(
                api, credentials, ownerId, match.Id, requestId,
                opts.SshKeyNamePrefix + SuffixOf(name, opts.ServerNamePrefix),
                opts.FirewallNamePrefix + SuffixOf(name, opts.ServerNamePrefix),
                opts.FloatingIpNamePrefix + SuffixOf(name, opts.ServerNamePrefix),
                throwOnFailure: true, ct).ConfigureAwait(false);
        }
        else
        {
            var suffix = SuffixOf(name, opts.ServerNamePrefix);
            await DeleteOwnedServerSetAsync(
                api, credentials, ownerId, serverId: null, requestId: null,
                opts.SshKeyNamePrefix + suffix,
                opts.FirewallNamePrefix + suffix,
                opts.FloatingIpNamePrefix + suffix,
                throwOnFailure: true, ct).ConfigureAwait(false);
        }
        MarkNoLongerActive(name);
        await SweepOrphanResourcesAsync(api, credentials, opts, ownerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-verifies exact ownership at the moment of deletion: the
    /// <c>codeybox-owned</c>/<c>codeybox-owner</c> labels must match this
    /// provider and this host exactly. Name prefixes alone never authorize
    /// deletion.
    /// </summary>
    internal static void VerifyOwnedServer(HetznerServer server, string ownerId, string name)
    {
        if (server.Labels is null
            || !server.Labels.TryGetValue(OwnedLabel, out var owned)
            || !string.Equals(owned, OwnedValue, StringComparison.Ordinal)
            || !server.Labels.TryGetValue(OwnerLabel, out var owner)
            || !string.Equals(owner, ownerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to delete server '{name}': ownership labels do not prove this host owns it.");
        }
    }

    private static string SuffixOf(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;

    /// <summary>
    /// Deletes one sandbox's full resource set: graceful shutdown
    /// (best-effort), server delete with bounded deletion confirm, floating
    /// IP unassign+delete, firewall delete, SSH key delete. The request label
    /// finds every member of the set; the name-derived fallbacks only match
    /// exact names carrying exact ownership labels.
    /// </summary>
    private async Task DeleteOwnedServerSetAsync(
        HetznerApiClient api, HetznerCredentials credentials,
        string ownerId, long? serverId, string? requestId,
        string sshKeyName, string firewallName, string floatingIpName,
        bool throwOnFailure, CancellationToken ct)
    {
        var failures = new List<Exception>();
        if (serverId is { } id)
        {
            try
            {
                await api.ShutdownServerAsync(credentials, id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner server {ServerId}: graceful shutdown failed; deleting anyway", id);
            }
            try
            {
                await api.DeleteServerAsync(credentials, id, ct).ConfigureAwait(false);
                await api.WaitForServerDeletedAsync(credentials, id, ct, DeleteConfirmTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never claim deletion from a merely accepted request: the
                // labels stay on the server, so the reaper retries by label.
                failures.Add(new InvalidOperationException(
                    $"Hetzner server {id.ToString(CultureInfo.InvariantCulture)} deletion was not confirmed; orphan identity retained for the reaper.", ex));
            }
        }
        if (requestId is not null)
        {
            await DeleteLabeledSetAsync(api, credentials, ownerId, requestId, failures, ct).ConfigureAwait(false);
        }
        else
        {
            await DeleteNamedFallbacksAsync(
                api, credentials, ownerId, sshKeyName, firewallName, floatingIpName, failures, ct).ConfigureAwait(false);
        }
        if (failures.Count > 0 && throwOnFailure)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        foreach (var failure in failures)
            _log.LogWarning(failure, "Hetzner cleanup: resource deletion not confirmed; leak reaper will retry");
    }

    private async Task DeleteLabeledSetAsync(
        HetznerApiClient api, HetznerCredentials credentials,
        string ownerId, string requestId, List<Exception> failures, CancellationToken ct)
    {
        var selector = new Dictionary<string, string>
        {
            [OwnedLabel] = OwnedValue,
            [OwnerLabel] = ownerId,
            [RequestLabel] = requestId,
        };
        IReadOnlyList<HetznerFloatingIp> floatingIps = [];
        IReadOnlyList<HetznerFirewall> firewalls = [];
        IReadOnlyList<HetznerSshKey> sshKeys = [];
        try
        {
            floatingIps = await api.ListFloatingIpsAsync(credentials, selector, ct).ConfigureAwait(false);
            firewalls = await api.ListFirewallsAsync(credentials, selector, ct).ConfigureAwait(false);
            sshKeys = await api.ListSshKeysAsync(credentials, selector, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                "Hetzner cleanup: could not list request-labeled resources; orphan identity retained for the reaper.", ex));
            return;
        }
        foreach (var floating in floatingIps)
        {
            try
            {
                await api.UnassignFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false);
                if (!await api.DeleteFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false))
                    _log.LogWarning("Hetzner floating IP {FloatingIpId}: already gone", floating.Id);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"Hetzner floating IP {floating.Id.ToString(CultureInfo.InvariantCulture)} delete failed; orphan identity retained.", ex));
            }
        }
        foreach (var firewall in firewalls)
        {
            try
            {
                if (!await api.DeleteFirewallAsync(credentials, firewall.Id, ct).ConfigureAwait(false))
                    _log.LogWarning("Hetzner firewall {FirewallId}: already gone", firewall.Id);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"Hetzner firewall {firewall.Id.ToString(CultureInfo.InvariantCulture)} delete failed; orphan identity retained.", ex));
            }
        }
        foreach (var key in sshKeys)
        {
            try
            {
                if (!await api.DeleteSshKeyAsync(credentials, key.Id, ct).ConfigureAwait(false))
                    _log.LogWarning("Hetzner SSH key {SshKeyId}: already gone", key.Id);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"Hetzner SSH key {key.Id.ToString(CultureInfo.InvariantCulture)} delete failed; orphan identity retained.", ex));
            }
        }
    }

    private async Task DeleteNamedFallbacksAsync(
        HetznerApiClient api, HetznerCredentials credentials,
        string ownerId, string sshKeyName, string firewallName, string floatingIpName,
        List<Exception> failures, CancellationToken ct)
    {
        var selector = new Dictionary<string, string>
        {
            [OwnedLabel] = OwnedValue,
            [OwnerLabel] = ownerId,
        };
        try
        {
            var key = (await api.ListSshKeysAsync(credentials, selector, ct).ConfigureAwait(false))
                .FirstOrDefault(k => string.Equals(k.Name, sshKeyName, StringComparison.Ordinal));
            if (key is not null && !await api.DeleteSshKeyAsync(credentials, key.Id, ct).ConfigureAwait(false))
                _log.LogWarning("Hetzner SSH key {SshKeyId}: already gone", key.Id);
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"Hetzner SSH key '{sshKeyName}' delete failed; orphan identity retained.", ex));
        }
        try
        {
            var firewall = (await api.ListFirewallsAsync(credentials, selector, ct).ConfigureAwait(false))
                .FirstOrDefault(f => string.Equals(f.Name, firewallName, StringComparison.Ordinal));
            if (firewall is not null && !await api.DeleteFirewallAsync(credentials, firewall.Id, ct).ConfigureAwait(false))
                _log.LogWarning("Hetzner firewall {FirewallId}: already gone", firewall.Id);
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"Hetzner firewall '{firewallName}' delete failed; orphan identity retained.", ex));
        }
        try
        {
            var floating = (await api.ListFloatingIpsAsync(credentials, selector, ct).ConfigureAwait(false))
                .FirstOrDefault(f => string.Equals(f.Name, floatingIpName, StringComparison.Ordinal));
            if (floating is not null)
            {
                await api.UnassignFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false);
                if (!await api.DeleteFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false))
                    _log.LogWarning("Hetzner floating IP {FloatingIpId}: already gone", floating.Id);
            }
        }
        catch (Exception ex)
        {
            failures.Add(new InvalidOperationException(
                $"Hetzner floating IP '{floatingIpName}' delete failed; orphan identity retained.", ex));
        }
    }

    /// <summary>
    /// Ownership-scoped orphan sweep: deletes this host's SSH keys,
    /// firewalls, and floating IPs whose request label matches no live owned
    /// server. Only exact owner-label matches are candidates, and only
    /// unreferenced ones are deleted — live resources and other owners are
    /// never touched. Budget-bounded; failures are logged, never thrown.
    /// </summary>
    private async Task SweepOrphanResourcesAsync(
        HetznerApiClient api, HetznerCredentials credentials, HetznerSandboxOptions opts,
        string ownerId, CancellationToken ct)
    {
        var ownerSelector = new Dictionary<string, string>
        {
            [OwnedLabel] = OwnedValue,
            [OwnerLabel] = ownerId,
        };
        IReadOnlyList<HetznerServer> servers;
        try
        {
            servers = await api.ListServersAsync(credentials, ownerSelector, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Orphan sweep: server inventory failed; skipping sweep rather than guessing");
            return;
        }
        var liveRequests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            if (server.Labels?.TryGetValue(RequestLabel, out var request) == true
                && !string.IsNullOrEmpty(request))
            {
                liveRequests.Add(request);
            }
        }
        var swept = 0;
        var budget = Math.Max(1, Math.Min(opts.MaxListItems, 1000));
        try
        {
            foreach (var key in await api.ListSshKeysAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                if (IsUnreferenced(key.Labels, liveRequests))
                {
                    try
                    {
                        if (await api.DeleteSshKeyAsync(credentials, key.Id, ct).ConfigureAwait(false))
                            swept++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Orphan sweep: failed to delete SSH key {SshKeyId}", key.Id);
                    }
                }
            }
            foreach (var firewall in await api.ListFirewallsAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                if (IsUnreferenced(firewall.Labels, liveRequests))
                {
                    try
                    {
                        if (await api.DeleteFirewallAsync(credentials, firewall.Id, ct).ConfigureAwait(false))
                            swept++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Orphan sweep: failed to delete firewall {FirewallId}", firewall.Id);
                    }
                }
            }
            foreach (var floating in await api.ListFloatingIpsAsync(credentials, ownerSelector, ct).ConfigureAwait(false))
            {
                if (swept >= budget)
                    break;
                // Never release an address still assigned to a server: the
                // sweep only frees fully orphaned addresses.
                if (floating.Server is not null)
                    continue;
                if (IsUnreferenced(floating.Labels, liveRequests))
                {
                    try
                    {
                        await api.UnassignFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false);
                        if (await api.DeleteFloatingIpAsync(credentials, floating.Id, ct).ConfigureAwait(false))
                            swept++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Orphan sweep: failed to delete floating IP {FloatingIpId}", floating.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Orphan sweep: resource listing failed partway; stopping rather than guessing");
        }
        if (swept > 0)
            _log.LogInformation("Orphan sweep: released {Count} unreferenced hetzner resource(s)", swept);
    }

    private static bool IsUnreferenced(
        IReadOnlyDictionary<string, string>? labels, HashSet<string> liveRequests)
    {
        if (labels is null)
            return false;
        if (!labels.TryGetValue(RequestLabel, out var request) || string.IsNullOrEmpty(request))
            return false;
        return !liveRequests.Contains(request);
    }

    // ------------------------------------------------------------------
    // Readiness / address / staging
    // ------------------------------------------------------------------

    private async Task<HetznerServer> WaitForRunningAsync(
        HetznerApiClient api, HetznerCredentials credentials, HetznerSandboxOptions opts,
        long serverId, CancellationToken ct)
    {
        try
        {
            return await api.WaitForServerStatusAsync(
                credentials, serverId, ["running"], ct,
                TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds),
                faultStatuses: ["off", "unknown", "deleting"]).ConfigureAwait(false);
        }
        catch (HetznerApiException ex)
        {
            var status = await TryGetServerStatusAsync(api, credentials, serverId).ConfigureAwait(false);
            if (status is not null
                && (string.Equals(status, "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "unknown", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "deleting", StringComparison.OrdinalIgnoreCase)))
            {
                await TryDeleteServerAsync(api, credentials, serverId).ConfigureAwait(false);
                throw ToDeferred(opts, ex, $"hetzner server {serverId.ToString(CultureInfo.InvariantCulture)} entered {status} and was deleted");
            }
            throw;
        }
    }

    private async Task<string?> TryGetServerStatusAsync(
        HetznerApiClient api, HetznerCredentials credentials, long serverId)
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
        HetznerApiClient api, HetznerCredentials credentials, HetznerSandboxOptions opts,
        HetznerServer running, string? floatingIpAddress, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(floatingIpAddress))
            return floatingIpAddress.Trim();
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(opts.ReadyTimeoutSeconds);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var address = RunningServerAddress(running, opts);
            if (address is not null)
                return address;
            var refreshed = await api.GetServerAsync(credentials, running.Id, ct).ConfigureAwait(false);
            if (refreshed is not null)
            {
                running = refreshed;
                address = RunningServerAddress(running, opts);
                if (address is not null)
                    return address;
            }
            if (_clock.GetUtcNow() >= deadline)
            {
                throw new HetznerApiException(
                    HetznerFailureKind.Unexpected, "resolve server address",
                    $"server '{running.Id.ToString(CultureInfo.InvariantCulture)}' exposed no usable public address in time");
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    internal static string? RunningServerAddress(HetznerServer server, HetznerSandboxOptions opts)
    {
        if (opts.EnablePublicIpv4 && !string.IsNullOrWhiteSpace(server.PublicNet?.Ipv4?.Ip))
        {
            var ipv4 = server.PublicNet.Ipv4.Ip.Trim();
            if (IPAddress.TryParse(ipv4, out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                return ipv4;
            }
        }
        if (opts.EnablePublicIpv6 && !string.IsNullOrWhiteSpace(server.PublicNet?.Ipv6?.Ip))
        {
            var ipv6 = ServerIpv6Address(server.PublicNet.Ipv6.Ip.Trim());
            if (ipv6 is not null)
                return ipv6;
        }
        return null;
    }

    /// <summary>
    /// The vendor reports the IPv6 slot as a <c>/64</c> network; the guest's
    /// own address on that network is the first host address
    /// (<c>&lt;network&gt;::1</c>), which cloud-init configures on the
    /// interface. Anything else is refused rather than guessed.
    /// </summary>
    internal static string? ServerIpv6Address(string raw)
    {
        var addressText = raw;
        var slash = raw.IndexOf('/');
        if (slash >= 0)
            addressText = raw[..slash];
        if (!IPAddress.TryParse(addressText.Trim(), out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }
        var bytes = parsed.GetAddressBytes();
        bytes[15] |= 1;
        return new IPAddress(bytes).ToString();
    }

    private async Task WaitForSshReadyAsync(
        IRemoteHostTransport transport, HetznerSandboxOptions opts, CancellationToken ct)
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
                    "hetzner server did not accept SSH before the readiness deadline",
                    TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds));
            }
            await Task.Delay(NextPollDelay(opts, attempt++), _clock, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan NextPollDelay(HetznerSandboxOptions opts, int attempt)
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
        HetznerMountPlan mounts,
        CancellationToken ct)
    {
        var parents = mounts.Staged
            .Select(m => ParentOf(m.RemotePath))
            .Append(SandboxConventions.WorkDir)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var script = "set -e; " + string.Join("; ", parents.Select(p => "mkdir -p " + HetznerSandbox.QuoteShellWord(p)));
        var run = await transport.RunAsync(["bash", "-c", script], stdin: null, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new HetznerApiException(
                HetznerFailureKind.Unexpected, "prepare guest filesystem",
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
        string serverName, ProvisionedResources tracked, string ownerId, string sshTempDirectory)
    {
        MarkNoLongerActive(serverName);
        await DeleteCloudResourcesAsync(tracked.Snapshot(), ownerId).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(sshTempDirectory))
                Directory.Delete(sshTempDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hetzner sandbox {Name}: failed to remove SSH key directory", serverName);
        }
    }

    /// <summary>
    /// Handle-disposal and provisioning-failure cloud cleanup. Never throws:
    /// shutdown is best-effort, deletion is confirmed where possible, and
    /// anything unconfirmed stays labeled for the leak reaper and is logged.
    /// Disposal itself must not fail because the cloud did.
    /// </summary>
    private async Task DeleteCloudResourcesAsync(ProvisionedResources tracked, string ownerId)
    {
        HetznerSandboxOptions opts;
        HetznerCredentials credentials;
        HetznerApiClient api;
        try
        {
            opts = ReadValidatedOptions();
            credentials = HetznerCredentials.Resolve(opts, _environment);
            api = CreateClient(opts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hetzner sandbox cleanup: cannot resolve credentials/options; cloud resources may leak");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Clamp(opts.ReadyTimeoutSeconds, 30, 3600)));
        var ct = cts.Token;
        if (tracked.ServerId is { } serverId)
        {
            try
            {
                await api.ShutdownServerAsync(credentials, serverId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner server {ServerId}: graceful shutdown failed; deleting anyway", serverId);
            }
            try
            {
                await api.DeleteServerAsync(credentials, serverId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner server {ServerId}: delete request failed; leak reaper will retry", serverId);
            }
            try
            {
                await api.WaitForServerDeletedAsync(credentials, serverId, ct, DeleteConfirmTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner server {ServerId}: deletion not confirmed; leak reaper will retry", serverId);
            }
        }
        if (tracked.FloatingIpId is { } floatingIpId)
        {
            try
            {
                await api.UnassignFloatingIpAsync(credentials, floatingIpId, ct).ConfigureAwait(false);
                await api.DeleteFloatingIpAsync(credentials, floatingIpId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner floating IP {FloatingIpId}: delete failed; leak reaper will retry", floatingIpId);
            }
        }
        if (tracked.FirewallId is { } firewallId)
        {
            try
            {
                await api.DeleteFirewallAsync(credentials, firewallId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner firewall {FirewallId}: delete failed; leak reaper will retry", firewallId);
            }
        }
        if (tracked.SshKeyId is { } sshKeyId)
        {
            try
            {
                await api.DeleteSshKeyAsync(credentials, sshKeyId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hetzner SSH key {SshKeyId}: delete failed; leak reaper will retry", sshKeyId);
            }
        }
        _ = ownerId;
    }

    private static async Task TryDeleteServerAsync(
        HetznerApiClient api, HetznerCredentials credentials, long serverId)
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

    // ------------------------------------------------------------------
    // Provisioning guards
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the boot image for one acquisition: the spec
    /// <c>ImageReference</c> wins when set, otherwise the configured approved
    /// image. Both resolve by numeric id or exact name among available,
    /// non-deprecated images — a missing or deprecated match fails loudly
    /// (operator configuration), never as a deferred item verdict.
    /// </summary>
    private async Task<HetznerImage> ResolveImageAsync(
        HetznerApiClient api, HetznerCredentials credentials, HetznerSandboxOptions opts,
        SandboxSpec spec, CancellationToken ct)
    {
        var reference = string.IsNullOrWhiteSpace(spec.ImageReference) ? opts.Image : spec.ImageReference.Trim();
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:Image must be set to an approved image id or exact name.");
        }
        return await api.GetImageByReferenceAsync(credentials, reference, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Hetzner image '{reference}' not found among available images. Set CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:Image " +
                "to an approved image id or exact name.");
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
                _log.LogWarning("DNS resolution of egress host {Host} timed out; omitting from firewall egress", host);
            }
            catch (Exception ex)
            {
                // Best-effort defence in depth: an unresolvable allowlist name
                // narrows egress rather than failing the item — placement has
                // already refused anything needing enforced egress.
                _log.LogWarning(ex, "DNS resolution of egress host {Host} failed; omitting from firewall egress", host);
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

    private HetznerMountPlan PlanMounts(SandboxSpec spec, HetznerSandboxOptions opts)
    {
        _ = opts;
        var tmpfsRoots = new List<HetznerTmpfsMount>();
        foreach (var mount in spec.Mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.SandboxPath) || !mount.SandboxPath.StartsWith('/'))
                throw new ArgumentException($"Sandbox mount path must be absolute: {mount.SandboxPath}");
            if (mount.Tmpfs)
            {
                HetznerCloudInit.ValidateMountPath(mount.SandboxPath);
                tmpfsRoots.Add(new HetznerTmpfsMount(
                    mount.SandboxPath, mount.SizeBytes is > 0 ? mount.SizeBytes.Value : SandboxConventions.CredentialsTmpfsBytes));
            }
        }
        if (!tmpfsRoots.Any(m => m.Path.TrimEnd('/').Equals(
                SandboxConventions.CredentialsDir, StringComparison.Ordinal)))
        {
            // The agent credential writer always targets CredentialsDir, so
            // the guest always carries a RAM-backed mount there — even when
            // the spec stages no credential mount of its own.
            tmpfsRoots.Insert(0, new HetznerTmpfsMount(
                SandboxConventions.CredentialsDir, SandboxConventions.CredentialsTmpfsBytes));
        }
        var staged = new List<HetznerStagedMount>();
        foreach (var mount in spec.Mounts)
        {
            if (HetznerSandbox.IsCredentialPath(mount.SandboxPath))
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
                staged.Add(new HetznerStagedMount(mount.SandboxPath, HostPath: null, Writable: !mount.ReadOnly));
                continue;
            }
            var hostPath = Path.GetFullPath(mount.HostPath);
            if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
            {
                throw new SandboxMountSourceMissingException(hostPath, $"hetzner mount source path does not exist: {hostPath}");
            }
            staged.Add(new HetznerStagedMount(mount.SandboxPath, hostPath, Writable: !mount.ReadOnly));
        }
        return new HetznerMountPlan(tmpfsRoots, staged);
    }

    internal static bool IsUnderTmpfs(string sandboxPath, IReadOnlyList<HetznerTmpfsMount> tmpfsRoots)
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

    private HetznerSandboxOptions ReadValidatedOptions()
    {
        var opts = ReadOptions();
        if (!opts.Enabled)
        {
            throw new InvalidOperationException(
                "The hetzner sandbox provider is disabled. Enable it via " +
                $"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:Enabled=true plus the plugin allowlist.");
        }
        if (string.IsNullOrWhiteSpace(opts.Image))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:Image must be set to an approved image id or exact name.");
        if (string.IsNullOrWhiteSpace(opts.ServerType))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:ServerType must be set.");
        if (string.IsNullOrWhiteSpace(opts.Location))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:Location must be set.");
        if (!opts.EnablePublicIpv4 && !opts.EnablePublicIpv6 && string.IsNullOrWhiteSpace(opts.FloatingIpHomeLocation))
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId} disables both public IPv4 and IPv6 with no floating IP: " +
                "the provider would have no SSH path into the sandbox.");
        }
        if (string.IsNullOrWhiteSpace(opts.ServerNamePrefix) || !opts.ServerNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:ServerNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.SshKeyNamePrefix) || !opts.SshKeyNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:SshKeyNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.FirewallNamePrefix) || !opts.FirewallNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:FirewallNamePrefix must start with 'codeybox-'.");
        if (string.IsNullOrWhiteSpace(opts.FloatingIpNamePrefix) || !opts.FloatingIpNamePrefix.StartsWith("codeybox-", StringComparison.Ordinal))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:FloatingIpNamePrefix must start with 'codeybox-'.");
        if (opts.OrchestratorSshCidrs.Count == 0)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:OrchestratorSshCidrs must name at least one CIDR — " +
                "without it neither the provider nor any operator could SSH into a sandbox.");
        }
        try
        {
            foreach (var cidr in opts.OrchestratorSshCidrs)
                HetznerFirewallPolicy.NormalizeCidr(cidr, nameof(opts.OrchestratorSshCidrs));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:OrchestratorSshCidrs is invalid: {ex.Message}", ex);
        }
        foreach (var ip in opts.DnsServerIps.Concat(opts.NtpServerIps))
        {
            if (!IPAddress.TryParse(ip.Trim(), out _))
                throw new InvalidOperationException($"Hetzner DNS/NTP server IP is not an IP address: '{ip}'.");
        }
        if (string.IsNullOrWhiteSpace(opts.SshUser))
            throw new InvalidOperationException($"CodeyBox:Plugins:{HetznerSandboxOptions.PluginId}:SshUser must be set.");
        return opts;
    }

    internal string ResolveOwnerId(HetznerSandboxOptions opts)
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
    /// Builds the ownership label set stamped on every server, SSH key,
    /// firewall, and floating IP. The request label gives each provisioning
    /// attempt a stable identity for ambiguous-create reconciliation; the
    /// created label is unix epoch seconds (label values admit no colons).
    /// </summary>
    internal static Dictionary<string, string> OwnershipLabels(
        string ownerId, string workItemId, string requestId, DateTimeOffset createdAt)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OwnedLabel] = OwnedValue,
            [OwnerLabel] = ownerId,
            [WorkItemLabel] = SanitizeLabelValue(string.IsNullOrWhiteSpace(workItemId) ? NoWorkItemValue : workItemId),
            [RequestLabel] = requestId,
            [CreatedLabel] = createdAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
    }

    internal static string SanitizeLabelValue(string value)
    {
        var chars = value.Trim().Select(ch =>
            (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
            || ch is '-' or '_' or '.' ? ch : '-').ToArray();
        var sanitized = new string(chars).Trim('-', '_', '.');
        return sanitized.Length switch
        {
            0 => NoWorkItemValue,
            > 63 => sanitized[..63].TrimEnd('-', '_', '.'),
            _ => sanitized,
        };
    }

    /// <summary>
    /// Maps a cloud failure onto a provisioning deferral: the cloud said no,
    /// which is an infrastructure signal — never a verdict on the work item's
    /// diff. Auth/quota failures get a longer recheck so an operator can fix
    /// credentials/capacity; throttling honours Retry-After.
    /// </summary>
    private SandboxProvisioningDeferredException ToDeferred(
        HetznerSandboxOptions opts, HetznerApiException ex, string context)
    {
        var baseRecheck = TimeSpan.FromSeconds(opts.ProvisioningRecheckSeconds);
        var (errorClass, recheck) = ex.Kind switch
        {
            HetznerFailureKind.Unauthorized or HetznerFailureKind.Forbidden =>
                ("unauthorized", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            HetznerFailureKind.QuotaExhausted =>
                ("quota-exhausted", MaxOf(baseRecheck, MinimumOperatorFixRecheck)),
            HetznerFailureKind.Throttled =>
                ("throttled", ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : baseRecheck),
            HetznerFailureKind.Conflict =>
                ("conflict", baseRecheck),
            HetznerFailureKind.NotFound =>
                ("service-rejected", baseRecheck),
            HetznerFailureKind.Unreachable => ("unreachable", baseRecheck),
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

    /// <summary>Cloud resource ids collected during one provisioning attempt (long? so "never created" is explicit).</summary>
    private sealed class ProvisionedResources
    {
        public long? ServerId { get; set; }

        public long? SshKeyId { get; set; }

        public long? FirewallId { get; set; }

        public long? FloatingIpId { get; set; }

        public ProvisionedResources Snapshot() => new()
        {
            ServerId = ServerId,
            SshKeyId = SshKeyId,
            FirewallId = FirewallId,
            FloatingIpId = FloatingIpId,
        };
    }

    private sealed record ActiveSandboxEntry(WorkItemId WorkItemId, HetznerSandbox Sandbox);

    private sealed record HetznerMountPlan(
        IReadOnlyList<HetznerTmpfsMount> TmpfsMounts,
        IReadOnlyList<HetznerStagedMount> Staged);
}
