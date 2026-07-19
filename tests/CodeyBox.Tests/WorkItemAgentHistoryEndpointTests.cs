using System.Net;
using System.Net.Http.Json;
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
using CodeyBox.Projects;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level coverage for the per-phase agent-involvement contract: the
/// agentHistory array + workAgent on GET /workitems/{id}, and the dedicated
/// GET /workitems/{id}/agent-history endpoint. Without these a regression in
/// MapInvolvement, the "omit when empty" branch, or the WorkAgent derivation
/// would silently mis-attribute quota burn during operator review.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemAgentHistoryEndpointTests : IDisposable
{
    private readonly AgentHistoryApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemAgentHistoryEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Get_WhenHistoryEmpty_ReturnsEmptyArray()
    {
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("agentHistory", out var ah),
            "agentHistory must be present on GET when the involvement store is wired");
        Assert.Equal(JsonValueKind.Array, ah.ValueKind);
        Assert.Equal(0, ah.GetArrayLength());
    }

    [Fact]
    public async Task Get_ReturnsFullHistoryAndDerivesWorkAgent()
    {
        // Models the wrong-attribution bug: Cursor did the work, Claude is now
        // auditing. WorkItem.agent reads "claude" (current phase) but workAgent
        // must report "cursor" (the original implementer) from agentHistory.
        var item = NewItem(currentAgent: AgentKind.Claude, state: WorkItemState.Auditing);
        await _factory.Store.CreateAsync(item);

        await SeedWork(item.Id, AgentKind.Cursor, "composer-2.5");
        await SeedFinalized(item.Id, AgentKind.Claude, "audit", iteration: 1, outcome: "success");

        var dto = await _client.GetFromJsonAsync<WorkItemDtoForTest>($"/workitems/{item.Id}");
        Assert.NotNull(dto);
        Assert.Equal("claude", dto!.Agent);          // current-phase field, unchanged contract
        Assert.Equal("cursor", dto.WorkAgent);        // derived original implementer
        Assert.NotNull(dto.AgentHistory);
        Assert.Equal(2, dto.AgentHistory!.Count);

        var work = Assert.Single(dto.AgentHistory, r => r.Phase == "work");
        Assert.Equal("cursor", work.AgentKind);
        Assert.Equal("composer-2.5", work.ModelId);
        Assert.Null(work.EndedAt);                    // still in progress
        Assert.Null(work.Outcome);

        var audit = Assert.Single(dto.AgentHistory, r => r.Phase == "audit");
        Assert.Equal("claude", audit.AgentKind);
        Assert.Equal(1, audit.Iteration);
        Assert.NotNull(audit.EndedAt);
        Assert.Equal("success", audit.Outcome);
    }

    [Fact]
    public async Task Get_AfterWorkQuotaFallback_WorkAgentReportsSuccessfulImplementer()
    {
        // Regression guard for the mis-attribution this feature exists to fix:
        // a work-phase quota fallback records the exhausted attempt first (codex
        // failure:quota) then the successor that produced the implementation
        // (claude success). workAgent must report claude — returning the first
        // (failed) work row would re-introduce the wrong-agent attribution.
        var item = NewItem(currentAgent: AgentKind.Claude, state: WorkItemState.Auditing);
        await _factory.Store.CreateAsync(item);

        await SeedFinalized(item.Id, AgentKind.Codex, "work", iteration: null, outcome: "failure:quota");
        await SeedFinalized(item.Id, AgentKind.Claude, "work", iteration: null, outcome: "success");

        var dto = await _client.GetFromJsonAsync<WorkItemDtoForTest>($"/workitems/{item.Id}");
        Assert.NotNull(dto);
        Assert.Equal("claude", dto!.WorkAgent);
    }

    [Fact]
    public async Task Get_WorkAgent_MatchesPhaseCaseInsensitively()
    {
        // ResolveWorkAgent compares phase with OrdinalIgnoreCase; a regression to
        // case-sensitive matching would silently return null for a "Work" row.
        var item = NewItem();
        await _factory.Store.CreateAsync(item);
        await SeedFinalized(item.Id, AgentKind.Cursor, "Work", iteration: null, outcome: "success");

        var dto = await _client.GetFromJsonAsync<WorkItemDtoForTest>($"/workitems/{item.Id}");
        Assert.NotNull(dto);
        Assert.Equal("cursor", dto!.WorkAgent);
    }

    [Fact]
    public async Task AgentHistoryEndpoint_ReturnsTrailAndWorkAgent()
    {
        var item = NewItem();
        await _factory.Store.CreateAsync(item);
        await SeedWork(item.Id, AgentKind.Opencode, "opencode-go/deepseek-v4-pro");
        await SeedFinalized(item.Id, AgentKind.Claude, "merge", iteration: null, outcome: "success");

        var resp = await _client.GetFromJsonAsync<AgentHistoryResponseForTest>(
            $"/workitems/{item.Id}/agent-history");
        Assert.NotNull(resp);
        Assert.Equal(item.Id.ToString(), resp!.WorkItemId);
        Assert.Equal("opencode", resp.WorkAgent);
        Assert.Equal(2, resp.AgentHistory.Count);
        Assert.Equal("work", resp.AgentHistory[0].Phase);
        Assert.Equal("merge", resp.AgentHistory[1].Phase);
    }

    [Fact]
    public async Task AgentHistoryEndpoint_UnknownItem_ReturnsNotFound()
    {
        var resp = await _client.GetAsync($"/workitems/{WorkItemId.New()}/agent-history");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AgentHistoryEndpoint_WhenWired_ReturnsEmptyArrayForNoRuns()
    {
        // Store wired but no agent has run yet → [] (not omitted), so a poller can
        // tell "no agent ran yet" apart from "feature disabled".
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}/agent-history");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("agentHistory", out var ah),
            "agentHistory must be present (empty array) when the store is wired");
        Assert.Equal(JsonValueKind.Array, ah.ValueKind);
        Assert.Equal(0, ah.GetArrayLength());
    }

    [Fact]
    public async Task Get_OnlyReturnsHistoryForRequestedWorkItem()
    {
        var requested = NewItem();
        var other = NewItem();
        await _factory.Store.CreateAsync(requested);
        await _factory.Store.CreateAsync(other);
        await SeedWork(other.Id, AgentKind.Codex, null);

        var dto = await _client.GetFromJsonAsync<WorkItemDtoForTest>($"/workitems/{requested.Id}");
        Assert.NotNull(dto);
        Assert.NotNull(dto!.AgentHistory);
        Assert.Empty(dto.AgentHistory!);
        Assert.Null(dto.WorkAgent);
    }

    private Task SeedWork(WorkItemId id, AgentKind agent, string? modelId) =>
        _factory.Involvement.RecordStartAsync(new AgentInvolvement(
            Guid.NewGuid(), id, agent, modelId, "work",
            DateTimeOffset.UtcNow.AddMinutes(-10), EndedAt: null, Iteration: 1, Outcome: null));

    private async Task SeedFinalized(WorkItemId id, AgentKind agent, string phase, int? iteration, string outcome)
    {
        var entryId = Guid.NewGuid();
        await _factory.Involvement.RecordStartAsync(new AgentInvolvement(
            entryId, id, agent, ModelId: null, phase,
            DateTimeOffset.UtcNow.AddMinutes(-5), EndedAt: null, iteration, Outcome: null));
        await _factory.Involvement.FinalizeAsync(entryId, DateTimeOffset.UtcNow, outcome);
    }

    private static WorkItem NewItem(AgentKind? currentAgent = null, WorkItemState state = WorkItemState.Working) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "agent-history test",
        Prompt = "p",
        Agent = currentAgent,
        State = state,
    };

    private sealed record WorkItemDtoForTest(
        string Id,
        string ProjectId,
        string State,
        string Agent,
        string? WorkAgent,
        IReadOnlyList<InvolvementRowForTest>? AgentHistory);

    private sealed record AgentHistoryResponseForTest(
        string WorkItemId,
        string? WorkAgent,
        IReadOnlyList<InvolvementRowForTest> AgentHistory);

    private sealed record InvolvementRowForTest(
        string Id,
        string AgentKind,
        string? ModelId,
        string Phase,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        int? Iteration,
        string? Outcome);
}

