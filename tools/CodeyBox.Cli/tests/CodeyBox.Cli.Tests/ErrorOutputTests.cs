using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class ErrorOutputTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(HttpStatusCode status, string body = "Unauthorized")
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(status) { Content = new StringContent(body) }))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Error_Unauthorized_WritesToStderrNonZeroExit()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls"], MakeFactory(HttpStatusCode.Unauthorized));

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
    public async Task Error_NotFound_WritesToStderrNonZeroExit()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            Func<ResolvedConfig, CodeyBoxClient> factory = config => new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.NotFound)))
                { BaseAddress = new Uri(config.ApiBaseUrl) });

            var code = await CliApp.InvokeAsync(["queue", "show", "nonexistent-id"], factory);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("not found", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Error_InternalServerError_WritesToStderrNonZeroExit()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "ls"],
                MakeFactory(HttpStatusCode.InternalServerError, "Internal error details"));

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("500", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Error_MissingApiKey_PrintsHintAndNonZeroExit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "ls"]);

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("codeybox configure", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Error_MissingPrompt_WritesToStderr()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "add", "--project", "foo", "--title", "bar"],
                MakeFactory(HttpStatusCode.OK));

            Assert.NotEqual(0, code);
            Assert.Empty(output.Out.ToString());
            Assert.Contains("--prompt", output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
