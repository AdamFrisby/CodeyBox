using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests.Uat.UpstreamWebhooksAndReleases;

/// <summary>
/// UAT coverage for GitHub release webhook ingest, changelog generation, and
/// release management workflow from the Upstream, Webhooks, And Releases plan
/// section.
/// Plan anchor: docs/uat/00-plan.md#upstream-webhooks-and-releases
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ChangelogAndReleaseWorkflowUatTests
{
    private const string GithubTokenEnvVar = "CODEYBOX_UAT_CHANGELOG_TOKEN";
    private const string WebhookSecretEnvVar = "UAT_CHANGELOG_WEBHOOK_SECRET";
    private const string GithubToken = "uat-github-token-not-real";
    private const string WebhookSecret = "uat-changelog-webhook-secret";

    [Fact]
    public async Task ManualReleaseEndpoint_EnumeratesPrsUsesProjectHeaderOverrideAndReportsCap()
    {
        WithEnv();
        await using var factory = new UatChangelogApiFactory(Project());
        factory.PullRequests.WasCapped = true;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/projects/release-uat/release",
            new { fromTag = "v1.0.0", toTag = "v1.1.0" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("wasCapped").GetBoolean());
        Assert.Contains("Ship release item", json.GetProperty("markdown").GetString());
        var request = Assert.Single(factory.Generator.Requests);
        Assert.Equal("### Release {tag}", request.SectionHeaderFormat);
        var call = Assert.Single(factory.PullRequests.Calls);
        Assert.Equal(("owner", "repo", "v1.0.0", "v1.1.0"), call);
    }

    [Fact]
    public async Task GitHubReleaseWebhook_WithValidSignatureCreatesChangelogWorkItemUsingProjectPath()
    {
        WithEnv();
        await using var factory = new UatChangelogApiFactory(Project());
        using var client = factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            action = "published",
            release = new { tag_name = "v1.2.0", prerelease = false },
            repository = new { full_name = "owner/repo", html_url = "https://github.com/owner/repo" },
        });

        using var response = await client.SendAsync(SignedReleaseWebhook(payload));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var item = Assert.Single(await ListWorkItemsAsync(factory.WorkItems));
        Assert.Equal("Update docs/CHANGELOG-UAT.md for v1.2.0", item.Title);
        Assert.Contains("docs/CHANGELOG-UAT.md", item.Prompt);
        Assert.Contains("<changelog_entry>", item.Prompt);
        Assert.Equal(item.Id, Assert.Single(factory.Queue.Enqueued));
        Assert.Equal("v1.0.0", Assert.Single(factory.PullRequests.Calls).FromTag);
        Assert.Equal("### Release {tag}", Assert.Single(factory.Generator.Requests).SectionHeaderFormat);
    }

    [Fact]
    public async Task GitHubReleaseWebhook_InvalidSignatureReturnsUnauthorizedWithoutWorkItem()
    {
        WithEnv();
        await using var factory = new UatChangelogApiFactory(Project());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github/release");
        request.Headers.Add("X-GitHub-Event", "release");
        request.Headers.Add("X-Hub-Signature-256", "sha256=bad");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await ListWorkItemsAsync(factory.WorkItems));
    }

    [Fact]
    public async Task ReleaseClose_WithFailedLinkedItem_EmitsOperatorWebhookAndKeepsReleaseClosed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-uat-release-{Guid.NewGuid():N}.db");
        try
        {
            using var releaseStore = new SqliteReleaseStore(dbPath);
            using var workItemStore = new SqliteWorkItemStore(dbPath);
            var webhooks = new CapturingWebhookDispatcher();
            var service = ReleaseTestHelper.BuildService(
                releaseStore,
                workItemStore,
                new InMemoryProjectRepository(Project() with
                {
                    ReleaseConfig = new ProjectReleaseConfig { Enabled = true },
                }),
                webhooks);
            var release = new Release
            {
                Id = ReleaseId.New(),
                ProjectId = new ProjectId("release-uat"),
                Name = "v1.3.0",
                State = ReleaseState.Open,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await releaseStore.CreateAsync(release);
            await workItemStore.CreateAsync(new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = release.ProjectId,
                ReleaseId = release.Id,
                Title = "failed release item",
                Prompt = "p",
                State = WorkItemState.Failed,
            });

            var (success, error) = await service.CloseAsync(release.Id, CancellationToken.None);

            Assert.True(success, error);
            var stored = await releaseStore.GetAsync(release.Id);
            Assert.Equal(ReleaseState.Closed, stored!.State);
            Assert.Contains(webhooks.Events, e => e.Event == "release.closed");
            Assert.Contains(webhooks.Events, e => e.Event == "release.has_failed_work_items");
        }
        finally
        {
            TestTempArtifacts.DeleteSqliteDatabase(dbPath);
        }
    }

    private static Project Project() => new()
    {
        Id = new ProjectId("release-uat"),
        DisplayName = "Release UAT",
        RepositoryUrl = "https://github.com/owner/repo",
        Upstream = new ProjectUpstream
        {
            Kind = "github",
            GitHubOwner = "owner",
            GitHubRepository = "repo",
            TokenEnvVar = GithubTokenEnvVar,
        },
        Changelog = new ProjectChangelog
        {
            Enabled = true,
            ChangelogPath = "docs/CHANGELOG-UAT.md",
            SectionHeaderFormat = "### Release {tag}",
        },
    };

    private static void WithEnv()
    {
        Environment.SetEnvironmentVariable(GithubTokenEnvVar, GithubToken);
        Environment.SetEnvironmentVariable(WebhookSecretEnvVar, WebhookSecret);
    }

    private static HttpRequestMessage SignedReleaseWebhook(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github/release");
        request.Headers.Add("X-GitHub-Event", "release");
        request.Headers.Add(
            "X-Hub-Signature-256",
            "sha256=" + UpstreamWebhooksAndReleasesHelpers.ComputeGitHubSignature(bytes, WebhookSecret));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return request;
    }

    private static async Task<List<WorkItem>> ListWorkItemsAsync(IWorkItemStore store)
    {
        var items = new List<WorkItem>();
        await foreach (var item in store.ListAsync())
            items.Add(item);
        return items;
    }
}
