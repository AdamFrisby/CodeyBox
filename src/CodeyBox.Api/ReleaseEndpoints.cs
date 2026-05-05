using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class ReleaseEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/releases");
        g.MapPost("/", CreateAsync);
        g.MapGet("/", ListAsync);
        g.MapGet("/{id}", GetAsync);
        g.MapGet("/{id}/workitems", GetWorkItemsAsync);
        g.MapPost("/{id}/close", CloseAsync);
        g.MapPost("/{id}/reopen", ReopenAsync);
        g.MapPost("/{id}/abandon", AbandonAsync);
        g.MapPost("/{id}/release", ReleaseAsync);
    }

    // ── POST /releases ─────────────────────────────────────────────────────

    private static async Task<IResult> CreateAsync(
        CreateReleaseRequest req,
        IReleaseStore store,
        IProjectRepository projects,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "name is required" });
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            return Results.BadRequest(new { error = "projectId is required" });
        if (req.Name.Length > 200)
            return Results.BadRequest(new { error = "name must be <= 200 chars" });
        if (req.Name.Any(char.IsControl))
            return Results.BadRequest(new { error = "name must not contain control characters" });

        ProjectId pid;
        try { pid = new ProjectId(req.ProjectId); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

        var project = await projects.GetAsync(pid, ct);
        if (project is null)
            return Results.NotFound(new { error = $"project '{req.ProjectId}' not found" });

        if (!project.ReleaseConfig.Enabled)
            return Results.BadRequest(new { error = $"release management is not enabled for project '{req.ProjectId}'" });

        var existing = await store.GetByNameAsync(pid, req.Name, ct);
        if (existing is not null)
            return Results.Conflict(new { error = $"a release named '{req.Name}' already exists in project '{req.ProjectId}'" });

        var release = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = pid,
            Name = req.Name,
            Description = req.Description,
            State = ReleaseState.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.CreateAsync(release, ct);
        await webhooks.PublishAsync(new WebhookEvent
        {
            Event = "release.created",
            Release = release,
            Project = project,
        }, ct);
        return Results.Created($"/releases/{release.Id}", ToDto(release));
    }

    // ── GET /releases ──────────────────────────────────────────────────────

    private static async Task<IResult> ListAsync(
        string? projectId,
        string? state,
        IReleaseStore store,
        CancellationToken ct)
    {
        ProjectId? pid = null;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            try { pid = new ProjectId(projectId); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }

        ReleaseState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<ReleaseState>(state, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"unknown state '{state}'" });
            stateFilter = parsed;
        }

        var releases = await store.ListAsync(pid, stateFilter, ct);
        return Results.Ok(releases.Select(ToDto).ToList());
    }

    // ── GET /releases/{id} ─────────────────────────────────────────────────

    private static async Task<IResult> GetAsync(
        string id,
        IReleaseStore store,
        CancellationToken ct)
    {
        var (release, err) = await ResolveAsync(id, store, ct);
        if (err is not null) return err;
        return Results.Ok(ToDto(release!));
    }

    // ── GET /releases/{id}/workitems ───────────────────────────────────────

    private static async Task<IResult> GetWorkItemsAsync(
        string id,
        IReleaseStore releaseStore,
        IWorkItemStore workItems,
        CancellationToken ct)
    {
        var (release, err) = await ResolveAsync(id, releaseStore, ct);
        if (err is not null) return err;

        var items = new List<object>();
        await foreach (var item in workItems.ListByReleaseAsync(release!.Id, ct))
        {
            items.Add(new
            {
                id = item.Id.ToString(),
                title = item.Title,
                state = item.State.ToString(),
                createdAt = item.CreatedAt,
                updatedAt = item.UpdatedAt,
                lastError = item.LastError,
            });
        }

        return Results.Ok(items);
    }

    // ── POST /releases/{id}/close ──────────────────────────────────────────

    private static async Task<IResult> CloseAsync(
        string id,
        IReleaseStore store,
        ReleaseService releaseService,
        CancellationToken ct)
    {
        var (release, err) = await ResolveAsync(id, store, ct);
        if (err is not null) return err;

        var (success, error) = await releaseService.CloseAsync(release!.Id, ct);
        if (!success) return Results.BadRequest(new { error });

        var updated = await store.GetAsync(release.Id, ct);
        return Results.Ok(ToDto(updated ?? release));
    }

    // ── POST /releases/{id}/reopen ─────────────────────────────────────────

    private static async Task<IResult> ReopenAsync(
        string id,
        ReopenReleaseRequest req,
        IReleaseStore store,
        ReleaseService releaseService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Results.BadRequest(new { error = "reason is required" });

        var (release, err) = await ResolveAsync(id, store, ct);
        if (err is not null) return err;

        var (success, error) = await releaseService.ReopenAsync(release!.Id, req.Reason, ct);
        if (!success) return Results.BadRequest(new { error });

        var updated = await store.GetAsync(release.Id, ct);
        return Results.Ok(ToDto(updated ?? release));
    }

    // ── POST /releases/{id}/abandon ────────────────────────────────────────

    private static async Task<IResult> AbandonAsync(
        string id,
        IReleaseStore store,
        ReleaseService releaseService,
        CancellationToken ct)
    {
        var (release, err) = await ResolveAsync(id, store, ct);
        if (err is not null) return err;

        var (success, error) = await releaseService.AbandonAsync(release!.Id, ct);
        if (!success) return Results.BadRequest(new { error });

        var updated = await store.GetAsync(release.Id, ct);
        return Results.Ok(ToDto(updated ?? release));
    }

    // ── POST /releases/{id}/release ────────────────────────────────────────

    private static async Task<IResult> ReleaseAsync(
        string id,
        IReleaseStore store,
        ReleaseService releaseService,
        CancellationToken ct)
    {
        var (release, err) = await ResolveAsync(id, store, ct);
        if (err is not null) return err;

        var (success, error) = await releaseService.ForceBeginReviewAsync(release!.Id, ct);
        if (!success) return Results.BadRequest(new { error });

        var updated = await store.GetAsync(release.Id, ct);
        return Results.Accepted(value: ToDto(updated ?? release));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<(Release? release, IResult? error)> ResolveAsync(
        string idSegment,
        IReleaseStore store,
        CancellationToken ct)
    {
        if (!ReleaseId.TryParse(idSegment, out var rid))
            return (null, Results.BadRequest(new { error = "invalid release id" }));
        var release = await store.GetAsync(rid, ct);
        return release is null ? (null, Results.NotFound()) : (release, null);
    }

    private static ReleaseDto ToDto(Release r) => new(
        r.Id.ToString(),
        r.ProjectId.Value,
        r.Name,
        r.Description,
        r.State.ToString(),
        r.BranchName,
        r.BaseCommitSha,
        r.CreatedAt,
        r.ClosedAt,
        r.ReviewStartedAt,
        r.ReleasedAt,
        r.FailedReason,
        r.TargetTag);
}

public sealed record CreateReleaseRequest(
    string ProjectId,
    string Name,
    string? Description = null);

public sealed record ReopenReleaseRequest(string Reason = "");

public sealed record ReleaseDto(
    string Id,
    string ProjectId,
    string Name,
    string? Description,
    string State,
    string? BranchName,
    string? BaseCommitSha,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? ReviewStartedAt,
    DateTimeOffset? ReleasedAt,
    string? FailedReason,
    string? TargetTag);
