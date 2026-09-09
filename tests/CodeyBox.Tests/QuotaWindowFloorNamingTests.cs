using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// The configured per-window floors must apply regardless of which naming convention the provider
/// uses. A floor that reads as configured but never matches is worse than no floor: it gates nothing
/// while appearing to.
/// </summary>
public sealed class QuotaWindowFloorNamingTests
{
    private const double GlobalFloor = 42.0;

    private static QuotaRouterOptions Options() => new()
    {
        MinQuotaPct = GlobalFloor,
        // The shipped config keys.
        MinQuotaPctByWindow = new Dictionary<string, double>
        {
            ["five_hour"] = 10,
            ["seven_day"] = 5,
        },
    };

    [Theory]
    // Provider-native names codex actually emits — these previously fell through to the global floor.
    [InlineData("weekly", 5)]
    [InlineData("5h-rolling", 10)]
    // Canonical names (antigravity normalises to these).
    [InlineData("seven_day", 5)]
    [InlineData("five_hour", 10)]
    // Casing and spacing must not matter.
    [InlineData("WEEKLY", 5)]
    [InlineData(" 5H-Rolling ", 10)]
    // Other spellings of the same windows.
    [InlineData("7d", 5)]
    [InlineData("5h", 10)]
    public void ConfiguredFloorApplies_WhicheverNamingTheProviderUses(string windowName, double expected)
    {
        var floor = QuotaGatePolicy.ResolveWindowFloorPct(Options(), AgentKind.Codex, windowName);

        Assert.Equal(expected, floor);
    }

    [Fact]
    public void UnrecognisedWindow_FallsBackToTheGlobalFloor()
    {
        // Don't guess: a window we cannot identify gets the global floor, not a neighbouring one.
        Assert.Equal(
            GlobalFloor,
            QuotaGatePolicy.ResolveWindowFloorPct(Options(), AgentKind.Codex, "monthly-burst"));
    }

    [Fact]
    public void ExactKeyWins_SoAnOperatorCanPinAProviderNativeName()
    {
        var options = new QuotaRouterOptions
        {
            MinQuotaPct = GlobalFloor,
            MinQuotaPctByWindow = new Dictionary<string, double>
            {
                // Both present: the verbatim provider name must take precedence over the canonical one.
                ["weekly"] = 25,
                ["seven_day"] = 5,
            },
        };

        Assert.Equal(25, QuotaGatePolicy.ResolveWindowFloorPct(options, AgentKind.Codex, "weekly"));
    }

    [Fact]
    public void CanonicalWindowName_RecognisesBothConventions_AndRejectsUnknowns()
    {
        Assert.Equal("seven_day", QuotaGatePolicy.CanonicalWindowName("weekly"));
        Assert.Equal("five_hour", QuotaGatePolicy.CanonicalWindowName("5h-rolling"));
        Assert.Null(QuotaGatePolicy.CanonicalWindowName("monthly"));
        Assert.Null(QuotaGatePolicy.CanonicalWindowName(null));
    }
}
