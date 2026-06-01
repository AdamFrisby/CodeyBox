using CodeyBox.Orchestrator;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the small internal helpers that the in-iteration quota
/// fallback wrapper uses to defend against attacker-influenced agent output.
/// These functions are pure and easy to regress; cover the corner cases
/// explicitly so a future refactor doesn't quietly remove the safety net.
/// </summary>
public sealed class QuotaFallbackHelpersTests
{
    [Fact]
    public void ClampQuotaReset_PassesThroughLegitimateResetHints()
    {
        var now = DateTimeOffset.UtcNow;
        var inAnHour = now.AddHours(1);
        var clamped = PipelineRunner.ClampQuotaReset(inAnHour);
        Assert.NotNull(clamped);
        Assert.Equal(inAnHour, clamped!.Value);
    }

    [Fact]
    public void ClampQuotaReset_NullInput_ReturnsNull()
    {
        Assert.Null(PipelineRunner.ClampQuotaReset(null));
    }

    [Fact]
    public void ClampQuotaReset_FarFutureValue_ClampedToCeiling()
    {
        // Simulate a prompt-injected Retry-After year (e.g. 31_536_000 seconds).
        // Without the clamp this would park the work item for an actual year.
        var farFuture = DateTimeOffset.UtcNow.AddDays(365);
        var clamped = PipelineRunner.ClampQuotaReset(farFuture);
        Assert.NotNull(clamped);

        var diff = clamped!.Value - DateTimeOffset.UtcNow;
        Assert.True(diff <= PipelineRunner.MaxParsedQuotaResetWindow + TimeSpan.FromSeconds(5),
            $"expected clamp to fit within the ceiling, was {diff}");
    }

    [Fact]
    public void ClampQuotaReset_ExplicitMaxWindow_CapsToCustomCeiling()
    {
        // Prove the maxWindow parameter (as supplied by production code from
        // PipelineTuningOptions.MaxParsedQuotaResetWindow) is actually
        // honoured. A far-future reset with a narrow maxWindow must be clamped
        // to the custom ceiling, not the legacy static fallback.
        var farFuture = DateTimeOffset.UtcNow.AddDays(100);
        var customMax = TimeSpan.FromMinutes(5);
        var clamped = PipelineRunner.ClampQuotaReset(farFuture, maxWindow: customMax);
        Assert.NotNull(clamped);

        var diff = clamped!.Value - DateTimeOffset.UtcNow;
        Assert.True(diff <= customMax + TimeSpan.FromSeconds(2),
            $"expected clamp to fit within custom ceiling {customMax}, was {diff}");
    }

    [Theory]
    [InlineData("simple message", "simple message")]
    [InlineData("first line\r\nsecond line", "first line second line")]
    [InlineData("tabs\tand\nnewlines\rmixed", "tabs and newlines mixed")]
    [InlineData("  leading and trailing  ", "leading and trailing")]
    [InlineData("multiple    spaces", "multiple spaces")]
    public void SingleLineSummary_StripsControlChars_AndCollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, PipelineRunner.SingleLineSummary(input));
    }

    [Fact]
    public void SingleLineSummary_Null_ReturnsEmpty()
    {
        Assert.Equal("", PipelineRunner.SingleLineSummary(null));
    }

    [Fact]
    public void SingleLineSummary_LogSpoofingPayload_IsDefused()
    {
        // CWE-117 defence: a crafted agent stderr that tries to inject a fake
        // audit-log line must be flattened to a single line so plain-text log
        // sinks render it as one entry, not multiple lines pretending to be
        // separate events.
        var hostile = "rate_limit_exceeded\n2025-01-01 [Audit] WorkItemDeleted by attacker";
        var safe = PipelineRunner.SingleLineSummary(hostile);
        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain('\r', safe);
    }
}
