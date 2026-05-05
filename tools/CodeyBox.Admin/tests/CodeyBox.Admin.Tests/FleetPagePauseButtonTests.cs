using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using FleetPage = CodeyBox.Admin.Web.Components.Pages.Fleet;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Tests for the per-project action buttons on the Fleet page. Until the
/// budget-alerts work item lands, per-project pause falls back to the global
/// pause queue; the fallback banner is always shown.
/// </summary>
public sealed class FleetPagePauseButtonTests : TestContext
{
    [Fact]
    public void Fleet_PauseButton_Present_WhenProjectNotPaused()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto { ProjectId = "proj-a", DisplayName = "A", IsPaused = false },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<FleetPage>();

        Assert.Contains("Pause queue (global)", cut.Markup);
    }

    [Fact]
    public void Fleet_PauseButton_CallsApiWithProjectIdAndReason()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto { ProjectId = "my-project", DisplayName = "My Project", IsPaused = false },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<FleetPage>();
        cut.Find(".btn-fleet-pause").Click();

        Assert.Equal("my-project", fake.FleetSummaryPauseProjectIdCaptured);
        Assert.NotNull(fake.FleetSummaryPauseReasonCaptured);
    }

    [Fact]
    public void Fleet_ResumeButton_Present_WhenProjectPaused()
    {
        var fake = new FakeApiClient([]);
        fake.FleetSummaryOverride =
        [
            new FleetSummaryDto { ProjectId = "proj-b", DisplayName = "B", IsPaused = true },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<FleetPage>();

        Assert.Contains("Resume queue (global)", cut.Markup);
        Assert.DoesNotContain("Pause queue (global)", cut.Markup);
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

        Assert.Contains("Queue", cut.Markup);
        Assert.Contains("global queue pause", cut.Markup);
    }
}
