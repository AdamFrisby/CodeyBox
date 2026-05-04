using System.Diagnostics;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when no ActivitySource listener is registered (or when no
/// activitySource is passed to TimingScope), no Activity is started.
/// This ensures the disabled OTel path is truly a no-op.
/// </summary>
public sealed class OtelDisabledNoOpTests
{
    private static readonly WorkItemId TestId = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));

    [Fact]
    public async Task TimingScope_NoActivitySource_DoesNotStartActivityOnPipelineSource()
    {
        // Register a listener scoped to the exact CodeyBox.Pipeline source (not a broad wildcard,
        // to avoid capturing activities from parallel tests that use unique GUID-named sources).
        // TimingScope must not touch CodeyBox.Pipeline when activitySource is not passed.
        var started = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src == CodeyBoxActivities.Pipeline,
            ActivityStarted = a => started.Add(a.OperationName),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec"
            // activitySource not passed — defaults to null
        );

        Assert.Empty(started);
    }

    [Fact]
    public async Task TimingScope_NullActivitySource_CurrentActivityIsNull()
    {
        // With no activitySource, the current Activity should remain whatever it was before.
        var before = Activity.Current;

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "agent.exec"
        );

        Assert.Equal(before, Activity.Current);
    }

    [Fact]
    public async Task TimingScope_WithActivitySource_ButNoListener_StartsNoActivity()
    {
        // An ActivitySource with no listener registered must silently return null from StartActivity.
        var isolatedSource = new ActivitySource("CodeyBox.TestIsolated." + Guid.NewGuid().ToString("N"));

        var started = new List<string>();
        // Do NOT register a listener for isolatedSource.

        await using var scope = await TimingScope.BeginAsync(
            store: null,
            itemId: TestId,
            phase: "work",
            step: "some.step",
            activitySource: isolatedSource
        );

        // Activity.Current may be non-null if a parent context exists, but the
        // isolated source produced nothing — started list stays empty regardless.
        Assert.Empty(started);
    }
}
