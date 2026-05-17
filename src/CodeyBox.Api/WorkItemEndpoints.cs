using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

internal static class WorkItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapPost("/", CreateAsync);
        group.MapPost("/reorder", ReorderWorkItemsAsync);
        group.MapPost("/{id}/retry", RetryAsync);
        group.MapPost("/{id}/replay", ReplayAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", CancelAsync);
        group.MapGet("/{id}/dependents", GetDependentsAsync);
        group.MapGet("/{id}/replays", GetReplaysAsync);
        group.MapPatch("/{id}", PatchWorkItemAsync);
        group.MapPatch("/{id}/priority", PatchPriorityAsync);
        group.MapGet("/{id}/timeline", GetTimelineAsync);
        group.MapGet("/{id}/questions", GetQuestionsAsync);
        group.MapPost("/{id}/answer", AnswerQuestionAsync);
        group.MapPost("/{id}/dismiss-question", DismissQuestionAsync);
        group.MapGet("/{id}/stdout-tail", GetStdoutTailAsync);
        group.MapPost("/{id}/uncancel", UncancelAsync);

        var projects = app.MapGroup("/projects");
        projects.MapGet("/", ListProjectsAsync);
        projects.MapGet("/{id}", GetProjectAsync);
        projects.MapGet("/{id}/budget/usage", GetBudgetUsageAsync);

        app.MapGet("/workers/status", GetWorkerStatusAsync);
        app.MapGet("/queue/status", GetQueueStatusAsync);
        app.MapPost("/queue/pause", PauseQueueAsync);
        app.MapPost("/queue/resume", ResumeQueueAsync);
    }

    private static async Task<IResult> GetWorkerStatusAsync(
        OrchestratorService orchestrator,
        CancellationToken ct)
    {
        var status = await orchestrator.GetStatusAsync(ct);
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
        IReleaseStore? releaseStore,
        IWebhookDispatcher webhooks,
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

        string? auditorProfile = null;
        if (!string.IsNullOrWhiteSpace(req.AuditorProfile))
        {
            auditorProfile = req.AuditorProfile.Trim();
            if (!AuditProfileExists(project.Audit, auditorProfile))
                return Results.BadRequest(new
                {
                    error = $"unknown auditorProfile '{auditorProfile}' for project '{pid}'",
                    availableProfiles = AvailableAuditProfiles(project.Audit),
                });
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

        // ── Release binding ───────────────────────────────────────────────────

        Release? boundRelease = null;
        ReleaseId? releaseId = null;
        if (!string.IsNullOrWhiteSpace(req.ReleaseId))
        {
            if (releaseStore is null)
                return Results.BadRequest(new { error = "release management is not available" });

            if (!ReleaseId.TryParse(req.ReleaseId, out var rid))
                return Results.BadRequest(new { error = "invalid releaseId" });

            var rel = await releaseStore.GetAsync(rid, ct);
            if (rel is null)
                return Results.NotFound(new { error = $"release '{req.ReleaseId}' not found" });
            if (rel.ProjectId != pid)
                return Results.BadRequest(new { error = "release belongs to a different project" });
            if (rel.State != ReleaseState.Open)
                return Results.BadRequest(new { error = $"release is {rel.State}; only Open releases accept new work items" });

            var relProject = project;
            if (relProject is not null && !relProject.ReleaseConfig.Enabled)
                return Results.BadRequest(new { error = $"release management is not enabled for project '{req.ProjectId}'" });

            boundRelease = rel;
            releaseId = rid;
        }

        // ── Build and persist ─────────────────────────────────────────────────

        string? agentClassId = null;
        if (!string.IsNullOrWhiteSpace(req.AgentClassId))
        {
            if (req.AgentClassId.Length > 200)
                return Results.BadRequest(new { error = "agentClassId must be <= 200 chars" });
            agentClassId = req.AgentClassId.Trim();
        }

        int priority = 0;
        if (req.Priority is { } p)
        {
            var priorityError = ValidatePriority(p, project);
            if (priorityError is not null) return priorityError;
            priority = p;
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
            AuditorProfile = auditorProfile,
            AgentClassId = agentClassId,
            PushUpstream = req.PushUpstream ?? true,
            DependsOn = dependsOnIds,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
            Priority = priority,
            ExternalId = externalId,
            ReleaseId = releaseId,
        };
        if (req.WorkTimeoutMinutes is { } w)
            item = item with { WorkTimeout = TimeSpan.FromMinutes(Math.Clamp(w, 1, 480)) };
        if (req.MergeTimeoutMinutes is { } m)
            item = item with { MergeTimeout = TimeSpan.FromMinutes(Math.Clamp(m, 1, 240)) };
        if (req.MinModelScore is { } minScore)
            item = item with { MinModelScore = Math.Clamp(minScore, 0, 200) };

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

        if (boundRelease is not null)
            await webhooks.PublishAsync(new WebhookEvent
            {
                Event = "release.work_item_added",
                WorkItem = item,
                Project = project,
                Release = boundRelease,
            }, ct);

        return Results.Created($"/workitems/{item.Id}", ToDto(item, project, freshDepStates, freshDepExternalIds));
    }

    private static async Task<IResult> ListAsync(
        IWorkItemStore store,
        IProjectRepository projects,
        IWorkItemCostStore? costs,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);
        var allItems = new List<WorkItem>();
        await foreach (var item in store.ListAsync(ct)) allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);
        var externalIdsById = allItems.ToDictionary(i => i.Id, i => i.ExternalId);

        // Batched cost lookup: one SQL round-trip total instead of N. The store's
        // SummariseManyAsync is keyed by work-item-id string and only contains
        // entries for items that have cost rows; missing → "usage unknown".
        var usageByItem = await TryGetUsageSummariesAsync(
            costs, allItems.Select(i => i.Id.ToString()).ToList(),
            loggerFactory.CreateLogger("CodeyBox.Api.WorkItemEndpoints"), ct);

        var list = new List<WorkItemDto>(allItems.Count);
        foreach (var item in allItems)
        {
            allProjects.TryGetValue(item.ProjectId.Value, out var p);
            var depExternalIds = item.DependsOn
                .Where(d => externalIdsById.TryGetValue(d, out _))
                .ToDictionary(d => d, d => externalIdsById[d]);
            usageByItem.TryGetValue(item.Id.ToString(), out var usage);
            list.Add(ToDto(item, p, statesById, depExternalIds, usage));
        }
        return Results.Ok(list);
    }

    private static async Task<IResult> GetAsync(
        string id,
        IWorkItemStore store,
        IProjectRepository projects,
        IWorkItemCostStore? costs,
        ILoggerFactory loggerFactory,
        IAgentFallbackHistoryStore? fallbackHistory,
        CancellationToken ct)
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
        var usage = await TryGetUsageSummaryAsync(
            costs, item.Id, loggerFactory.CreateLogger("CodeyBox.Api.WorkItemEndpoints"), ct);
        var dto = ToDto(item, project, statesById, depExternalIds, usage);
        if (fallbackHistory is not null)
        {
            var history = await fallbackHistory.ListByWorkItemAsync(item.Id, ct);
            if (history.Count > 0)
                dto = dto with { FallbackHistory = history.Select(MapFallback).ToList() };
        }
        return Results.Ok(dto);
    }

    private static async Task<WorkItemUsageSummary?> TryGetUsageSummaryAsync(
        IWorkItemCostStore? costs, WorkItemId workItemId, ILogger log, CancellationToken ct)
    {
        if (costs is null) return null;
        try { return await costs.SummariseAsync(workItemId.ToString(), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Cost: failed to summarise usage for work item {Id}; response will omit usage", workItemId);
            return null;
        }
    }

    private static async Task<IReadOnlyDictionary<string, WorkItemUsageSummary>> TryGetUsageSummariesAsync(
        IWorkItemCostStore? costs, IReadOnlyCollection<string> workItemIds, ILogger log, CancellationToken ct)
    {
        if (costs is null || workItemIds.Count == 0)
            return new Dictionary<string, WorkItemUsageSummary>(StringComparer.Ordinal);
        try { return await costs.SummariseManyAsync(workItemIds, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Cost: failed to summarise usage for {Count} work items; response will omit usage", workItemIds.Count);
            return new Dictionary<string, WorkItemUsageSummary>(StringComparer.Ordinal);
        }
    }

    private static AgentFallbackDto MapFallback(AgentFallbackRecord r) =>
        new(
            Id: r.Id.ToString(),
            Phase: r.Phase,
            Iteration: r.Iteration,
            FromAgent: r.FromAgent.Value,
            FromModel: r.FromModel,
            ToAgent: r.ToAgent?.Value,
            ToModel: r.ToModel,
            Reason: r.Reason,
            OccurredAt: r.OccurredAt);

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
        WorkItemRetrier retrier,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        // Only resume from terminal-failed states or the parked
        // WaitingForQuotaReset state (operator override of the scheduler).
        // Done items have nothing to retry; other non-terminal states would
        // race the pipeline.
        if (item!.State is not (WorkItemState.Failed or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed or WorkItemState.Cancelled
            or WorkItemState.AbandonedAfterRecoveryAttempts
            or WorkItemState.WaitingForQuotaReset))
            return Results.Conflict(new { error = $"cannot retry item in state {item.State}; only terminal-failed items can be retried" });

        var from = (body?.From ?? "work").Trim().ToLowerInvariant();
        var (success, error, resumeState, actualFrom) = await retrier.RetryAsync(item, from, trigger: "manual", ct);

        if (!success)
        {
            if (error!.Contains("no longer exists"))
                return Results.Conflict(new { error, hint = "retry with from=\"work\" to start over from a fresh clone" });

            return Results.Conflict(new { error });
        }

        return Results.Accepted(
            $"/workitems/{item.Id}",
            new { id = item.Id.ToString(), from, actualFrom = actualFrom!, state = resumeState!.Value.ToString() });
    }

    /// <summary>
    /// Create a replay of a terminal work item, optionally swapping the agent via agentClassId.
    /// The new item gets the same prompt, base branch, and dependsOn list; it runs
    /// independently with its own ID, work branch, and audit iterations.
    /// </summary>
    private static async Task<IResult> ReplayAsync(
        string id,
        ReplayWorkItemRequest? body,
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        var (source, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var terminalStates = new[]
        {
            WorkItemState.Done, WorkItemState.Failed,
            WorkItemState.AuditFailed, WorkItemState.MergeConflictResolutionFailed,
            WorkItemState.Cancelled,
        };
        if (!terminalStates.Contains(source!.State))
            return Results.BadRequest(new
            {
                error = $"cannot replay work item in state {source.State}; source must be in a terminal state (Done, Failed, AuditFailed, MergeConflictResolutionFailed, Cancelled)"
            });

        // Resolve agent override — null means keep the source's agent.
        AgentKind? agentOverride = source.Agent;
        string? agentClassOverride = source.AgentClassId;

        if (!string.IsNullOrWhiteSpace(body?.Agent))
        {
            var kind = new AgentKind(body.Agent);
            if (!agents.TryGet(kind, out _))
                return Results.BadRequest(new { error = $"unknown agent '{body.Agent}'", available = agents.Available.Select(a => a.Value) });
            agentOverride = kind;
            agentClassOverride = null; // agent-specific override clears class routing
        }

        if (!string.IsNullOrWhiteSpace(body?.AgentClassId))
        {
            if (body.AgentClassId.Length > 200)
                return Results.BadRequest(new { error = "agentClassId must be <= 200 chars" });
            agentClassOverride = body.AgentClassId.Trim();
            agentOverride = null; // class routing takes precedence
        }

        if (!string.IsNullOrWhiteSpace(body?.ModelId))
            return Results.BadRequest(new
            {
                error = "modelId is resolved at pickup from AgentMembership and cannot be set directly on a replay; use agentClassId to route via a class that specifies the target model"
            });

        // Resolve work branch: explicit > auto-generated.
        var newId = WorkItemId.New();
        string workBranch;
        if (!string.IsNullOrWhiteSpace(body?.WorkBranch))
        {
            try { Validation.ValidateBranchName(body.WorkBranch, nameof(body.WorkBranch)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            if (source.BaseBranch is not null &&
                string.Equals(body.WorkBranch, source.BaseBranch, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "workBranch must differ from baseBranch" });

            workBranch = body.WorkBranch;
        }
        else
        {
            var shortId = newId.Value.ToString("N")[..8];
            workBranch = source.WorkBranch is { Length: > 0 } wb
                ? $"{TruncateToGitBranchPrefix(wb)}-replay-{shortId}"
                : $"replay-{shortId}";
        }

        var project = await projects.GetAsync(source.ProjectId, ct);

        var replay = new WorkItem
        {
            Id = newId,
            ProjectId = source.ProjectId,
            Title = source.Title,
            Prompt = source.Prompt,
            BaseBranch = source.BaseBranch,
            WorkBranch = workBranch,
            Agent = agentOverride,
            AuditorProfile = source.AuditorProfile,
            AgentClassId = agentClassOverride,
            PushUpstream = source.PushUpstream,
            WorkTimeout = source.WorkTimeout,
            MergeTimeout = source.MergeTimeout,
            DependsOn = source.DependsOn,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
            ReplayOfWorkItemId = source.Id,
        };

        await store.CreateAsync(replay, ct);
        AuditLog.WorkItemCreated(replay.Id, replay.ProjectId, replay.Title);

        // Re-read dep states to decide whether to enqueue immediately.
        var depStates = new Dictionary<WorkItemId, WorkItemState>();
        var depExtIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in replay.DependsOn)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
            {
                depStates[depId] = dep.State;
                depExtIds[depId] = dep.ExternalId;
            }
        }
        if (WorkItemDependencies.AreSatisfied(replay.DependsOn, depStates))
            await queue.EnqueueAsync(replay.Id, ct);

        return Results.Created($"/workitems/{replay.Id}", ToDto(replay, project, depStates, depExtIds));
    }

    /// <summary>
    /// Returns the source work item and all its replays recursively (BFS)
    /// in chronological order at each level. When the given ID is itself a replay, that
    /// item becomes the "source" in the response and its own descendants are "replays".
    /// </summary>
    private static async Task<IResult> GetReplaysAsync(
        string id,
        IWorkItemStore store,
        IProjectRepository projects,
        IAuditReportStore reportStore,
        CancellationToken ct)
    {
        var (source, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);

        async Task<WorkItemDto> BuildDtoWithAuditAsync(WorkItem item)
        {
            allProjects.TryGetValue(item.ProjectId.Value, out var proj);

            // Fetch dependency states and external IDs individually to avoid a full table scan.
            var depStates = new Dictionary<WorkItemId, WorkItemState>();
            var depExtIds = new Dictionary<WorkItemId, string?>();
            foreach (var depId in item.DependsOn)
            {
                var dep = await store.GetAsync(depId, ct);
                if (dep is not null)
                {
                    depStates[depId] = dep.State;
                    depExtIds[depId] = dep.ExternalId;
                }
            }

            var dto = ToDto(item, proj, depStates, depExtIds);

            var reports = await reportStore.GetByWorkItemAsync(item.Id.ToString(), ct);
            if (reports.Count > 0)
            {
                var maxIter = reports.Max(r => r.Iteration);
                var iterCount = reports.Select(r => r.Iteration).Distinct().Count();
                var lastBlockingCount = reports
                    .Where(r => r.Iteration == maxIter)
                    .SelectMany(r => r.Findings)
                    .Count(f => string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                dto = dto with { AuditIterations = iterCount, FinalAuditBlockingFindings = lastBlockingCount };
            }
            return dto;
        }

        // BFS using ListByReplaySourceAsync — targeted per-source indexed queries, no full table scan.
        var replays = new List<WorkItemDto>();
        var toVisit = new Queue<WorkItemId>();
        toVisit.Enqueue(source!.Id);

        while (toVisit.Count > 0)
        {
            var current = toVisit.Dequeue();
            await foreach (var child in store.ListByReplaySourceAsync(current, ct))
            {
                replays.Add(await BuildDtoWithAuditAsync(child));
                toVisit.Enqueue(child.Id);
            }
        }

        return Results.Ok(new WorkItemReplaysResponse(await BuildDtoWithAuditAsync(source), replays));
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
            or WorkItemState.Cancelled or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts)
            return Results.Conflict(new { error = $"cannot cancel item in state {item.State}" });

        var wasActive = cancellations.Cancel(workItemId);
        if (!wasActive)
        {
            var cancelled = item.With(WorkItemState.Cancelled, "cancelled via API",
                WorkItemCancellationReason.OperatorRequested);
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

        // Orphan any replays: clear their replay_of link so they keep running
        // but are no longer linked to the (now-cancelled) source.
        await store.OrphanReplaysAsync(workItemId, ct);

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
            var cancelled = target.With(WorkItemState.Cancelled, "parent dependency cancelled",
                WorkItemCancellationReason.ParentCascaded);
            var updated = await store.TryUpdateIfStateAsync(cancelled, WorkItemState.Queued, ct);
            if (updated)
                AuditLog.WorkItemDependentCancelled(target.Id, cancelledId);
        }
    }

    /// <summary>
    /// Resets a Cancelled work item back to Queued so it will be retried.
    ///
    /// Returns 409 Conflict when:
    ///   - The item is not in Cancelled state.
    ///   - The cancellation was operator-requested (use POST /workitems with the
    ///     same body to re-create; respecting an explicit operator cancel is intentional).
    ///
    /// Succeeds for:
    ///   - Items with cancellation_reason = ParentCascaded (parent was since retried).
    ///   - Legacy items with cancellation_reason IS NULL (ambiguous; likely a host-shutdown
    ///     victim from before the no-shutdown-cancel fix was deployed).
    /// </summary>
    private static async Task<IResult> UncancelAsync(
        string id,
        IWorkItemStore store,
        ITaskQueue queue,
        IAgentStreamSummaryStore? streamSummaries,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State != WorkItemState.Cancelled)
            return Results.Conflict(new
            {
                error = $"cannot uncancel item in state {item.State}; only Cancelled items can be uncancelled",
            });

        if (item.CancellationReason == WorkItemCancellationReason.OperatorRequested)
            return Results.Conflict(new
            {
                error = "cannot uncancel an operator-requested cancellation; use POST /workitems with the same body to re-create the work item",
            });

        var requeued = item.With(WorkItemState.Queued) with { RecoveryAttempts = 0 };
        var updated = await store.TryUpdateIfStateAsync(requeued, WorkItemState.Cancelled, ct);
        if (!updated)
            return Results.Conflict(new { error = "concurrent uncancel request already processed this item" });
        if (streamSummaries is not null)
            await streamSummaries.DeleteByWorkItemAsync(requeued.Id, ct);
        await queue.EnqueueAsync(requeued.Id, ct);
        AuditLog.WorkItemRetried(requeued.Id, "uncancel");

        return Results.Ok(new { id = requeued.Id.ToString(), state = requeued.State.ToString() });
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
    /// Update the dispatch priority of a work item. Allowed for non-terminal
    /// states; only affects pickup order while the item is still Queued —
    /// in-flight items run to terminal state regardless of priority changes.
    /// Terminal items (Done / Failed / Cancelled / AuditFailed /
    /// MergeConflictResolutionFailed / AbandonedAfterRecoveryAttempts) reject
    /// with 409 because priority cannot affect them and silently mutating
    /// closed history is undesirable. The write goes through a partial UPDATE
    /// touching only the priority and updated_at columns, so a concurrent
    /// worker picking the item up between the read and the write is not
    /// stomped (TOCTOU-safe).
    /// </summary>
    private static async Task<IResult> PatchPriorityAsync(
        string id,
        PatchPriorityRequest body,
        IWorkItemStore store,
        IProjectRepository projects,
        ITaskQueue queue,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var project = await projects.GetAsync(item!.ProjectId, ct);
        if (project is null)
            return Results.BadRequest(new { error = $"unknown project '{item.ProjectId}'" });

        var priorityError = ValidatePriority(body.Priority, project);
        if (priorityError is not null) return priorityError;

        if (WorkItemDependencies.TerminalStates.Contains(item.State))
            return Results.Conflict(new
            {
                error = $"cannot change priority of work item in terminal state '{item.State}'",
            });

        if (item.Priority == body.Priority)
            return Results.Ok(new { id = item.Id.ToString(), priority = body.Priority, status = "no-op" });

        var result = await store.UpdatePriorityAsync(item.Id, body.Priority, DateTimeOffset.UtcNow, ct);
        switch (result.Outcome)
        {
            case PriorityUpdateOutcome.NotFound:
                return Results.NotFound(new { error = $"work item '{id}' no longer exists" });
            case PriorityUpdateOutcome.TerminalState:
                // The item raced into a terminal state between the read above and
                // the partial UPDATE; surface 409 like the pre-check would have.
                return Results.Conflict(new
                {
                    error = $"work item transitioned to terminal state '{result.Item!.State}' before priority could be updated",
                });
            case PriorityUpdateOutcome.Updated:
                break;
            default:
                throw new InvalidOperationException($"Unexpected priority update outcome '{result.Outcome}'.");
        }

        var updated = result.Item!;
        AuditLog.WorkItemPriorityChanged(updated.Id, result.OldPriority!.Value, updated.Priority);

        // Kick the dispatcher so the new ordering is picked up immediately when the
        // item is still Queued. Harmless for in-flight items: the dispatch loop will
        // re-pick from the store and find the highest-priority eligible item.
        if (updated.State == WorkItemState.Queued)
            await queue.EnqueueAsync(updated.Id, ct);

        return Results.Ok(new { id = updated.Id.ToString(), priority = updated.Priority });
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

    // Git branch names have a 255-byte UTF-8 limit. The auto-generated suffix "-replay-{8hex}" is 17 bytes,
    // so the prefix may be at most 238 bytes.
    private static string TruncateToGitBranchPrefix(string branch)
    {
        const int maxPrefixBytes = 255 - 17;
        if (System.Text.Encoding.UTF8.GetByteCount(branch) <= maxPrefixBytes) return branch;
        var len = branch.Length;
        while (len > 0 && System.Text.Encoding.UTF8.GetByteCount(branch.AsSpan(0, len)) > maxPrefixBytes)
            len--;
        return branch[..len];
    }

    private static WorkItemDto ToDto(
        WorkItem item,
        Project? project,
        IReadOnlyDictionary<WorkItemId, WorkItemState> statesById,
        IReadOnlyDictionary<WorkItemId, string?>? depExternalIds = null,
        WorkItemUsageSummary? usage = null)
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
            item.AuditorProfile,
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
            item.QueuePosition,
            item.ReplayOfWorkItemId?.ToString(),
            item.AgentClassId,
            MergeSha: item.MergeSha,
            MinModelScore: item.MinModelScore,
            ReleaseId: item.ReleaseId?.ToString(),
            FailureKind: item.FailureKind,
            QuotaResetAt: item.QuotaResetAt,
            NextQuotaRetryAt: item.NextQuotaRetryAt,
            QuotaRetryAttempts: item.QuotaRetryAttempts,
            Usage: usage?.Iteration,
            UsageTotal: usage?.Total,
            Priority: item.Priority);
    }

    private static ProjectDto ToProjectDto(Project p)
    {
        var audit = p.Audit.ResolveProfile();
        return new ProjectDto(
            p.Id.Value,
            p.DisplayName,
            p.RepositoryUrl,
            p.DefaultBaseBranch,
            p.DefaultAgent.Value,
            p.Upstream.Kind,
            audit.Languages,
            audit.AuditTypes,
            audit.MaxIterations);
    }

    private const int GlobalMinPriority = -1000;
    private const int GlobalMaxPriority = 1000;

    /// <summary>
    /// Validates a requested priority against the global cap and the project's
    /// per-project ceiling. Returns null on success or a 400 result on failure.
    /// </summary>
    private static IResult? ValidatePriority(int priority, Project project)
    {
        if (priority < GlobalMinPriority || priority > GlobalMaxPriority)
            return Results.BadRequest(new
            {
                error = $"priority must be within [{GlobalMinPriority}, {GlobalMaxPriority}]",
            });
        if (project.MaxPriority is { } maxPriority && priority > maxPriority)
            return Results.BadRequest(new
            {
                error = $"priority {priority} exceeds project '{project.Id}' max priority {maxPriority}",
            });
        return null;
    }

    private static bool AuditProfileExists(ProjectAudit audit, string profile)
        => profile.Equals(ProjectAudit.DefaultProfileName, StringComparison.OrdinalIgnoreCase)
           || audit.Profiles.ContainsKey(profile);

    private static IReadOnlyList<string> AvailableAuditProfiles(ProjectAudit audit)
    {
        var profiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProjectAudit.DefaultProfileName,
        };
        foreach (var profile in audit.Profiles.Keys)
            profiles.Add(profile);
        return profiles.ToList();
    }

    private static async Task<IResult> GetStdoutTailAsync(
        string id,
        IWorkItemStore store,
        IStdoutBroadcaster broadcaster,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var tail = broadcaster.GetTail(item!.Id);
        if (tail is null)
            return Results.Text("", "text/plain");  // Work item exists but no live stream data yet.

        return Results.Text(tail, "text/plain");
    }

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
            WorkItemState.Cancelled or WorkItemState.AuditFailed or
            WorkItemState.MergeConflictResolutionFailed or
            WorkItemState.AbandonedAfterRecoveryAttempts;

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
    string? AuditorProfile,
    string? AgentClassId,
    string? BaseBranch,
    string? WorkBranch,
    bool? PushUpstream,
    int? WorkTimeoutMinutes,
    int? MergeTimeoutMinutes,
    string? ExternalId = null,
    string[]? DependsOn = null,
    int? MinModelScore = null,
    string? ReleaseId = null,
    int? Priority = null);

