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
}
