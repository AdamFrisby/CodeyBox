using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Integration tests for GET /workitems/{id}/diff.
/// Each test that exercises git synthesizes a fake bare repo using the git CLI.
/// </summary>
public sealed class WorkItemDiffEndpointTests : IClassFixture<DiffApiFactory>
{
    private readonly DiffApiFactory _factory;

    public WorkItemDiffEndpointTests(DiffApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetDiff_UnknownId_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{Guid.NewGuid()}/diff");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetDiff_InvalidGuid_Returns400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/workitems/not-a-guid/diff");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetDiff_ItemExistsButNoRepo_Returns204()
    {
        var item = MakeItem();
        await _factory.Store.CreateAsync(item);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/diff");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task GetDiff_WithChanges_ReturnsJsonShape()
    {
        var item = MakeItem(workBranch: null);
        await _factory.Store.CreateAsync(item);

        // Build work branch name as the endpoint does: codeybox/<first 8 chars of id>
        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        await CreateBareRepoWithCommitsAsync(_factory.GitRootDir, item.Id, "main", workBranch);

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}/diff");
        req.Headers.Accept.ParseAdd("application/json");
        var resp = await client.SendAsync(req);

        resp.EnsureSuccessStatusCode();
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(item.Id.ToString(), body.GetProperty("workItemId").GetString());
        Assert.Equal("main", body.GetProperty("baseBranch").GetString());
        Assert.Equal(workBranch, body.GetProperty("workBranch").GetString());
        Assert.NotNull(body.GetProperty("baseCommitSha").GetString());
        Assert.NotNull(body.GetProperty("workCommitSha").GetString());
        Assert.True(body.GetProperty("filesChanged").GetInt32() > 0);
        Assert.True(body.GetProperty("linesAdded").GetInt32() > 0);
        var diffText = body.GetProperty("diff").GetString();
        Assert.False(string.IsNullOrEmpty(diffText));
        Assert.Contains("+++", diffText);
    }

