using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for CodeyBoxApiClient using a fake HttpMessageHandler.
/// Verifies the correct URL, HTTP method, and body shape for each API call.
/// </summary>
public sealed class CodeyBoxApiClientTests
{
    private static (CodeyBoxApiClient client, FakeHttpHandler handler) Build(
        string responseJson = "[]",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(responseJson, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://codeybox-test") };
        return (new CodeyBoxApiClient(http), handler);
    }

    [Fact]
    public async Task GetWorkItemsAsync_CallsCorrectEndpoint()
    {
        var (client, handler) = Build("[]");
        await client.GetWorkItemsAsync();
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/workitems", handler.LastPath);
    }

    [Fact]
    public async Task GetWorkItemsAsync_DeserializesResponse()
    {
        var json = """
            [{"id":"abc","projectId":"p","title":"t","prompt":"pr","agent":"claude",
              "state":"Queued","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
              "upstreamPushAttempts":0,"dependsOn":[],"dependsOnSatisfied":true,"queuePosition":0}]
            """;
        var (client, _) = Build(json);
        var items = await client.GetWorkItemsAsync();
        Assert.Single(items);
        Assert.Equal("abc", items[0].Id);
        Assert.Equal("t", items[0].Title);
    }

    [Fact]
    public async Task GetWorkItemAsync_CallsCorrectEndpoint()
    {
        var json = """
            {"id":"id1","projectId":"p","title":"t","prompt":"pr","agent":"claude",
             "state":"Queued","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
             "upstreamPushAttempts":0,"dependsOn":[],"dependsOnSatisfied":true,"queuePosition":0}
            """;
        var (client, handler) = Build(json);
        await client.GetWorkItemAsync("id1");
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/workitems/id1", handler.LastPath);
    }

    [Fact]
    public async Task GetWorkItemAsync_Returns_NullOnNotFound()
    {
        var (client, _) = Build("{}", HttpStatusCode.NotFound);
        var result = await client.GetWorkItemAsync("missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectsAsync_CallsCorrectEndpoint()
    {
        var (client, handler) = Build("[]");
        await client.GetProjectsAsync();
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/projects", handler.LastPath);
    }

    [Fact]
    public async Task GetFleetAgentStreamAggregateAsync_CallsCorrectEndpoint()
    {
        var (client, handler) = Build("""
            {"totalAgentDurationMs":0,"totalToolCalls":0,"byTool":[],"thinkingMs":0,"executingMs":0,"stallCount":0,"longestStallMs":0,"estimatedUsdTotal":0,"slowestToolCalls":[],"invocations":[]}
            """);

        await client.GetFleetAgentStreamAggregateAsync(50);

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("/workitems/agent-streams/aggregate?n=50", handler.LastPath);
    }

    [Fact]
    public async Task CreateWorkItemAsync_PostsToCorrectEndpoint()
    {
        var respJson = """
            {"id":"new1","projectId":"proj","title":"T","prompt":"P","agent":"claude",
             "state":"Queued","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
             "upstreamPushAttempts":0,"dependsOn":[],"dependsOnSatisfied":true,"queuePosition":0}
            """;
        var (client, handler) = Build(respJson, HttpStatusCode.Created);
        var req = new CreateWorkItemRequest { ProjectId = "proj", Title = "T", Prompt = "P" };
        var result = await client.CreateWorkItemAsync(req);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/workitems", handler.LastPath);
        Assert.NotNull(result);
        Assert.Equal("new1", result!.Id);
    }

    [Fact]
    public async Task CreateWorkItemAsync_SendsBodyWithCorrectFields()
    {
        var respJson = """
            {"id":"x","projectId":"proj","title":"T","prompt":"P","agent":"claude",
             "state":"Queued","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
             "upstreamPushAttempts":0,"dependsOn":["dep1"],"dependsOnSatisfied":false,"queuePosition":0}
            """;
        var (client, handler) = Build(respJson, HttpStatusCode.Created);
        var req = new CreateWorkItemRequest
        {
            ProjectId = "proj",
            Title = "T",
            Prompt = "P",
            Agent = "codex",
            DependsOn = ["dep1"],
        };
        await client.CreateWorkItemAsync(req);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastBody ?? "{}");
        Assert.Equal("proj", body.GetProperty("projectId").GetString());
        Assert.Equal("codex", body.GetProperty("agent").GetString());
        Assert.Single(body.GetProperty("dependsOn").EnumerateArray());
    }

    [Fact]
    public async Task PatchWorkItemAsync_SendsPatchToCorrectEndpoint()
    {
        var respJson = """
            {"id":"id1","projectId":"p","title":"updated","prompt":"P","agent":"claude",
             "state":"Queued","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
             "upstreamPushAttempts":0,"dependsOn":[],"dependsOnSatisfied":true,"queuePosition":0}
            """;
        var (client, handler) = Build(respJson);
        var req = new PatchWorkItemRequest { Title = "updated" };
        await client.PatchWorkItemAsync("id1", req);

        Assert.Equal(HttpMethod.Patch, handler.LastMethod);
        Assert.Equal("/workitems/id1", handler.LastPath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastBody ?? "{}");
        Assert.Equal("updated", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task PatchWorkItemAsync_Returns_NullOnConflict()
    {
        var (client, _) = Build("{}", HttpStatusCode.Conflict);
        var result = await client.PatchWorkItemAsync("id1", new PatchWorkItemRequest { Title = "x" });
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteWorkItemAsync_SendsDeleteToCorrectEndpoint()
    {
        var (client, handler) = Build("{}", HttpStatusCode.Accepted);
        await client.DeleteWorkItemAsync("id1");
        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("/workitems/id1", handler.LastPath);
    }

    [Fact]
    public async Task RetryWorkItemAsync_PostsToRetryEndpoint()
    {
        var (client, handler) = Build("{}", HttpStatusCode.Accepted);
        await client.RetryWorkItemAsync("id1", "audit");
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/workitems/id1/retry", handler.LastPath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastBody ?? "{}");
        Assert.Equal("audit", body.GetProperty("from").GetString());
    }

    [Fact]
    public async Task ReorderWorkItemsAsync_PostsToReorderEndpoint()
    {
        var (client, handler) = Build("{}", HttpStatusCode.NoContent);
        await client.ReorderWorkItemsAsync(["id1", "id2"]);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/workitems/reorder", handler.LastPath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastBody ?? "{}");
        var ids = body.GetProperty("ids").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["id1", "id2"], ids);
    }
}

/// <summary>
/// Fake HttpMessageHandler that captures the last request and returns a canned response.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly string _responseJson;
    private readonly HttpStatusCode _status;

    public HttpMethod? LastMethod { get; private set; }
    public string? LastPath { get; private set; }
    public string? LastBody { get; private set; }

    public FakeHttpHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseJson = responseJson;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastPath = request.RequestUri?.PathAndQuery;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
