using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class PlanApprovalPolicyTests
{
    [Fact]
    public void ReviewTaskBinding_RejectsCanonicalPlanUnrelatedToTask()
    {
        var finding = PlanApprovalPolicy.ReviewTaskBinding(
            "write output.txt",
            Plan("refactor billing service", "billing.cs", "billing integration", "updates billing"),
            "process:plan-task-binding");

        Assert.NotNull(finding);
        Assert.Equal(AuditSeverity.Error, finding!.Severity);
        Assert.Equal("PLAN:satisfiesTask", finding.Location);
    }

    [Fact]
    public void ReviewTaskBinding_ApprovesCanonicalPlanExplicitlyBoundToTask()
    {
        var finding = PlanApprovalPolicy.ReviewTaskBinding(
            "write output.txt",
            Plan("write the output file", "output.txt", "verify output", "satisfies output request"),
            "process:plan-task-binding");

        Assert.Null(finding);
    }

    private static string Plan(string approach, string file, string test, string satisfies) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            approach,
            files = new[] { file },
            testStrategy = new[] { test },
            risks = new[] { "none" },
            satisfiesTask = satisfies,
        });
}
