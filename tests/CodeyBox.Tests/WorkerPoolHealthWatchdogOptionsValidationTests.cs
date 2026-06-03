using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkerPoolHealthWatchdogOptionsValidationTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        new WorkerPoolHealthWatchdogOptions().Validate();
    }

    [Fact]
    public void StallTimeout_Zero_DisablesWatchdog()
    {
        var opts = new WorkerPoolHealthWatchdogOptions
        {
            StallTimeout = TimeSpan.Zero,
        };

        opts.Validate();
    }

    [Fact]
    public void StallTimeout_LessThanCheckInterval_Throws()
    {
        var opts = new WorkerPoolHealthWatchdogOptions
        {
            StallTimeout = TimeSpan.FromSeconds(30),
            CheckInterval = TimeSpan.FromSeconds(60),
        };

        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("StallTimeout", ex.Message);
        Assert.Contains("CheckInterval", ex.Message);
    }

    [Theory]
    [InlineData("stall")]
    [InlineData("check")]
    [InlineData("attempts")]
    [InlineData("batch")]
    [InlineData("verify")]
    public void InvalidValues_Throw(string scenario)
    {
        var opts = new WorkerPoolHealthWatchdogOptions();
        switch (scenario)
        {
            case "stall":
                opts.StallTimeout = TimeSpan.FromSeconds(-1);
                break;
            case "check":
                opts.CheckInterval = TimeSpan.Zero;
                break;
            case "attempts":
                opts.MaxRecoveryAttempts = -1;
                break;
            case "batch":
                opts.MaxRecoveryEnqueueBatchSize = 0;
                break;
            case "verify":
                opts.RecoveryVerificationDelay = TimeSpan.FromSeconds(-1);
                break;
        }

        Assert.Throws<InvalidOperationException>(opts.Validate);
    }
}
