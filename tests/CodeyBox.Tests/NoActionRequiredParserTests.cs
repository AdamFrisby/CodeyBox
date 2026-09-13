using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit coverage for the no-action-required determination protocol: the
/// report parser, the terminal-state membership, and the failure-accounting
/// carve-outs that keep a determination from looking like an agent failure.
/// </summary>
public sealed class NoActionRequiredParserTests
{
    private static readonly NullLogger<NoActionRequiredParserTests> Log =
        NullLogger<NoActionRequiredParserTests>.Instance;

    [Fact]
    public void Parse_ValidReport_ReturnsReasonAndPrecondition()
    {
        var report = NoActionRequiredFileParser.Parse(
            """{"reason": "No trigger endpoint exists.", "precondition": "a POST trigger endpoint"}""",
            Log);

        Assert.NotNull(report);
        Assert.Equal("No trigger endpoint exists.", report!.Reason);
        Assert.Equal("a POST trigger endpoint", report.Precondition);
    }

    [Fact]
    public void Parse_ReasonOnly_ReturnsNullPrecondition()
    {
        var report = NoActionRequiredFileParser.Parse(
            """{"reason": "Nothing to do."}""",
            Log);

        Assert.NotNull(report);
        Assert.Equal("Nothing to do.", report!.Reason);
        Assert.Null(report.Precondition);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var report = NoActionRequiredFileParser.Parse(
            """{"reason": "  padded  ", "precondition": "  p  "}""",
            Log);

        Assert.NotNull(report);
        Assert.Equal("padded", report!.Reason);
        Assert.Equal("p", report.Precondition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    [InlineData("[1, 2]")]
    [InlineData("\"just a string\"")]
    [InlineData("{}")]
    [InlineData("""{"reason": ""}""")]
    [InlineData("""{"reason": "   "}""")]
    [InlineData("""{"reason": 42}""")]
    [InlineData("""{"reason": null}""")]
    [InlineData("""{"precondition": "p"}""")]
    public void Parse_InvalidReport_ReturnsNull(string? json)
    {
        Assert.Null(NoActionRequiredFileParser.Parse(json, Log));
    }

    [Fact]
    public void Parse_OversizeReason_RejectsReport()
    {
        var json = """{"reason": """ + '"' + new string('x', NoActionRequiredFileParser.MaxReasonLength + 1) + '"' + "}";
        Assert.Null(NoActionRequiredFileParser.Parse(json, Log));
    }

    [Fact]
    public void Parse_MaxLengthReason_Accepted()
    {
        var json = """{"reason": """ + '"' + new string('x', NoActionRequiredFileParser.MaxReasonLength) + '"' + "}";
        var report = NoActionRequiredFileParser.Parse(json, Log);
        Assert.NotNull(report);
        Assert.Equal(NoActionRequiredFileParser.MaxReasonLength, report!.Reason.Length);
    }

    [Fact]
    public void Parse_OversizePrecondition_DropsFieldKeepsReason()
    {
        var json = """{"reason": "fine", "precondition": """ + '"' + new string('y', NoActionRequiredFileParser.MaxPreconditionLength + 1) + '"' + "}";
        var report = NoActionRequiredFileParser.Parse(json, Log);

        Assert.NotNull(report);
        Assert.Equal("fine", report!.Reason);
        Assert.Null(report.Precondition);
    }

    [Fact]
    public void Parse_NonStringPrecondition_DropsFieldKeepsReason()
    {
        var report = NoActionRequiredFileParser.Parse(
            """{"reason": "fine", "precondition": 42}""",
            Log);

        Assert.NotNull(report);
        Assert.Equal("fine", report!.Reason);
        Assert.Null(report.Precondition);
    }

    [Fact]
    public void Parse_NullPrecondition_ReturnsNullPrecondition()
    {
        var report = NoActionRequiredFileParser.Parse(
            """{"reason": "fine", "precondition": null}""",
            Log);

        Assert.NotNull(report);
        Assert.Null(report!.Precondition);
    }

    [Fact]
    public void NoActionRequired_IsTerminal()
    {
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.NoActionRequired));
        Assert.Contains(WorkItemState.NoActionRequired, WorkItemStates.Terminal);
        Assert.Contains(WorkItemState.NoActionRequired, WorkItemDependencies.TerminalStates);
        Assert.True(WorkItemInFlight.IsExcludedState(WorkItemState.NoActionRequired));
    }

    [Fact]
    public void ExistingTerminalStates_Unchanged()
    {
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.Done));
        Assert.True(WorkItemStates.IsTerminal(WorkItemState.Failed));
        Assert.False(WorkItemStates.IsTerminal(WorkItemState.Queued));
        Assert.False(WorkItemStates.IsTerminal(WorkItemState.Working));
    }

    [Fact]
    public void DispatchOutcome_NoActionRequired_CountsAsSuccess()
    {
        var ex = new NoActionRequiredException(AgentKind.Claude, "precondition unmet");
        Assert.True(PipelineRunner.ClassifyDispatchOutcome(ex, genuineAttemptTimeout: false));
    }

    [Fact]
    public void InvolvementOutcome_NoActionRequired_MapsToSuccess()
    {
        var ex = new NoActionRequiredException(AgentKind.Claude, "precondition unmet");
        Assert.Equal(AgentInvolvementOutcomes.Success, AgentInvolvementOutcomes.ForFailure(ex));
    }
}
