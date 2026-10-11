using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the throw paths in <see cref="PipelineTuningOptions.Validate"/> for the
/// test-pass-specific knobs (<c>CSharpTestPassAuditorIdleTimeout</c> /
/// <c>CSharpTestPassBlameHangTimeout</c>). <see cref="PipelineTuningSnapshot"/>'s
/// constructor calls <c>Validate()</c>, so a misconfiguration must throw rather
/// than silently ship a bad snapshot. The two knobs deliberately use different
/// predicates (idle: '&lt; Zero'; blame-hang: '&lt;= Zero'), so the boundary at
/// zero is asserted explicitly for each.
/// </summary>
public sealed class PipelineTuningOptionsValidationTests
{
    [Fact]
    public void Validate_NegativeCSharpTestPassAuditorIdleTimeout_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            CSharpTestPassAuditorIdleTimeout = TimeSpan.FromSeconds(-1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.CSharpTestPassAuditorIdleTimeout), ex.ParamName);
    }

    [Fact]
    public void Validate_ZeroCSharpTestPassAuditorIdleTimeout_Allowed()
    {
        // '< Zero' guard: zero is a legitimate value (disables the per-test guard).
        var opts = new PipelineTuningOptions
        {
            CSharpTestPassAuditorIdleTimeout = TimeSpan.Zero,
        };

        opts.Validate();
    }

    [Fact]
    public void Validate_ZeroCSharpTestPassBlameHangTimeout_Throws()
    {
        // '<= Zero' guard: unlike the idle knob, zero is rejected for blame-hang.
        var opts = new PipelineTuningOptions
        {
            CSharpTestPassBlameHangTimeout = TimeSpan.Zero,
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.CSharpTestPassBlameHangTimeout), ex.ParamName);
    }

    [Fact]
    public void Validate_NegativeCSharpTestPassBlameHangTimeout_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            CSharpTestPassBlameHangTimeout = TimeSpan.FromSeconds(-1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.CSharpTestPassBlameHangTimeout), ex.ParamName);
    }

    [Fact]
    public void Snapshot_Constructor_RejectsBadCSharpTestPassKnob()
    {
        // PipelineTuningSnapshot runs Validate() in its ctor, so a bad knob must
        // abort snapshot construction rather than ship silently.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineTuningSnapshot(
            new PipelineTuningOptions
            {
                CSharpTestPassBlameHangTimeout = TimeSpan.Zero,
            }));
    }

    [Fact]
    public void Defaults_AreValid()
    {
        // The knobs are null by default (feature off); an untouched instance must
        // pass so operators that never set them don't get a startup exception.
        var opts = new PipelineTuningOptions();
        opts.Validate();
        Assert.Null(opts.CSharpTestPassAuditorIdleTimeout);
        Assert.Null(opts.CSharpTestPassBlameHangTimeout);
    }

    [Fact]
    public void Validate_NegativeAuditorAbsoluteTimeout_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            AuditorAbsoluteTimeout = TimeSpan.FromSeconds(-1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.AuditorAbsoluteTimeout), ex.ParamName);
    }

    [Fact]
    public void Defaults_AbsoluteTimeoutFitsInsideIterationBudget()
    {
        // The absolute bound must sit above the idle window it backstops and
        // below the per-iteration budget so one auditor cannot outlive its
        // iteration (see AuditBudgetOrdering).
        var opts = new PipelineTuningOptions();
        opts.Validate();

        Assert.True(opts.AuditorAbsoluteTimeout > opts.AuditorIdleTimeout);
        Assert.True(
            opts.AuditorAbsoluteTimeout
                < TimeSpan.FromMinutes(ProjectAuditConfig.DefaultPerIterationTimeoutMinutes));
    }

    [Fact]
    public void Validate_ZeroMaxPlanReviewIterations_ThrowsSharedLimitError()
    {
        var opts = new PipelineTuningOptions { MaxPlanReviewIterations = 0 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);

        Assert.Equal("value", ex.ParamName);
        Assert.Contains("MaxPlanReviewIterations must be >= 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_RebalanceEnabled_WithArchitectureAdvisory()
    {
        var opts = new PipelineTuningOptions();
        opts.Validate();

        Assert.True(opts.PlannedItemAuditRebalanceEnabled);
        Assert.Equal(
            [PipelineTuningOptions.DefaultPlannedItemAdvisoryAuditor],
            opts.PlannedItemAdvisoryAuditors);
    }

    [Fact]
    public void Validate_NullAdvisoryList_Throws()
    {
        var opts = new PipelineTuningOptions { PlannedItemAdvisoryAuditors = null! };

        var ex = Assert.Throws<ArgumentNullException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.PlannedItemAdvisoryAuditors), ex.ParamName);
    }

    [Fact]
    public void Validate_BlankAdvisoryEntry_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            PlannedItemAdvisoryAuditors = new List<string> { "architecture:llm-review", "  " },
        };

        var ex = Assert.Throws<ArgumentException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.PlannedItemAdvisoryAuditors), ex.ParamName);
    }

    [Fact]
    public void Validate_EmptyAdvisoryList_Allowed()
    {
        // An empty list disables demotion without disabling the flag.
        var opts = new PipelineTuningOptions
        {
            PlannedItemAdvisoryAuditors = new List<string>(),
        };

        opts.Validate();
    }

    [Fact]
    public void EffectiveMergeBudget_DefaultsToRequiredPlusAllowance()
    {
        // The incident: an operator raised the required-build budget to 45
        // minutes but the 15-minute merge-result default silently capped every
        // merge verification. The effective budget must never sit below the
        // required-build budget plus the provisioning allowance.
        var opts = new PipelineTuningOptions();
        opts.Validate();

        var effective = opts.GetEffectiveMergeResultBuildVerificationTimeout(TimeSpan.FromMinutes(45));

        Assert.True(
            effective >= TimeSpan.FromMinutes(45),
            $"effective merge budget {effective} must cover the 45-minute required-build budget");
        Assert.Equal(
            TimeSpan.FromMinutes(45) + opts.MergeResultBuildVerificationProvisioningAllowance,
            effective);
    }

    [Fact]
    public void EffectiveMergeBudget_Unset_NeverWarns()
    {
        var opts = new PipelineTuningOptions { MergeResultBuildVerificationTimeout = null };
        opts.Validate();

        Assert.False(
            opts.TryGetMergeResultBuildBudgetWarning(TimeSpan.FromMinutes(45), out _),
            "unset means automatic (exactly the floor), so there is nothing to warn about");
    }

    [Fact]
    public void EffectiveMergeBudget_ExplicitShortValue_ClampsUpWithWarning()
    {
        var opts = new PipelineTuningOptions
        {
            MergeResultBuildVerificationTimeout = TimeSpan.FromMinutes(5),
        };
        opts.Validate();

        var required = TimeSpan.FromMinutes(45);
        var effective = opts.GetEffectiveMergeResultBuildVerificationTimeout(required);

        Assert.Equal(required + opts.MergeResultBuildVerificationProvisioningAllowance, effective);
        Assert.True(
            opts.TryGetMergeResultBuildBudgetWarning(required, out var warning),
            "a configured merge budget below the required-build floor must produce a warning, never a silent cap");
        Assert.Contains("MergeResultBuildVerificationTimeout", warning, StringComparison.Ordinal);
        Assert.Contains("RequiredBuildVerificationTimeout", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveMergeBudget_GenerousExplicitValue_NoWarning()
    {
        var opts = new PipelineTuningOptions
        {
            MergeResultBuildVerificationTimeout = TimeSpan.FromHours(2),
        };
        opts.Validate();

        var required = TimeSpan.FromMinutes(45);

        Assert.Equal(TimeSpan.FromHours(2), opts.GetEffectiveMergeResultBuildVerificationTimeout(required));
        Assert.False(opts.TryGetMergeResultBuildBudgetWarning(required, out _));
    }

    [Fact]
    public void Validate_NegativeMergeProvisioningAllowance_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            MergeResultBuildVerificationProvisioningAllowance = TimeSpan.FromSeconds(-1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(
            nameof(PipelineTuningOptions.MergeResultBuildVerificationProvisioningAllowance),
            ex.ParamName);
    }

    [Fact]
    public void Validate_ZeroMergeInfraMaxAttempts_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            MergeResultBuildVerificationInfraMaxAttempts = 0,
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(
            nameof(PipelineTuningOptions.MergeResultBuildVerificationInfraMaxAttempts),
            ex.ParamName);
    }

    [Fact]
    public void Validate_NegativeSandboxPermitWaitWarningThreshold_Throws()
    {
        var opts = new PipelineTuningOptions
        {
            SandboxPermitWaitWarningThreshold = TimeSpan.FromSeconds(-1),
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(opts.Validate);
        Assert.Equal(nameof(PipelineTuningOptions.SandboxPermitWaitWarningThreshold), ex.ParamName);
    }

    [Fact]
    public void Validate_ZeroSandboxPermitWaitWarningThreshold_Allowed()
    {
        // Zero disables the slow-permit warning.
        var opts = new PipelineTuningOptions
        {
            SandboxPermitWaitWarningThreshold = TimeSpan.Zero,
        };

        opts.Validate();
    }
}
