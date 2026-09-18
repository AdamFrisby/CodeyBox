using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// A teardown failure after completed work is an operational event, not the
/// item's outcome: the work-phase sandbox may fail to dispose, but the item
/// must still complete on the strength of its work, the failure must stay
/// logged (surfaced, not swallowed), and the sandbox must remain visible in
/// the managed inventory instead of being silently forgotten.
/// </summary>
[Collection("Pipeline integration")]
public sealed class WorkPhaseTeardownResilienceTests : IDisposable
{
    private readonly string _workspace;
    public WorkPhaseTeardownResilienceTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-teardown-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task WorkPhase_SandboxTeardownFailureAfterSuccess_DoesNotFailItemAndStaysVisible()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var provider = new FirstDisposeFailingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var pipelineLog = new CapturingLogger<PipelineRunner>();
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions { EnableSandboxReuse = false });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            sandboxProvider: provider,
            logger: pipelineLog,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("hello.txt", "hello world\n"));

        var item = NewItem("feature/teardown-resilience");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.DoesNotContain("teardown", final.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disposal", final.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var teardownWarning = Assert.Single(
            pipelineLog.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(
                    "Sandbox disposal failed after successful phase",
                    StringComparison.Ordinal));
        Assert.Contains(
            "simulated work-sandbox teardown failure",
            teardownWarning.Exception?.Message ?? string.Empty,
            StringComparison.Ordinal);

        var remaining = await provider.ListAllManagedAsync(CancellationToken.None);
        var leaked = Assert.Single(remaining);
        Assert.Equal(provider.FirstSandboxId, leaked.Name);
        Assert.True(leaked.IsTrackedActive);
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    /// <summary>
    /// Decorates a real provider with a managed inventory (created sandboxes
    /// minus successfully disposed ones) and fails the first-created
    /// sandbox's disposal with an ordinary, non-benign teardown error. The
    /// work-phase sandbox is always created before any auxiliary sandbox, so
    /// failing the first-created disposal deterministically targets it.
    /// </summary>
    private sealed class FirstDisposeFailingSandboxProvider(ISandboxProvider inner) : ISandboxProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ManagedSandboxInfo> _inventory = new(StringComparer.Ordinal);
        private string? _firstSandboxId;

        public string Name => inner.Name;

        internal string? FirstSandboxId
        {
            get { lock (_gate) return _firstSandboxId; }
        }

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            var sandbox = await inner.CreateAsync(spec, ct).ConfigureAwait(false);
            bool isFirst;
            lock (_gate)
            {
                isFirst = _firstSandboxId is null;
                _firstSandboxId ??= sandbox.Id;
                _inventory[sandbox.Id] = new ManagedSandboxInfo(
                    sandbox.Id,
                    DateTimeOffset.UtcNow,
                    DiskBytes: null,
                    IsTrackedActive: true);
            }
            return new FirstFailingSandbox(this, sandbox, isFirst);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(_inventory.Values.ToArray());
        }

        public Task DisposeLeakedAsync(string name, CancellationToken ct) =>
            inner.DisposeLeakedAsync(name, ct);

        internal void NoteDisposed(string id)
        {
            lock (_gate)
                _inventory.Remove(id);
        }
    }

    private sealed class FirstFailingSandbox(
        FirstDisposeFailingSandboxProvider owner,
        ISandbox inner,
        bool failDispose) : ISandbox, IPreemptibleSandbox, IPreserveOnDisposeSandbox
    {
        private int _disposeAttempts;

        public string Id => inner.Id;

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            inner.ExecAsync(exec, ct);

        public Task StopAndPreserveAsync(CancellationToken ct = default) =>
            ((IPreemptibleSandbox)inner).StopAndPreserveAsync(ct);

        public void DisablePreserveOnDispose() =>
            ((IPreserveOnDisposeSandbox)inner).DisablePreserveOnDispose();

        public async ValueTask DisposeAsync()
        {
            if (failDispose && Interlocked.Increment(ref _disposeAttempts) == 1)
            {
                throw new InvalidOperationException(
                    "simulated work-sandbox teardown failure: forced stop verification failed (exit 1)");
            }

            await inner.DisposeAsync().ConfigureAwait(false);
            owner.NoteDisposed(Id);
        }
    }
}
