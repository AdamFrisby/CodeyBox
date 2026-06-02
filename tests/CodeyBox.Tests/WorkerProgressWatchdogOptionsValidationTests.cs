using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Covers every throw path in
/// <see cref="WorkerProgressWatchdogOptions.Validate"/>. The watchdog is wired
/// up via a DI factory that calls <c>Validate()</c> at startup, so a
/// misconfiguration must abort process start rather than surface as a runtime
/// crash inside a sweep.
/// </summary>
public sealed class WorkerProgressWatchdogOptionsValidationTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        // Sanity: an out-of-the-box options instance must pass Validate so
        // operators that never touch the config block don't get a startup
        // exception.
        new WorkerProgressWatchdogOptions().Validate();
    }

    [Fact]
    public void ProgressTimeout_Negative_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromSeconds(-1),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ProgressTimeout", ex.Message);
    }

    [Fact]
    public void ProgressTimeout_Zero_Allowed_DisablesWatchdog()
    {
        // Zero is the documented "disable" sentinel and must NOT throw —
        // operators turn the watchdog off by setting ProgressTimeout to 0.
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.Zero,
            CheckInterval = TimeSpan.FromSeconds(60),
        };
        opts.Validate();
    }

    [Fact]
    public void CheckInterval_Zero_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            CheckInterval = TimeSpan.Zero,
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("CheckInterval", ex.Message);
    }

    [Fact]
    public void CheckInterval_Negative_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            CheckInterval = TimeSpan.FromSeconds(-1),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("CheckInterval", ex.Message);
    }

    [Fact]
    public void PostAgentTransitionTimeout_Zero_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            PostAgentTransitionTimeout = TimeSpan.Zero,
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("PostAgentTransitionTimeout", ex.Message);
    }

    [Fact]
    public void PostAgentTransitionTimeout_Negative_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            PostAgentTransitionTimeout = TimeSpan.FromSeconds(-1),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("PostAgentTransitionTimeout", ex.Message);
    }

    [Fact]
    public void ProgressTimeout_LessThanCheckInterval_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromSeconds(30),
            CheckInterval = TimeSpan.FromSeconds(60),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ProgressTimeout", ex.Message);
        Assert.Contains("CheckInterval", ex.Message);
    }

    [Fact]
    public void ProgressTimeout_EqualToCheckInterval_Allowed()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromSeconds(60),
            CheckInterval = TimeSpan.FromSeconds(60),
        };
        opts.Validate();
    }

    [Fact]
    public void MaxRecoveryAttempts_Negative_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            MaxRecoveryAttempts = -1,
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("MaxRecoveryAttempts", ex.Message);
    }

    [Fact]
    public void MaxRecoveryAttempts_Zero_Allowed_MeansUnlimited()
    {
        // 0 = unlimited (matches DeadWorkerOptions / OrchestratorOptions semantics).
        var opts = new WorkerProgressWatchdogOptions
        {
            MaxRecoveryAttempts = 0,
        };
        opts.Validate();
    }
}
