using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class DefaultTerminalFailureClassifierTests
{
    private readonly DefaultTerminalFailureClassifier _sut = new();

    [Theory]
    [InlineData("quota", TerminalFailureClass.PolicyQuota)]
    [InlineData("QUOTA", TerminalFailureClass.PolicyQuota)]
    public void Quota_failures_route_to_PolicyQuota_regardless_of_case(string kind, TerminalFailureClass expected)
    {
        var item = BuildItem(state: WorkItemState.Failed, kind);
        Assert.Equal(expected, _sut.Classify(item).Class);
    }

    [Theory]
    [InlineData("infrastructure", TerminalFailureClass.Transient)]
    [InlineData("agent_unavailable", TerminalFailureClass.Transient)]
    public void Infra_shaped_failures_are_Transient(string kind, TerminalFailureClass expected)
    {
        var item = BuildItem(state: WorkItemState.Failed, kind);
        Assert.Equal(expected, _sut.Classify(item).Class);
    }

    [Fact]
    public void First_attempt_timeout_is_Transient()
    {
        var item = BuildItem(state: WorkItemState.Failed, failureKind: "timeout");
        var verdict = _sut.Classify(item);
        Assert.Equal(TerminalFailureClass.Transient, verdict.Class);
        Assert.Contains("first-attempt", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repeated_timeout_after_transient_cancel_retries_is_Deterministic()
    {
        // Pipeline already attempted at least one transient-cancel retry on
        // the prior pickup. Retrying again under the same budget cannot help.
        var item = BuildItem(state: WorkItemState.Failed, failureKind: "timeout") with
        {
            TransientCancelRetries = 1,
        };
        Assert.Equal(TerminalFailureClass.Deterministic, _sut.Classify(item).Class);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("agent")]
    [InlineData("configuration")]
    [InlineData("cancelled")]
    public void Agent_reasoning_and_operator_failures_are_Deterministic(string kind)
    {
        var item = BuildItem(state: WorkItemState.Failed, failureKind: kind);
        Assert.Equal(TerminalFailureClass.Deterministic, _sut.Classify(item).Class);
    }

    [Fact]
    public void AuditFailed_state_is_Deterministic()
    {
        var item = BuildItem(state: WorkItemState.AuditFailed, failureKind: null);
        Assert.Equal(TerminalFailureClass.Deterministic, _sut.Classify(item).Class);
    }

    [Fact]
    public void MergeConflictResolutionFailed_state_is_Deterministic()
    {
        var item = BuildItem(state: WorkItemState.MergeConflictResolutionFailed, failureKind: null);
        Assert.Equal(TerminalFailureClass.Deterministic, _sut.Classify(item).Class);
    }

    [Fact]
    public void Unknown_failure_kinds_fail_closed_to_Unknown_for_operator_triage()
    {
        var item = BuildItem(state: WorkItemState.Failed, failureKind: "other");
        var verdict = _sut.Classify(item);
        Assert.Equal(TerminalFailureClass.Unknown, verdict.Class);
        Assert.Contains("unclassified", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_failure_kind_falls_through_to_Unknown_on_plain_Failed_state()
    {
        var item = BuildItem(state: WorkItemState.Failed, failureKind: null);
        Assert.Equal(TerminalFailureClass.Unknown, _sut.Classify(item).Class);
    }

    [Fact]
    public void Verdict_reason_is_non_empty_for_every_class()
    {
        foreach (var kind in new[]
        {
            "quota", "infrastructure", "agent_unavailable", "timeout",
            "build", "agent", "configuration", "cancelled", "other", null,
        })
        {
            var item = BuildItem(state: WorkItemState.Failed, failureKind: kind);
            var verdict = _sut.Classify(item);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Reason), $"empty reason for kind={kind ?? "(null)"}");
        }
    }

    private static WorkItem BuildItem(WorkItemState state, string? failureKind)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = state,
            FailureKind = failureKind,
            LastError = "synthetic failure",
        };
}
