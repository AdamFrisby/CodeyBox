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

    // ── GET /suggestions ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestions_EmptyStore_ReturnsEmptyArray()
    {
        var resp = await _client.GetAsync("/suggestions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var items = await resp.Content.ReadFromJsonAsync<List<SuggestionResponse>>();
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetSuggestions_WithOpenSuggestion_ReturnsSingleItem()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.GetAsync("/suggestions");
        resp.EnsureSuccessStatusCode();
        var items = await resp.Content.ReadFromJsonAsync<List<SuggestionResponse>>();
        Assert.Single(items!);
        Assert.Equal(s.Id, items![0].Id);
        Assert.Equal(s.Title, items[0].Title);
        Assert.Equal(s.Rationale, items[0].Rationale);
    }

    [Fact]
    public async Task GetSuggestions_DismissedSuggestion_NotIncluded()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);
        await _factory.SuggestionStore.UpdateAsync(s with { State = "dismissed" });

        var resp = await _client.GetAsync("/suggestions");
        var items = await resp.Content.ReadFromJsonAsync<List<SuggestionResponse>>();
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetSuggestions_CategoryFilter_OnlyMatchingCategory()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(category: "security"));
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(category: "docs"));

        var resp = await _client.GetAsync("/suggestions?category=security");
        var items = await resp.Content.ReadFromJsonAsync<List<SuggestionResponse>>();
        Assert.Single(items!);
        Assert.Equal("security", items![0].Category);
    }

    [Fact]
    public async Task GetSuggestions_SeverityFilter_OnlyMatchingSeverity()
    {
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(severity: "important"));
        await _factory.SuggestionStore.CreateAsync(MakeSuggestion(severity: "minor"));

        var resp = await _client.GetAsync("/suggestions?severity=important");
        var items = await resp.Content.ReadFromJsonAsync<List<SuggestionResponse>>();
        Assert.Single(items!);
        Assert.Equal("important", items![0].Severity);
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
    public async Task PromoteSuggestion_CreatesWorkItemAndTransitionsToAccepted()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<PromoteResponseBody>();
        Assert.NotNull(body);
        Assert.NotNull(body.WorkItemId);
        Assert.Equal("accepted", body.Suggestion.State);
        Assert.Equal(body.WorkItemId, body.Suggestion.PromotedToWorkItemId);
    }

    [Fact]
    public async Task PromoteSuggestion_WorkItemHasCorrectPrompt()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteResponseBody>();

        // Find the created work item via GET (the store is a direct reference)
        var wi = await _factory.WorkItemStore.GetAsync(WorkItemId.Parse(body!.WorkItemId));
        Assert.NotNull(wi);
        Assert.StartsWith("# From suggestion:", wi.Prompt);
        Assert.Contains(s.Title, wi.Prompt);
        Assert.Contains(s.Rationale, wi.Prompt);
        Assert.Equal(s.Title, wi.Title);
    }

    [Fact]
    public async Task PromoteSuggestion_SuggestionLinkedToWorkItem()
    {
        var s = MakeSuggestion();
        await _factory.SuggestionStore.CreateAsync(s);

        var resp = await _client.PostAsJsonAsync($"/suggestions/{s.Id}/promote", new { });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PromoteResponseBody>();

        var got = await _factory.SuggestionStore.GetAsync(s.Id);
        Assert.Equal("accepted", got!.State);
        Assert.Equal(body!.WorkItemId, got.PromotedToWorkItemId);
    }

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
internal sealed class SuggestionsApiFactory : WebApplicationFactory<Program>
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
            var tmp = Path.GetTempPath();
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
    {
        if (disposing)
        {
            SuggestionStore.Dispose();
            WorkItemStore.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
