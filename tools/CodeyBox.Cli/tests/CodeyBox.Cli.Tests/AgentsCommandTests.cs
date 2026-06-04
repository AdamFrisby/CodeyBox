using System.Net;
using System.Net.Http.Json;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class AgentsCommandTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task AgentsPause_PostsAgentPauseWithReasonAndDuration()
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
                ["agents", "pause", "claude", "--reason", "reserve quota", "--for", "6h"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.EndsWith("/agents/claude/pause", captured.RequestUri!.ToString());
            var body = await captured.Content!.ReadFromJsonAsync(CliJsonContext.Default.PauseAgentRequest);
            Assert.Equal("reserve quota", body!.Reason);
            Assert.Equal(21600, body.DurationSeconds);
            Assert.Contains("Agent claude paused", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task AgentsResume_PostsAgentResume()
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
            var code = await CliApp.InvokeAsync(["agents", "resume", "gemini"], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.EndsWith("/agents/gemini/resume", captured.RequestUri!.ToString());
            Assert.Contains("Agent gemini resumed", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
