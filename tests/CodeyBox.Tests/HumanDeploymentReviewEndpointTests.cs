using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// HTTP-level tests for the human deployment-review verdict surface:
///   GET  /workitems/{id}/deployment-review
///   POST /workitems/{id}/deployment-review/approve
///   POST /workitems/{id}/deployment-review/reject
/// plus the generic POST /answer path, which verdicts identically when it
/// addresses the backing review question.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class HumanDeploymentReviewEndpointTests : IDisposable
{
    private readonly HumanReviewEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public HumanDeploymentReviewEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<WorkItem> CreateItemAsync(WorkItemState state = WorkItemState.NeedsOperatorInput)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(HumanReviewEndpointFactory.ProjectId),
            Title = "Review me",
            Prompt = "serve the widget",
            State = state,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await _factory.WorkItemStore.CreateAsync(item);
        return item;
    }

    private async Task<HumanDeploymentReview> CreateReviewAsync(
        WorkItem item, int iteration = 1, TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var review = new HumanDeploymentReview
        {
            WorkItemId = item.Id.ToString(),
            Iteration = iteration,
            DeploymentId = "dep-9",
            EndpointJson = """{"Kind":0,"Url":"http://127.0.0.1:9999"}""",
            Deadline = now + (lifetime ?? TimeSpan.FromHours(1)),
            RequestedAt = now,
            Brief = "verify the widget against the criteria",
            QuestionId = HumanDeploymentReviewPolicy.QuestionIdFor(iteration),
            HumanAuditorsJson = """["human:deployment-review"]""",
            CodeFindingsJson = "[]",
            CodeCompletedJson = """["code:scripted"]""",
            AutomatedFindingsJson = "[]",
            AutomatedCompletedJson = """["deploy:smoke"]""",
            AutomatedIncompleteJson = "[]",
        };
        await _factory.ReviewStore.GetOrCreatePendingAsync(review);
        await _factory.QuestionStore.CreateIfNotExistsAsync(new WorkItemQuestion
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = item.Id.ToString(),
            QuestionId = review.QuestionId,
            QuestionText = review.Brief,
        });
        return review;
    }

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetReview_Pending_ReturnsEndpointAndBrief()
    {
        var item = await CreateItemAsync();
        var review = await CreateReviewAsync(item);

        var resp = await _client.GetAsync($"/workitems/{item.Id}/deployment-review");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(review.DeploymentId, body.GetProperty("deploymentId").GetString());
        Assert.Equal(1, body.GetProperty("iteration").GetInt32());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Contains("verify the widget", body.GetProperty("brief").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReview_None_Returns404()
    {
        var item = await CreateItemAsync();

        var resp = await _client.GetAsync($"/workitems/{item.Id}/deployment-review");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Approve_RecordsVerdict_AnswersQuestion_Resumes()
    {
        var item = await CreateItemAsync();
        var review = await CreateReviewAsync(item);

        var resp = await _client.PostAsync(
            $"/workitems/{item.Id}/deployment-review/approve", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", body.GetProperty("status").GetString());

        var decided = await _factory.ReviewStore.TryGetAsync(item.Id.ToString(), 1);
        Assert.Equal(HumanDeploymentReviewStatus.Approved, decided!.Status);
        var q = await _factory.QuestionStore.GetAsync(item.Id.ToString(), review.QuestionId);
        Assert.Equal("answered", q!.State);
        Assert.Equal("approve", q.AnswerText);
        var resumed = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
    }

    [Fact]
    public async Task Reject_RequiresNotes_AndRecordsThem()
    {
        var item = await CreateItemAsync();
        await CreateReviewAsync(item);

        var missing = await _client.PostAsync(
            $"/workitems/{item.Id}/deployment-review/reject", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var tooLong = await _client.PostAsync(
            $"/workitems/{item.Id}/deployment-review/reject",
            JsonBody(JsonSerializer.Serialize(new { notes = new string('x', 4001) })));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var ok = await _client.PostAsync(
            $"/workitems/{item.Id}/deployment-review/reject",
            JsonBody(JsonSerializer.Serialize(new { notes = "header is wrong" })));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var decided = await _factory.ReviewStore.TryGetAsync(item.Id.ToString(), 1);
        Assert.Equal(HumanDeploymentReviewStatus.Rejected, decided!.Status);
        Assert.Equal("header is wrong", decided.Notes);
    }

    [Fact]
    public async Task Approve_WhenExpired_Returns410AndFailsClosed()
    {
        var item = await CreateItemAsync();
        var review = await CreateReviewAsync(item, lifetime: TimeSpan.FromMinutes(-5));

        var resp = await _client.PostAsync(
            $"/workitems/{item.Id}/deployment-review/approve", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);

        var expired = await _factory.ReviewStore.TryGetAsync(item.Id.ToString(), 1);
        Assert.Equal(HumanDeploymentReviewStatus.Expired, expired!.Status);
        var q = await _factory.QuestionStore.GetAsync(item.Id.ToString(), review.QuestionId);
        Assert.Equal("dismissed", q!.State);
        var resumed = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
    }

    [Fact]
    public async Task GenericAnswer_Approve_VerdictsLikeDedicatedEndpoint()
    {
        var item = await CreateItemAsync();
        var review = await CreateReviewAsync(item);

        var resp = await _client.PostAsync(
            $"/workitems/{item.Id}/answer",
            JsonBody(JsonSerializer.Serialize(new
            {
                questionId = review.QuestionId,
                answer = "approve",
            })));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var decided = await _factory.ReviewStore.TryGetAsync(item.Id.ToString(), 1);
        Assert.Equal(HumanDeploymentReviewStatus.Approved, decided!.Status);
    }

    [Fact]
    public async Task GenericAnswer_OtherText_RejectsWithNotes()
    {
        var item = await CreateItemAsync();
        var review = await CreateReviewAsync(item);

        var resp = await _client.PostAsync(
            $"/workitems/{item.Id}/answer",
            JsonBody(JsonSerializer.Serialize(new
            {
                questionId = review.QuestionId,
                answer = "the footer overlaps on mobile",
            })));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var decided = await _factory.ReviewStore.TryGetAsync(item.Id.ToString(), 1);
        Assert.Equal(HumanDeploymentReviewStatus.Rejected, decided!.Status);
        Assert.Equal("the footer overlaps on mobile", decided.Notes);
        var resumed = await _factory.WorkItemStore.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
    }
}

internal sealed class HumanReviewEndpointFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-human-ep-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public SqliteWorkItemQuestionStore QuestionStore { get; }
    public SqliteHumanDeploymentReviewStore ReviewStore { get; }

    public HumanReviewEndpointFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        QuestionStore = new SqliteWorkItemQuestionStore(_dbPath);
        ReviewStore = new SqliteHumanDeploymentReviewStore(_dbPath);
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

            services.RemoveAll<IHumanDeploymentReviewStore>();
            services.AddSingleton<IHumanDeploymentReviewStore>(ReviewStore);

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
            ReviewStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        base.Dispose(disposing);
    }
}
