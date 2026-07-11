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
/// HTTP-level coverage for the FallbackHistory contract on GET /workitems/{id}.
/// The mid-iteration quota-fallback feature relies on operators being able to
/// see which agents were tried via the /workitems/{id} read; without these
/// tests a regression that drops MapFallback, mangles a field, or breaks the
/// "omit when empty" branch would not be caught.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkItemFallbackHistoryEndpointTests : IDisposable
{
    private readonly FallbackHistoryApiFactory _factory = new();
    private readonly HttpClient _client;

    public WorkItemFallbackHistoryEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Get_WhenHistoryEmpty_ReturnsEmptyArray()
    {
        // Contract: when the fallback-history store is wired and a work item
        // has no recorded events, GET /workitems/{id} must emit
        // `"fallbackHistory": []`, not `null` and not "omitted". Consumers rely
        // on this to distinguish "no fallback happened" (empty array) from
        // "data was lost / store unavailable" (null on bulk-list endpoints
        // that don't query the store).
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("fallbackHistory", out var fh),
            "fallbackHistory must be present on the GET response when the store is wired");
        Assert.Equal(JsonValueKind.Array, fh.ValueKind);
        Assert.Equal(0, fh.GetArrayLength());
    }

    [Fact]
    public async Task Get_WhenHistoryHasSwapAndPark_ReturnsBothMappedRows()
    {
        // Two records: a successful Codex→Claude swap and an
        // all-members-exhausted park (ToAgent=null, ToModel=null). The
        // response must include both, with every MapFallback field correctly
        // populated. A column-swap regression in MapFallback (e.g.,
        // FromAgent.Value vs ToAgent.Value) would be caught here.
        var item = NewItem();
        await _factory.Store.CreateAsync(item);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var swap = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: item.Id,
            Phase: "work",
            Iteration: 2,
            FromAgent: AgentKind.Codex,
            FromModel: "gpt-5",
            ToAgent: AgentKind.Claude,
            ToModel: "claude-sonnet-4",
            Reason: "rate_limit_exceeded",
            OccurredAt: t0);
        var park = new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: item.Id,
            Phase: "rework",
            Iteration: 3,
            FromAgent: AgentKind.Claude,
            FromModel: "claude-sonnet-4",
            ToAgent: null,
            ToModel: null,
            Reason: "all members exhausted",
            OccurredAt: t0.AddMinutes(2));
        await _factory.FallbackHistory.RecordAsync(swap);
        await _factory.FallbackHistory.RecordAsync(park);

        var dto = await _client.GetFromJsonAsync<WorkItemDtoForTest>($"/workitems/{item.Id}");
        Assert.NotNull(dto);
        Assert.NotNull(dto!.FallbackHistory);
        Assert.Equal(2, dto.FallbackHistory!.Count);

        var swapDto = Assert.Single(dto.FallbackHistory, r => r.ToAgent == "claude");
        Assert.Equal("work", swapDto.Phase);
        Assert.Equal(2, swapDto.Iteration);
        Assert.Equal("codex", swapDto.FromAgent);
        Assert.Equal("gpt-5", swapDto.FromModel);
        Assert.Equal("claude-sonnet-4", swapDto.ToModel);
        Assert.Equal("rate_limit_exceeded", swapDto.Reason);
        Assert.Equal(swap.Id.ToString(), swapDto.Id);

        var parkDto = Assert.Single(dto.FallbackHistory, r => r.ToAgent is null);
        Assert.Equal("rework", parkDto.Phase);
        Assert.Equal("claude", parkDto.FromAgent);
        Assert.Null(parkDto.ToModel);
        Assert.Equal("all members exhausted", parkDto.Reason);
    }

    [Fact]
    public async Task Get_OnlyReturnsHistoryForRequestedWorkItem()
    {
        // Cross-talk guard: an unrelated work item's history must not appear
        // on the requested item's response.
        var requested = NewItem();
        var other = NewItem();
        await _factory.Store.CreateAsync(requested);
        await _factory.Store.CreateAsync(other);

        await _factory.FallbackHistory.RecordAsync(new AgentFallbackRecord(
            Id: Guid.NewGuid(),
            WorkItemId: other.Id,
            Phase: "work",
            Iteration: 1,
            FromAgent: AgentKind.Codex,
            FromModel: null,
            ToAgent: AgentKind.Claude,
            ToModel: null,
            Reason: "other-item",
            OccurredAt: DateTimeOffset.UtcNow));

        var dto = await _client.GetFromJsonAsync<WorkItemDtoForTest>($"/workitems/{requested.Id}");
        Assert.NotNull(dto);
        // FallbackHistory must be an empty array, never populated with the
        // other item's record.
        Assert.NotNull(dto!.FallbackHistory);
        Assert.Empty(dto.FallbackHistory!);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "fallback-history test",
        Prompt = "p",
        State = WorkItemState.Working,
    };

    private sealed record WorkItemDtoForTest(
        string Id,
        string ProjectId,
        string State,
        IReadOnlyList<FallbackRowForTest>? FallbackHistory);

    private sealed record FallbackRowForTest(
        string Id,
        string Phase,
        int? Iteration,
        string FromAgent,
        string? FromModel,
        string? ToAgent,
        string? ToModel,
        string Reason,
        DateTimeOffset OccurredAt);
}

/// <summary>
/// WebApplicationFactory that swaps the real SQLite-backed fallback-history
/// store for the in-memory variant so tests can pre-seed records without
/// hitting disk.
/// </summary>
internal sealed class FallbackHistoryApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-fbtest-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }
    public InMemoryAgentFallbackHistoryStore FallbackHistory { get; } = new();

    public FallbackHistoryApiFactory()
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
            services.RemoveAll<IAgentFallbackHistoryStore>();
            services.AddSingleton<IAgentFallbackHistoryStore>(FallbackHistory);
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
