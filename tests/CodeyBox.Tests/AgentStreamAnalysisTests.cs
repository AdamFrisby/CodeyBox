using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class ClaudeStreamParserTests
{
    [Fact]
    public async Task ParseAsync_ComputesToolDurationsAndUsage()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromSeconds(30) });
        await using var stream = StreamOf("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:12Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-01-01T00:00:15Z","result":"done","total_cost_usd":0.42,"usage":{"input_tokens":100,"output_tokens":20,"cached_input_tokens":5}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("Bash", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.True(tool.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(2), summary.TimeToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(15), summary.TotalDuration);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(20, summary.OutputTokens);
        Assert.Equal(5, summary.CachedInputTokens);
        Assert.Equal(0.42m, summary.EstimatedUsd);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
}

public sealed class StallDetectionTests
{
    [Fact]
    public async Task ParseAsync_RecordsToolExecutionStalls()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.FromSeconds(30) });
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"sleep 45"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:45Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-01-01T00:00:46Z","result":"done"}
            """));

        var summary = await parser.ParseAsync(stream);

        var stall = Assert.Single(summary.Stalls);
        Assert.Equal(TimeSpan.FromSeconds(45), stall.GapDuration);
        Assert.Equal("tool_execution", stall.Classification);
        Assert.Equal("tool_use", stall.PreviousEventType);
        Assert.Equal("tool_result", stall.NextEventType);
    }
}

public sealed class ThinkingVsExecutingSplitTests
{
    [Fact]
    public async Task Aggregate_SubtractsToolTimeFromAgentDuration()
    {
        var parser = new ClaudeStreamParser();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"system","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"assistant","timestamp":"2026-01-01T00:00:10Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"a"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:25Z","tool_use_id":"t1","content":"ok"}
            {"type":"result","timestamp":"2026-01-01T00:00:40Z","result":"done"}
            """));

        var summary = await parser.ParseAsync(stream);
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()), "work-1-abcdef.jsonl", "work", 1, AgentKind.Claude, summary, DateTimeOffset.UtcNow)]);

        Assert.Equal(40_000, aggregate.TotalAgentDurationMs);
        Assert.Equal(15_000, aggregate.ExecutingMs);
        Assert.Equal(25_000, aggregate.ThinkingMs);
    }
}

public sealed class StreamAnalysisServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-{Guid.NewGuid():N}.db");
    private readonly string _streamRoot = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-{Guid.NewGuid():N}");
    private readonly SqliteWorkItemStore _workItems;
    private readonly AgentStreamStore _streams;
    private readonly SqliteAgentStreamSummaryStore _summaries;
    private readonly SqliteWorkItemCostStore _costs;

    public StreamAnalysisServiceTests()
    {
        _workItems = new SqliteWorkItemStore(_dbPath);
        _streams = new AgentStreamStore(new AgentStreamsOptions { Path = _streamRoot }, NullLogger<AgentStreamStore>.Instance);
        _summaries = new SqliteAgentStreamSummaryStore(_dbPath);
        _costs = new SqliteWorkItemCostStore(_dbPath);
    }

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_WritesSummaries()
    {
        var item = CreateItem(WorkItemState.Done);
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "work-1-abcdef.jsonl", """
            {"type":"assistant","timestamp":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"path":"a"}}]}}
            {"type":"tool_result","timestamp":"2026-01-01T00:00:01Z","tool_use_id":"t1","content":"ok"}
            {"type":"result","timestamp":"2026-01-01T00:00:02Z","result":"done","total_cost_usd":0.75,"usage":{"input_tokens":10,"output_tokens":2}}
            """);
        await _costs.RecordAsync(new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = item.Id.ToString(),
            Phase = "work",
            Iteration = 1,
            AgentKind = "claude",
            InputTokens = 1,
            OutputTokens = 1,
            EstimatedUsd = 0.01,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var rows = await _summaries.GetByWorkItemAsync(item.Id);
        var row = Assert.Single(rows);
        Assert.Equal("work-1-abcdef.jsonl", row.FileName);
        Assert.Single(row.Summary.ToolCalls);
        var costs = await _costs.GetByWorkItemAsync(item.Id.ToString());
        var cost = Assert.Single(costs);
        Assert.Equal(0.75, cost.EstimatedUsd);
        Assert.Equal(10, cost.InputTokens);
    }

    private void WriteStreamFile(WorkItemId id, string fileName, string content)
    {
        var dir = Path.Combine(_streamRoot, id.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private static WorkItem CreateItem(WorkItemState state) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "stream",
        Prompt = "stream",
        Agent = AgentKind.Claude,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    public void Dispose()
    {
        _summaries.Dispose();
        _costs.Dispose();
        _workItems.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_streamRoot, recursive: true); } catch { }
    }
}

public sealed class AggregateEndpointTests : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public AggregateEndpointTests(AgentStreamAnalysisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task WorkItemAggregate_GroupsByTool()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);
        await _factory.Summaries.UpsertAsync(new AgentStreamSummaryRow(
            item.Id,
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 0, 0, 0, 1.25m,
                [
                    new ToolCallInvocation("t1", "Bash", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(4), TimeSpan.FromSeconds(4), true, 10),
                    new ToolCallInvocation("t2", "Read", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromSeconds(1), true, 10),
                ],
                [],
                "done"),
            DateTimeOffset.UtcNow));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/aggregate");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalToolCalls").GetInt32());
        Assert.Equal(5_000, body.GetProperty("executingMs").GetInt64());
        Assert.Equal(5_000, body.GetProperty("thinkingMs").GetInt64());
        var bash = body.GetProperty("byTool").EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "Bash");
        Assert.Equal(4_000, bash.GetProperty("totalDurationMs").GetInt64());
    }
}

public sealed class MissingFileTests : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public MissingFileTests(AgentStreamAnalysisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task WorkItemAggregate_NoSummaries_ReturnsZeros()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/aggregate");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("totalAgentDurationMs").GetInt64());
        Assert.Equal(0, body.GetProperty("totalToolCalls").GetInt32());
    }

    [Fact]
    public async Task AnalyzeFile_MissingFile_Returns404()
    {
        var item = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done);
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/agent-streams/missing-1-abcdef.jsonl/analysis");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

public sealed class AgentStreamAnalysisApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-api-{Guid.NewGuid():N}.db");
    private readonly string _streamRoot = Path.Combine(Path.GetTempPath(), $"codeybox-stream-analysis-api-{Guid.NewGuid():N}");

    public SqliteWorkItemStore Store { get; }
    public SqliteAgentStreamSummaryStore Summaries { get; }

    public AgentStreamAnalysisApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        Summaries = new SqliteAgentStreamSummaryStore(_dbPath);
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
                ["CodeyBox:GitRootDirectory"] = Path.Combine(Path.GetTempPath(), $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(Path.GetTempPath(), $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(Path.GetTempPath(), $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = _streamRoot,
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);
            services.RemoveAll<IAgentStreamSummaryStore>();
            services.AddSingleton<IAgentStreamSummaryStore>(Summaries);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    public static WorkItem CreateItem(WorkItemState state) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "stream",
        Prompt = "stream",
        Agent = AgentKind.Claude,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Summaries.Dispose();
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { }
            try { Directory.Delete(_streamRoot, recursive: true); } catch { }
        }
        base.Dispose(disposing);
    }
}
