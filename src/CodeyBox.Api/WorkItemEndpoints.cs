using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class WorkItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapPost("/", CreateAsync);
        group.MapPost("/{id}/retry", RetryAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", CancelAsync);
        group.MapGet("/{id}/dependents", GetDependentsAsync);

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

        // ── Dependency validation ─────────────────────────────────────────────

        // Parse dependsOn IDs. Cap at 100 to bound sequential existence checks
        // and cycle-detection graph size (prevents resource exhaustion via
        // a single oversized request).
        if ((req.DependsOn?.Length ?? 0) > 100)
            return Results.BadRequest(new { error = "dependsOn must contain at most 100 entries" });

        var dependsOnIds = new List<WorkItemId>();
        foreach (var rawId in req.DependsOn ?? [])
        {
            if (!Guid.TryParse(rawId, out var g))
                return Results.BadRequest(new { error = $"dependency '{rawId}' is not a valid work item id" });
            dependsOnIds.Add(new WorkItemId(g));
        }

        var newId = WorkItemId.New();

        // Self-dependency check: explicit guard per spec.
        if (dependsOnIds.Contains(newId))
            return Results.BadRequest(new { error = "a work item cannot depend on itself" });

        // Load all existing items once — used for both existence and cycle checks.
        var allItems = new List<WorkItem>();
        await foreach (var existing in store.ListAsync(ct)) allItems.Add(existing);

        // Existence check: every dep must already be in the store.
        var missingDep = WorkItemDependencies.FindMissingDependency(dependsOnIds, allItems);
        if (missingDep is not null)
            return Results.BadRequest(new { error = $"dependency {missingDep} not found" });

        // Cycle check: build the full graph (existing items + proposed new item)
        // and verify the graph remains acyclic. O(V + E).
        var cyclePath = WorkItemDependencies.FindCycle(newId, dependsOnIds, allItems);
        if (cyclePath is not null)
            return Results.BadRequest(new { error = $"circular dependency detected: {cyclePath}" });

        // ── Build and persist ─────────────────────────────────────────────────

        var item = new WorkItem
        {
            Id = newId,
            ProjectId = pid,
            Title = req.Title,
            Prompt = req.Prompt,
            BaseBranch = req.BaseBranch,
            WorkBranch = req.WorkBranch,
            Agent = agentOverride,
            PushUpstream = req.PushUpstream ?? true,
            DependsOn = dependsOnIds,
        };
        if (req.WorkTimeoutMinutes is { } w)
            item = item with { WorkTimeout = TimeSpan.FromMinutes(Math.Clamp(w, 1, 480)) };
        if (req.MergeTimeoutMinutes is { } m)
            item = item with { MergeTimeout = TimeSpan.FromMinutes(Math.Clamp(m, 1, 240)) };

        await store.CreateAsync(item, ct);
        AuditLog.WorkItemCreated(item.Id, item.ProjectId, item.Title);

        // Re-read dep states after persisting to avoid TOCTOU: a dep may have
        // transitioned to terminal between the allItems snapshot and the commit.
        // Using fresh reads ensures we don't miss the initial enqueue.
        var freshDepStates = new Dictionary<WorkItemId, WorkItemState>();
        foreach (var depId in dependsOnIds)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
                freshDepStates[depId] = dep.State;
        }
        if (WorkItemDependencies.AreSatisfied(item.DependsOn, freshDepStates))
            await queue.EnqueueAsync(item.Id, ct);

        return Results.Created($"/workitems/{item.Id}", ToDto(item, project, freshDepStates));
    }

    private static async Task<IResult> ListAsync(IWorkItemStore store, IProjectRepository projects, CancellationToken ct)
    {
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);
        var allItems = new List<WorkItem>();
        await foreach (var item in store.ListAsync(ct)) allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);

        var list = allItems.Select(item =>
        {
            allProjects.TryGetValue(item.ProjectId.Value, out var p);
            return ToDto(item, p, statesById);
        }).ToList();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(string id, IWorkItemStore store, IProjectRepository projects, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var item = await store.GetAsync(new WorkItemId(g), ct);
        if (item is null) return Results.NotFound();

        // Read only the dep states needed — avoids an O(N) full-store scan.
        var statesById = new Dictionary<WorkItemId, WorkItemState>();
        foreach (var depId in item.DependsOn)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
                statesById[depId] = dep.State;
        }

        var project = await projects.GetAsync(item.ProjectId, ct);
        return Results.Ok(ToDto(item, project, statesById));
    }

    /// <summary>
    /// List all work items that directly depend on the given item. Useful for
    /// inspecting blast radius before cancelling.
    /// </summary>
    private static async Task<IResult> GetDependentsAsync(
        string id,
        IWorkItemStore store,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var targetId = new WorkItemId(g);
        var target = await store.GetAsync(targetId, ct);
        if (target is null) return Results.NotFound();

        var allItems = new List<WorkItem>();
        await foreach (var item in store.ListAsync(ct)) allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);

        var dependents = allItems
            .Where(item => item.DependsOn.Contains(targetId))
            .Select(item =>
            {
                allProjects.TryGetValue(item.ProjectId.Value, out var p);
                return ToDto(item, p, statesById);
            })
            .ToList();

        return Results.Ok(dependents);
    }

    /// <summary>
    /// Retry a terminal-failed work item from a specific phase. Resets the
    /// state to the matching pre-phase marker and re-enqueues; the pipeline
    /// runner gates each phase by entry state, so earlier phases are
    /// skipped (their output — branch / merged base — is still in the bare
    /// repo from the prior run).
    /// </summary>
    private static async Task<IResult> RetryAsync(
        string id,
        RetryWorkItemRequest? body,
        IWorkItemStore store,
        ITaskQueue queue,
        IGitHost gitHost,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);
        var item = await store.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();

        // Only resume from terminal-failed states. Done items have nothing
        // to retry; non-terminal states would race the pipeline.
        if (item.State is not (WorkItemState.Failed or WorkItemState.AuditFailed or WorkItemState.Cancelled))
            return Results.Conflict(new { error = $"cannot retry item in state {item.State}; only terminal-failed items can be retried" });

        var from = (body?.From ?? "work").Trim().ToLowerInvariant();
        var resumeState = from switch
        {
            "work" => WorkItemState.Queued,
            "audit" => WorkItemState.WorkComplete,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => (WorkItemState?)null,
        };
        if (resumeState is null)
            return Results.BadRequest(new { error = $"invalid 'from' value '{from}'", valid = new[] { "work", "audit", "merge", "upstream" } });

        // For from != "work", the pipeline expects the bare repo (with the
        // work branch and any later merges) to still be present. If the
        // operator deleted it, fail loudly rather than re-clone empty.
        if (resumeState != WorkItemState.Queued)
        {
            var present = await gitHost.RepositoryExistsAsync(item.Id, ct);
            if (!present)
                return Results.Conflict(new
                {
                    error = $"cannot retry from '{from}': bare repo for work item {id} no longer exists",
                    hint = "retry with from=\"work\" to start over from a fresh clone"
                });
        }

        var resumed = item.With(resumeState.Value, error: null);
        await store.UpdateAsync(resumed, ct);
        AuditLog.WorkItemRetried(workItemId, from);
        await queue.EnqueueAsync(resumed.Id, ct);
        return Results.Accepted($"/workitems/{id}", new { id, from, state = resumeState.Value.ToString() });
    }

    private static async Task<IResult> CancelAsync(
        string id,
        IWorkItemStore store,
        CancellationRegistry cancellations,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);
        var item = await store.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();
        if (item.State is WorkItemState.Done or WorkItemState.Failed
            or WorkItemState.Cancelled or WorkItemState.AuditFailed)
            return Results.Conflict(new { error = $"cannot cancel item in state {item.State}" });

        var wasActive = cancellations.Cancel(workItemId);
        if (!wasActive)
        {
            var cancelled = item.With(WorkItemState.Cancelled, "cancelled via API");
            await store.UpdateAsync(cancelled, ct);
            AuditLog.WorkItemCancelled(workItemId);
            var project = await projects.GetAsync(item.ProjectId, ct);
            if (project is not null)
                await webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.cancelled",
                    WorkItem = cancelled,
                    Project = project,
                }, ct);
        }

        // Cascade: cancel all Queued items that (transitively) depend on this
        // one. In-flight items (non-Queued) are left to run their course.
        await CascadeCancelDependentsAsync(workItemId, store, ct);

        return Results.Accepted($"/workitems/{id}");
    }

    private static async Task CascadeCancelDependentsAsync(
        WorkItemId cancelledId,
        IWorkItemStore store,
        CancellationToken ct)
    {
        var allItems = new List<WorkItem>();
        await foreach (var i in store.ListAsync(ct)) allItems.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(cancelledId, allItems);
        foreach (var target in targets)
        {
            // Atomic conditional update: only writes Cancelled when the item is still
            // Queued in the DB. If a worker raced and transitioned it to Working between
            // the ListAsync snapshot and now, the WHERE guard returns 0 rows and we skip
            // the audit log — no spurious WorkItemDependentCancelled for in-flight items.
            var cancelled = target.With(WorkItemState.Cancelled, "parent dependency cancelled");
            var updated = await store.TryUpdateIfStateAsync(cancelled, WorkItemState.Queued, ct);
            if (updated)
                AuditLog.WorkItemDependentCancelled(target.Id, cancelledId);
        }
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

    private static WorkItemDto ToDto(WorkItem item, Project? project, IReadOnlyDictionary<WorkItemId, WorkItemState> statesById)
    {
        var depsSatisfied = WorkItemDependencies.AreSatisfied(item.DependsOn, statesById);
        return new WorkItemDto(
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
            item.UpstreamPushAttempts,
            item.DependsOn.Select(d => d.ToString()).ToList(),
            depsSatisfied);
    }

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
    bool? PushUpstream,
    int? WorkTimeoutMinutes,
    int? MergeTimeoutMinutes,
    string[]? DependsOn = null);

public sealed record RetryWorkItemRequest(string? From);

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
    int UpstreamPushAttempts,
    IReadOnlyList<string> DependsOn,
    bool DependsOnSatisfied);

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
