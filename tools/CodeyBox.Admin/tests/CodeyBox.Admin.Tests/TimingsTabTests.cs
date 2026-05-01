using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemTimingsPage = CodeyBox.Admin.Web.Components.Pages.WorkItemTimings;

namespace CodeyBox.Admin.Tests;

public sealed class TimingsTabTests : TestContext
{
    private const string ItemId = "aabbccdd-0000-0000-0000-000000000001";

    private static WorkItemTimingsDto MakeTimings(
        long total = 12_000,
        string[]? topStepNames = null)
    {
        var topSteps = (topStepNames ?? ["agent.exec", "git.clone_into_sandbox"])
            .Select((s, i) => new TopStepDto { Step = s, TotalMs = total / (i + 1), Count = 1 })
            .ToList();

        var phaseElement = JsonSerializer.SerializeToElement(new
        {
            durationMs = total,
            steps = new[]
            {
                new { step = "agent.exec", durationMs = 10_000L },
                new { step = "git.clone_into_sandbox", durationMs = 2_000L },
            },
        });

        return new WorkItemTimingsDto
        {
            WorkItemId = ItemId,
            TotalDurationMs = total,
            TopSteps = topSteps,
            ByPhase = new Dictionary<string, JsonElement> { ["work"] = phaseElement },
        };
    }

    [Fact]
    public void WorkItemTimings_ShowsTotalDuration()
    {
        var fake = new FakeApiClient([]);
        fake.TimingsOverride[ItemId] = MakeTimings(12_000);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("12.0s", cut.Markup);
    }

    [Fact]
    public void WorkItemTimings_ShowsTopStepsTable()
    {
        var fake = new FakeApiClient([]);
        fake.TimingsOverride[ItemId] = MakeTimings(12_000, ["agent.exec"]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("agent.exec", cut.Markup);
        Assert.Contains("timings-top", cut.Markup);
    }

    [Fact]
    public void WorkItemTimings_ShowsPhaseSection()
    {
        var fake = new FakeApiClient([]);
        fake.TimingsOverride[ItemId] = MakeTimings();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("timings-phase", cut.Markup);
        Assert.Contains("work", cut.Markup);
    }

    [Fact]
    public void WorkItemTimings_NotFound_ShowsErrorBanner()
    {
        var fake = new FakeApiClient([]);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkItemTimings_ShowsLinkBackToWorkItem()
    {
        var fake = new FakeApiClient([]);
        fake.TimingsOverride[ItemId] = MakeTimings();
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<WorkItemTimingsPage>(p => p.Add(x => x.Id, ItemId));

        Assert.Contains($"/work-items/{ItemId}", cut.Markup);
    }
}
