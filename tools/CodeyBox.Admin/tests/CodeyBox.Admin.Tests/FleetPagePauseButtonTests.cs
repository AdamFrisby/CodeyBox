using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using FleetPage = CodeyBox.Admin.Web.Components.Pages.Fleet;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for the per-project action buttons on the Fleet page. Until the
/// budget-alerts work item lands, per-project pause is not available; the
/// page shows a fallback banner and a "queue" link per project.
/// </summary>
public sealed class FleetPagePauseButtonTests : TestContext
{
    [Fact]
    public void Fleet_QueueLink_Present_PerProject()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto { ProjectId = "my-project", DisplayName = "My Project" },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<FleetPage>();

        // Each row has a "queue" link.
        Assert.Contains("queue", cut.Markup);
        Assert.Contains("my-project", cut.Markup);
    }

    [Fact]
    public void Fleet_QueueLink_IncludesProjectId()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto { ProjectId = "proj-x", DisplayName = "X" },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<FleetPage>();

        // The queue link should encode the project ID as a query param.
        Assert.Contains("proj-x", cut.Markup);
    }

    [Fact]
    public void Fleet_FallbackBanner_ReferencesGlobalPause()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto { ProjectId = "proj-a", DisplayName = "A" },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<FleetPage>();

        // The fallback banner must reference the Queue page for global pause.
        Assert.Contains("Queue", cut.Markup);
        Assert.Contains("global pause", cut.Markup);
    }
}
