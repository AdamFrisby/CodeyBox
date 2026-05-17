using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.CostTelemetryAndStreams;

/// <summary>
/// UAT coverage for structured stream persistence, endpoint retrieval,
/// on-demand analysis, aggregate stream metrics, redaction, and live stdout tail.
/// Plan anchor:
/// docs/uat/00-plan.md#agent-stream-capture-analysis-and-live-stdout---persists-structured-streams-and-broadcasts-live-output
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AgentStreamsAndStdoutUatTests : IDisposable
{
    private readonly CostTelemetryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task StreamEndpoints_ListDownloadAndAnalyzeCapturedRedactedStream()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var item = CostTelemetryFixtures.WorkItem();
        await factory.SeedWorkItemAsync(item);
        var fileName = await factory.WriteCapturedStreamAsync(item.Id, "work", 1, """
            {"type":"assistant","timestamp":"2026-05-14T00:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"tool_result","timestamp":"2026-05-14T00:00:04Z","tool_use_id":"t1","content":"ok","is_error":false}
            {"type":"result","timestamp":"2026-05-14T00:00:05Z","result":"done","usage":{"input_tokens":100,"output_tokens":25,"cached_input_tokens":10}}
            {"Authorization":"Bearer plain-uat-secret","message":"safe"}
            """);

        var client = factory.CreateClient();
        var listResponse = await client.GetAsync($"/workitems/{item.Id}/agent-streams?includeLineCount=true");
        var rawResponse = await client.GetAsync($"/workitems/{item.Id}/agent-streams/{fileName}");
        var analysisResponse = await client.GetAsync($"/workitems/{item.Id}/agent-streams/{fileName}/analysis");

        listResponse.EnsureSuccessStatusCode();
        rawResponse.EnsureSuccessStatusCode();
        analysisResponse.EnsureSuccessStatusCode();
        var listed = Assert.Single((await listResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(fileName, listed.GetProperty("fileName").GetString());
        Assert.Equal("work", listed.GetProperty("phase").GetString());
        Assert.Equal(4, listed.GetProperty("lineCount").GetInt64());

        var raw = await rawResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"Authorization\":\"***\"", raw);
        Assert.DoesNotContain("plain-uat-secret", raw);

        var analysis = await analysisResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("claude", analysis.GetProperty("agentKind").GetString());
        Assert.Equal(100, analysis.GetProperty("inputTokens").GetInt32());
        Assert.Equal(25, analysis.GetProperty("outputTokens").GetInt32());
        Assert.Equal(10, analysis.GetProperty("cachedInputTokens").GetInt32());
        Assert.Equal("done", analysis.GetProperty("finalAssistantMessage").GetString());
        var tool = Assert.Single(analysis.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal("Bash", tool.GetProperty("toolName").GetString());
    }

    [Fact]
    public async Task StreamAggregate_ReturnsToolSpendAndStallMetricsForWorkItem()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var item = CostTelemetryFixtures.WorkItem();
        await factory.SeedWorkItemAsync(item);
        await factory.StreamSummaries.UpsertAsync(new AgentStreamSummaryRow(
            item.Id,
            "work-1-abcdef.jsonl",
            "work",
            1,
            AgentKind.Codex,
            new AgentStreamSummary(
                TimeSpan.FromSeconds(12),
                TimeSpan.FromSeconds(2),
                120,
                40,
                30,
                0.64m,
                [new ToolCallInvocation(
                    "call-1",
                    "unified_exec",
                    "{\"cmd\":\"dotnet test\"}",
                    DateTimeOffset.Parse("2026-05-14T00:00:02Z"),
                    DateTimeOffset.Parse("2026-05-14T00:00:08Z"),
                    TimeSpan.FromSeconds(6),
                    true,
                    16)],
                [new StallEvent(
                    DateTimeOffset.Parse("2026-05-14T00:00:08Z"),
                    TimeSpan.FromSeconds(4),
                    "tool_result",
                    "message",
                    "thinking")],
                "done"),
            DateTimeOffset.Parse("2026-05-14T00:00:13Z")));

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/workitems/{item.Id}/agent-streams/aggregate");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(item.Id.ToString(), json.GetProperty("workItemId").GetString());
        Assert.Equal(12_000, json.GetProperty("totalAgentDurationMs").GetInt64());
        Assert.Equal(1, json.GetProperty("totalToolCalls").GetInt32());
        Assert.Equal(1, json.GetProperty("stallCount").GetInt32());
        Assert.Equal(0.64m, json.GetProperty("estimatedUsdTotal").GetDecimal());
        var byTool = Assert.Single(json.GetProperty("byTool").EnumerateArray());
        Assert.Equal("unified_exec", byTool.GetProperty("tool").GetString());
        var invocation = Assert.Single(json.GetProperty("invocations").EnumerateArray());
        Assert.Equal("codex", invocation.GetProperty("agentKind").GetString());
    }

    [Fact]
    public async Task UnknownStreamKind_ReturnsUnsupportedAnalysisWithoutCrashing()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var item = CostTelemetryFixtures.WorkItem() with { Agent = new AgentKind("unknown-agent") };
        await factory.SeedWorkItemAsync(item);
        const string fileName = "work-1-abcdef.jsonl";
        var dir = Path.Combine(factory.Streams.Options.Path, item.Id.ToString());
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, fileName), "{\"type\":\"mystery\",\"value\":1}\n");

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/workitems/{item.Id}/agent-streams/{fileName}/analysis");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown", json.GetProperty("agentKind").GetString());
        Assert.Equal(0, json.GetProperty("totalDurationMs").GetInt64());
        Assert.Empty(json.GetProperty("toolCalls").EnumerateArray());
    }

    [Fact]
    public async Task MissingOrUnsafeStreamFile_ReturnsNotFound()
    {
        using var factory = new CostTelemetryApiFactory(
            _workspace.NewDatabasePath(),
            _workspace.NewStreamRoot(),
            CostTelemetryFixtures.Project());
        var item = CostTelemetryFixtures.WorkItem();
        await factory.SeedWorkItemAsync(item);

        var client = factory.CreateClient();
        var missing = await client.GetAsync($"/workitems/{item.Id}/agent-streams/missing-1-abcdef.jsonl");
        var unsafeName = await client.GetAsync($"/workitems/{item.Id}/agent-streams/..%2Fsecret.jsonl");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unsafeName.StatusCode);
    }

    [Fact]
    public async Task LiveStdoutTail_ReturnsRedactedLateJoinerOutputAfterCompletion()
    {
        var hub = new FakeHubContext();
        using var broadcaster = new AgentStdoutBroadcastService(hub);
        var item = CostTelemetryFixtures.WorkItem();

        broadcaster.BroadcastChunk(item.Id, "work", "line one\n");
        broadcaster.BroadcastChunk(item.Id, "work", "token=gho_FAKE_REDACTION_TEST_TOKEN_XXX\n");
        await broadcaster.CompleteAsync(item.Id);

        var tail = broadcaster.GetTail(item.Id);
        Assert.Contains("line one", tail);
        Assert.Contains("token=***", tail);
        Assert.DoesNotContain("gho_", tail);
        Assert.Contains(hub.Clients.Sent, m => m.Group == $"wi:{item.Id}" && m.Method == "streamComplete");
    }
}
