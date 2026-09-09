using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class TestFailureAttributionClassifierTests
{
    [Theory]
    [InlineData(TestFailureRunOutcome.Passed, TestFailureRunOutcome.Failed, TestFailureAttribution.DiffAttributable)]
    [InlineData(TestFailureRunOutcome.Failed, TestFailureRunOutcome.Failed, TestFailureAttribution.NotDiffAttributable)]
    [InlineData(TestFailureRunOutcome.Passed, TestFailureRunOutcome.Passed, TestFailureAttribution.NotDiffAttributable)]
    [InlineData(TestFailureRunOutcome.Failed, TestFailureRunOutcome.Passed, TestFailureAttribution.NotDiffAttributable)]
    public void Classify_UsesOnlyBaseAndDiffOutcomes(
        TestFailureRunOutcome baseRun,
        TestFailureRunOutcome diffRun,
        TestFailureAttribution expected)
    {
        var result = TestFailureAttributionClassifier.Classify(new TestFailureRunPair(
            "App.Tests.InvoiceTests.CalculatesTotals",
            baseRun,
            diffRun));

        Assert.Equal(expected, result.Attribution);
        Assert.Equal(baseRun, result.BaseRun);
        Assert.Equal(diffRun, result.DiffRun);
        Assert.Equal(TestFailureAttributionSkipReason.None, result.SkipReason);
    }

    [Fact]
    public void FailClosed_BaseRerunUnavailable_ReturnsDiffAttributable()
    {
        var result = Assert.Single(TestFailureAttributionClassifier.FailClosed(
            ["App.Tests.InvoiceTests.CalculatesTotals"],
            TestFailureAttributionSkipReason.BaseRerunUnavailable));

        Assert.Equal(TestFailureAttribution.DiffAttributable, result.Attribution);
        Assert.Equal(TestFailureRunOutcome.Unavailable, result.BaseRun);
        Assert.Equal(TestFailureRunOutcome.Failed, result.DiffRun);
        Assert.Equal(TestFailureAttributionSkipReason.BaseRerunUnavailable, result.SkipReason);
    }

    [Fact]
    public void FailClosed_Disabled_ReturnsDiffAttributableWithoutBaseRun()
    {
        var result = Assert.Single(TestFailureAttributionClassifier.FailClosed(
            ["App.Tests.InvoiceTests.CalculatesTotals"],
            TestFailureAttributionSkipReason.Disabled));

        Assert.Equal(TestFailureAttribution.DiffAttributable, result.Attribution);
        Assert.Equal(TestFailureRunOutcome.NotRun, result.BaseRun);
        Assert.Equal(TestFailureRunOutcome.Failed, result.DiffRun);
        Assert.Equal(TestFailureAttributionSkipReason.Disabled, result.SkipReason);
    }
}
