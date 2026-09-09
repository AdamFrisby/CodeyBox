using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using FleetPage = CodeyBox.Admin.Web.Components.Pages.Fleet;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for the Fleet page empty state when no projects are configured.
/// </summary>
public sealed class FleetPageEmptyStateTests : BunitContext
{
    [Fact]
    public void Fleet_NoProjects_ShowsEmptyStateMessage()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("No projects configured", cut.Markup);
    }

    [Fact]
    public void Fleet_NoProjects_DoesNotRenderTable()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride = [];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.DoesNotContain("queue-table", cut.Markup);
    }

    [Fact]
    public void Fleet_SingleProjectNoItems_ShowsTableWithOneRow()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto
            {
                ProjectId = "solo",
                DisplayName = "Solo Project",
                QueuedCount = 0,
                InFlightCount = 0,
                RecentOutcomes = [],
            },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();

        Assert.Contains("queue-table", cut.Markup);
        Assert.Contains("Solo Project", cut.Markup);
    }
}
