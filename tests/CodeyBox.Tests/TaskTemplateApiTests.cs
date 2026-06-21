using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Orchestrator.Knobs;
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
                  "mode": "completion",
                  "prompt": "Inspect SQL construction only.",
                  "onYes": {
                    "title": "Fix SQL injection",
                    "prompt": "Replace unsafe SQL construction with parameters.",
                    "minModelScore": 70,
                    "priority": 100,
                    "agent": "codex",
                    "agentClassId": "secure-class",
                    "dependsOn": [ "ticket:SEC-1", "550e8400-e29b-41d4-a716-446655440000" ],
                    "knobs": { "changeScope": "refactor" }
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
        var queuedIds = new List<WorkItemId>();
        for (var i = 0; i < 2; i++)
        {
            var queuedId = await queue.DequeueAsync();
            Assert.True(queuedId.HasValue);
            queuedIds.Add(queuedId.Value);
        }
        Assert.Equal(stored.Select(item => item.Id).OrderBy(id => id.ToString()), queuedIds.OrderBy(id => id.ToString()));

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
        Assert.Equal(0, byIndex[0].TemplateEntryIndex);
        Assert.Equal(1, byIndex[1].TemplateEntryIndex);
        Assert.Equal("Inspect SQL construction only.", byIndex[0].Prompt);
        Assert.Equal("Is user input interpolated into SQL?", byIndex[0].Check!.Question);
        Assert.Equal("completion", byIndex[0].Check!.Mode);
        Assert.StartsWith("Check template entry 1:", byIndex[0].Title);
        Assert.True(byIndex[0].Check!.ActionableAnswer);
        Assert.Equal("Fix SQL injection", byIndex[0].Check!.OnYes.Title);
        Assert.Equal("Replace unsafe SQL construction with parameters.", byIndex[0].Check!.OnYes.Prompt);
        Assert.Equal(70, byIndex[0].Check!.OnYes.MinModelScore);
        Assert.Equal(100, byIndex[0].Check!.OnYes.Priority);
        Assert.Equal("codex", byIndex[0].Check!.OnYes.Agent);
        Assert.Equal("secure-class", byIndex[0].Check!.OnYes.AgentClassId);
        Assert.Equal(
            ["ticket:SEC-1", "550e8400-e29b-41d4-a716-446655440000"],
            byIndex[0].Check!.OnYes.DependsOn);
        Assert.Equal(ChangeScopeKnob.ValueRefactor, byIndex[0].Check!.OnYes.Knobs[ChangeScopeKnob.KeyName]);
        Assert.Equal("Check cookie attributes", byIndex[1].Title);
        Assert.Equal("agentic", byIndex[1].Check!.Mode);
        Assert.False(byIndex[1].Check!.ActionableAnswer);
        Assert.NotEqual(byIndex[0].Id, byIndex[1].Id);

        var firstDto = doc.GetProperty("workItems")[0];
        Assert.Equal("security", firstDto.GetProperty("templateName").GetString());
        Assert.Equal(0, firstDto.GetProperty("templateEntryIndex").GetInt32());
    }

    [Fact]
    public async Task QueueTemplate_PersistsTopLevelAgentClassAndRequiredCapabilities()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "test-project",
            agent = "codex",
            agentClassId = "frontier",
            requiredCapabilities = new[] { " sensitive ", "Sensitive", "", "architecture\t" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = Assert.Single(await ReadAllItemsAsync());
        Assert.Equal(new AgentKind("codex"), item.Agent);
        Assert.Equal("frontier", item.AgentClassId);
        Assert.Equal(["sensitive", "architecture"], item.RequiredCapabilities);
    }

    [Fact]
    public async Task ListTemplates_ReturnsDiscoveredTemplateSummariesAndErrors()
    {
        await WriteValidTemplateAsync("security");
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "broken.json"), """{"checks":[]}""");

        var response = await _client.GetAsync("/templates/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var byName = doc.EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        var good = byName["security"];
        Assert.Equal("templates/security.json", good.GetProperty("path").GetString());
        Assert.Equal(1, good.GetProperty("checkCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, good.GetProperty("error").ValueKind);

        var broken = byName["broken"];
        Assert.Equal(JsonValueKind.Null, broken.GetProperty("checkCount").ValueKind);
        Assert.Contains("at least one", broken.GetProperty("error").GetString());
    }

    [Fact]
    public async Task QueueTemplate_ByNameRoute_UsesRouteNameWhenBodyTemplateIsOmitted()
    {
        await WriteValidTemplateAsync("path-route");

        var response = await _client.PostAsJsonAsync("/templates/path-route/queue", new
        {
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("path-route", doc.GetProperty("template").GetString());
        Assert.Equal(1, doc.GetProperty("enqueued").GetInt32());

        var item = Assert.Single(await ReadAllItemsAsync());
        Assert.Equal("path-route", item.TemplateName);
        Assert.Equal(0, item.TemplateEntryIndex);
        Assert.Equal(new ProjectId("test-project"), item.ProjectId);
    }

    [Fact]
    public async Task QueueTemplate_ByNameRoute_AllowsMatchingBodyTemplate()
    {
        await WriteValidTemplateAsync("path-route");

        var response = await _client.PostAsJsonAsync("/templates/path-route/queue", new
        {
            template = "templates/path-route.json",
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("path-route", doc.GetProperty("template").GetString());

        var item = Assert.Single(await ReadAllItemsAsync());
        Assert.Equal("path-route", item.TemplateName);
        Assert.Equal(0, item.TemplateEntryIndex);
    }

    [Fact]
    public async Task QueueTemplate_ByNameRoute_RejectsConflictingBodyTemplate()
    {
        await WriteValidTemplateAsync("security");
        await WriteValidTemplateAsync("other");

        var response = await _client.PostAsJsonAsync("/templates/security/queue", new
        {
            template = "templates/other",
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("must match", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_InvalidTemplate_Returns400WithoutPartialEnqueue()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad.json"), """
            {
              "checks": [
                {
                  "question": "This valid entry must not be enqueued before later validation fails.",
                  "onYes": {
                    "title": "Fix first issue",
                    "prompt": "This should not be queued."
                  }
                },
                { "question": "This later entry has no action." }
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
    public async Task QueueTemplate_MissingTemplateInRequest_Returns400WithoutPartialEnqueue()
    {
        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("template is required", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_MissingProjectId_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("projectId is required", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_InvalidProjectId_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "bad project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("ProjectId", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_UnknownProjectId_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "missing-project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("unknown project", err.GetProperty("error").GetString());
        Assert.Contains("test-project", err.GetProperty("available").EnumerateArray().Select(v => v.GetString()));
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_UnknownTopLevelAgent_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "test-project",
            agent = "definitely-not-an-agent",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("unknown agent", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_TopLevelAgentClassIdTooLong_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "test-project",
            agentClassId = new string('c', 201),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("agentClassId", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Theory]
    [MemberData(nameof(InvalidRequiredCapabilitiesCases))]
    public async Task QueueTemplate_InvalidRequiredCapabilities_Returns400WithoutPartialEnqueue(
        string[] requiredCapabilities,
        string expectedMessage)
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "test-project",
            requiredCapabilities,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(expectedMessage, err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_PriorityOutsideGlobalRange_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "test-project",
            priority = 1001,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("priority", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_PriorityAboveProjectMax_Returns400WithoutPartialEnqueue()
    {
        await WriteValidTemplateAsync("security");
        var project = new Project
        {
            Id = new ProjectId("limited"),
            DisplayName = "Limited",
            RepositoryUrl = "https://github.com/test/limited",
            MaxPriority = 10,
        };

        using var factory = new WorkItemApiFactory(null, project) { TemplateDirectory = _templateDir };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "limited",
            priority = 11,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("maxPriority", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync(factory);
    }

    [Fact]
    public async Task QueueTemplate_OnYesPriorityAboveProjectMax_IsStoredForPipelineClamp()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad-followup-priority.json"), """
            {
              "checks": [
                {
                  "question": "Is the risky pattern present?",
                  "onYes": {
                    "title": "Fix risky pattern",
                    "prompt": "Remove the risky pattern.",
                    "priority": 11
                  }
                }
              ]
            }
            """);
        var project = new Project
        {
            Id = new ProjectId("limited"),
            DisplayName = "Limited",
            RepositoryUrl = "https://github.com/test/limited",
            MaxPriority = 10,
        };

        using var factory = new WorkItemApiFactory(null, project) { TemplateDirectory = _templateDir };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/templates/queue", new
        {
            template = "bad-followup-priority",
            projectId = "limited",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = Assert.Single(await ReadAllItemsAsync(factory.Store));
        Assert.Equal(11, item.Check!.OnYes.Priority);
        var queue = factory.Services.GetRequiredService<InMemoryTaskQueue>();
        Assert.Equal(1, queue.Count);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 200)]
    public async Task QueueTemplate_MinModelScore_IsClampedToSupportedRange(
        int requested,
        int expected)
    {
        await WriteValidTemplateAsync("security");

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "security",
            projectId = "test-project",
            minModelScore = requested,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = Assert.Single(await ReadAllItemsAsync());
        Assert.Equal(expected, item.MinModelScore);
    }

    [Fact]
    public async Task QueueTemplate_UnknownOnYesAgent_Returns400WithoutPartialEnqueue()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "bad-agent.json"), """
            {
              "checks": [
                {
                  "question": "Is the first risky pattern present?",
                  "onYes": {
                    "title": "Fix first risky pattern",
                    "prompt": "Remove the first risky pattern."
                  }
                },
                {
                  "question": "Is the risky pattern present?",
                  "onYes": {
                    "title": "Fix risky pattern",
                    "prompt": "Remove the risky pattern.",
                    "agent": "ghostagent"
                  }
                }
              ]
            }
            """);

        var response = await _client.PostAsJsonAsync("/templates/queue", new
        {
            template = "bad-agent",
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        var message = err.GetProperty("error").GetString();
        Assert.Contains("unknown agent", message);
        Assert.Contains("checks[1].onYes", message);
        await AssertNoItemsQueuedAsync();
    }

    [Fact]
    public async Task QueueTemplate_TooManyTemplateChecks_Returns400WithoutPartialEnqueue()
    {
        await File.WriteAllTextAsync(Path.Combine(_templateDir, "too-big.json"), """
            {
              "checks": [
                {
                  "question": "Is the first risky pattern present?",
                  "onYes": {
                    "title": "Fix first risky pattern",
                    "prompt": "Remove the first risky pattern."
                  }
                },
                {
                  "question": "Is the second risky pattern present?",
                  "onYes": {
                    "title": "Fix second risky pattern",
                    "prompt": "Remove the second risky pattern."
                  }
                }
              ]
            }
            """);

        using var factory = new WorkItemApiFactory { TemplateDirectory = _templateDir, MaxTemplateChecks = 1 };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/templates/queue", new
        {
            template = "too-big",
            projectId = "test-project",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("at most 1", err.GetProperty("error").GetString());
        await AssertNoItemsQueuedAsync(factory);
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

    public static IEnumerable<object[]> InvalidRequiredCapabilitiesCases()
    {
        yield return new object[]
        {
            Enumerable.Range(0, 17).Select(i => $"cap-{i}").ToArray(),
            "at most 16 entries",
        };
        yield return new object[] { new[] { new string('x', 65) }, "64 chars" };
        yield return new object[] { new[] { "sens\nitive" }, "control characters" };
    }

    private Task WriteValidTemplateAsync(string name) =>
        File.WriteAllTextAsync(Path.Combine(_templateDir, $"{name}.json"), """
            {
              "checks": [
                {
                  "question": "Is logging missing?",
                  "onYes": {
                    "title": "Add logging",
                    "prompt": "Add useful logs."
                  }
                }
              ]
            }
            """);

    private Task AssertNoItemsQueuedAsync() => AssertNoItemsQueuedAsync(_factory);

    private static async Task AssertNoItemsQueuedAsync(WorkItemApiFactory factory)
    {
        Assert.Empty(await ReadAllItemsAsync(factory.Store));
        var queue = factory.Services.GetRequiredService<InMemoryTaskQueue>();
        Assert.Equal(0, queue.Count);
    }

    private Task<List<WorkItem>> ReadAllItemsAsync() => ReadAllItemsAsync(_factory.Store);

    private static async Task<List<WorkItem>> ReadAllItemsAsync(IWorkItemStore store)
    {
        var items = new List<WorkItem>();
        await foreach (var item in store.ListAsync()) items.Add(item);
        return items;
    }
}
