using System.Net;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Audit.Llm;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Cross-module coverage for the actual provider capability objects consumed
/// by LlmReviewAuditor's separated-channel Plan gate.
/// </summary>
public sealed class ConcretePlanReviewProviderTests
{
    private const string PassingVerdict = "{\"passed\":true,\"findings\":[]}";

    [Fact]
    public async Task ClaudeRunner_PassesConcretePlanReviewCapabilityGate()
    {
        var handler = new QueueHttpHandler(
            "{\"data\":[]}",
            "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"passed\\\":true,\\\"findings\\\":[]}\"}]}");
        var defaults = Defaults(("claude", "claude-test"));
        var runner = new ClaudeAgentRunner(
            defaults,
            rotationPusher: null,
            sanitizerConfig: null,
            networkTolerance: null,
            textOnlyHttp: new HttpClient(handler));
        var credential = Credential(AgentKind.Claude, "ANTHROPIC_API_KEY", "test-key");

        var result = await Auditor(runner).RunAsync(
            new NoExecSandbox(),
            "/work",
            Context(credential, "claude-test"));

        Assert.True(result.Passed);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CodexRunner_PassesConcretePlanReviewCapabilityGate()
    {
        var handler = new QueueHttpHandler($"{{\"output_text\":{System.Text.Json.JsonSerializer.Serialize(PassingVerdict)}}}");
        var runner = new CodexAgentRunner(
            Defaults(("codex", "gpt-test")),
            networkTolerance: null,
            textOnlyHttp: new HttpClient(handler));
        var credential = Credential(AgentKind.Codex, "OPENAI_API_KEY", "test-key");

        var result = await Auditor(runner).RunAsync(
            new NoExecSandbox(),
            "/work",
            Context(credential, "gpt-test"));

        Assert.True(result.Passed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GeminiRunner_PassesConcretePlanReviewCapabilityGate()
    {
        var verdictJson = System.Text.Json.JsonSerializer.Serialize(PassingVerdict);
        var handler = new QueueHttpHandler(
            $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{verdictJson}}}]}}}}]}}");
        var runner = new GeminiAgentRunner(new HttpClient(handler));
        var credential = Credential(AgentKind.Gemini, "GEMINI_API_KEY", "test-key");

        var result = await Auditor(runner).RunAsync(
            new NoExecSandbox(),
            "/work",
            Context(credential, "gemini-test"));

        Assert.True(result.Passed);
        Assert.Equal(1, handler.RequestCount);
    }

    private static LlmReviewAuditor Auditor(IAgentRunner runner) => new(new LlmReviewAuditorOptions
    {
        Name = "architecture:llm-review",
        Agent = runner,
        ReviewFocus = "Review architecture.",
        FrameTemplate = "{{reviewFocus}} {{resultFile}}",
        Targets = AuditTargets.PlanOnly,
    });

    private static AuditContext Context(AgentCredential credential, string modelId) => new(
        WorkItemId.New(),
        "work",
        "main",
        1,
        "write output.txt",
        AuditCredential: credential,
        ModelId: modelId,
        Target: AuditTarget.Plan,
        PlanArtifact: "{\"approach\":\"write output\",\"files\":[\"output.txt\"],\"testStrategy\":[\"test output\"],\"risks\":[\"none\"],\"satisfiesTask\":\"write output.txt\"}");

    private static AgentDefaultsSnapshot Defaults(params (string Agent, string Model)[] values) =>
        new(values.ToDictionary(value => value.Agent, value => (string?)value.Model, StringComparer.OrdinalIgnoreCase));

    private static AgentCredential Credential(AgentKind kind, string key, string value) =>
        new(kind, new Dictionary<string, string> { [key] = value }, new Dictionary<string, string>());

    private sealed class QueueHttpHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var body = _bodies.Count > 0 ? _bodies.Dequeue() : throw new InvalidOperationException("Unexpected HTTP request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class NoExecSandbox : ISandbox
    {
        public string Id => "no-exec";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            throw new InvalidOperationException("The host-side Plan review path must not execute sandbox commands.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
