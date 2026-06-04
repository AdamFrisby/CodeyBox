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
/// HTTP-level tests for the agent-question endpoints:
///   GET  /workitems/{id}/questions
///   POST /workitems/{id}/answer
///   POST /workitems/{id}/dismiss-question
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AnswerEndpointTests : IDisposable
{
    private readonly AnswerEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public AnswerEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<WorkItem> CreateWorkItemAsync(WorkItemState state = WorkItemState.NeedsOperatorInput)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(AnswerEndpointFactory.ProjectId),
            Title = "Test item",
            Prompt = "do something",
            State = state,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _factory.WorkItemStore.CreateAsync(item);
        return item;
    }

    private async Task<WorkItemQuestion> CreateQuestionAsync(WorkItem item, string questionId = "q-001")
    {
        var q = new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = item.Id.ToString(),
            QuestionId = questionId,
            QuestionText = $"What approach for {questionId}?",
        };
        await _factory.QuestionStore.CreateIfNotExistsAsync(q);
        return q;
    }

    // ── GET /workitems/{id}/questions ────────────────────────────────────────

    [Fact]
    public async Task GetQuestions_NoQuestions_ReturnsEmptyList()
    {
        var item = await CreateWorkItemAsync();

        var resp = await _client.GetAsync($"/workitems/{item.Id}/questions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<List<object>>();
        Assert.Empty(body!);
    }

    [Fact]
    public async Task GetQuestions_WithQuestion_ReturnsList()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.GetAsync($"/workitems/{item.Id}/questions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.Single(body!);
        Assert.Equal("q-001", body![0].GetProperty("questionId").GetString());
        Assert.Equal("open", body![0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetQuestions_UnknownWorkItem_Returns404()
    {
        var resp = await _client.GetAsync($"/workitems/{Guid.NewGuid()}/questions");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── POST /workitems/{id}/answer ──────────────────────────────────────────

    [Fact]
    public async Task AnswerQuestion_HappyPath_Returns200()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = "Use approach B." });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task AnswerQuestion_MarksQuestionAnswered()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = "Use approach B." });

        var q = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("answered", q!.State);
        Assert.Equal("Use approach B.", q.AnswerText);
    }

    [Fact]
    public async Task AnswerQuestion_AllAnswered_TransitionsToWorkComplete()
    {
        var item = await CreateWorkItemAsync(WorkItemState.NeedsOperatorInput);
        await CreateQuestionAsync(item, "q-001");

        await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = "Proceed with A." });

        var updated = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, updated!.State);
    }

    [Fact]
    public async Task AnswerQuestion_StillOpenQuestions_StaysAtNeedsOperatorInput()
    {
        var item = await CreateWorkItemAsync(WorkItemState.NeedsOperatorInput);
        await CreateQuestionAsync(item, "q-001");
        await CreateQuestionAsync(item, "q-002");

        // Answer only the first.
        await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = "OK." });

        var updated = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, updated!.State);
    }

    [Fact]
    public async Task Retry_WhenOpenQuestionExists_ReturnsConflictAndLeavesItemParked()
    {
        var item = await CreateWorkItemAsync(WorkItemState.NeedsOperatorInput);
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.PostAsync($"/workitems/{item.Id}/retry", content: null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var updated = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, updated!.State);
    }

    [Fact]
    public async Task AnswerQuestion_AlreadyAnswered_IsNoOp()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");
        await _factory.QuestionStore.AnswerAsync(item.Id.ToString(), "q-001", "First answer.", null);

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = "Second answer (should be ignored)." });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("no-op", body.GetProperty("status").GetString());

        // Original answer preserved.
        var q = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("First answer.", q!.AnswerText);
    }

    [Fact]
    public async Task AnswerQuestion_UnknownQuestionId_Returns404()
    {
        var item = await CreateWorkItemAsync();

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "nonexistent", answer = "Answer." });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AnswerQuestion_UnknownWorkItem_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{Guid.NewGuid()}/answer",
            new { questionId = "q-001", answer = "Answer." });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AnswerQuestion_MissingQuestionId_Returns400()
    {
        var item = await CreateWorkItemAsync();

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "", answer = "Answer." });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── POST /workitems/{id}/dismiss-question ────────────────────────────────

    [Fact]
    public async Task DismissQuestion_HappyPath_Returns200()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/dismiss-question",
            new { questionId = "q-001", reason = "Out of scope." });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task DismissQuestion_MarksDismissed()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/dismiss-question",
            new { questionId = "q-001", reason = "Out of scope." });

        var q = await _factory.QuestionStore.GetAsync(item.Id.ToString(), "q-001");
        Assert.Equal("dismissed", q!.State);
        Assert.Equal("Out of scope.", q.DismissReason);
    }

    [Fact]
    public async Task DismissQuestion_AllResolved_TransitionsToWorkComplete()
    {
        var item = await CreateWorkItemAsync(WorkItemState.NeedsOperatorInput);
        await CreateQuestionAsync(item, "q-001");

        await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/dismiss-question",
            new { questionId = "q-001", reason = "Not needed." });

        var updated = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, updated!.State);
    }

    [Fact]
    public async Task DismissQuestion_MissingReason_Returns400()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/dismiss-question",
            new { questionId = "q-001", reason = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DismissQuestion_UnknownQuestionId_Returns404()
    {
        var item = await CreateWorkItemAsync();

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/dismiss-question",
            new { questionId = "no-such", reason = "reason." });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AnswerQuestion_OversizedAnswer_Returns400()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/answer",
            new { questionId = "q-001", answer = new string('x', 4001) });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DismissQuestion_OversizedReason_Returns400()
    {
        var item = await CreateWorkItemAsync();
        await CreateQuestionAsync(item, "q-001");

        var resp = await _client.PostAsJsonAsync(
            $"/workitems/{item.Id}/dismiss-question",
            new { questionId = "q-001", reason = new string('x', 501) });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

internal sealed class AnswerEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-answer-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }

    public AnswerEndpointFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        QuestionStore = new SqliteWorkItemQuestionStore(_dbPath);
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

            services.RemoveAll<IWorkItemQuestionStore>();
            services.AddSingleton<IWorkItemQuestionStore>(QuestionStore);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new CodeyBox.Core.ProjectId(ProjectId),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    DefaultAgent = AgentKind.Claude,
                    DefaultBaseBranch = "main",
                    AllowAgentQuestions = true,
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            QuestionStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
