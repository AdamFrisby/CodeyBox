using System.Net.Http.Json;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueAddTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Add_PostsCorrectShape()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return SampleData.CreatedWorkItemResponse();
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "add", "--project", "testproject", "--title", "My title", "--prompt", "Hello prompt"],
                factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.EndsWith("/workitems", captured.RequestUri!.AbsolutePath);

            var body = await captured.Content!.ReadFromJsonAsync(CliJsonContext.Default.CreateWorkItemRequest);
            Assert.Equal("testproject", body!.ProjectId);
            Assert.Equal("My title", body.Title);
            Assert.Equal("Hello prompt", body.Prompt);
            Assert.Null(body.Agent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Add_AllOptions_PostsCorrectShape()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return SampleData.CreatedWorkItemResponse();
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync([
                "queue", "add",
                "--project", "myapp",
                "--title", "Refactor",
                "--prompt", "Do refactor",
                "--agent", "gemini",
                "--work-branch", "feat/refactor",
                "--push-upstream",
                "--depends-on", "id-1",
                "--depends-on", "id-2",
            ], factory);

            Assert.Equal(0, code);
            var body = await captured!.Content!.ReadFromJsonAsync(CliJsonContext.Default.CreateWorkItemRequest);
            Assert.Equal("gemini", body!.Agent);
            Assert.Equal("feat/refactor", body.WorkBranch);
            Assert.True(body.PushUpstream);
            Assert.Equal(["id-1", "id-2"], body.DependsOn);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Add_PromptFileFromStdin_ReadsStdin()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return SampleData.CreatedWorkItemResponse();
        });

        using var output = new TestOutput();
        var prevIn = Console.In;
        Console.SetIn(new StringReader("Prompt from stdin"));
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync(
                ["queue", "add", "--project", "foo", "--title", "T", "--prompt-file", "-"],
                factory);

            Assert.Equal(0, code);
            var body = await captured!.Content!.ReadFromJsonAsync(CliJsonContext.Default.CreateWorkItemRequest);
            Assert.Equal("Prompt from stdin", body!.Prompt);
        }
        finally
        {
            Console.SetIn(prevIn);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Add_Quiet_PrintsOnlyId()
    {
        var item = SampleData.WorkItem();
        var factory = MakeFactory(_ => SampleData.CreatedWorkItemResponse(item));

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            await CliApp.InvokeAsync(
                ["queue", "add", "--project", "foo", "--title", "T", "--prompt", "P", "--quiet"],
                factory);

            Assert.Equal(item.Id, output.Out.ToString().Trim());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
