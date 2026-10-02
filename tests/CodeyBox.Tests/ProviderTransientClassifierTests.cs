using CodeyBox.Agents.Aider;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Agents.Autohand;
using CodeyBox.Agents.CavemanCode;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Cline;
using CodeyBox.Agents.Cmd;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Continue;
using CodeyBox.Agents.Copilot;
using CodeyBox.Agents.Crush;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Devin;
using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Goose;
using CodeyBox.Agents.Kilo;
using CodeyBox.Agents.Omp;
using CodeyBox.Agents.Opencode;
using CodeyBox.Agents.Pi;
using CodeyBox.Agents.Prime;
using CodeyBox.Agents.Qwen;
using CodeyBox.Agents.Unreal;
using CodeyBox.Agents.Vibe;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the provider-side transient classification chain: exact
/// per-agent signatures (model capacity, output truncation, infra transport),
/// the hot-reloadable operator-signature store, the host-level correlation
/// tracker, and the pure retry policy (same agent/model park, bounded
/// truncation nudge). Every evidence string from the incident that motivated
/// this work must classify transient; bare words and unrelated failures must
/// stay terminal.
/// </summary>
public sealed class ProviderTransientClassifierTests
{
    private static IAgentQuotaFailureDetector[] AllDetectors() =>
    [
        new AiderQuotaFailureDetector(),
        new AntigravityQuotaFailureDetector(),
        new AutohandQuotaFailureDetector(),
        new CavemanCodeQuotaFailureDetector(),
        new ClaudeQuotaFailureDetector(),
        new ClineQuotaFailureDetector(),
        new CmdQuotaFailureDetector(),
        new CodexQuotaFailureDetector(),
        new ContinueQuotaFailureDetector(),
        new CopilotQuotaFailureDetector(),
        new CrushQuotaFailureDetector(),
        new CursorQuotaFailureDetector(),
        new DevinQuotaFailureDetector(),
        new DotNetOpencodeQuotaFailureDetector(),
        new GeminiQuotaFailureDetector(),
        new GooseQuotaFailureDetector(),
        new KiloQuotaFailureDetector(),
        new OmpQuotaFailureDetector(),
        new OpencodeQuotaFailureDetector(),
        new PiQuotaFailureDetector(),
        new PrimeQuotaFailureDetector(),
        new QwenQuotaFailureDetector(),
        new UnrealQuotaFailureDetector(),
        new VibeQuotaFailureDetector(),
    ];

    public static IEnumerable<object[]> AllDetectorInstances()
    {
        foreach (var detector in AllDetectors())
            yield return [detector];
    }

    [Fact]
    public void DevinCapacityError_ClassifiesModelCapacity()
    {
        const string stderr = "Agent error: Client error: Protocol error (unimplemented): " +
            "We are currently experiencing capacity issues with this serving model. " +
            "Please switch to a different model";

        var detection = new DevinQuotaFailureDetector().DetectProviderTransient(stderr, null, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.ModelCapacity, detection!.Kind);
    }

    [Fact]
    public void DevinCapacityError_DoesNotEnterQuotaFailover()
    {
        // The quota path is what switches models mid-iteration. A capacity
        // shape must never classify as quota, so the item parks for
        // same-agent/same-model transient retry instead of failing over.
        const string stderr = "Agent error: Client error: Protocol error (unimplemented): " +
            "We are currently experiencing capacity issues with this serving model. " +
            "Please switch to a different model";

        var classifier = new CompositeQuotaFailureClassifier([new DevinQuotaFailureDetector()]);

        Assert.Equal(
            QuotaFailureClassificationKind.None,
            classifier.Classify(AgentKind.Devin, stderr, null).Kind);
    }

