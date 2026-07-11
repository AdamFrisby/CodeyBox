using CodeyBox.Orchestrator;

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
}
