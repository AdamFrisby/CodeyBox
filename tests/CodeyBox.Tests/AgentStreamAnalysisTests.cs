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

    [Fact]
    public async Task ParseAsync_LeavesDurationsUnknownWhenTimestampsAreMissing()
    {
        var parser = new ClaudeStreamParser();
        await using var stream = StreamOf("""
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","result":"done","usage":{"input_tokens":100,"output_tokens":20}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Null(tool.StartedAt);
        Assert.Null(tool.EndedAt);
        Assert.Null(tool.Duration);
        Assert.Equal(TimeSpan.Zero, summary.TotalDuration);
        Assert.Null(summary.TimeToFirstToken);
        Assert.Empty(summary.Stalls);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_UsesCaptureFileTimingWhenEventsHaveNoTimestamps()
    {
        var parser = new ClaudeStreamParser(new AgentStreamParserOptions { StallThreshold = TimeSpan.Zero });
        await using var stream = TimedStreamOf(
            """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","result":"done","usage":{"input_tokens":100,"output_tokens":20}}
            """,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:30Z"));

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.NotNull(tool.StartedAt);
        Assert.NotNull(tool.EndedAt);
        Assert.NotNull(tool.Duration);
        Assert.True(tool.Duration > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), summary.TotalDuration);
        Assert.NotNull(summary.TimeToFirstToken);
        Assert.Contains(summary.Stalls, s => s.Classification == "tool_execution");
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
    private static TimedMemoryStream TimedStreamOf(string text, DateTimeOffset capturedAt, DateTimeOffset completedAt) =>
        new(Encoding.UTF8.GetBytes(text), capturedAt, completedAt);
}

public sealed class CodexStreamParserTests
{
    [Fact]
    public async Task ParseAsync_ParsesNestedItemCompletedEvents()
    {
        var parser = new CodexStreamParser();
        await using var stream = StreamOf("""
            {"type":"thread.started","timestamp":"2026-01-01T00:00:00Z","thread_id":"thread_1"}
            {"type":"turn.started","timestamp":"2026-01-01T00:00:01Z"}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:02Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{\"cmd\":\"dotnet test\"}"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:12Z","item":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:13Z","item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}
            {"type":"turn.completed","timestamp":"2026-01-01T00:00:15Z","usage":{"input_tokens":30,"output_tokens":4,"cached_input_tokens":2}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("call_1", tool.ToolUseId);
        Assert.Equal("shell", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(10), tool.Duration);
        Assert.Equal(30, summary.InputTokens);
        Assert.Equal(4, summary.OutputTokens);
        Assert.Equal(2, summary.CachedInputTokens);
        Assert.Equal("done", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_ParsesInstalledCommandExecutionAndAgentMessageEvents()
    {
        var parser = new CodexStreamParser();
        await using var stream = TimedStreamOf(
            """
            {"type":"thread.started","thread_id":"thread_1"}
            {"type":"turn.started"}
            {"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"/bin/bash -lc pwd","aggregated_output":"","exit_code":null,"status":"in_progress"}}
            {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"/bin/bash -lc pwd","aggregated_output":"/work\n","exit_code":0,"status":"completed"}}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Done."}}
            {"type":"turn.completed","usage":{"input_tokens":29990,"cached_input_tokens":18176,"output_tokens":44,"reasoning_output_tokens":0}}
            """,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:12Z"));

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("item_0", tool.ToolUseId);
        Assert.Equal("Bash", tool.ToolName);
        Assert.NotNull(tool.StartedAt);
        Assert.NotNull(tool.EndedAt);
        Assert.NotNull(tool.Duration);
        Assert.True(tool.Duration > TimeSpan.Zero);
        Assert.True(tool.Succeeded);
        Assert.Equal(6, tool.OutputBytes);
        Assert.Equal(29990, summary.InputTokens);
        Assert.Equal(44, summary.OutputTokens);
        Assert.Equal(18176, summary.CachedInputTokens);
        Assert.Equal("Done.", summary.FinalAssistantMessage);
        Assert.Equal(TimeSpan.FromSeconds(12), summary.TotalDuration);
    }

    [Fact]
    public async Task ParseAsync_ParsesToolResultsLargerThanOneMiB()
    {
        var parser = new CodexStreamParser();
        var output = new string('x', 1024 * 1024 + 128);
        await using var stream = StreamOf(
            """
            {"type":"item.completed","timestamp":"2026-01-01T00:00:00Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}
            """ + "\n" +
            """
            {"type":"item.completed","timestamp":"2026-01-01T00:00:03Z","item":{"type":"function_call_output","call_id":"call_1","output":
            """ + JsonSerializer.Serialize(output) + "}}\n");

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(3), tool.Duration);
        Assert.Equal(Encoding.UTF8.GetByteCount(output), tool.OutputBytes);
    }

    private static MemoryStream StreamOf(string text) => new(Encoding.UTF8.GetBytes(text));
    private static TimedMemoryStream TimedStreamOf(string text, DateTimeOffset capturedAt, DateTimeOffset completedAt) =>
        new(Encoding.UTF8.GetBytes(text), capturedAt, completedAt);
}

public sealed class TimedMemoryStream : MemoryStream, IAgentStreamTimingSource
{
    public TimedMemoryStream(byte[] buffer, DateTimeOffset capturedAt, DateTimeOffset completedAt)
        : base(buffer)
    {
        CapturedAt = capturedAt;
        CompletedAt = completedAt;
    }

    public DateTimeOffset? CapturedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
}

public sealed class GeminiStreamParserTests
{
    [Fact]
    public async Task ParseAsync_ParsesFunctionCallPartsAndUsageMetadata()
    {
        var parser = new GeminiStreamParser();
        await using var stream = StreamOf("""
            {"type":"response","timestamp":"2026-01-01T00:00:00Z","candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"g1","name":"read_file","args":{"path":"a.txt"}}}]}}]}
            {"type":"response","timestamp":"2026-01-01T00:00:03Z","candidates":[{"content":{"role":"model","parts":[{"functionResponse":{"id":"g1","name":"read_file","response":{"content":"ok"}}}]}}]}
            {"type":"response","timestamp":"2026-01-01T00:00:05Z","candidates":[{"content":{"role":"model","parts":[{"text":"done"}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":3,"cachedContentTokenCount":1}}
            """);

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("read_file", tool.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(3), tool.Duration);
        Assert.Equal(10, summary.InputTokens);
        Assert.Equal(3, summary.OutputTokens);
        Assert.Equal(1, summary.CachedInputTokens);
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

    [Fact]
    public void Aggregate_UsesWallClockUnionForOverlappingToolCalls()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var aggregate = AgentStreamAnalytics.Aggregate("wid", [new AgentStreamSummaryRow(
            new WorkItemId(Guid.NewGuid()),
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [
                    new ToolCallInvocation("t1", "Bash", "{}", start, start.AddSeconds(10), TimeSpan.FromSeconds(10), true, 10),
                    new ToolCallInvocation("t2", "Read", "{}", start.AddSeconds(5), start.AddSeconds(20), TimeSpan.FromSeconds(15), true, 10),
                ],
                [],
                null),
            DateTimeOffset.UtcNow)]);

        Assert.Equal(25_000, aggregate.ByTool.Sum(t => t.TotalDurationMs));
        Assert.Equal(20_000, aggregate.ExecutingMs);
        Assert.Equal(10_000, aggregate.ThinkingMs);
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

    [Fact]
    public async Task AnalyzeRecentTerminalWorkItemsAsync_UsesParserDetectedFromEachStreamFile()
    {
        var item = CreateItem(WorkItemState.Done);
        await _workItems.CreateAsync(item);
        WriteStreamFile(item.Id, "audit-llm-security:llm-review-1-abcdef.jsonl", """
            {"type":"thread.started","timestamp":"2026-01-01T00:00:00Z"}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:01Z","item":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}
            {"type":"item.completed","timestamp":"2026-01-01T00:00:02Z","item":{"type":"function_call_output","call_id":"call_1","output":"ok"}}
            {"type":"turn.completed","timestamp":"2026-01-01T00:00:03Z","usage":{"input_tokens":5,"output_tokens":1}}
            """);

        var service = new StreamAnalysisService(_workItems, _streams, _summaries,
            [new ClaudeStreamParser(), new CodexStreamParser(), new UnknownAgentStreamParser()],
            NullLogger<StreamAnalysisService>.Instance,
            _costs);

        var count = await service.AnalyzeRecentTerminalWorkItemsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.Equal(1, count);
        var row = Assert.Single(await _summaries.GetByWorkItemAsync(item.Id));
        Assert.Equal(AgentKind.Codex, row.AgentKind);
        Assert.Equal("shell", Assert.Single(row.Summary.ToolCalls).ToolName);
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
        Assert.Equal(4_000, body.GetProperty("executingMs").GetInt64());
        Assert.Equal(6_000, body.GetProperty("thinkingMs").GetInt64());
        var bash = body.GetProperty("byTool").EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "Bash");
        Assert.Equal(4_000, bash.GetProperty("totalDurationMs").GetInt64());
    }
}

public sealed class FleetAggregateEndpointTests : IClassFixture<AgentStreamAnalysisApiFactory>
{
    private readonly AgentStreamAnalysisApiFactory _factory;

    public FleetAggregateEndpointTests(AgentStreamAnalysisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task FleetAggregate_UsesRecentTerminalWorkItems()
    {
        var first = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Done) with
        {
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var second = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.AuditFailed) with
        {
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        var active = AgentStreamAnalysisApiFactory.CreateItem(WorkItemState.Working) with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Store.CreateAsync(first);
        await _factory.Store.CreateAsync(second);
        await _factory.Store.CreateAsync(active);

        var now = DateTimeOffset.UtcNow;
        await _factory.Summaries.UpsertAsync(Row(first.Id, "Bash", TimeSpan.FromSeconds(3), now));
        await _factory.Summaries.UpsertAsync(Row(second.Id, "Read", TimeSpan.FromSeconds(1), now));
        await _factory.Summaries.UpsertAsync(Row(active.Id, "Write", TimeSpan.FromSeconds(9), now));

        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/agent-streams/aggregate?n=50");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("totalToolCalls").GetInt32());
        Assert.Equal(4_000, body.GetProperty("executingMs").GetInt64());
        Assert.Empty(body.GetProperty("invocations").EnumerateArray());
        Assert.DoesNotContain(
            body.GetProperty("byTool").EnumerateArray(),
            t => t.GetProperty("tool").GetString() == "Write");
    }

    private static AgentStreamSummaryRow Row(
        WorkItemId id,
        string tool,
        TimeSpan duration,
        DateTimeOffset now)
    {
        var start = now - duration;
        return new AgentStreamSummaryRow(
            id,
            $"{tool.ToLowerInvariant()}-1-abcdef.jsonl",
            tool.ToLowerInvariant(),
            1,
            AgentKind.Claude,
            new AgentStreamSummary(
                duration,
                TimeSpan.Zero,
                0,
                0,
                0,
                null,
                [new ToolCallInvocation($"{tool}-1", tool, "{}", start, now, duration, true, 1)],
                [],
                null),
            now);
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