    [Fact]
    public async Task GetDiff_EmptyDiff_Returns204()
    {
        var item = MakeItem(workBranch: null);
        await _factory.Store.CreateAsync(item);

        // Create bare repo where baseBranch and workBranch are the same commit.
        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        await CreateBareRepoNoChangesAsync(_factory.GitRootDir, item.Id, "main", workBranch);

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/workitems/{item.Id}/diff");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task GetDiff_RawFormat_ReturnsDiffText()
    {
        var item = MakeItem(workBranch: null);
        await _factory.Store.CreateAsync(item);

        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        await CreateBareRepoWithCommitsAsync(_factory.GitRootDir, item.Id, "main", workBranch);

        var client = _factory.CreateClient();
        // No Accept: application/json → raw diff
        var resp = await client.GetAsync($"/workitems/{item.Id}/diff");
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        Assert.Equal("text/x-diff", contentType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("+++", body);
    }

    [Fact]
    public async Task GetDiff_SecretInDiff_IsRedacted()
    {
        var item = MakeItem(workBranch: null);
        await _factory.Store.CreateAsync(item);

        var workBranch = $"codeybox/{item.Id.ToString()[..8]}";
        // Create a commit that adds a file containing a fake Anthropic key.
        await CreateBareRepoWithSecretAsync(_factory.GitRootDir, item.Id, "main", workBranch);

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/workitems/{item.Id}/diff");
        req.Headers.Accept.ParseAdd("application/json");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var diff = body.GetProperty("diff").GetString() ?? "";
        // The raw key must not appear; only *** should.
        Assert.DoesNotContain("sk-ant-", diff);
        Assert.Contains("***", diff);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WorkItem MakeItem(string? workBranch = null) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Diff Test",
        Prompt = "test",
        BaseBranch = "main",
        WorkBranch = workBranch,
        State = WorkItemState.Working,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromHours(1),
        MergeTimeout = TimeSpan.FromMinutes(30),
    };

    /// <summary>Creates a bare repo with main and a work branch that has changes.</summary>
    private static async Task CreateBareRepoWithCommitsAsync(
        string gitRoot, WorkItemId id, string baseBranch, string workBranch)
    {
        var barePath = Path.Combine(gitRoot, id + ".git");
        var tempWork = Path.Combine(Path.GetTempPath(), $"diff-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempWork);
            await TestSupport.RunGit(tempWork, "init", "-b", baseBranch);
            await TestSupport.RunGit(tempWork, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(tempWork, "config", "user.name", "Test");

            await File.WriteAllTextAsync(Path.Combine(tempWork, "readme.txt"), "initial content\n");
            await TestSupport.RunGit(tempWork, "add", "readme.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "initial");

            await TestSupport.RunGit(tempWork, "checkout", "-b", workBranch);
            await File.WriteAllTextAsync(Path.Combine(tempWork, "readme.txt"), "modified content\n");
            await File.WriteAllTextAsync(Path.Combine(tempWork, "new-file.txt"), "new file\n");
            await TestSupport.RunGit(tempWork, "add", "readme.txt", "new-file.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "agent changes");

            await TestSupport.RunGit(Path.GetTempPath(), "clone", "--bare", "--local", tempWork, barePath);
        }
        finally
        {
            Directory.Delete(tempWork, recursive: true);
        }
    }

    /// <summary>Creates a bare repo where baseBranch and workBranch point to the same commit.</summary>
    private static async Task CreateBareRepoNoChangesAsync(
        string gitRoot, WorkItemId id, string baseBranch, string workBranch)
    {
        var barePath = Path.Combine(gitRoot, id + ".git");
        var tempWork = Path.Combine(Path.GetTempPath(), $"diff-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempWork);
            await TestSupport.RunGit(tempWork, "init", "-b", baseBranch);
            await TestSupport.RunGit(tempWork, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(tempWork, "config", "user.name", "Test");

            await File.WriteAllTextAsync(Path.Combine(tempWork, "readme.txt"), "initial\n");
            await TestSupport.RunGit(tempWork, "add", "readme.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "initial");

            // Work branch points to same commit as base branch (no changes).
            await TestSupport.RunGit(tempWork, "branch", workBranch);

            await TestSupport.RunGit(Path.GetTempPath(), "clone", "--bare", "--local", tempWork, barePath);
        }
        finally
        {
            Directory.Delete(tempWork, recursive: true);
        }
    }

    /// <summary>Creates a bare repo with a fake secret token committed on the work branch.</summary>
    private static async Task CreateBareRepoWithSecretAsync(
        string gitRoot, WorkItemId id, string baseBranch, string workBranch)
    {
        var barePath = Path.Combine(gitRoot, id + ".git");
        var tempWork = Path.Combine(Path.GetTempPath(), $"diff-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempWork);
            await TestSupport.RunGit(tempWork, "init", "-b", baseBranch);
            await TestSupport.RunGit(tempWork, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(tempWork, "config", "user.name", "Test");

            await File.WriteAllTextAsync(Path.Combine(tempWork, "config.txt"), "safe content\n");
            await TestSupport.RunGit(tempWork, "add", "config.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "initial");

            await TestSupport.RunGit(tempWork, "checkout", "-b", workBranch);
            // Simulate an agent accidentally committing a token.
            await File.WriteAllTextAsync(
                Path.Combine(tempWork, "config.txt"),
                "api_key = sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n");
            await TestSupport.RunGit(tempWork, "add", "config.txt");
            await TestSupport.RunGit(tempWork, "commit", "-m", "agent commit with secret");

            await TestSupport.RunGit(Path.GetTempPath(), "clone", "--bare", "--local", tempWork, barePath);
        }
        finally
        {
            Directory.Delete(tempWork, recursive: true);
        }
    }
}

/// <summary>
/// Isolated WebApplicationFactory for diff endpoint tests.
/// Exposes <see cref="GitRootDir"/> so tests can populate bare repos.
/// </summary>
public sealed class DiffApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-diff-test-{Guid.NewGuid():N}.db");

    public readonly string GitRootDir = Path.Combine(
        Path.GetTempPath(), $"diff-git-{Guid.NewGuid():N}");

    public SqliteWorkItemStore Store { get; }

    public DiffApiFactory()
    {
        Store = new SqliteWorkItemStore(_dbPath);
        Directory.CreateDirectory(GitRootDir);
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
                ["CodeyBox:GitRootDirectory"] = GitRootDir,
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"diff-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"diff-audit-{Guid.NewGuid():N}-.json"),
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
                    DefaultBaseBranch = "main",
                }));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Store.Dispose();
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
            try { Directory.Delete(GitRootDir, recursive: true); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
