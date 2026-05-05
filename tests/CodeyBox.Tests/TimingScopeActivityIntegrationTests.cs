using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that TimingScope correctly starts an OTel Activity, sets the
/// expected span tags, and disposes (stops) the Activity on scope disposal.
/// </summary>
public sealed class TimingScopeActivityIntegrationTests
{
    private static readonly WorkItemId TestId = new(Guid.Parse("00000000-0000-0000-0000-000000000003"));

    [Fact]
    public async Task BeginAsync_WithActivitySource_StartsActivityWithCorrectOperationName()
    {
        var src = new ActivitySource("CodeyBox.TestActivityOp." + Guid.NewGuid().ToString("N"));
        var started = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStarted = started.Add,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec",
            activitySource: src);

        Assert.Single(started);
        Assert.Equal("agent.exec", started[0].OperationName);
    }

    [Fact]
    public async Task BeginAsync_SetsWorkItemIdTag()
    {
        var src = new ActivitySource("CodeyBox.TestActivityTags." + Guid.NewGuid().ToString("N"));
        Activity? captured = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStarted = a => captured = a,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec",
            activitySource: src);

        Assert.NotNull(captured);
        Assert.Equal(TestId.ToString(), captured!.GetTagItem("codeybox.work_item_id")?.ToString());
    }

    [Fact]
    public async Task BeginAsync_SetsPhaseTag()
    {
        var src = new ActivitySource("CodeyBox.TestActivityPhase." + Guid.NewGuid().ToString("N"));
        Activity? captured = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStarted = a => captured = a,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "audit",
            step: "auditor.shell",
            activitySource: src);

        Assert.NotNull(captured);
        Assert.Equal("audit", captured!.GetTagItem("codeybox.phase")?.ToString());
    }

    [Fact]
    public async Task BeginAsync_WithIteration_SetsIterationTag()
    {
        var src = new ActivitySource("CodeyBox.TestActivityIter." + Guid.NewGuid().ToString("N"));
        Activity? captured = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStarted = a => captured = a,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "audit",
            step: "auditor.llm",
            iteration: 3,
            activitySource: src);

        Assert.NotNull(captured);
        Assert.Equal("3", captured!.GetTagItem("codeybox.iteration")?.ToString());
    }

    [Fact]
    public async Task BeginAsync_WithoutIteration_DoesNotSetIterationTag()
    {
        var src = new ActivitySource("CodeyBox.TestActivityNoIter." + Guid.NewGuid().ToString("N"));
        Activity? captured = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStarted = a => captured = a,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec",
            activitySource: src);

        Assert.NotNull(captured);
        Assert.Null(captured!.GetTagItem("codeybox.iteration"));
    }

    [Fact]
    public async Task BeginAsync_WithMetadata_SetsMetadataTags()
    {
        var src = new ActivitySource("CodeyBox.TestActivityMeta." + Guid.NewGuid().ToString("N"));
        Activity? captured = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStarted = a => captured = a,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var meta = new Dictionary<string, object>
        {
            ["agent"] = "claude",
            ["model"] = "claude-opus-4-7",
        };

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec",
            metadata: meta,
            activitySource: src);

        Assert.NotNull(captured);
        Assert.Equal("claude", captured!.GetTagItem("codeybox.agent")?.ToString());
        Assert.Equal("claude-opus-4-7", captured.GetTagItem("codeybox.model")?.ToString());
    }

    [Fact]
    public async Task DisposeAsync_StopsActivity()
    {
        var src = new ActivitySource("CodeyBox.TestActivityStop." + Guid.NewGuid().ToString("N"));
        var stopped = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == src,
            ActivityStopped = stopped.Add,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec",
            activitySource: src);

        Assert.Empty(stopped);
        await scope.DisposeAsync();
        Assert.Single(stopped);
    }
}
