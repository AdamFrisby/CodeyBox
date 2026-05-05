using System.Net;
using System.Security.Cryptography;
using System.Text;
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
/// HTTP-level tests for POST /webhooks/github/release.
/// </summary>
public sealed class ChangelogWebhookTests : IDisposable
{
    private const string WebhookSecretEnvVar = "TEST_CHANGELOG_WEBHOOK_SECRET";
    private const string WebhookSecret = "super-secret-test-value-1234567890ab";
    private const string GithubPat = "ghp_test_pat_token";

    private readonly ChangelogWebhookFactory _factory;
    private readonly HttpClient _client;

    public ChangelogWebhookTests()
    {
        Environment.SetEnvironmentVariable(WebhookSecretEnvVar, WebhookSecret);
        Environment.SetEnvironmentVariable("TEST_WEBHOOK_PAT", GithubPat);
        _factory = new ChangelogWebhookFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(WebhookSecretEnvVar, null);
        Environment.SetEnvironmentVariable("TEST_WEBHOOK_PAT", null);
    }

    [Fact]
    public async Task Webhook_ValidHmac_Published_Returns202()
    {
        var payload = BuildReleasePayload("published", "v1.1.0",
            "https://github.com/owner/repo");

        var resp = await PostWebhookAsync("release", payload);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_InvalidHmac_Returns401()
    {
        var payload = BuildReleasePayload("published", "v1.1.0",
            "https://github.com/owner/repo");

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.Add("X-GitHub-Event", "release");
        content.Headers.Add("X-Hub-Signature-256", "sha256=badhash");
        var resp = await _client.PostAsync("/webhooks/github/release", content);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_MissingSignature_Returns401()
    {
        var payload = BuildReleasePayload("published", "v1.1.0",
            "https://github.com/owner/repo");

        // No signature header.
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.Add("X-GitHub-Event", "release");
        var resp = await _client.PostAsync("/webhooks/github/release", content);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_PingEvent_Returns202WithoutCreatingWorkItem()
    {
        var payload = """{"zen":"Keep it logically awesome"}""";
        var resp = await PostWebhookAsync("ping", payload);
        // Ping is not a "release" event, so it's silently accepted.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_NonPublishedAction_Returns202()
    {
        var payload = BuildReleasePayload("deleted", "v1.1.0",
            "https://github.com/owner/repo");
        var resp = await PostWebhookAsync("release", payload);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_UnknownRepository_Returns202()
    {
        var payload = BuildReleasePayload("published", "v1.1.0",
            "https://github.com/unknown/repo");
        var resp = await PostWebhookAsync("release", payload);
        // Unknown repo is silently accepted (we don't manage it).
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_ValidRelease_CreatesWorkItem()
    {
        var payload = BuildReleasePayload("published", "v1.1.0",
            "https://github.com/owner/repo");

        var resp = await PostWebhookAsync("release", payload);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Verify a work item was created in the store.
        var store = _factory.Services.GetRequiredService<IWorkItemStore>();
        var items = new List<WorkItem>();
        await foreach (var item in store.ListAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.Contains("v1.1.0", items[0].Title);
        Assert.Contains("CHANGELOG.md", items[0].Prompt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostWebhookAsync(string eventName, string payload)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        var sig = ComputeSignature(bodyBytes, WebhookSecret);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github/release");
        req.Headers.Add("X-GitHub-Event", eventName);
        req.Headers.Add("X-Hub-Signature-256", $"sha256={sig}");
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return await _client.SendAsync(req);
    }

    private static string ComputeSignature(byte[] body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildReleasePayload(string action, string tagName, string htmlUrl)
    {
        var ownerRepo = htmlUrl.Replace("https://github.com/", "");
        return JsonSerializer.Serialize(new
        {
            action,
            release = new { tag_name = tagName, prerelease = false },
            repository = new
            {
                full_name = ownerRepo,
                html_url = htmlUrl,
            },
        });
    }
}

// ── Test factory ──────────────────────────────────────────────────────────────

internal sealed class ChangelogWebhookFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"codeybox-webhook-test-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }

    public ChangelogWebhookFactory()
    {
        WorkItemStore = new SqliteWorkItemStore(_dbPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = System.IO.Path.GetTempPath();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = System.IO.Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = System.IO.Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = System.IO.Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:Changelog:GitHubWebhookSecretEnvVar"] = "TEST_CHANGELOG_WEBHOOK_SECRET",
                ["CodeyBox:Changelog:Enabled"] = "true",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IWorkItemStore>();
            services.AddSingleton<IWorkItemStore>(WorkItemStore);

            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository(
                new Project
                {
                    Id = new ProjectId("test-project"),
                    DisplayName = "Webhook Test Project",
                    RepositoryUrl = "https://github.com/owner/repo",
                    Upstream = new ProjectUpstream
                    {
                        Kind = "github",
                        GitHubOwner = "owner",
                        GitHubRepository = "repo",
                        TokenEnvVar = "TEST_WEBHOOK_PAT",
                    },
                }));

            services.RemoveAll<IPullRequestEnumerator>();
            services.AddSingleton<IPullRequestEnumerator>(new StubPullRequestEnumerator());

            services.RemoveAll<IChangelogGenerator>();
            services.AddSingleton<IChangelogGenerator>(new StubChangelogGenerator());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WorkItemStore.Dispose();
            try { System.IO.File.Delete(_dbPath); } catch { }
        }
        base.Dispose(disposing);
    }
}
