using CodeyBox.Agents.Crush;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrushCostExtractor"/>: the unknown-not-zero
/// contract. Crush's headless transport (<c>crush run</c>) carries no
/// machine-readable usage frame — plain-text replies, empty replies, and
/// the exit-1 styled <c>ERROR</c> failure blocks alike — so the extractor
/// returns null (no cost row) rather than a zero snapshot that looks like
/// measured data. <c>DefaultPricing</c> stays null because no single
/// fallback rate is honest across the dozens of providers Crush fronts;
/// the shipped free-tier member bills $0 via the explicit zero-rate
/// pricing bucket.
/// </summary>
public sealed class CrushCostExtractorTests
{
    [Fact]
    public void Kind_IsCrush()
    {
        Assert.Equal(AgentKind.Crush, new CrushCostExtractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // No single fallback rate is honest for a multi-provider front; the
        // shipped free-tier member prices through the explicit zero-rate
        // bucket instead.
        Assert.Null(new CrushCostExtractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_PlainTextReply_ReturnsNull()
    {
        // Recorded real output (@charmland/crush 0.95.0): free text with no
        // usage signal must not fabricate a snapshot.
        Assert.Null(new CrushCostExtractor().TryExtract("hello-crush-ok\n", string.Empty));
    }

    [Fact]
    public void TryExtract_RepoEditSummary_ReturnsNull()
    {
        // Recorded real output (seeded-bug repo-edit run): the summary names
        // the changed file but carries no token counts.
        Assert.Null(new CrushCostExtractor().TryExtract(
            "Fixed. The `add` function in `/tmp/crushrepo/calc.py:1` now returns `a + b` instead of `a - b`. Verified: `calc.add(3, 5)` returns 8.\n",
            string.Empty));
    }

    [Fact]
    public void TryExtract_EmptyReply_ReturnsNull()
    {
        // A run whose work landed in files may reply with empty stdout.
        // Empty output is unknown cost, not zero.
        Assert.Null(new CrushCostExtractor().TryExtract(string.Empty, string.Empty));
    }

    [Fact]
    public void TryExtract_QuotaFailureBlock_ReturnsNull()
    {
        // Recorded real shape ($0-spend-limit key against a paid model):
        // the ERROR block names the quota failure but carries no token
        // counts — a failure must never record a zero that looks measured.
        const string stderr =
            "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).";
        Assert.Null(new CrushCostExtractor().TryExtract(string.Empty, stderr));
    }

    [Fact]
    public void TryExtract_NullStreams_ReturnsNull()
    {
        Assert.Null(new CrushCostExtractor().TryExtract(null, null));
    }
}
