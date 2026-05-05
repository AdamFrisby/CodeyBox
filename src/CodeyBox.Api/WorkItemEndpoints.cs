using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class WorkItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapPost("/", CreateAsync);
        group.MapPost("/reorder", ReorderWorkItemsAsync);
        group.MapPost("/{id}/retry", RetryAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", CancelAsync);
        group.MapGet("/{id}/dependents", GetDependentsAsync);
        group.MapPatch("/{id}", PatchWorkItemAsync);
        group.MapGet("/{id}/timeline", GetTimelineAsync);
        group.MapGet("/{id}/questions", GetQuestionsAsync);
        group.MapPost("/{id}/answer", AnswerQuestionAsync);
        group.MapPost("/{id}/dismiss-question", DismissQuestionAsync);

        var projects = app.MapGroup("/projects");
        projects.MapGet("/", ListProjectsAsync);
        projects.MapGet("/{id}", GetProjectAsync);
        projects.MapGet("/{id}/budget/usage", GetBudgetUsageAsync);

        app.MapGet("/workers/status", GetWorkerStatusAsync);
        app.MapGet("/queue/status", GetQueueStatusAsync);
        app.MapPost("/queue/pause", PauseQueueAsync);
        app.MapPost("/queue/resume", ResumeQueueAsync);
    }

    private static IResult GetWorkerStatusAsync(OrchestratorService orchestrator)
    {
        var status = orchestrator.GetStatus();
        return Results.Ok(new
        {
            maxConcurrent = status.MaxConcurrent,
            currentlyRunning = status.CurrentlyRunning,
            queuedCount = status.QueuedCount,
            lastSpawnAt = status.LastSpawnAt,
        });
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

        // ── ExternalId validation ─────────────────────────────────────────────

        string? externalId = null;
        if (!string.IsNullOrEmpty(req.ExternalId))
        {
            try { Validation.ValidateExternalId(req.ExternalId, nameof(req.ExternalId)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            externalId = req.ExternalId;

            var conflict = await store.GetByExternalIdAsync(pid, externalId, ct);
            if (conflict is not null)
                return Results.BadRequest(new
                {
                    error = $"externalId '{externalId}' already exists in project '{pid}' for work item {conflict.Id} (state: {conflict.State})"
                });
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

        // Load all existing items — used for existence/cycle checks and externalId resolution.
        // Skip the scan entirely when dependsOn is empty to avoid an O(N) read for the common case.
        var allItems = new List<WorkItem>();
        var allItemsByExternalId = new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        if (req.DependsOn?.Length > 0)
        {
            await foreach (var existing in store.ListAsync(ct)) allItems.Add(existing);
            // GroupBy guards against data-inconsistency duplicates (index corruption, missed migration):
            // last-wins ensures a deterministic result rather than an unhandled InvalidOperationException.
            allItemsByExternalId = allItems
                .Where(i => i.ExternalId != null && i.ProjectId == pid)
                .GroupBy(i => i.ExternalId!)
                .ToDictionary(g => g.Key, g => g.Last());
        }

        var newId = WorkItemId.New();
        var dependsOnIds = new List<WorkItemId>();
        foreach (var rawId in req.DependsOn ?? [])
        {
            if (rawId is null)
                return Results.BadRequest(new { error = $"dependency could not be resolved: null entry in dependsOn array" });
            if (Guid.TryParse(rawId, out var g))
            {
                dependsOnIds.Add(new WorkItemId(g));
            }
            else
            {
                // Treat as externalId within the same project.
                if (!allItemsByExternalId.TryGetValue(rawId, out var depByExtId))
                    return Results.BadRequest(new { error = $"dependency '{rawId}' could not be resolved: no work item with externalId '{rawId}' in project '{pid}'" });
                dependsOnIds.Add(depByExtId.Id);
            }
        }

        // Self-dependency check: explicit guard per spec.
        if (dependsOnIds.Contains(newId))
            return Results.BadRequest(new { error = "a work item cannot depend on itself" });

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

        string? agentClassId = null;
        if (!string.IsNullOrWhiteSpace(req.AgentClassId))
        {
            if (req.AgentClassId.Length > 200)
                return Results.BadRequest(new { error = "agentClassId must be <= 200 chars" });
            agentClassId = req.AgentClassId.Trim();
        }

        // Use creation timestamp as default queue position so new items sort after
        // any explicitly reordered items (which get small integers 1, 2, 3 …).
        var item = new WorkItem
        {
            Id = newId,
            ProjectId = pid,
            Title = req.Title,
            Prompt = req.Prompt,
            BaseBranch = req.BaseBranch,
            WorkBranch = req.WorkBranch,
            Agent = agentOverride,
            AgentClassId = agentClassId,
            PushUpstream = req.PushUpstream ?? true,
            DependsOn = dependsOnIds,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
            ExternalId = externalId,
        };
        if (req.WorkTimeoutMinutes is { } w)
            item = item with { WorkTimeout = TimeSpan.FromMinutes(Math.Clamp(w, 1, 480)) };
        if (req.MergeTimeoutMinutes is { } m)
            item = item with { MergeTimeout = TimeSpan.FromMinutes(Math.Clamp(m, 1, 240)) };

        try { await store.CreateAsync(item, ct); }
        catch (WorkItemExternalIdConflictException)
        {
            var concurrentConflict = await store.GetByExternalIdAsync(pid, externalId!, ct);
            var conflictDetail = concurrentConflict is not null
                ? $"for work item {concurrentConflict.Id} (state: {concurrentConflict.State})"
                : "(concurrent duplicate)";
            return Results.BadRequest(new
            {
                error = $"externalId '{externalId}' already exists in project '{pid}' {conflictDetail}"
            });
        }
        AuditLog.WorkItemCreated(item.Id, item.ProjectId, item.Title);

        // Re-read dep states after persisting to avoid TOCTOU: a dep may have
        // transitioned to terminal between the allItems snapshot and the commit.
        // Using fresh reads ensures we don't miss the initial enqueue.
        var freshDepStates = new Dictionary<WorkItemId, WorkItemState>();
        var freshDepExternalIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in dependsOnIds)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
            {
                freshDepStates[depId] = dep.State;
                freshDepExternalIds[depId] = dep.ExternalId;
            }
        }
        if (WorkItemDependencies.AreSatisfied(item.DependsOn, freshDepStates))
            await queue.EnqueueAsync(item.Id, ct);

        return Results.Created($"/workitems/{item.Id}", ToDto(item, project, freshDepStates, freshDepExternalIds));
    }

    private static async Task<IResult> ListAsync(IWorkItemStore store, IProjectRepository projects, CancellationToken ct)
    {
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);
        var allItems = new List<WorkItem>();
        await foreach (var item in store.ListAsync(ct)) allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);
        var externalIdsById = allItems.ToDictionary(i => i.Id, i => i.ExternalId);

        var list = allItems.Select(item =>
        {
            allProjects.TryGetValue(item.ProjectId.Value, out var p);
            var depExternalIds = item.DependsOn
                .Where(d => externalIdsById.TryGetValue(d, out _))
                .ToDictionary(d => d, d => externalIdsById[d]);
            return ToDto(item, p, statesById, depExternalIds);
        }).ToList();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(string id, IWorkItemStore store, IProjectRepository projects, CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        // Read only the dep states needed — avoids an O(N) full-store scan.
        var statesById = new Dictionary<WorkItemId, WorkItemState>();
        var depExternalIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in item!.DependsOn)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
            {
                statesById[depId] = dep.State;
                depExternalIds[depId] = dep.ExternalId;
            }
        }

        var project = await projects.GetAsync(item.ProjectId, ct);
        return Results.Ok(ToDto(item, project, statesById, depExternalIds));
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
        var (target, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var targetId = target!.Id;

        var allItems = new List<WorkItem>();
        await foreach (var item in store.ListAsync(ct)) allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);
        var externalIdsById = allItems.ToDictionary(i => i.Id, i => i.ExternalId);
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);

        var dependents = allItems
            .Where(item => item.DependsOn.Contains(targetId))
            .Select(item =>
            {
                allProjects.TryGetValue(item.ProjectId.Value, out var p);
                var depExternalIds = item.DependsOn
                    .Where(d => externalIdsById.ContainsKey(d))
                    .ToDictionary(d => d, d => externalIdsById[d]);
                return ToDto(item, p, statesById, depExternalIds);
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
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var workItemId = item!.Id;

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
            var present = await gitHost.RepositoryExistsAsync(workItemId, ct);
            if (!present)
                return Results.Conflict(new
                {
                    error = $"cannot retry from '{from}': bare repo for work item {workItemId} no longer exists",
                    hint = "retry with from=\"work\" to start over from a fresh clone"
                });
        }

        var resumed = item.With(resumeState.Value, error: null);
        await store.UpdateAsync(resumed, ct);
        AuditLog.WorkItemRetried(workItemId, from);
        await queue.EnqueueAsync(resumed.Id, ct);
        return Results.Accepted($"/workitems/{workItemId}", new { id = workItemId.ToString(), from, state = resumeState.Value.ToString() });
    }

    private static async Task<IResult> CancelAsync(
        string id,
        IWorkItemStore store,
        CancellationRegistry cancellations,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        ITimingStore? timings,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var workItemId = item!.Id;

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

            // Only delete timing rows when the pipeline was not active. If the
            // pipeline was running (wasActive=true) it races to Done; deleting
            // here could erase timing records for a successfully-completed item.
            if (timings is not null)
                await timings.DeleteByWorkItemAsync(workItemId, ct);
        }

        // Cascade: cancel all Queued items that (transitively) depend on this
        // one. In-flight items (non-Queued) are left to run their course.
        await CascadeCancelDependentsAsync(workItemId, store, ct);

        return Results.Accepted($"/workitems/{workItemId}");
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

    /// <summary>
    /// Partially update a Queued work item's title, prompt, and/or agent.
    /// Returns 409 Conflict when the item is no longer in Queued state.
    /// </summary>
    private static async Task<IResult> PatchWorkItemAsync(
        string id,
        PatchWorkItemRequest body,
        IWorkItemStore store,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var workItemId = item!.Id;

        if (item.State != WorkItemState.Queued)
            return Results.Conflict(new { error = $"cannot edit item in state {item.State}; only Queued items are editable" });

        var updated = item;

        if (body.Title is not null)
        {
            try { Validation.ValidateNoOptionLikeOrControl(body.Title, nameof(body.Title)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (body.Title.Length > 200) return Results.BadRequest(new { error = "title must be <= 200 chars" });
            updated = updated with { Title = body.Title, UpdatedAt = DateTimeOffset.UtcNow };
        }

        if (body.Prompt is not null)
        {
            if (body.Prompt.Length > 64 * 1024) return Results.BadRequest(new { error = "prompt must be <= 64KB" });
            updated = updated with { Prompt = body.Prompt, UpdatedAt = DateTimeOffset.UtcNow };
        }

        if (body.Agent is not null)
        {
            var kind = new AgentKind(body.Agent);
            if (!agents.TryGet(kind, out _))
                return Results.BadRequest(new { error = $"unknown agent '{body.Agent}'", available = agents.Available.Select(a => a.Value) });
            updated = updated with { Agent = kind, UpdatedAt = DateTimeOffset.UtcNow };
        }

        // TryUpdateIfStateAsync guards against a race where the orchestrator picks
        // up the item between the GetAsync above and this write.
        var written = await store.TryUpdateIfStateAsync(updated, WorkItemState.Queued, ct);
        if (!written)
            return Results.Conflict(new { error = "item transitioned out of Queued state before the update could be written" });

        AuditLog.WorkItemPatched(
            updated.Id,
            titleChanged: body.Title is not null,
            promptChanged: body.Prompt is not null,
            agentChanged: body.Agent is not null);

        var statesById = new Dictionary<WorkItemId, WorkItemState>();
        var depExternalIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in updated.DependsOn)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
            {
                statesById[depId] = dep.State;
                depExternalIds[depId] = dep.ExternalId;
            }
        }

        var project = await projects.GetAsync(updated.ProjectId, ct);
        return Results.Ok(ToDto(updated, project, statesById, depExternalIds));
    }

    /// <summary>
    /// Reorder the Queued items. The request body must list exactly the current
    /// set of Queued item IDs; any mismatch (stale view) is rejected with 400.
    /// </summary>
    private static async Task<IResult> ReorderWorkItemsAsync(
        ReorderWorkItemsRequest req,
        IWorkItemStore store,
        CancellationToken ct)
    {
        var rawIds = req.Ids ?? [];

        if (rawIds.Length > 1000)
            return Results.BadRequest(new { error = "ids array must contain at most 1000 items" });

        // Parse IDs
        var parsedIds = new List<WorkItemId>(rawIds.Length);
        foreach (var raw in rawIds)
        {
            if (!Guid.TryParse(raw, out var g))
                return Results.BadRequest(new { error = $"'{raw}' is not a valid work item id" });
            parsedIds.Add(new WorkItemId(g));
        }

        // Reject duplicates before set comparison so we don't silently deduplicate.
        var uniqueCount = new HashSet<WorkItemId>(parsedIds).Count;
        if (uniqueCount != parsedIds.Count)
            return Results.BadRequest(new { error = "ids must not contain duplicates" });

        // Fetch current Queued items
        var queuedItems = new List<WorkItem>();
        await foreach (var item in store.ListByStateAsync(WorkItemState.Queued, ct))
            queuedItems.Add(item);

        var queuedSet = new HashSet<WorkItemId>(queuedItems.Select(i => i.Id));
        var requestedSet = new HashSet<WorkItemId>(parsedIds);

        if (!queuedSet.SetEquals(requestedSet))
        {
            var missing = queuedSet.Except(requestedSet).Select(i => i.ToString()).ToList();
            var extra = requestedSet.Except(queuedSet).Select(i => i.ToString()).ToList();
            return Results.BadRequest(new
            {
                error = "provided ids do not exactly match the current Queued items (view is stale)",
                missingFromRequest = missing,
                unknownInRequest = extra,
            });
        }

        await store.ReorderAsync(parsedIds, ct);
        AuditLog.WorkItemReordered(parsedIds.Count);
        return Results.NoContent();
    }

    // ── Queue control ─────────────────────────────────────────────────────────

    private static IResult GetQueueStatusAsync(IQueueController queueController)
    {
        return Results.Ok(new
        {
            state = queueController.State.ToString(),
            pausedAt = queueController.PausedAt,
            pausedReason = queueController.PausedReason,
        });
    }

    private static async Task<IResult> PauseQueueAsync(
        PauseQueueRequest body,
        IQueueController queueController,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new { error = "reason is required" });
        if (body.Reason.Any(char.IsControl))
            return Results.BadRequest(new { error = "reason must not contain control characters" });
        if (body.Reason.Length > 500)
            return Results.BadRequest(new { error = "reason must be <= 500 chars" });

        await queueController.PauseAsync(body.Reason, ct);
        _ = webhooks.PublishAsync(new WebhookEvent
        {
            Event = "queue.paused",
            Details = new { pausedAt = queueController.PausedAt, reason = queueController.PausedReason, pausedBy = "api" },
        }, CancellationToken.None);
        return Results.Ok(new
        {
            state = queueController.State.ToString(),
            pausedAt = queueController.PausedAt,
            pausedReason = queueController.PausedReason,
        });
    }

    private static async Task<IResult> ResumeQueueAsync(
        IQueueController queueController,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        var wasRunning = queueController.State == QueueState.Running;
        await queueController.ResumeAsync(ct);
        if (!wasRunning)
        {
            _ = webhooks.PublishAsync(new WebhookEvent
            {
                Event = "queue.resumed",
                Details = new { resumedAt = DateTimeOffset.UtcNow },
            }, CancellationToken.None);
        }
        return Results.Ok(new
        {
            state = queueController.State.ToString(),
            pausedAt = queueController.PausedAt,
            pausedReason = queueController.PausedReason,
        });
    }

    // ── Budget usage ──────────────────────────────────────────────────────────

    private static async Task<IResult> GetBudgetUsageAsync(
        string id,
        IProjectRepository projects,
        IWorkItemStore store,
        CancellationToken ct)
    {
        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid project id" }); }
        var project = await projects.GetAsync(pid, ct);
        if (project is null) return Results.NotFound();

        var now = DateTimeOffset.UtcNow;
        var lastHour = await store.CountStartedInWindowAsync(pid, now.AddHours(-1), ct);
        var last24h = await store.CountStartedInWindowAsync(pid, now.AddHours(-24), ct);
        var inFlight = await store.CountInFlightAsync(pid, ct);

        return Results.Ok(new
        {
            lastHour,
            last24h,
            currentlyInFlight = inFlight,
            limits = new
            {
                perHour = project.Budget.MaxItemsPerHour,
                perDay = project.Budget.MaxItemsPerDay,
                concurrent = project.Budget.MaxConcurrentForProject,
            },
        });
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

    // ── Agent question endpoints ──────────────────────────────────────────────

    private static async Task<IResult> GetQuestionsAsync(
        string id,
        IWorkItemStore store,
        IWorkItemQuestionStore? questionStore,
        CancellationToken ct)
    {
        if (questionStore is null) return Results.Json(new { error = "question store not configured" }, statusCode: 503);
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var questions = await questionStore.ListByWorkItemAsync(item!.Id.ToString(), ct);
        return Results.Ok(questions.Select(q => new QuestionDto(
            q.Id, q.WorkItemId, q.QuestionId, q.QuestionText,
            q.State, q.AskedAt, q.AnsweredAt, q.AnswerText, q.AnsweredBy,
            q.DismissedAt, q.DismissReason)));
    }

    private static async Task<IResult> AnswerQuestionAsync(
        string id,
        AnswerQuestionRequest req,
        IWorkItemStore store,
        IWorkItemQuestionStore? questionStore,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (questionStore is null) return Results.Json(new { error = "question store not configured" }, statusCode: 503);
        if (string.IsNullOrWhiteSpace(req.QuestionId))
            return Results.BadRequest(new { error = "questionId is required" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(req.QuestionId, @"^[a-zA-Z0-9_-]{1,64}$"))
            return Results.BadRequest(new { error = "questionId must be 1-64 alphanumeric/hyphen/underscore characters" });
        if (string.IsNullOrWhiteSpace(req.Answer))
            return Results.BadRequest(new { error = "answer is required" });
        if (req.Answer.Length > 4000)
            return Results.BadRequest(new { error = "answer must be <= 4000 chars" });

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State != WorkItemState.NeedsOperatorInput)
            return Results.Conflict(new { error = "work item is not waiting for operator input" });

        var question = await questionStore.GetAsync(item.Id.ToString(), req.QuestionId, ct);
        if (question is null) return Results.NotFound(new { error = $"question '{req.QuestionId}' not found" });

        // Idempotent: answering an already-answered question is a no-op.
        if (question.State != "open")
            return Results.Ok(new { status = "no-op", questionState = question.State });

        var redactedAnswer = RawOutputRedactor.Redact(req.Answer);
        await questionStore.AnswerAsync(item.Id.ToString(), req.QuestionId, redactedAnswer, answeredBy: null, ct);

        var project = await projects.GetAsync(item.ProjectId, ct);
        await webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.question_answered",
            WorkItem = item,
            Project = project,
            Details = new QuestionAnsweredDetails(item.Id.ToString(), item.ProjectId.Value, req.QuestionId, redactedAnswer, AnsweredBy: null),
        }, ct);

        // Transition out of NeedsOperatorInput if all questions are now resolved.
        await MaybeResumeFromNeedsOperatorInputAsync(item, store, questionStore, queue, webhooks, project, ct);

        return Results.Ok(new { status = "answered" });
    }

    private static async Task<IResult> DismissQuestionAsync(
        string id,
        DismissQuestionRequest req,
        IWorkItemStore store,
        IWorkItemQuestionStore? questionStore,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (questionStore is null) return Results.Json(new { error = "question store not configured" }, statusCode: 503);
        if (string.IsNullOrWhiteSpace(req.QuestionId))
            return Results.BadRequest(new { error = "questionId is required" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(req.QuestionId, @"^[a-zA-Z0-9_-]{1,64}$"))
            return Results.BadRequest(new { error = "questionId must be 1-64 alphanumeric/hyphen/underscore characters" });
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Results.BadRequest(new { error = "reason is required" });
        if (req.Reason.Length > 500)
            return Results.BadRequest(new { error = "reason must be <= 500 chars" });

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State != WorkItemState.NeedsOperatorInput)
            return Results.Conflict(new { error = "work item is not waiting for operator input" });

        var question = await questionStore.GetAsync(item.Id.ToString(), req.QuestionId, ct);
        if (question is null) return Results.NotFound(new { error = $"question '{req.QuestionId}' not found" });

        if (question.State != "open")
            return Results.Ok(new { status = "no-op", questionState = question.State });

        var redactedReason = RawOutputRedactor.Redact(req.Reason);
        await questionStore.DismissAsync(item.Id.ToString(), req.QuestionId, redactedReason, ct);

        var project = await projects.GetAsync(item.ProjectId, ct);
        await webhooks.PublishAsync(new WebhookEvent
        {
            Event = "work_item.question_dismissed",
            WorkItem = item,
            Project = project,
            Details = new QuestionDismissedDetails(item.Id.ToString(), item.ProjectId.Value, req.QuestionId, redactedReason),
        }, ct);

        // Transition out of NeedsOperatorInput if all questions are now resolved.
        await MaybeResumeFromNeedsOperatorInputAsync(item, store, questionStore, queue, webhooks, project, ct);

        return Results.Ok(new { status = "dismissed" });
    }

    /// <summary>
    /// When a work item is in NeedsOperatorInput state and all its questions are now
    /// resolved (answered or dismissed), transitions back to WorkComplete and re-enqueues.
    /// </summary>
    private static async Task MaybeResumeFromNeedsOperatorInputAsync(
        WorkItem item,
        IWorkItemStore store,
        IWorkItemQuestionStore questionStore,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        Project? project,
        CancellationToken ct)
    {
        var current = await store.GetAsync(item.Id, ct) ?? item;
        if (current.State != WorkItemState.NeedsOperatorInput) return;

        var allQuestions = await questionStore.ListByWorkItemAsync(item.Id.ToString(), ct);
        var hasOpen = allQuestions.Any(q => q.State == "open");
        if (hasOpen) return;

        var resumed = current.With(WorkItemState.WorkComplete);
        var transitioned = await store.TryUpdateIfStateAsync(resumed, WorkItemState.NeedsOperatorInput, ct);
        if (!transitioned) return;
        AuditLog.WorkItemTransitioned(item.Id, "WorkComplete (resumed from NeedsOperatorInput)");
        await queue.EnqueueAsync(item.Id, ct);

        if (project is not null)
        {
            await webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.work_complete",
                WorkItem = resumed,
                Project = project,
            }, ct);
        }
    }

    // ── Work item resolver ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a route path segment to a <see cref="WorkItem"/>. Accepts either
    /// a UUID or a composite <c>projectId:externalId</c> form. Returns the item
    /// and a null error result on success, or a null item with an error result on failure.
    /// </summary>
    private static async Task<(WorkItem? item, IResult? error)> ResolveWorkItemAsync(
        string idSegment,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (idSegment.Contains(':'))
        {
            var colonIdx = idSegment.IndexOf(':');
            var projectPart = idSegment[..colonIdx];
            var externalPart = idSegment[(colonIdx + 1)..];
            if (string.IsNullOrEmpty(projectPart) || string.IsNullOrEmpty(externalPart))
                return (null, Results.BadRequest(new { error = "composite id format requires non-empty projectId and externalId: '<projectId>:<externalId>'" }));
            ProjectId pid;
            try { pid = new ProjectId(projectPart); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            try { Validation.ValidateExternalId(externalPart, "externalId"); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            var byExtId = await store.GetByExternalIdAsync(pid, externalPart, ct);
            return byExtId is null ? (null, Results.NotFound()) : (byExtId, null);
        }

        if (!Guid.TryParse(idSegment, out var g))
            return (null, Results.BadRequest(new { error = "invalid id" }));
        var byId = await store.GetAsync(new WorkItemId(g), ct);
        return byId is null ? (null, Results.NotFound()) : (byId, null);
    }

    private static WorkItemDto ToDto(
        WorkItem item,
        Project? project,
        IReadOnlyDictionary<WorkItemId, WorkItemState> statesById,
        IReadOnlyDictionary<WorkItemId, string?>? depExternalIds = null)
    {
        var depsSatisfied = WorkItemDependencies.AreSatisfied(item.DependsOn, statesById);
        var depExtIds = item.DependsOn.ToDictionary(
            d => d.ToString(),
            d => depExternalIds is not null && depExternalIds.TryGetValue(d, out var eid) ? eid : null);
        return new WorkItemDto(
            item.Id.ToString(),
            item.ExternalId,
            item.ProjectId.Value,
            item.Title,
            item.Prompt,
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
            depsSatisfied,
            depExtIds,
            item.QueuePosition);
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

    private static async Task<IResult> GetTimelineAsync(
        string id,
        string? kind,
        string? since,
        int? iteration,
        IWorkItemStore store,
        AuditLogTimelineReader timeline,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var workItemId = item!.Id;

        var isTerminal = item.State is
            WorkItemState.Done or WorkItemState.Failed or
            WorkItemState.Cancelled or WorkItemState.AuditFailed;

        var entries = await timeline.GetTimelineAsync(workItemId.ToString(), isTerminal, item.CreatedAt, ct);

        IEnumerable<TimelineEntry> filtered = entries;

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var kinds = kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(e => kinds.Contains(e.Kind, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(since) &&
            DateTimeOffset.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sinceDate))
        {
            filtered = filtered.Where(e => e.OccurredAt >= sinceDate);
        }

        if (iteration is { } iter)
        {
            filtered = filtered.Where(e =>
                e.Kind is "auditor_run" or "iteration_complete" &&
                e.Details is not null &&
                TryGetIterationFromDetails(e.Details, out var entryIter) &&
                entryIter == iter);
        }

        return Results.Ok(new WorkItemTimelineResponse(workItemId.ToString(), filtered.ToList()));
    }

    private static bool TryGetIterationFromDetails(object details, out int iteration)
    {
        iteration = 0;
        var json = System.Text.Json.JsonSerializer.Serialize(details);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("iteration", out var p) && p.TryGetInt32(out var i))
            {
                iteration = i;
                return true;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return false;
    }
}

public sealed record CreateWorkItemRequest(
    string ProjectId,
    string Title,
    string Prompt,
    string? Agent,
    string? AgentClassId,
    string? BaseBranch,
    string? WorkBranch,
    bool? PushUpstream,
    int? WorkTimeoutMinutes,
    int? MergeTimeoutMinutes,
    string? ExternalId = null,
    string[]? DependsOn = null);

public sealed record RetryWorkItemRequest(string? From);

public sealed record PatchWorkItemRequest(
    string? Title = null,
    string? Prompt = null,
    string? Agent = null);

public sealed record ReorderWorkItemsRequest(string[]? Ids = null);

public sealed record PauseQueueRequest(string Reason = "");

public sealed record WorkItemTimelineResponse(string WorkItemId, IReadOnlyList<TimelineEntry> Entries);

public sealed record WorkItemDto(
    string Id,
    string? ExternalId,
    string ProjectId,
    string Title,
    string Prompt,
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
    bool DependsOnSatisfied,
    IReadOnlyDictionary<string, string?> DependsOnExternalIds,
    long QueuePosition = 0);

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

public sealed record AnswerQuestionRequest(string QuestionId, string Answer);

public sealed record DismissQuestionRequest(string QuestionId, string Reason);

public sealed record QuestionDto(
    string Id,
    string WorkItemId,
    string QuestionId,
    string QuestionText,
    string State,
    DateTimeOffset AskedAt,
    DateTimeOffset? AnsweredAt,
    string? AnswerText,
    string? AnsweredBy,
    DateTimeOffset? DismissedAt,
    string? DismissReason);
