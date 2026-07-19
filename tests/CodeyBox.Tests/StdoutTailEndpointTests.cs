using System.Net;
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
/// HTTP-level tests for GET /workitems/{id}/stdout-tail.
/// Exercises the endpoint plumbing (ResolveWorkItemAsync + IStdoutBroadcaster.GetTail)
/// without requiring a live SignalR hub.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class StdoutTailEndpointTests : IDisposable
{
    private readonly StdoutTailApiFactory _factory = new();
    private readonly HttpClient _client;

    public StdoutTailEndpointTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem MakeItem(WorkItemId id) => new()
    {
        Id = id,
        ProjectId = new ProjectId("p"),
        Title = "t",
        Prompt = "pr",
        Agent = AgentKind.Claude,
    };

    [Fact]
    public async Task GetStdoutTail_NotFound_WhenWorkItemDoesNotExist()
    {
        var resp = await _client.GetAsync("/workitems/aabbccdd-0000-0000-0000-000000000099/stdout-tail");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetStdoutTail_InvalidId_Returns400()
    {
        var resp = await _client.GetAsync("/workitems/not-a-guid/stdout-tail");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetStdoutTail_ExistsButNoBroadcasterEntry_ReturnsEmptyOk()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));

        var resp = await _client.GetAsync($"/workitems/{id}/stdout-tail");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("", body);
    }

    [Fact]
    public async Task GetStdoutTail_ReturnsPlainText_WhenTailAvailable()
    {
        var id = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id));
        _factory.Broadcaster.SetTail(id, "agent output line 1\nagent output line 2\n");

        var resp = await _client.GetAsync($"/workitems/{id}/stdout-tail");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Contains("text/plain", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("agent output line 1", body);
        Assert.Contains("agent output line 2", body);
    }

    [Fact]
    public async Task GetStdoutTail_TailIsolatedPerWorkItem()
    {
        var id1 = WorkItemId.New();
        var id2 = WorkItemId.New();
        await _factory.WorkItemStore.CreateAsync(MakeItem(id1));
        await _factory.WorkItemStore.CreateAsync(MakeItem(id2));

        _factory.Broadcaster.SetTail(id1, "item1 output");
        _factory.Broadcaster.SetTail(id2, "item2 output");

        var body1 = await (await _client.GetAsync($"/workitems/{id1}/stdout-tail")).Content.ReadAsStringAsync();
        var body2 = await (await _client.GetAsync($"/workitems/{id2}/stdout-tail")).Content.ReadAsStringAsync();

        Assert.Contains("item1 output", body1);
        Assert.DoesNotContain("item2 output", body1);
        Assert.Contains("item2 output", body2);
        Assert.DoesNotContain("item1 output", body2);
    }
}

// ── Test infrastructure ────────────────────────────────────────────────────────

internal sealed class CapturingStdoutBroadcaster : IStdoutBroadcaster
{
    private readonly Dictionary<string, string> _tails = new();

    public void SetTail(WorkItemId id, string tail)
    {
        lock (_tails) _tails[id.ToString()] = tail;
    }

    public void BroadcastChunk(WorkItemId workItemId, string phase, string chunk) { }
    public Task CompleteAsync(WorkItemId workItemId) => Task.CompletedTask;

    public string? GetTail(WorkItemId workItemId)
    {
        lock (_tails) return _tails.TryGetValue(workItemId.ToString(), out var t) ? t : null;
    }
}

internal sealed class StdoutTailApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-stdout-tail-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }
    public CapturingStdoutBroadcaster Broadcaster { get; } = new();

    public StdoutTailApiFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Temp.Root;
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
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
            // Replace the SignalR-backed broadcaster with a test double
            services.RemoveAll<CodeyBox.Api.AgentStdoutBroadcastService>();
            services.RemoveAll<IStdoutBroadcaster>();
            services.AddSingleton<IStdoutBroadcaster>(Broadcaster);
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, WorkItemStore.Dispose);
}