/// <summary>
/// WebApplicationFactory that swaps the real SQLite-backed involvement store
/// for the in-memory variant so tests can pre-seed entries without disk.
/// </summary>
internal sealed class AgentHistoryApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-ahtest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public InMemoryAgentInvolvementStore Involvement { get; } = new();

    public AgentHistoryApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Temp.Root;
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);
            services.RemoveAll<IAgentInvolvementStore>();
            services.AddSingleton<IAgentInvolvementStore>(Involvement);
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                }));
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, Store.Dispose);
}

/// <summary>
/// Exercises the feature-DISABLED branch: when no <see cref="IAgentInvolvementStore"/>
/// is registered, GET /workitems/{id} must OMIT agentHistory/workAgent entirely
/// (not emit [] or null-valued fields), and GET /workitems/{id}/agent-history must
/// return a body carrying only workItemId. This is the contract that lets a poller
/// tell "feature disabled" (field absent) apart from "wired but empty" ([]). The
/// always-wired <see cref="AgentHistoryApiFactory"/> never reaches these branches.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemAgentHistoryDisabledEndpointTests : IDisposable
{
    private readonly AgentHistoryDisabledApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemAgentHistoryDisabledEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Get_WhenStoreUnwired_OmitsAgentHistoryAndWorkAgent()
    {
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("agentHistory", out _),
            "agentHistory must be omitted (not []) when the involvement store is unwired");
        Assert.False(doc.RootElement.TryGetProperty("workAgent", out _),
            "workAgent must be omitted when the involvement store is unwired");
    }

    [Fact]
    public async Task AgentHistoryEndpoint_WhenStoreUnwired_OmitsTrailAndWorkAgent()
    {
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}/agent-history");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(item.Id.ToString(), doc.RootElement.GetProperty("workItemId").GetString());
        Assert.False(doc.RootElement.TryGetProperty("agentHistory", out _),
            "agentHistory must be omitted when the involvement store is unwired");
        Assert.False(doc.RootElement.TryGetProperty("workAgent", out _),
            "workAgent must be omitted when the involvement store is unwired");
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "agent-history disabled test",
        Prompt = "p",
        State = WorkItemState.Working,
    };
}

/// <summary>
/// WebApplicationFactory that REMOVES the involvement store registration so the
/// endpoints' optional <c>IAgentInvolvementStore?</c> parameter binds to null,
/// driving the feature-disabled branches.
/// </summary>
internal sealed class AgentHistoryDisabledApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-ahdis-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }

    public AgentHistoryDisabledApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Temp.Root;
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);
            // Intentionally NOT re-adding IAgentInvolvementStore → endpoint param is null.
            services.RemoveAll<IAgentInvolvementStore>();
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                }));
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, Store.Dispose);
}
