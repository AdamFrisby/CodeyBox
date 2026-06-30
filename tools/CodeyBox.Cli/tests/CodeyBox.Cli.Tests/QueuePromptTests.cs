using System.Net;
using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class QueuePromptTests
{
    private const string WorkItemId = "aabbccdd-0000-0000-0000-000000000000";

    private static Func<ResolvedConfig, CodeyBoxClient> MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return config => new CodeyBoxClient(
            new HttpClient(new FakeHttpMessageHandler(handler))
            { BaseAddress = new Uri(config.ApiBaseUrl) });
    }

    [Fact]
    public async Task Prompt_PositionalText_PutsPromptAndPrintsServerRevision()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return PromptResponse(5);
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", "external/id:42", "hello world"],
            factory);

        Assert.Equal(0, result.Code);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured.Method);
        Assert.Equal("/workitems/external%2Fid%3A42/prompt", captured.RequestUri!.AbsolutePath);
        AssertPromptBody(capturedBody, "hello world");
        Assert.Contains("new prompt revision: 5", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Prompt_FromBareStdin_PutsPrompt()
    {
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return PromptResponse(6);
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId],
            factory,
            stdin: "prompt from stdin");

        Assert.Equal(0, result.Code);
        AssertPromptBody(capturedBody, "prompt from stdin");
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Prompt_FromPromptFileDash_ReadsStdin()
    {
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return PromptResponse(7);
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "--prompt-file", "-"],
            factory,
            stdin: "prompt from --prompt-file dash");

        Assert.Equal(0, result.Code);
        AssertPromptBody(capturedBody, "prompt from --prompt-file dash");
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Prompt_FromFile_PutsPrompt()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "hello from file");

        try
        {
            string? capturedBody = null;
            var factory = MakeFactory(req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return PromptResponse(12);
            });

            var result = await InvokeWithApiKeyAsync(
                ["queue", "prompt", WorkItemId, "--prompt-file", tempFile],
                factory);

            Assert.Equal(0, result.Code);
            AssertPromptBody(capturedBody, "hello from file");
            Assert.Contains("new prompt revision: 12", result.Stdout);
            Assert.Empty(result.Stderr);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Prompt_PositionalTextAndPromptFile_WritesErrorAndDoesNotCreateClient()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "prompt from file");

        try
        {
            var factoryCalled = false;
            var result = await InvokeWithApiKeyAsync(
                ["queue", "prompt", WorkItemId, "inline prompt", "--prompt-file", tempFile],
                MakeTrackingFactory(() => factoryCalled = true));

            Assert.Equal(1, result.Code);
            Assert.False(factoryCalled);
            Assert.Contains("provide either prompt text or --prompt-file", result.Stderr);
            Assert.Empty(result.Stdout);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Prompt_EmptyStdin_WritesErrorAndDoesNotCreateClient()
    {
        var factoryCalled = false;
        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId],
            MakeTrackingFactory(() => factoryCalled = true),
            stdin: "");

        Assert.Equal(1, result.Code);
        Assert.False(factoryCalled);
        Assert.Contains("prompt is required", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Prompt_EmptyPromptFile_WritesErrorAndDoesNotCreateClient()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            var factoryCalled = false;
            var result = await InvokeWithApiKeyAsync(
                ["queue", "prompt", WorkItemId, "--prompt-file", tempFile],
                MakeTrackingFactory(() => factoryCalled = true));

            Assert.Equal(1, result.Code);
            Assert.False(factoryCalled);
            Assert.Contains("prompt is required", result.Stderr);
            Assert.Empty(result.Stdout);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Prompt_Quiet_PrintsOnlyServerRevision()
    {
        var factory = MakeFactory(_ => PromptResponse(42));

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "test", "--quiet"],
            factory);

        Assert.Equal(0, result.Code);
        Assert.Equal("42\n", NormalizeLines(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Prompt_Json_PrintsRawJson()
    {
        const string raw = "{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"promptRevision\":99}";
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw),
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "test", "--json"],
            factory);

        Assert.Equal(0, result.Code);
        Assert.Equal(raw + "\n", NormalizeLines(result.Stdout));
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Prompt_ExactLimitStdin_PutsPrompt()
    {
        var prompt = new string('x', 64 * 1024);
        string? capturedBody = null;
        var factory = MakeFactory(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return PromptResponse(13);
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId],
            factory,
            stdin: prompt);

        Assert.Equal(0, result.Code);
        AssertPromptBody(capturedBody, prompt);
        Assert.Contains("new prompt revision: 13", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Prompt_OversizedStdin_WritesErrorAndDoesNotCallApi()
    {
        var factoryCalled = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            factoryCalled = true;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => PromptResponse(1)))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId],
            factory,
            stdin: new string('x', 64 * 1024 + 1));

        Assert.NotEqual(0, result.Code);
        Assert.False(factoryCalled);
        Assert.Contains("prompt exceeds 64 KB limit", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Prompt_OversizedPromptFile_WritesErrorAndDoesNotCreateClient()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, new string('x', 64 * 1024 + 1));

        try
        {
            var factoryCalled = false;
            var result = await InvokeWithApiKeyAsync(
                ["queue", "prompt", WorkItemId, "--prompt-file", tempFile],
                MakeTrackingFactory(() => factoryCalled = true));

            Assert.Equal(1, result.Code);
            Assert.False(factoryCalled);
            Assert.Contains("prompt exceeds 64 KB limit", result.Stderr);
            Assert.Empty(result.Stdout);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Prompt_MissingPromptFile_WritesErrorAndDoesNotCreateClient()
    {
        var missingFile = Path.Combine(Path.GetTempPath(), $"codeybox-missing-{Guid.NewGuid():N}.txt");
        var factoryCalled = false;

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "--prompt-file", missingFile],
            MakeTrackingFactory(() => factoryCalled = true));

        Assert.Equal(1, result.Code);
        Assert.False(factoryCalled);
        Assert.NotEmpty(result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Prompt_ApiFailure_WritesErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":\"blocked\"}"),
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "test"],
            factory);

        Assert.Equal(1, result.Code);
        Assert.Contains("Error (409)", result.Stderr);
        Assert.Contains("blocked", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Prompt_NetworkFailure_WritesConnectionErrorAndNonZeroExit()
    {
        var factory = MakeFactory(_ => throw new HttpRequestException("offline"));

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "test"],
            factory);

        Assert.Equal(1, result.Code);
        Assert.Contains("Connection error: offline", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Prompt_MissingApiKey_WritesErrorAndDoesNotCreateClient()
    {
        var factoryCalled = false;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            factoryCalled = true;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => PromptResponse(1)))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        var result = await InvokeAsync(
            ["queue", "prompt", WorkItemId, "test"],
            factory,
            setApiKey: false);

        Assert.Equal(1, result.Code);
        Assert.False(factoryCalled);
        Assert.Contains("API key not configured", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Prompt_MissingPromptRevision_WritesIntegrationError()
    {
        var factory = MakeFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"aabbccdd-0000-0000-0000-000000000000\"}"),
        });

        var result = await InvokeWithApiKeyAsync(
            ["queue", "prompt", WorkItemId, "test"],
            factory);

        Assert.Equal(1, result.Code);
        Assert.Contains("response missing numeric promptRevision", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    private static HttpResponseMessage PromptResponse(int revision) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"id\":\"aabbccdd-0000-0000-0000-000000000000\",\"promptRevision\":{revision}}}"),
    };

    private static Func<ResolvedConfig, CodeyBoxClient> MakeTrackingFactory(Action onCreate)
    {
        return config =>
        {
            onCreate();
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => PromptResponse(1)))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };
    }

    private static Task<(int Code, string Stdout, string Stderr)> InvokeWithApiKeyAsync(
        string[] args,
        Func<ResolvedConfig, CodeyBoxClient> factory,
        string? stdin = null) =>
        InvokeAsync(args, factory, setApiKey: true, stdin);

    private static async Task<(int Code, string Stdout, string Stderr)> InvokeAsync(
        string[] args,
        Func<ResolvedConfig, CodeyBoxClient> factory,
        bool setApiKey,
        string? stdin = null)
    {
        var previousIn = Console.In;
        if (stdin is not null)
            Console.SetIn(new StringReader(stdin));

        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", setApiKey ? "test-key" : null);
        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(args, factory);
            return (code, output.Out.ToString(), output.Error.ToString());
        }
        finally
        {
            if (stdin is not null)
                Console.SetIn(previousIn);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", null);
        }
    }

    private static void AssertPromptBody(string? body, string expected)
    {
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(expected, root.GetProperty("prompt").GetString());
        Assert.False(root.TryGetProperty("text", out _));
    }

    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
