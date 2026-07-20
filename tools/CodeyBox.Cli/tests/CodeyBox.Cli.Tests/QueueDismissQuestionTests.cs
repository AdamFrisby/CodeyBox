using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueDismissQuestionTests
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
    public async Task Dismiss_WithReason_PostsQuestionIdAndReason()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"status\":\"dismissed\"}");
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "dismiss-question", Id, "q-1", "--reason", "obsolete"], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Post, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/dismiss-question", captured.RequestUri!.ToString());
            using var doc = JsonDocument.Parse(capturedBody!);
            Assert.Equal("q-1", doc.RootElement.GetProperty("questionId").GetString());
            Assert.Equal("obsolete", doc.RootElement.GetProperty("reason").GetString());
            Assert.Contains("Dismissed question 'q-1'", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Dismiss_WithoutReason_SendsNonEmptyDefaultReason()
    {
        // The server requires a non-empty reason; the CLI supplies a default when --reason is omitted.
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"status\":\"dismissed\"}");
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "dismiss-question", Id, "q-1"], factory);

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(capturedBody!);
            var reason = doc.RootElement.GetProperty("reason").GetString();
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Dismiss_NoOp_PrintsAlreadyResolvedMessage()
    {
        var factory = MakeFactory(_ =>
            JsonResponse("{\"status\":\"no-op\",\"questionState\":\"dismissed\"}"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "dismiss-question", Id, "q-1"], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("No change", stdout);
            Assert.Contains("already dismissed", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Dismiss_Quiet_PrintsOnlyStatus()
    {
        var factory = MakeFactory(_ => JsonResponse("{\"status\":\"dismissed\"}"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "dismiss-question", Id, "q-1", "--quiet"], factory);

            Assert.Equal(0, code);
            Assert.Equal("dismissed", output.Out.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Dismiss_Json_PrintsRawResponse()
    {
        const string raw = "{\"status\":\"dismissed\"}";
        var factory = MakeFactory(_ => JsonResponse(raw));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "dismiss-question", Id, "q-1", "--json"], factory);

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
    public async Task Dismiss_ServerRejects_SurfacesErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"question 'q-1' not found\"}"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "dismiss-question", Id, "q-1"], factory);

            Assert.Equal(1, code);
            Assert.Contains("Error (404)", output.Error.ToString());
            Assert.Empty(output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Dismiss_MissingApiKey_WritesErrorAndDoesNotCreateClient()
    {
        var factoryCalled = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            factoryCalled = true;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse("{\"status\":\"dismissed\"}")))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        using var output = new TestOutput();
        var code = await CliApp.InvokeAsync(["queue", "dismiss-question", Id, "q-1"], factory);

        Assert.Equal(1, code);
        Assert.False(factoryCalled);
        Assert.Contains("API key not configured", output.Error.ToString());
    }
}
