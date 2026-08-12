using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemTimelinePage = CodeyBox.Admin.Web.Components.Pages.WorkItemTimeline;

namespace CodeyBox.Admin.Tests;

public sealed class TimelineFilterTests : BunitContext
{
    private static TimelineEntryDto MakeEntry(string kind, string summary, object? details = null) => new()
    {
        OccurredAt = DateTimeOffset.UtcNow,
        Kind = kind,
        Summary = summary,
        Details = details is not null
            ? JsonSerializer.SerializeToElement(details)
            : default,
    };

    [Fact]
    public void Filter_KindQueryParam_ShowsOnlyMatchingKind()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = id,
            Entries = [
                MakeEntry("state_transition", "→ Done", new { from = "Working", to = "Done" }),
                MakeEntry("agent_started", "claude started", new { agent = "claude", phase = "work" }),
            ],
        };
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/work-items/{id}/timeline?kind=state_transition");

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("→ Done", cut.Markup);
        Assert.DoesNotContain("claude started", cut.Markup);
    }

    [Fact]
    public void Filter_NoQueryParam_ShowsAllEntries()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = id,
            Entries = [
                MakeEntry("state_transition", "→ Done", new { from = "Working", to = "Done" }),
                MakeEntry("agent_started", "claude started", new { agent = "claude", phase = "work" }),
            ],
        };
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("→ Done", cut.Markup);
        Assert.Contains("claude started", cut.Markup);
    }

    [Fact]
    public void Filter_ClickingChip_FiltersEntries()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = id,
            Entries = [
                MakeEntry("state_transition", "→ Done", new { from = "Working", to = "Done" }),
                MakeEntry("agent_started", "claude started", new { agent = "claude", phase = "work" }),
            ],
        };
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        // Both entries visible initially.
        Assert.Contains("→ Done", cut.Markup);
        Assert.Contains("claude started", cut.Markup);

        // Click the "State" filter chip (first chip, activates state_transition kind).
        cut.Find("button.filter-chip").Click();

        Assert.Contains("→ Done", cut.Markup);
        Assert.DoesNotContain("claude started", cut.Markup);
    }

    [Fact]
    public void Filter_ClearButton_ResetsFilter()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = id,
            Entries = [
                MakeEntry("state_transition", "→ Done", new { from = "Working", to = "Done" }),
                MakeEntry("agent_started", "claude started", new { agent = "claude", phase = "work" }),
            ],
        };
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        // Start with a kind filter active via URL.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/work-items/{id}/timeline?kind=state_transition");

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.DoesNotContain("claude started", cut.Markup);

        // Click the clear button.
        cut.Find("button.filter-chip--clear").Click();

        // Both entries visible again.
        Assert.Contains("→ Done", cut.Markup);
        Assert.Contains("claude started", cut.Markup);
    }

    [Fact]
    public void Filter_IterationQueryParam_SetsIterationFilter()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = id,
            Entries = [
                MakeEntry("state_transition", "→ Done", new { from = "Working", to = "Done" }),
                MakeEntry("auditor_run", "lint (iter 1) — 0 findings", new { name = "lint", iteration = 1, severity = "None" }),
                MakeEntry("auditor_run", "fmt (iter 2) — 0 findings", new { name = "fmt", iteration = 2, severity = "None" }),
            ],
        };
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/work-items/{id}/timeline?iteration=1");

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("iter 1", cut.Markup);
        Assert.DoesNotContain("iter 2", cut.Markup);
    }
}
