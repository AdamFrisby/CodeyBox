using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Components.Shared;
using CodeyBox.Admin.Web.Services;
using StatusChipComponent = CodeyBox.Admin.Web.Components.Shared.StatusChip;
using CopyableIdComponent = CodeyBox.Admin.Web.Components.Shared.CopyableId;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Design-system contract tests: the shared status vocabulary, the single
/// formatting helper, copyable identifiers, and the ban on inline state
/// formatting in pages.
/// </summary>
public sealed class StatusVocabularyTests
{
    [Fact]
    public void AllWorkItemStates_CoveredByVocabulary()
    {
        Assert.Equal(25, StatusVocabulary.AllWorkItemStates.Length);
        foreach (var state in StatusVocabulary.AllWorkItemStates)
        {
            var info = StatusVocabulary.ForWorkItem(state);
            Assert.False(string.IsNullOrWhiteSpace(info.Text));
            Assert.False(string.IsNullOrWhiteSpace(info.Glyph));
            Assert.False(string.IsNullOrWhiteSpace(info.Tone));
            Assert.False(string.IsNullOrWhiteSpace(info.Title));
            Assert.NotEqual("?", info.Glyph);
        }
    }

    [Fact]
    public void WorkItemStates_EveryStateHasDistinctGlyph()
    {
        // Colour is never the only carrier: even two states sharing a tone
        // must differ by glyph.
        var glyphs = StatusVocabulary.AllWorkItemStates
            .Select(s => StatusVocabulary.ForWorkItem(s).Glyph)
            .ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
    }

    [Fact]
    public void WorkItemStates_SharingTone_StillDifferByGlyph()
    {
        var groups = StatusVocabulary.AllWorkItemStates
            .Select(s => StatusVocabulary.ForWorkItem(s))
            .GroupBy(i => i.Tone)
            .Where(g => g.Count() > 1);
        Assert.NotEmpty(groups);
        foreach (var group in groups)
        {
            Assert.Equal(group.Count(), group.Select(i => i.Glyph).Distinct().Count());
        }
    }

    [Fact]
    public void ForWorkItem_IsCaseInsensitive()
    {
        Assert.Equal("Done", StatusVocabulary.ForWorkItem("done").Text);
        Assert.Equal("Done", StatusVocabulary.ForWorkItem("DONE").Text);
    }

    [Fact]
    public void ForWorkItem_UnknownState_PreservesText()
    {
        var info = StatusVocabulary.ForWorkItem("SomethingNew");
        Assert.Equal("SomethingNew", info.Text);
        Assert.Equal("muted", info.Tone);
    }

    [Fact]
    public void ForQuotaBand_NullPct_IsUnknown()
    {
        var info = StatusVocabulary.ForQuotaBand(null);
        Assert.Equal("?", info.Glyph);
        Assert.False(string.IsNullOrWhiteSpace(info.Text));
    }

    [Theory]
    [InlineData(0, "⏳")]
    [InlineData(10, "⚠")]
    [InlineData(30, "◐")]
    [InlineData(80, "●")]
    [InlineData(100, "●")]
    public void ForQuotaBand_MapsPctToDistinctGlyph(int pct, string glyph)
    {
        var info = StatusVocabulary.ForQuotaBand(pct);
        Assert.Equal(glyph, info.Glyph);
        Assert.False(string.IsNullOrWhiteSpace(info.Text));
    }

    [Fact]
    public void QuotaBands_HaveDistinctGlyphs()
    {
        var glyphs = new int?[] { null, 0, 10, 30, 80 }
            .Select(p => StatusVocabulary.ForQuotaBand(p).Glyph);
        Assert.Equal(5, glyphs.Distinct().Count());
    }

    [Theory]
    [InlineData("Available", "✓")]
    [InlineData("Paused", "⏸")]
    [InlineData("QuotaExhausted", "⏳")]
    [InlineData("Backoff", "⇄")]
    [InlineData("Unknown", "?")]
    [InlineData("bogus", "?")]
    public void ForAvailability_MapsToGlyphAndText(string input, string glyph)
    {
        var info = StatusVocabulary.ForAvailability(input);
        Assert.Equal(glyph, info.Glyph);
        Assert.False(string.IsNullOrWhiteSpace(info.Text));
    }

