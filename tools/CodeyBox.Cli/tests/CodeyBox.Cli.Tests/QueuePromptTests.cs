using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueuePromptTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Prompt_Success_PrintsUpdatedAndExitsZero()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"promptRevision\":5}")
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "prompt", "aabbccdd-0000-0000-0000-000000000000", "--prompt", "hello world"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Put, captured.Method);
            Assert.Contains("aabbccdd-0000-0000-0000-000000000000/prompt", captured.RequestUri!.ToString());
            Assert.Contains("new prompt revision: 5", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Prompt_FromFile_Success_PrintsUpdatedAndExitsZero()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "hello from file");

        try
        {
            HttpRequestMessage? captured = null;
            string? capturedBody = null;
            var factory = MakeFactory(req =>
            {
                captured = req;
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"promptRevision\":12}")
                };
            });

            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
            using var output = new TestOutput();
            try
            {
                var code = await CliApp.InvokeAsync(
                    ["queue", "prompt", "aabbccdd-0000-0000-0000-000000000000", "--prompt-file", tempFile],
                    factory);

                Assert.Equal(0, code);
                Assert.NotNull(captured);
                Assert.Equal(HttpMethod.Put, captured.Method);
                Assert.Contains("hello from file", capturedBody!);
                Assert.Contains("new prompt revision: 12", output.Out.ToString());
                Assert.Empty(output.Error.ToString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Prompt_Quiet_PrintsOnlyRevision()
    {
        var factory = MakeFactory(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"promptRevision\":42}")
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "prompt", "aabbccdd-0000-0000-0000-000000000000", "--prompt", "test", "--quiet"],
                factory);

            Assert.Equal(0, code);
            Assert.Equal("42\n", output.Out.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Prompt_Json_PrintsRawJson()
    {
        var factory = MakeFactory(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"promptRevision\":99}")
            };
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "prompt", "aabbccdd-0000-0000-0000-000000000000", "--prompt", "test", "--json"],
                factory);

            Assert.Equal(0, code);
            Assert.Contains("\"promptRevision\":99", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Prompt_MissingPrompt_WritesErrorToStderrNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "prompt", "some-id"],
                factory);

            Assert.NotEqual(0, code);
            Assert.Contains("Error: provide --prompt or --prompt-file.", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
