using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="WorkTimeoutPolicy"/>: the precedence chain
/// (per-item → per-project → global default) and the 1..480 clamp on the
/// configuration surfaces. Pure input→output tests with no I/O.
/// </summary>
public sealed class WorkTimeoutPolicyTests
{
    [Fact]
    public void Resolve_NoOverrides_ReturnsShippedDefault()
    {
        var (budget, source) = WorkTimeoutPolicy.Resolve(null, null, null);

        Assert.Equal(TimeSpan.FromMinutes(240), budget);
        Assert.Equal(WorkTimeoutSource.Default, source);
    }

    [Fact]
    public void Resolve_DefaultMinutesConstant_Is240()
    {
        // The defect is that the value was unreachable, not that 240 is
        // wrong — pin the shipped default so a well-meaning bump cannot
        // slip in as part of unrelated work.
        Assert.Equal(240, WorkTimeoutPolicy.DefaultMinutes);
    }

    [Fact]
    public void Resolve_ProjectOverride_BeatsDefault()
    {
        var (budget, source) = WorkTimeoutPolicy.Resolve(null, 300, 240);

        Assert.Equal(TimeSpan.FromMinutes(300), budget);
        Assert.Equal(WorkTimeoutSource.Project, source);
    }

    [Fact]
    public void Resolve_ItemOverride_BeatsProjectAndDefault()
    {
        var (budget, source) = WorkTimeoutPolicy.Resolve(TimeSpan.FromMinutes(60), 300, 240);

        Assert.Equal(TimeSpan.FromMinutes(60), budget);
        Assert.Equal(WorkTimeoutSource.Item, source);
    }

    [Fact]
    public void Resolve_ConfiguredDefault_ReplacesShippedDefault()
    {
        var (budget, source) = WorkTimeoutPolicy.Resolve(null, null, 120);

        Assert.Equal(TimeSpan.FromMinutes(120), budget);
        Assert.Equal(WorkTimeoutSource.Default, source);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-30, 1)]
    [InlineData(1, 1)]
    [InlineData(480, 480)]
    [InlineData(9999, 480)]
    public void Resolve_ProjectMinutes_ClampedTo1Through480(int configured, int expected)
    {
        // A typo (0, negative, huge) must fail safe to a live timeout, never
        // disable it.
        var (budget, source) = WorkTimeoutPolicy.Resolve(null, configured, null);

        Assert.Equal(TimeSpan.FromMinutes(expected), budget);
        Assert.Equal(WorkTimeoutSource.Project, source);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9999, 480)]
    public void Resolve_GlobalDefault_ClampedTo1Through480(int configured, int expected)
    {
        var (budget, source) = WorkTimeoutPolicy.Resolve(null, null, configured);

        Assert.Equal(TimeSpan.FromMinutes(expected), budget);
        Assert.Equal(WorkTimeoutSource.Default, source);
    }

    [Fact]
    public void Resolve_ItemValue_PassesThroughUnclamped()
    {
        // Per-item values are clamped at the API entry points (create / PATCH /
        // retry); the resolver trusts the persisted value so sub-minute test
        // budgets and operator-pinned values flow through exactly.
        var (budget, source) = WorkTimeoutPolicy.Resolve(TimeSpan.FromMilliseconds(250), 300, 240);

        Assert.Equal(TimeSpan.FromMilliseconds(250), budget);
        Assert.Equal(WorkTimeoutSource.Item, source);
    }

    [Theory]
    [InlineData(240, "240 minutes")]
    [InlineData(1, "1 minutes")]
    [InlineData(480, "480 minutes")]
    public void FormatBudget_MinuteScale(int minutes, string expected)
    {
        Assert.Equal(expected, WorkTimeoutPolicy.FormatBudget(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void FormatBudget_SubMinuteScale_UsesSecondsOrMilliseconds()
    {
        Assert.Equal("30 seconds", WorkTimeoutPolicy.FormatBudget(TimeSpan.FromSeconds(30)));
        Assert.Equal("250 ms", WorkTimeoutPolicy.FormatBudget(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void FormatTimeoutError_ItemSource_NamesBudgetSourceAndRemedy()
    {
        var message = WorkTimeoutPolicy.FormatTimeoutError(
            "work", WorkItemId.New(), TimeSpan.FromMinutes(60), WorkTimeoutSource.Item, "proj");

        Assert.Contains("phase 'work'", message);
        Assert.Contains("60 minutes", message);
        Assert.Contains("per-item", message);
        Assert.Contains("workTimeoutMinutes", message);
    }

    [Fact]
    public void FormatTimeoutError_ProjectSource_NamesProject()
    {
        var message = WorkTimeoutPolicy.FormatTimeoutError(
            "work", WorkItemId.New(), TimeSpan.FromMinutes(300), WorkTimeoutSource.Project, "my-repo");

        Assert.Contains("300 minutes", message);
        Assert.Contains("my-repo", message);
        Assert.Contains("WorkTimeoutMinutes", message);
    }

    [Fact]
    public void FormatTimeoutError_DefaultSource_NamesGlobalKey()
    {
        var message = WorkTimeoutPolicy.FormatTimeoutError(
            "work", WorkItemId.New(), TimeSpan.FromMinutes(240), WorkTimeoutSource.Default, "proj");

        Assert.Contains("240 minutes", message);
        Assert.Contains("CodeyBox:DefaultWorkTimeoutMinutes", message);
    }
}