    [Theory]
    [InlineData("closed", "✓")]
    [InlineData("half-open", "◐")]
    [InlineData("open", "⚠")]
    [InlineData(null, "?")]
    public void ForBreaker_MapsToDistinctGlyphs(string? input, string glyph)
    {
        Assert.Equal(glyph, StatusVocabulary.ForBreaker(input).Glyph);
    }

    [Theory]
    [InlineData("Open", "Open")]
    [InlineData("InReview", "In review")]
    [InlineData("Released", "Released")]
    [InlineData("Closed", "Closed")]
    [InlineData("Failed", "Failed")]
    public void ForRelease_CoversReleaseStates(string input, string text)
    {
        Assert.Equal(text, StatusVocabulary.ForRelease(input).Text);
    }

    [Theory]
    [InlineData("open", "Open")]
    [InlineData("dismissed", "Dismissed")]
    [InlineData("promoted", "Promoted")]
    public void ForSuggestion_CoversSuggestionStates(string input, string text)
    {
        Assert.Equal(text, StatusVocabulary.ForSuggestion(input).Text);
    }

    [Fact]
    public void ForSeverity_BlockingVsMinor_DifferInGlyphAndTone()
    {
        var blocking = StatusVocabulary.ForSeverity("important");
        var minor = StatusVocabulary.ForSeverity("minor");
        Assert.NotEqual(blocking.Glyph, minor.Glyph);
        Assert.NotEqual(blocking.Tone, minor.Tone);
    }

    [Fact]
    public void ForProject_PausedHasGlyphAndText()
    {
        var info = StatusVocabulary.ForProject(isPaused: true, hasRecentFailures: false, inFlight: 0, queued: 0);
        Assert.Equal("Paused", info.Text);
        Assert.Equal("⏸", info.Glyph);
    }

    [Fact]
    public void BudgetHelpers_AgreeWithChips()
    {
        // Single source of truth: bar CSS classes come from the vocabulary.
        Assert.Equal("budget-full", StatusVocabulary.BudgetBarCss(100));
        Assert.Equal("budget-warn", StatusVocabulary.QuotaBarCss(20));
        Assert.Equal("", StatusVocabulary.QuotaBarCss(90));
    }
}

public sealed class StatusChipComponentTests : BunitContext
{
    [Fact]
    public void StatusChip_RendersGlyphTextAndTone()
    {
        var cut = Render<StatusChipComponent>(p =>
            p.Add(x => x.Info, StatusVocabulary.ForWorkItem("Working")));

        Assert.Contains("status-chip", cut.Markup);
        Assert.Contains("chip--active", cut.Markup);
        Assert.Contains("▶", cut.Markup);
        Assert.Contains("Working", cut.Markup);
    }

    [Fact]
    public void StatusChip_FailedState_RendersFailToneWithGlyph()
    {
        var cut = Render<StatusChipComponent>(p =>
            p.Add(x => x.Info, StatusVocabulary.ForWorkItem("Failed")));

        Assert.Contains("chip--fail", cut.Markup);
        Assert.Contains("✗", cut.Markup);
        Assert.Contains("Failed", cut.Markup);
    }
}

public sealed class CopyableIdComponentTests : BunitContext
{
    [Fact]
    public void CopyableId_ShowsPrefixAndCopyButtonWithFullValue()
    {
        var cut = Render<CopyableIdComponent>(p =>
            p.Add(x => x.Value, "aabbccdd-0000-1111"));

        Assert.Contains("aabbccdd", cut.Markup);
        Assert.Contains("data-copy=\"aabbccdd-0000-1111\"", cut.Markup);
        Assert.Contains("ident-copy", cut.Markup);
    }

    [Fact]
    public void CopyableId_ShowFull_RendersEntireValue()
    {
        var cut = Render<CopyableIdComponent>(p => p
            .Add(x => x.Value, "main")
            .Add(x => x.ShowFull, true));

        Assert.Contains(">main<", cut.Markup);
    }

