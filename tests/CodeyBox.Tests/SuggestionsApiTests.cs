using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for /suggestions endpoints: list, get, dismiss (PATCH),
/// and promote (POST /{id}/promote).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class SuggestionsApiTests : IDisposable
{
    private readonly SuggestionsApiFactory _factory = new();
    private readonly HttpClient _client;

    public SuggestionsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Suggestion MakeSuggestion(
        string? projectId = null,
        string category = "test-coverage",
        string severity = "minor") => new()
        {
            Id = Guid.NewGuid().ToString(),
            SourceWorkItemId = Guid.NewGuid().ToString(),
            ProjectId = projectId ?? SuggestionsApiFactory.ProjectId,
            Title = "Add tests",
            Rationale = "Missing coverage",
            Category = category,
            Severity = severity,
            EstimatedEffort = "small",
            CreatedAt = DateTimeOffset.UtcNow,
            State = "open",
        };

    // ── GET /suggestions (paginated) ─────────────────────────────────────────

    [Fact]
    public async Task GetSuggestions_EmptyStore_ReturnsEmptyPage()
    {
        var resp = await _client.GetAsync("/suggestions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task GetSuggestions_WithOpenSuggestion_ReturnsSingleItem()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.GetAsync("/suggestions");
        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Single(page!.Items);
        Assert.Equal(s.Id, page.Items[0].Id);
        Assert.Equal(s.Title, page.Items[0].Title);
        Assert.Equal(s.Rationale, page.Items[0].Rationale);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task GetSuggestions_DismissedSuggestion_NotIncluded()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);
        await _factory.SuggestionStore.UpdateAsync(s with { State = "dismissed" });

        var resp = await _client.GetAsync("/suggestions");
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task GetSuggestions_CategoryFilter_OnlyMatchingCategory()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(category: "security"));
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(category: "docs"));

        var resp = await _client.GetAsync("/suggestions?category=security");
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Single(page!.Items);
        Assert.Equal("security", page.Items[0].Category);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task GetSuggestions_SeverityFilter_OnlyMatchingSeverity()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(severity: "important"));
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(severity: "minor"));

        var resp = await _client.GetAsync("/suggestions?severity=important");
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Single(page!.Items);
        Assert.Equal("important", page.Items[0].Severity);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task GetSuggestions_LimitAndOffset_Paginate()
    {
        for (var i = 0; i < 5; i++)
            await _factory.SuggestionStore.CreateAsync(MakeSuggestion());

        var resp = await _client.GetAsync("/suggestions?limit=2&offset=0");
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(5, page.Total);
    }

    // ── GET /suggestions/count ────────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestionsCount_EmptyStore_ReturnsZero()
    {
        var resp = await _client.GetAsync("/suggestions/count");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CountResponse>();
        Assert.Equal(0, body!.Count);
    }

    [Fact]
    public async Task GetSuggestionsCount_WithOpenSuggestions_ReturnsCount()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion());
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion());

        var resp = await _client.GetAsync("/suggestions/count");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CountResponse>();
        Assert.Equal(2, body!.Count);
    }

    [Fact]
    public async Task GetSuggestionsCount_ProjectFilter_CountsOnlyMatchingProject()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(projectId: SuggestionsApiFactory.ProjectId));
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(projectId: "other-project"));

        var resp = await _client.GetAsync($"/suggestions/count?project={SuggestionsApiFactory.ProjectId}");
        var body = await resp.Content.ReadFromJsonAsync<CountResponse>();
        Assert.Equal(1, body!.Count);
    }

    // ── GET /suggestions?project= ─────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestions_ProjectFilter_OnlyMatchingProject()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(projectId: SuggestionsApiFactory.ProjectId));
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(projectId: "other-project"));

        var resp = await _client.GetAsync($"/suggestions?project={SuggestionsApiFactory.ProjectId}");
        var page = await resp.Content.ReadFromJsonAsync<PagedSuggestionsResult>();
        Assert.Single(page!.Items);
        Assert.Equal(SuggestionsApiFactory.ProjectId, page.Items[0].ProjectId);
        Assert.Equal(1, page.Total);
    }

    // ── limit/offset validation ───────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestions_LimitZero_Returns400()
    {
        var resp = await _client.GetAsync("/suggestions?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSuggestions_LimitAbove500_Returns400()
    {
        var resp = await _client.GetAsync("/suggestions?limit=501");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSuggestions_NegativeOffset_Returns400()
    {
        var resp = await _client.GetAsync("/suggestions?offset=-1");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── GET /suggestions/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestionById_Exists_ReturnsDto()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.GetAsync($"/suggestions/{s.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SuggestionResponse>();
        Assert.Equal(s.Id, body!.Id);
        Assert.Equal(s.Rationale, body.Rationale);
        Assert.Equal(s.ProjectId, body.ProjectId);
    }

    [Fact]
    public async Task GetSuggestionById_Missing_Returns404()
    {
        var resp = await _client.GetAsync("/suggestions/no-such-id");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── PATCH /suggestions/{id} (dismiss) ─────────────────────────────────────

    [Fact]
    public async Task PatchSuggestion_Dismiss_UpdatesStateAndReason()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PatchAsJsonAsync(
            $"/suggestions/{s.Id}",
            new { state = "dismissed", dismissReason = "not relevant" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var got = await _factory.SuggestionStore.GetAsync(s.Id);
        Assert.Equal("dismissed", got!.State);
        Assert.Equal("not relevant", got.DismissReason);
    }

    [Fact]
    public async Task PatchSuggestion_DismissNoReason_Works()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PatchAsJsonAsync(
            $"/suggestions/{s.Id}",
            new { state = "dismissed" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var got = await _factory.SuggestionStore.GetAsync(s.Id);
        Assert.Equal("dismissed", got!.State);
        Assert.Null(got.DismissReason);
    }

    [Fact]
    public async Task PatchSuggestion_InvalidState_Returns400()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PatchAsJsonAsync(
            $"/suggestions/{s.Id}",
            new { state = "open" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PatchSuggestion_AlreadyDismissed_Returns409()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);
        await _factory.SuggestionStore.UpdateAsync(s with { State = "dismissed" });

        var resp = await _client.PatchAsJsonAsync(
            $"/suggestions/{s.Id}",
            new { state = "dismissed" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task PatchSuggestion_Missing_Returns404()
    {
        var resp = await _client.PatchAsJsonAsync(
            "/suggestions/no-such-id",
            new { state = "dismissed" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PatchSuggestion_ReasonTooLong_Returns400()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);
        var longReason = new string('x', 501);

        var resp = await _client.PatchAsJsonAsync(
            $"/suggestions/{s.Id}",
            new { state = "dismissed", dismissReason = longReason });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── POST /suggestions/{id}/promote ────────────────────────────────────────

    [Fact]
    public async Task PromoteSuggestion_AlreadyAccepted_Returns409()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);
        await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task PromoteSuggestion_DismissedSuggestion_Returns409()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);
        await _factory.SuggestionStore.UpdateAsync(s with { State = "dismissed" });

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task PromoteSuggestion_Missing_Returns404()
    {
        var resp = await _client.PostAsJsonAsync("/suggestions/no-such-id/promote", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Local response shapes ─────────────────────────────────────────────────

    private sealed record CountResponse(int Count);

    private sealed record PagedSuggestionsResult(
        List<SuggestionResponse> Items,
        int Total,
        int Offset,
        int Limit);

    private sealed record SuggestionResponse(
        string Id,
        string SourceWorkItemId,
        string ProjectId,
        string Title,
        string Rationale,
        string Category,
        string Severity,
        string EstimatedEffort,
        IReadOnlyList<string>? FilesReferenced,
        DateTimeOffset CreatedAt,
        string State,
        string? DismissReason,
        string? PromotedToWorkItemId);

    private sealed record PromoteResponseBody(string WorkItemId, SuggestionResponse Suggestion);
}

/// <summary>
/// WebApplicationFactory variant that injects an isolated SqliteSuggestionStore
/// and SqliteWorkItemStore backed by the same temp database.
/// </summary>
internal sealed class SuggestionsApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    public const string ProjectId = "test-project";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-suggestionshttp-{Guid.NewGuid():N}.db");

    public SqliteSuggestionStore SuggestionStore { get; }
    public SqliteWorkItemStore WorkItemStore { get; }

    public SuggestionsApiFactory()
    {
        SuggestionStore = new SqliteSuggestionStore(_dbPath);
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
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
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            services.RemoveAll<ISuggestionStore>();
            services.AddSingleton<ISuggestionStore>(SuggestionStore);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new Core.ProjectId(ProjectId),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                }));
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(
            disposing,
            _dbPath,
            SuggestionStore.Dispose,
            WorkItemStore.Dispose);
}
