using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.CostTelemetryAndStreams;

internal sealed class CostTelemetryWorkspace : IDisposable
{
    private readonly TestTempDirectory _temp = TestTempDirectory.Create("codeybox-uat-cost-telemetry-");

    public string Root => _temp.Root;

    public string NewDatabasePath() => Path.Combine(Root, $"state-{Guid.NewGuid():N}.db");

    public string NewStreamRoot() => Path.Combine(Root, $"streams-{Guid.NewGuid():N}");

    public void Dispose() => _temp.Dispose();
}

internal sealed class CostTelemetryApiFactory : CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath;
    private readonly string _streamRoot;
    private readonly Project[] _projects;

    public SqliteWorkItemStore WorkItems { get; }
    public SqliteWorkItemCostStore Costs { get; }
    public SqliteTimingStore Timings { get; }
    public SqliteAgentStreamSummaryStore StreamSummaries { get; }
    public AgentStreamStore Streams { get; }
    public RecordingQueueController Queue { get; } = new();

    public CostTelemetryApiFactory(string dbPath, string streamRoot, params Project[] projects)
    {
        _dbPath = dbPath;
        _streamRoot = streamRoot;
        _projects = projects;
        WorkItems = new SqliteWorkItemStore(_dbPath);
        Costs = new SqliteWorkItemCostStore(_dbPath);
        Timings = new SqliteTimingStore(_dbPath);
        StreamSummaries = new SqliteAgentStreamSummaryStore(_dbPath);
        Streams = new AgentStreamStore(new AgentStreamsOptions { Path = _streamRoot }, NullLogger<AgentStreamStore>.Instance);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Temp.NewDirectoryPath("test-git-"),
                ["CodeyBox:AuditLog:Path"] = Temp.NewLogPath("test-log"),
                ["CodeyBox:AuditLog:AuditPath"] = Temp.NewLogPath("test-audit"),
                ["CodeyBox:AgentStreams:Path"] = _streamRoot,
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItems);
            services.RemoveAll<IWorkItemCostStore>();
            services.AddSingleton<IWorkItemCostStore>(Costs);
            services.RemoveAll<ITimingStore>();
            services.AddSingleton<ITimingStore>(Timings);
            services.RemoveAll<IAgentStreamStore>();
            services.AddSingleton<IAgentStreamStore>(Streams);
            services.RemoveAll<IAgentStreamSummaryStore>();
            services.AddSingleton<IAgentStreamSummaryStore>(StreamSummaries);
            services.RemoveAll<IQueueController>();
            services.AddSingleton<IQueueController>(Queue);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(_projects));
        });
    }

    public Task SeedWorkItemAsync(WorkItem item) => WorkItems.CreateAsync(item);

    public async Task<string> WriteCapturedStreamAsync(WorkItemId workItemId, string phase, int iteration, string content)
    {
        await using var capture = await Streams.BeginCaptureAsync(workItemId, phase, iteration);
        Assert.NotNull(capture);
        capture!.WriteChunk(content);
        return capture.FileName;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        TestTempArtifacts.CleanupAll(
            StreamSummaries.Dispose,
            Timings.Dispose,
            Costs.Dispose,
            WorkItems.Dispose,
            () => base.Dispose(disposing));
    }
}

internal sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<string, Project> _projects;

    public InMemoryProjectRepository(params Project[] projects)
    {
        _projects = projects.ToDictionary(p => p.Id.Value, StringComparer.Ordinal);
    }

    public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        => Task.FromResult(_projects.TryGetValue(id.Value, out var project) ? project : null);

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Project>>([.. _projects.Values]);
}

internal sealed class RecordingQueueController : IQueueController
{
    private readonly Dictionary<string, ProjectQueueState> _projectStates = new(StringComparer.Ordinal);

    public QueueState State => QueueState.Running;
    public DateTimeOffset? PausedAt => null;
    public string? PausedReason => null;
    public IReadOnlyDictionary<string, ProjectQueueState> ProjectStates => _projectStates;

    public Task PauseAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default)
    {
        _projectStates[projectId.Value] = new ProjectQueueState(projectId, true, DateTimeOffset.UtcNow, reason);
        return Task.CompletedTask;
    }

    public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        _projectStates.Remove(projectId.Value);
        return Task.CompletedTask;
    }

    public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default)
        => Task.FromResult(_projectStates.GetValueOrDefault(projectId.Value));
}

internal sealed class FixedSpendCostStore : IWorkItemCostStore
{
    private readonly Dictionary<string, decimal> _spend = new(StringComparer.Ordinal);

    public void SetSpend(ProjectId projectId, decimal spend) => _spend[projectId.Value] = spend;

    public Task<decimal> SumEstimatedUsdAsync(
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
        => Task.FromResult(_spend.GetValueOrDefault(projectId));

    public Task RecordAsync(WorkItemCost cost, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

    public Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkItemCost>>([]);

    public Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string ProjectId, double TotalUsd)>>([]);

    public Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class CapturingWebhookDispatcher : IWebhookDispatcher
{
    public List<WebhookEvent> Events { get; } = [];

    public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingLogSink<T> : ILogger<T>, IDisposable
{
    public List<string> Warnings { get; } = [];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
        => this;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }

    public void Dispose()
    {
    }
}

internal static class CostTelemetryFixtures
{
    public static readonly ProjectId ProjectId = new("uat-cost-telemetry");

    public static Project Project(ProjectBudget? budget = null) => new()
    {
        Id = ProjectId,
        DisplayName = "UAT Cost Telemetry",
        RepositoryUrl = "https://example.invalid/repo.git",
        Budget = budget ?? new ProjectBudget(),
    };

    public static WorkItem WorkItem(WorkItemState state = WorkItemState.Done) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = ProjectId,
        Title = "Cost telemetry streams UAT",
        Prompt = "exercise cost telemetry and stream diagnostics",
        Agent = AgentKind.Claude,
        State = state,
        CreatedAt = DateTimeOffset.Parse("2026-05-14T00:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-05-14T00:10:00Z"),
    };

    public static WorkItemCost Cost(
        WorkItemId workItemId,
        string phase,
        DateTimeOffset startedAt,
        double estimatedUsd,
        AgentKind? agentKind = null,
        string? modelId = "uat-model")
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = workItemId.ToString(),
            Phase = phase,
            AgentKind = (agentKind ?? AgentKind.Claude).Value,
            ModelId = modelId,
            InputTokens = 100,
            CachedInputTokens = 20,
            OutputTokens = 30,
            EstimatedUsd = estimatedUsd,
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(5),
        };
}