    [Fact]
    public void DevinCapacityError_ParksOnTransientBudgetPreservingModel()
    {
        // The parked failure kind is the established transient budget (backoff
        // with jitter, no TerminalFailureCount increment); the transition
        // preserves the item's agent and model id, so the retry re-dispatches
        // the same model id and never a different one.
        Assert.Equal("transient", ProviderTransientRetryPolicy.ParkedFailureKind);
        Assert.NotEqual("quota", ProviderTransientRetryPolicy.ParkedFailureKind);
    }

    [Theory]
    [InlineData("warning: response truncated (model hit max output token limit) Error: Response truncated: model hit max output token limit")]
    [InlineData("Response truncated: model hit max output token limit")]
    [InlineData("Error: response truncated because the model hit the max output token limit")]
    public void TruncationShapes_ClassifyOutputTruncation(string stderr)
    {
        foreach (var detector in AllDetectors())
        {
            var detection = detector.DetectProviderTransient(stderr, null, null);
            Assert.NotNull(detection);
            Assert.Equal(ProviderTransientKind.OutputTruncation, detection!.Kind);
        }
    }

    [Theory]
    [InlineData("Error: websocket: close 1006 (abnormal closure): unexpected EOF")]
    [InlineData("websocket: close 1006 (abnormal closure): unexpected EOF")]
    public void WebsocketClose1006_ClassifiesInfraTransport(string stderr)
    {
        foreach (var detector in AllDetectors())
        {
            var detection = detector.DetectProviderTransient(stderr, null, null);
            Assert.NotNull(detection);
            Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
        }
    }

