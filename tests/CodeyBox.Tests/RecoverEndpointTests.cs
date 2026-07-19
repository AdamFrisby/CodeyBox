using System.Net;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for POST /workitems/{id}/recover — the operator-triggered
/// single-call recovery for a stuck in-flight item. Same code path the
/// per-item stale-updatedAt watchdog drives; this endpoint just exposes it
/// so an operator does not need to cancel + resume manually.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class RecoverEndpointTests : IDisposable
{
    private readonly RecoverApiFactory _factory = new();
    private readonly HttpClient _client;

    public RecoverEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static WorkItem WorkingItem(string workBranch = "codeybox/auto/work-recover") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        WorkBranch = workBranch,
        State = WorkItemState.Working,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-90),
    };

    [Fact]
    public async Task Recover_WorkingItem_RequeuesPreservingBranch_ReturnsAccepted()
    {
        var item = WorkingItem();
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/recover", content: null);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var after = await _factory.Store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(item.WorkBranch, after.WorkBranch);
        Assert.True(after.PreserveWorkBranchOnQueuedPickup);
        Assert.Equal(1, after.RecoveryAttempts);
    }

    [Fact]
    public async Task Recover_QueuedItem_RefusesWithConflict()
    {
        // Queued items aren't in-flight; the operator should use /retry or
        // /resume instead.
        var item = WorkingItem() with { State = WorkItemState.Queued };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/recover", content: null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var after = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
    }

    [Fact]
    public async Task Recover_DoneItem_RefusesWithConflict()
    {
        var item = WorkingItem() with { State = WorkItemState.Done };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/recover", content: null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Recover_UnknownId_Returns404()
    {
        var resp = await _client.PostAsync($"/workitems/{WorkItemId.New()}/recover", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Recover_AtCap_EscalatesToNeedsOperatorInput()
    {
        // Verifies the bounded-then-escalate contract is preserved when the
        // endpoint drives recovery, not just the watchdog sweep.
        var item = WorkingItem() with { RecoveryAttempts = 3 };
        await _factory.Store.CreateAsync(item);

        var resp = await _client.PostAsync($"/workitems/{item.Id}/recover", content: null);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var after = await _factory.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, after!.State);
    }
}

internal sealed class RecoverApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"cb-recover-http-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore Store { get; }

    public RecoverApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
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
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"recover-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"recover-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"recover-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"recover-agent-streams-{Guid.NewGuid():N}"),
                // Tighten the bounded-attempt cap so the at-cap test exercises
                // a small recovery budget without inflating other tests.
                ["CodeyBox:WorkerProgressWatchdog:ItemStaleMaxRecoveryAttempts"] = "3",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(Store);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                }));
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath, Store.Dispose);
}
