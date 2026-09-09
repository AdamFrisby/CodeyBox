using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueLogsTests
{
    private const string Id = "aabbccdd-0000-0000-0000-000000000000";
    private const char Esc = '\u001b';

    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    private static HttpResponseMessage TextResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain"),
    };

    [Fact]
    public async Task Logs_HumanReadable_GetsStdoutTailAndPrintsIt()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req => { captured = req; return TextResponse("building...\ndone\n"); });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "logs", Id], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/stdout-tail", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("building...", stdout);
            Assert.Contains("done", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Logs_Empty_PrintsPlaceholder()
    {
        var factory = MakeFactory(_ => TextResponse(""));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "logs", Id], factory);

            Assert.Equal(0, code);
            Assert.Contains("No output captured yet", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Logs_Json_WrapsTailInObject()
    {
        var factory = MakeFactory(_ => TextResponse("hello\n"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "logs", Id, "--json"], factory);

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(output.Out.ToString());
            Assert.Equal(Id, doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("hello\n", doc.RootElement.GetProperty("tail").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Logs_ControlChars_AreStrippedButNewlinesKept()
    {
        // Agent stdout is untrusted: escapes stripped, layout whitespace preserved.
        var factory = MakeFactory(_ => TextResponse("a\u001b[2Jb\nc\td\n"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "logs", Id], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.DoesNotContain(Esc, stdout);
            Assert.Contains("a[2Jb\nc\td", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
