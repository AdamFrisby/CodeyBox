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
    }

    private static async Task<IResult> CreateAsync(
        CreateWorkItemRequest req,
        IWorkItemStore store,
        ITaskQueue queue,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required" });
        if (string.IsNullOrWhiteSpace(req.Prompt)) return Results.BadRequest(new { error = "prompt is required" });
        if (string.IsNullOrWhiteSpace(req.RepositoryUrl)) return Results.BadRequest(new { error = "repositoryUrl is required" });
        if (string.IsNullOrWhiteSpace(req.Agent)) return Results.BadRequest(new { error = "agent is required" });

        // Validate everything that ends up on a git argv before persisting.
        try
        {
            Validation.ValidateRepositoryUrl(req.RepositoryUrl, nameof(req.RepositoryUrl));
            if (req.BaseBranch is not null) Validation.ValidateBranchName(req.BaseBranch, nameof(req.BaseBranch));
            if (req.WorkBranch is not null) Validation.ValidateBranchName(req.WorkBranch, nameof(req.WorkBranch));
            Validation.ValidateNoOptionLikeOrControl(req.Title, nameof(req.Title));

            // Don't allow the agent to push directly to the integration branch.
            // If WorkBranch == BaseBranch, the work-phase push lands on
            // BaseBranch and the merge phase becomes a no-op fast-forward,
            // skipping the merge-sandbox containment of the agent's output.
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

        var agentKind = new AgentKind(req.Agent);
        if (!agents.TryGet(agentKind, out _))
            return Results.BadRequest(new { error = $"unknown agent '{req.Agent}'", available = agents.Available.Select(a => a.Value) });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            Title = req.Title,
            Prompt = req.Prompt,
            RepositoryUrl = req.RepositoryUrl,
            BaseBranch = req.BaseBranch,
            WorkBranch = req.WorkBranch,
            Agent = agentKind,
            PushUpstream = req.PushUpstream ?? true,
        };
        await store.CreateAsync(item, ct);
        await queue.EnqueueAsync(item.Id, ct);
        return Results.Created($"/workitems/{item.Id}", ToDto(item));
    }

    private static async Task<IResult> ListAsync(IWorkItemStore store, CancellationToken ct)
    {
        var list = new List<WorkItemDto>();
        await foreach (var item in store.ListAsync(ct)) list.Add(ToDto(item));
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(string id, IWorkItemStore store, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var item = await store.GetAsync(new WorkItemId(g), ct);
        return item is null ? Results.NotFound() : Results.Ok(ToDto(item));
    }

    private static async Task<IResult> CancelAsync(string id, IWorkItemStore store, CancellationRegistry cancellations, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);
        var item = await store.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State is WorkItemState.Done or WorkItemState.Failed or WorkItemState.Cancelled)
            return Results.Conflict(new { error = $"cannot cancel item in state {item.State}" });

        // If a worker is actively running this item, cancel its CTS — the
        // pipeline will catch OperationCanceledException, mark Cancelled in
        // the store, and unwind the sandbox via DisposeAsync.
        var wasActive = cancellations.Cancel(workItemId);
        if (!wasActive)
        {
            // Item is queued but not yet picked up. Mark Cancelled directly;
            // the worker's pre-run check will skip it.
            await store.UpdateAsync(item.With(WorkItemState.Cancelled, "cancelled via API"), ct);
        }
        return Results.Accepted($"/workitems/{id}");
    }

    private static WorkItemDto ToDto(WorkItem item) => new(
        item.Id.ToString(),
        item.Title,
        item.Agent.Value,
        item.RepositoryUrl,
        item.BaseBranch,
        item.WorkBranch,
        item.State.ToString(),
        item.CreatedAt,
        item.UpdatedAt,
        item.LastError,
        item.UpstreamPushAttempts);
}

public sealed record CreateWorkItemRequest(
    string Title,
    string Prompt,
    string RepositoryUrl,
    string Agent,
    string? BaseBranch,
    string? WorkBranch,
    bool? PushUpstream);

public sealed record WorkItemDto(
    string Id,
    string Title,
    string Agent,
    string RepositoryUrl,
    string? BaseBranch,
    string? WorkBranch,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    int UpstreamPushAttempts);
