using CodeyBox.Cli.Models;

namespace CodeyBox.Cli.Tests;

public sealed class WorkItemDtoTests
{
    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    [InlineData("AbandonedAfterRecoveryAttempts")]
    public void IsTerminalState_ServerTerminalStates_ReturnTrue(string state) =>
        Assert.True(WorkItemDto.IsTerminalState(state));

    [Theory]
    [InlineData("Merged")]
    [InlineData("Queued")]
    [InlineData("Working")]
    [InlineData("UpstreamPushing")]
    public void IsTerminalState_NonTerminalStates_ReturnFalse(string state) =>
        Assert.False(WorkItemDto.IsTerminalState(state));
}
