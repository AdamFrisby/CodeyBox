using System.Reflection;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// The auto-retry path must pin <see cref="WorkItem.BaselineImageRef"/> BEFORE it
/// calls the router, mirroring the dispatch pickup path, so the in-VM smoke gate
/// probes the image the retried item will actually clone rather than the active
/// baseline. A regression dropping that pre-routing pin would forward a null ref
/// to the gate (gating on the active baseline) while later phases stamp a
/// different ref — reproducing the AC#1 mismatch this change fixes. This test
/// wires a real <see cref="AgentClassRouter"/> with a recording in-VM gate and a
/// stub <see cref="IBaselineImageResolver"/>, retries an item with a null
/// BaselineImageRef, and asserts the gate saw the resolver's pinned ref.
/// </summary>
public sealed class QuotaRetrySchedulerBaselinePinTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("test-project");
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-quota-pin-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    [Fact]
    public async Task Retry_PinsBaselineRef_BeforeRouterGatesInVmSmoke()
    {
        var gate = new RecordingInVmSmokeGate();
        var resolver = new StubBaselineResolver("cb-retry-pin");
        using var fixture = BuildScheduler(BuildRouterWithGate(gate), resolver);

        // Item never ran → null BaselineImageRef, so the retry pin block fills it.
        var item = CreateQuotaItem() with { BaselineImageRef = null };
        await fixture.Store.CreateAsync(item);

        await InvokeTryRetryAsync(fixture.Scheduler, item, "periodic", CancellationToken.None);

        // The router forwards item.BaselineImageRef to the gate per scored member.
        // If the retry pinned before routing the gate saw the resolver's ref; a
        // dropped pre-routing pin would have forwarded null.
        Assert.NotEmpty(gate.SeenBaselineRefs);
        Assert.Contains("cb-retry-pin", gate.SeenBaselineRefs);
        Assert.DoesNotContain(null, gate.SeenBaselineRefs);
    }

    private static AgentClassRouter BuildRouterWithGate(IInVmSmokeGate gate)
    {
        var cls = new AgentClass
        {
            Id = "frontier",
            DisplayName = "Frontier",
            Members =
            [
                new() { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };
        // availablePct below MinQuotaPct → router waits after consulting the gate,
        // so we never reach the git-dependent PerformRetry path; the gate was
        // already asked with the pinned ref by then.
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        return new AgentClassRouter(
            [cls],
            [new FakeProbe(AgentKind.Claude, 0.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromMinutes(5) },
            NullLogger<AgentClassRouter>.Instance,
            timeProvider: null,
            todModifiers: null,
            quotaFailures: null,
            burnEstimator: null,
            runningCounters: null,
            dispatchAvailability: new AgentDispatchAvailability(availability, gate));
    }

    private Fixture BuildScheduler(AgentClassRouter router, IBaselineImageResolver resolver)
    {
        var store = new SqliteWorkItemStore(
            Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N") + ".db"));
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, new InMemoryTaskQueue(), gitHost, NullLogger<WorkItemRetrier>.Instance);
        var scheduler = new QuotaRetryScheduler(
            store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = true,
                    PeriodicCheckInterval = TimeSpan.FromHours(1),
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            new InMemoryProjectRepository(new Project
            {
                Id = TestProjectId,
                DisplayName = "Test",
                RepositoryUrl = "https://example.invalid/repo.git",
                DefaultAgentClass = "frontier",
                NetworkProfiles = new ProjectNetworkProfiles { Work = "work-profile" },
            }),
            queueController: null,
            webhooks: null,
            timeProvider: new InertTimeProvider(DateTimeOffset.UtcNow),
            baselineResolver: resolver);
        return new Fixture(store, scheduler);
    }

    private static WorkItem CreateQuotaItem()
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "frontier",
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

    private static async Task InvokeTryRetryAsync(
        QuotaRetryScheduler scheduler, WorkItem item, string source, CancellationToken ct)
    {
        var retry = typeof(QuotaRetryScheduler).GetMethod(
            "TryRetryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)retry.Invoke(scheduler, [item, source, ct])!;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(SqliteWorkItemStore store, QuotaRetryScheduler scheduler)
        {
            Store = store;
            Scheduler = scheduler;
        }

        public SqliteWorkItemStore Store { get; }
        public QuotaRetryScheduler Scheduler { get; }

        public void Dispose()
        {
            Scheduler.Dispose();
            Store.Dispose();
        }
    }

    private sealed class InertTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public InertTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
