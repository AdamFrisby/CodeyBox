using System.Net.Http.Json;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueTemplateTests
{
    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Template_PostsExpansionRequest()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return QueueTemplateResponse();
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync([
                "queue", "template", "templates/security",
                "--project", "myapp",
                "--agent", "codex",
                "--agent-class", "frontier",
                "--priority", "50",
                "--min-model-score", "90",
            ], factory);

            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Equal(HttpMethod.Post, captured.Method);
            Assert.EndsWith("/templates/queue", captured.RequestUri!.AbsolutePath);

            var body = await captured.Content!.ReadFromJsonAsync(CliJsonContext.Default.QueueTemplateRequest);
            Assert.Equal("templates/security", body!.Template);
            Assert.Equal("myapp", body.ProjectId);
            Assert.Equal("codex", body.Agent);
            Assert.Equal("frontier", body.AgentClassId);
            Assert.Equal(50, body.Priority);
            Assert.Equal(90, body.MinModelScore);

            Assert.Contains("Queued 2 check-and-act work items", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task TemplatesAlias_Quiet_PrintsOnlyCreatedIds()
    {
        var factory = MakeFactory(_ => QueueTemplateResponse());

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync([
                "queue", "templates", "security",
                "--project", "myapp",
                "--quiet",
            ], factory);

            Assert.Equal(0, code);
            Assert.Equal("11111111-1111-1111-1111-111111111111\n22222222-2222-2222-2222-222222222222",
                output.Out.ToString().Trim().ReplaceLineEndings("\n"));
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task QueueTemplatePathShortcut_UsesTemplateCommand()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            return QueueTemplateResponse();
        });

        using var output = new TestOutput();
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        try
        {
            var code = await CliApp.InvokeAsync([
                "queue", "templates/security",
                "--project", "myapp",
                "--quiet",
            ], factory);

            Assert.Equal(0, code);
            var body = await captured!.Content!.ReadFromJsonAsync(CliJsonContext.Default.QueueTemplateRequest);
            Assert.Equal("templates/security", body!.Template);
            Assert.Equal("myapp", body.ProjectId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    private static HttpResponseMessage QueueTemplateResponse() =>
        new(System.Net.HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new QueueTemplateResponse
            {
                Template = "security",
                Enqueued = 2,
                WorkItems =
                [
                    new()
                    {
                        Id = "11111111-1111-1111-1111-111111111111",
                        ProjectId = "myapp",
                        Title = "Check one",
                        Prompt = "p",
                        Agent = "claude",
                        State = "Queued",
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    new()
                    {
                        Id = "22222222-2222-2222-2222-222222222222",
                        ProjectId = "myapp",
                        Title = "Check two",
                        Prompt = "p",
                        Agent = "claude",
                        State = "Queued",
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                ],
            }, CliJsonContext.Default.QueueTemplateResponse),
        };
}
