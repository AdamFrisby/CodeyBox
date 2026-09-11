using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the audit-phase budget ordering check: auditor idle timeout &lt;
/// per-iteration audit timeout &lt; item-stale timeout &lt; sandbox wall
/// clock. An inverted configuration must fail at configuration load with the
/// offending config paths named, instead of killing healthy long runs at
/// audit time.
/// </summary>
public sealed class AuditBudgetOrderingTests
{
    [Fact]
    public void ShippedDefaults_SatisfyOrdering()
    {
        // Ties the real defaults together: 5 min idle (plus no test-pass
        // override) < 120 min per-iteration < 150 min item-stale < 6 h wall
        // clock. If any default drifts, startup validation must catch it here
        // first.
        var tuning = new PipelineTuningOptions();
        var watchdog = new WorkerProgressWatchdogOptions();
        var idle = AuditBudgetOrdering.EffectiveIdleTimeout(
            tuning.AuditorIdleTimeout, tuning.CSharpTestPassAuditorIdleTimeout);

        AuditBudgetOrdering.Validate(
            idle,
            TimeSpan.FromMinutes(ProjectAuditConfig.DefaultPerIterationTimeoutMinutes),
            watchdog.ItemStaleTimeout,
            SandboxResourceLimits.Default.WallClock);
    }

    [Fact]
    public void IdleAtOrAbovePerIteration_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditBudgetOrdering.Validate(
                TimeSpan.FromMinutes(120),
                TimeSpan.FromMinutes(120),
                TimeSpan.FromMinutes(150),
                TimeSpan.FromHours(6)));

        Assert.Contains("CodeyBox:PipelineTuning:AuditorIdleTimeout", ex.Message);
        Assert.Contains("CodeyBox:Defaults:Audit:PerIterationTimeoutMinutes", ex.Message);
    }

    [Fact]
    public void PerIterationAtOrAboveItemStale_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditBudgetOrdering.Validate(
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(150),
                TimeSpan.FromMinutes(150),
                TimeSpan.FromHours(6)));

        Assert.Contains("CodeyBox:Defaults:Audit:PerIterationTimeoutMinutes", ex.Message);
        Assert.Contains("CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout", ex.Message);
    }

    [Fact]
    public void ItemStaleAtOrAboveWallClock_Rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditBudgetOrdering.Validate(
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(120),
                TimeSpan.FromHours(6),
                TimeSpan.FromHours(6)));

        Assert.Contains("CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout", ex.Message);
        Assert.Contains("Sandbox:Limits:WallClock", ex.Message);
    }

    [Fact]
    public void DisabledLegs_Skipped()
    {
        AuditBudgetOrdering.Validate(TimeSpan.Zero, TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(150), TimeSpan.FromHours(6));
        AuditBudgetOrdering.Validate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(120), TimeSpan.Zero, TimeSpan.FromHours(6));
        AuditBudgetOrdering.Validate(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(150), wallClock: null);
    }

    [Fact]
    public void EffectiveIdleTimeout_PicksLargestWindow()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(20),
            AuditBudgetOrdering.EffectiveIdleTimeout(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            AuditBudgetOrdering.EffectiveIdleTimeout(TimeSpan.FromMinutes(5), null));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            AuditBudgetOrdering.EffectiveIdleTimeout(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void FormatBudgetedAuditorLabel_NamesBudgetAndValue()
    {
        var label = AuditBudgetOrdering.FormatBudgetedAuditorLabel(
            "csharp:test-pass", "codex",
            "CodeyBox:PipelineTuning:AuditorIdleTimeout",
            TimeSpan.FromMinutes(5));

        Assert.Contains("csharp:test-pass", label);
        Assert.Contains("codex", label);
        Assert.Contains("CodeyBox:PipelineTuning:AuditorIdleTimeout", label);
        Assert.Contains(TimeSpan.FromMinutes(5).ToString(), label);
    }

    [Fact]
    public void StartupValidator_AcceptsShippedDefaults()
    {
        AuditBudgetOrderingStartupValidator.Validate(new CodeyBoxOptions(), new ProjectsOptions());
    }

    [Fact]
    public void StartupValidator_RejectsIdleAbovePerIteration()
    {
        var options = new CodeyBoxOptions();
        options.PipelineTuning.AuditorIdleTimeout = TimeSpan.FromHours(3);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditBudgetOrderingStartupValidator.Validate(options, new ProjectsOptions()));

        Assert.Contains("CodeyBox:PipelineTuning:AuditorIdleTimeout", ex.Message);
        Assert.Contains("CodeyBox:Defaults:Audit:PerIterationTimeoutMinutes", ex.Message);
    }

    [Fact]
    public void StartupValidator_RejectsPerProjectPerIterationAboveItemStale()
    {
        var options = new CodeyBoxOptions();
        var projects = new ProjectsOptions
        {
            Projects =
            [
                new ProjectConfig
                {
                    Id = "demo",
                    Audit = new ProjectAuditConfig { PerIterationTimeoutMinutes = 600 },
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditBudgetOrderingStartupValidator.Validate(options, projects));

        Assert.Contains("CodeyBox:Projects:0:Audit:PerIterationTimeoutMinutes", ex.Message);
        Assert.Contains("CodeyBox:WorkerProgressWatchdog:ItemStaleTimeout", ex.Message);
    }

    [Fact]
    public void StartupValidator_UsesWidestIdleWindow()
    {
        // The test-pass override is the effective idle for ordering: a 3 h
        // test window against the 120 min iteration budget must fail even
        // though the generic 5 min idle fits.
        var options = new CodeyBoxOptions();
        options.PipelineTuning.CSharpTestPassAuditorIdleTimeout = TimeSpan.FromHours(3);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AuditBudgetOrderingStartupValidator.Validate(options, new ProjectsOptions()));

        Assert.Contains("CodeyBox:PipelineTuning:AuditorIdleTimeout", ex.Message);
    }
}
