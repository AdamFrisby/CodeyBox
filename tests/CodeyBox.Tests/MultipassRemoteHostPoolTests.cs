using System.Collections.Concurrent;
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
        public int LaunchCount => _calls.Count(argv => argv.Contains("launch"));

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
            if (argv.Contains("info"))
            {
                var vm = argv.SkipWhile(a => a != "info").Skip(1).First();
                return Task.FromResult(new ProcessRunResult(
                    0,
                    $"{{\"info\":{{\"{vm}\":{{\"state\":\"Running\"}}}}}}",
                    ""));
            }
            if (argv.Contains("list"))
                return Task.FromResult(new ProcessRunResult(0, "{\"list\":[]}", ""));
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct) =>
            Task.CompletedTask;

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
