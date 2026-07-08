using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class PlanArtifactValidationGateTests
{
    private const string ValidPlan = """
        {"approach":"a","files":["f.cs"],"testStrategy":["unit"],"risks":["none"],"satisfiesTask":"yes"}
        """;

    [Fact]
    public async Task ReviewAsync_CompatibilityGateApprovesSchemaValidPlan()
    {
        var gate = BuildGate();

        var decision = await gate.ReviewAsync(Request());

        Assert.True(decision.Approved);
        Assert.Contains("compatibility", decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewAsync_CompatibilityGateRejectsStructurallyInvalidPlan()
    {
        var gate = BuildGate();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await gate.ReviewAsync(Request(artifact: """{"approach":"a"}""")));
    }

    private static PlanArtifactValidationGate BuildGate()
        => new(NullLogger<PlanArtifactValidationGate>.Instance);

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