    [Fact]
    public void CopyableId_CopyButtonOnly_RendersNoVisibleText()
    {
        var cut = Render<CopyableIdComponent>(p => p
            .Add(x => x.Value, "aabbccdd-0000-1111")
            .Add(x => x.CopyButtonOnly, true));

        Assert.DoesNotContain("ident-code", cut.Markup);
        Assert.Contains("data-copy=\"aabbccdd-0000-1111\"", cut.Markup);
    }
}

public sealed class AdminFormatTests
{
    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(850, "850ms")]
    [InlineData(12_000, "12.0s")]
    [InlineData(184_000, "3m 4s")]
    [InlineData(7_200_000, "2h 0m")]
    [InlineData(-5, "—")]
    public void FormatDurationMs_FormatsBuckets(long ms, string expected)
    {
        Assert.Equal(expected, AdminFormat.FormatDurationMs(ms));
    }

    [Fact]
    public void FormatDuration_TimeSpan_DelegatesToMs()
    {
        Assert.Equal("12.0s", AdminFormat.FormatDuration(TimeSpan.FromSeconds(12)));
        Assert.Equal("—", AdminFormat.FormatDuration(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(12_300, "12.3K")]
    [InlineData(2_500_000, "2.5M")]
    [InlineData(-1, "—")]
    public void FormatCount_Compacts(long count, string expected)
    {
        Assert.Equal(expected, AdminFormat.FormatCount(count));
    }

    [Theory]
    [InlineData(18.42, "$18.42")]
    [InlineData(0, "$0.00")]
    [InlineData(0.004, "$0.0040")]
    public void FormatUsd_FormatsMoney(double usd, string expected)
    {
        Assert.Equal(expected, AdminFormat.FormatUsd(usd));
    }

    [Fact]
    public void FormatRelative_ShowsAgo()
    {
        var now = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", AdminFormat.FormatRelative(now.AddSeconds(-3), now));
        Assert.Equal("4m ago", AdminFormat.FormatRelative(now.AddMinutes(-4), now));
        Assert.Equal("3h ago", AdminFormat.FormatRelative(now.AddHours(-3), now));
        Assert.Equal("just now", AdminFormat.FormatRelative(now.AddMinutes(5), now));
    }

    [Fact]
    public void FormatCountdown_CountsDown()
    {
        var now = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("—", AdminFormat.FormatCountdown(null, now));
        Assert.Equal("now", AdminFormat.FormatCountdown(now.AddMinutes(-1), now));
        Assert.Equal("5m", AdminFormat.FormatCountdown(now.AddMinutes(5), now));
        Assert.Equal("2h 5m", AdminFormat.FormatCountdown(now.AddHours(2).AddMinutes(5), now));
    }

    [Fact]
    public void FormatDateTime_NullRendersDash()
    {
        Assert.Equal("—", AdminFormat.FormatDateTime(null));
        Assert.Equal("—", AdminFormat.FormatDateTimeFull(null));
        Assert.Contains("2026", AdminFormat.FormatDateTimeFull(
            new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero)));
    }
}

public sealed class NoInlineStateFormattingTests
{
    private static readonly string[] BannedSnippets =
    [
        "state-@",
        "severity-badge--@",
        "fleet-dot-@",
        "fleet-outcome-@",
    ];

    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "CodeyBox.Admin",
                "src", "CodeyBox.Admin.Web", "Components", "Pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("Could not locate the Admin web Pages directory from the test run.");
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public void NoPage_FormatsStateInline()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(PagesDir(), "*.razor"))
        {
            var text = File.ReadAllText(file);
            if (BannedSnippets.Any(text.Contains))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0,
            $"Pages must render states through StatusChip, not inline CSS classes: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryWorkItemState_RendersThroughSharedChip()
    {
        // Guards the vocabulary against backend drift: the chip input type is
        // the full known state list, so a state missing here cannot render.
        foreach (var state in StatusVocabulary.AllWorkItemStates)
        {
            var info = StatusVocabulary.ForWorkItem(state);
            Assert.NotEqual("?", info.Glyph);
            Assert.NotEqual("Unrecognised work-item state", info.Title);
        }
    }
}
