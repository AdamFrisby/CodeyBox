using System.Text.Json;

namespace CodeyBox.Tests.Uat.OperatorClients;

public sealed class CliClientUatTests
{
    [Fact]
    public async Task Version_PrintsCompiledVersionAsSingleLineWithoutResolvingApiConfiguration()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "codeybox-bad-cli-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "config.json"), "{ this is not json");

        try
        {
            var result = await OperatorClientProcess.RunCodeyBoxCliAsync(
                ["--api-url", "http://203.0.113.10:5050", "--api-key", "flag-key", "version"],
                new Dictionary<string, string?> { ["CODEYBOX_CLI_CONFIG_DIR"] = configDir });

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(OperatorClientProcess.ReadCliVersionFromSource(), result.Stdout.Trim());
            Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Empty(result.Stderr);
        }
        finally
        {
            Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public async Task RootHelp_RegistersQueueConfigureAndVersionCommands()
    {
        var result = await OperatorClientProcess.RunCodeyBoxCliAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("queue", result.Stdout);
        Assert.Contains("configure", result.Stdout);
        Assert.Contains("version", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task QueueListJson_UsesConfiguredEndpointAndBearerToken()
    {
        await using var api = new FakeOperatorApiServer();
        api.EnqueueResponse($$"""
            [{
              "id":"aabbccdd-0000-0000-0000-000000000001",
              "projectId":"proj",
              "title":"List item",
              "prompt":"Prompt",
              "agent":"codex",
              "state":"Queued",
              "createdAt":"2026-01-01T00:00:00Z",
              "updatedAt":"2026-01-01T00:00:00Z",
              "upstreamPushAttempts":0,
              "dependsOn":[],
              "dependsOnSatisfied":true,
              "dependsOnExternalIds":{},
              "queuePosition":1
            }]
            """);

        var result = await OperatorClientProcess.RunCodeyBoxCliAsync(
            ["--api-url", api.BaseUrl, "queue", "ls", "--json"],
            new Dictionary<string, string?> { ["CODEYBOX_CLI_API_KEY"] = "uat-api-key" });

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(api.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/workitems", request.Target);
        Assert.Equal("Bearer uat-api-key", request.Headers["Authorization"]);

        using var doc = JsonDocument.Parse(result.Stdout);
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("aabbccdd-0000-0000-0000-000000000001", item.GetProperty("id").GetString());
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task QueueAdd_PostsTypedRequestAndCanRenderQuietId()
    {
        await using var api = new FakeOperatorApiServer();
        api.EnqueueResponse(req =>
        {
            using var body = JsonDocument.Parse(req.Body);
            var root = body.RootElement;
            Assert.Equal("proj", root.GetProperty("projectId").GetString());
            Assert.Equal("Operator UAT", root.GetProperty("title").GetString());
            Assert.Equal("Do the operator thing", root.GetProperty("prompt").GetString());
            Assert.Equal("codex", root.GetProperty("agent").GetString());
            Assert.True(root.GetProperty("pushUpstream").GetBoolean());
            Assert.Equal("dep-1", Assert.Single(root.GetProperty("dependsOn").EnumerateArray()).GetString());

            return new FakeOperatorApiResponse(201, WorkItemJson("new-item-0001", "Operator UAT", "Queued"), "application/json");
        });

        var result = await OperatorClientProcess.RunCodeyBoxCliAsync(
            [
                "--api-url", api.BaseUrl,
                "queue", "add",
                "--project", "proj",
                "--title", "Operator UAT",
                "--prompt", "Do the operator thing",
                "--agent", "codex",
                "--push-upstream",
                "--depends-on", "dep-1",
                "--quiet",
            ],
            new Dictionary<string, string?> { ["CODEYBOX_CLI_API_KEY"] = "uat-api-key" });

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(api.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/workitems", request.Target);
        Assert.Equal("new-item-0001", result.Stdout.Trim());
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task QueueShowRetryAndCancel_TargetDocumentedEndpoints()
    {
        var show = await RunSingleRequestCommandAsync(
            ["queue", "show", "item-1"],
            WorkItemJson("item-1", "Shown item", "Working"));
        Assert.Equal(("GET", "/workitems/item-1"), (show.Method, show.Target));

        var retry = await RunSingleRequestCommandAsync(
            ["queue", "retry", "item-1", "--from", "audit"],
            WorkItemJson("item-1", "Retried item", "Queued"));
        Assert.Equal(("POST", "/workitems/item-1/retry"), (retry.Method, retry.Target));
        using (var retryBody = JsonDocument.Parse(retry.Body))
            Assert.Equal("audit", retryBody.RootElement.GetProperty("from").GetString());

        var cancel = await RunSingleRequestCommandAsync(
            ["queue", "cancel", "item-1"],
            "{}");
        Assert.Equal(("DELETE", "/workitems/item-1"), (cancel.Method, cancel.Target));
    }

    [Fact]
    public async Task QueueNonSuccess_PrintsConciseErrorAndReturnsNonZero()
    {
        await using var api = new FakeOperatorApiServer();
        api.EnqueueResponse("Unauthorized", System.Net.HttpStatusCode.Unauthorized);

        var result = await OperatorClientProcess.RunCodeyBoxCliAsync(
            ["--api-url", api.BaseUrl, "queue", "ls"],
            new Dictionary<string, string?> { ["CODEYBOX_CLI_API_KEY"] = "bad-key" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Error (401): HTTP 401: Unauthorized", result.Stderr);
        Assert.DoesNotContain(" at ", result.Stderr);
    }

    private static async Task<FakeOperatorApiRequest> RunSingleRequestCommandAsync(
        IReadOnlyList<string> args,
        string responseJson)
    {
        await using var api = new FakeOperatorApiServer();
        api.EnqueueResponse(responseJson);

        var fullArgs = new List<string> { "--api-url", api.BaseUrl };
        fullArgs.AddRange(args);
        var result = await OperatorClientProcess.RunCodeyBoxCliAsync(
            fullArgs,
            new Dictionary<string, string?> { ["CODEYBOX_CLI_API_KEY"] = "uat-api-key" });

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        return Assert.Single(api.Requests);
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
          "dependsOnExternalIds":{},
          "queuePosition":1
        }
        """;
}
