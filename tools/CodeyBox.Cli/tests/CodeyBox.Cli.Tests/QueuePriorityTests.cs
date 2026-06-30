using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueuePriorityTests
{
    private const string WorkItemId = "aabbccdd-0000-0000-0000-000000000000";

    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Priority_PatchesPriorityAndPrintsServerValue()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return PriorityResponse(8);
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "priority", "external/id:42", "5"],
            factory);

        Assert.Equal(0, result.Code);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Patch, captured.Method);
        Assert.Equal("/workitems/external%2Fid%3A42/priority", captured.RequestUri!.AbsolutePath);
        AssertPriorityBody(capturedBody, 5);
        Assert.Contains("new priority: 8", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Priority_Quiet_PrintsOnlyServerPriority()
    {
        var factory = MakeFactory(_ => PriorityResponse(7));

        var result = await InvokeWithApiKeyAsync(
            ["queue", "priority", WorkItemId, "5", "--quiet"],
            factory);

        Assert.Equal(0, result.Code);
        Assert.Equal("7\n", NormalizeLines(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Priority_Json_PrintsRawJson()
    {
        const string raw = "{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"priority\":10,\"status\":\"updated\"}";
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw),
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "priority", WorkItemId, "10", "--json"],
            factory);

        Assert.Equal(0, result.Code);
        Assert.Equal(raw + "\n", NormalizeLines(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Priority_ApiFailure_WritesErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad priority\"}"),
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "priority", WorkItemId, "1001"],
            factory);

        Assert.Equal(1, result.Code);
        Assert.Contains("Error (400)", result.Stderr);
        Assert.Contains("bad priority", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Priority_NetworkFailure_WritesConnectionErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => throw new HttpRequestException("offline"));

        var result = await InvokeWithApiKeyAsync(
            ["queue", "priority", WorkItemId, "5"],
            factory);

        Assert.Equal(1, result.Code);
        Assert.Contains("Connection error: offline", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Priority_MissingApiKey_WritesErrorAndDoesNotCreateClient()
    {
        var factoryCalled = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            factoryCalled = true;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => PriorityResponse(1)))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        var result = await InvokeAsync(
            ["queue", "priority", WorkItemId, "5"],
            factory,
            setApiKey: false);

        Assert.Equal(1, result.Code);
        Assert.False(factoryCalled);
        Assert.Contains("API key not configured", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Priority_MissingPriority_WritesIntegrationError()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\"}"),
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "priority", WorkItemId, "5"],
            factory);

        Assert.Equal(1, result.Code);
        Assert.Contains("response missing numeric priority", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    private static HttpResponseMessage PriorityResponse(int priority) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"priority\":{priority}}}"),
    };

    private static Task<(int Code, string Stdout, string Stderr)> InvokeWithApiKeyAsync(
        string[] args,
        Func<ResolvedConfig, CodeyBoxClient> factory) =>
        InvokeAsync(args, factory, setApiKey: true);

    private static async Task<(int Code, string Stdout, string Stderr)> InvokeAsync(
        string[] args,
        Func<ResolvedConfig, CodeyBoxClient> factory,
        bool setApiKey)
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", setApiKey ? "test-key" : null);
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(args, factory);
            return (code, output.Out.ToString(), output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    private static void AssertPriorityBody(string? body, int expected)
    {
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(expected, root.GetProperty("priority").GetInt32());
        Assert.False(root.TryGetProperty("value", out _));
    }

    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
