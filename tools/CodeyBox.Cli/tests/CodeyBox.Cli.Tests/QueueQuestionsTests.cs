using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueueQuestionsTests
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

    private const string TwoQuestions = """
        [
          {"id":"1","workItemId":"aabbccdd-0000-0000-0000-000000000000","questionId":"q-open",
           "questionText":"Which database?","state":"open","askedAt":"2026-07-20T10:00:00Z",
           "answeredAt":null,"answerText":null,"answeredBy":null,"dismissedAt":null,"dismissReason":null},
          {"id":"2","workItemId":"aabbccdd-0000-0000-0000-000000000000","questionId":"q-done",
           "questionText":"Which region?","state":"answered","askedAt":"2026-07-20T10:05:00Z",
           "answeredAt":"2026-07-20T10:06:00Z","answerText":"us-east","answeredBy":null,
           "dismissedAt":null,"dismissReason":null}
        ]
        """;

    [Fact]
    public async Task Questions_HumanReadable_ListsQuestionsInTable()
    {
        HttpRequestMessage? captured = null;
        var factory = MakeFactory(req => { captured = req; return JsonResponse(TwoQuestions); });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "questions", Id], factory);

            Assert.Equal(0, code);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.EndsWith($"/workitems/{Id}/questions", captured.RequestUri!.ToString());
            var stdout = output.Out.ToString();
            Assert.Contains("q-open", stdout);
            Assert.Contains("Which database?", stdout);
            Assert.Contains("q-done", stdout);
            Assert.Contains("answered", stdout);
            Assert.Empty(output.Error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Questions_Empty_PrintsPlaceholder()
    {
        var factory = MakeFactory(_ => JsonResponse("[]"));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "questions", Id], factory);

            Assert.Equal(0, code);
            Assert.Contains("(no questions)", output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Questions_Quiet_PrintsOnlyOpenQuestionIds()
    {
        var factory = MakeFactory(_ => JsonResponse(TwoQuestions));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "questions", Id, "--quiet"], factory);

            Assert.Equal(0, code);
            var lines = output.Out.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[] { "q-open" }, lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Questions_Json_PrintsRawResponse()
    {
        var factory = MakeFactory(_ => JsonResponse(TwoQuestions));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "questions", Id, "--json"], factory);

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(output.Out.ToString());
            Assert.Equal(2, doc.RootElement.GetArrayLength());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Questions_UntrustedText_HasControlCharsStripped()
    {
        // Question text is agent-supplied. The server sends it as valid JSON with the escape
        // sequence encoded (); after decoding it is a real ESC char that must not reach
        // the terminal.
        var payload =
            "[{\"id\":\"1\",\"workItemId\":\"x\",\"questionId\":\"q1\"," +
            "\"questionText\":\"before\\u001b[2Jafter\",\"state\":\"open\"," +
            "\"askedAt\":\"2026-07-20T10:00:00Z\",\"answeredAt\":null,\"answerText\":null," +
            "\"answeredBy\":null,\"dismissedAt\":null,\"dismissReason\":null}]";
        var factory = MakeFactory(_ => JsonResponse(payload));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "questions", Id], factory);

            Assert.Equal(0, code);
            var stdout = output.Out.ToString();
            Assert.DoesNotContain(Esc, stdout);
            Assert.Contains("before[2Jafter", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Questions_ApiFailure_WritesErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"not found\"}"),
        });

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "test-key");
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["queue", "questions", Id], factory);

            Assert.Equal(1, code);
            Assert.Contains("Error (404)", output.Error.ToString());
            Assert.Empty(output.Out.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Questions_MissingApiKey_WritesErrorAndDoesNotCreateClient()
    {
        var factoryCalled = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            factoryCalled = true;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse("[]")))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        using var output = new TestOutput();
        var code = await CliApp.InvokeAsync(["queue", "questions", Id], factory);

        Assert.Equal(1, code);
        Assert.False(factoryCalled);
        Assert.Contains("API key not configured", output.Error.ToString());
    }
}
