using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class QuotaRetrySchedulerAuditTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-quota-audit-").FullName;
    private readonly TestSink _sink = new();

    public QuotaRetrySchedulerAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task PeriodicSweep_AuditLogsEveryWalkedWaitingForQuotaResetItem()
    {
        var dbPath = Path.Combine(_workspace, "state.db");
        using var store = new SqliteWorkItemStore(dbPath);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos") },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(store, new InMemoryTaskQueue(), gitHost, NullLogger<WorkItemRetrier>.Instance);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = "https://example.invalid/repo.git",
            DefaultAgentClass = "frontier",
        });
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "frontier",
                    DisplayName = "Frontier",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Claude,
                            Billing = AgentBilling.Subscription,
                            QualityScore = 100,
                        },
                    ],
                },
            ],
            [new StaticQuotaProbe(0)],
            new QuotaRouterOptions { MinQuotaPct = 10 },
            NullLogger<AgentClassRouter>.Instance);
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
            projects);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "parked",
            Prompt = "p",
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "frontier",
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(-2),
        };
        await store.CreateAsync(item);

        var sweep = typeof(QuotaRetryScheduler).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)sweep.Invoke(scheduler, [CancellationToken.None])!;

        var evt = Assert.Single(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "quota_retry_attempted", StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "WorkItemId"), item.Id.ToString(), StringComparison.Ordinal));
        Assert.Equal("periodic", GetScalar<string>(evt, "Source"));
        Assert.Equal("skipped:quota-still-gated", GetScalar<string>(evt, "Outcome"));
        Assert.Equal("WaitingForQuotaReset", GetScalar<string>(evt, "State"));
    }

    private sealed class StaticQuotaProbe : IAgentQuotaProbe
    {
        private readonly double _availablePct;
        public StaticQuotaProbe(double availablePct) => _availablePct = availablePct;
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
            => Task.FromResult(new AgentQuotaSnapshot { AvailablePct = _availablePct });
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        return sv.Value is T t ? t : default;
    }
}
