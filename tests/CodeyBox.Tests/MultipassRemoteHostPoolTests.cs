using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class MultipassRemoteHostPoolTests
{
    [Fact]
    public async Task CreateAsync_distributes_across_hosts_and_respects_host_caps()
    {
        var opts = Options(
            Host("a", cap: 2),
            Host("b", cap: 2));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        var sandboxes = new List<ISandbox>();
        try
        {
            for (var i = 0; i < 4; i++)
                sandboxes.Add(await provider.CreateAsync(Spec()));

            Assert.Equal(2, transports["a"].LaunchCount);
            Assert.Equal(2, transports["b"].LaunchCount);

            var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
                await provider.CreateAsync(Spec()));
            Assert.Equal("placement", ex.Operation);
            Assert.Equal("no-eligible-host", ex.ErrorClass);

            var snapshot = provider.SnapshotHostPool().OrderBy(h => h.HostId).ToArray();
            Assert.Equal(2, snapshot[0].Reserved);
            Assert.Equal(2, snapshot[1].Reserved);
        }
        finally
        {
            foreach (var sandbox in sandboxes)
                await sandbox.DisposeAsync();
        }

        Assert.All(provider.SnapshotHostPool(), row => Assert.Equal(0, row.Reserved));
    }

    [Fact]
    public async Task CreateAsync_honors_hot_reloaded_cordon_state()
    {
        var current = Options(
            Host("a", cap: 2, cordoned: true),
            Host("b", cap: 2));
        var transports = new HostTransportSet();
        var provider = Provider(() => current, transports);

        await using (await provider.CreateAsync(Spec()))
        {
            Assert.Equal(0, transports["a"].LaunchCount);
            Assert.Equal(1, transports["b"].LaunchCount);
        }

        current = Options(
            Host("a", cap: 2),
            Host("b", cap: 2, cordoned: true));

        await using (await provider.CreateAsync(Spec()))
        {
            Assert.Equal(1, transports["a"].LaunchCount);
            Assert.Equal(1, transports["b"].LaunchCount);
        }
    }

    [Fact]
    public async Task CreateAsync_marks_failed_host_unhealthy_and_retries_another_host()
    {
        var opts = Options(
            Host("a", cap: 2),
            Host("b", cap: 2));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnRun = true;
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec());

        Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        var unhealthy = Assert.Single(provider.SnapshotHostPool(), h => h.HostId == "a");
        Assert.False(unhealthy.RuntimeHealthy);
        Assert.Contains("simulated transport drop", unhealthy.RuntimeUnhealthyReason);
        Assert.Equal(1, transports["b"].LaunchCount);
    }

    [Fact]
    public async Task ExecAsync_transport_drop_defers_item_and_releases_host_on_dispose()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        var sandbox = await provider.CreateAsync(Spec());
        transports["a"].ThrowTransportOnExec = true;

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hello"] }));
        Assert.Equal("exec", ex.Operation);
        Assert.Equal("remote-host-unreachable", ex.ErrorClass);

        var unhealthy = Assert.Single(provider.SnapshotHostPool());
        Assert.False(unhealthy.RuntimeHealthy);
        Assert.Equal(1, unhealthy.Reserved);

        await sandbox.DisposeAsync();

        var afterDispose = Assert.Single(provider.SnapshotHostPool());
        Assert.Equal(0, afterDispose.Reserved);
    }

    [Fact]
    public async Task DisposeLeakedAsync_active_sandbox_releases_host_reservation()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        var sandbox = await provider.CreateAsync(Spec());

        Assert.Equal(1, Assert.Single(provider.SnapshotHostPool()).Reserved);

        await provider.DisposeLeakedAsync(sandbox.Id, CancellationToken.None);

        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task CreateAsync_filters_hosts_by_allowed_network_profile()
    {
        var opts = Options(
            Host("work-host", cap: 2, allowedProfiles: ["work"]),
            Host("audit-host", cap: 2, allowedProfiles: ["audit"]));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec("audit"));

        Assert.Equal("audit-host", ((MultipassRemoteSandbox)sandbox).HostId);
        Assert.Equal(0, transports["work-host"].LaunchCount);
        Assert.Equal(1, transports["audit-host"].LaunchCount);
    }

    [Fact]
    public async Task CreateAsync_skips_configured_unhealthy_hosts()
    {
        var opts = Options(
            Host("a", cap: 2, healthy: false),
            Host("b", cap: 2));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec());

        Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        Assert.Equal(0, transports["a"].LaunchCount);
        Assert.Equal(1, transports["b"].LaunchCount);
    }

    [Fact]
    public async Task CreateAsync_counts_existing_managed_vms_against_host_capacity()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ManagedNames.Add("codeybox-r-existing");
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec());

        Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        Assert.Equal(0, transports["a"].LaunchCount);
        Assert.Equal(1, transports["b"].LaunchCount);
    }

    [Fact]
    public async Task CreateAsync_when_all_hosts_unreachable_reports_all_hosts_unavailable()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnRun = true;
        transports["b"].ThrowTransportOnRun = true;
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("placement", ex.Operation);
        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        Assert.IsType<RemoteSshTransportException>(ex.InnerException);
        Assert.All(provider.SnapshotHostPool(), row => Assert.Equal(0, row.Reserved));
    }

    [Fact]
    public async Task CreateAsync_releases_reservation_after_remote_provisioning_failure_cleanup_succeeds()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].LaunchExitCode = 1;
        var provider = Provider(() => opts, transports);

        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
        Assert.Equal(1, transports["a"].DeleteCount);
    }

    [Fact]
    public async Task CreateAsync_retains_reservation_when_rollback_cleanup_fails()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].LaunchExitCode = 1;
        transports["a"].DeleteExitCode = 1;
        var provider = Provider(() => opts, transports);

        await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal(1, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task ListAllManagedAsync_returns_healthy_hosts_when_one_host_fails()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnRun = true;
        transports["b"].ManagedNames.Add("codeybox-r-bbbbb");
        var provider = Provider(() => opts, transports);

        var infos = await provider.ListAllManagedAsync(CancellationToken.None);

        var info = Assert.Single(infos);
        Assert.Equal("codeybox-r-bbbbb", info.Name);
        Assert.Equal("b", info.HostId);
    }

    [Fact]
    public async Task DisposeLeakedAsync_uses_managed_host_identity()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        await provider.DisposeLeakedAsync(
            new ManagedSandboxInfo("codeybox-r-leak", null, null, IsTrackedActive: false, HostId: "b"),
            CancellationToken.None);

        Assert.Equal(0, transports["a"].DeleteCount);
        Assert.Equal(1, transports["b"].DeleteCount);
        Assert.Equal(1, transports["b"].RmCount);
    }

    [Fact]
    public async Task CreateAsync_emits_remote_placement_and_deferral_metrics()
    {
        var measurements = new ConcurrentQueue<(string Instrument, long Value, string? TagValue)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "CodeyBox.Sandbox"
                && (instrument.Name == "codeybox.sandbox.remote_placement.count"
                    || instrument.Name == "codeybox.sandbox.remote_placement.deferrals"))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tag = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key is "outcome" or "reason")
                    tag = tags[i].Value?.ToString();
            }
            measurements.Enqueue((instrument.Name, value, tag));
        });
        listener.Start();

        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);
        var sandbox = await provider.CreateAsync(Spec());
        try
        {
            await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
                await provider.CreateAsync(Spec()));
        }
        finally
        {
            await sandbox.DisposeAsync();
        }

        Assert.Contains(measurements, m =>
            m.Instrument == "codeybox.sandbox.remote_placement.count" && m.TagValue == "reserved");
        Assert.Contains(measurements, m =>
            m.Instrument == "codeybox.sandbox.remote_placement.count" && m.TagValue == "created");
        Assert.Contains(measurements, m =>
            m.Instrument == "codeybox.sandbox.remote_placement.deferrals" && m.TagValue == "no-eligible-host");
    }

    private static MultipassRemoteSandboxProvider Provider(
        Func<MultipassRemoteSandboxOptions> opts,
        HostTransportSet transports) =>
        new(
            opts,
            host => transports[host.HostId],
            NullLogger<MultipassRemoteSandboxProvider>.Instance);

    private static SandboxSpec Spec(string? networkProfile = null) => new()
    {
        ImageReference = "24.04",
        WorkingDirectory = "/work",
        Network = new SandboxNetworkPolicy { ProfileName = networkProfile },
    };

    private static MultipassRemoteSandboxOptions Options(params MultipassRemoteExecutorHostOptions[] hosts) => new()
    {
        SshTarget = "unused-default",
        RemoteStagingRoot = "/remote/staging",
        PlacementRecheckIn = TimeSpan.FromMilliseconds(10),
        RuntimeUnhealthyBackoff = TimeSpan.FromMinutes(10),
        ExecutorHosts = hosts,
    };

    private static MultipassRemoteExecutorHostOptions Host(
        string id,
        int cap,
        bool cordoned = false,
        bool healthy = true,
        IReadOnlyList<string>? allowedProfiles = null) =>
        new()
        {
            Id = id,
            SshTarget = $"{id}.example",
            MaxConcurrentSandboxes = cap,
            Cordoned = cordoned,
            Healthy = healthy,
            AllowedNetworkProfiles = allowedProfiles,
        };

    private sealed class HostTransportSet
    {
        private readonly ConcurrentDictionary<string, ScriptedTransport> _transports = new(StringComparer.Ordinal);

        public ScriptedTransport this[string hostId] =>
            _transports.GetOrAdd(hostId, static id => new ScriptedTransport(id));
    }

    private sealed class ScriptedTransport(string hostId) : IRemoteHostTransport
    {
        private readonly ConcurrentQueue<IReadOnlyList<string>> _calls = new();

        public string DiagnosticId => $"fake-{hostId}";
        public bool ThrowTransportOnRun { get; set; }
        public bool ThrowTransportOnExec { get; set; }
        public int LaunchExitCode { get; set; }
        public int DeleteExitCode { get; set; }
        public List<string> ManagedNames { get; } = [];
        public int LaunchCount => _calls.Count(argv => argv.Contains("launch"));
        public int DeleteCount => _calls.Count(argv => argv.Contains("delete"));
        public int RmCount => _calls.Count(argv => argv.Count >= 2 && argv[0] == "rm" && argv[1] == "-rf");

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null)
        {
            _calls.Enqueue(argv.ToArray());
            if (ThrowTransportOnRun)
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop");
            if (ThrowTransportOnExec && argv.Contains("exec") && argv.Contains("bash"))
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop during exec");
            if (argv.Contains("launch") && LaunchExitCode != 0)
                return Task.FromResult(new ProcessRunResult(LaunchExitCode, "", "launch failed"));
            if (argv.Contains("delete") && DeleteExitCode != 0)
                return Task.FromResult(new ProcessRunResult(DeleteExitCode, "", "delete failed"));
            if (argv.Contains("info"))
            {
                var vm = argv.SkipWhile(a => a != "info").Skip(1).First();
                return Task.FromResult(new ProcessRunResult(
                    0,
                    $"{{\"info\":{{\"{vm}\":{{\"state\":\"Running\"}}}}}}",
                    ""));
            }
            if (argv.Contains("list"))
            {
                var entries = string.Join(",", ManagedNames.Select(name => $"{{\"name\":\"{name}\",\"state\":\"Running\"}}"));
                return Task.FromResult(new ProcessRunResult(0, $"{{\"list\":[{entries}]}}", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct) =>
            Task.CompletedTask;

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
