using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CompositeQuotaFailureClassifierTests
{
    private readonly CompositeQuotaFailureClassifier _classifier = new(
    [
        new ClaudeQuotaFailureDetector(),
        new CodexQuotaFailureDetector(),
        new GeminiQuotaFailureDetector(),
    ]);

    [Fact]
    public void Detect_DispatchesByAgentKind_ClaudeRateLimit()
    {
        var detection = _classifier.Detect(AgentKind.Claude, "rate_limit_exceeded", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_DispatchesByAgentKind_GeminiResourceExhausted()
    {
        var detection = _classifier.Detect(AgentKind.Gemini, "RESOURCE_EXHAUSTED reset after 5m", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_ClaudeAgent_DoesNotMatchGeminiText()
    {
        // Per-provider dispatch must isolate provider vocabularies:
        // gemini's RESOURCE_EXHAUSTED is meaningless when the runner is claude.
        Assert.Null(_classifier.Detect(AgentKind.Claude, "RESOURCE_EXHAUSTED", stdout: null));
    }

    [Fact]
    public void Detect_UnknownAgentKind_FallsThroughToNull()
    {
        var unknown = new AgentKind("mistral");

        Assert.Null(_classifier.Detect(unknown, "rate_limit_exceeded", stdout: null));
    }

    [Fact]
    public void Detect_EmptyInput_ReturnsNullWithoutCallingDetector()
    {
        Assert.Null(_classifier.Detect(AgentKind.Claude, stderr: null, stdout: null));
        Assert.Null(_classifier.Detect(AgentKind.Claude, stderr: "", stdout: ""));
    }
}
