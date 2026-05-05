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

    [Fact]
    public async Task CreateWorkItem_WithInReviewReleaseId_Returns400()
    {
        var relId = ReleaseId.New();
        var inReview = new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"inreview-{relId}",
            State = ReleaseState.InReview,
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewStartedAt = DateTimeOffset.UtcNow,
        };
        await _factory.ReleaseStore.CreateAsync(inReview);

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
    public async Task CreateWorkItem_WithReleasedReleaseId_Returns400()
    {
        var relId = ReleaseId.New();
        var released = new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"released-{relId}",
            State = ReleaseState.Released,
            CreatedAt = DateTimeOffset.UtcNow,
            ReleasedAt = DateTimeOffset.UtcNow,
        };
        await _factory.ReleaseStore.CreateAsync(released);

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

/// <summary>
/// Verifies that POST /workitems with a releaseId against a project with
/// ReleaseConfig.Enabled=false returns 400 (spec constraint).
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ReleaseDisabledProjectRejectsReleaseIdTests : IDisposable
{
    private readonly DisabledReleaseApiFactory _factory = new();
    private readonly HttpClient _client;

    public ReleaseDisabledProjectRejectsReleaseIdTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task CreateWorkItem_WithReleaseId_AgainstDisabledProject_Returns400()
    {
        // Create an Open release so we get past the release-lookup and state checks
        // and exercise the project Enabled=false guard (WorkItemEndpoints.cs:197).
        var relId = ReleaseId.New();
        await _factory.ReleaseStore.CreateAsync(new Release
        {
            Id = relId,
            ProjectId = new ProjectId("disabled-project"),
            Name = $"test-{relId}",
            State = ReleaseState.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var response = await _client.PostAsJsonAsync("/workitems", new
        {
            projectId = "disabled-project",
            title = "test item",
            prompt = "do the thing",
            releaseId = relId.ToString(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// Verifies that the POST /releases/{id}/release endpoint enforces project-scope
/// authorization (IDOR guard) and the confirmation-token requirement.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ForceBeginReviewAuthTests : IDisposable
{
    private readonly ReleaseWorkItemApiFactory _factory = new();
    private readonly HttpClient _client;

    public ForceBeginReviewAuthTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task ForceRelease_WrongConfirmation_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/releases/{ReleaseId.New()}/release?projectId=test-project",
            new { confirmation = "wrong" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ForceRelease_MissingProjectId_Returns400()
    {
        var relId = ReleaseId.New();
        await _factory.ReleaseStore.CreateAsync(new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"test-{relId}",
            State = ReleaseState.Closed,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var response = await _client.PostAsJsonAsync(
            $"/releases/{relId}/release",
            new { confirmation = "yes-i-know-the-risk" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ForceRelease_WrongProjectId_Returns403()
    {
        var relId = ReleaseId.New();
        await _factory.ReleaseStore.CreateAsync(new Release
        {
            Id = relId,
            ProjectId = new ProjectId("test-project"),
            Name = $"test-{relId}",
            State = ReleaseState.Closed,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var response = await _client.PostAsJsonAsync(
            $"/releases/{relId}/release?projectId=other-project",
            new { confirmation = "yes-i-know-the-risk" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

internal sealed class DisabledReleaseApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"cb-rel-disabled-{Guid.NewGuid():N}.db");

    public SqliteReleaseStore ReleaseStore { get; }

    public DisabledReleaseApiFactory()
    {
        ReleaseStore = new SqliteReleaseStore(_dbPath);
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

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("disabled-project"),
                    DisplayName = "Disabled Release Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    ReleaseConfig = new ProjectReleaseConfig { Enabled = false },
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseStore.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
