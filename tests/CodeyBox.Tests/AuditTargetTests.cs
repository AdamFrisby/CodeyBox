using CodeyBox.Audit.Llm;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

public sealed class AuditTargetTests
{
    [Fact]
    public void IAuditor_DefaultTargets_IsCodeOnly_SoExistingAuditorsAreUnchanged()
    {
        IAuditor auditor = new BareCodeAuditor();

        // Every auditor that does not opt in reviews code only — the default
        // keeps all existing and external auditors behaving exactly as before.
        Assert.True(auditor.Targets.Contains(AuditTarget.Code));
        Assert.False(auditor.Targets.Contains(AuditTarget.Plan));
        Assert.Same(AuditTargets.CodeOnly, auditor.Targets);
    }

    [Fact]
    public void AuditTargets_Of_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => AuditTargets.Of());

    [Fact]
    public void AuditContext_EffectiveTarget_DefaultsToCode_WhenTargetUnset()
    {
        var ctx = new AuditContext(
            WorkItemId.New(), "work", "main", 1, "prompt");

        Assert.Equal(AuditTarget.Code, ctx.EffectiveTarget);

        var planCtx = ctx with { Target = AuditTarget.Plan };
        Assert.Equal(AuditTarget.Plan, planCtx.EffectiveTarget);
    }

    [Fact]
    public async Task LlmReviewAuditor_BothTargets_AdaptsToPlanReviewViaThreadedTarget()
    {
        // An auditor that targets BOTH plan and code adapts its review to the
        // threaded target: reviewing a PLAN artifact (text-only) versus a diff.
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = new NoopAgent(),
            ReviewFocus = "- Layering violations\n- God objects",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanAndCode,
        });

        Assert.True(auditor.Targets.Contains(AuditTarget.Plan));
        Assert.True(auditor.Targets.Contains(AuditTarget.Code));

        var runner = new FakeTextOnlyRunner("""{"passed": true, "findings": []}""");
        var ctx = new AuditContext(
            WorkItemId.New(), "work", "main", 1, "make the widget faster",
            Target: AuditTarget.Plan,
            PlanArtifact: """{"approach":"rewrite the widget","files":["w.cs"],"testStrategy":["unit"],"risks":["none"],"satisfiesTask":"yes"}""");

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(ctx, runner, credential: null);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        // The plan-review prompt targets the PLAN, embeds the artifact + focus,
        // and does NOT ask to write a result file (that's the code/diff path).
        Assert.Contains("reviewing a proposed implementation PLAN", runner.LastPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Layering violations", runner.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("rewrite the widget", runner.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("audit/result.json", runner.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_SurfacesBlockingFindings()
    {
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = new NoopAgent(),
            ReviewFocus = "- Layering violations",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanAndCode,
        });
        var runner = new FakeTextOnlyRunner(
            """{"passed": false, "findings": [{"severity":"error","title":"wrong layer","description":"domain calls infra"}]}""");
        var ctx = new AuditContext(
            WorkItemId.New(), "work", "main", 1, "task",
            Target: AuditTarget.Plan,
            PlanArtifact: """{"approach":"a","files":["f"],"testStrategy":["t"],"risks":["r"],"satisfiesTask":"s"}""");

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(ctx, runner, credential: null);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Equal("wrong layer", finding.Title);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_MissingArtifact_IsBlocking()
    {
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = new NoopAgent(),
            ReviewFocus = "- x",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanAndCode,
        });
        var ctx = new AuditContext(WorkItemId.New(), "w", "main", 1, "t", Target: AuditTarget.Plan);

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(
            ctx, new FakeTextOnlyRunner("unused"), credential: null);

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_AgentFailure_IsBlocking()
    {
        // The text-only runner reporting failure is an agent/infra error, not a
        // passing review — it must surface a blocking Error finding so a failed
        // reviewer never silently approves the plan.
        var auditor = PlanAuditor();
        var runner = new FakeTextOnlyRunner("ignored", success: false);
        var ctx = PlanContext();

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(ctx, runner, credential: null);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("plan review agent failed to run", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_EmptyVerdict_IsBlocking()
    {
        // A successful call that returns no text has no verdict to trust.
        var auditor = PlanAuditor();
        var runner = new FakeTextOnlyRunner("   ");
        var ctx = PlanContext();

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(ctx, runner, credential: null);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("produced no verdict", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_InvalidJson_IsBlocking()
    {
        // A chatty/malformed model response that isn't parseable JSON must be a
        // blocking Error, not swallowed into a passing verdict.
        var auditor = PlanAuditor();
        var runner = new FakeTextOnlyRunner("I could not decide, sorry — no JSON here.");
        var ctx = PlanContext();

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(ctx, runner, credential: null);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("produced invalid JSON", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_ParsesFencedJsonVerdict()
    {
        var auditor = PlanAuditor();
        var runner = new FakeTextOnlyRunner("""
            The plan is acceptable.

            ```json
            {"passed":true,"findings":[{"severity":"warning","title":"minor risk","description":"watch rollout"}]}
            ```
            """);
        var ctx = PlanContext();

        var result = await ((IPlanTextReviewer)auditor).ReviewPlanAsync(ctx, runner, credential: null);

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("minor risk", finding.Title);
    }

    private static LlmReviewAuditor PlanAuditor() => new(new LlmReviewAuditorOptions
    {
        Name = "architecture:llm-review",
        Agent = new NoopAgent(),
        ReviewFocus = "- Layering violations",
        FrameTemplate = "{{reviewFocus}} {{resultFile}}",
        Targets = AuditTargets.PlanAndCode,
    });

    private static AuditContext PlanContext() => new(
        WorkItemId.New(), "work", "main", 1, "task",
        Target: AuditTarget.Plan,
        PlanArtifact: """{"approach":"a","files":["f"],"testStrategy":["t"],"risks":["r"],"satisfiesTask":"s"}""");

    private sealed class BareCodeAuditor : IAuditor
    {
        public string Name => "bare";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class NoopAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }
}

internal sealed class FakeTextOnlyRunner(
    string output,
    bool success = true,
    string? unavailabilityReason = null,
    bool requiresSandbox = false,
    string? throwMessage = null)
    : ITextOnlyAgentRunner
{
    public AgentKind Kind => AgentKind.Claude;
    public string LastPrompt { get; private set; } = string.Empty;
    public int Calls { get; private set; }
    public bool TextOnlyRequiresSandbox => requiresSandbox;

    public Task<AgentResult> RunAsync(
        ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
        string? modelId = null, string? reasoningMode = null, CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        => Task.FromResult(new AgentResult(false, "use RunTextOnlyAsync", null, null));

    public Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt, AgentCredential? credential, string? modelId = null, string? reasoningMode = null,
        CancellationToken ct = default, ISandbox? sandbox = null, string? workingDirectory = null)
    {
        Calls++;
        LastPrompt = prompt;
        if (throwMessage is not null)
            throw new InvalidOperationException(throwMessage);
        return Task.FromResult(new TextOnlyAgentResult(success, "done", success ? output : null, success ? null : "failed"));
    }

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) => unavailabilityReason;
}
