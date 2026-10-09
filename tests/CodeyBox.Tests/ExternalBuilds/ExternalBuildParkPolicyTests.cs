using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Tests.ExternalBuilds;

/// <summary>Park policy: deterministic boundaries, estimators, censored
/// samples, cache classes, outliers, and the elapsed fallback.</summary>
public sealed class ExternalBuildParkPolicyTests
{
    private static ExternalBuildTargetKey Key(string cache = "warm") => new()
    {
        ProviderId = "p", TargetId = "t", Configuration = "release",
        Toolchain = "tc", Platform = "linux", CacheClass = cache,
    };

    private static ExternalBuildDurationSample Sample(
        ExternalBuildTargetKey key, double minutes, bool ok = true, bool cold = false, int daysAgo = 0) =>
        new(key, TimeSpan.FromMinutes(minutes), ok,
            DateTimeOffset.UtcNow.AddDays(-daysAgo), cold);

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void Boundary_BelowEqualAbove10Minutes(double minutes, bool expectedPark)
    {
        var key = Key();
        var history = Enumerable.Range(0, 5).Select(_ => Sample(key, minutes)).ToList();
        var now = DateTimeOffset.UtcNow;
        var decision = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Median, now);
        Assert.Equal(expectedPark, decision.ShouldPark);
        Assert.Equal(5, decision.SampleCount);
        Assert.NotNull(decision.PredictedDuration);
    }

    [Fact]
    public void InsufficientHistory_StaysActive_WithElapsedFallback()
    {
        var key = Key();
        var now = DateTimeOffset.UtcNow;
        var decision = ExternalBuildParkPolicy.Decide(key, [], 8, 3, ParkEstimator.Median, now);
        Assert.False(decision.ShouldPark);
        Assert.Null(decision.PredictedDuration);
        Assert.False(ExternalBuildParkPolicy.ShouldParkOnElapsed(TimeSpan.FromMinutes(9)));
        Assert.False(ExternalBuildParkPolicy.ShouldParkOnElapsed(TimeSpan.FromMinutes(10)));
        Assert.True(ExternalBuildParkPolicy.ShouldParkOnElapsed(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void FailedCancelledRuns_AreCensored_NotShortBuilds()
    {
        var key = Key();
        var now = DateTimeOffset.UtcNow;
        var history = new List<ExternalBuildDurationSample>
        {
            Sample(key, 1, ok: false),
            Sample(key, 2, ok: false),
            Sample(key, 1, ok: false),
        };
        var decision = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Median, now);
        Assert.False(decision.ShouldPark);
        Assert.Equal(0, decision.SampleCount);
    }

    [Fact]
    public void ColdWarmCache_Distinguished()
    {
        var warm = Key("warm");
        var history = new List<ExternalBuildDurationSample>
        {
            Sample(warm, 20, cold: false),
            Sample(warm, 22, cold: false),
            Sample(warm, 21, cold: false),
            Sample(Key("cold"), 2, cold: true),
            Sample(Key("cold"), 3, cold: true),
            Sample(Key("cold"), 2, cold: true),
        };
        var now = DateTimeOffset.UtcNow;
        var warmDecision = ExternalBuildParkPolicy.Decide(warm, history, 8, 3, ParkEstimator.Median, now, coldCache: false);
        Assert.True(warmDecision.ShouldPark);
        var coldDecision = ExternalBuildParkPolicy.Decide(Key("cold"), history, 8, 3, ParkEstimator.Median, now, coldCache: true);
        Assert.False(coldDecision.ShouldPark);
    }

    [Fact]
    public void StaleHistory_Ignored()
    {
        var key = Key();
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 5).Select(_ => Sample(key, 30, daysAgo: 60)).ToList();
        var decision = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Median, now);
        Assert.False(decision.ShouldPark);
        Assert.Equal(0, decision.SampleCount);
    }

    [Fact]
    public void Outlier_Trimmed()
    {
        var key = Key();
        var history = new List<ExternalBuildDurationSample>
        {
            Sample(key, 9), Sample(key, 9), Sample(key, 9), Sample(key, 60),
        };
        var now = DateTimeOffset.UtcNow;
        var decision = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Median, now);
        Assert.False(decision.ShouldPark);
    }

    [Fact]
    public void MeanEstimator_Selecttable()
    {
        var key = Key();
        var history = new List<ExternalBuildDurationSample>
        {
            Sample(key, 12), Sample(key, 12), Sample(key, 12),
        };
        var now = DateTimeOffset.UtcNow;
        var mean = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Mean, now);
        var median = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Median, now);
        Assert.True(mean.ShouldPark);
        Assert.True(median.ShouldPark);
        Assert.True(ExternalBuildOptions.IsValid(new ExternalBuildOptions()));
        var bad = new ExternalBuildOptions { HistorySampleSize = 2, MinSamplesForPrediction = 5 };
        Assert.False(ExternalBuildOptions.IsValid(bad));
    }

    [Fact]
    public void NonComparableTargets_NotMixed()
    {
        var key = Key();
        var other = Key() with { TargetId = "other" };
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 5).Select(_ => Sample(other, 30)).ToList();
        var decision = ExternalBuildParkPolicy.Decide(key, history, 8, 3, ParkEstimator.Median, now);
        Assert.False(decision.ShouldPark);
        Assert.Equal(0, decision.SampleCount);
    }
}
