using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

internal static class AuditRunEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/audit-runs");
        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapGet("/{id}/reports", GetReportsAsync);
        group.MapGet("/{id}/logs", GetLogsAsync);
        group.MapGet("/{id}/artifacts", ListArtifactsAsync);
        group.MapGet("/{id}/artifacts/{name}", GetArtifactAsync);
        group.MapPost("/{id}/cancel", CancelAsync);
    }

    private static async Task<IResult> CreateAsync(
        AuditRunCreateDto req,
        HttpContext context,
        StandaloneAuditService service,
        IOptionsMonitor<AuditRunOptions> opts,
        CancellationToken ct)
    {
        if (!opts.CurrentValue.Enabled)
            return Results.BadRequest(new { error = "Standalone audit runs are disabled by configuration." });
        var initiator = ApiKeyAuth.ResolveInitiator(context, req.Initiator);
        if (initiator.Error is not null) return initiator.Error;
        var headerKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var hv) ? hv.ToString() : null;
        var request = new AuditRunCreateRequest
        {
            ProjectId = req.Project ?? string.Empty,
            Ref = req.Ref ?? string.Empty,
            BaseRef = req.BaseRef,
            Selection = new AuditRunSelection
            {
                AuditorIds = req.Auditors ?? [],
                Profile = req.Profile,
            },
            IdempotencyKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? headerKey : req.IdempotencyKey,
            RepositoryOverride = req.Repository,
        };
        var outcome = await service.CreateAsync(request, initiator.Value?.ToString(), ct);
        if (outcome.Run is null)
        {
            var status = outcome.Conflict ? Results.Conflict(new { error = outcome.Error }) : Results.BadRequest(new { error = outcome.Error });
            return status;
        }
        return Results.Created($"/audit-runs/{outcome.Run.Id}", ToSummary(outcome.Run, outcome.Replay));
    }

    private static async Task<IResult> ListAsync(
        string? project, int? limit,
        StandaloneAuditService service,
        CancellationToken ct)
    {
        var runs = await service.ListAsync(project, limit ?? 50, ct);
        return Results.Ok(runs.Select(r => ToSummary(r, false)).ToList());
    }

    private static async Task<IResult> GetAsync(
        string id, string? project,
        StandaloneAuditService service,
        CancellationToken ct)
    {
        var run = await service.GetAsync(id, project, ct);
        if (run is null)
        {
            var unscoped = await service.GetAsync(id, null, ct);
            if (unscoped is not null)
                return Results.Json(new { error = "Run belongs to a different project." }, statusCode: 403);
            return Results.NotFound();
        }
        return Results.Ok(ToDetail(run));
    }

    private static async Task<IResult> GetReportsAsync(
        string id, string? project,
        StandaloneAuditService service,
        CancellationToken ct)
    {
        var run = await service.GetAsync(id, project, ct);
        if (run is null)
            return DenyOrMissing(await service.GetAsync(id, null, ct));
        return Results.Ok(new
        {
            runId = run.Id,
            project = run.ProjectId,
            state = run.State.ToString(),
            aggregate = run.AggregateOutcome.ToString(),
            auditors = run.AuditorResults.Select(ToAuditorDto).ToList(),
        });
    }

    private static async Task<IResult> GetLogsAsync(
        string id, string? project, string? auditor,
        StandaloneAuditService service,
        CancellationToken ct)
    {
        var run = await service.GetAsync(id, project, ct);
        if (run is null)
            return DenyOrMissing(await service.GetAsync(id, null, ct));
        var results = run.AuditorResults.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(auditor))
            results = results.Where(r => string.Equals(r.AuditorName, auditor, StringComparison.Ordinal));
        return Results.Ok(new
        {
            runId = run.Id,
            logs = results.Select(r => new
            {
                auditor = r.AuditorName,
                outcome = r.Outcome.ToString(),
                excerpt = r.LogExcerpt,
                artifactDigest = r.LogArtifactDigest,
                durationMs = r.DurationMs,
            }).ToList(),
        });
    }

    private static async Task<IResult> ListArtifactsAsync(
        string id, string? project,
        StandaloneAuditService service,
        CancellationToken ct)
    {
        var run = await service.GetAsync(id, project, ct);
        if (run is null)
            return DenyOrMissing(await service.GetAsync(id, null, ct));
        return Results.Ok(new
        {
            runId = run.Id,
            artifacts = run.Artifacts.Select(a => new
            {
                name = a.Name,
                contentDigest = a.ContentDigest,
                sizeBytes = a.SizeBytes,
                mediaType = a.MediaType,
            }).ToList(),
        });
    }

    private static async Task<IResult> GetArtifactAsync(
        string id, string name, string? project,
        StandaloneAuditService service,
        IAuditRunArtifactStore artifacts,
        CancellationToken ct)
    {
        var run = await service.GetAsync(id, project, ct);
        if (run is null)
            return DenyOrMissing(await service.GetAsync(id, null, ct));
        var decoded = Uri.UnescapeDataString(name);
        var match = run.Artifacts.FirstOrDefault(a => string.Equals(a.Name, decoded, StringComparison.Ordinal));
        if (match is null)
            return Results.NotFound();
        var blob = await artifacts.GetAsync(id, decoded, ct);
        if (blob is null)
            return Results.NotFound();
        return Results.Bytes(blob.Value.Content, blob.Value.MediaType);
    }

    private static async Task<IResult> CancelAsync(
        string id,
        StandaloneAuditService service,
        IOptionsMonitor<AuditRunOptions> opts,
        CancellationToken ct)
    {
        if (!opts.CurrentValue.Enabled)
            return Results.BadRequest(new { error = "Standalone audit runs are disabled by configuration." });
        var (ok, error) = await service.CancelAsync(id, ct);
        if (!ok)
            return string.Equals(error, "Not found.", StringComparison.Ordinal)
                ? Results.NotFound()
                : Results.BadRequest(new { error });
        var run = await service.GetAsync(id, null, ct);
        return Results.Ok(ToSummary(run!, false));
    }

    private static IResult DenyOrMissing(AuditRunRecord? unscoped) =>
        unscoped is not null
            ? Results.Json(new { error = "Run belongs to a different project." }, statusCode: 403)
            : Results.NotFound();

    private static object ToSummary(AuditRunRecord run, bool replay) => new
    {
        id = run.Id,
        project = run.ProjectId,
        state = run.State.ToString(),
        aggregate = run.AggregateOutcome.ToString(),
        sha = run.Provenance.ResolvedSha,
        baseSha = run.Provenance.ResolvedBaseSha,
        auditors = run.Provenance.ResolvedAuditorIds,
        profile = run.Provenance.ResolvedProfile,
        configDigest = run.Provenance.ConfigDigest,
        createdAt = run.CreatedAt,
        updatedAt = run.UpdatedAt,
        completedAt = run.CompletedAt,
        replayed = replay,
    };

    private static object ToDetail(AuditRunRecord run) => new
    {
        id = run.Id,
        project = run.ProjectId,
        state = run.State.ToString(),
        aggregate = run.AggregateOutcome.ToString(),
        provenance = new
        {
            requestedRef = run.Provenance.RequestedRef,
            sha = run.Provenance.ResolvedSha,
            requestedBaseRef = run.Provenance.RequestedBaseRef,
            baseSha = run.Provenance.ResolvedBaseSha,
            auditors = run.Provenance.ResolvedAuditorIds,
            profile = run.Provenance.ResolvedProfile,
            configDigest = run.Provenance.ConfigDigest,
            createdAt = run.Provenance.CreatedAt,
            createdBy = run.Provenance.CreatedBy,
        },
        auditors = run.AuditorResults.Select(ToAuditorDto).ToList(),
        artifacts = run.Artifacts.Select(a => new
        {
            name = a.Name,
            contentDigest = a.ContentDigest,
            sizeBytes = a.SizeBytes,
            mediaType = a.MediaType,
        }).ToList(),
        attempts = run.Attempts,
        failure = run.FailureDetail,
        createdAt = run.CreatedAt,
        updatedAt = run.UpdatedAt,
        completedAt = run.CompletedAt,
    };

    private static object ToAuditorDto(AuditRunAuditorResult r) => new
    {
        name = r.AuditorName,
        kind = r.AuditorKind,
        outcome = r.Outcome.ToString(),
        unsupportedReason = r.UnsupportedReason.ToString(),
        unsupportedDetail = r.UnsupportedDetail,
        findings = r.Findings.Select(f => new
        {
            id = f.Id,
            severity = f.Severity,
            title = f.Title,
            message = f.Message,
            files = f.Files,
            lineHints = f.LineHints,
        }).ToList(),
        exitCode = r.ExitCode,
        startedAt = r.StartedAt,
        endedAt = r.EndedAt,
        durationMs = r.DurationMs,
        toolVersion = r.ToolVersion,
        toolDigest = r.ToolDigest,
        attempt = r.Attempt,
        evidenceSufficient = r.EvidenceSufficient,
        logArtifactDigest = r.LogArtifactDigest,
        logAvailable = r.LogExcerpt is not null,
    };
}

internal sealed record AuditRunCreateDto
{
    public string? Project { get; init; }
    public string? Ref { get; init; }
    public string? BaseRef { get; init; }
    public IReadOnlyList<string>? Auditors { get; init; }
    public string? Profile { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Repository { get; init; }
    public WorkInitiator? Initiator { get; init; }
}
