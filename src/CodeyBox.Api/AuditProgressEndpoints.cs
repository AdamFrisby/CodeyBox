using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// Read API over per-iteration audit progress reports (<c>work_item_audit_progress</c>).
/// Two endpoints:
/// <list type="bullet">
///   <item><c>GET /workitems/{id}/audit-progress</c> — every progress row for the work item.
///   Finding descriptions are truncated to a configured cap so the common case returns in one
///   fetch; rows with a truncated description are flagged.</item>
///   <item><c>GET /workitems/{id}/audit-progress/{progressId}</c> — one row by its surrogate
///   id, with full (untruncated) descriptions.</item>
/// </list>
/// </summary>
internal static class AuditProgressEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems/{id}/audit-progress");
        group.MapGet("/", GetAllAsync);
        group.MapGet("/{progressId}", GetByIdAsync);
    }

    /// <summary>
    /// GET /workitems/{id}/audit-progress — all progress rows, finding descriptions truncated
    /// to <see cref="AuditProgressApiOptions.ListFindingDescriptionMaxChars"/>.
    /// </summary>
    private static async Task<IResult> GetAllAsync(
        string id,
        IWorkItemStore store,
        IAuditProgressStore progressStore,
        IOptionsMonitor<AuditProgressApiOptions> options,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(guid);
        if (await store.GetAsync(workItemId, ct) is null)
            return Results.NotFound();

        var configured = options.CurrentValue.ListFindingDescriptionMaxChars;
        int? cap = configured > 0 ? configured : null;

        var rows = await progressStore.GetAllAuditProgressForWorkItemAsync(workItemId, ct);
        var dtos = rows.Select(r => AuditProgressDtoMapper.ToDto(r, cap)).ToList();
        return Results.Ok(new AuditProgressListResponse(id, dtos));
    }

    /// <summary>
    /// GET /workitems/{id}/audit-progress/{progressId} — one row by surrogate id, full detail.
    /// The row is only returned when it belongs to the work item (verified in the store query).
    /// </summary>
    private static async Task<IResult> GetByIdAsync(
        string id,
        string progressId,
        IWorkItemStore store,
        IAuditProgressStore progressStore,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(guid);
        if (await store.GetAsync(workItemId, ct) is null)
            return Results.NotFound();

        var row = await progressStore.GetAuditProgressByIdAsync(workItemId, progressId, ct);
        if (row is null)
            return Results.NotFound();

        // Detail view: never truncate — return the full result.
        return Results.Ok(AuditProgressDtoMapper.ToDto(row, maxDescriptionChars: null));
    }
}

// ── Mapping (pure, testable) ──────────────────────────────────────────────────

internal static class AuditProgressDtoMapper
{
    /// <summary>
    /// Projects a stored progress row to its API shape. When
    /// <paramref name="maxDescriptionChars"/> is a positive value, each finding's description is
    /// truncated to it and flagged; null (or a non-positive value) returns full descriptions.
    /// </summary>
    internal static AuditProgressDto ToDto(StoredAuditProgress row, int? maxDescriptionChars)
    {
        var p = row.Progress;
        var findings = MapFindings(p.Findings, maxDescriptionChars);
        var blocking = MapFindings(p.BlockingFindingsDetails, maxDescriptionChars);
        var truncated = findings.Any(f => f.DescriptionTruncated)
            || blocking.Any(f => f.DescriptionTruncated);

        return new AuditProgressDto(
            Id: row.Id,
            WorkItemId: row.WorkItemId.ToString(),
            WorkAttemptKey: row.WorkAttemptKey,
            Iteration: p.Iteration,
            MaxIterations: p.MaxIterations,
            Status: p.Status,
            BlockingFindings: p.BlockingFindings,
            NonBlockingFindings: p.NonBlockingFindings,
            RecordedAt: row.RecordedAt,
            WorkBranchTip: p.WorkBranchTip,
            ScheduledAuditors: p.ScheduledAuditors ?? [],
            CompletedAuditors: p.CompletedAuditors ?? [],
            BlockingFindingIds: p.BlockingFindingIds,
            BlockingFindingsDetails: blocking,
            Findings: findings,
            Truncated: truncated);
    }

    private static List<AuditProgressFindingDto> MapFindings(
        IReadOnlyList<AuditProgressFinding> findings,
        int? maxDescriptionChars)
        => findings.Select(f =>
        {
            var full = f.Description ?? string.Empty;
            var truncate = maxDescriptionChars is int cap && cap > 0 && full.Length > cap;
            return new AuditProgressFindingDto(
                AuditorName: f.AuditorName,
                Severity: f.Severity.ToString(),
                Title: f.Title,
                Description: truncate ? SafeTruncate(full, maxDescriptionChars!.Value) : full,
                DescriptionLength: full.Length,
                DescriptionTruncated: truncate,
                Location: f.Location);
        }).ToList();

    /// <summary>
    /// Truncates <paramref name="s"/> to at most <paramref name="max"/> UTF-16 code units without
    /// splitting a surrogate pair at the boundary (which would leave an invalid lone surrogate).
    /// </summary>
    internal static string SafeTruncate(string s, int max)
    {
        if (max <= 0) return string.Empty;
        if (s.Length <= max) return s;
        var end = max;
        if (char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end];
    }
}

// ── Response DTOs ─────────────────────────────────────────────────────────────

public sealed record AuditProgressListResponse(
    string WorkItemId,
    IReadOnlyList<AuditProgressDto> Progress);

public sealed record AuditProgressDto(
    string Id,
    string WorkItemId,
    string WorkAttemptKey,
    int Iteration,
    int MaxIterations,
    string Status,
    int BlockingFindings,
    int NonBlockingFindings,
    DateTimeOffset RecordedAt,
    string? WorkBranchTip,
    IReadOnlyList<string> ScheduledAuditors,
    IReadOnlyList<string> CompletedAuditors,
    IReadOnlyList<string> BlockingFindingIds,
    IReadOnlyList<AuditProgressFindingDto> BlockingFindingsDetails,
    IReadOnlyList<AuditProgressFindingDto> Findings,
    bool Truncated);

public sealed record AuditProgressFindingDto(
    string AuditorName,
    string Severity,
    string Title,
    string Description,
    int DescriptionLength,
    bool DescriptionTruncated,
    string? Location);
