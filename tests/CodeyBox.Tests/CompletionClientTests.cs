using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification tests for the configuration-driven <see cref="ICompletionClient"/>
/// and the migration of its two callers onto it.
/// </summary>
public sealed class CompletionClientTests
{
    // ── Destination is entirely configuration-driven ──

    [Fact]
    public async Task CompleteAsync_ChatCompletions_SendsExactUrlModelAndHeaders()
    {
        var handler = new RequestCapturingHandler(_ => ChatResponse("hello", 10, 3));
        var client = BuildClient(handler, new CompletionClientOptions());

        var result = await client.CompleteAsync(new CompletionRequest
        {
            Endpoint = "https://llm.internal.example/v1/chat",
            Model = "custom-model-1",
            Messages = [new CompletionMessage("system", "sys"), new CompletionMessage("user", "hi")],
            WireApi = CompletionWireApi.OpenAiChatCompletions,
            BearerToken = "bearer-secret-1",
            ExtraHeaders = new Dictionary<string, string> { ["x-custom"] = "header-value-1" },
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://llm.internal.example/v1/chat", request.Uri.ToString());
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("bearer-secret-1", request.Authorization?.Parameter);
        Assert.Equal("header-value-1", request.Headers["x-custom"]);

        using var body = JsonDocument.Parse(request.Body, new JsonDocumentOptions());
        Assert.Equal("custom-model-1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task CompleteAsync_ResponsesWireShape_RoundTrips()
    {
        var handler = new RequestCapturingHandler(_ => ResponsesResponse("resp-text", 7, 2));
        var client = BuildClient(handler, new CompletionClientOptions());

        var result = await client.CompleteAsync(new CompletionRequest
        {
            Endpoint = "https://gateway.example/v1/responses",
            Model = "resp-model",
            Messages = [new CompletionMessage("user", "question")],
            WireApi = CompletionWireApi.OpenAiResponses,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("resp-text", result.Text);
        Assert.Equal(7, result.Usage!.InputTokens);
        Assert.Equal(2, result.Usage.OutputTokens);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.True(body.RootElement.TryGetProperty("input", out _));
        Assert.True(body.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task CompleteAsync_AnthropicAndGeminiShapes_ParseTheirResponses()
    {
        var anthropicHandler = new RequestCapturingHandler(_ => AnthropicResponse("anthropic-text"));
        var anthropicClient = BuildClient(anthropicHandler, new CompletionClientOptions());
        var anthropic = await anthropicClient.CompleteAsync(new CompletionRequest
        {
            Endpoint = "https://anthropic.example/v1/messages",
            Model = "m1",
            Messages = [new CompletionMessage("system", "s"), new CompletionMessage("user", "u")],
            WireApi = CompletionWireApi.AnthropicMessages,
        });
        Assert.True(anthropic.IsSuccess);
        Assert.Equal("anthropic-text", anthropic.Text);
        using (var body = JsonDocument.Parse(anthropicHandler.Requests[0].Body))
        {
            Assert.Equal("s", body.RootElement.GetProperty("system").GetString());
        }

        var geminiHandler = new RequestCapturingHandler(_ => GeminiResponse("gemini-text"));
        var geminiClient = BuildClient(geminiHandler, new CompletionClientOptions());
        var gemini = await geminiClient.CompleteAsync(new CompletionRequest
        {
            Endpoint = "https://gemini.example/v1beta/models/m:generateContent",
            Model = "m",
            Messages = [new CompletionMessage("user", "prompt")],
            WireApi = CompletionWireApi.GeminiGenerateContent,
        });
        Assert.True(gemini.IsSuccess);
        Assert.Equal("gemini-text", gemini.Text);
    }

    // ── Failures are distinct results, never exceptions ──

    [Fact]
    public async Task CompleteAsync_401_500_Timeout_Empty_MapToDistinctResults()
    {
        var unauthorized = await BuildClient(
            new RequestCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            new CompletionClientOptions()).CompleteAsync(ChatRequest("https://x.example/"));
        var serverError = await BuildClient(
            new RequestCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            new CompletionClientOptions()).CompleteAsync(ChatRequest("https://x.example/"));
        var timeoutClient = BuildClient(
            new RequestCapturingHandler(async (req, ct) =>
            {
                await Task.Delay(5000, ct);
                return ChatResponse("late", 1, 1);
            }),
            new CompletionClientOptions { RequestTimeoutSeconds = 1 });
        var timedOut = await timeoutClient.CompleteAsync(ChatRequest("https://x.example/"));
        var empty = await BuildClient(
            new RequestCapturingHandler(_ => ChatResponse("", 5, 0)),
            new CompletionClientOptions()).CompleteAsync(ChatRequest("https://x.example/"));

        Assert.Equal(CompletionStatus.AuthenticationFailed, unauthorized.Status);
        Assert.Equal(CompletionStatus.ServerError, serverError.Status);
        Assert.Equal(CompletionStatus.TimedOut, timedOut.Status);
        Assert.Equal(CompletionStatus.EmptyCompletion, empty.Status);
        Assert.NotEqual(unauthorized.Status, serverError.Status);
    }

    [Fact]
    public async Task CompleteAsync_TransportFailure_ReturnsResult()
    {
        var client = BuildClient(new ThrowingHandler(), new CompletionClientOptions());
        var result = await client.CompleteAsync(ChatRequest("https://unreachable.example/"));
        Assert.Equal(CompletionStatus.TransportFailed, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CompleteAsync_Refusal_MapsToRefused()
    {
        var handler = new RequestCapturingHandler(_ => ChatRefusal("policy"));
        var client = BuildClient(handler, new CompletionClientOptions());
        var result = await client.CompleteAsync(ChatRequest("https://x.example/"));
        Assert.Equal(CompletionStatus.Refused, result.Status);
    }

    [Fact]
    public async Task CompleteAsync_PromptTooLarge_SendsNoRequest()
    {
        var handler = new RequestCapturingHandler(_ => ChatResponse("x", 1, 1));
        var client = BuildClient(handler, new CompletionClientOptions { MaxPromptChars = 10 });
        var result = await client.CompleteAsync(new CompletionRequest
        {
            Endpoint = "https://x.example/",
            Model = "m",
            Messages = [new CompletionMessage("user", "this prompt is far too long")],
        });
        Assert.Equal(CompletionStatus.PromptTooLarge, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_OversizeResponse_RejectedWithoutFullBuffering()
    {
        var content = new CountingReadContent(200_000);
        var handler = new RequestCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var client = BuildClient(handler, new CompletionClientOptions { MaxResponseBytes = 1024 });
        var result = await client.CompleteAsync(ChatRequest("https://x.example/"));
        Assert.Equal(CompletionStatus.ResponseTooLarge, result.Status);
        Assert.True(content.BytesRead <= 1024 + 8192, $"read {content.BytesRead} bytes");
        Assert.True(content.BytesRead < 200_000);
    }

    [Fact]
    public async Task CompleteAsync_NeverLogsSecretsOrBody()
    {
        var logger = new CapturingLogger<CompletionClient>();
        var handler = new RequestCapturingHandler(_ => ChatResponse("body-secret-123", 4, 1));
        var factory = new SingleClientFactory(handler);
        var client = new CompletionClient(factory, () => new CompletionClientOptions(), logger);

        var result = await client.CompleteAsync(new CompletionRequest
        {
            Endpoint = "https://x.example/",
            Model = "m",
            Messages = [new CompletionMessage("user", "prompt")],
            BearerToken = "secret-token-xyz",
            ExtraHeaders = new Dictionary<string, string> { ["x-api-key"] = "header-secret-abc" },
        });

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(logger.Messages);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain("secret-token-xyz", message);
            Assert.DoesNotContain("header-secret-abc", message);
            Assert.DoesNotContain("body-secret-123", message);
        }
    }

    // ── Caller migration parity ──

    [Fact]
    public async Task CheckAndAct_FailureFallsThroughToNextProviderInsteadOfThrowing()
    {
        var handler = new RequestCapturingHandler(req =>
            req.RequestUri!.ToString().Contains("failing")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GeminiVerdict("ok"), Encoding.UTF8, "application/json"),
                });
        var options = new CheckAndActCompletionOptions
        {
            ProviderOrder = ["custom-failing", CheckAndActCompletionProviders.GeminiApiKey],
            GeminiApiKey = "gemini-key",
            GeminiApiKeyEnvVars = [],
            OpenAiApiKeyEnvVars = [],
            AnthropicApiKeyEnvVars = [],
            CustomProviders =
            [
                new CustomCompletionProviderConfig
                {
                    Name = "custom-failing",
                    Endpoint = "https://failing.example/v1/chat",
                    Model = "custom-model",
                    WireApi = CompletionWireApi.OpenAiChatCompletions,
                    ApiKey = "custom-key",
                    ApiKeyEnvVars = [],
                },
            ],
        };
        var runner = new DefaultCheckAndActCompletionRunner(
            new SingleClientFactory(handler), options, NullLogger<DefaultCheckAndActCompletionRunner>.Instance);

        var result = await runner.TryCompleteAsync(TestCompletionRequest());

        Assert.NotNull(result);
        Assert.Equal(CheckAndActCompletionProviders.GeminiApiKey, result!.Provider);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CheckAndAct_AllProvidersFail_ReturnsNullWithoutThrowing()
    {
        var handler = new RequestCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var options = new CheckAndActCompletionOptions
        {
            ProviderOrder = [CheckAndActCompletionProviders.GeminiApiKey],
            GeminiApiKey = "gemini-key",
            GeminiApiKeyEnvVars = [],
            OpenAiApiKeyEnvVars = [],
            AnthropicApiKeyEnvVars = [],
        };
        var runner = new DefaultCheckAndActCompletionRunner(
            new SingleClientFactory(handler), options, NullLogger<DefaultCheckAndActCompletionRunner>.Instance);

        var result = await runner.TryCompleteAsync(TestCompletionRequest());

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAndAct_OptionsAreReadLivePerCall()
    {
        var handler = new RequestCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GeminiVerdict("ok"), Encoding.UTF8, "application/json"),
        });
        var options = new CheckAndActCompletionOptions
        {
            ProviderOrder = [CheckAndActCompletionProviders.GeminiApiKey],
            GeminiApiKey = "gemini-key",
            GeminiApiKeyEnvVars = [],
            OpenAiApiKeyEnvVars = [],
            AnthropicApiKeyEnvVars = [],
        };
        var runner = new DefaultCheckAndActCompletionRunner(
            new SingleClientFactory(handler), () => options, NullLogger<DefaultCheckAndActCompletionRunner>.Instance);

        await runner.TryCompleteAsync(TestCompletionRequest());
        options.GeminiModel = "gemini-live-model";
        await runner.TryCompleteAsync(TestCompletionRequest());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("gemini-2.5-pro", handler.Requests[0].Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("gemini-live-model", handler.Requests[1].Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changelog_UsesConfiguredBaseUrlModelAndKey()
    {
        var handler = new RequestCapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(AnthropicJson("## [v9]\n### Added\n- thing ([#1])\n"), Encoding.UTF8, "application/json"),
        });
        var opts = new ChangelogOptions
        {
            GeneratorBaseUrl = "https://gateway.example/anthropic",
            GeneratorModelId = "custom-claude",
            GeneratorApiKey = "configured-key",
        };
        var generator = new ClaudeChangelogGenerator(
            new SingleClientFactory(handler),
            NullLogger<ClaudeChangelogGenerator>.Instance,
            opts);

        var entry = await generator.GenerateAsync(new ChangelogRequest
        {
            ProjectId = new ProjectId("test"),
            FromTag = "v8",
            ToTag = "v9",
            PullRequests = [new(1, "Thing", "body", "2026-05-01", [], [])],
        }, CancellationToken.None);

        Assert.Contains("thing", entry.Markdown, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://gateway.example/anthropic", request.Uri.ToString());
        Assert.Equal("configured-key", request.Headers["x-api-key"]);
        Assert.Contains("custom-claude", request.Body, StringComparison.Ordinal);
    }

    // ── Helpers ──

    private static CompletionClient BuildClient(HttpMessageHandler handler, CompletionClientOptions opts) =>
        new(new SingleClientFactory(handler), () => opts, NullLogger<CompletionClient>.Instance);

    private static CompletionRequest ChatRequest(string endpoint) => new()
    {
        Endpoint = endpoint,
        Model = "m",
        Messages = [new CompletionMessage("user", "hi")],
    };

    private static CheckAndActCompletionRequest TestCompletionRequest()
    {
        var blocks = new CheckAndActCompletionPromptBlocks("system", "review", "question?");
        return new CheckAndActCompletionRequest(
            WorkItemId.New(), "check", null, blocks, new CheckAndActCompletionCredentials());
    }

    private static string GeminiVerdict(string evidence)
    {
        var verdict = $"{CheckAndActPipeline.StartSentinel}\n{{\"answer\": true, \"evidence\": \"{evidence}\", \"confidence\": \"high\"}}\n{CheckAndActPipeline.EndSentinel}";
        return JsonSerializer.Serialize(new
        {
            response = new
            {
                candidates = new[]
                {
                    new { content = new { parts = new[] { new { text = verdict } } } },
                },
                usageMetadata = new { promptTokenCount = 100, candidatesTokenCount = 5 },
            },
        });
    }

    private static HttpResponseMessage ChatResponse(string text, int promptTokens, int outputTokens) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = text } } },
                usage = new { prompt_tokens = promptTokens, completion_tokens = outputTokens },
            }), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage ChatRefusal(string reason) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = (string?)null, refusal = reason } } },
                usage = new { prompt_tokens = 3, completion_tokens = 0 },
            }), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage ResponsesResponse(string text, int inputTokens, int outputTokens) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[] { new { type = "output_text", text } },
                    },
                },
                usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            }), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage AnthropicResponse(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text } },
                usage = new { input_tokens = 100, output_tokens = 5 },
            }), Encoding.UTF8, "application/json"),
        };

    private static string AnthropicJson(string text) => JsonSerializer.Serialize(new
    {
        content = new[] { new { type = "text", text } },
        usage = new { input_tokens = 100, output_tokens = 5 },
    });

    private static HttpResponseMessage GeminiResponse(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new { content = new { parts = new[] { new { text } } } },
                },
                usageMetadata = new { promptTokenCount = 100, candidatesTokenCount = 5 },
            }), Encoding.UTF8, "application/json"),
        };

    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RequestCapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((req, _) => Task.FromResult(respond(req)))
        {
        }

        public RequestCapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<CapturedCompletionRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedCompletionRequest(
                request.RequestUri!,
                request.Headers.Authorization,
                request.Headers.ToDictionary(static h => h.Key, static h => string.Join("", h.Value)),
                body));
            return await _respond(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record CapturedCompletionRequest(
        Uri Uri,
        System.Net.Http.Headers.AuthenticationHeaderValue? Authorization,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class CountingReadContent : HttpContent
    {
        private readonly byte[] _data;
        public long BytesRead;

        public CountingReadContent(int size)
        {
            _data = new byte[size];
            Array.Fill(_data, (byte)'x');
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingStream(_data, this));

        private sealed class CountingStream : MemoryStream
        {
            private readonly CountingReadContent _parent;

            public CountingStream(byte[] data, CountingReadContent parent)
                : base(data, writable: false) => _parent = parent;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                // NB: MemoryStream.ReadAsync routes through Read(Span) internally,
                // so only count here to avoid double-counting.
                var read = await base.ReadAsync(buffer, ct).ConfigureAwait(false);
                _parent.BytesRead += read;
                return read;
            }
        }
    }
}
