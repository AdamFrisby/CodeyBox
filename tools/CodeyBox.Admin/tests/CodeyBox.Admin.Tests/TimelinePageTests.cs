using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemTimelinePage = CodeyBox.Admin.Web.Components.Pages.WorkItemTimeline;

namespace CodeyBox.Admin.Tests;

public sealed class TimelinePageTests : BunitContext
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

    // Adds a terminal state_transition so _isInFlight = false and no refresh loop starts.
    private static WorkItemTimelineDto TerminalTimeline(string id, params TimelineEntryDto[] entries)
    {
        var list = entries.ToList();
        list.Add(MakeEntry("state_transition", "→ Done", new { from = "Working", to = "Done" }));
        return new WorkItemTimelineDto { WorkItemId = id, Entries = list };
    }

    [Fact]
    public void Timeline_RendersEntries()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = TerminalTimeline(id,
            MakeEntry("state_transition", "Queued → Working", new { from = "Queued", to = "Working" }),
            MakeEntry("agent_started", "claude (work) started", new { agent = "claude", phase = "work" }));

        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Queued → Working", cut.Markup);
        Assert.Contains("claude (work) started", cut.Markup);
    }

    [Fact]
    public void Timeline_NotFound_ShowsErrorBanner()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        // TimelineOverride is null → GetWorkItemTimelineAsync returns null → _notFound = true
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeline_EmptyEntries_ShowsEmptyMessage()
    {
        var id = Guid.NewGuid().ToString();
        // One terminal state_transition so it's not in-flight, but filter to a kind with no matches.
        var timeline = TerminalTimeline(id);
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        // Navigate with ?kind=webhook_delivered so VisibleEntries is empty.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"http://localhost/work-items/{id}/timeline?kind=webhook_delivered");

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("No timeline entries", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeline_InFlight_ShowsLiveBadge()
    {
        var id = Guid.NewGuid().ToString();
        // No terminal state_transition → _isInFlight = true.
        var timeline = new WorkItemTimelineDto
        {
            WorkItemId = id,
            Entries = [MakeEntry("state_transition", "Queued → Working", new { from = "Queued", to = "Working" })],
        };
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Live", cut.Markup);
    }

    [Fact]
    public void Timeline_Terminal_DoesNotShowLiveBadge()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = TerminalTimeline(id);
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.DoesNotContain("Live", cut.Markup);
    }

    [Fact]
    public void Timeline_ShowsDetailLink_BackToWorkItem()
    {
        var id = Guid.NewGuid().ToString();
        var timeline = TerminalTimeline(id);
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = timeline;
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Detail", cut.Markup);
        Assert.Contains($"/work-items/{id}", cut.Markup);
    }
}
