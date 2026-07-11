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

    [Fact]
    public void ReviewTaskBinding_RequiresProportionalCoverageForSubstantiveTask()
    {
        // A substantive task (9 distinct significant terms) requires more than a
        // single echoed token: ceil(9 * 0.2) = 2 distinct terms must be covered.
        const string Task =
            "implement secure billing invoice export pipeline reconciliation ledger auditing";

        var echoesOne = PlanApprovalPolicy.ReviewTaskBinding(
            Task,
            Plan("handle billing", "billing.cs", "billing check", "does billing"),
            "process:plan-task-binding");
        Assert.NotNull(echoesOne);
        Assert.Equal("PLAN:satisfiesTask", echoesOne!.Location);

        var echoesTwo = PlanApprovalPolicy.ReviewTaskBinding(
            Task,
            Plan("handle billing", "billing.cs", "billing check", "does billing invoice export"),
            "process:plan-task-binding");
        Assert.Null(echoesTwo);
    }

    [Fact]
    public void ReviewTaskBinding_HigherCoverageRatioTightensTheGate()
    {
        const string Task =
            "implement secure billing invoice export pipeline reconciliation ledger auditing";

        var plan = Plan("handle billing", "billing.cs", "billing check", "does billing invoice export");

        // Two terms covered passes the default ratio but fails a full-coverage ratio.
        Assert.Null(PlanApprovalPolicy.ReviewTaskBinding(Task, plan, "process:plan-task-binding"));
        Assert.NotNull(PlanApprovalPolicy.ReviewTaskBinding(Task, plan, "process:plan-task-binding", coverageRatio: 1.0));
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
