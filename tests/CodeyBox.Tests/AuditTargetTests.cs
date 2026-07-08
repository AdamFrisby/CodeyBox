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
    public void AuditTarget_NormalizesCaseAndWhitespace()
    {
        Assert.Equal(AuditTarget.Plan, new AuditTarget(" Plan "));
        Assert.True(AuditTargets.Of(new AuditTarget("MIGRATION")).Contains(new AuditTarget("migration")));
    }

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
        // threaded target: reviewing a PLAN artifact versus a diff.
        var runner = new FakePlanRunner();
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = runner,
            ReviewFocus = "- Layering violations\n- God objects",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanAndCode,
        });

        Assert.True(auditor.Targets.Contains(AuditTarget.Plan));
        Assert.True(auditor.Targets.Contains(AuditTarget.Code));

        var ctx = new AuditContext(
            WorkItemId.New(), "work", "main", 1, "make the widget faster",
            Target: AuditTarget.Plan,
            PlanArtifact: """{"approach":"rewrite the widget","files":["w.cs"],"testStrategy":["unit"],"risks":["none"],"satisfiesTask":"yes"}""");

        var result = await auditor.RunAsync(
            new PlanResultSandbox("""{"passed": true, "findings": []}"""),
            "/work",
            ctx);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
        // The plan-review prompt targets the PLAN, embeds the artifact + focus,
        // and still uses the standard audit/result.json verdict contract.
        Assert.Contains("reviewing a proposed implementation PLAN", runner.LastPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Layering violations", runner.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("rewrite the widget", runner.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("audit/result.json", runner.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_SurfacesBlockingFindings()
    {
        var auditor = new LlmReviewAuditor(new LlmReviewAuditorOptions
        {
            Name = "architecture:llm-review",
            Agent = new FakePlanRunner(),
            ReviewFocus = "- Layering violations",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanAndCode,
        });
        var ctx = new AuditContext(
            WorkItemId.New(), "work", "main", 1, "task",
            Target: AuditTarget.Plan,
            PlanArtifact: """{"approach":"a","files":["f"],"testStrategy":["t"],"risks":["r"],"satisfiesTask":"s"}""");

        var result = await auditor.RunAsync(
            new PlanResultSandbox(
                """{"passed": false, "findings": [{"severity":"error","title":"wrong layer","description":"domain calls infra"}]}"""),
            "/work",
            ctx);

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
            Agent = new FakePlanRunner(),
            ReviewFocus = "- x",
            FrameTemplate = "{{reviewFocus}} {{resultFile}}",
            Targets = AuditTargets.PlanAndCode,
        });
        var ctx = new AuditContext(WorkItemId.New(), "w", "main", 1, "t", Target: AuditTarget.Plan);

        var result = await auditor.RunAsync(new PlanResultSandbox("unused"), "/work", ctx);

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Severity == AuditSeverity.Error);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_AgentFailure_IsBlocking()
    {
        // The review agent reporting failure is an agent/infra error, not a
        // passing review — it must surface a blocking Error finding so a failed
        // reviewer never silently approves the plan.
        var runner = new FakePlanRunner(success: false);
        var auditor = PlanAuditor(runner);
        var ctx = PlanContext();

        var result = await auditor.RunAsync(new PlanResultSandbox("ignored"), "/work", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("review agent failed to run", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_EmptyVerdict_IsBlocking()
    {
        // A successful call that leaves the shared result file empty has no
        // verdict to trust.
        var auditor = PlanAuditor();
        var ctx = PlanContext();

        var result = await auditor.RunAsync(new PlanResultSandbox("   "), "/work", ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("agent did not write audit/result.json", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_InvalidJson_IsBlocking()
    {
        // A chatty/malformed model response that isn't parseable JSON must be a
        // blocking Error, not swallowed into a passing verdict.
        var auditor = PlanAuditor();
        var ctx = PlanContext();

        var result = await auditor.RunAsync(
            new PlanResultSandbox("I could not decide, sorry — no JSON here."),
            "/work",
            ctx);

        Assert.False(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Error, finding.Severity);
        Assert.Contains("produced invalid JSON", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlmReviewAuditor_PlanReview_ParsesFencedJsonVerdict()
    {
        var auditor = PlanAuditor();
        var result = await auditor.RunAsync(new PlanResultSandbox("""
            The plan is acceptable.

            ```json
            {"passed":true,"findings":[{"severity":"warning","title":"minor risk","description":"watch rollout"}]}
            ```
            """), "/work", PlanContext());

        Assert.True(result.Passed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(AuditSeverity.Warning, finding.Severity);
        Assert.Equal("minor risk", finding.Title);
    }

    private static LlmReviewAuditor PlanAuditor(IAgentRunner? agent = null) => new(new LlmReviewAuditorOptions
    {
        Name = "architecture:llm-review",
        Agent = agent ?? new FakePlanRunner(),
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

    private sealed class FakePlanRunner(bool success = true) : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            LastPrompt = prompt;
            return Task.FromResult(success
                ? new AgentResult(true, "ok", "review complete", null)
                : new AgentResult(false, "failed", null, "failed"));
        }
    }

    private sealed class PlanResultSandbox(string resultJson) : ISandbox
    {
        public string Id => "plan-result";

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
        {
            if (exec.Argv.Count > 0 && exec.Argv[0] == "cat")
                return Task.FromResult(new SandboxExecResult(0, resultJson, ""));

            return Task.FromResult(new SandboxExecResult(0, "", ""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
