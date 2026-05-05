using System.Net;
using System.Net.Http.Json;
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
/// Verifies that the /workitems endpoint rejects work items associated with
/// non-Open releases (Closed, Abandoned, Released) with 400 Bad Request.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ClosedReleaseRejectsWorkItemsTests : IDisposable
{
    private readonly ReleaseWorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ClosedReleaseRejectsWorkItemsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task CreateWorkItem_WithClosedReleaseId_Returns400()
    {
        var relId = ReleaseId.New();
        var closedRelease = new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"closed-{relId}",
            State = ReleaseState.Closed,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.ReleaseStore.CreateAsync(closedRelease);

        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "test item",
            prompt = "do the thing",
            releaseId = relId.ToString(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateWorkItem_WithOpenReleaseId_Returns201()
    {
        var relId = ReleaseId.New();
        var openRelease = new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"open-{relId}",
            State = ReleaseState.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.ReleaseStore.CreateAsync(openRelease);

        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "test item",
            prompt = "do the thing",
            releaseId = relId.ToString(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateWorkItem_WithAbandonedReleaseId_Returns400()
    {
        var relId = ReleaseId.New();
        var abandoned = new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"abandoned-{relId}",
            State = ReleaseState.Abandoned,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.ReleaseStore.CreateAsync(abandoned);

        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "test-project",
            title = "test item",
            prompt = "do the thing",
            releaseId = relId.ToString(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

internal sealed class ReleaseWorkItemApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"cb-rel-http-{Guid.NewGuid():N}.db");

    public SqliteReleaseStore ReleaseStore { get; }
    public SqliteWorkItemStore WorkItemStore { get; }

    public ReleaseWorkItemApiFactory()
    {
        ReleaseStore = new SqliteReleaseStore(_dbPath);
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
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

            services.RemoveAll<IReleaseStore>();
            services.AddSingleton<IReleaseStore>(ReleaseStore);

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    ReleaseConfig = new ProjectReleaseConfig { Enabled = true },
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            ReleaseStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
