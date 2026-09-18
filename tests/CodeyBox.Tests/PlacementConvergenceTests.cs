using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Convergence proof for the shared placement decider: the same candidate
/// set and requirements must yield the same selection through
/// <see cref="ExecutorPlacement"/> directly, through the multipass-remote
/// provider's host selection, and through the E2E pool's host selection.
/// Each leg runs through the real public API (no feeder mocks), so a
/// regression that reintroduces a parallel selection path flips red.
/// </summary>
public sealed class PlacementConvergenceTests
{
    [Fact]
    public async Task E2ePool_refuses_lease_when_no_host_allows_required_network_profile()
    {
        var host = new SnapshotSandboxProvider("only", ["other"]);
        var monitor = new StubOptionsMonitor<E2eExecutionOptions>(
            new E2eExecutionOptions { MaxConcurrent = 1, NetworkProfile = "e2e-net" });
        var pool = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("only", host, 1)],
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() => pool.LeaseAsync());

        Assert.Equal("placement", ex.Operation);
        Assert.Equal("no-eligible-host", ex.ErrorClass);
        Assert.Contains("e2e-net", ex.Detail, StringComparison.Ordinal);
        Assert.Equal(0, host.CreateCount);
        Assert.Equal(0, pool.InFlight);
    }

    [Fact]
    public async Task E2ePool_leases_single_host_when_profile_matches()
    {
        var host = new SnapshotSandboxProvider("only", ["e2e-net"]);
        var monitor = new StubOptionsMonitor<E2eExecutionOptions>(
            new E2eExecutionOptions { MaxConcurrent = 1, NetworkProfile = "e2e-net" });
        var pool = new MultiHostE2eExecutionPool(
            [new E2eExecutionHost("only", host, 1)],
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance);

        await using var slot = await pool.LeaseAsync();

        Assert.Equal(1, host.CreateCount);
        Assert.Equal("e2e-net", Assert.Single(host.Specs).Network.ProfileName);
        Assert.Equal(1, pool.InFlight);
    }

    [Fact]
    public async Task AllPlacementPaths_select_same_host_for_profile_requirements()
    {
        // Logical candidates shared by every leg: "b" is the only host
        // accepting "audit" (declared with different casing to pin the
        // case-insensitive seam every path projects through).
        var requirements = ExecutorPlacementRequirements.FromValues(
            null,
            ExecutorEligibility.NormalizeRequiredNetworkProfile("audit"),
            []);

        var members = new[]
        {
            Member("a", 2, ["work"]),
            Member("b", 2, ["Audit"]),
            Member("c", 2, ["other"]),
        };
        Assert.Equal("b", ExecutorPlacement.Decide(members, requirements, loads: null, runtimeUnhealthy: null).SelectedHostId);

        var transports = new ConvergenceTransportSet();
        var provider = MultipassProvider(
            transports,
            MultipassHost("a", 2, ["work"]),
            MultipassHost("b", 2, ["Audit"]),
            MultipassHost("c", 2, ["other"]));
        await using (var sandbox = await provider.CreateAsync(MultipassSpec("audit")))
        {
            Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        }
        Assert.Equal(0, transports.LaunchCount("a"));
        Assert.Equal(1, transports.LaunchCount("b"));
        Assert.Equal(0, transports.LaunchCount("c"));

        var (e2eA, e2eB, e2eC) = (
            new SnapshotSandboxProvider("a", ["work"]),
            new SnapshotSandboxProvider("b", ["Audit"]),
            new SnapshotSandboxProvider("c", ["other"]));
        var pool = E2ePool("audit", ("a", e2eA), ("b", e2eB), ("c", e2eC));
        await using (await pool.LeaseAsync())
        {
            Assert.Equal(0, e2eA.CreateCount);
            Assert.Equal(1, e2eB.CreateCount);
            Assert.Equal(0, e2eC.CreateCount);
        }
    }

    [Fact]
    public async Task AllPlacementPaths_select_same_host_for_load_ordering()
    {
        // Both hosts eligible; "a" carries load, so least-loaded "b" wins on
        // every path.
        var requirements = ExecutorPlacementRequirements.FromValues(null, null, []);
        var members = new[]
        {
            Member("a", 2, []),
            Member("b", 2, []),
        };
        var loads = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 };
        Assert.Equal(
            "b",
            ExecutorPlacement.Decide(members, requirements, loads, runtimeUnhealthy: null).SelectedHostId);

        var transports = new ConvergenceTransportSet();
        transports.SetManagedNames("a", ["codeybox-r-preexisting"]);
        var provider = MultipassProvider(
            transports,
            MultipassHost("a", 2),
            MultipassHost("b", 2));
        await using (var sandbox = await provider.CreateAsync(MultipassSpec()))
        {
            Assert.Equal("b", ((MultipassRemoteSandbox)sandbox).HostId);
        }

        var (e2eA, e2eB) = (new SnapshotSandboxProvider("a", []), new SnapshotSandboxProvider("b", []));
        var pool = E2ePool(null, ("a", e2eA), ("b", e2eB));
        await using (var first = await pool.LeaseAsync())
        {
            Assert.Equal(1, e2eA.CreateCount);
            await using (var second = await pool.LeaseAsync())
            {
                Assert.Equal(1, e2eA.CreateCount);
                Assert.Equal(1, e2eB.CreateCount);
            }
        }
    }

    private static SandboxPlacementMember Member(string id, int cap, IReadOnlyList<string> profiles) =>
        new()
        {
            MemberId = id,
            MaxConcurrentSandboxes = cap,
            NetworkProfiles = ExecutorEligibility.NormalizeNetworkProfilesForComparison(profiles),
        };

    private static MultiHostE2eExecutionPool E2ePool(
        string? networkProfile,
        params (string Name, SnapshotSandboxProvider Provider)[] hosts)
    {
        var monitor = new StubOptionsMonitor<E2eExecutionOptions>(
            new E2eExecutionOptions { MaxConcurrent = 4, NetworkProfile = networkProfile });
        return new MultiHostE2eExecutionPool(
            hosts.Select(static h => new E2eExecutionHost(h.Name, h.Provider, 2)).ToArray(),
            monitor,
            NullLogger<MultiHostE2eExecutionPool>.Instance);
    }

    private static MultipassRemoteSandboxProvider MultipassProvider(
        ConvergenceTransportSet transports,
        params MultipassRemoteExecutorHostOptions[] hosts) =>
        new(
            () => new MultipassRemoteSandboxOptions
            {
                SshTarget = "unused-default",
                RemoteStagingRoot = "/remote/staging",
                PlacementRecheckIn = TimeSpan.FromMilliseconds(10),
                RuntimeUnhealthyBackoff = TimeSpan.FromMinutes(10),
                NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["work"] = "cb-work",
                    ["audit"] = "cb-audit",
                },
                ExecutorHosts = hosts,
            },
            host => transports[host.HostId],
            NullLogger<MultipassRemoteSandboxProvider>.Instance);

    private static MultipassRemoteExecutorHostOptions MultipassHost(
        string id,
        int cap,
        IReadOnlyList<string>? allowedProfiles = null) =>
        new()
        {
            Id = id,
            SshTarget = $"{id}.example",
            MaxConcurrentSandboxes = cap,
            AllowedNetworkProfiles = allowedProfiles,
        };

    private static SandboxSpec MultipassSpec(string? networkProfile = null) => new()
    {
        ImageReference = "24.04",
        WorkingDirectory = "/work",
        Network = new SandboxNetworkPolicy { ProfileName = networkProfile },
    };

    private sealed class SnapshotSandboxProvider(string hostId, IReadOnlyList<string> profiles)
        : ISandboxProvider, ISandboxHostPoolSnapshot
    {
        private readonly List<SandboxSpec> _specs = new();
        private int _createCount;

        public string Name => $"fake-{hostId}";

        public int CreateCount => Volatile.Read(ref _createCount);

        public IReadOnlyList<SandboxSpec> Specs
        {
            get { lock (_specs) return _specs.ToList(); }
        }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            lock (_specs) _specs.Add(spec);
            var count = Interlocked.Increment(ref _createCount);
            return Task.FromResult<ISandbox>(new PlacementFakeSandbox($"{hostId}-sandbox-{count}"));
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<SandboxHostPoolEntry> SnapshotHostPool() =>
        [
            new SandboxHostPoolEntry(
                hostId,
                Capacity: 2,
                Reserved: 0,
                Cordoned: false,
                ConfiguredHealthy: true,
                RuntimeHealthy: true,
                RuntimeUnhealthyReason: null,
                RuntimeUnhealthyUntil: null,
                AllowedNetworkProfiles: profiles),
        ];
    }

    private sealed class ConvergenceTransportSet
    {
        private readonly Dictionary<string, ConvergenceTransport> _transports = new(StringComparer.Ordinal);

        public ConvergenceTransport this[string hostId]
        {
            get
            {
                lock (_transports)
                {
                    if (!_transports.TryGetValue(hostId, out var transport))
                    {
                        transport = new ConvergenceTransport();
                        _transports[hostId] = transport;
                    }

                    return transport;
                }
            }
        }

        public void SetManagedNames(string hostId, IReadOnlyList<string> names) =>
            this[hostId].ManagedNames.AddRange(names);

        public int LaunchCount(string hostId)
        {
            lock (_transports)
                return _transports.TryGetValue(hostId, out var transport) ? transport.LaunchCount : 0;
        }
    }

    private sealed class ConvergenceTransport : IRemoteHostTransport
    {
        private int _launchCount;

        public List<string> ManagedNames { get; } = new();

        public int LaunchCount => Volatile.Read(ref _launchCount);

        public string DiagnosticId => "fake-convergence";

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            bool killOnOutputLimit = true)
        {
            ArgumentNullException.ThrowIfNull(argv);
            if (argv.Contains("launch"))
                Interlocked.Increment(ref _launchCount);
            if (argv.Contains("list"))
            {
                var stdout = $"{{\"list\":[{string.Join(",", ManagedNames.Select(static name => $"{{\"name\":\"{name}\",\"state\":\"Running\"}}"))}]}}";
                return Task.FromResult(new ProcessRunResult(0, stdout, ""));
            }

            if (argv.Contains("info"))
            {
                var vm = argv.SkipWhile(static a => a != "info").Skip(1).First();
                return Task.FromResult(new ProcessRunResult(0, $"{{\"info\":{{\"{vm}\":{{\"state\":\"Running\"}}}}}}", ""));
            }

            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct) => Task.CompletedTask;

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
        where T : class
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
