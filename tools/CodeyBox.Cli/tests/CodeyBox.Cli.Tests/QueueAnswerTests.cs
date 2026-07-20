using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueAnswerTests
{
    private const string Id = "aabbccdd-0000-0000-0000-000000000000";

    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Answer_PostsQuestionIdAndAnswer_PrintsConfirmation()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"status\":\"answered\"}");
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "answer", Id, "q-1", "use postgres"], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Post, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/answer", captured.RequestUri!.ToString());
            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal("q-1", doc.RootElement.GetProperty("questionId").GetString());
            Assert.Equal("use postgres", doc.RootElement.GetProperty("answer").GetString());
            Assert.Contains("Answered question 'q-1'", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Answer_NoOp_PrintsAlreadyResolvedMessage()
    {
        var factory = MakeFactory(_ =>
            JsonResponse("{\"status\":\"no-op\",\"questionState\":\"answered\"}"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "answer", Id, "q-1", "text"], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("No change", stdout);
            Assert.Contains("already answered", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Answer_Quiet_PrintsOnlyStatus()
    {
        var factory = MakeFactory(_ => JsonResponse("{\"status\":\"answered\"}"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "answer", Id, "q-1", "text", "--quiet"], factory);

            Assert.Equal(0, code);
            Assert.Equal("answered", output.Out.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Answer_Json_PrintsRawResponse()
    {
        const string raw = "{\"status\":\"answered\"}";
        var factory = MakeFactory(_ => JsonResponse(raw));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "answer", Id, "q-1", "text", "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal(raw, output.Out.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Answer_ServerRejects_SurfacesErrorAndNonZeroExit()
    {
        // e.g. work item not waiting for input, or answer too long — the server owns the rule.
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":\"work item is not waiting for operator input\"}"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "answer", Id, "q-1", "text"], factory);

            Assert.Equal(1, code);
            Assert.Contains("Error (409)", output.Error.ToString());
            Assert.Contains("not waiting for operator input", output.Error.ToString());
            Assert.Empty(output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Answer_MissingApiKey_WritesErrorAndDoesNotCreateClient()
    {
        var factoryCalled = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            factoryCalled = true;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse("{\"status\":\"answered\"}")))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        using var output = new TestOutput();
        var code = await CliApp.InvokeAsync(["queue", "answer", Id, "q-1", "text"], factory);

        Assert.Equal(1, code);
        Assert.False(factoryCalled);
        Assert.Contains("API key not configured", output.Error.ToString());
    }
}
