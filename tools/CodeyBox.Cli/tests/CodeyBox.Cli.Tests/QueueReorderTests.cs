using System.Net;
using System.Net.Http.Json;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueReorderTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Reorder_SingleId_PostsWithIdsBody()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "reorder", "abc-123"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.Contains("/workitems/reorder", captured.RequestUri!.ToString());
            Assert.NotNull(captured.Content);
            var body = await captured.Content.ReadFromJsonAsync(CliJsonContext.Default.ReorderRequest);
            Assert.NotNull(body);
            Assert.Single(body.Ids);
            Assert.Equal("abc-123", body.Ids[0]);
            Assert.Contains("Queue reordered", output.Out.ToString());
            Assert.Contains("(1 item)", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Reorder_MultipleIds_PostsWithIdsBody()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "reorder", "id-a", "id-b", "id-c"],
                factory);

            Assert.Equal(0, code);
            var body = await captured!.Content!.ReadFromJsonAsync(CliJsonContext.Default.ReorderRequest);
            Assert.NotNull(body);
            Assert.Equal(3, body.Ids.Length);
            Assert.Equal("id-a", body.Ids[0]);
            Assert.Equal("id-b", body.Ids[1]);
            Assert.Equal("id-c", body.Ids[2]);
            Assert.Contains("Queue reordered", output.Out.ToString());
            Assert.Contains("(3 items)", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Reorder_Unauthorized_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "reorder", "some-id"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("401", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Reorder_NetworkFailure_WritesToStderrNonZeroExit()
    {
        Func<ResolvedConfig, CodeyBoxClient> factory = config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(_ =>
                throw new HttpRequestException("Connection refused")))
            { BaseAddress = new Uri(config.ApiBaseUrl) });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "reorder", "some-id"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("Connection", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
