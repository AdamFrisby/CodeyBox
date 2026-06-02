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
    public void QuotaClassification_Quota_RejectsNullDetection()
    {
        Assert.Throws<ArgumentNullException>(() => QuotaFailureClassification.Quota(null!));
    }

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

    [Fact]
    public void EmitAdvisoryAuditEvents_DispatchesToMatchingDetector()
    {
        var claude = new RecordingDetector(AgentKind.Claude);
        var codex = new RecordingDetector(AgentKind.Codex);
        var classifier = new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[] { claude, codex });

        classifier.EmitAdvisoryAuditEvents(AgentKind.Claude, "stderr-text", "stdout-text", "work", "vm-1");

        var call = Assert.Single(claude.Calls);
        Assert.Equal("stderr-text", call.Stderr);
        Assert.Equal("stdout-text", call.Stdout);
        Assert.Equal("work", call.Phase);
        Assert.Equal("vm-1", call.SandboxName);
        Assert.Empty(codex.Calls);
    }

    [Fact]
    public void EmitAdvisoryAuditEvents_EmptyInput_DoesNotDispatch()
    {
        var claude = new RecordingDetector(AgentKind.Claude);
        var classifier = new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[] { claude });

        classifier.EmitAdvisoryAuditEvents(AgentKind.Claude, stderr: null, stdout: null, "work", "vm-1");
        classifier.EmitAdvisoryAuditEvents(AgentKind.Claude, stderr: "", stdout: "", "work", "vm-1");

        Assert.Empty(claude.Calls);
    }

    [Fact]
    public void EmitAdvisoryAuditEvents_UnknownAgentKind_SilentlyNoOps()
    {
        var claude = new RecordingDetector(AgentKind.Claude);
        var classifier = new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[] { claude });
        var unknown = new AgentKind("mistral");

        // Must not throw and must not dispatch to any registered detector.
        classifier.EmitAdvisoryAuditEvents(unknown, "stderr", "stdout", "work", "vm-1");

        Assert.Empty(claude.Calls);
    }

    [Fact]
    public async Task RecordIfQuotaFailureAsync_NullClassifier_ThrowsArgumentNullException()
    {
        IQuotaFailureClassifier classifier = null!;

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            classifier.RecordIfQuotaFailureAsync(
                store: null,
                agent: AgentKind.Claude,
                modelId: null,
                summary: "agent exited 1",
                stderr: "rate_limit_exceeded",
                observedAt: DateTimeOffset.UtcNow,
                retention: TimeSpan.FromMinutes(30),
                ct: CancellationToken.None));

        Assert.Equal("classifier", ex.ParamName);
    }

    // ── IsAgentExited1Summary ───────────────────────────────────────────────
    //
    // The composite classifier's persistent-store gate requires an "agent
    // exited 1" summary so non-quota infrastructure failures (e.g.
    // "failed to materialise gemini auth: exit 1") don't pollute the
    // observed-failure store. The GeminiAgentRunner now appends a diagnostic
    // tail to that summary; the gate must still recognise the enriched form,
    // otherwise persistent recording silently stops and the next pickup
    // routes back to an agent we already know is exhausted.

    [Theory]
    [InlineData("agent exited 1")]
    [InlineData("  agent exited 1  ")]
    [InlineData("AGENT EXITED 1")]
    [InlineData("agent exited 1: RESOURCE_EXHAUSTED quota exceeded")]
    [InlineData("agent exited 1: API Error: 401 Unauthorized")]
    [InlineData("agent exited 1: …<truncated tail>")]
    public void IsAgentExited1Summary_AcceptsBaseAndEnrichedForms(string summary)
    {
        Assert.True(QuotaFailureClassifierStoreExtensions.IsAgentExited1Summary(summary));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("agent exited 2")]
    [InlineData("agent exited 1foo")]
    [InlineData("failed to materialise gemini auth: exit 1")]
    public void IsAgentExited1Summary_RejectsOtherShapes(string? summary)
    {
        Assert.False(QuotaFailureClassifierStoreExtensions.IsAgentExited1Summary(summary));
    }

    private sealed record AdvisoryCall(string? Stderr, string? Stdout, string Phase, string? SandboxName);

    private sealed class RecordingDetector : IAgentQuotaFailureDetector
    {
        public RecordingDetector(AgentKind kind) { Kind = kind; }
        public AgentKind Kind { get; }
        public List<AdvisoryCall> Calls { get; } = new();
        public QuotaDetection? Detect(string? stderr, string? stdout) => null;
        public void EmitAdvisoryAuditEvents(string? stderr, string? stdout, string phase, string? sandboxName) =>
            Calls.Add(new AdvisoryCall(stderr, stdout, phase, sandboxName));
    }
}
