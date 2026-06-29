using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.MultipassRemote;
using CodeyBox.Sandbox.Process;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodeyBox.Tests;

public sealed class E2eRunApiTests : IDisposable
{
    private readonly TestCaseApiFactory _factory = new();
    private readonly HttpClient _client;

    public E2eRunApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void Program_wires_remote_ssh_pool_to_multipass_remote_provider()
    {
        using var factory = new E2ePoolWiringFactory("remote-ssh");

        var pool = factory.Services.GetRequiredService<IE2eExecutionPool>();

        Assert.Equal("remote-ssh", pool.Name);
        Assert.IsType<MultipassRemoteSandboxProvider>(GetInnerProvider(pool));
    }

    [Fact]
    public void Program_wires_local_e2e_pool_to_separate_unadmitted_provider()
    {
        using var factory = new E2ePoolWiringFactory("local");

        var pool = factory.Services.GetRequiredService<IE2eExecutionPool>();
        var codingProvider = factory.Services.GetRequiredService<ISandboxProvider>();
        var e2eProvider = GetInnerProvider(pool);

        Assert.Equal("local", pool.Name);
        Assert.IsType<ProcessSandboxProvider>(e2eProvider);
        Assert.IsAssignableFrom<SandboxAdmissionControlledProvider>(codingProvider);
        Assert.NotSame(codingProvider, e2eProvider);
    }

    [Fact]
    public async Task E2eRun_routes_enqueue_list_get_cancel_and_summarise_batch()
    {
        var testCaseId = await SeedE2eCaseAsync("api-run-case");
        var batchId = Guid.NewGuid().ToString("N");

        var enqueue = await _client.PostAsJsonAsync("/e2eruns", new EnqueueE2eRunRequest(testCaseId, batchId));
        Assert.Equal(HttpStatusCode.Created, enqueue.StatusCode);
        var created = await enqueue.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(created);
        Assert.Equal(testCaseId, created.TestCaseId);
        Assert.Equal(E2eRunStatus.Queued, created.Status);

        var get = await _client.GetAsync($"/e2eruns/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        var all = await _client.GetFromJsonAsync<List<E2eRunDto>>("/e2eruns");
        Assert.NotNull(all);
        Assert.Contains(all, r => r.Id == created.Id);

        var byCase = await _client.GetFromJsonAsync<List<E2eRunDto>>($"/testcases/{testCaseId}/runs");
        Assert.NotNull(byCase);
        Assert.Single(byCase, r => r.Id == created.Id);

        var cancel = await _client.PostAsync($"/e2eruns/{created.Id}/cancel", content: null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var canceled = await cancel.Content.ReadFromJsonAsync<E2eRunDto>();
        Assert.NotNull(canceled);
        Assert.Equal(E2eRunStatus.Canceled, canceled.Status);

        var cancelAgain = await _client.PostAsync($"/e2eruns/{created.Id}/cancel", content: null);
        Assert.Equal(HttpStatusCode.Conflict, cancelAgain.StatusCode);

        var summary = await _client.GetFromJsonAsync<BatchSummaryDto>($"/e2eruns/batches/{batchId}");
        Assert.NotNull(summary);
        Assert.Equal(batchId, summary.BatchId);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Canceled);
        Assert.True(summary.Complete);
    }

    [Fact]
    public async Task E2eRun_bulk_enqueue_validates_before_creating_any_rows()
    {
        var valid = await SeedE2eCaseAsync("api-bulk-valid");

        var response = await _client.PostAsJsonAsync(
            "/e2eruns/bulk",
            new EnqueueBulkE2eRunsRequest([valid, "missing-case"], BatchId: "batch-atomic"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var runs = new List<E2eRun>();
        await foreach (var run in _factory.E2eRunStore.ListAsync())
            runs.Add(run);
        Assert.Empty(runs);
    }

    [Fact]
    public async Task E2eRun_bulk_enqueue_creates_batch_and_summary()
    {
        var first = await SeedE2eCaseAsync("api-bulk-first");
        var second = await SeedE2eCaseAsync("api-bulk-second");

        var response = await _client.PostAsJsonAsync(
            "/e2eruns/bulk",
            new EnqueueBulkE2eRunsRequest([first, second], BatchId: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EnqueueBulkE2eRunsResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Runs.Count);

        var summary = await _client.GetFromJsonAsync<BatchSummaryDto>($"/e2eruns/batches/{body.BatchId}");
        Assert.NotNull(summary);
        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Queued);
        Assert.False(summary.Complete);
    }

    private async Task<string> SeedE2eCaseAsync(string id)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId(TestCaseApiFactory.ProjectId),
            Title = id,
            Prompt = "fixture",
        };
        await _factory.WorkItemStore.CreateAsync(item);

        var testCase = new TestCase
        {
            Id = id,
            Name = id,
            Description = "",
            SourceWorkItemId = item.Id.ToString(),
            AutomationKind = AutomationKind.E2eReplay,
            ExecutableArtifactJson = JsonSerializer.Serialize(new E2eReplayArtifact
            {
                Steps = [new E2eReplayStep { Action = "navigate", Target = "http://app.local/" }],
                Assertions = [new E2eReplayAssertion { Kind = "selectorVisible", Selector = "#root" }],
            }),
        };
        await _factory.TestCaseStore.CreateAsync(testCase);
        return testCase.Id;
    }

    private static ISandboxProvider GetInnerProvider(IE2eExecutionPool pool)
    {
        var field = typeof(LocalE2eExecutionPool).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<ISandboxProvider>(field.GetValue(pool));
    }
}

internal sealed class E2ePoolWiringFactory(string poolKind) : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-e2epool-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:DangerouslyAllowProcessSandbox"] = "true",
                ["CodeyBox:SandboxProvider"] = "process",
                ["CodeyBox:E2eExecution:PoolKind"] = poolKind,
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId(TestCaseApiFactory.ProjectId),
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
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
