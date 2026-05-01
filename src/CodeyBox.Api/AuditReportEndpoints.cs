using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class AuditReportEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems/{id}/audit-reports");
        group.MapGet("/", GetAuditReportsAsync);
        group.MapGet("/{iteration}/{auditor}/raw", GetRawOutputAsync);
    }

    /// <summary>
    /// GET /workitems/{id}/audit-reports
    /// Returns all stored auditor reports for a work item, grouped by iteration.
    /// findings are included inline; raw_output is omitted (fetch separately via /raw).
    /// </summary>
    private static async Task<IResult> GetAuditReportsAsync(
        string id,
        IWorkItemStore store,
        IAuditReportStore reportStore,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = "invalid id" });
        var item = await store.GetAsync(new WorkItemId(Guid.Parse(id)), ct);
        if (item is null) return Results.NotFound();

        var reports = await reportStore.GetByWorkItemAsync(id, ct);

        // Group by iteration and build response shape.
        var iterationGroups = reports
            .GroupBy(r => r.Iteration)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var auditors = g.OrderBy(r => r.AuditorName).Select(r => new AuditReportAuditorDto(
                    r.AuditorName,
                    r.AuditorKind,
                    r.WorstSeverity,
                    r.DurationMs,
                    r.Findings.Select(f => new AuditReportFindingDto(
                        f.Id, f.Severity, f.Title, f.Message, f.Files, f.LineHints)).ToList(),
                    RawOutputAvailable: r.RawOutput is not null)).ToList();

                var allFindings = auditors.SelectMany(a => a.Findings).ToList();
                var blockingCount = allFindings.Count(f =>
                    string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                var nonBlockingCount = allFindings.Count - blockingCount;

                return new AuditReportIterationDto(g.Key, blockingCount, nonBlockingCount, auditors);
            })
            .ToList();

        return Results.Ok(new AuditReportsResponse(id, iterationGroups));
    }

    /// <summary>
    /// GET /workitems/{id}/audit-reports/{iteration}/{auditor}/raw
    /// Returns the raw_output for a single auditor invocation as plain text.
    /// </summary>
    private static async Task<IResult> GetRawOutputAsync(
        string id,
        int iteration,
        string auditor,
        IWorkItemStore store,
        IAuditReportStore reportStore,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) return Results.BadRequest(new { error = "invalid id" });
        var item = await store.GetAsync(new WorkItemId(Guid.Parse(id)), ct);
        if (item is null) return Results.NotFound();

        var decodedAuditor = Uri.UnescapeDataString(auditor);
        var raw = await reportStore.GetRawOutputAsync(id, iteration, decodedAuditor, ct);
        if (raw is null) return Results.NotFound();

        return Results.Text(raw, contentType: "text/plain; charset=utf-8");
    }
}

// ── Response DTOs ─────────────────────────────────────────────────────────────

public sealed record AuditReportsResponse(
    string WorkItemId,
    IReadOnlyList<AuditReportIterationDto> Iterations);

public sealed record AuditReportIterationDto(
    int Iteration,
    int BlockingCount,
    int NonBlockingCount,
    IReadOnlyList<AuditReportAuditorDto> Auditors);

public sealed record AuditReportAuditorDto(
    string Name,
    string Kind,
    string WorstSeverity,
    long DurationMs,
    IReadOnlyList<AuditReportFindingDto> Findings,
    bool RawOutputAvailable);

public sealed record AuditReportFindingDto(
    string Id,
    string Severity,
    string Title,
    string Message,
    IReadOnlyList<string> Files,
    IReadOnlyList<int> LineHints);
