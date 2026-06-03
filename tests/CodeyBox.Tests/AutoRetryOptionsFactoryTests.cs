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
}
