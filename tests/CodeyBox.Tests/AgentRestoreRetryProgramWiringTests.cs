using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

public sealed class AgentRestoreRetryProgramWiringTests
{
    [Fact]
    public async Task ProgramHostedRestoreScheduler_RequeuesFromRegisteredRestoreSignal()
    {
        using var factory = new RestoreRetryWiringFactory();

        var scheduler = factory.Services.GetRequiredService<AgentRestoreRetryScheduler>();
        Assert.Same(
            scheduler,
            factory.Services.GetServices<IHostedService>().OfType<AgentRestoreRetryScheduler>().Single());
        Assert.Same(
            factory.Services.GetRequiredService<AgentAvailabilityRegistry>(),
            factory.Services.GetRequiredService<IAgentRestoreSignal>());
        Assert.Same(
            factory.Services.GetRequiredService<AgentAvailabilityRegistry>(),
            factory.Services.GetRequiredService<IAgentRestorePublisher>());

        var store = factory.Services.GetRequiredService<IWorkItemStore>();
        var queue = factory.Services.GetRequiredService<RecordingTaskQueue>();
        var registry = factory.Services.GetRequiredService<AgentAvailabilityRegistry>();

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "missing binary", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("restore-program-test"),
            Title = "restore wiring",
            Prompt = "retry after restore",
            Agent = AgentKind.Claude,
            State = WorkItemState.Failed,
            FailureKind = WorkItemFailureKinds.AgentUnavailable,
            LastError = "agent binary missing",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(item);
        queue.CaptureWithoutForwarding(item.Id);

        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10), SmokeFailureCategory.None));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(item.Id, await queue.WaitForCapturedEnqueueAsync(timeout.Token));

        var requeued = await store.GetAsync(item.Id, timeout.Token);
        Assert.Equal(WorkItemState.Queued, requeued!.State);
    }

    [Fact]
    public async Task ProgramHostedRestoreScheduler_RequeuesGenericInfrastructureFailureFromInvolvement()
    {
        using var factory = new RestoreRetryWiringFactory();

        var store = factory.Services.GetRequiredService<IWorkItemStore>();
        var involvement = factory.Services.GetRequiredService<IAgentInvolvementStore>();
        var queue = factory.Services.GetRequiredService<RecordingTaskQueue>();
        var registry = factory.Services.GetRequiredService<AgentAvailabilityRegistry>();

        registry.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(false, "codex binary missing", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        var failedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("restore-program-test"),
            Title = "restore infrastructure wiring",
            Prompt = "retry after restore",
            Agent = AgentKind.Claude,
            State = WorkItemState.Failed,
            FailureKind = WorkItemFailureKinds.Infrastructure,
            LastError = "planning agent codex reported failure: binary missing",
            UpdatedAt = failedAt,
        };
        await store.CreateAsync(item);

        var involvementId = Guid.NewGuid();
        await involvement.RecordStartAsync(new AgentInvolvement(
            Id: involvementId,
            WorkItemId: item.Id,
            AgentKind: AgentKind.Codex,
            AgentInstanceId: null,
            ModelId: null,
            Phase: "planning",
            StartedAt: failedAt.AddSeconds(-5),
            EndedAt: null,
            Iteration: null,
            Outcome: null));
        await involvement.FinalizeAsync(
            involvementId,
            failedAt,
            AgentInvolvementOutcomes.FailureInfrastructure);
        queue.CaptureWithoutForwarding(item.Id);

        registry.MarkSmokeResult(
            AgentKind.Codex,
            new AgentSmokeResult(true, null, TimeSpan.FromMilliseconds(10), SmokeFailureCategory.None));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(item.Id, await queue.WaitForCapturedEnqueueAsync(timeout.Token));

        var requeued = await store.GetAsync(item.Id, timeout.Token);
        Assert.Equal(WorkItemState.Queued, requeued!.State);
        Assert.Equal(AgentKind.Claude, requeued.Agent);
    }

    private sealed class RestoreRetryWiringFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-restore-retry-wiring-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:Smoke:Enabled"] = "false",
                    ["CodeyBox:Smoke:InVm:Enabled"] = "false",
                    ["CodeyBox:AutoRequeueOnAgentRestore:Enabled"] = "true",
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITaskQueue>();
                services.AddSingleton<RecordingTaskQueue>();
                services.AddSingleton<ITaskQueue>(sp => sp.GetRequiredService<RecordingTaskQueue>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { File.Delete(_dbPath); } catch { }
                try { File.Delete(_dbPath + "-wal"); } catch { }
                try { File.Delete(_dbPath + "-shm"); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingTaskQueue : ITaskQueue
    {
        private readonly InMemoryTaskQueue _inner = new();
        private readonly TaskCompletionSource<WorkItemId> _capturedEnqueue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WorkItemId? _capturedItemId;

        public void CaptureWithoutForwarding(WorkItemId id) => _capturedItemId = id;

        public int Count => _inner.Count;

        public async Task<WorkItemId> WaitForCapturedEnqueueAsync(CancellationToken ct)
            => await _capturedEnqueue.Task.WaitAsync(ct);

        public async ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        {
            if (_capturedItemId == id)
            {
                _capturedEnqueue.TrySetResult(id);
                return;
            }

            await _inner.EnqueueAsync(id, ct);
        }

        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default) =>
            _inner.EnqueueDispatchWakeAsync(ct);

        public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default) =>
            _inner.DequeueAsync(ct);

        public ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default) =>
            _inner.DequeueDispatchSignalAsync(ct);
    }
}
