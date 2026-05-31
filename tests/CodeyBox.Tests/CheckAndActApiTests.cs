using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for POST /workitems accepting the optional <c>check</c> block to
/// create a <see cref="JobType.CheckAndAct"/> item. The API must:
///   - persist the check spec verbatim on the work item
///   - default ActionableAnswer to true
///   - reject bad shapes (missing question, missing onYes, oversize fields)
///   - surface the verdict (when present) in GET /workitems/{id}
/// </summary>
public sealed class CheckAndActApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public CheckAndActApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PostWorkItems_WithCheckBlock_Creates_CheckAndAct_Item()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "Check for SQL injection",
            prompt = "evaluate the repo",
            check = new
            {
                question = "Is any user-facing SQL built via string concatenation / interpolation (SQL-injection risk)?",
                actionableAnswer = true,
                onYes = new
                {
                    title = "Fix all SQL injection vulnerabilities and verify none remain",
                    prompt = "Remediate all SQL string interpolation.",
                    minModelScore = 50,
                    priority = 100,
                },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CheckAndAct", doc.GetProperty("jobType").GetString());
        var check = doc.GetProperty("check");
        Assert.Contains("SQL", check.GetProperty("question").GetString());
        Assert.True(check.GetProperty("actionableAnswer").GetBoolean());
        Assert.Equal("Fix all SQL injection vulnerabilities and verify none remain",
            check.GetProperty("onYes").GetProperty("title").GetString());

        // Persisted: re-read by ID and confirm.
        var id = doc.GetProperty("id").GetString()!;
        var stored = await _factory.Store.GetAsync(new WorkItemId(Guid.Parse(id)));
        Assert.NotNull(stored);
        Assert.Equal(JobType.CheckAndAct, stored!.JobType);
        Assert.NotNull(stored.Check);
        Assert.True(stored.Check!.ActionableAnswer);
        Assert.Equal(100, stored.Check.OnYes.Priority);
        Assert.Equal(50, stored.Check.OnYes.MinModelScore);
    }

    [Fact]
    public async Task PostWorkItems_WithoutCheckBlock_DefaultsToNormal()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "regular work item",
            prompt = "do the thing",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Normal", doc.GetProperty("jobType").GetString());
        // The check property is omitted (WhenWritingNull on the DTO).
        Assert.False(doc.TryGetProperty("check", out _));
    }

    [Fact]
    public async Task PostWorkItems_CheckWithoutQuestion_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "",
                onYes = new { title = "fix", prompt = "go" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("question", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckWithoutOnYes_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new { question = "is x?" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesMissingTitleOrPrompt_Returns400()
    {
        var noTitle = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = "", prompt = "y" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);

        var noPrompt = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = "fix", prompt = "" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noPrompt.StatusCode);
    }

    [Fact]
    public async Task PostWorkItems_CheckBlock_ActionableAnswerDefaultsTrue()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "check",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = "fix", prompt = "go" },
                // actionableAnswer intentionally omitted
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.GetProperty("check").GetProperty("actionableAnswer").GetBoolean());
    }

    [Fact]
    public async Task GetWorkItem_AfterVerdictPersisted_ReturnsVerdictField()
    {
        // Direct-store mutation: simulate the pipeline persisting a verdict on
        // the work item without running an actual sandbox. The API DTO must
        // surface the persisted verdict + origin-check linkage.
        var checkId = WorkItemId.New();
        var check = new WorkItem
        {
            Id = checkId,
            ProjectId = new ProjectId("test-project"),
            Title = "stored check",
            Prompt = "x",
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                OnYes = new OnYesActionSpec { Title = "fix", Prompt = "go" },
            },
            Verdict = new CheckVerdict
            {
                Answer = true,
                Evidence = "found vulnerability in src/Foo.cs",
                Confidence = "high",
            },
        };
        await _factory.Store.CreateAsync(check);

        var get = await _client.GetAsync($"/workitems/{checkId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CheckAndAct", doc.GetProperty("jobType").GetString());
        var verdict = doc.GetProperty("verdict");
        Assert.True(verdict.GetProperty("answer").GetBoolean());
        Assert.Contains("Foo.cs", verdict.GetProperty("evidence").GetString());
        Assert.Equal("high", verdict.GetProperty("confidence").GetString());
    }
}
