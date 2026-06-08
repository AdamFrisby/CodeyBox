using System.Net;
using System.Text;
using System.Text.Json;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeAgentRunner.RunTextOnlyAsync"/> — the raw
/// <c>/v1/messages</c> path used by the rebase-resolver router and the
/// advisory merge security review when Claude is the chosen agent.
///
/// <para>The load-bearing fix exercised here: configured Claude model ids are
/// undated CLI aliases (e.g. <c>claude-opus-4-7</c>) which the Messages API
/// answers with HTTP 404, even when <c>/v1/models</c> lists them. The runner
/// resolves the alias to a dated canonical id via <c>GET /v1/models</c>
/// before posting; a probe failure leaves the requested id untouched so the
/// fallback is best-effort. Account-safety: the path is restricted to
/// <c>ANTHROPIC_API_KEY</c> — subscription OAuth on <c>/v1/messages</c> is the
/// wrong-client-shape that risks account termination.</para>
/// </summary>
public sealed class ClaudeAgentRunnerTextOnlyTests
{
    private const string ApiKey = "sk-ant-test-key";
    private const string ModelsJson = """
        {"data":[
          {"id":"claude-haiku-4-5-20251001","type":"model"},
          {"id":"claude-opus-4-7-20260101","type":"model"},
          {"id":"claude-opus-4-7-20260315","type":"model"},
          {"id":"claude-sonnet-4-6-20260201","type":"model"}
        ]}
        """;

