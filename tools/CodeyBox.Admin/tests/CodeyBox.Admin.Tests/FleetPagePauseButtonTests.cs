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
public sealed class FleetPagePauseButtonTests : BunitContext
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

        var cut = Render<FleetPage>();

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

        var cut = Render<FleetPage>();
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

        var cut = Render<FleetPage>();

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

        var cut = Render<FleetPage>();

        Assert.Contains("Queue", cut.Markup);
        Assert.Contains("global queue pause", cut.Markup);
    }

    [Fact]
    public void Fleet_AgentPause_CallsApiWithKindReasonAndDuration()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();
        cut.FindAll("input")[0].Change("claude");
        cut.FindAll("input")[1].Change("reserve quota");
        cut.FindAll("input")[2].Change("6");
        cut.Find(".btn-agent-pause").Click();

        Assert.Equal("claude", fake.AgentPauseKindCaptured);
        Assert.Equal("reserve quota", fake.AgentPauseReasonCaptured);
        Assert.Equal(21600, fake.AgentPauseDurationCaptured);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("<code>claude</code>", cut.Markup);
            Assert.Contains("reserve quota", cut.Markup);
            Assert.DoesNotContain("No paused agents.", cut.Markup);
        });
    }

    [Fact]
    public void Fleet_PausedAgentResume_CallsApi()
    {
        var fake = new FakeApiClient([]);
        fake.PausedAgentsOverride =
        [
            new AgentPauseStateDto
            {
                Agent = "gemini",
                Paused = true,
                PausedReason = "outage",
                PausedAt = DateTimeOffset.UtcNow,
            },
        ];
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<FleetPage>();
        cut.Find(".btn-agent-resume").Click();

        Assert.Equal("gemini", fake.AgentResumeKindCaptured);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No paused agents.", cut.Markup);
            Assert.DoesNotContain("<code>gemini</code>", cut.Markup);
            Assert.DoesNotContain("outage", cut.Markup);
        });
    }
}
