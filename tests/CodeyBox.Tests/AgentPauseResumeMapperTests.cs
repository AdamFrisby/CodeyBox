using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentPauseResumeMapperTests
{
    [Theory]
    [InlineData("planning", "planning")]
    [InlineData("plan_review", "plan_review")]
    [InlineData("plan_approved", "plan_approved")]
    [InlineData("audit", "audit")]
    [InlineData("conflict_rework", "conflict_rework")]
    [InlineData("merge", "merge")]
    [InlineData("upstream", "upstream")]
    [InlineData("work", "work")]
    public void NormalizeRetryFrom_RecognizesKnownValues(string input, string expected)
    {
        Assert.Equal(expected, AgentPauseResumeMapper.NormalizeRetryFrom(input));
    }

    [Theory]
    [InlineData("PLANNING")]
    [InlineData("Plan_Review")]
    [InlineData("PLAN_APPROVED")]
    [InlineData("Audit")]
    public void NormalizeRetryFrom_IsCaseInsensitive(string input)
    {
        var expected = input.ToLowerInvariant();
        Assert.Equal(expected, AgentPauseResumeMapper.NormalizeRetryFrom(input));
    }

    [Theory]
    [InlineData("  planning  ")]
    [InlineData("\tplan_review\n")]
    [InlineData(" plan_approved ")]
    public void NormalizeRetryFrom_TrimsSurroundingWhitespace(string input)
    {
        Assert.Equal(input.Trim(), AgentPauseResumeMapper.NormalizeRetryFrom(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not_a_real_phase")]
    [InlineData("plan")]
    [InlineData("planning_review")]
    [InlineData("planreview")]
    [InlineData("rework")]
    public void NormalizeRetryFrom_DefaultsToWork_ForUnknownOrEmptyValues(string? input)
    {
        Assert.Equal("work", AgentPauseResumeMapper.NormalizeRetryFrom(input));
    }

    [Theory]
    [InlineData("planning", WorkItemState.Queued)]
    [InlineData("plan_review", WorkItemState.PlanReview)]
    [InlineData("plan_approved", WorkItemState.PlanApproved)]
    [InlineData("audit", WorkItemState.WorkComplete)]
    [InlineData("conflict_rework", WorkItemState.ReworkingForConflict)]
    [InlineData("merge", WorkItemState.AuditPassed)]
    [InlineData("upstream", WorkItemState.Merged)]
    [InlineData("work", WorkItemState.Queued)]
    [InlineData(null, WorkItemState.Queued)]
    [InlineData("unknown", WorkItemState.Queued)]
    public void ResumeStateForRetryFrom_MapsNormalizedRetryFromsToResumeState(
        string? retryFrom,
        WorkItemState expected)
    {
        Assert.Equal(expected, AgentPauseResumeMapper.ResumeStateForRetryFrom(retryFrom));
    }

    [Theory]
    [InlineData(WorkItemState.Planning, "planning")]
    [InlineData(WorkItemState.PlanReview, "plan_review")]
    [InlineData(WorkItemState.PlanApproved, "plan_approved")]
    [InlineData(WorkItemState.WorkComplete, "audit")]
    [InlineData(WorkItemState.Auditing, "audit")]
    [InlineData(WorkItemState.Reworking, "audit")]
    [InlineData(WorkItemState.ReworkingForConflict, "conflict_rework")]
    [InlineData(WorkItemState.AuditFailed, "audit")]
    [InlineData(WorkItemState.AuditPassed, "merge")]
    [InlineData(WorkItemState.Merging, "merge")]
    [InlineData(WorkItemState.Merged, "upstream")]
    [InlineData(WorkItemState.UpstreamPushing, "upstream")]
    [InlineData(WorkItemState.Queued, "work")]
    [InlineData(WorkItemState.Working, "work")]
    [InlineData(WorkItemState.Done, "work")]
    [InlineData(WorkItemState.Failed, "work")]
    public void RetryFromForState_MapsLifecycleStatesToOperatorFacingRetryFromValues(
        WorkItemState state,
        string expected)
    {
        Assert.Equal(expected, AgentPauseResumeMapper.RetryFromForState(state));
    }
}
