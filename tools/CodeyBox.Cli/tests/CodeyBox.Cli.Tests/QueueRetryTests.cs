using System.Net;
using System.Net.Http.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueRetryTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Retry_DefaultFrom_PostsWithWorkPhase()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Queued"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "retry", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.Contains("retry", captured.RequestUri!.ToString());
            var body = await captured.Content!.ReadFromJsonAsync(CliJsonContext.Default.RetryRequest);
            Assert.Equal("work", body!.From);
            Assert.Contains("Retrying", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Retry_FromAudit_PostsWithAuditPhase()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Queued"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "retry", "some-id", "--from", "audit"],
                factory);

            Assert.Equal(0, code);
            var body = await captured!.Content!.ReadFromJsonAsync(CliJsonContext.Default.RetryRequest);
            Assert.Equal("audit", body!.From);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("upstream")]
    public async Task Retry_AllValidFromValues_Succeed(string fromValue)
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return SampleData.WorkItemResponse(SampleData.WorkItem("Queued"));
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "retry", "some-id", "--from", fromValue],
                factory);

            Assert.Equal(0, code);
            var body = await captured!.Content!.ReadFromJsonAsync(CliJsonContext.Default.RetryRequest);
            Assert.Equal(fromValue, body!.From);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Retry_InvalidFrom_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => SampleData.WorkItemResponse());

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "retry", "some-id", "--from", "invalid-phase"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("--from", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Retry_Unauthorized_WritesToStderrNonZeroExit()
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
                ["queue", "retry", "some-id"],
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
}
