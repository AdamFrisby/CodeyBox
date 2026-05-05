using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
/// Integration tests verifying the full changelog flow: webhook received →
/// work item created with the correct prompt structure.
/// </summary>
public sealed class ChangelogIntegrationTests : IDisposable
{
    private const string WebhookSecret = "integration-test-secret-9876543210ab";
    private const string SecretEnvVar = "TEST_INTEG_WEBHOOK_SECRET";
    private const string PatEnvVar = "TEST_INTEG_PAT";

    private readonly ChangelogIntegrationFactory _factory;
    private readonly HttpClient _client;

    public ChangelogIntegrationTests()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, WebhookSecret);
        Environment.SetEnvironmentVariable(PatEnvVar, "ghp_integration_test_token");
        _factory = new ChangelogIntegrationFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable(SecretEnvVar, null);
        Environment.SetEnvironmentVariable(PatEnvVar, null);
    }

    [Fact]
    public async Task Integration_WebhookTrigger_WorkItemPromptContainsChangelogMarkdown()
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "published",
            release = new { tag_name = "v2.0.0", prerelease = false },
            repository = new
            {
                full_name = "integration-owner/integration-repo",
                html_url = "https://github.com/integration-owner/integration-repo",
            },
        });
        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        var sig = ComputeSignature(bodyBytes, WebhookSecret);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github/release");
        req.Headers.Add("X-GitHub-Event", "release");
        req.Headers.Add("X-Hub-Signature-256", $"sha256={sig}");
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var store = _factory.Services.GetRequiredService<IWorkItemStore>();
        var items = new List<WorkItem>();
        await foreach (var item in store.ListAsync())
            items.Add(item);

        Assert.Single(items);
        var workItem = items[0];

        // Title should reference the new tag.
        Assert.Contains("v2.0.0", workItem.Title);

        // Prompt should tell the agent to apply the changelog to CHANGELOG.md.
        Assert.Contains("CHANGELOG.md", workItem.Prompt);

        // Prompt should embed the generated markdown (from the stub generator).
        Assert.Contains("v2.0.0", workItem.Prompt);
        Assert.Contains("Added", workItem.Prompt);
    }

    [Fact]
    public async Task Integration_ManualRelease_ReturnsMarkdownWithCategories()
    {
        var resp = await _client.PostAsJsonAsync(
            "/projects/integ-project/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var markdown = doc.GetProperty("markdown").GetString()!;
        var categories = doc.GetProperty("categoryToPrNumbers");

        Assert.NotEmpty(markdown);
        Assert.Contains("v1.1.0", markdown);
        // The stub generator always returns "Added" with PR #42.
        Assert.True(categories.TryGetProperty("Added", out var addedPrs));
        Assert.Equal(42, addedPrs[0].GetInt32());
    }

    private static string ComputeSignature(byte[] body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed class ChangelogIntegrationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"codeybox-integ-{Guid.NewGuid():N}.db");

    public SqliteWorkItemStore WorkItemStore { get; }

    public ChangelogIntegrationFactory()
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
                ["CodeyBox:Changelog:GitHubWebhookSecretEnvVar"] = "TEST_INTEG_WEBHOOK_SECRET",
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
                    Id = new ProjectId("integ-project"),
                    DisplayName = "Integration Project",
                    RepositoryUrl = "https://github.com/integration-owner/integration-repo",
                    Upstream = new ProjectUpstream
                    {
                        Kind = "github",
                        GitHubOwner = "integration-owner",
                        GitHubRepository = "integration-repo",
                        TokenEnvVar = "TEST_INTEG_PAT",
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
