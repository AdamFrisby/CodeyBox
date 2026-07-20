using System.Net;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueDiffTests
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

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Diff_HumanReadable_RequestsJsonAndPrintsSummaryAndBody()
    {
        const string body = """
{
  "workItemId": "aabbccdd-0000-0000-0000-000000000000",
  "baseBranch": "main",
  "workBranch": "codeybox/aabbccdd",
  "baseCommitSha": "1111111111111111111111111111111111111111",
  "workCommitSha": "2222222222222222222222222222222222222222",
  "filesChanged": 2,
  "linesAdded": 10,
  "linesRemoved": 3,
  "diff": "diff --git a/x b/x\n+added\n",
  "truncated": false
}
""";
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req => { captured = req; return JsonResponse(body); });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "diff", Id], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/diff", captured.RequestUri!.ToString());
            // The command negotiates JSON so it can render a summary header.
            Assert.Contains(
                captured.Headers.Accept,
                h => h.MediaType == "application/json");

            var stdout = output.Out.ToString();
            Assert.Contains("main (11111111)", stdout);
            Assert.Contains("codeybox/aabbccdd (22222222)", stdout);
            Assert.Contains("Files changed: 2", stdout);
            Assert.Contains("(+10 / -3)", stdout);
            Assert.Contains("diff --git a/x b/x", stdout);
            Assert.Contains("+added", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Diff_ControlCharsInBody_AreStrippedButNewlinesKept()
    {
        // Repo file content is untrusted; ANSI escapes must not survive to the terminal,
        // but the newlines that structure the diff must.
        const string body =
            "{\"workItemId\":\"x\",\"baseBranch\":\"main\",\"workBranch\":\"w\"," +
            "\"baseCommitSha\":\"a\",\"workCommitSha\":\"b\",\"filesChanged\":1," +
            "\"linesAdded\":1,\"linesRemoved\":0,\"diff\":\"line1\\u001b[31m\\nline2\\n\",\"truncated\":false}";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "diff", Id], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.DoesNotContain(Esc, stdout);
            Assert.Contains("line1[31m", stdout);
            Assert.Contains("line2", stdout);
            // Newline between the two diff lines is preserved.
            Assert.Contains("line1[31m\nline2", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Diff_NoContent_HumanReadable_PrintsUnavailableMessage()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "diff", Id], factory);

            Assert.Equal(0, code);
            Assert.Contains("No diff available", output.Out.ToString());
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Diff_NoContent_Json_PrintsEmptyObject()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "diff", Id, "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal("{}", output.Out.ToString().Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Diff_Json_PrintsRawResponse()
    {
        const string body = """{"workItemId":"x","filesChanged":0,"diff":""}""";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "diff", Id, "--json"], factory);

            Assert.Equal(0, code);
            Assert.Equal(body, output.Out.ToString().Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Diff_LargeDiffWithHint_PrintsHint()
    {
        const string body = """
{
  "workItemId": "x",
  "baseBranch": "main",
  "workBranch": "w",
  "baseCommitSha": "a",
  "workCommitSha": "b",
  "filesChanged": 5000,
  "linesAdded": 0,
  "linesRemoved": 0,
  "diff": null,
  "truncated": true,
  "hint": "This diff spans 5000 files and is too large to display inline. Review on GitHub."
}
""";
        var factory = MakeFactory(_ => JsonResponse(body));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "diff", Id], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.Contains("too large to display inline", stdout);
            Assert.Contains("diff truncated", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }
}
