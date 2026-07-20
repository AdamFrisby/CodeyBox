using CodeyBox.Core;
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
        var opts = new WorkerProgressWatchdogOptions();
        opts.Validate();
        Assert.Equal(TimeSpan.FromMinutes(60), opts.ProgressTimeout);
        Assert.True(opts.ProcessCpuProgressSignalEnabled);
        Assert.True(opts.ActiveSandboxProgressSignalEnabled);
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

    [Fact]
    public void ItemStaleTimeout_Negative_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleTimeout = TimeSpan.FromSeconds(-1),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ItemStaleTimeout", ex.Message);
    }

    [Fact]
    public void ItemStaleTimeout_Zero_Allowed_DisablesDetector()
    {
        // Zero is the documented "disable item-stale path" sentinel — keeps
        // the worker-progress watchdog while turning off the item-stale sweep.
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleTimeout = TimeSpan.Zero,
        };
        opts.Validate();
    }

    [Fact]
    public void ItemStaleCheckInterval_Zero_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleCheckInterval = TimeSpan.Zero,
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ItemStaleCheckInterval", ex.Message);
    }

    [Fact]
    public void ItemStaleTimeout_LessThanItemStaleCheckInterval_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleTimeout = TimeSpan.FromMinutes(1),
            ItemStaleCheckInterval = TimeSpan.FromMinutes(5),
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ItemStaleTimeout", ex.Message);
        Assert.Contains("ItemStaleCheckInterval", ex.Message);
    }

    [Fact]
    public void ItemStaleMaxRecoveryAttempts_Negative_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleMaxRecoveryAttempts = -1,
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("ItemStaleMaxRecoveryAttempts", ex.Message);
    }

    [Fact]
    public void Defaults_IncludeItemStaleFields()
    {
        // Spec: detection threshold and recovery cap are config-driven; defaults
        // are comfortably above a normal phase duration but tighter than the
        // ~90-minute production incident window.
        var opts = new WorkerProgressWatchdogOptions();
        Assert.Equal(TimeSpan.FromMinutes(75), opts.ItemStaleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), opts.ItemStaleCheckInterval);
        Assert.Equal(3, opts.ItemStaleMaxRecoveryAttempts);
        opts.Validate();
    }

    // --- Per-agent overrides --------------------------------------------

    [Fact]
    public void PerAgent_EmptyMap_ValidatesAndResolvesGlobalDefaults()
    {
        var opts = new WorkerProgressWatchdogOptions();
        opts.Validate();
        // No override for crock → resolves to global default.
        Assert.Equal(opts.ProgressTimeout, opts.ResolveProgressTimeout(AgentKind.Crock));
        Assert.Equal(opts.ItemStaleTimeout, opts.ResolveItemStaleTimeout(AgentKind.Crock));
        // Null agent → resolves to global default.
        Assert.Equal(opts.ProgressTimeout, opts.ResolveProgressTimeout(agent: null));
        Assert.Equal(opts.ItemStaleTimeout, opts.ResolveItemStaleTimeout(agent: null));
    }

    [Fact]
    public void PerAgent_CrockOverride_TakesPrecedenceForMatchingItems()
    {
        // Spec: a batch-latency agent (crock — minutes-to-hours per task)
        // must be able to opt out of the 60-min default without bumping the
        // global value and losing protection for synchronous agents.
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(60),
            ItemStaleTimeout = TimeSpan.FromMinutes(75),
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(6),
                    ItemStaleTimeout = TimeSpan.FromHours(8),
                },
            },
        };
        opts.Validate();

        Assert.Equal(TimeSpan.FromHours(6), opts.ResolveProgressTimeout(AgentKind.Crock));
        Assert.Equal(TimeSpan.FromHours(8), opts.ResolveItemStaleTimeout(AgentKind.Crock));
        // Synchronous agents keep the tight global default.
        Assert.Equal(TimeSpan.FromMinutes(60), opts.ResolveProgressTimeout(AgentKind.Claude));
        Assert.Equal(TimeSpan.FromMinutes(75), opts.ResolveItemStaleTimeout(AgentKind.Claude));
    }

    [Fact]
    public void PerAgent_KeyComparisonIsCaseInsensitive()
    {
        // Operators sometimes write keys in different casings; the lookup
        // must be tolerant so a "Crock" config entry still matches.
        var opts = new WorkerProgressWatchdogOptions
        {
            PerAgent =
            {
                ["CROCK"] = new AgentWatchdogOverride { ProgressTimeout = TimeSpan.FromHours(3) },
            },
        };
        opts.Validate();
        Assert.Equal(TimeSpan.FromHours(3), opts.ResolveProgressTimeout(AgentKind.Crock));
    }

    [Fact]
    public void PerAgent_PartialOverride_OnlyAppliesSetFields()
    {
        // Setting only ProgressTimeout leaves ItemStaleTimeout falling back
        // to the global default.
        var opts = new WorkerProgressWatchdogOptions
        {
            ItemStaleTimeout = TimeSpan.FromMinutes(75),
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(6),
                    // ItemStaleTimeout unset.
                },
            },
        };
        opts.Validate();
        Assert.Equal(TimeSpan.FromHours(6), opts.ResolveProgressTimeout(AgentKind.Crock));
        Assert.Equal(TimeSpan.FromMinutes(75), opts.ResolveItemStaleTimeout(AgentKind.Crock));
    }

    [Fact]
    public void PerAgent_ZeroOverride_DisablesForThatKind()
    {
        // Zero is a documented "disable" sentinel — operators can opt an
        // agent out of either watchdog without affecting the global default.
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(60),
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride { ProgressTimeout = TimeSpan.Zero },
            },
        };
        opts.Validate();
        Assert.Equal(TimeSpan.Zero, opts.ResolveProgressTimeout(AgentKind.Crock));
    }

    [Fact]
    public void PerAgent_NegativeProgressTimeout_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromSeconds(-1),
                },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("crock", ex.Message);
        Assert.Contains("ProgressTimeout", ex.Message);
    }

    [Fact]
    public void PerAgent_ProgressTimeoutBelowCheckInterval_Throws()
    {
        // The constraint that ProgressTimeout >= CheckInterval applies to
        // per-agent overrides too — a 5s override against a 60s sweep would
        // pretend to trip on every sweep.
        var opts = new WorkerProgressWatchdogOptions
        {
            CheckInterval = TimeSpan.FromSeconds(60),
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromSeconds(5),
                },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("crock", ex.Message);
        Assert.Contains("CheckInterval", ex.Message);
    }

    [Fact]
    public void PerAgent_EmptyKey_Throws()
    {
        var opts = new WorkerProgressWatchdogOptions
        {
            PerAgent =
            {
                [" "] = new AgentWatchdogOverride { ProgressTimeout = TimeSpan.FromHours(1) },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(opts.Validate);
        Assert.Contains("PerAgent", ex.Message);
    }
}
