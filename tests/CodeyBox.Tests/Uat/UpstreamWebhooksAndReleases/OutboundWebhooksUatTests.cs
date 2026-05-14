using System.Net;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.UpstreamWebhooksAndReleases;

/// <summary>
/// UAT coverage for "Outbound webhooks - Publishes pipeline, suggestion,
/// budget, retry, and release events".
/// Plan anchor: docs/uat/00-plan.md#upstream-webhooks-and-releases
/// </summary>
public sealed class OutboundWebhooksUatTests
{
    private const string SecretEnvVar = "CODEYBOX_UAT_WEBHOOK_SECRET";
    private const string Secret = "uat-webhook-secret-value";

    [Fact]
    public async Task MatchingEndpoint_PostsSignedPayloadWithExternalIdAndRetriesTransientFailure()
    {
        Environment.SetEnvironmentVariable(SecretEnvVar, Secret);
        try
        {
            var handler = new SequenceHttpMessageHandler();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted));
            var dispatcher = new HttpWebhookDispatcher(
                new WebhookDispatcherOptions
                {
                    Endpoints =
                    [
                        new WebhookEndpointConfig
                        {
                            Name = "uat",
                            Url = "https://receiver.example.invalid/hook",
                            SecretEnvVar = SecretEnvVar,
                            EventFilter = ["work_item.done"],
                            MaxAttempts = 2,
                            InitialBackoffSeconds = 0,
                            TimeoutSeconds = 5,
                        },
                    ],
                },
                new NamedHttpClientFactory("webhook", handler),
                NullLogger<HttpWebhookDispatcher>.Instance);

            await dispatcher.PublishAsync(Event("work_item.working"), CancellationToken.None);
            await dispatcher.PublishAsync(Event("work_item.done"), CancellationToken.None);
            await dispatcher.DisposeAsync();

            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, req =>
            {
                Assert.Equal("work_item.done", Assert.Single(req.Headers.GetValues("X-CodeyBox-Event")));
                Assert.StartsWith(
                    "sha256=",
                    Assert.Single(req.Headers.GetValues("X-CodeyBox-Signature")));
            });

            using var payload = JsonDocument.Parse(handler.RequestBodies[0]);
            Assert.Equal("work_item.done", payload.RootElement.GetProperty("event").GetString());
            Assert.Equal("UAT-WEBHOOK-1",
                payload.RootElement.GetProperty("workItem").GetProperty("externalId").GetString());
            Assert.Equal("uat-project",
                payload.RootElement.GetProperty("project").GetProperty("id").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvVar, null);
        }
    }

    [Fact]
    public async Task NullDispatcher_NoOpsForEmptyWebhookConfiguration()
    {
        var dispatcher = new NullWebhookDispatcher();

        var task = dispatcher.PublishAsync(Event("work_item.auto_retry"), CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public void BuildPayload_CarriesRetryBudgetSuggestionAndReleaseDetails()
    {
        var retryPayload = HttpWebhookDispatcher.BuildPayload(Event(
            "work_item.auto_retry",
            details: new { retryAt = "2026-05-14T00:00:00Z", failureKind = "quota" }));
        var releasePayload = HttpWebhookDispatcher.BuildPayload(new WebhookEvent
        {
            Event = "release.sync_conflict",
            Release = new Release
            {
                Id = ReleaseId.New(),
                ProjectId = new ProjectId("uat-project"),
                Name = "v1.0",
                State = ReleaseState.Open,
                BranchName = "release/v1.0",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            Project = Project(),
            Details = new { sourceBranch = "main", targetBranch = "release/v1.0" },
        });

        using var retry = JsonDocument.Parse(retryPayload);
        Assert.Equal("work_item.auto_retry", retry.RootElement.GetProperty("event").GetString());
        Assert.Equal("quota", retry.RootElement.GetProperty("details").GetProperty("failureKind").GetString());

        using var release = JsonDocument.Parse(releasePayload);
        Assert.Equal("release.sync_conflict", release.RootElement.GetProperty("event").GetString());
        Assert.Equal("main", release.RootElement.GetProperty("details").GetProperty("sourceBranch").GetString());
        Assert.False(release.RootElement.TryGetProperty("workItem", out _));
    }

    private static WebhookEvent Event(string name, object? details = null) => new()
    {
        Event = name,
        WorkItem = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("uat-project"),
            Title = "Webhook UAT item",
            Prompt = "run webhook uat",
            ExternalId = "UAT-WEBHOOK-1",
            Agent = AgentKind.Codex,
            BaseBranch = "main",
            WorkBranch = "feature/webhook-uat",
            State = WorkItemState.Done,
        },
        Project = Project(),
        Details = details,
    };

    private static Project Project() => new()
    {
        Id = new ProjectId("uat-project"),
        DisplayName = "Webhook UAT",
        RepositoryUrl = "https://user:secret@example.invalid/repo.git",
        DefaultAgent = AgentKind.Claude,
    };
}
