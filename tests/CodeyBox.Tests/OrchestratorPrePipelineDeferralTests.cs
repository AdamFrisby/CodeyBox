using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

/// <summary>
/// A provisioning or disk deferral raised before the pipeline runs (agent-class
/// routing's smoke gate warms the baseline via EnsureBaselineImageAsync, which
/// throws when the bake failed or the disk guard refuses) must park the item
/// until RecheckSeconds, exactly like a pipeline-time deferral. Previously the
/// outer pickup try had no deferral catch, so the exception escaped the worker,
/// the slot-release wake re-picked the still-Queued item immediately, and the
/// dispatcher re-evaluated (and re-logged routing for) the item in a tight loop.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class OrchestratorPrePipelineDeferralTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-prepipelinedefer-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly TestSink _sink = new();

    public OrchestratorPrePipelineDeferralTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
    }

    [Fact]
    public async Task ProvisioningDeferredDuringRouting_ParksUntilRecheckInterval()
    {
        await RunPrePipelineDeferralParkTestAsync(
            new SandboxProvisioningDeferredException(
                provider: "incus",
                operation: "baseline-cleanup",
                errorClass: "incus-baseline-delete-unconfirmed",
                detail: "candidate delete unconfirmed; re-checking",
                recheckIn: TimeSpan.FromSeconds(300)),
            expectedAuditEvent: "sandbox.provisioning_deferred");
    }

    [Fact]
    public async Task DiskDeferredDuringRouting_ParksUntilRecheckInterval()
    {
        await RunPrePipelineDeferralParkTestAsync(
            new SandboxDiskDeferredException(
                mountPath: "/var/lib/incus",
                freeBytes: 1,
                thresholdBytes: 2,
                recheckIn: TimeSpan.FromSeconds(300)),
            expectedAuditEvent: "disk.deferred");
    }

    private async Task RunPrePipelineDeferralParkTestAsync(
        Exception deferral,
        string expectedAuditEvent)
    {
        var fakeTime = new ControllableTimeProvider();
        fakeTime.SetUtcNow(DateTimeOffset.UtcNow);
        var recheckIn = TimeSpan.FromSeconds(300);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
            AgentClassId = "frontier",
        };
        await _store.CreateAsync(item);

        var availability = new ThrowingDispatchAvailability(deferral);
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [new AgentMembership
                {
                    Agent = AgentKind.Devin,
                    Billing = AgentBilling.Subscription,
                    QualityScore = 100,
                }],
            }],
            [new FullQuotaProbe(AgentKind.Devin)],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = TimeSpan.FromMinutes(5),
            },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: fakeTime,
            dispatchAvailability: availability);

        var queue = new InMemoryTaskQueue();
        var pipeline = new NeverRunPipeline();
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: new InMemoryProjectRepository(new Project
            {
                Id = item.ProjectId,
                DisplayName = "Test",
                RepositoryUrl = "https://git.example.com/repo",
                DefaultAgentClass = "frontier",
                Budget = new ProjectBudget { MaxConcurrentForProject = 1 },
            }),
            timeProvider: fakeTime);

        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        try
        {
            // Backstop-only deadline for a deterministic-but-starved event.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (availability.CallCount < 1 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(1, availability.CallCount);

            // The bug re-picked the item on every slot-release wake, so the
            // routing evaluation (and its Information lines) repeated at
            // spawn-loop speed. Parked, the count must not move on real time.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.Equal(1, availability.CallCount);
            Assert.True(svc.IsDeferredForTest(item.Id), "item should be parked in _deferredItems");
            Assert.Equal(0, pipeline.CallCount);

            // Fake-time advances short of the recheck interval must not requeue.
            fakeTime.Advance(TimeSpan.FromSeconds(299));
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Equal(1, availability.CallCount);

            // Crossing the recheck interval requeues exactly once; the repeat
            // evaluation defers again (the condition still holds), so the
            // pipeline still never runs.
            fakeTime.Advance(TimeSpan.FromSeconds(2));
            while (availability.CallCount < 2 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(2, availability.CallCount);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Equal(2, availability.CallCount);
            Assert.Equal(0, pipeline.CallCount);

            var stored = await _store.GetAsync(item.Id);
            Assert.NotNull(stored);
            Assert.Equal(WorkItemState.Queued, stored!.State);

            Assert.Contains(_sink.Events, e =>
                e.Properties.TryGetValue("EventName", out var name)
                && name.ToString() == $"\"{expectedAuditEvent}\"");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ThrowingDispatchAvailability(Exception exception) : IAgentDispatchAvailability
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AgentAvailability?> EnsureAvailableAsync(
            AgentKind kind, InVmSmokeSandboxTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromException<AgentAvailability?>(exception);
        }

        public AgentAvailability? GetAvailability(AgentKind kind) => null;
    }

    private sealed class FullQuotaProbe(AgentKind kind) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(new AgentQuotaSnapshot { AvailablePct = 100.0 });
    }

    private sealed class NeverRunPipeline : IPipelineRunner
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.CompletedTask;
        }
    }
}
