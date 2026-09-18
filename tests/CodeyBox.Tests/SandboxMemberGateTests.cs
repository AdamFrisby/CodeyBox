using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Tests;

/// <summary>
/// Per-member sandbox admission: each <see cref="SandboxMember"/> owns a
/// <see cref="ResizableConcurrencyGate"/> sized from its
/// <see cref="SandboxMember.Capacity"/>, the process-wide ceiling is the
/// derived member sum, permits stay balanced on failures, sub-minimum live
/// resizes are refused, and growing a gate admits queued waiters immediately.
/// </summary>
public sealed class SandboxMemberGateTests
{
    [Fact]
    public async Task Acquire_TwoMembersCapacitiesThreeAndFive_RunsEightConcurrently()
    {
        var provider = new BlockingSandboxProvider();
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "r", preferenceScore: 100, capacity: 3),
                SandboxPlacementTestMembers.Member("b", "r", preferenceScore: 100, capacity: 5)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        // Derived ceiling: the member sum, not an imposed global scalar.
        Assert.Equal(8, acquirer.MaxConcurrent);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => acquirer.AcquireAsync(
                new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
                CancellationToken.None))
            .ToList();

        // Every acquisition must be inside CreateAsync at once: with the old
        // global clamp (or a pile-onto-one-member race) fewer than eight
        // could proceed and this backstop would fire instead of hanging.
        using var enteredCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (provider.Entered < 8)
            await Task.Delay(10, enteredCts.Token);

        Assert.Equal(8, provider.MaxObserved);
        provider.Release();

        var sandboxes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(8, sandboxes.Length);
        Assert.Equal(8, acquirer.InFlight);

        foreach (var sandbox in sandboxes)
            await sandbox.DisposeAsync();
        Assert.Equal(0, acquirer.InFlight);
    }

    [Fact]
    public void SyncMemberCapacities_ResizeBelowTwiceWorkers_IsRefusedAndPriorValueStays()
    {
        var provider = new PlacementFakeSandboxProvider("a");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a", capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]));
        Assert.Equal(8, acquirer.MaxConcurrent);

        var shrunk = SandboxPlacementTestMembers.Snapshot(
            SandboxPlacementTestMembers.Member("a", "a", capacity: 3));

        var ex = Assert.Throws<InvalidOperationException>(
            () => acquirer.SyncMemberCapacities(shrunk.Current, maxConcurrentWorkers: 2));
        Assert.Contains("'a'", ex.Message);
        Assert.Contains("4", ex.Message);

        // Refused: the previous capacity stays in force and the gate still works.
        Assert.Equal(8, acquirer.MaxConcurrent);
    }

    [Fact]
    public void SyncMemberCapacities_ValidResize_LogsOldNewAndInFlight()
    {
        var logger = new CapturingLogger<SandboxPlacementAcquirer>();
        var provider = new PlacementFakeSandboxProvider("a");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a", capacity: 8)),
            new PlacementFakeSandboxProviderRegistry([provider]),
            log: logger);

        var grown = SandboxPlacementTestMembers.Snapshot(
            SandboxPlacementTestMembers.Member("a", "a", capacity: 10));
        acquirer.SyncMemberCapacities(grown.Current, maxConcurrentWorkers: 2);

        Assert.Equal(10, acquirer.MaxConcurrent);
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Equal("a", entry.Properties["MemberId"]);
        Assert.Equal(8, entry.Properties["OldValue"]);
        Assert.Equal(10, entry.Properties["NewValue"]);
        Assert.Equal(0, entry.Properties["InFlight"]);

        // Idempotent: same capacities are a no-op with no further log.
        acquirer.SyncMemberCapacities(grown.Current, maxConcurrentWorkers: 2);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task Acquire_CreateFailure_ReleasesMemberPermit()
    {
        var provider = new FailingSandboxProvider("f");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("f", "f", capacity: 4)),
            new PlacementFakeSandboxProviderRegistry([provider]));
        Assert.Equal(0, acquirer.InFlight);

        await Assert.ThrowsAsync<InvalidOperationException>(() => acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None));

        Assert.Equal(0, acquirer.InFlight);
        Assert.Equal(4, acquirer.MaxConcurrent);
    }

    [Fact]
    public async Task Acquire_GrowResizeAdmitsQueuedWaiterImmediately()
    {
        var provider = new PlacementFakeSandboxProvider("a");
        var acquirer = new SandboxPlacementAcquirer(
            SandboxPlacementTestMembers.Snapshot(
                SandboxPlacementTestMembers.Member("a", "a", capacity: 1)),
            new PlacementFakeSandboxProviderRegistry([provider]));

        var held = await acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);
        Assert.Equal(1, acquirer.InFlight);

        // The member is at cap, so the next acquisition parks on the member
        // gate through a true async wait (everything before the wait is
        // synchronous, so it is already queued once the task is returned).
        var waiter = acquirer.AcquireAsync(
            new SandboxPlacementAcquisition(WorkItemId.New(), "work", [], null, null, SandboxPlacementTestMembers.Spec()),
            CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        var grownSnapshot = SandboxPlacementTestMembers.Snapshot(
            SandboxPlacementTestMembers.Member("a", "a", capacity: 2));
        acquirer.SyncMemberCapacities(grownSnapshot.Current, maxConcurrentWorkers: null);
        Assert.Equal(2, acquirer.MaxConcurrent);

        var second = await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(second);
        Assert.Equal(2, acquirer.InFlight);

        await held.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(0, acquirer.InFlight);
    }

    [Fact]
    public async Task Gate_GrowResizeAdmitsWaitersImmediately()
    {
        using var gate = new ResizableConcurrencyGate(1);
        await gate.WaitAsync(CancellationToken.None);

        var waiter = gate.WaitAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        var result = gate.Resize(2);
        Assert.Equal(1, result.OldTarget);
        Assert.Equal(2, result.NewTarget);
        Assert.Equal(1, result.InFlight);

        await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        gate.Release();
        gate.Release();
    }

    private sealed class BlockingSandboxProvider : ISandboxProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _current;
        private int _entered;
        private int _maxObserved;

        public string Name => "r";

        public IReadOnlyList<string> DeclaredCapabilities => [];

        public int Entered => Volatile.Read(ref _entered);

        public int MaxObserved
        {
            get { lock (_sync) return _maxObserved; }
        }

        public void Release() => _release.TrySetResult();

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            var current = Interlocked.Increment(ref _current);
            Interlocked.Increment(ref _entered);
            lock (_sync) _maxObserved = Math.Max(_maxObserved, current);
            try
            {
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
            return new PlacementFakeSandbox($"r-sandbox-{current}");
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailingSandboxProvider(string name) : ISandboxProvider
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> DeclaredCapabilities => [];

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default) =>
            throw new InvalidOperationException($"provider '{Name}' cannot provision right now");

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
