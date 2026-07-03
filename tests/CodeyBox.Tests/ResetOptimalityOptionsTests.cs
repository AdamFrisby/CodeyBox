using CodeyBox.StatisticsPlugin;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Binding coverage for <see cref="ResetOptimalityConfigOptions.FromConfiguration"/>
/// — the operator-facing knobs for the reset-optimality advisor (plan end,
/// cadence anchor + period, dust threshold, tolerance, agent scope, refinement).
/// </summary>
public sealed class ResetOptimalityOptionsTests
{
    private static IConfigurationSection Section(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Root");

    [Fact]
    public void Defaults_WhenSectionEmpty()
    {
        var opts = ResetOptimalityConfigOptions.FromConfiguration(Section(new()));

        Assert.Equal(new[] { "codex" }, opts.Agents);
        Assert.Null(opts.PlanEndsAt);
        Assert.Null(opts.CadenceAnchor);
        Assert.Equal(TimeSpan.FromDays(7), opts.CadencePeriod);
        Assert.Equal(1.0, opts.DustThresholdPct);
        Assert.Equal(TimeSpan.FromHours(6), opts.TimeTolerance);
        Assert.Equal(TimeSpan.FromHours(6), opts.AnchorRefineTolerance);
        Assert.Equal("weekly", opts.ResetTargetWindow);
        Assert.True(opts.RefineAnchorFromLogger);
    }

    [Fact]
    public void ReadsScalarKnobs()
    {
        var opts = ResetOptimalityConfigOptions.FromConfiguration(Section(new()
        {
            ["Root:PlanEndsAt"] = "2026-12-31T00:00:00Z",
            ["Root:CadenceAnchor"] = "2026-06-01T06:00:00Z",
            ["Root:CadencePeriodDays"] = "7",
            ["Root:DustThresholdPct"] = "2.5",
            ["Root:TimeToleranceHours"] = "12",
            ["Root:AnchorRefineToleranceHours"] = "3",
            ["Root:ResetTargetWindow"] = "seven_day",
            ["Root:RefineAnchorFromLogger"] = "false",
        }));

        Assert.Equal(DateTimeOffset.Parse("2026-12-31T00:00:00Z"), opts.PlanEndsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T06:00:00Z"), opts.CadenceAnchor);
        Assert.Equal(TimeSpan.FromDays(7), opts.CadencePeriod);
        Assert.Equal(2.5, opts.DustThresholdPct);
        Assert.Equal(TimeSpan.FromHours(12), opts.TimeTolerance);
        Assert.Equal(TimeSpan.FromHours(3), opts.AnchorRefineTolerance);
        Assert.Equal("seven_day", opts.ResetTargetWindow);
        Assert.False(opts.RefineAnchorFromLogger);
    }

    [Fact]
    public void ReadsAgentsArray()
    {
        var opts = ResetOptimalityConfigOptions.FromConfiguration(Section(new()
        {
            ["Root:Agents:0"] = "codex",
            ["Root:Agents:1"] = " claude ",
        }));

        Assert.Equal(new[] { "codex", "claude" }, opts.Agents);
    }

    [Fact]
    public void PresentButEmptyAgentsSection_MeansAdviseForNone()
    {
        // A present Agents section with no usable entries is an explicit
        // "advise for none" — it must NOT silently fall back to the codex
        // default (that would re-enable an operator who meant to disable).
        var opts = ResetOptimalityConfigOptions.FromConfiguration(Section(new()
        {
            ["Root:Agents:0"] = "   ",
        }));

        Assert.Empty(opts.Agents);
    }

    [Fact]
    public void InvalidScalarValues_FallBackToDefaults()
    {
        var opts = ResetOptimalityConfigOptions.FromConfiguration(Section(new()
        {
            ["Root:CadenceAnchor"] = "not-a-date",   // unparseable → null
            ["Root:CadencePeriodDays"] = "-3",       // negative → default
            ["Root:TimeToleranceHours"] = "junk",    // unparseable → default
        }));

        Assert.Null(opts.CadenceAnchor);
        Assert.Equal(TimeSpan.FromDays(7), opts.CadencePeriod);
        Assert.Equal(TimeSpan.FromHours(6), opts.TimeTolerance);
    }

    [Fact]
    public void DustThreshold_IsClampedToPercentageRange()
    {
        var high = ResetOptimalityConfigOptions.FromConfiguration(Section(new()
        {
            ["Root:DustThresholdPct"] = "250",
        }));
        Assert.Equal(100.0, high.DustThresholdPct);

        var low = ResetOptimalityConfigOptions.FromConfiguration(Section(new()
        {
            ["Root:DustThresholdPct"] = "-5",
        }));
        Assert.Equal(0.0, low.DustThresholdPct);
    }
}
