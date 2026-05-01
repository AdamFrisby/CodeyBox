using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class SuggestionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/suggestions");
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPatch("/{id}", PatchAsync);
        group.MapPost("/{id}/promote", PromoteAsync);
    }

    private static async Task<IResult> ListAsync(
        string? project,
        string? category,
        string? severity,
        ISuggestionStore store,
        CancellationToken ct)
    {
        var results = new List<Suggestion>();
        await foreach (var s in store.ListAsync(
            projectId: project,
            category: category,
            severity: severity,
            state: "open",
            ct: ct))
            results.Add(s);
        return Results.Ok(results.Select(ToDto));
    }

    private static async Task<IResult> GetAsync(
        string id,
        ISuggestionStore store,
        CancellationToken ct)
    {
        var suggestion = await store.GetAsync(id, ct);
        return suggestion is null ? Results.NotFound() : Results.Ok(ToDto(suggestion));
    }

    private static async Task<IResult> PatchAsync(
        string id,
        PatchSuggestionRequest body,
        ISuggestionStore store,
        CancellationToken ct)
    {
        if (body.State != "dismissed")
            return Results.BadRequest(new { error = "only state='dismissed' is accepted via PATCH" });
        if (body.DismissReason is not null && body.DismissReason.Length > 500)
            return Results.BadRequest(new { error = "dismissReason must be <= 500 chars" });

        var suggestion = await store.GetAsync(id, ct);
        if (suggestion is null) return Results.NotFound();
        if (suggestion.State != "open")
            return Results.Conflict(new { error = $"suggestion is in state '{suggestion.State}'; only 'open' suggestions can be dismissed" });

        var updated = suggestion with { State = "dismissed", DismissReason = body.DismissReason };
        await store.UpdateAsync(updated, ct);
        AuditLog.SuggestionDismissed(id, body.DismissReason);
        return Results.Ok(ToDto(updated));
    }

    private static async Task<IResult> PromoteAsync(
        string id,
        PromoteSuggestionRequest? body,
        ISuggestionStore store,
        IWorkItemStore workItemStore,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        var suggestion = await store.GetAsync(id, ct);
        if (suggestion is null) return Results.NotFound();
        if (suggestion.State != "open")
            return Results.Conflict(new { error = $"suggestion is in state '{suggestion.State}'; only 'open' suggestions can be promoted" });

        ProjectId pid;
        try { pid = new ProjectId(suggestion.ProjectId); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

        var project = await projects.GetAsync(pid, ct);
        if (project is null)
            return Results.BadRequest(new { error = $"source project '{suggestion.ProjectId}' no longer exists" });

        AgentKind? agentOverride = null;
        if (!string.IsNullOrWhiteSpace(body?.Agent))
        {
            var kind = new AgentKind(body.Agent);
            if (!agents.TryGet(kind, out _))
                return Results.BadRequest(new { error = $"unknown agent '{body.Agent}'" });
            agentOverride = kind;
        }

        var workBranch = body?.WorkBranch;
        if (workBranch is not null)
        {
            try { Validation.ValidateBranchName(workBranch, nameof(workBranch)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }

        var newId = WorkItemId.New();
        var prompt = $"# From suggestion: {suggestion.Title}\n\n{suggestion.Rationale}";
        var item = new WorkItem
        {
            Id = newId,
            ProjectId = pid,
            Title = suggestion.Title,
            Prompt = prompt,
            Agent = agentOverride,
            BaseBranch = body?.BaseBranch,
            WorkBranch = workBranch,
            PushUpstream = body?.PushUpstream ?? true,
            AgentClassId = body?.AgentClassId,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
        };

        await workItemStore.CreateAsync(item, ct);
        AuditLog.WorkItemCreated(item.Id, item.ProjectId, item.Title);
        await queue.EnqueueAsync(item.Id, ct);

        var promoted = suggestion with
        {
            State = "accepted",
            PromotedToWorkItemId = newId.ToString(),
        };
        await store.UpdateAsync(promoted, ct);
        AuditLog.SuggestionPromoted(id, newId.ToString());

        var statesById = new Dictionary<WorkItemId, WorkItemState>();
        return Results.Ok(new PromoteResponse(
            WorkItemId: newId.ToString(),
            Suggestion: ToDto(promoted)));
    }

    private static SuggestionDto ToDto(Suggestion s) => new(
        s.Id,
        s.SourceWorkItemId,
        s.ProjectId,
        s.Title,
        s.Rationale,
        s.Category,
        s.Severity,
        s.EstimatedEffort,
        s.FilesReferenced.ToList(),
        s.CreatedAt,
        s.State,
        s.DismissReason,
        s.PromotedToWorkItemId);
}

public sealed record PatchSuggestionRequest(
    string State,
    string? DismissReason = null);

public sealed record PromoteSuggestionRequest(
    string? Agent = null,
    string? WorkBranch = null,
    bool? PushUpstream = null,
    string? BaseBranch = null,
    string? AgentClassId = null);

public sealed record SuggestionDto(
    string Id,
    string SourceWorkItemId,
    string ProjectId,
    string Title,
    string Rationale,
    string Category,
    string Severity,
    string EstimatedEffort,
    IReadOnlyList<string> FilesReferenced,
    DateTimeOffset CreatedAt,
    string State,
    string? DismissReason,
    string? PromotedToWorkItemId);

internal sealed record PromoteResponse(string WorkItemId, SuggestionDto Suggestion);
