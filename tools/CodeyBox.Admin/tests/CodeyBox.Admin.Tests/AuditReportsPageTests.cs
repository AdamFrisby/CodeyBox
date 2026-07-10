using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using AuditReportsPage = CodeyBox.Admin.Web.Components.Pages.AuditReports;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Verifies the AuditReports page renders the findings-across-iterations matrix
/// and per-auditor finding details correctly.
/// </summary>
public sealed class AuditReportsPageTests : TestContext
{
    private static AuditReportsDto MakeReports(string id, params AuditReportIterationDto[] iterations) =>
        new() { WorkItemId = id, Iterations = [.. iterations] };

    private static AuditReportIterationDto MakeIteration(int number, params AuditReportAuditorDto[] auditors)
    {
        var allFindings = auditors.SelectMany(a => a.Findings).ToList();
        var blocking = allFindings.Count(f => string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        return new AuditReportIterationDto
        {
            Iteration = number,
            BlockingCount = blocking,
            NonBlockingCount = allFindings.Count - blocking,
            Auditors = [.. auditors],
        };
    }

    private static AuditReportAuditorDto MakeAuditor(string name, bool rawAvailable = false, params AuditReportFindingDto[] findings) =>
        new()
        {
            Name = name,
            Kind = "diff-pattern",
            WorstSeverity = findings.Length > 0 ? findings[0].Severity : "none",
            DurationMs = 100,
            Findings = [.. findings],
            RawOutputAvailable = rawAvailable,
        };

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

    // ── Matrix tests ────────────────────────────────────────────────────────────

    [Fact]
    public void AuditReports_MatrixNotShown_WhenNoIterations()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("No audit reports", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audit-matrix", cut.Markup);
    }

    [Fact]
    public void AuditReports_MatrixShown_WhenFindingsExist()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint",
                findings: MakeFinding("f-aa", "Missing null check"))));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("audit-matrix", cut.Markup);
        Assert.Contains("Findings across iterations", cut.Markup);
    }

    [Fact]
    public void AuditReports_Matrix_PresentCellRendered()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint",
                findings: MakeFinding("f-aa", "Missing null check"))));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        // The finding is present in iteration 1 — matrix should show ✓
        Assert.Contains("✓", cut.Markup);
    }

    [Fact]
    public void AuditReports_Matrix_AbsentCellRendered()
    {
        var id = Guid.NewGuid().ToString();
        var finding = MakeFinding("f-aa", "Missing null check");

        var fake = new FakeApiClient([]);
        // Finding only in iteration 1, not in iteration 2
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", findings: finding)),
            MakeIteration(2, MakeAuditor("Lint"))); // no findings
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        // Should have ✓ for iter1 and · for iter2
        Assert.Contains("✓", cut.Markup);
        Assert.Contains("·", cut.Markup);
    }

    [Fact]
    public void AuditReports_Matrix_ShowsIterationColumns()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", findings: MakeFinding("f-aa", "Issue"))),
            MakeIteration(2, MakeAuditor("Lint", findings: MakeFinding("f-aa", "Issue"))));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("audit-matrix-table", cut.Markup);
        // Both iteration columns should appear in the table header
        Assert.Contains("<th title=\"code iteration 1\">code:1</th>", cut.Markup);
        Assert.Contains("<th title=\"code iteration 2\">code:2</th>", cut.Markup);
    }

    // ── Iteration/auditor expansion tests ──────────────────────────────────────

    [Fact]
    public void AuditReports_ShowsIterationWithPassBadge_WhenNoFindings()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint")));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("count-pass", cut.Markup);
        Assert.Contains("✓ pass", cut.Markup);
    }

    [Fact]
    public void AuditReports_ShowsBlockingCount_WhenErrorFindings()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", false,
                MakeFinding("f-aa", "Error one"),
                MakeFinding("f-bb", "Error two"))));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("count-blocking", cut.Markup);
        Assert.Contains("2 blocking", cut.Markup);
    }

    [Fact]
    public void AuditReports_ShowsFindingTitle_InAuditorSection()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint",
                findings: MakeFinding("f-aa", "Null pointer dereference", message: "The variable is never checked."))));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Null pointer dereference", cut.Markup);
        Assert.Contains("The variable is never checked.", cut.Markup);
    }

    [Fact]
    public void AuditReports_ShowsRawButton_WhenRawOutputAvailable()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", rawAvailable: true)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("audit-raw-btn", cut.Markup);
        Assert.Contains("raw", cut.Markup);
    }

    [Fact]
    public void AuditReports_DoesNotShowRawButton_WhenRawOutputNotAvailable()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", rawAvailable: false)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.DoesNotContain("audit-raw-btn", cut.Markup);
    }

    [Fact]
    public void AuditReports_NotFound_ShowsErrorBanner()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        // AuditReportsOverride is null → GetAuditReportsAsync returns null → _notFound = true
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("not found", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditReports_ShowsNavigationLinks()
    {
        var id = Guid.NewGuid().ToString();
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id);
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = RenderComponent<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains($"/work-items/{id}", cut.Markup);
        Assert.Contains($"/work-items/{id}/timeline", cut.Markup);
    }
}
