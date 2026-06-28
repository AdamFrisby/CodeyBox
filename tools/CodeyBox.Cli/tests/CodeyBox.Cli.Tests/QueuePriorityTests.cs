using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueuePriorityTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Priority_Success_PrintsUpdatedAndExitsZero()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"priority\":5}")
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "priority", "aabbccdd-0000-0000-0000-000000000000", "5"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Patch, captured.Method);
            Assert.Contains("aabbccdd-0000-0000-0000-000000000000/priority", captured.RequestUri!.ToString());
            Assert.Contains("new priority: 5", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Priority_Quiet_PrintsOnlyPriority()
    {
        var factory = MakeFactory(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"priority\":7}")
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "priority", "aabbccdd-0000-0000-0000-000000000000", "7", "--quiet"],
                factory);

            Assert.Equal(0, code);
            Assert.Equal("7\n", output.Out.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Priority_Json_PrintsRawJson()
    {
        var factory = MakeFactory(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"priority\":10,\"status\":\"updated\"}")
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "priority", "aabbccdd-0000-0000-0000-000000000000", "10", "--json"],
                factory);

            Assert.Equal(0, code);
            Assert.Contains("\"priority\":10", output.Out.ToString());
            Assert.Contains("\"status\":\"updated\"", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