public sealed record RetryWorkItemRequest(string? From);

public sealed record PatchWorkItemRequest(
    string? Title = null,
    string? Prompt = null,
    string? Agent = null);

public sealed record PatchPriorityRequest(int Priority);

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
    string? AuditorProfile,
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
    long QueuePosition = 0,
    string? ReplayOfWorkItemId = null,
    string? AgentClassId = null,
    int? AuditIterations = null,
    int? FinalAuditBlockingFindings = null,
    string? MergeSha = null,
    int MinModelScore = 95,
    string? ReleaseId = null,
    string? FailureKind = null,
    DateTimeOffset? QuotaResetAt = null,
    DateTimeOffset? NextQuotaRetryAt = null,
    int QuotaRetryAttempts = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkItemIterationUsage? Usage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkItemUsageTotal? UsageTotal = null,
    IReadOnlyList<AgentFallbackDto>? FallbackHistory = null,
    int Priority = 0);

public sealed record AgentFallbackDto(
    string Id,
    string Phase,
    int? Iteration,
    string FromAgent,
    string? FromModel,
    string? ToAgent,
    string? ToModel,
    string Reason,
    DateTimeOffset OccurredAt);

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
public sealed record ReplayWorkItemRequest(
    string? Agent = null,
    string? ModelId = null,
    string? AgentClassId = null,
    string? WorkBranch = null);

public sealed record WorkItemReplaysResponse(
    WorkItemDto Source,
    IReadOnlyList<WorkItemDto> Replays);
