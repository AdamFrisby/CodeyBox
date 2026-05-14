extern alias CodeyBoxAdminWeb;

using System.Net;
using System.Text.Json;
using CodeyBoxAdminWeb::CodeyBox.Admin.Web.Models;
using CodeyBoxAdminWeb::CodeyBox.Admin.Web.Services;

namespace CodeyBox.Tests.Uat.OperatorClients;

public sealed class AdminDashboardClientUatTests
{
    [Fact]
    public async Task QueueOperations_CallDocumentedApiEndpoints()
    {
        var handler = new CapturingAdminHandler(req =>
            req.Method == HttpMethod.Get && req.RequestUri!.PathAndQuery == "/workitems"
                ? "[]"
                : WorkItemJson("item-1", "Created", "Queued"));
        var client = new CodeyBoxApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://codeybox.test") });

        await client.GetWorkItemsAsync();
        Assert.Equal(("GET", "/workitems"), handler.LastMethodAndPath);

        await client.GetWorkItemAsync("item/1");
        Assert.Equal(("GET", "/workitems/item%2F1"), handler.LastMethodAndPath);

        await client.CreateWorkItemAsync(new CreateWorkItemRequest
        {
            ProjectId = "proj",
            Title = "Created",
            Prompt = "Prompt",
            Agent = "codex",
        });
        Assert.Equal(("POST", "/workitems"), handler.LastMethodAndPath);
        using (var createBody = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal("proj", createBody.RootElement.GetProperty("projectId").GetString());

        await client.PatchWorkItemAsync("item-1", new PatchWorkItemRequest { Title = "Updated" });
        Assert.Equal(("PATCH", "/workitems/item-1"), handler.LastMethodAndPath);

        await client.RetryWorkItemAsync("item-1", "merge");
        Assert.Equal(("POST", "/workitems/item-1/retry"), handler.LastMethodAndPath);
        using (var retryBody = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal("merge", retryBody.RootElement.GetProperty("from").GetString());

        await client.DeleteWorkItemAsync("item-1");
        Assert.Equal(("DELETE", "/workitems/item-1"), handler.LastMethodAndPath);
    }

    [Fact]
    public async Task QueueAndProjectPauseControls_CallDocumentedApiEndpoints()
    {
        var handler = new CapturingAdminHandler("""{"state":"Paused","pausedReason":"maintenance"}""");
        var client = new CodeyBoxApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://codeybox.test") });

        await client.GetQueueStatusAsync();
        Assert.Equal(("GET", "/queue/status"), handler.LastMethodAndPath);

        await client.PauseQueueAsync("maintenance");
        Assert.Equal(("POST", "/queue/pause"), handler.LastMethodAndPath);
        using (var pauseBody = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal("maintenance", pauseBody.RootElement.GetProperty("reason").GetString());

        await client.ResumeQueueAsync();
        Assert.Equal(("POST", "/queue/resume"), handler.LastMethodAndPath);

        await client.PauseProjectQueueAsync("proj/1", "budget");
        Assert.Equal(("POST", "/projects/proj%2F1/queue/pause"), handler.LastMethodAndPath);

        await client.ResumeProjectQueueAsync("proj/1");
        Assert.Equal(("POST", "/projects/proj%2F1/queue/resume"), handler.LastMethodAndPath);
    }

    [Fact]
    public async Task DetailSuggestionFleetPluginAndReleaseViews_CallDocumentedApiEndpoints()
    {
        var handler = new CapturingAdminHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/workitems/item-1/stdout-tail" => "stdout tail",
            "/workitems/item-1/timeline" => "{}",
            "/suggestions" => """{"items":[],"total":0,"offset":0,"limit":50}""",
            "/fleet/summary" => "[]",
            "/plugins" => "[]",
            "/releases" => "[]",
            _ => "{}",
        });
        var client = new CodeyBoxApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://codeybox.test") });

        await client.GetStdoutTailAsync("item-1");
        Assert.Equal(("GET", "/workitems/item-1/stdout-tail"), handler.LastMethodAndPath);

        await client.GetWorkItemTimelineAsync("item-1", kind: "audit", since: "2026-01-01T00:00:00Z", iteration: 2);
        Assert.Equal(
            ("GET", "/workitems/item-1/timeline?kind=audit&since=2026-01-01T00%3A00%3A00Z&iteration=2"),
            handler.LastMethodAndPath);

        await client.GetSuggestionsAsync(projectId: "proj", category: "test-coverage", severity: "notable");
        Assert.Equal(
            ("GET", "/suggestions?project=proj&category=test-coverage&severity=notable"),
            handler.LastMethodAndPath);

        await client.GetFleetSummaryAsync();
        Assert.Equal(("GET", "/fleet/summary"), handler.LastMethodAndPath);

        await client.GetAuditorPluginsAsync();
        Assert.Equal(("GET", "/plugins"), handler.LastMethodAndPath);

        await client.GetReleasesAsync(projectId: "proj", state: "open", limit: 10, offset: 5);
        Assert.Equal(("GET", "/releases?projectId=proj&state=open&limit=10&offset=5"), handler.LastMethodAndPath);
    }

    private static string WorkItemJson(string id, string title, string state) =>
        $$"""
        {
          "id":"{{id}}",
          "projectId":"proj",
          "title":"{{title}}",
          "prompt":"Prompt",
          "agent":"codex",
          "state":"{{state}}",
          "createdAt":"2026-01-01T00:00:00Z",
          "updatedAt":"2026-01-01T00:00:00Z",
          "upstreamPushAttempts":0,
          "dependsOn":[],
          "dependsOnSatisfied":true,
          "queuePosition":1
        }
        """;

    private sealed class CapturingAdminHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _responseJson;

        internal CapturingAdminHandler(string responseJson)
            : this(_ => responseJson)
        {
        }

        internal CapturingAdminHandler(Func<HttpRequestMessage, string> responseJson)
        {
            _responseJson = responseJson;
        }

        internal (string Method, string PathAndQuery)? LastMethodAndPath { get; private set; }
        internal string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethodAndPath = (request.Method.Method, request.RequestUri!.PathAndQuery);
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson(request), System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
