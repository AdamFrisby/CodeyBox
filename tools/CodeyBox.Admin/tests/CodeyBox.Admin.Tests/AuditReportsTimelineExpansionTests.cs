using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using WorkItemTimelinePage = CodeyBox.Admin.Web.Components.Pages.WorkItemTimeline;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Verifies the timeline page's per-auditor findings expansion: each auditor_run entry
/// shows the findings loaded from the audit-reports API, and the raw-output button appears.
/// </summary>
public sealed class AuditReportsTimelineExpansionTests : BunitContext
{
    private static TimelineEntryDto MakeAuditorEntry(string name, int iteration, string summary = "auditor ran") =>
        new()
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Kind = "auditor_run",
            Summary = summary,
            Details = JsonSerializer.SerializeToElement(new { name, iteration }),
        };

    private static WorkItemTimelineDto TerminalTimeline(string id, params TimelineEntryDto[] entries)
    {
        var list = entries.ToList();
        list.Add(new TimelineEntryDto
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Kind = "state_transition",
            Summary = "→ Done",
            Details = JsonSerializer.SerializeToElement(new { from = "Working", to = "Done" }),
        });
        return new WorkItemTimelineDto { WorkItemId = id, Entries = list };
    }

    private static AuditReportsDto MakeAuditReports(
        string id, int iteration, string auditorName, params AuditReportFindingDto[] findings)
    {
        var auditor = new AuditReportAuditorDto
        {
            Name = auditorName,
            Kind = "diff-pattern",
            WorstSeverity = findings.Length > 0 ? findings[0].Severity : "none",
            DurationMs = 100,
            Findings = [.. findings],
            RawOutputAvailable = false,
        };
        var iter = new AuditReportIterationDto
        {
            Iteration = iteration,
            BlockingCount = findings.Count(f => string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            NonBlockingCount = findings.Count(f => !string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            Auditors = [auditor],
        };
        return new AuditReportsDto { WorkItemId = id, Iterations = [iter] };
    }

    private static AuditReportFindingDto MakeFinding(string id, string title, string severity = "Error",
        string message = "Details here") =>
        new()
        {
            Id = id,
            Title = title,
            Severity = severity,
            Message = message,
            Files = [],
            LineHints = [],
        };

    [Fact]
    public void Timeline_AuditorEntry_ShowsFindingTitle_WhenPresent()
    {
        var id = Guid.NewGuid().ToString();
        var finding = MakeFinding("f-aabb", "Missing null check");
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = TerminalTimeline(id, MakeAuditorEntry("Lint", 1));
        fake.AuditReportsOverride = MakeAuditReports(id, 1, "Lint", finding);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Missing null check", cut.Markup);
    }

    [Fact]
    public void Timeline_AuditorEntry_ShowsNoFindingsMessage_WhenEmpty()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = TerminalTimeline(id, MakeAuditorEntry("Lint", 1));
        fake.AuditReportsOverride = MakeAuditReports(id, 1, "Lint"); // no findings
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("No findings", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeline_AuditorEntry_ShowsRawButton()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = TerminalTimeline(id, MakeAuditorEntry("Lint", 1));
        fake.AuditReportsOverride = MakeAuditReports(id, 1, "Lint");
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("audit-raw-btn", cut.Markup);
    }

    [Fact]
    public void Timeline_AuditorEntry_ShowsFindingCount_InSummary()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = TerminalTimeline(id, MakeAuditorEntry("Lint", 1));
        fake.AuditReportsOverride = MakeAuditReports(id, 1, "Lint",
            MakeFinding("f-1", "Issue A"),
            MakeFinding("f-2", "Issue B", "Warning"));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("(2)", cut.Markup);
    }

    [Fact]
    public void Timeline_AuditorEntry_FallsBackGracefully_WhenAuditReportsUnavailable()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.TimelineOverride = TerminalTimeline(id, MakeAuditorEntry("Lint", 1));
        // AuditReportsOverride is null — simulates API returning null
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<WorkItemTimelinePage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Findings not available", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
