using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AutoRetryOptionsFactoryTests
{
    [Fact]
    public void BuildAutoRetryOptions_Disabled_IgnoresMalformedValues()
    {
        var opts = OrchestratorOptionsFactory.BuildAutoRetryOptions(
            enabled: false,
            periodicCheckInterval: "not-a-timespan",
            clockDriftMargin: "also-bad",
            maxRetriesPerWorkItem: -1);

        Assert.False(opts.Enabled);
    }

    [Fact]
    public void BuildAutoRetryOptions_Enabled_ParsesConfiguredValues()
    {
        var opts = OrchestratorOptionsFactory.BuildAutoRetryOptions(
            enabled: true,
            periodicCheckInterval: "00:03:00",
            clockDriftMargin: "00:00:15",
            maxRetriesPerWorkItem: 7);

        Assert.True(opts.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(3), opts.PeriodicCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), opts.ClockDriftSafetyMargin);
        Assert.Equal(7, opts.MaxAutoRetriesPerWorkItem);
    }

    [Fact]
    public void Build_WithAutoRetryParameters_UsesAutoRetryBuilder()
    {
        var opts = OrchestratorOptionsFactory.Build(
            legacyConcurrency: null,
            workerPool: new WorkerPoolOptions { MaxConcurrentWorkers = 2 },
            autoRetryEnabled: true,
            autoRetryPeriodicInterval: "00:04:00",
            autoRetryDriftMargin: "00:00:30",
            autoRetryMaxRetries: 5,
            log: NullLogger.Instance);

        Assert.Equal(2, opts.MaxConcurrentWorkers);
        Assert.True(opts.AutoRetryOnQuotaFailure.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(4), opts.AutoRetryOnQuotaFailure.PeriodicCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), opts.AutoRetryOnQuotaFailure.ClockDriftSafetyMargin);
        Assert.Equal(5, opts.AutoRetryOnQuotaFailure.MaxAutoRetriesPerWorkItem);
    }

    [Theory]
    [InlineData("bad", "00:00:01", 1, "PeriodicCheckInterval")]
    [InlineData("00:00:00", "00:00:01", 1, "PeriodicCheckInterval")]
    [InlineData("-00:00:01", "00:00:01", 1, "PeriodicCheckInterval")]
    [InlineData("00:00:01", "bad", 1, "ClockDriftSafetyMargin")]
    [InlineData("00:00:01", "-00:00:01", 1, "ClockDriftSafetyMargin")]
    [InlineData("00:00:01", "00:00:01", -1, "MaxAutoRetriesPerWorkItem")]
    public void BuildAutoRetryOptions_Enabled_RejectsInvalidValues(
        string periodic,
        string drift,
        int maxRetries,
        string expectedMessage)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildAutoRetryOptions(
                enabled: true,
                periodicCheckInterval: periodic,
                clockDriftMargin: drift,
                maxRetriesPerWorkItem: maxRetries));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void BuildTransientRetryOptions_Disabled_IgnoresMalformedValues()
    {
        var opts = OrchestratorOptionsFactory.BuildTransientRetryOptions(
            enabled: false,
            periodicCheckInterval: "bad",
            baseDelay: "bad",
            multiplier: -1,
            maxDelay: "bad",
            maxRetriesPerWorkItem: -1,
            maxElapsedTime: "bad",
            jitterMode: "bad");

        Assert.False(opts.Enabled);
    }

    [Fact]
    public void BuildTransientRetryOptions_Enabled_ParsesConfiguredValues()
    {
        var opts = OrchestratorOptionsFactory.BuildTransientRetryOptions(
            enabled: true,
            periodicCheckInterval: "00:00:10",
            baseDelay: "00:00:30",
            multiplier: 2.5,
            maxDelay: "00:05:00",
            maxRetriesPerWorkItem: 6,
            maxElapsedTime: "00:45:00",
            jitterMode: "Decorrelated");

        Assert.True(opts.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(10), opts.PeriodicCheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), opts.BaseDelay);
        Assert.Equal(2.5, opts.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(5), opts.MaxDelay);
        Assert.Equal(6, opts.MaxAutoRetriesPerWorkItem);
        Assert.Equal(TimeSpan.FromMinutes(45), opts.MaxElapsedTime);
        Assert.Equal(TransientRetryJitterMode.Decorrelated, opts.JitterMode);
    }

    [Theory]
    [InlineData("bad", "00:00:01", 2, "00:00:02", 1, "00:01:00", "Full", "PeriodicCheckInterval")]
    [InlineData("00:00:01", "00:00:00", 2, "00:00:02", 1, "00:01:00", "Full", "BaseDelay")]
    [InlineData("00:00:01", "00:00:01", 0.5, "00:00:02", 1, "00:01:00", "Full", "Multiplier")]
    [InlineData("00:00:01", "00:00:05", 2, "00:00:02", 1, "00:01:00", "Full", "MaxDelay")]
    [InlineData("00:00:01", "00:00:01", 2, "00:00:02", -1, "00:01:00", "Full", "MaxAutoRetriesPerWorkItem")]
    [InlineData("00:00:01", "00:00:01", 2, "00:00:02", 1, "00:00:00", "Full", "MaxElapsedTime")]
    [InlineData("00:00:01", "00:00:01", 2, "00:00:02", 1, "00:01:00", "bad", "JitterMode")]
    public void BuildTransientRetryOptions_Enabled_RejectsInvalidValues(
        string periodic,
        string baseDelay,
        double multiplier,
        string maxDelay,
        int maxRetries,
        string maxElapsed,
        string jitterMode,
        string expectedMessage)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrchestratorOptionsFactory.BuildTransientRetryOptions(
                enabled: true,
                periodicCheckInterval: periodic,
                baseDelay: baseDelay,
                multiplier: multiplier,
                maxDelay: maxDelay,
                maxRetriesPerWorkItem: maxRetries,
                maxElapsedTime: maxElapsed,
                jitterMode: jitterMode));

        Assert.Contains(expectedMessage, ex.Message);
    }
}
