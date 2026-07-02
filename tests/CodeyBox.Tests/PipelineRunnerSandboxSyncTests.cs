using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;

namespace CodeyBox.Tests;

[Collection("Pipeline integration")]
public sealed class PipelineRunnerSandboxSyncTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerSandboxSyncTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-pipeline-sync-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task WorkPhase_RethrowsPostPushSyncDeferral_AndDoesNotMarkWorkComplete()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var deferAt = new SandboxProvisioningDeferredException(
            provider: "multipass-remote",
            operation: "stage-out",
            errorClass: "remote-sync-failed",
            detail: "rsync failed",
            recheckIn: TimeSpan.FromMinutes(1),
            retainedSandboxName: "codeybox-remote",
            retainedSandboxHostId: "host-a");
        var sandboxProvider = new SyncFailingSandboxProvider(deferAt);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: sandboxProvider,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("sync-marker.txt", "work completed\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "sync failure",
            Prompt = "write the marker",
            State = WorkItemState.Queued,
            WorkBranch = "feature/sync-deferral",
        };
        await tp.Store.CreateAsync(item);

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => tp.Pipeline.RunAsync(item, CancellationToken.None));

        Assert.Same(deferAt, thrown);
        Assert.Equal(1, sandboxProvider.SyncCalls);

        var persisted = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Working, persisted!.State);
        Assert.NotEqual(WorkItemState.WorkComplete, persisted.State);
        Assert.NotEqual(WorkItemState.Failed, persisted.State);
    }

    [Fact]
    public async Task WorkPhase_DisposeFailureAfterSuccessfulPhase_FailsItemInsteadOfSwallowing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var sandboxProvider = new DisposeFailingSandboxProvider();
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EnableSandboxReuse = false
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: sandboxProvider,
            pipelineTuning: tuning,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("dispose-marker.txt", "work completed\n"));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "dispose failure",
            Prompt = "write the marker",
            State = WorkItemState.Queued,
            WorkBranch = "feature/dispose-failure",
        };
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(1, sandboxProvider.DisposeCalls);
        var persisted = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Failed, persisted!.State);
        Assert.Contains("dispose failed after successful phase", persisted.LastError);
    }

    [Fact]
    public void PreemptCheckpointPushes_AreFollowedByRemoteStateSyncBeforeHostSideRecovery()
    {
        var source = ReadPipelineRunnerSource();

        AssertSyncCallFollows(
            source,
            "git.push_resumed_checkpoint_to_bare_repo",
            "await sandbox.SyncStateToHostAsync(ct);",
            maxDistance: 512);
        AssertSyncCallFollows(
            source,
            "\"push\", \"origin\", $\"HEAD:{checkpointRef}\"",
            "await sandbox.SyncStateToHostAsync(ct);",
            maxDistance: 512);
    }

    private static void AssertSyncCallFollows(
        string source,
        string marker,
        string syncCall,
        int maxDistance)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"PipelineRunner.cs no longer contains marker: {marker}");

        var syncIndex = source.IndexOf(syncCall, markerIndex, StringComparison.Ordinal);
        Assert.True(syncIndex >= 0, $"PipelineRunner.cs does not sync host state after marker: {marker}");
        Assert.True(
            syncIndex - markerIndex <= maxDistance,
            $"PipelineRunner.cs sync call drifted too far from marker '{marker}' to pin the push/import ordering");
    }

    private static string ReadPipelineRunnerSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "CodeyBox.Orchestrator",
                "PipelineRunner.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Could not locate src/CodeyBox.Orchestrator/PipelineRunner.cs");
    }

    private sealed class SyncFailingSandboxProvider : ISandboxProvider
    {
        private readonly SandboxProvisioningDeferredException _exception;
        private readonly ProcessSandboxProvider _inner = new(NullLogger<ProcessSandboxProvider>.Instance);
        private int _syncCalls;

        public SyncFailingSandboxProvider(SandboxProvisioningDeferredException exception)
            => _exception = exception;

        public string Name => _inner.Name;
        public int SyncCalls => _syncCalls;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new SyncFailingSandbox(await _inner.CreateAsync(spec, ct), _exception, () => Interlocked.Increment(ref _syncCalls));

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class SyncFailingSandbox : ISandbox
    {
        private readonly ISandbox _inner;
        private readonly SandboxProvisioningDeferredException _exception;
        private readonly Action _recordSync;

        public SyncFailingSandbox(ISandbox inner, SandboxProvisioningDeferredException exception, Action recordSync)
        {
            _inner = inner;
            _exception = exception;
            _recordSync = recordSync;
        }

        public string Id => _inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => _inner.ExecAsync(exec, ct);

        public Task SyncStateToHostAsync(CancellationToken ct = default)
        {
            _recordSync();
            throw _exception;
        }

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => _inner.KillActiveExecsAsync(ct);

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => _inner.GetScreenshotAsync(ct);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => _inner.SynthesizeInputAsync(events, ct);

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
            => _inner.GetAccessibilityAtPointAsync(x, y, ct);

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
            => _inner.GetAccessibilityTreeJsonAsync(ct);

        public ValueTask DisposeAsync()
            => _inner.DisposeAsync();
    }

    private sealed class DisposeFailingSandboxProvider : ISandboxProvider
    {
        private readonly ProcessSandboxProvider _inner = new(NullLogger<ProcessSandboxProvider>.Instance);
        private int _disposeCalls;

        public string Name => _inner.Name;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new DisposeFailingSandbox(
                await _inner.CreateAsync(spec, ct),
                () => Interlocked.Increment(ref _disposeCalls));

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class DisposeFailingSandbox(ISandbox inner, Action recordDispose) : ISandbox
    {
        public string Id => inner.Id;
        public SandboxAgentOutputTransportKind AgentOutputTransportKind => inner.AgentOutputTransportKind;
        public SandboxBatchLaunchMode BatchLaunchMode => inner.BatchLaunchMode;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => inner.ExecAsync(exec, ct);

        public Task SyncStateToHostAsync(CancellationToken ct = default)
            => inner.SyncStateToHostAsync(ct);

        public Task KillActiveExecsAsync(CancellationToken ct = default)
            => inner.KillActiveExecsAsync(ct);

        public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
            => inner.GetScreenshotAsync(ct);

        public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
            => inner.SynthesizeInputAsync(events, ct);

        public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(int x, int y, CancellationToken ct = default)
            => inner.GetAccessibilityAtPointAsync(x, y, ct);

        public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
            => inner.GetAccessibilityTreeJsonAsync(ct);

        public async ValueTask DisposeAsync()
        {
            recordDispose();
            await inner.DisposeAsync();
            throw new InvalidOperationException("dispose failed after successful phase");
        }
    }
}