    [Fact]
    public void CopilotUpstream504AfterRetries_ClassifiesInfraTransport()
    {
        const string stdout = "Failed to get response from the AI model; retried 5 times " +
            "(total retry wait time: 91.99 seconds). Last error: 504 Upstream response was not valid JSON";

        var detection = new CopilotQuotaFailureDetector().DetectProviderTransient(null, stdout, null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
    }

    [Theory]
    [MemberData(nameof(AllDetectorInstances))]
    public void AllDetectors_TruncationAndTransport_ClassifyTransient(IAgentQuotaFailureDetector detector)
    {
        Assert.NotNull(detector.DetectProviderTransient(
            "Error: websocket: close 1006 (abnormal closure): unexpected EOF", null, null));
        Assert.NotNull(detector.DetectProviderTransient(
            "Response truncated: model hit max output token limit", null, null));
    }

    [Theory]
    [MemberData(nameof(AllDetectorInstances))]
    public void AllDetectors_UnrelatedFailures_StayTerminal(IAgentQuotaFailureDetector detector)
    {
        Assert.Null(detector.DetectProviderTransient("go build ./... failed: undefined: foo", null, null));
        Assert.Null(detector.DetectProviderTransient(null, "test XYZ failed with exit code 1", null));
        Assert.Null(detector.DetectProviderTransient(null, null, "agent exited 1"));
        Assert.Null(detector.DetectProviderTransient(null, null, null));
        Assert.Null(detector.DetectProviderTransient("", "", ""));
    }

    [Theory]
    [MemberData(nameof(AllDetectorInstances))]
    public void AllDetectors_BareWords_NeverMatch(IAgentQuotaFailureDetector detector)
    {
        // Signatures are held as exact multi-token diagnostics, not loose
        // substrings: a bare word or status code must not classify.
        Assert.Null(detector.DetectProviderTransient("capacity", null, null));
        Assert.Null(detector.DetectProviderTransient("504", null, null));
        Assert.Null(detector.DetectProviderTransient("429", null, null));
        Assert.Null(detector.DetectProviderTransient("timeout", null, null));
        Assert.Null(detector.DetectProviderTransient("truncated", null, null));
        Assert.Null(detector.DetectProviderTransient("websocket", null, null));
        Assert.Null(detector.DetectProviderTransient("overloaded", null, null));
    }

    [Theory]
    [MemberData(nameof(AllDetectorInstances))]
    public void AllDetectors_ModelProse_StaysTerminal(IAgentQuotaFailureDetector detector)
    {
        Assert.Null(detector.DetectProviderTransient(
            null,
            "The function checks whether the user's quota is sufficient before calling the API.",
            null));
    }

    [Fact]
    public void Composite_DetectProviderTransient_DispatchesByAgent()
    {
        var classifier = new CompositeQuotaFailureClassifier(AllDetectors());

        var detection = classifier.DetectProviderTransient(
            AgentKind.Copilot,
            "Error: websocket: close 1006 (abnormal closure): unexpected EOF",
            null,
            null);

        Assert.NotNull(detection);
        Assert.Equal(ProviderTransientKind.InfraTransport, detection!.Kind);
    }

    [Fact]
    public void Composite_DetectProviderTransient_UnknownAgent_ReturnsNull()
    {
        var classifier = new CompositeQuotaFailureClassifier(AllDetectors());

        Assert.Null(classifier.DetectProviderTransient(
            new AgentKind("no-such-agent"), "Error: websocket: close 1006 (abnormal closure): unexpected EOF", null, null));
        Assert.Null(classifier.DetectProviderTransient(AgentKind.Copilot, "boom", null, null));
    }

    [Fact]
    public void OperatorSignatures_AppendToBuiltins()
    {
        const string agent = "provider-transient-test-agent";
        try
        {
            ProviderTransientSignatureStore.SetAgentSignatures(
                agent,
                [new ProviderTransientSignature("custom tenant capacity crunch", ProviderTransientKind.ModelCapacity)]);

            var detection = ProviderTransientDetectorCore.Detect(
                agent, "custom tenant capacity crunch on shard 7", null, null);

            Assert.NotNull(detection);
            Assert.Equal(ProviderTransientKind.ModelCapacity, detection!.Kind);

            // Built-ins still apply alongside operator entries.
            Assert.NotNull(ProviderTransientDetectorCore.Detect(
                agent, "Error: websocket: close 1006 (abnormal closure): unexpected EOF", null, null));
        }
        finally
        {
            ProviderTransientSignatureStore.SetAgentSignatures(agent, null);
        }
    }

    [Fact]
    public void OperatorSignatures_SyncAgents_ReplacesTableAtomically()
    {
        const string agent = "provider-transient-test-agent";
        try
        {
            ProviderTransientSignatureStore.SyncAgents(new Dictionary<string, IEnumerable<ProviderTransientSignature>?>
            {
                [agent] = [new ProviderTransientSignature("custom tenant capacity crunch", ProviderTransientKind.ModelCapacity)],
            });
            Assert.NotNull(ProviderTransientDetectorCore.Detect(agent, "custom tenant capacity crunch", null, null));

            ProviderTransientSignatureStore.SyncAgents(new Dictionary<string, IEnumerable<ProviderTransientSignature>?>());
            Assert.Null(ProviderTransientDetectorCore.Detect(agent, "custom tenant capacity crunch", null, null));
            Assert.NotNull(ProviderTransientDetectorCore.Detect(
                agent, "Error: websocket: close 1006 (abnormal closure): unexpected EOF", null, null));
        }
        finally
        {
            ProviderTransientSignatureStore.SetAgentSignatures(agent, null);
        }
    }

    [Fact]
    public void OperatorSignatures_InvalidPattern_ThrowsLoudly()
    {
        Assert.Throws<ArgumentException>(() =>
            ProviderTransientSignatureStore.SetAgentSignatures(
                "provider-transient-test-agent",
                [new ProviderTransientSignature("([invalid", ProviderTransientKind.ModelCapacity)]));
    }

    [Fact]
    public void RetryPolicy_ParkedError_CarriesFamilyAndSignatureOnly()
    {
        var error = ProviderTransientRetryPolicy.BuildParkedError(
            new ProviderTransientDetection(ProviderTransientKind.ModelCapacity, "serving-model-capacity", "detail"));

        Assert.Contains("model-capacity", error);
        Assert.Contains("serving-model-capacity", error);
    }

    [Fact]
    public void RetryPolicy_TruncationPrompt_NamesContinuation()
    {
        var prompt = ProviderTransientRetryPolicy.BuildTruncationContinuePrompt("work");

        Assert.Contains("continue", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output token limit", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(99, 5)]
    public void RetryPolicy_TruncationNudges_Clamped(int configured, int expected)
    {
        Assert.Equal(expected, ProviderTransientRetryPolicy.ClampTruncationNudges(configured));
        Assert.Equal(5, ProviderTransientRetryPolicy.MaxTruncationContinueNudges);
    }

    [Fact]
    public void CorrelationTracker_SingleAgent_NeverTrips()
    {
        var tracker = new ProviderTransientCorrelationTracker();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
            Assert.False(tracker.Observe("websocket-close-1006", AgentKind.Copilot, now.AddSeconds(i)));

        Assert.False(tracker.IsPaused(now.AddMinutes(1)));
    }

    [Fact]
    public void CorrelationTracker_TwoAgentsSameWindow_TripsPause()
    {
        var tracker = new ProviderTransientCorrelationTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.False(tracker.Observe("websocket-close-1006", AgentKind.Copilot, now));
        Assert.True(tracker.Observe("websocket-close-1006", AgentKind.Devin, now));
        Assert.True(tracker.IsPaused(now));
    }

    [Fact]
    public void CorrelationTracker_PauseExpires()
    {
        var tracker = new ProviderTransientCorrelationTracker();
        var now = DateTimeOffset.UtcNow;

        tracker.Observe("websocket-close-1006", AgentKind.Copilot, now);
        tracker.Observe("websocket-close-1006", AgentKind.Devin, now);

        Assert.True(tracker.IsPaused(now.AddMinutes(1)));
        Assert.False(tracker.IsPaused(now.AddMinutes(30)));
    }

    [Fact]
    public void CorrelationTracker_DifferentSignatures_AreIndependent()
    {
        var tracker = new ProviderTransientCorrelationTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.False(tracker.Observe("websocket-close-1006", AgentKind.Copilot, now));
        Assert.False(tracker.Observe("serving-model-capacity", AgentKind.Devin, now));
        Assert.False(tracker.IsPaused(now));
    }

    [Fact]
    public void CorrelationTracker_ThresholdRespected()
    {
        var tracker = new ProviderTransientCorrelationTracker(() =>
            ProviderTransientCorrelationSettings.Create(TimeSpan.FromMinutes(5), 3, TimeSpan.FromMinutes(2)));
        var now = DateTimeOffset.UtcNow;

        Assert.False(tracker.Observe("websocket-close-1006", AgentKind.Copilot, now));
        Assert.False(tracker.Observe("websocket-close-1006", AgentKind.Devin, now));
        Assert.True(tracker.Observe("websocket-close-1006", AgentKind.Claude, now));
    }

    [Fact]
    public void CorrelationTracker_EmptySignature_NeverTrips()
    {
        var tracker = new ProviderTransientCorrelationTracker();
        var now = DateTimeOffset.UtcNow;

        Assert.False(tracker.Observe(null, AgentKind.Copilot, now));
        Assert.False(tracker.Observe("  ", AgentKind.Devin, now));
        Assert.False(tracker.IsPaused(now));
    }

    [Fact]
    public void CorrelationSettings_ClampInsteadOfThrow()
    {
        var settings = ProviderTransientCorrelationSettings.Create(TimeSpan.Zero, 1, TimeSpan.FromHours(5));

        Assert.Equal(ProviderTransientCorrelationSettings.DefaultWindow, settings.Window);
        Assert.Equal(2, settings.DistinctAgentThreshold);
        Assert.Equal(ProviderTransientCorrelationSettings.MaxPauseDuration, settings.PauseDuration);
    }
}
