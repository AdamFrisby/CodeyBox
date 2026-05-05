using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueCancelTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Cancel_Success_PrintsCancelledAndExitsZero()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "cancel", "aabbccdd-0000-0000-0000-000000000000"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Delete, captured.Method);
            Assert.Contains("aabbccdd-0000-0000-0000-000000000000", captured.RequestUri!.ToString());
            Assert.Contains("Cancelled", output.Out.ToString());
            Assert.Contains("aabbccdd-0000-0000-0000-000000000000", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Cancel_Unauthorized_WritesToStderrNonZeroExit()
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
                ["queue", "cancel", "some-id"],
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
    public async Task Cancel_NotFound_WritesToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not found"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "cancel", "nonexistent-id"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("404", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Cancel_NetworkFailure_WritesToStderrNonZeroExit()
    {
        Func<ResolvedConfig, CodeyBoxClient> factory = config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(_ =>
                throw new HttpRequestException("Connection refused")))
            { BaseAddress = new Uri(config.ApiBaseUrl) });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "cancel", "some-id"], factory);

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