    private static AgentCredential ApiKeyCredential() =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = ApiKey },
            new Dictionary<string, string>());

    private static AgentCredential OAuthOnlyCredential() =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat01-x" },
            new Dictionary<string, string>());

    private static ClaudeAgentRunner BuildRunner(HttpMessageHandler handler, string? defaultModel = "claude-opus-4-7")
    {
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = defaultModel,
            });
        return new ClaudeAgentRunner(defaults, rotationPusher: null, sanitizerConfig: null,
            textOnlyHttp: new HttpClient(handler));
    }

    // ── Credential viability ──────────────────────────────────────────────────

    [Fact]
    public void GetTextOnlyUnavailabilityReason_NullCredential_ReturnsReason()
    {
        var runner = new ClaudeAgentRunner();
        Assert.Equal(ClaudeAgentRunner.MissingApiKeyReason,
            runner.GetTextOnlyUnavailabilityReason(credential: null));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_OAuthOnly_ReturnsReason()
    {
        // Subscription OAuth on /v1/messages risks account termination — the
        // router must walk past Claude when only OAuth is configured.
        var runner = new ClaudeAgentRunner();
        Assert.Equal(ClaudeAgentRunner.MissingApiKeyReason,
            runner.GetTextOnlyUnavailabilityReason(OAuthOnlyCredential()));
    }

    [Fact]
    public void GetTextOnlyUnavailabilityReason_ApiKeyPresent_ReturnsNull()
    {
        var runner = new ClaudeAgentRunner();
        Assert.Null(runner.GetTextOnlyUnavailabilityReason(ApiKeyCredential()));
    }

    [Fact]
    public async Task RunTextOnlyAsync_NullCredential_ReturnsMissingCredential()
    {
        var runner = new ClaudeAgentRunner();
        var result = await runner.RunTextOnlyAsync("hi", credential: null);
        Assert.False(result.Success);
        Assert.Equal(ClaudeAgentRunner.MissingApiKeyReason, result.Error);
    }

    [Fact]
    public async Task RunTextOnlyAsync_OAuthOnly_ReturnsMissingCredential()
    {
        var runner = new ClaudeAgentRunner();
        var result = await runner.RunTextOnlyAsync("hi", OAuthOnlyCredential());
        Assert.False(result.Success);
        Assert.Equal(ClaudeAgentRunner.MissingApiKeyReason, result.Error);
    }

    // ── Alias → canonical resolution (ResolveCanonicalModelId pure function) ──

    [Fact]
    public void ResolveCanonicalModelId_PicksLatestDatedVariant()
    {
        var available = new[]
        {
            "claude-opus-4-7-20260101",
            "claude-opus-4-7-20260315",
            "claude-opus-4-7-20260201",
            "claude-sonnet-4-6-20260201",
        };
        Assert.Equal("claude-opus-4-7-20260315",
            ClaudeAgentRunner.ResolveCanonicalModelId("claude-opus-4-7", available));
    }

    [Fact]
    public void ResolveCanonicalModelId_NoMatch_ReturnsRequestedUnchanged()
    {
        var available = new[] { "claude-haiku-4-5-20251001" };
        Assert.Equal("claude-opus-4-7",
            ClaudeAgentRunner.ResolveCanonicalModelId("claude-opus-4-7", available));
    }

    [Fact]
    public void ResolveCanonicalModelId_PrefersDatedOverExactAliasMatch()
    {
        // Anthropic's /v1/models has been observed to list an undated alias
        // alongside its dated variants while /v1/messages 404s the alias —
        // so we always prefer the dated form when one is available.
        var available = new[]
        {
            "claude-opus-4-7",
            "claude-opus-4-7-20260315",
        };
        Assert.Equal("claude-opus-4-7-20260315",
            ClaudeAgentRunner.ResolveCanonicalModelId("claude-opus-4-7", available));
    }

    [Fact]
    public void ResolveCanonicalModelId_EmptyList_ReturnsRequestedUnchanged()
    {
        Assert.Equal("claude-opus-4-7",
            ClaudeAgentRunner.ResolveCanonicalModelId("claude-opus-4-7", Array.Empty<string>()));
    }

    [Fact]
    public void ParseModelIds_WellFormedJson_ReturnsIds()
    {
        var ids = ClaudeAgentRunner.ParseModelIds(ModelsJson);
        Assert.Contains("claude-opus-4-7-20260315", ids);
        Assert.Contains("claude-haiku-4-5-20251001", ids);
        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public void ParseModelIds_InvalidJson_ReturnsEmpty()
    {
        var ids = ClaudeAgentRunner.ParseModelIds("not json");
        Assert.Empty(ids);
    }

    [Fact]
    public void ParseModelIds_MissingDataField_ReturnsEmpty()
    {
        var ids = ClaudeAgentRunner.ParseModelIds("""{"object":"list"}""");
        Assert.Empty(ids);
    }

    [Fact]
    public void ExtractResponseText_ConcatenatesTextParts()
    {
        var text = ClaudeAgentRunner.ExtractResponseText("""
            {"id":"msg_x","content":[
              {"type":"text","text":"hello "},
              {"type":"text","text":"world"}
            ]}
            """);
        Assert.Equal("hello world", text);
    }

    [Fact]
    public void ExtractResponseText_MissingContent_ReturnsEmpty()
    {
        Assert.Equal(string.Empty,
            ClaudeAgentRunner.ExtractResponseText("""{"id":"msg_x"}"""));
    }

    // ── Wire-level request shape with alias resolution ────────────────────────

    [Fact]
    public async Task RunTextOnlyAsync_AliasModelId_ResolvesViaModelListBeforePosting()
    {
        // Reproduces the original 404 bug: configured undated alias would
        // 404 against /v1/messages. With the fix, the runner first fetches
        // /v1/models, picks the latest dated variant, and posts THAT.
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, ModelsJson),
            messagesResponder: req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var model = doc.RootElement.GetProperty("model").GetString();
                if (model == "claude-opus-4-7-20260315")
                    return (HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok-canonical"}]}""");
                // Alias-shaped id reaches /v1/messages → 404 (the original bug).
                return (HttpStatusCode.NotFound, """{"type":"error","error":{"type":"not_found_error","message":"model not found"}}""");
            });
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync("hi", ApiKeyCredential(), modelId: "claude-opus-4-7");

        Assert.True(result.Success, $"expected success after alias resolution; got '{result.Summary}' / '{result.Error}'");
        Assert.Equal("ok-canonical", result.Output);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(ClaudeAgentRunner.ModelsEndpoint, handler.Calls[0].Uri.ToString());
        Assert.Equal(ClaudeAgentRunner.MessagesEndpoint, handler.Calls[1].Uri.ToString());
    }

    [Fact]
    public async Task RunTextOnlyAsync_RequestShape_PostsExpectedHeadersAndBody()
    {
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, ModelsJson),
            messagesResponder: _ => (HttpStatusCode.OK, """{"content":[{"type":"text","text":"reply-body"}]}"""));
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync("hello prompt", ApiKeyCredential(), modelId: "claude-opus-4-7");

        // SUT outcome assertions: a successful 2xx must be reflected in the
        // returned record. A test that asserts only against mock invocations
        // would pass even if RunTextOnlyAsync ignored the response and
        // returned failure — these guards block that regression.
        Assert.True(result.Success, $"expected success; got '{result.Summary}' / '{result.Error}'");
        Assert.Equal("reply-body", result.Output);
        Assert.Null(result.Error);

        var modelsCall = handler.Calls[0];
        Assert.Equal(HttpMethod.Get, modelsCall.Method);
        Assert.Equal(ApiKey, modelsCall.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", modelsCall.Headers["anthropic-version"]);

        var messagesCall = handler.Calls[1];
        Assert.Equal(HttpMethod.Post, messagesCall.Method);
        Assert.Equal(ApiKey, messagesCall.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", messagesCall.Headers["anthropic-version"]);
        Assert.Equal("application/json", messagesCall.ContentType);

        using var body = JsonDocument.Parse(messagesCall.Body!);
        Assert.Equal("claude-opus-4-7-20260315", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(8192, body.RootElement.GetProperty("max_tokens").GetInt32());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hello prompt", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task RunTextOnlyAsync_ModelListFails_FallsBackToRequestedIdWithoutFailingTheCall()
    {
        // The /v1/models probe is best-effort: a 500 there must not degrade
        // a working /v1/messages call. The post still happens with the
        // requested id (which may already be canonical, or may 404 — but
        // that's a separate failure, not a cascading regression).
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.InternalServerError, "boom"),
            messagesResponder: req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var model = doc.RootElement.GetProperty("model").GetString();
                Assert.Equal("claude-opus-4-7-20260315", model);
                return (HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}]}""");
            });
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync(
            "hi", ApiKeyCredential(), modelId: "claude-opus-4-7-20260315");

        Assert.True(result.Success);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public async Task RunTextOnlyAsync_MessagesReturns404_SurfacesFailureWithModelDiagnostic()
    {
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, """{"data":[]}"""),
            messagesResponder: _ => (HttpStatusCode.NotFound, """{"error":{"message":"model not found"}}"""));
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync(
            "hi", ApiKeyCredential(), modelId: "claude-opus-4-7");

        Assert.False(result.Success);
        Assert.Contains("HTTP 404", result.Summary);
        Assert.Contains("claude-opus-4-7", result.Summary);
        Assert.Contains("model not found", result.Error);
    }

    [Fact]
    public async Task RunTextOnlyAsync_DefaultModelId_UsedWhenNoOverride()
    {
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, ModelsJson),
            messagesResponder: req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                // The configured default "claude-opus-4-7" must resolve to a dated variant.
                Assert.Equal("claude-opus-4-7-20260315", doc.RootElement.GetProperty("model").GetString());
                return (HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok-default"}]}""");
            });
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync("hi", ApiKeyCredential(), modelId: null);
        Assert.True(result.Success);
        Assert.Equal("ok-default", result.Output);
    }

    [Fact]
    public async Task RunTextOnlyAsync_NoDefaultAndNoOverride_ReturnsError()
    {
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, ModelsJson),
            messagesResponder: _ => throw new InvalidOperationException("/v1/messages must not be called when model id is missing"));
        var runner = BuildRunner(handler, defaultModel: null);

        var result = await runner.RunTextOnlyAsync("hi", ApiKeyCredential(), modelId: null);
        Assert.False(result.Success);
        Assert.Contains("missing model id", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task RunTextOnlyAsync_AlreadyDatedId_StillPostsThatExactId()
    {
        // If the operator explicitly passes the dated id, alias resolution
        // is a no-op (no prefix match), and the post uses the id as-is.
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, ModelsJson),
            messagesResponder: req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("claude-haiku-4-5-20251001", doc.RootElement.GetProperty("model").GetString());
                return (HttpStatusCode.OK, """{"content":[{"type":"text","text":"haiku"}]}""");
            });
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync(
            "hi", ApiKeyCredential(), modelId: "claude-haiku-4-5-20251001");
        Assert.True(result.Success);
        Assert.Equal("haiku", result.Output);
    }

    [Fact]
    public async Task RunTextOnlyAsync_AliasResolved_ButMessagesFails_SummaryShowsBothIds()
    {
        // When alias resolution rewrote the id and the POST still fails, the
        // diagnostic must surface BOTH ids so operators see the rewrite as
        // separate from a pure-model-typo failure.
        var handler = new FakeAnthropicHandler(
            modelsResponse: (HttpStatusCode.OK, ModelsJson),
            messagesResponder: _ => (HttpStatusCode.InternalServerError, """{"error":{"message":"upstream blew up"}}"""));
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync(
            "hi", ApiKeyCredential(), modelId: "claude-opus-4-7");

        Assert.False(result.Success);
        Assert.Contains("HTTP 500", result.Summary);
        Assert.Contains("model=claude-opus-4-7-20260315", result.Summary);
        Assert.Contains("requested=claude-opus-4-7", result.Summary);
    }

    [Fact]
    public async Task RunTextOnlyAsync_Cancelled_PropagatesOperationCanceled()
    {
        // The OCE re-throw branch in TryResolveCanonicalModelIdAsync (and
        // the outer catch-filter that excludes OCE) must let cancellation
        // propagate to the caller rather than swallowing it as a generic
        // failure result.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new CancellingHandler(cts.Token);
        var runner = BuildRunner(handler);

        // TaskCanceledException is the concrete subclass HttpClient surfaces;
        // both inherit OperationCanceledException, and the test pins the
        // contract that OCE propagates (rather than being swallowed into a
        // generic failure result).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunTextOnlyAsync("hi", ApiKeyCredential(),
                modelId: "claude-opus-4-7", ct: cts.Token));
    }

    [Fact]
    public async Task RunTextOnlyAsync_ModelListThrowsNetworkException_FallsBackToRequestedId()
    {
        // Exercises the generic (non-OCE) catch in TryResolveCanonicalModelIdAsync —
        // a thrown HttpRequestException during /v1/models must NOT degrade the
        // call; resolution returns the requested id and the POST proceeds.
        var handler = new ScriptedAnthropicHandler(
            modelsResponder: _ => throw new HttpRequestException("dns failed"),
            messagesResponder: req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                // Fell back to the requested id verbatim.
                Assert.Equal("claude-opus-4-7", doc.RootElement.GetProperty("model").GetString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"content":[{"type":"text","text":"ok"}]}""", Encoding.UTF8, "application/json"),
                };
            });
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync(
            "hi", ApiKeyCredential(), modelId: "claude-opus-4-7");
        Assert.True(result.Success);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public async Task RunTextOnlyAsync_MessagesThrowsNetworkException_SurfacedAsFailureNotRethrown()
    {
        // Exercises the outer general-exception catch in RunTextOnlyAsync.
        // A transport failure during /v1/messages becomes a non-success
        // result; the caller is never asked to unwind a network blip.
        var handler = new ScriptedAnthropicHandler(
            modelsResponder: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelsJson, Encoding.UTF8, "application/json"),
            },
            messagesResponder: _ => throw new HttpRequestException("transport blew up"));
        var runner = BuildRunner(handler);

        var result = await runner.RunTextOnlyAsync(
            "hi", ApiKeyCredential(), modelId: "claude-opus-4-7");
        Assert.False(result.Success);
        Assert.Equal("Claude text-only call failed", result.Summary);
        Assert.Contains("transport blew up", result.Error);
    }

    // ── Live opt-in (compiles unconditionally; runs only with the env flag) ───

    /// <summary>
    /// Live round-trip against the real Anthropic API. Off by default — set
    /// <c>CODEYBOX_LIVE_CLAUDE_API_KEY</c> to a real <c>sk-ant-...</c> key to
    /// run it. MUST compile unconditionally so a stale reference (the
    /// previous attempt's failure mode) is caught at build time.
    /// </summary>
    [LiveClaudeFact]
    public async Task RunTextOnlyAsync_Live_ReturnsTwoXxNotFourOhFour()
    {
        var apiKey = Environment.GetEnvironmentVariable(LiveClaudeFactAttribute.EnvVar)!;
        var runner = new ClaudeAgentRunner();
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = apiKey },
            new Dictionary<string, string>());

        // Pass the undated alias on purpose — the fix's whole point is that
        // alias resolution converts it to a canonical id that the Messages
        // API actually accepts.
        var result = await runner.RunTextOnlyAsync(
            "Reply with the single word 'ok'.",
            cred,
            modelId: "claude-opus-4-7");

        Assert.True(result.Success, $"expected 2xx; got '{result.Summary}' / '{result.Error}'");
        Assert.False(string.IsNullOrEmpty(result.Output));
    }

    // ── Fake handler ──────────────────────────────────────────────────────────

    /// <summary>
    /// Records every outbound call and returns canned responses keyed off
    /// the request URL. The <c>messagesResponder</c> callback lets a test
    /// assert on the request body before producing its response — used to
    /// pin the canonical-id rewrite without an extra round-trip.
    /// </summary>
    private sealed class FakeAnthropicHandler : HttpMessageHandler
    {
        public List<RecordedCall> Calls { get; } = new();

        private readonly (HttpStatusCode Status, string Body) _modelsResponse;
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _messagesResponder;

        public FakeAnthropicHandler(
            (HttpStatusCode Status, string Body) modelsResponse,
            Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> messagesResponder)
        {
            _modelsResponse = modelsResponse;
            _messagesResponder = messagesResponder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            string? bodyText = null;
            if (request.Content is not null)
                bodyText = await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new RecordedCall(
                request.Method,
                request.RequestUri!,
                CaptureHeaders(request),
                request.Content?.Headers.ContentType?.MediaType,
                bodyText));

            (HttpStatusCode status, string body) = url switch
            {
                ClaudeAgentRunner.ModelsEndpoint => _modelsResponse,
                ClaudeAgentRunner.MessagesEndpoint => _messagesResponder(request),
                _ => (HttpStatusCode.NotFound, $"unhandled url: {url}"),
            };
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        private static Dictionary<string, string> CaptureHeaders(HttpRequestMessage request)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
                dict[h.Key] = string.Join(",", h.Value);
            return dict;
        }
    }

    private sealed record RecordedCall(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string? ContentType,
        string? Body);

    /// <summary>
    /// Yields control via <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// so an already-cancelled token surfaces an
    /// <see cref="OperationCanceledException"/> from inside
    /// <c>SendAsync</c> — exactly the shape <c>HttpClient.SendAsync</c>
    /// produces under real cancellation.
    /// </summary>
    private sealed class CancellingHandler : HttpMessageHandler
    {
        private readonly CancellationToken _externalCt;

        public CancellingHandler(CancellationToken externalCt) { _externalCt = externalCt; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_externalCt, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(30), linked.Token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Per-endpoint responder; the callback may throw to simulate a transport
    /// fault (used to exercise the runner's general-exception handlers).
    /// </summary>
    private sealed class ScriptedAnthropicHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _modelsResponder;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _messagesResponder;

        public ScriptedAnthropicHandler(
            Func<HttpRequestMessage, HttpResponseMessage> modelsResponder,
            Func<HttpRequestMessage, HttpResponseMessage> messagesResponder)
        {
            _modelsResponder = modelsResponder;
            _messagesResponder = messagesResponder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            return Task.FromResult(url switch
            {
                ClaudeAgentRunner.ModelsEndpoint => _modelsResponder(request),
                ClaudeAgentRunner.MessagesEndpoint => _messagesResponder(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"unhandled url: {url}", Encoding.UTF8, "application/json"),
                },
            });
        }
    }
}

/// <summary>
/// Opt-in [Fact] attribute: the test only runs when
/// <see cref="EnvVar"/> is set to a real Claude API key. Compiles
/// unconditionally so a stale reference to a non-existent runner method
/// surfaces as a build error (the integrity gap that sank the prior
/// attempt — see commit 8718309).
/// </summary>
public sealed class LiveClaudeFactAttribute : FactAttribute
{
    public const string EnvVar = "CODEYBOX_LIVE_CLAUDE_API_KEY";

    public LiveClaudeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar}=<sk-ant-...> to run the live Claude text-only round-trip.";
    }
}
