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
    public async Task CreateAsync_concurrent_requests_never_oversubscribe_host_caps()
    {
        const int perHostCapacity = 3;
        const int aggregateCapacity = perHostCapacity * 2;
        const int requestedSandboxes = aggregateCapacity + 12;

        var opts = Options(
            Host("a", cap: perHostCapacity),
            Host("b", cap: perHostCapacity));
        var transports = new HostTransportSet();
        var stagingGate = new AsyncGate(aggregateCapacity);
        transports["a"].StagingGate = stagingGate;
        transports["b"].StagingGate = stagingGate;
        var provider = Provider(() => opts, transports);

        var createTasks = Enumerable.Range(0, requestedSandboxes)
            .Select(_ => provider.CreateAsync(Spec()))
            .ToArray();

        IReadOnlyList<SandboxHostPoolEntry> blockedSnapshot = [];
        int blockedLaunchCountA = 0;
        int blockedLaunchCountB = 0;
        try
        {
            await stagingGate.WaitForExpectedAsync().WaitAsync(TimeSpan.FromSeconds(5));
            blockedSnapshot = provider.SnapshotHostPool();
            blockedLaunchCountA = transports["a"].LaunchCount;
            blockedLaunchCountB = transports["b"].LaunchCount;
        }
        finally
        {
            stagingGate.Release();
        }

        var outcomes = await Task.WhenAll(createTasks.Select(ObserveCreateAsync));
        var sandboxes = outcomes
            .Where(static outcome => outcome.Sandbox is not null)
            .Select(static outcome => outcome.Sandbox!)
            .ToArray();
        try
        {
            Assert.All(blockedSnapshot, row =>
                Assert.True(row.Reserved <= row.Capacity, $"{row.HostId} reserved {row.Reserved}/{row.Capacity}"));
            Assert.True(blockedLaunchCountA <= perHostCapacity);
            Assert.True(blockedLaunchCountB <= perHostCapacity);
            Assert.Equal(aggregateCapacity, sandboxes.Length);
            Assert.Equal(
                requestedSandboxes - aggregateCapacity,
                outcomes.Count(static outcome => outcome.Error is SandboxProvisioningDeferredException
                {
                    Operation: "placement",
                    ErrorClass: "no-eligible-host",
                }));
            Assert.True(transports["a"].LaunchCount <= perHostCapacity);
            Assert.True(transports["b"].LaunchCount <= perHostCapacity);
        }
        finally
        {
            foreach (var sandbox in sandboxes)
                await sandbox.DisposeAsync();
        }

        static async Task<(ISandbox? Sandbox, Exception? Error)> ObserveCreateAsync(Task<ISandbox> task)
        {
            try
            {
                return (await task.ConfigureAwait(false), null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }
    }

    [Fact]
    public async Task CreateAsync_defers_when_remote_inventory_output_exceeds_cap()
    {
        var opts = Options(Host("a", cap: 1)) with { RemoteInventoryMaxOutputBytes = 32 };
        var transports = new HostTransportSet();
        transports["a"].ListStdoutOverride = "{\"list\":[" + new string('x', 256) + "]}";
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("placement", ex.Operation);
        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        Assert.Contains(transports["a"].ListStdoutCaps, cap => cap == opts.RemoteInventoryMaxOutputBytes);
        var host = Assert.Single(provider.SnapshotHostPool());
        Assert.False(host.RuntimeHealthy);
        Assert.Equal(0, host.Reserved);
        Assert.Equal(0, transports["a"].LaunchCount);
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
        transports["a"].ThrowTransportOnLaunch = true;
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec());

        Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        var unhealthy = Assert.Single(provider.SnapshotHostPool(), h => h.HostId == "a");
        Assert.False(unhealthy.RuntimeHealthy);
        Assert.Contains("simulated transport drop", unhealthy.RuntimeUnhealthyReason);
        Assert.Equal(1, transports["b"].LaunchCount);
        Assert.Equal(1, transports["a"].DeleteCount);
    }

    [Fact]
    public async Task CreateAsync_defers_with_retained_sandbox_when_failed_host_rollback_is_unconfirmed()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnLaunch = true;
        transports["a"].DeleteExitCode = 1;
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("create-rollback-cleanup", ex.Operation);
        Assert.Equal("remote-cleanup-unconfirmed", ex.ErrorClass);
        Assert.NotNull(ex.RetainedSandboxName);
        Assert.Equal(1, transports["a"].DeleteCount);
        Assert.Equal(0, transports["b"].LaunchCount);
        var snapshot = provider.SnapshotHostPool().OrderBy(h => h.HostId).ToArray();
        Assert.Equal(1, snapshot[0].Reserved);
        Assert.Equal(0, snapshot[1].Reserved);
        Assert.False(snapshot[0].RuntimeHealthy);
    }

    [Fact]
    public async Task CreateAsync_routes_around_host_runtime_command_failure_when_cleanup_succeeds()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].LaunchExitCode = 1;
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec());

        Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        Assert.Equal(1, transports["a"].DeleteCount);
        Assert.Equal(1, transports["b"].LaunchCount);
        var snapshot = provider.SnapshotHostPool().OrderBy(h => h.HostId).ToArray();
        Assert.Equal(0, snapshot[0].Reserved);
        Assert.Equal(1, snapshot[1].Reserved);
        Assert.False(snapshot[0].RuntimeHealthy);
    }

    [Fact]
    public async Task CreateAsync_runtime_unhealthy_host_is_not_probed_until_backoff_expires()
    {
        var current = Options(
            Host("a", cap: 2),
            Host("b", cap: 2));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnLaunch = true;
        var provider = Provider(() => current, transports);

        var first = await provider.CreateAsync(Spec());
        transports["a"].ThrowTransportOnLaunch = false;
        var listCountAfterFailure = transports["a"].ListCount;
        var second = await provider.CreateAsync(Spec());
        try
        {
            Assert.Equal("b", ((MultipassRemoteSandbox)first).HostId);
            Assert.Equal("b", ((MultipassRemoteSandbox)second).HostId);
            Assert.Equal(listCountAfterFailure, transports["a"].ListCount);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_allows_runtime_unhealthy_host_after_backoff_expires()
    {
        var current = Options(
            [
            Host("a", cap: 2),
            Host("b", cap: 2),
            ],
            runtimeUnhealthyBackoff: TimeSpan.FromMilliseconds(25));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnLaunch = true;
        var provider = Provider(() => current, transports);

        var first = await provider.CreateAsync(Spec());
        transports["a"].ThrowTransportOnLaunch = false;
        await Task.Delay(TimeSpan.FromMilliseconds(60));
        var second = await provider.CreateAsync(Spec());
        try
        {
            Assert.Equal("b", ((MultipassRemoteSandbox)first).HostId);
            Assert.Equal("a", ((MultipassRemoteSandbox)second).HostId);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
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
    public async Task DisposeAsync_after_exec_transport_drop_releases_active_tracking_when_syncback_cannot_run()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);
        var hostTemp = Path.Combine(Path.GetTempPath(), "codeybox-remote-host-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostTemp);
        try
        {
            var sandbox = await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "24.04",
                WorkingDirectory = "/work",
                Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = hostTemp, ReadOnly = false }],
            });
            transports["a"].ManagedNames.Add(sandbox.Id);
            transports["a"].ThrowTransportOnExec = true;

            await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
                await sandbox.ExecAsync(new SandboxExec { Argv = ["echo", "hello"] }));

            transports["a"].ThrowTransportOnStageOut = true;
            await sandbox.DisposeAsync();

            Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
            var leaked = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
            Assert.Equal(sandbox.Id, leaked.Name);
            Assert.False(leaked.IsTrackedActive);
            Assert.Equal(0, transports["a"].DeleteCount);
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
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
    public async Task CreateAsync_matches_allowed_network_profile_case_insensitively()
    {
        var opts = Options(
            Host("work-host", cap: 2, allowedProfiles: ["Work"]));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec("work"));

        Assert.Equal("work-host", ((MultipassRemoteSandbox)sandbox).HostId);
        Assert.Equal(1, transports["work-host"].LaunchCount);
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
    public async Task CreateAsync_does_not_inventory_hosts_filtered_out_before_placement()
    {
        var opts = Options(
            Host("cordoned", cap: 2, cordoned: true),
            Host("unhealthy", cap: 2, healthy: false),
            Host("profile", cap: 2, allowedProfiles: ["audit"]),
            Host("eligible", cap: 2, allowedProfiles: ["work"]));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec("work"));

        Assert.Equal("eligible", ((MultipassRemoteSandbox)sandbox).HostId);
        Assert.Equal(0, transports["cordoned"].ListCount);
        Assert.Equal(0, transports["unhealthy"].ListCount);
        Assert.Equal(0, transports["profile"].ListCount);
        Assert.Equal(1, transports["eligible"].ListCount);
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

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
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
    public async Task ListAllManagedAsync_releases_retained_reservation_when_inventory_proves_vm_absent()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].LaunchExitCode = 1;
        transports["a"].DeleteExitCode = 1;
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));
        Assert.Equal("create-rollback-cleanup", ex.Operation);
        Assert.NotNull(ex.RetainedSandboxName);
        Assert.Equal(1, Assert.Single(provider.SnapshotHostPool()).Reserved);

        await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task CreateAsync_failed_delete_does_not_retain_capacity_when_info_proves_vm_absent()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].LaunchExitCode = 1;
        transports["a"].DeleteExitCode = 1;
        transports["a"].InfoExitCode = 1;
        transports["a"].InfoStderr = "instance not found";
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
        Assert.Equal(1, transports["a"].DeleteCount);
        Assert.Equal(1, transports["a"].InfoCount);
        Assert.Equal(1, transports["a"].RmCount);
    }

    [Fact]
    public async Task CreateAsync_host_runtime_launch_failure_marks_host_runtime_unhealthy()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].LaunchExitCode = 1;
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        var host = Assert.Single(provider.SnapshotHostPool());
        Assert.False(host.RuntimeHealthy);
        Assert.Equal(0, host.Reserved);
    }

    [Fact]
    public async Task CreateAsync_list_exit_marks_host_unhealthy_and_defers_when_all_hosts_fail_inventory()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ListExitCode = 1;
        transports["b"].ListExitCode = 1;
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(Spec()));

        Assert.Equal("placement", ex.Operation);
        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        Assert.All(provider.SnapshotHostPool(), row =>
        {
            Assert.False(row.RuntimeHealthy);
            Assert.Equal(0, row.Reserved);
        });
        Assert.Equal(0, transports["a"].LaunchCount);
        Assert.Equal(0, transports["b"].LaunchCount);
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
    public async Task ListAllManagedAsync_returns_healthy_hosts_when_one_host_metadata_scan_fails()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ThrowTransportOnMetadataScan = true;
        transports["a"].ManagedNames.Add("codeybox-r-aaaaa");
        transports["b"].ManagedNames.Add("codeybox-r-bbbbb");
        var provider = Provider(() => opts, transports);

        var inventory = await provider.ListManagedInventoryAsync(CancellationToken.None);

        var info = Assert.Single(inventory);
        Assert.Equal("codeybox-r-bbbbb", info.Name);
        Assert.Equal("b", info.HostId);
        Assert.False(provider.SnapshotHostPool().Single(h => h.HostId == "a").RuntimeHealthy);
        Assert.False(inventory.IsComplete);
        Assert.Equal(["b"], inventory.InventoriedHostIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
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
    public async Task DisposeLeakedAsync_refuses_duplicate_bare_name_discovery()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        transports["a"].ManagedNames.Add("codeybox-r-same");
        transports["b"].ManagedNames.Add("codeybox-r-same");
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeLeakedAsync("codeybox-r-same", CancellationToken.None));

        Assert.Contains("multiple executor hosts", ex.Message);
        Assert.Equal(0, transports["a"].DeleteCount);
        Assert.Equal(0, transports["b"].DeleteCount);
    }

    [Fact]
    public async Task DisposeLeakedAsync_refuses_shared_prefix_bare_name()
    {
        var opts = Options(
            Host("a", cap: 1, vmNamePrefix: "codeybox-r-"),
            Host("b", cap: 1, vmNamePrefix: "codeybox-r-"));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeLeakedAsync("codeybox-r-orphan", CancellationToken.None));

        Assert.Contains("share a matching prefix", ex.Message);
    }

    [Fact]
    public async Task DisposeLeakedAsync_refuses_unknown_managed_host_id()
    {
        var opts = Options(Host("a", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeLeakedAsync(
                new ManagedSandboxInfo("codeybox-r-leak", null, null, IsTrackedActive: false, HostId: "missing"),
                CancellationToken.None));

        Assert.Contains("not configured", ex.Message);
        Assert.Equal(0, transports["a"].DeleteCount);
    }

    [Fact]
    public async Task ListAllManagedAsync_tracks_active_sandboxes_by_host_and_name()
    {
        var opts = Options(
            Host("a", cap: 1),
            Host("b", cap: 1));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        await using var sandbox = await provider.CreateAsync(Spec());
        var vmName = sandbox.Id;
        transports["a"].ManagedNames.Add(vmName);
        transports["b"].ManagedNames.Add(vmName);

        var infos = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.True(Assert.Single(infos, i => i.HostId == "a").IsTrackedActive);
        Assert.False(Assert.Single(infos, i => i.HostId == "b").IsTrackedActive);
    }

    [Fact]
    public async Task DisposeLeakedAsync_refuses_managed_host_prefix_mismatch()
    {
        var opts = Options(Host("a", cap: 1, vmNamePrefix: "codeybox-a-"));
        var transports = new HostTransportSet();
        var provider = Provider(() => opts, transports);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.DisposeLeakedAsync(
                new ManagedSandboxInfo("codeybox-b-leak", null, null, IsTrackedActive: false, HostId: "a"),
                CancellationToken.None));

        Assert.Contains("does not match", ex.Message);
        Assert.Equal(0, transports["a"].DeleteCount);
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

    private static MultipassRemoteSandboxOptions Options(
        params MultipassRemoteExecutorHostOptions[] hosts) =>
        Options(hosts, runtimeUnhealthyBackoff: TimeSpan.FromMinutes(10));

    private static MultipassRemoteSandboxOptions Options(
        IReadOnlyList<MultipassRemoteExecutorHostOptions> hosts,
        TimeSpan runtimeUnhealthyBackoff) => new()
    {
        SshTarget = "unused-default",
        RemoteStagingRoot = "/remote/staging",
        PlacementRecheckIn = TimeSpan.FromMilliseconds(10),
        RuntimeUnhealthyBackoff = runtimeUnhealthyBackoff,
        NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["work"] = "cb-work",
            ["audit"] = "cb-audit",
        },
        ExecutorHosts = hosts,
    };

    private static MultipassRemoteExecutorHostOptions Host(
        string id,
        int cap,
        bool cordoned = false,
        bool healthy = true,
        IReadOnlyList<string>? allowedProfiles = null,
        string? vmNamePrefix = null) =>
        new()
        {
            Id = id,
            SshTarget = $"{id}.example",
            MaxConcurrentSandboxes = cap,
            Cordoned = cordoned,
            Healthy = healthy,
            AllowedNetworkProfiles = allowedProfiles,
            VmNamePrefix = vmNamePrefix,
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
        public bool ThrowTransportOnLaunch { get; set; }
        public bool ThrowTransportOnExec { get; set; }
        public bool ThrowTransportOnStageOut { get; set; }
        public bool ThrowTransportOnMetadataScan { get; set; }
        public AsyncGate? StagingGate { get; set; }
        public int LaunchExitCode { get; set; }
        public int DeleteExitCode { get; set; }
        public int InfoExitCode { get; set; }
        public int ListExitCode { get; set; }
        public string? ListStdoutOverride { get; set; }
        public string InfoStderr { get; set; } = "";
        public List<string> ManagedNames { get; } = [];
        public ConcurrentQueue<int?> ListStdoutCaps { get; } = new();
        public int LaunchCount => _calls.Count(argv => argv.Contains("launch"));
        public int DeleteCount => _calls.Count(argv => argv.Contains("delete"));
        public int RmCount => _calls.Count(argv => argv.Count >= 2 && argv[0] == "rm" && argv[1] == "-rf");
        public int ListCount => _calls.Count(argv => argv.Contains("list"));
        public int InfoCount => _calls.Count(argv => argv.Contains("info"));

        public async Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            bool killOnOutputLimit = true)
        {
            _calls.Enqueue(argv.ToArray());
            if (ThrowTransportOnRun)
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop");
            if (ThrowTransportOnLaunch && argv.Contains("launch"))
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop during launch");
            if (IsStagingDirectorySetup(argv) && StagingGate is { } stagingGate)
                await stagingGate.WaitAsync(ct).ConfigureAwait(false);
            if (ThrowTransportOnExec && argv.Contains("exec") && argv.Contains("bash"))
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop during exec");
            if (ThrowTransportOnMetadataScan
                && argv.Count >= 3
                && argv[0] == "sh"
                && argv[1] == "-c"
                && argv[2].Contains(".codeybox-created-at", StringComparison.Ordinal))
            {
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop during metadata scan");
            }
            if (argv.Contains("launch") && LaunchExitCode != 0)
                return new ProcessRunResult(LaunchExitCode, "", "launch failed");
            if (argv.Contains("delete") && DeleteExitCode != 0)
                return new ProcessRunResult(DeleteExitCode, "", "delete failed");
            if (argv.Contains("info"))
            {
                var vm = argv.SkipWhile(a => a != "info").Skip(1).First();
                if (InfoExitCode != 0)
                    return new ProcessRunResult(InfoExitCode, "", InfoStderr);
                return new ProcessRunResult(
                    0,
                    $"{{\"info\":{{\"{vm}\":{{\"state\":\"Running\"}}}}}}",
                    "");
            }
            if (argv.Contains("list"))
            {
                ListStdoutCaps.Enqueue(maxStdoutBytes);
                if (ListExitCode != 0)
                    return new ProcessRunResult(ListExitCode, "", "list failed");
                var stdout = ListStdoutOverride
                    ?? $"{{\"list\":[{string.Join(",", ManagedNames.Select(name => $"{{\"name\":\"{name}\",\"state\":\"Running\"}}"))}]}}";
                if (maxStdoutBytes is { } cap && System.Text.Encoding.UTF8.GetByteCount(stdout) > cap)
                    return new ProcessRunResult(137, stdout[..Math.Min(stdout.Length, cap)], "", StdoutLimitExceeded: true);
                return new ProcessRunResult(0, stdout, "");
            }
            return new ProcessRunResult(0, "", "");
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct) =>
            Task.CompletedTask;

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
        {
            if (ThrowTransportOnStageOut)
                throw new RemoteSshTransportException($"{hostId}: simulated transport drop during stage-out");
            return Task.CompletedTask;
        }

        private static bool IsStagingDirectorySetup(IReadOnlyList<string> argv) =>
            argv.Count >= 3
            && argv[0] == "sh"
            && argv[1] == "-c"
            && argv[2].Contains("mkdir -p", StringComparison.Ordinal)
            && argv[2].Contains("chmod 0700", StringComparison.Ordinal);
    }

    private sealed class AsyncGate(int expectedWaiters)
    {
        private readonly TaskCompletionSource _expectedReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public Task WaitForExpectedAsync() => _expectedReached.Task;

        public void Release() => _release.TrySetResult();

        public async Task WaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _started) >= expectedWaiters)
                _expectedReached.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
