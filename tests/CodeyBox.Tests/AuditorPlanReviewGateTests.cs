using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class AuditorPlanReviewGateTests
{
    private const string ValidPlan = """
        {"approach":"a","files":["f.cs"],"testStrategy":["unit"],"risks":["none"],"satisfiesTask":"yes"}
        """;

    [Fact]
    public async Task ReviewAsync_ApprovesWhenPlanTargetReviewerPasses()
    {
        var runner = new FakeTextOnlyRunner("""{"passed": true, "findings": []}""");
        var gate = BuildGate(runner, auditTypes: ["architecture"]);

        var decision = await gate.ReviewAsync(Request());

        Assert.True(decision.Approved);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task ReviewAsync_RejectsAndSummarisesBlockingFindings()
    {
        var runner = new FakeTextOnlyRunner(
            """{"passed": false, "findings": [{"severity":"error","title":"wrong approach","description":"picks the wrong data structure"}]}""");
        var gate = BuildGate(runner, auditTypes: ["architecture"]);

        var decision = await gate.ReviewAsync(Request());

        Assert.False(decision.Approved);
        Assert.Contains("wrong approach", decision.RejectionReason, StringComparison.Ordinal);
        Assert.Contains("architecture:llm-review", decision.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewAsync_ApprovesWhenNoPlanTargetReviewersConfigured()
    {
        // security is a Code-only audit type — no plan reviewers → approve on validity.
        var runner = new FakeTextOnlyRunner("unused");
        var gate = BuildGate(runner, auditTypes: ["security"]);

        var decision = await gate.ReviewAsync(Request());

        Assert.True(decision.Approved);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ReviewAsync_RejectsWhenReviewerVerdictIsPassedFalseWithoutErrorFinding()
    {
        // An explicit reject verdict (passed=false) with only advisory findings
        // must still block — the boolean cannot be discarded.
        var runner = new FakeTextOnlyRunner(
            """{"passed": false, "findings": [{"severity":"warning","title":"iffy approach","description":"maybe"}]}""");
        var gate = BuildGate(runner, auditTypes: ["architecture"]);

        var decision = await gate.ReviewAsync(Request());

        Assert.False(decision.Approved);
        Assert.Contains("plan rejected by reviewer", decision.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewAsync_ApprovesWhenAgentRequiresSandbox()
    {
        // Subscription CLIs (Cursor/Opencode) need a sandbox the host-only gate
        // can't provide; the gate degrades-and-skips instead of hard-failing.
        var runner = new FakeTextOnlyRunner("unused", requiresSandbox: true);
        var gate = BuildGate(runner, auditTypes: ["architecture"]);

        var decision = await gate.ReviewAsync(Request());

        Assert.True(decision.Approved);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task ReviewAsync_ApprovesWithAdvisoryNotes_WhenOnlyNonErrorFindings()
    {
        // passed=true with warning/info findings → approved, but the advisory
        // count is surfaced in the summary.
        var runner = new FakeTextOnlyRunner(
            """{"passed": true, "findings": [{"severity":"warning","title":"nit","description":"n"},{"severity":"info","title":"fyi","description":"i"}]}""");
        var gate = BuildGate(runner, auditTypes: ["architecture"]);

        var decision = await gate.ReviewAsync(Request());

        Assert.True(decision.Approved);
        Assert.Contains("advisory", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewAsync_RejectsStructurallyInvalidPlan()
    {
        var runner = new FakeTextOnlyRunner("unused");
        var gate = BuildGate(runner, auditTypes: ["architecture"]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await gate.ReviewAsync(Request(artifact: """{"approach":"a"}""")));
    }

    private static AuditorPlanReviewGate BuildGate(FakeTextOnlyRunner runner, string[] auditTypes)
    {
        var project = new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Proj",
            RepositoryUrl = "https://example.com/r.git",
            Audit = new ProjectAudit { AuditTypes = auditTypes },
        };
        return new AuditorPlanReviewGate(
            new ProjectAuditorComposer(new PresetCatalog()),
            new InMemoryProjectRepository(project),
            new AgentRegistry([runner]),
            new StaticCredentialProvider(),
            NullLogger<AuditorPlanReviewGate>.Instance);
    }

    private static PlanReviewRequest Request(string artifact = ValidPlan) => new(
        WorkItemId.New(),
        new ProjectId("proj"),
        "title",
        "do the work",
        PromptRevision: 1,
        artifact,
        AgentKind.Claude,
        AgentInstanceId: null,
        ModelId: null,
        ReasoningMode: null);
}
