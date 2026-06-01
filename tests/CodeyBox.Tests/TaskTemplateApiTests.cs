using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace CodeyBox.Tests;

public sealed class TaskTemplateApiTests : IDisposable
{
    private readonly string _templateDir = Directory.CreateTempSubdirectory("codeybox-api-templates-").FullName;
    private readonly WorkItemApiFactory _factory;
    private readonly HttpClient _client;

    public TaskTemplateApiTests()
    {
        _factory = new WorkItemApiFactory { TemplateDirectory = _templateDir };
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_templateDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task QueueTemplate_ExpandsEachCheckIntoIndependentQueuedCheckAndActItem()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "security.json"), """
            {
              "checks": [
                {
                  "question": "Is user input interpolated into SQL?",
                  "onYes": {
                    "title": "Fix SQL injection",
                    "prompt": "Replace unsafe SQL construction with parameters.",
                    "priority": 100
                  }
                },
                {
                  "title": "Check cookie attributes",
                  "question": "Are auth cookies missing SameSite?",
                  "actionableAnswer": false,
                  "onYes": {
                    "title": "Add SameSite cookie settings",
                    "prompt": "Set secure SameSite attributes on auth cookies."
                  }
                }
              ]
            }
            """);

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "templates/security",
            projectId = "test-project",
            priority = 25,
            minModelScore = 80,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("security", doc.GetProperty("template").GetString());
        Assert.Equal(2, doc.GetProperty("enqueued").GetInt32());
        Assert.Equal(2, doc.GetProperty("workItems").GetArrayLength());

        var queue = _factory.Services.GetRequiredService<InMemoryTaskQueue>();
        Assert.Equal(2, queue.Count);

        var stored = new List<WorkItem>();
        await foreach (var item in _factory.Store.ListAsync()) stored.Add(item);

        Assert.Equal(2, stored.Count);
        Assert.All(stored, item =>
        {
            Assert.Equal(JobType.CheckAndAct, item.JobType);
            Assert.Equal("security", item.TemplateName);
            Assert.Equal(new ProjectId("test-project"), item.ProjectId);
            Assert.Equal(25, item.Priority);
            Assert.Equal(80, item.MinModelScore);
            Assert.Null(item.OriginCheckWorkItemId);
            Assert.Empty(item.DependsOn);
        });

        var byIndex = stored.ToDictionary(item => item.TemplateEntryIndex!.Value);
        Assert.Equal("Is user input interpolated into SQL?", byIndex[0].Check!.Question);
        Assert.True(byIndex[0].Check!.ActionableAnswer);
        Assert.Equal("Fix SQL injection", byIndex[0].Check!.OnYes.Title);
        Assert.Equal(100, byIndex[0].Check!.OnYes.Priority);
        Assert.Equal("Check cookie attributes", byIndex[1].Title);
        Assert.False(byIndex[1].Check!.ActionableAnswer);
        Assert.NotEqual(byIndex[0].Id, byIndex[1].Id);

        var firstDto = doc.GetProperty("workItems")[0];
        Assert.Equal("security", firstDto.GetProperty("templateName").GetString());
        Assert.Equal(0, firstDto.GetProperty("templateEntryIndex").GetInt32());
    }

    [Fact]
    public async Task QueueTemplate_InvalidTemplate_Returns400WithoutPartialEnqueue()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad.json"), """
            {
              "checks": [
                { "question": "This entry has no action." }
              ]
            }
            """);

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "bad",
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("onYes", err.GetProperty("error").GetString());

        var stored = new List<WorkItem>();
        await foreach (var item in _factory.Store.ListAsync()) stored.Add(item);
        Assert.Empty(stored);

        var queue = _factory.Services.GetRequiredService<InMemoryTaskQueue>();
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task QueueTemplate_MissingTemplate_Returns404WithoutPartialEnqueue()
    {
        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "missing",
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ReadAllItemsAsync());
    }

    private async Task<List<WorkItem>> ReadAllItemsAsync()
    {
        var items = new List<WorkItem>();
        await foreach (var item in _factory.Store.ListAsync()) items.Add(item);
        return items;
    }
}
