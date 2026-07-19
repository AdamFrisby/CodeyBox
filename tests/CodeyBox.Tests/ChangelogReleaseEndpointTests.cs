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
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// HTTP-level tests for POST /projects/{id}/release.
/// </summary>
public sealed class ChangelogReleaseEndpointTests : IDisposable
{
    private readonly ChangelogApiFactory _factory = new();
    private readonly HttpClient _client;

    public ChangelogReleaseEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Release_UnknownProject_Returns404()
    {
        var resp = await _client.PostAsJsonAsync("/projects/no-such-project/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Release_InvalidProjectId_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/projects/!!bad!!/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Release_MissingFromTag_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/projects/test-project/release",
            new { toTag = "v1.1.0" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("fromTag", body);
    }

    [Fact]
    public async Task Release_MissingToTag_Returns400()
    {
        var resp = await _client.PostAsJsonAsync("/projects/test-project/release",
            new { fromTag = "v1.0.0" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("toTag", body);
    }

    [Fact]
    public async Task Release_NoGitHubUpstream_Returns400()
    {
        // "test-project" has no upstream in the test factory, so credentials are missing.
        var resp = await _client.PostAsJsonAsync("/projects/test-project/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("github", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Release_GitHubProject_Returns200WithMarkdown()
    {
        var resp = await _client.PostAsJsonAsync("/projects/gh-project/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.TryGetProperty("markdown", out var md));
        Assert.Equal(JsonValueKind.String, md.ValueKind);
        Assert.NotEmpty(md.GetString()!);
    }

    [Fact]
    public async Task Release_WasCapped_IncludedInResponse()
    {
        var resp = await _client.PostAsJsonAsync("/projects/gh-project/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.TryGetProperty("wasCapped", out _));
    }
}

// ── Test factory ──────────────────────────────────────────────────────────────

internal sealed class ChangelogApiFactory : CodeyBox.Tests.CodeyBoxWebApplicationFactory
{
    private readonly string _dbPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"codeybox-changelog-test-{Guid.NewGuid():N}.db");

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
                ["CodeyBox:GitRootDirectory"] = System.IO.Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = System.IO.Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = System.IO.Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                // Token env var for "gh-project".
                ["CodeyBox:Changelog:Enabled"] = "true",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // Provide a project with github upstream.
            // Token is read from env var "TEST_GITHUB_PAT" — set it to a placeholder.
            Environment.SetEnvironmentVariable("TEST_GITHUB_PAT", "ghp_test_token");
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Test Project",
                    RepositoryUrl = "https://github.com/test/repo",
                    // No upstream — used to test the "no credentials" path.
                    Upstream = ProjectUpstream.Noop,
                },
                new Project
                {
                    Id = new ProjectId("gh-project"),
                    DisplayName = "GitHub Project",
                    RepositoryUrl = "https://github.com/owner/repo",
                    Upstream = new ProjectUpstream
                    {
                        Kind = "github",
                        GitHubOwner = "owner",
                        GitHubRepository = "repo",
                        TokenEnvVar = "TEST_GITHUB_PAT",
                    },
                }));

            // Stub out the PR enumerator and changelog generator.
            services.RemoveAll<IPullRequestEnumerator>();
            services.AddSingleton<IPullRequestEnumerator>(new StubPullRequestEnumerator());

            services.RemoveAll<IChangelogGenerator>();
            services.AddSingleton<IChangelogGenerator>(new StubChangelogGenerator());
        });
    }

    protected override void Dispose(bool disposing)
        => DisposeHostThenDeleteSqliteDatabase(disposing, _dbPath);
}

internal sealed class StubPullRequestEnumerator : IPullRequestEnumerator
{
    public Task<PullRequestEnumeratorResult> ListMergedBetweenAsync(
        string owner, string repo, string token,
        string fromTag, string toTag, CancellationToken ct)
        => Task.FromResult(new PullRequestEnumeratorResult(
            new List<MergedPullRequest>
            {
                new(42, "Test PR", "body", "2026-05-01T00:00:00Z", [], []),
            },
            WasCapped: false));

    public Task<string?> ResolvePreviousTagAsync(
        string owner, string repo, string token,
        string currentTag, CancellationToken ct)
        => Task.FromResult<string?>("v1.0.0");
}

internal sealed class StubChangelogGenerator : IChangelogGenerator
{
    public Task<ChangelogEntry> GenerateAsync(ChangelogRequest request, CancellationToken ct)
        => Task.FromResult(new ChangelogEntry
        {
            ToTag = request.ToTag,
            Markdown = $"## [{request.ToTag}] - 2026-05-02\n\n### Added\n- Test PR ([#42])\n",
            CategoryToPrNumbers = new Dictionary<string, IReadOnlyList<int>>
            {
                ["Added"] = [42],
            },
        });
}
