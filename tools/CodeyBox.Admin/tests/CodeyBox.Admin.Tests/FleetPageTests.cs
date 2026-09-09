using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using FleetPage = CodeyBox.Admin.Web.Components.Pages.Fleet;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Renders the Fleet component with canned summary data and asserts the correct
/// rendering of rows, status dots, and recent-outcome glyphs.
/// </summary>
public sealed class FleetPageTests : BunitContext
{
    private static FleetSummaryDto MakeSummary(
        string projectId = "proj-alpha",
        string displayName = "Alpha",
        int queued = 0,
        int inFlight = 0,
        string? currentPhase = null,
        List<string>? outcomes = null,
        bool isPaused = false,
        double? spendUsd = null) => new()
        {
            ProjectId = projectId,
            DisplayName = displayName,
            QueuedCount = queued,
            InFlightCount = inFlight,
            CurrentPhase = currentPhase,
            RecentOutcomes = outcomes ?? [],
            IsPaused = isPaused,
            MonthlySpendUsd = spendUsd,
            BudgetThresholdState = spendUsd.HasValue ? "ok" : "unknown",
        };

    [Fact]
    public void Fleet_OneRowPerProject()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            MakeSummary("proj-a", "Alpha"),
            MakeSummary("proj-b", "Beta"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("Alpha", cut.Markup);
        Assert.Contains("Beta", cut.Markup);
    }

    [Fact]
    public void Fleet_StatusDot_GreyWhenIdle()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary(queued: 0, inFlight: 0)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("fleet-dot-grey", cut.Markup);
    }

    [Fact]
    public void Fleet_StatusDot_BlueWhenInFlight()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary(inFlight: 1)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("fleet-dot-blue", cut.Markup);
    }

    [Fact]
    public void Fleet_StatusDot_YellowWhenOnlyQueued()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary(queued: 2)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("fleet-dot-yellow", cut.Markup);
    }

    [Fact]
    public void Fleet_StatusDot_RedWhenPaused()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary(isPaused: true)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("fleet-dot-red", cut.Markup);
    }

    [Fact]
    public void Fleet_RecentOutcomes_CorrectGlyphs()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            MakeSummary(outcomes: ["Done", "Failed", "AuditFailed", "Cancelled", "Done"]),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        // Done → ✓, Failed/AuditFailed → ✗, Cancelled → !
        Assert.Contains("fleet-outcome-ok", cut.Markup);
        Assert.Contains("fleet-outcome-fail", cut.Markup);
        Assert.Contains("fleet-outcome-cancel", cut.Markup);
    }

    [Fact]
    public void Fleet_RecentOutcomes_InCorrectOrder()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            MakeSummary(outcomes: ["Failed", "Done", "Done"]),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        // ✗ must appear before ✓✓ in the rendered output (newest first).
        var failIdx = cut.Markup.IndexOf("fleet-outcome-fail", StringComparison.Ordinal);
        var okIdx = cut.Markup.IndexOf("fleet-outcome-ok", StringComparison.Ordinal);
        Assert.True(failIdx < okIdx, "Failed outcome (✗) should appear before Done (✓) outcomes");
    }

    [Fact]
    public void Fleet_CurrentPhase_ShownWhenInFlight()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            MakeSummary(inFlight: 1, currentPhase: "Auditing"),
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("Auditing", cut.Markup);
    }

    [Fact]
    public void Fleet_CurrentPhase_DashWhenIdle()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary()];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("—", cut.Markup);
    }

    [Fact]
    public void Fleet_BudgetBar_RenderedWhenSpendKnown()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary(spendUsd: 18.42)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("$18.42", cut.Markup);
        Assert.Contains("budget-bar", cut.Markup);
    }

    [Fact]
    public void Fleet_BudgetBar_DashWhenSpendUnknown()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary(spendUsd: null)];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.DoesNotContain("budget-bar-wrap", cut.Markup);
    }

    [Fact]
    public void Fleet_FallbackBanner_AlwaysShown()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary()];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("budget-alerts work item", cut.Markup);
    }

    [Fact]
    public void Fleet_RendersSlowestToolCallLeaderboard()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [MakeSummary()];
        fake.FleetAgentStreamAggregateOverride = new AgentStreamAggregateDto
        {
            SlowestToolCalls =
            [
                new AgentStreamSlowToolCallDto
                {
                    WorkItemId = "12345678-0000-0000-0000-000000000000",
                    Phase = "audit",
                    Iteration = 9,
                    ToolName = "Bash",
                    DurationMs = 184_000,
                    Succeeded = true,
                    InputSummary = "{\"command\":\"dotnet test\"}",
                },
            ],
        };
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("fleet-tool-leaderboard-table", cut.Markup);
        Assert.Contains("Bash", cut.Markup);
        Assert.Contains("3m 4s", cut.Markup);
        Assert.Contains("dotnet test", cut.Markup);
    }
}
