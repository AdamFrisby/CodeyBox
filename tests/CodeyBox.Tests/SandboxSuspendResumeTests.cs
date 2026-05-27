using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the R8-core sandbox suspend/resume cycle: shutdown freezes every
/// active sandbox via <see cref="ISuspendableSandbox.SuspendAsync"/> and writes
/// <see cref="WorkItem.SuspendedVmName"/>; startup calls
/// <see cref="ISuspendingSandboxProvider.ResumeSandboxAsync"/> and clears the
/// bookkeeping. The leak reaper skips VMs named in the suspended set.
/// </summary>
public sealed class SandboxSuspendResumeTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-suspend-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SandboxSuspendResumeTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state = WorkItemState.Working) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    // ── Schema round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task SuspendedVmName_AndSuspendedAt_RoundTripThroughStore()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        var suspendedAt = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(item with
        {
            SuspendedVmName = "codeybox-abc123",
            SuspendedAt = suspendedAt,
        });

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal("codeybox-abc123", after.SuspendedVmName);
        Assert.NotNull(after.SuspendedAt);
        // SQLite text round-trip preserves DateTimeOffset to whole microseconds.
        Assert.Equal(suspendedAt.ToUnixTimeMilliseconds(), after.SuspendedAt.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task LegacyRow_WithoutSuspendedColumns_ReadsAsNull()
    {
        // Default WorkItem has SuspendedVmName=null and SuspendedAt=null.
        var item = MakeItem(WorkItemState.WorkComplete);
        await _store.CreateAsync(item);
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    // ── Suspend-on-shutdown handler ──────────────────────────────────────────

    [Fact]
    public async Task ShutdownHandler_SuspendsEveryActiveSandbox_AndPersistsVmName()
    {
        var item1 = MakeItem();
        var item2 = MakeItem();
        await _store.CreateAsync(item1);
        await _store.CreateAsync(item2);

        var sandbox1 = new FakeSuspendableSandbox("vm-1");
        var sandbox2 = new FakeSuspendableSandbox("vm-2");
        var provider = new FakeSuspendingProvider();
        provider.Register(item1.Id, sandbox1);
        provider.Register(item2.Id, sandbox2);

        var lifetime = new FakeLifetime();
        var svc = new SandboxSuspendOnShutdownService(
            provider, _store, lifetime,
            NullLogger<SandboxSuspendOnShutdownService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await svc.SuspendAllAsync();

        Assert.True(sandbox1.SuspendCalled);
        Assert.True(sandbox2.SuspendCalled);

        var after1 = await _store.GetAsync(item1.Id);
        var after2 = await _store.GetAsync(item2.Id);
        Assert.Equal("vm-1", after1!.SuspendedVmName);
        Assert.Equal("vm-2", after2!.SuspendedVmName);
        Assert.NotNull(after1.SuspendedAt);
        Assert.NotNull(after2.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_SuspendFailure_DoesNotPersistVmName()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);
        var sandbox = new FakeSuspendableSandbox("vm-bad", shouldThrow: true);
        var provider = new FakeSuspendingProvider();
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store, new FakeLifetime(),
            NullLogger<SandboxSuspendOnShutdownService>.Instance);
        await svc.SuspendAllAsync();

        var after = await _store.GetAsync(item.Id);
        // No persisted bookkeeping when suspend failed — the item flows through
        // the standard stranded-item recovery path on the next start.
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    [Fact]
    public async Task ShutdownHandler_NonSuspendingProvider_IsNoOp()
    {
        var item = MakeItem();
        await _store.CreateAsync(item);

        // A provider that doesn't implement ISuspendingSandboxProvider — e.g.
        // process or bubblewrap. The handler must silently skip and leave the
        // suspend fields untouched so the existing PreemptCheckpoint flow
        // continues to be the recovery mechanism.
        var nonSuspending = new NonSuspendingProvider();
        var svc = new SandboxSuspendOnShutdownService(
            nonSuspending, _store, new FakeLifetime(),
            NullLogger<SandboxSuspendOnShutdownService>.Instance);
        await svc.SuspendAllAsync();

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    // ── Startup resume handler ───────────────────────────────────────────────

    [Fact]
    public async Task StartupResume_StartsEveryPersistedVm_AndClearsBookkeeping()
    {
        var item1 = MakeItem();
        var item2 = MakeItem();
        await _store.CreateAsync(item1 with { SuspendedVmName = "vm-1", SuspendedAt = DateTimeOffset.UtcNow });
        await _store.CreateAsync(item2 with { SuspendedVmName = "vm-2", SuspendedAt = DateTimeOffset.UtcNow });

        var provider = new FakeSuspendingProvider();
        var orchestrator = MakeOrchestratorWithProvider(provider);

        await orchestrator.ResumeSuspendedSandboxesForTestAsync(CancellationToken.None);

        Assert.Contains("vm-1", provider.ResumedNames);
        Assert.Contains("vm-2", provider.ResumedNames);

        var after1 = await _store.GetAsync(item1.Id);
        var after2 = await _store.GetAsync(item2.Id);
        Assert.Null(after1!.SuspendedVmName);
        Assert.Null(after1.SuspendedAt);
        Assert.Null(after2!.SuspendedVmName);
        Assert.Null(after2.SuspendedAt);
    }

    [Fact]
    public async Task StartupResume_ResumeFailure_StillClearsBookkeeping()
    {
        // If multipassd is unavailable or the VM was operator-deleted, we
        // can't bring it back. The item flows through the standard stranded-
        // item recovery path; the bookkeeping must be cleared so the orphaned
        // VM (if any) can be reaped on the leak reaper's normal schedule.
        var item = MakeItem();
        await _store.CreateAsync(item with { SuspendedVmName = "vm-gone", SuspendedAt = DateTimeOffset.UtcNow });

        var provider = new FakeSuspendingProvider { ResumeThrows = true };
        var orchestrator = MakeOrchestratorWithProvider(provider);

        await orchestrator.ResumeSuspendedSandboxesForTestAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
        Assert.Null(after.SuspendedAt);
    }

    [Fact]
    public async Task StartupResume_NoSuspendedItems_DoesNothing()
    {
        var item = MakeItem();
        await _store.CreateAsync(item); // No SuspendedVmName.

        var provider = new FakeSuspendingProvider();
        var orchestrator = MakeOrchestratorWithProvider(provider);

        await orchestrator.ResumeSuspendedSandboxesForTestAsync(CancellationToken.None);

        Assert.Empty(provider.ResumedNames);
    }

    // ── Leak reaper integration ──────────────────────────────────────────────

    [Fact]
    public async Task LeakReaper_SkipsVmsNamedInSuspendedVmName()
    {
        // Two VMs reported by the provider: one is suspended (mapped to a
        // mid-flight work item), one is a legitimate untracked leak. The
        // reaper must skip the suspended one and dispose the other.
        var suspendedItem = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(suspendedItem with
        {
            SuspendedVmName = "vm-suspended",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new FakeSandboxLeakProvider();
        var oldEnough = DateTimeOffset.UtcNow.AddHours(-1);
        provider.SeedManaged(new("vm-suspended", oldEnough, 1024L * 1024, IsTrackedActive: false));
        provider.SeedManaged(new("vm-leaked", oldEnough, 1024L * 1024, IsTrackedActive: false));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions { LeakAgeThreshold = TimeSpan.FromMinutes(30), AutoDispose = true },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("vm-leaked", provider.DisposedNames);
        Assert.DoesNotContain("vm-suspended", provider.DisposedNames);
    }

    [Fact]
    public async Task LeakReaper_SuspendedVmName_OnTerminalItem_IsNotProtected()
    {
        // Operator cancelled the item between suspend and the next sweep:
        // SuspendedVmName is set but state is Cancelled. The reaper must
        // treat the orphaned VM as a leak so disk space is reclaimed.
        var item = MakeItem(WorkItemState.Cancelled);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-stale-suspended",
            SuspendedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });

        var provider = new FakeSandboxLeakProvider();
        provider.SeedManaged(new("vm-stale-suspended",
            DateTimeOffset.UtcNow.AddHours(-1), 1024L * 1024, IsTrackedActive: false));

        var webhooks = new CapturingWebhookDispatcher();
        var reaper = new SandboxLeakReaper(
            provider, webhooks,
            () => new SandboxLeakOptions { LeakAgeThreshold = TimeSpan.FromMinutes(30), AutoDispose = true },
            NullLogger<SandboxLeakReaper>.Instance,
            _store);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Contains("vm-stale-suspended", provider.DisposedNames);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private OrchestratorService MakeOrchestratorWithProvider(ISandboxProvider provider)
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new ImmediateDonePipeline(_store);
        var cancellations = new CancellationRegistry(CancellationToken.None);
        return new OrchestratorService(
            queue, _store, pipeline, cancellations,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            sandboxProvider: provider);
    }

    private sealed class FakeSuspendableSandbox : ISuspendableSandbox
    {
        public bool SuspendCalled { get; private set; }
        private readonly bool _shouldThrow;
        public FakeSuspendableSandbox(string id, bool shouldThrow = false)
        {
            Id = id;
            _shouldThrow = shouldThrow;
        }
        public string Id { get; }
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SuspendAsync(CancellationToken ct = default)
        {
            SuspendCalled = true;
            if (_shouldThrow) throw new InvalidOperationException("simulated suspend failure");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSuspendingProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly ConcurrentDictionary<WorkItemId, ISuspendableSandbox> _active = new();
        public List<string> ResumedNames { get; } = new();
        public bool ResumeThrows { get; set; }

        public void Register(WorkItemId id, ISuspendableSandbox sandbox) => _active[id] = sandbox;

        public string Name => "fake-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive()
        {
            var list = new List<(WorkItemId, ISuspendableSandbox)>();
            foreach (var kv in _active) list.Add((kv.Key, kv.Value));
            return list;
        }

        public Task ResumeSandboxAsync(string name, CancellationToken ct)
        {
            ResumedNames.Add(name);
            if (ResumeThrows) throw new InvalidOperationException("simulated multipass failure");
            return Task.CompletedTask;
        }
    }

    private sealed class NonSuspendingProvider : ISandboxProvider
    {
        public string Name => "fake-non-suspending";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSandboxLeakProvider : ISandboxProvider
    {
        private readonly List<ManagedSandboxInfo> _managed = new();
        public List<string> DisposedNames { get; } = new();

        public void SeedManaged(ManagedSandboxInfo info) => _managed.Add(info);

        public string Name => "fake-leak-provider";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(_managed);
        public Task DisposeLeakedAsync(string name, CancellationToken ct)
        {
            DisposedNames.Add(name);
            _managed.RemoveAll(m => m.Name == name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
