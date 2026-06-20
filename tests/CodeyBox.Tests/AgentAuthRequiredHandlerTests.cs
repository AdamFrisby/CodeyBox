using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public class AgentAuthRequiredHandlerTests
{
    [Fact]
    public void MissingAgentAuthAvailabilityRegistry_MarkAuthRequired_ThrowsInvalidOperation()
    {
        // Pins the deliberate fail-loud safety net in MissingAgentAuthAvailabilityRegistry.
        // PipelineRunner and ReleaseService fall back to this stub when no
        // IAgentAuthAvailabilityRegistry is wired in DI. A future refactor that
        // swaps the throw for a silent no-op would reintroduce the original
        // benign-no-changes shape this branch was created to prevent.
        var registry = MissingAgentAuthAvailabilityRegistry.Instance;

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.MarkAuthRequired(new AgentKind("antigravity"), "auth required"));
        Assert.Contains("not wired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth-required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishSideEffectsAsync_FromMissingRegistryFallback_PropagatesInvalidOperation()
    {
        // End-to-end: when the handler is constructed against the fallback
        // registry, the first auth-required publish should surface the safety
        // net rather than silently dropping the bench.
        var handler = new AgentAuthRequiredHandler(
            MissingAgentAuthAvailabilityRegistry.Instance,
            new CapturingWebhookDispatcher(),
            NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.PublishSideEffectsAsync(
                new AgentKind("antigravity"),
                reason: "auth required from agent output during work: login prompt matched"));
    }

    [Fact]
    public async Task PublishSideEffectsAsync_RealRegistry_BenchesAgentAndPublishesWebhookOnFirstTransition()
    {
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        var handler = new AgentAuthRequiredHandler(availability, webhooks, NullLogger.Instance);

        const string reason = "auth required from agent output during work: login prompt matched";
        await handler.PublishSideEffectsAsync(AgentKind.Claude, reason);

        var current = availability.GetAvailability(AgentKind.Claude);
        Assert.False(current.Available, current.Reason);

        var evt = Assert.Single(webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(evt.Details);
        Assert.Equal("claude", details.AgentKind);
        Assert.Equal(reason, details.Reason);
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task PublishSideEffectsAsync_DuplicateMark_DoesNotRePublishWebhook()
    {
        // SourceChanged is false on the second call (already auth-benched), so the
        // webhook is suppressed to avoid duplicate operator alerts.
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        var webhooks = new CapturingWebhookDispatcher();
        var handler = new AgentAuthRequiredHandler(availability, webhooks, NullLogger.Instance);

        await handler.PublishSideEffectsAsync(AgentKind.Claude, reason: "auth required: first");
        await handler.PublishSideEffectsAsync(AgentKind.Claude, reason: "auth required: second");

        var smokeEvents = webhooks.Events.Where(e => e.Event == "agent.smoke_failed").ToList();
        Assert.Single(smokeEvents);
    }

    [Fact]
    public void BuildReason_StderrEvidence_OmitsStdoutOnlyNote()
    {
        var handler = NewHandler();
        var classification = new AgentFailureClassification(
            AgentFailureKind.AuthRequired,
            Reason: "auth/login prompt pattern matched in stderr");

        var reason = handler.BuildReason("work", classification, stdoutOnlyEvidence: false);

        Assert.StartsWith("auth required from agent output during work:", reason);
        Assert.Contains("auth/login prompt pattern matched in stderr", reason);
        Assert.DoesNotContain("stdout accepted", reason);
    }

    [Fact]
    public void BuildReason_StdoutOnlyEvidence_AppendsDefaultNote()
    {
        var handler = NewHandler();
        var classification = new AgentFailureClassification(
            AgentFailureKind.AuthRequired,
            Reason: "auth/login prompt pattern matched in stdout");

        var reason = handler.BuildReason("audit:llm-review", classification, stdoutOnlyEvidence: true);

        Assert.Contains("during audit:llm-review:", reason);
        Assert.Contains("stdout accepted as authoritative CLI output", reason);
    }

    [Fact]
    public void BuildReason_StdoutOnlyEvidence_CustomNote_Overrides()
    {
        var handler = NewHandler();
        var classification = new AgentFailureClassification(
            AgentFailureKind.AuthRequired,
            Reason: "auth/login prompt pattern matched in stdout");

        var reason = handler.BuildReason(
            "release-deep-audit:archcoherence",
            classification,
            stdoutOnlyEvidence: true,
            stdoutOnlyNote: "stdout accepted for release failure only because deep-audit stdout is model-controlled");

        Assert.Contains("stdout accepted for release failure only", reason);
        Assert.DoesNotContain("stdout accepted as authoritative CLI output", reason);
    }

    [Fact]
    public void BuildReason_WithRelease_IncludesReleaseScope()
    {
        var handler = NewHandler();
        var classification = new AgentFailureClassification(
            AgentFailureKind.AuthRequired,
            Reason: "login");
        var releaseId = ReleaseId.New();
        var release = new Release
        {
            Id = releaseId,
            ProjectId = new ProjectId("proj"),
            Name = "v1.0.0",
            State = ReleaseState.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var reason = handler.BuildReason("release-deep-audit:owasp", classification, false, release: release);

        Assert.Contains($"during release-deep-audit:owasp for release {releaseId}:", reason);
    }

    [Fact]
    public void BuildReason_NormalisesControlCharactersToSingleLine()
    {
        // SingleLineSummary protects log/webhook sinks from CR/LF injection.
        var handler = NewHandler();
        var classification = new AgentFailureClassification(
            AgentFailureKind.AuthRequired,
            Reason: "login\nprompt\rmatched\tinlinectrl");

        var reason = handler.BuildReason("work", classification, false);

        Assert.DoesNotContain('\n', reason);
        Assert.DoesNotContain('\r', reason);
        Assert.DoesNotContain('\t', reason);
        Assert.DoesNotContain('', reason);
        Assert.Contains("login prompt matched inline ctrl", reason);
    }

    [Fact]
    public void AgentAuthRequiredException_CarriesAgentAndPhase()
    {
        // PipelineRunner and ReleaseService both catch this exception and use
        // ex.Agent/ex.Phase to attribute downstream side effects; pin the
        // properties so a future regression that drops them surfaces here.
        var ex = new AgentAuthRequiredException(
            new AgentKind("antigravity"),
            "release-deep-audit:llm-review",
            "auth required from agent output during release-deep-audit:llm-review for release r-7: login prompt matched");

        Assert.Equal("antigravity", ex.Agent.Value);
        Assert.Equal("release-deep-audit:llm-review", ex.Phase);
        Assert.Contains("auth required", ex.Message);
    }

    private static AgentAuthRequiredHandler NewHandler()
    {
        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        return new AgentAuthRequiredHandler(availability, new CapturingWebhookDispatcher(), NullLogger.Instance);
    }
}
