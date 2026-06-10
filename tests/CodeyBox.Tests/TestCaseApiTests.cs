using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Api;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class TestCaseApiTests : IDisposable
{
    private readonly TestCaseApiFactory _factory = new();
    private readonly HttpClient _client;

    public TestCaseApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> SeedWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(TestCaseApiFactory.ProjectId),
            Title = "Linked WorkItem",
            Prompt = "Some prompt"
        };
        await _factory.WorkItemStore.CreateAsync(item);
        return item.Id.ToString();
    }

    [Fact]
    public async Task CreateTestCase_ValidRequest_ReturnsCreated()
    {
        var wid = await SeedWorkItemAsync();
        var req = new CreateTestCaseRequest(
            Id: "tc-api-1",
            Name: "Sample Test Case",
            Description: "Verifies standard behaviour",
            SourceWorkItemId: wid,
            AutomationKind: AutomationKind.E2eReplay,
            ExecutableArtifactJson: "{\"steps\": []}",
            ConformanceJson: "{\"brokenBranch\": \"main\"}",
            Label: "api-test"
        );

        var resp = await _client.PostAsJsonAsync("/testcases", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<TestCaseDto>();
        Assert.NotNull(body);
        Assert.Equal("tc-api-1", body.Id);
        Assert.Equal("Sample Test Case", body.Name);
        Assert.Equal("Verifies standard behaviour", body.Description);
        Assert.Equal(wid, body.SourceWorkItemId);
        Assert.Equal(AutomationKind.E2eReplay, body.AutomationKind);
        Assert.Equal("{\"steps\": []}", body.ExecutableArtifactJson);
        Assert.Equal("{\"brokenBranch\": \"main\"}", body.ConformanceJson);
        Assert.Equal("api-test", body.Label);
    }

    [Fact]
    public async Task CreateTestCase_InvalidWorkItemIdFormat_ReturnsBadRequest()
    {
        var req = new CreateTestCaseRequest(
            Id: "tc-api-2",
            Name: "Sample Case",
            Description: "",
            SourceWorkItemId: "not-a-guid"
        );

        var resp = await _client.PostAsJsonAsync("/testcases", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateTestCase_NonExistentWorkItem_ReturnsBadRequest()
    {
        var req = new CreateTestCaseRequest(
            Id: "tc-api-3",
            Name: "Sample Case",
            Description: "",
            SourceWorkItemId: Guid.NewGuid().ToString()
        );

        var resp = await _client.PostAsJsonAsync("/testcases", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task BulkCreateTestCase_ValidRequest_CreatesAll()
    {
        var wid = await SeedWorkItemAsync();
        var req1 = new CreateTestCaseRequest("bulk-1", "Bulk Case 1", "", wid);
        var req2 = new CreateTestCaseRequest("bulk-2", "Bulk Case 2", "", wid);

        var resp = await _client.PostAsJsonAsync("/testcases/bulk", new[] { req1, req2 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var list = await resp.Content.ReadFromJsonAsync<List<TestCaseDto>>();
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.Id == "bulk-1");
        Assert.Contains(list, x => x.Id == "bulk-2");

        var storeList = new List<TestCase>();
        await foreach (var tc in _factory.TestCaseStore.ListByWorkItemAsync(wid))
            storeList.Add(tc);

        Assert.Equal(2, storeList.Count);
    }

    [Fact]
    public async Task CreateTestCase_HyphenatedWorkItemId_NormalisesAndSucceeds()
    {
        var wid = await SeedWorkItemAsync();
        var g = Guid.Parse(wid);
        var hyphenatedWid = g.ToString("D");

        var req = new CreateTestCaseRequest(
            Id: "tc-hyphen-1",
            Name: "Sample Test Case",
            Description: "Verifies hyphenated GUID behaviour",
            SourceWorkItemId: hyphenatedWid
        );

        var resp = await _client.PostAsJsonAsync("/testcases", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<TestCaseDto>();
        Assert.NotNull(body);
        Assert.Equal(wid, body.SourceWorkItemId);
    }

    [Fact]
    public async Task BulkCreateTestCase_ExceedsMaxLimit_ReturnsBadRequest()
    {
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:MaxBulkItems"] = "2"
                });
            });
        });
        using var client = customFactory.CreateClient();

        var wid = await SeedWorkItemAsync();
        var req1 = new CreateTestCaseRequest("bulk-limit-1", "Bulk Case 1", "", wid);
        var req2 = new CreateTestCaseRequest("bulk-limit-2", "Bulk Case 2", "", wid);
        var req3 = new CreateTestCaseRequest("bulk-limit-3", "Bulk Case 3", "", wid);

        var resp = await client.PostAsJsonAsync("/testcases/bulk", new[] { req1, req2, req3 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var err = await resp.Content.ReadAsStringAsync();
        Assert.Contains("exceeds maximum limit", err);
    }

    [Fact]
    public async Task GetTestCase_NonExistent_ReturnsNotFound()
    {
        var resp = await _client.GetAsync("/testcases/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetTestCase_Exists_ReturnsTestCase()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-get-1",
            Name = "Get Case",
            Description = "Should be retrieved",
            SourceWorkItemId = wid
        };
        await _factory.TestCaseStore.CreateAsync(tc);

        var resp = await _client.GetAsync("/testcases/tc-get-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<TestCaseDto>();
        Assert.NotNull(body);
        Assert.Equal("tc-get-1", body.Id);
        Assert.Equal("Get Case", body.Name);
    }

    [Fact]
    public async Task UpdateTestCase_ValidRequest_UpdatesSuccessfully()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-up-1",
            Name = "Old Name",
            Description = "Old Desc",
            SourceWorkItemId = wid
        };
        await _factory.TestCaseStore.CreateAsync(tc);

        var req = new UpdateTestCaseRequest(
            Name: "New Name",
            Description: "New Desc",
            SourceWorkItemId: wid,
            AutomationKind: AutomationKind.Unit,
            Label: "new-label",
            IsArchived: true,
            LastRunPassed: false,
            LastRunAt: DateTimeOffset.UtcNow,
            LastRunResult: "Some error"
        );

        var resp = await _client.PutAsJsonAsync("/testcases/tc-up-1", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var loaded = await _factory.TestCaseStore.GetAsync("tc-up-1");
        Assert.NotNull(loaded);
        Assert.Equal("New Name", loaded.Name);
        Assert.Equal("New Desc", loaded.Description);
        Assert.Equal(AutomationKind.Unit, loaded.AutomationKind);
        Assert.Equal("new-label", loaded.Label);
        Assert.True(loaded.IsArchived);
        Assert.False(loaded.LastRunPassed);
        Assert.Equal("Some error", loaded.LastRunResult);
    }

    [Fact]
    public async Task DeleteTestCase_Exists_DeletesSuccessfully()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-del-1",
            Name = "To Delete",
            Description = "",
            SourceWorkItemId = wid
        };
        await _factory.TestCaseStore.CreateAsync(tc);

        var resp = await _client.DeleteAsync("/testcases/tc-del-1");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var loaded = await _factory.TestCaseStore.GetAsync("tc-del-1");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task ListByWorkItem_ReturnsCorrectCases()
    {
        var widA = await SeedWorkItemAsync();
        var widB = await SeedWorkItemAsync();

        var tc1 = new TestCase { Id = "tc-list-1", Name = "Case 1", Description = "", SourceWorkItemId = widA };
        var tc2 = new TestCase { Id = "tc-list-2", Name = "Case 2", Description = "", SourceWorkItemId = widB };
        await _factory.TestCaseStore.CreateAsync(tc1);
        await _factory.TestCaseStore.CreateAsync(tc2);

        var resp = await _client.GetAsync($"/workitems/{widA}/testcases");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var list = await resp.Content.ReadFromJsonAsync<List<TestCaseDto>>();
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("tc-list-1", list[0].Id);
    }
}

internal sealed class TestCaseApiFactory : WebApplicationFactory<Program>
{
    public const string ProjectId = "test-project";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-testcaseshttp-{Guid.NewGuid():N}.db");

    public SqliteTestCaseStore TestCaseStore { get; }
    public SqliteWorkItemStore WorkItemStore { get; }

    public TestCaseApiFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
        TestCaseStore = new SqliteTestCaseStore(_dbPath);
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

            services.RemoveAll<ITestCaseStore>();
            services.AddSingleton<ITestCaseStore>(TestCaseStore);

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
            TestCaseStore.Dispose();
            WorkItemStore.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
