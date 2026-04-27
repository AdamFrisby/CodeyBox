using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class WorkItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", CancelAsync);

        var projects = app.MapGroup("/projects");
        projects.MapGet("/", ListProjectsAsync);
        projects.MapGet("/{id}", GetProjectAsync);
    }

    private static async Task<IResult> CreateAsync(
        CreateWorkItemRequest req,
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required" });
        if (string.IsNullOrWhiteSpace(req.Prompt)) return Results.BadRequest(new { error = "prompt is required" });
        if (string.IsNullOrWhiteSpace(req.ProjectId)) return Results.BadRequest(new { error = "projectId is required" });

        ProjectId pid;
        try { pid = new ProjectId(req.ProjectId); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

        var project = await projects.GetAsync(pid, ct);
        if (project is null)
        {
            var known = (await projects.ListAsync(ct)).Select(p => p.Id.Value).ToList();
            return Results.BadRequest(new { error = $"unknown project '{req.ProjectId}'", available = known });
        }

        // Validate everything that ends up on a git argv before persisting.
        try
        {
            if (req.BaseBranch is not null) Validation.ValidateBranchName(req.BaseBranch, nameof(req.BaseBranch));
            if (req.WorkBranch is not null) Validation.ValidateBranchName(req.WorkBranch, nameof(req.WorkBranch));
            Validation.ValidateNoOptionLikeOrControl(req.Title, nameof(req.Title));

            // Don't allow the agent to push directly to the integration branch.
            if (req.WorkBranch is not null && req.BaseBranch is not null
                && string.Equals(req.WorkBranch, req.BaseBranch, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "workBranch must differ from baseBranch" });
            }

            if (req.Title.Length > 200)
                return Results.BadRequest(new { error = "title must be <= 200 chars" });
            if (req.Prompt.Length > 64 * 1024)
                return Results.BadRequest(new { error = "prompt must be <= 64KB" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        AgentKind? agentOverride = null;
        if (!string.IsNullOrWhiteSpace(req.Agent))
        {
            var kind = new AgentKind(req.Agent);
            if (!agents.TryGet(kind, out _))
                return Results.BadRequest(new { error = $"unknown agent '{req.Agent}'", available = agents.Available.Select(a => a.Value) });
            agentOverride = kind;
        }

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = pid,
            Title = req.Title,
            Prompt = req.Prompt,
            BaseBranch = req.BaseBranch,
            WorkBranch = req.WorkBranch,
            Agent = agentOverride,
            PushUpstream = req.PushUpstream ?? true,
        };
        await store.CreateAsync(item, ct);
        await queue.EnqueueAsync(item.Id, ct);
        return Results.Created($"/workitems/{item.Id}", ToDto(item, project));
    }

    private static async Task<IResult> ListAsync(IWorkItemStore store, IProjectRepository projects, CancellationToken ct)
    {
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);
        var list = new List<WorkItemDto>();
        await foreach (var item in store.ListAsync(ct))
        {
            allProjects.TryGetValue(item.ProjectId.Value, out var p);
            list.Add(ToDto(item, p));
        }
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(string id, IWorkItemStore store, IProjectRepository projects, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var item = await store.GetAsync(new WorkItemId(g), ct);
        if (item is null) return Results.NotFound();
        var project = await projects.GetAsync(item.ProjectId, ct);
        return Results.Ok(ToDto(item, project));
    }

    private static async Task<IResult> CancelAsync(string id, IWorkItemStore store, CancellationRegistry cancellations, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);
        var item = await store.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
            return Results.Conflict(new { error = $"cannot cancel item in state {item.State}" });

        var wasActive = cancellations.Cancel(workItemId);
        if (!wasActive)
            await store.UpdateAsync(item.With(WorkItemState.Cancelled, "cancelled via API"), ct);
        return Results.Accepted($"/workitems/{id}");
    }

    private static async Task<IResult> ListProjectsAsync(IProjectRepository projects, CancellationToken ct)
    {
        var list = await projects.ListAsync(ct);
        return Results.Ok(list.Select(ToProjectDto));
    }

    private static async Task<IResult> GetProjectAsync(string id, IProjectRepository projects, CancellationToken ct)
    {
        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid project id" }); }
        var project = await projects.GetAsync(pid, ct);
        return project is null ? Results.NotFound() : Results.Ok(ToProjectDto(project));
    }

    private static WorkItemDto ToDto(WorkItem item, Project? project) => new(
        item.Id.ToString(),
        item.ProjectId.Value,
        item.Title,
        (item.Agent ?? project?.DefaultAgent ?? AgentKind.Claude).Value,
        project?.RepositoryUrl,
        item.BaseBranch,
        item.WorkBranch,
        item.State.ToString(),
        item.CreatedAt,
        item.UpdatedAt,
        item.LastError,
        item.UpstreamPushAttempts);

    private static ProjectDto ToProjectDto(Project p) => new(
        p.Id.Value,
        p.DisplayName,
        p.RepositoryUrl,
        p.DefaultBaseBranch,
        p.DefaultAgent.Value,
        p.Upstream.Kind,
        p.Audit.Languages,
        p.Audit.AuditTypes,
        p.Audit.MaxIterations);
}

public sealed record CreateWorkItemRequest(
    string ProjectId,
    string Title,
    string Prompt,
    string? Agent,
    string? BaseBranch,
    string? WorkBranch,
    bool? PushUpstream);

public sealed record WorkItemDto(
    string Id,
    string ProjectId,
    string Title,
    string Agent,
    string? RepositoryUrl,
    string? BaseBranch,
    string? WorkBranch,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    int UpstreamPushAttempts);

public sealed record ProjectDto(
    string Id,
    string DisplayName,
    string RepositoryUrl,
    string? DefaultBaseBranch,
    string DefaultAgent,
    string UpstreamKind,
    IReadOnlyList<string> AuditLanguages,
    IReadOnlyList<string> AuditTypes,
    int AuditMaxIterations);
