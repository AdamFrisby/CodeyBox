using Bunit;
using Microsoft.Extensions.DependencyInjection;
using CodeyBox.Admin.Web.Models;
using CodeyBox.Admin.Web.Services;
using AuditReportsPage = CodeyBox.Admin.Web.Components.Pages.AuditReports;

namespace CodeyBox.Admin.Tests;

/// <summary>
/// Verifies the "Findings across iterations" matrix table in the AuditReports page.
/// Key scenario: finding f-007 persists across iterations 3–7 then resolves in 8.
/// </summary>
public sealed class FindingsAcrossIterationsTableTests : BunitContext
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

    private static AuditReportAuditorDto MakeAuditor(string name, params AuditReportFindingDto[] findings) =>
        new()
        {
            Name = name,
            Kind = "diff-pattern",
            WorstSeverity = findings.Length > 0 ? findings[0].Severity : "none",
            DurationMs = 100,
            Findings = [.. findings],
            RawOutputAvailable = false,
        };

    private static AuditReportFindingDto MakeFinding(string id, string title, string severity = "Error") =>
        new()
        {
            Id = id,
            Title = title,
            Severity = severity,
            Message = "Details here",
            Files = [],
            LineHints = [],
        };

    [Fact]
    public void Matrix_F007_PersistingIter3To7_ResolvingIn8_RendersCorrectCells()
    {
        var id = Guid.NewGuid().ToString();
        var finding = MakeFinding("f-007", "Hard-coded secret");

        // f-007 present in iterations 3–7, absent in 8
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(3, MakeAuditor("Lint", finding)),
            MakeIteration(4, MakeAuditor("Lint", finding)),
            MakeIteration(5, MakeAuditor("Lint", finding)),
            MakeIteration(6, MakeAuditor("Lint", finding)),
            MakeIteration(7, MakeAuditor("Lint", finding)),
            MakeIteration(8, MakeAuditor("Lint"))); // resolved — no findings
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<AuditReportsPage>(p => p.Add(x => x.Id, id));

        // Matrix should be shown and include finding title
        Assert.Contains("audit-matrix", cut.Markup);
        Assert.Contains("Hard-coded secret", cut.Markup);

        // Present cells (iters 3–7) and absent cell (iter 8) both rendered
        Assert.Contains("✓", cut.Markup);
        Assert.Contains("·", cut.Markup);

        // All iteration columns appear in the table header
        for (var i = 3; i <= 8; i++)
            Assert.Contains($"<th title=\"code iteration {i}\">code:{i}</th>", cut.Markup);
    }

    [Fact]
    public void Matrix_FindingPresentInAllIterations_NoAbsentCells()
    {
        var id = Guid.NewGuid().ToString();
        var finding = MakeFinding("f-007", "Persistent issue");
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", finding)),
            MakeIteration(2, MakeAuditor("Lint", finding)),
            MakeIteration(3, MakeAuditor("Lint", finding)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<AuditReportsPage>(p => p.Add(x => x.Id, id));

        // Only present cells (✓) — no absent CSS class in matrix cells
        Assert.Contains("✓", cut.Markup);
        Assert.DoesNotContain("audit-matrix-cell--absent", cut.Markup);
    }

    [Fact]
    public void Matrix_FindingResolvesInNextIteration_ShowsAbsentCell()
    {
        var id = Guid.NewGuid().ToString();
        var finding = MakeFinding("f-007", "Resolved issue");
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", finding)),
            MakeIteration(2, MakeAuditor("Lint"))); // resolved
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("✓", cut.Markup);
        Assert.Contains("·", cut.Markup);
    }

    [Fact]
    public void Matrix_FindingTitle_IsRenderedInRow()
    {
        var id = Guid.NewGuid().ToString();
        var finding = MakeFinding("f-007", "Hard-coded secret in TestSupport.cs");
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(3, MakeAuditor("Lint", finding)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Hard-coded secret in TestSupport.cs", cut.Markup);
    }

    [Fact]
    public void Matrix_MultipleDistinctFindings_EachGetsOwnRow()
    {
        var id = Guid.NewGuid().ToString();
        var finding1 = MakeFinding("f-001", "Issue Alpha");
        var finding2 = MakeFinding("f-002", "Issue Beta", "Warning");
        var fake = new FakeApiClient([]);
        fake.AuditReportsOverride = MakeReports(id,
            MakeIteration(1, MakeAuditor("Lint", finding1, finding2)));
        Services.AddSingleton<ICodeyBoxApiClient>(fake);

        var cut = Render<AuditReportsPage>(p => p.Add(x => x.Id, id));

        Assert.Contains("Issue Alpha", cut.Markup);
        Assert.Contains("Issue Beta", cut.Markup);
    }
}
