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
    public async Task PostWorkItems_CheckQuestionTooLarge_Returns400()
    {
        var oversized = new string('q', 64 * 1024 + 1);
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = oversized,
                onYes = new { title = "fix", prompt = "go" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("question", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesTitleTooLong_Returns400()
    {
        // Boundary: 201 chars must fail (cap is <= 200).
        var oversizedTitle = new string('t', 201);
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = oversizedTitle, prompt = "go" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes.title", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesTitleWithControlChars_Returns400()
    {
        // Validation.ValidateNoOptionLikeOrControl rejects \n / \r / \0 in
        // the follow-up title so the operator can't sneak log/audit-bending
        // characters into a downstream work item via the check spec.
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = "fix\nme", prompt = "go" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("control", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesTitleLeadingDash_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = "--fix-now", prompt = "go" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes.title", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesPromptTooLarge_Returns400()
    {
        var oversizedPrompt = new string('p', 64 * 1024 + 1);
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new { title = "fix", prompt = oversizedPrompt },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes.prompt", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesUnknownAgent_Returns400()
    {
        // The test app's agent registry does not include "ghostagent". The
        // endpoint must reject an unknown agent kind on the on-yes spec at
        // create time so the follow-up cannot fail later for the same reason.
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new
                {
                    title = "fix",
                    prompt = "go",
                    agent = "ghostagent",
                },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("unknown agent", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesAgentClassIdTooLong_Returns400()
    {
        var oversizedClass = new string('c', 201);
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new
                {
                    title = "fix",
                    prompt = "go",
                    agentClassId = oversizedClass,
                },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes.agentClassId", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_CheckOnYesDependsOnTooMany_Returns400()
    {
        // Cap is 100 entries; 101 must fail.
        var deps = Enumerable.Range(0, 101).Select(i => $"dep-{i}").ToArray();
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "bad",
            prompt = "x",
            check = new
            {
                question = "is x?",
                onYes = new
                {
                    title = "fix",
                    prompt = "go",
                    dependsOn = deps,
                },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes.dependsOn", err.GetProperty("error").GetString());
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

    [Fact]
    public async Task GetWorkItem_FollowupBackLink_SurfacesOriginCheckWorkItemId()
    {
        // The follow-up Normal item's back-link to its check (the field added
        // in this diff) must surface via the API DTO. A regression that
        // forgot to wire the column or used the wrong Id would only be
        // caught by reading the store directly otherwise.
        var checkId = WorkItemId.New();
        var followupId = WorkItemId.New();
        await _factory.Store.CreateAsync(new WorkItem
        {
            Id = checkId,
            ProjectId = new ProjectId("test-project"),
            Title = "the check",
            Prompt = "x",
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "is x?",
                OnYes = new OnYesActionSpec { Title = "fix", Prompt = "go" },
            },
        });
        await _factory.Store.CreateAsync(new WorkItem
        {
            Id = followupId,
            ProjectId = new ProjectId("test-project"),
            Title = "the follow-up",
            Prompt = "remediate",
            JobType = JobType.Normal,
            OriginCheckWorkItemId = checkId,
        });

        var get = await _client.GetAsync($"/workitems/{followupId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Normal", doc.GetProperty("jobType").GetString());
        Assert.Equal(checkId.ToString(), doc.GetProperty("originCheckWorkItemId").GetString());
    }
}
