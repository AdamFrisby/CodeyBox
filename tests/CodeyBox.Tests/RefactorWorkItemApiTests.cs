using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class RefactorWorkItemApiTests : IDisposable
{
    private readonly WorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public RefactorWorkItemApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task PostWorkItems_WithIsRefactor_CreatesRefactorItem()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "refactor auth module",
            prompt = "refactor the auth module",
            isRefactor = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Refactor", doc.GetProperty("jobType").GetString());
        Assert.False(doc.TryGetProperty("check", out _));
        Assert.False(doc.TryGetProperty("agentControl", out _));

        var id = WorkItemId.Parse(doc.GetProperty("id").GetString()!);
        var stored = await _factory.Store.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(JobType.Refactor, stored!.JobType);
        Assert.Null(stored.Check);
        Assert.Null(stored.AgentControl);
    }

    [Fact]
    public async Task PostWorkItems_CheckAndIsRefactor_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "ambiguous work",
            prompt = "do it",
            isRefactor = true,
            check = new
            {
                question = "Is this needed?",
                onYes = new { title = "follow up", prompt = "fix it" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("check and isRefactor", err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostWorkItems_AgentControlAndIsRefactor_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "ambiguous control",
            prompt = "pause claude",
            isRefactor = true,
            agentControl = new
            {
                action = "pause",
                agent = "claude",
                reason = "reserve quota",
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("agentControl and isRefactor", err.GetProperty("error").GetString());
    }
}
