using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class CheckAndActCompletionRunnerTests
{
    [Fact]
    public async Task TryCompleteAsync_DisabledReturnsNullWithoutCallingProvider()
    {
        var handler = new CapturingHandler(_ => JsonResponse(GeminiResponse(BuildVerdict(true, "ok"))));
        var runner = BuildRunner(handler, new CheckAndActCompletionOptions
        {
            Enabled = false,
            ProviderOrder = [CheckAndActCompletionProviders.GeminiApiKey],
            GeminiApiKey = "gemini-api-key",
            GeminiApiKeyEnvVars = [],
        });

        var result = await runner.TryCompleteAsync(BuildRequest());

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryCompleteAsync_PrefersGeminiOAuthAndSendsThreeBlockPrompt()
    {
        var handler = new CapturingHandler(_ => JsonResponse(GeminiResponse(BuildVerdict(true, "ok"))));
        var runner = BuildRunner(handler, new CheckAndActCompletionOptions
        {
            ProviderOrder = [CheckAndActCompletionProviders.GeminiOAuth, CheckAndActCompletionProviders.GeminiApiKey],
            GeminiApiKey = "fallback-api-key",
            GeminiApiKeyEnvVars = [],
        });

        var result = await runner.TryCompleteAsync(BuildRequest(
            credentials: new CheckAndActCompletionCredentials(Gemini: GeminiOAuthCred("oauth-token"))));

        Assert.NotNull(result);
        Assert.Equal(CheckAndActCompletionProviders.GeminiOAuth, result!.Provider);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(DefaultCheckAndActCompletionRunner.GeminiOAuthEndpoint), request.Uri);
        Assert.Equal("Bearer", request.Authorization?.Scheme);
        Assert.Equal("oauth-token", request.Authorization?.Parameter);
        Assert.False(request.Headers.ContainsKey("x-goog-api-key"));

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("models/gemini-2.5-pro", body.RootElement.GetProperty("model").GetString());
        var prompt = body.RootElement
            .GetProperty("request")
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("[1: fixed generic system prompt]", prompt);
        Assert.Contains("[2: the code/diff under review]", prompt);
        Assert.Contains("[3: the specific check question]", prompt);
        Assert.Contains("src/Foo.cs", prompt);
        Assert.Contains("Is SQL built from user input?", prompt);
    }

    [Fact]
    public async Task TryCompleteAsync_ReportsCacheHitForRepeatedSystemAndDiffPrefix()
    {
        var handler = new CapturingHandler(_ => JsonResponse(GeminiResponse(BuildVerdict(false, "clean"), promptTokens: 120)));
        var runner = BuildRunner(handler, new CheckAndActCompletionOptions
        {
            ProviderOrder = [CheckAndActCompletionProviders.GeminiApiKey],
            GeminiApiKey = "gemini-api-key",
            GeminiApiKeyEnvVars = [],
        });

        var first = await runner.TryCompleteAsync(BuildRequest(question: "Is SQL built from user input?"));
        var second = await runner.TryCompleteAsync(BuildRequest(question: "Does the diff introduce magic numbers?"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first!.Usage.CacheHit);
        Assert.Equal(0, first.Usage.CachedInputTokens);
        Assert.True(second!.Usage.CacheHit);
        Assert.True(second.Usage.CachedInputTokens > 0);
        Assert.True(second.Usage.InputTokens < first.Usage.InputTokens);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TryCompleteAsync_AnthropicProviderIgnoresClaudeSubscriptionOAuthAndFallsBack()
    {
        var handler = new CapturingHandler(_ => JsonResponse(AnthropicResponse(BuildVerdict(true, "ok"))));
        var runner = BuildRunner(handler, new CheckAndActCompletionOptions
        {
            ProviderOrder = [CheckAndActCompletionProviders.AnthropicApiKey],
            AnthropicApiKeyEnvVars = [],
        });

        var result = await runner.TryCompleteAsync(BuildRequest(
            credentials: new CheckAndActCompletionCredentials(Claude: ClaudeOAuthCred("subscription-oauth-token"))));

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryCompleteAsync_AnthropicApiKeyUsesXApiKeyNotBearer()
    {
        var handler = new CapturingHandler(_ => JsonResponse(AnthropicResponse(BuildVerdict(true, "ok"))));
        var runner = BuildRunner(handler, new CheckAndActCompletionOptions
        {
            ProviderOrder = [CheckAndActCompletionProviders.AnthropicApiKey],
            AnthropicApiKey = "anthropic-api-key",
            AnthropicApiKeyEnvVars = [],
        });

        var result = await runner.TryCompleteAsync(BuildRequest());

        Assert.NotNull(result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), request.Uri);
        Assert.Null(request.Authorization);
        Assert.Equal("anthropic-api-key", request.Headers["x-api-key"]);
    }

    private static DefaultCheckAndActCompletionRunner BuildRunner(
        CapturingHandler handler,
        CheckAndActCompletionOptions options)
    {
        options.HttpClientName = "check-completion-test";
        options.OpenAiApiKeyEnvVars = [];
        return new DefaultCheckAndActCompletionRunner(
            new FakeHttpClientFactory(handler),
            options,
            NullLogger<DefaultCheckAndActCompletionRunner>.Instance);
    }

    private static CheckAndActCompletionRequest BuildRequest(
        string question = "Is SQL built from user input?",
        CheckAndActCompletionCredentials? credentials = null)
    {
        var blocks = CheckAndActPipeline.BuildCompletionPromptBlocks(
            new CheckAndActSpec
            {
                Question = question,
                OnYes = new OnYesActionSpec { Title = "Fix", Prompt = "Remediate" },
            },
            """
            Base branch: main

            ### File: src/Foo.cs

            var sql = $"select * from users where id = {id}";
            """);
        return new CheckAndActCompletionRequest(
            WorkItemId.New(),
            "check",
            null,
            blocks,
            credentials ?? new CheckAndActCompletionCredentials(),
            ModelId: null);
    }

    private static AgentCredential GeminiOAuthCred(string token) =>
        new(AgentKind.Gemini,
            new Dictionary<string, string>
            {
                [GeminiConstants.OAuthCredsEnvVar] = JsonSerializer.Serialize(new { access_token = token }),
            },
            new Dictionary<string, string>());

    private static AgentCredential ClaudeOAuthCred(string token) =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token },
            new Dictionary<string, string>());

    private static string BuildVerdict(bool answer, string evidence)
    {
        var ans = answer ? "true" : "false";
        return $"{CheckAndActPipeline.StartSentinel}\n{{\"answer\": {ans}, \"evidence\": \"{evidence}\", \"confidence\": \"high\"}}\n{CheckAndActPipeline.EndSentinel}";
    }

    private static string GeminiResponse(string text, int promptTokens = 100, int outputTokens = 5) =>
        $$"""
          {
            "response": {
              "candidates": [
                { "content": { "parts": [ { "text": {{JsonSerializer.Serialize(text)}} } ] } }
              ],
              "usageMetadata": {
                "promptTokenCount": {{promptTokens}},
                "candidatesTokenCount": {{outputTokens}}
              }
            }
          }
          """;

    private static string AnthropicResponse(string text) =>
        $$"""
          {
            "content": [ { "type": "text", "text": {{JsonSerializer.Serialize(text)}} } ],
            "usage": {
              "input_tokens": 100,
              "cache_read_input_tokens": 0,
              "output_tokens": 5
            }
          }
          """;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
        {
            _ = name;
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization,
                request.Headers.ToDictionary(static h => h.Key, static h => string.Join("", h.Value)),
                body));
            return _respond(request);
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
