using CodeyBox.StatisticsPlugin;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Tests;

/// <summary>
/// Binding coverage for <see cref="ResetCreditExpiryOptions.FromConfiguration"/>
/// — the operator-facing knobs for the reset-credit expiry tracker (period,
/// buffer, agent, and the seeded pre-observation credit list).
/// </summary>
public sealed class ResetCreditExpiryOptionsTests
{
    private static IConfigurationSection Section(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Root");

    [Fact]
    public void Defaults_WhenSectionEmpty()
    {
        var opts = ResetCreditExpiryOptions.FromConfiguration(Section(new()));

        Assert.Equal("codex", opts.Agent);
        Assert.Equal(TimeSpan.FromDays(30), opts.ExpiryPeriod);
        Assert.Equal(TimeSpan.FromHours(24), opts.SafetyBuffer);
        Assert.Empty(opts.Seeds);
    }

    [Fact]
    public void ReadsScalarKnobs()
    {
        var opts = ResetCreditExpiryOptions.FromConfiguration(Section(new()
        {
            ["Root:Agent"] = " codex ",
            ["Root:ExpiryPeriodDays"] = "14",
            ["Root:SafetyBufferHours"] = "6",
            ["Root:LookbackDays"] = "45",
        }));

        Assert.Equal("codex", opts.Agent);
        Assert.Equal(TimeSpan.FromDays(14), opts.ExpiryPeriod);
        Assert.Equal(TimeSpan.FromHours(6), opts.SafetyBuffer);
        Assert.Equal(TimeSpan.FromDays(45), opts.Lookback);
    }

    [Fact]
    public void ReadsSeedArray_SkippingEntriesWithNoOrUnparseableExpiry()
    {
        var opts = ResetCreditExpiryOptions.FromConfiguration(Section(new()
        {
            ["Root:Seeds:0:EstimatedExpiresAt"] = "2026-07-16T00:00:00Z",
            ["Root:Seeds:0:Label"] = "credit A",
            ["Root:Seeds:1:Label"] = "no expiry — dropped",
            ["Root:Seeds:2:EstimatedExpiresAt"] = "not-a-date",
            ["Root:Seeds:3:EstimatedExpiresAt"] = "2026-08-01T00:00:00Z",
        }));

        Assert.Equal(2, opts.Seeds.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T00:00:00Z"), opts.Seeds[0].EstimatedExpiresAt);
        Assert.Equal("credit A", opts.Seeds[0].Label);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:00:00Z"), opts.Seeds[1].EstimatedExpiresAt);
        Assert.Null(opts.Seeds[1].Label);
    }

    [Fact]
    public void InvalidScalarValues_FallBackToDefaults()
    {
        var opts = ResetCreditExpiryOptions.FromConfiguration(Section(new()
        {
            ["Root:ExpiryPeriodDays"] = "-5",     // negative rejected
            ["Root:SafetyBufferHours"] = "junk",  // unparseable rejected
        }));

        Assert.Equal(TimeSpan.FromDays(30), opts.ExpiryPeriod);
        Assert.Equal(TimeSpan.FromHours(24), opts.SafetyBuffer);
    }
}
