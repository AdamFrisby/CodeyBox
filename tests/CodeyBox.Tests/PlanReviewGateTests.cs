using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class PlanReviewGateTests
{
    [Theory]
    [InlineData(
        """
        {"files":["output.txt"],"testStrategy":["run tests"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "missing required string field 'approach'")]
    [InlineData(
        """
        {"approach":"do it","testStrategy":["run tests"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "missing required string-array field 'files'")]
    [InlineData(
        """
        {"approach":"do it","files":["output.txt"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "missing required string-array field 'testStrategy'")]
    [InlineData(
        """
        {"approach":"do it","files":["output.txt"],"testStrategy":["run tests"],"satisfiesTask":"does the task"}
        """,
        "missing required string-array field 'risks'")]
    [InlineData(
        """
        {"approach":"do it","files":["output.txt"],"testStrategy":["run tests"],"risks":["none"]}
        """,
        "missing required string field 'satisfiesTask'")]
    [InlineData(
        """
        {"approach":42,"files":["output.txt"],"testStrategy":["run tests"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "PLAN field 'approach' must be a string")]
    [InlineData(
        """
        {"approach":"do it","files":{"path":"output.txt"},"testStrategy":["run tests"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "PLAN field 'files' must be a string array")]
    [InlineData(
        """
        {"approach":"do it","files":[42],"testStrategy":["run tests"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "PLAN field 'files' item 0 must be a string")]
    [InlineData(
        """
        {"approach":"do it","files":"","testStrategy":["run tests"],"risks":["none"],"satisfiesTask":"does the task"}
        """,
        "missing required string-array field 'files'")]
    public async Task AlwaysPassReview_RejectsIncompleteOrWronglyTypedPlan(
        string artifact,
        string expectedMessage)
    {
        var gate = new AlwaysPassPlanReviewGate();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await gate.ReviewAsync(SampleItem(), artifact));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    private static WorkItem SampleItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "plan review",
        Prompt = "do work",
    };
}
