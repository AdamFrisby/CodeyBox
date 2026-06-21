using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

internal static class WorkItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/workitems");
        group.MapPost("/", CreateAsync);
        group.MapPost("/reorder", ReorderWorkItemsAsync);
        group.MapPost("/{id}/abandon", AbandonAsync);
        group.MapPost("/{id}/promote", PromoteAsync);
        group.MapPost("/{id}/retry", RetryAsync);
        group.MapPost("/{id}/replay", ReplayAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapDelete("/{id}", CancelAsync);
        group.MapGet("/{id}/dependents", GetDependentsAsync);
        group.MapGet("/{id}/agent-history", GetAgentHistoryAsync);
        group.MapGet("/{id}/replays", GetReplaysAsync);
        group.MapPatch("/{id}", PatchWorkItemAsync);
        group.MapPatch("/{id}/external-ids", PatchExternalIdsAsync);
        group.MapPut("/{id}/prompt", PutPromptAsync);
        group.MapPatch("/{id}/priority", PatchPriorityAsync);
        group.MapGet("/{id}/timeline", GetTimelineAsync);
        group.MapGet("/{id}/questions", GetQuestionsAsync);
        group.MapPost("/{id}/answer", AnswerQuestionAsync);
        group.MapPost("/{id}/dismiss-question", DismissQuestionAsync);
        group.MapGet("/{id}/stdout-tail", GetStdoutTailAsync);
        group.MapPost("/{id}/uncancel", UncancelAsync);
        group.MapPost("/{id}/resume", ResumeAsync);
        group.MapPost("/{id}/recover", RecoverAsync);

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
        WorkItemCreationService creation,
        CancellationToken ct)
    {
        var prepared = await creation.PrepareAsync(req, ct);
        if (prepared.Error is not null) return prepared.Error;

        var committed = await creation.CommitAsync(prepared.Prepared!, ct);
        if (committed.Error is not null) return committed.Error;

        return Results.Created(
            $"/workitems/{committed.Item.Id}",
            ToDto(committed.Item, committed.Project, committed.DependencyStates, committed.DependencyExternalIds));
    }

    private static async Task<IResult> ListAsync(
        IWorkItemStore store,
        IProjectRepository projects,
        IWorkItemCostStore? costs,
        ILoggerFactory loggerFactory,
        string? externalId,
        string? projectId,
        CancellationToken ct)
    {
        var allProjects = (await projects.ListAsync(ct)).ToDictionary(p => p.Id.Value);
        var allItems = new List<WorkItem>();
        await foreach (var item in store.ListAsync(ct)) allItems.Add(item);

        // ?externalId=ns:val filter (matches the namespaced PATCH/POST surface).
        // Also accepts a bare value: returns every item that carries the value
        // in any namespace, leaving the caller to disambiguate by namespace.
        // Optional ?projectId=… narrows further when set.
        if (!string.IsNullOrEmpty(externalId))
        {
            if (Validation.TryParseNamespacedExternalId(externalId, out var filterNs, out var filterValue) && filterNs is not null)
            {
                allItems = allItems
                    .Where(i => i.ExternalIds.TryGetValue(filterNs, out var v) && v == filterValue)
                    .ToList();
            }
            else
            {
                allItems = allItems
                    .Where(i => i.ExternalIds.Values.Any(v => v == externalId))
                    .ToList();
            }
        }
        if (!string.IsNullOrEmpty(projectId))
            allItems = allItems.Where(i => i.ProjectId.Value == projectId).ToList();

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
        // [FromServices] + nullable makes this a genuinely OPTIONAL dependency:
        // when no involvement store is registered the framework binds null
        // (rather than failing endpoint construction with inferred-body), so the
        // "feature disabled → omit agentHistory/workAgent" branch below is real
        // and testable, not dead code.
        [FromServices] IAgentInvolvementStore? involvement,
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
        var iterations = await store.GetIterationsAsync(item.Id, ct);
        var dto = ToDto(item, project, statesById, depExternalIds, usage,
            iterations: iterations.Count > 0 ? iterations : null);
        if (fallbackHistory is not null)
        {
            // Always emit a list (possibly empty) when the store is wired, so
            // consumers can distinguish "no fallback happened" ([]) from "data
            // never fetched / store unavailable" (null on listing endpoints).
            var history = await fallbackHistory.ListByWorkItemAsync(item.Id, ct);
            dto = dto with
            {
                FallbackHistory = history.Count > 0
                    ? history.Select(MapFallback).ToList()
                    : Array.Empty<AgentFallbackDto>(),
            };
        }
        if (involvement is not null)
        {
            // Always emit a list (possibly empty) when the store is wired so
            // consumers distinguish "no agent ran yet / history started
            // post-migration" ([]) from "store unavailable" (omitted). WorkAgent
            // is the original implementer, derived from the successful Work entry.
            var involvementHistory = await involvement.ListByWorkItemAsync(item.Id, ct);
            dto = dto with
            {
                // Select(...).ToList() already yields an empty (non-null) list for an
                // empty trail, so [] still distinguishes "no agent ran yet" from the
                // store-unwired case above (where AgentHistory is left null/omitted).
                AgentHistory = involvementHistory.Select(MapInvolvement).ToList(),
                WorkAgent = ResolveWorkAgent(involvementHistory),
            };
        }
        return Results.Ok(dto);
    }

    /// <summary>
    /// GET /workitems/{id}/agent-history — the per-phase agent involvement trail
    /// alone. Cheaper than the full <c>GET /workitems/{id}</c> for UI polling.
    /// </summary>
    private static async Task<IResult> GetAgentHistoryAsync(
        string id,
        IWorkItemStore store,
        // See GetAsync: [FromServices] + nullable = optional dependency, so the
        // store-unwired branch binds null instead of breaking endpoint setup.
        [FromServices] IAgentInvolvementStore? involvement,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        // Store unwired → omit WorkAgent/AgentHistory entirely (feature disabled),
        // matching the full GET handler. Store wired → always emit a list (possibly
        // empty) so [] means "no agent has run yet", not "unavailable".
        if (involvement is null)
            return Results.Ok(new WorkItemAgentHistoryResponse(
                WorkItemId: item!.Id.ToString(), WorkAgent: null, AgentHistory: null));

        var history = await involvement.ListByWorkItemAsync(item!.Id, ct);
        return Results.Ok(new WorkItemAgentHistoryResponse(
            WorkItemId: item.Id.ToString(),
            WorkAgent: ResolveWorkAgent(history),
            AgentHistory: history.Select(MapInvolvement).ToList()));
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

    private static AgentInvolvementDto MapInvolvement(AgentInvolvement r) =>
        new(
            Id: r.Id.ToString(),
            AgentKind: r.AgentKind.Value,
            ModelId: r.ModelId,
            Phase: r.Phase,
            StartedAt: r.StartedAt,
            EndedAt: r.EndedAt,
            Iteration: r.Iteration,
            Outcome: r.Outcome,
            AgentInstanceId: r.AgentInstanceId);

    /// <summary>
    /// The agent that ran the original implementation. Distinct from
    /// <see cref="WorkItem.Agent"/>, which reflects whichever phase is current.
    /// Phase match is case-insensitive ("work" vs "Work").
    /// <para>
    /// A work-phase quota/timeout fallback records the exhausted attempt first
    /// (e.g. codex <c>failure:quota</c>) and then the successor that actually
    /// produced the implementation (e.g. claude <c>success</c>). Returning the
    /// first row would re-introduce the exact mis-attribution this feature
    /// exists to fix, so prefer the work row that finished successfully. Fall
    /// back to the first work attempt only while none has succeeded yet (still
    /// in progress, or every attempt failed).
    /// </para>
    /// </summary>
    private static string? ResolveWorkAgent(IReadOnlyList<AgentInvolvement> history)
    {
        AgentInvolvement? firstWork = null;
        foreach (var h in history)
        {
            if (!string.Equals(h.Phase, "work", StringComparison.OrdinalIgnoreCase)) continue;
            firstWork ??= h;
            if (string.Equals(h.Outcome, "success", StringComparison.Ordinal))
                return h.AgentKind.Value;
        }
        return firstWork?.AgentKind.Value;
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
            OccurredAt: r.OccurredAt,
            FromInstanceId: r.FromInstanceId,
            ToInstanceId: r.ToInstanceId);

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
    /// Retry a terminal-failed or operator-parked work item from a specific phase. Resets the
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

        // Only resume from terminal-failed states or parked states
        // (NeedsOperatorInput for operator triage, WaitingForQuotaReset /
        // WaitingForTransientRetry for operator override of the schedulers,
        // WaitingForAgentResume for operator override of per-agent runtime
        // pause controls).
        // Done items have nothing to retry; other non-terminal states would
        // race the pipeline.
        if (item!.State is not (WorkItemState.Failed or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed or WorkItemState.Cancelled
            or WorkItemState.AbandonedAfterRecoveryAttempts
            or WorkItemState.NeedsOperatorInput
            or WorkItemState.WaitingForQuotaReset
            or WorkItemState.WaitingForAgentResume
            or WorkItemState.WaitingForTransientRetry))
            return Results.Conflict(new { error = $"cannot retry item in state {item.State}; only terminal-failed or operator-parked items can be retried" });

        // Pass body.From through verbatim (including null) so the retrier can
        // auto-pick when the operator didn't specify a phase — defaulting at
        // the API layer would erase that signal. The echoed `from` field in
        // the response reflects what was requested ("auto" when unspecified)
        // so operators can distinguish auto-pick from an explicit choice;
        // `actualFrom` reflects the phase actually resumed from.
        var requestedFrom = string.IsNullOrWhiteSpace(body?.From)
            ? null
            : body!.From!.Trim().ToLowerInvariant();
        var (success, error, resumeState, actualFrom, openQuestions) = await retrier.RetryAsync(
            item,
            requestedFrom,
            trigger: "manual",
            ct: ct);

        if (!success)
        {
            if (openQuestions is { Count: > 0 })
                return Results.Conflict(new { error, openQuestions });

            if (error!.Contains("no longer exists"))
                return Results.Conflict(new { error, hint = "retry with from=\"work\" to start over from a fresh clone" });

            return Results.Conflict(new { error });
        }

        return Results.Accepted(
            $"/workitems/{item.Id}",
            new { id = item.Id.ToString(), from = requestedFrom ?? "auto", actualFrom = actualFrom!, state = resumeState!.Value.ToString() });
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
            MinModelScore = source.MinModelScore,
            RequiredCapabilities = source.RequiredCapabilities,
            Knobs = source.Knobs,
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
        string? reason,
        string? resolutionSha,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var workItemId = item!.Id;

        // Validate optional close-out metadata. Same shape as /resume's reason
        // guard (no control chars, ≤500 chars); resolutionSha is a Git-shaped
        // hex SHA so triage tooling can link the manual-resolution commit.
        if (reason is not null)
        {
            if (reason.Any(char.IsControl))
                return Results.BadRequest(new { error = "reason must not contain control characters" });
            if (reason.Length > 500)
                return Results.BadRequest(new { error = "reason must be <= 500 chars" });
        }
        if (resolutionSha is not null)
        {
            if (resolutionSha.Length is < 7 or > 40
                || !resolutionSha.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return Results.BadRequest(new { error = "resolutionSha must be a 7-40 character hex string" });
        }

        // Bookkeeping close-out path: when an operator resolves a terminal-failure
        // item out-of-band (e.g. manually merges after MergeConflictResolutionFailed),
        // DELETE used to 409 — leaving the item stranded forever. Transition it to
        // Cancelled with the same OperatorRequested reason as the in-flight path so
        // there is a single terminal-closed shape regardless of how it got there.
        if (IsTerminalFailureCloseable(item.State))
        {
            var priorState = item.State;
            var lastError = BuildCloseLastError(priorState, reason, resolutionSha);
            var closed = item.With(WorkItemState.Cancelled, lastError,
                WorkItemCancellationReason.OperatorRequested);
            await store.UpdateAsync(closed, ct);
            AuditLog.WorkItemCancelled(workItemId);
            var project = await projects.GetAsync(item.ProjectId, ct);
            if (project is not null)
                await webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.cancelled",
                    WorkItem = closed,
                    Project = project,
                    Details = new
                    {
                        priorState = priorState.ToString(),
                        reason,
                        resolutionSha,
                    },
                }, ct);
            return Results.Accepted($"/workitems/{workItemId}");
        }

        // Idempotent close: an already-cancelled item is a no-op rather than 409.
        // Lets operator scripts and the audit UI retry DELETE safely.
        if (item.State == WorkItemState.Cancelled)
            return Results.Accepted($"/workitems/{workItemId}");

        if (item.State == WorkItemState.Done)
            return Results.Conflict(new { error = $"cannot cancel item in state {item.State}" });

        var wasActive = cancellations.Cancel(workItemId);
        if (!wasActive)
        {
            var lastError = BuildCloseLastError(item.State, reason, resolutionSha)
                ?? "cancelled via API";
            var cancelled = item.With(WorkItemState.Cancelled, lastError,
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
                    Details = reason is null && resolutionSha is null ? null : new
                    {
                        priorState = item.State.ToString(),
                        reason,
                        resolutionSha,
                    },
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

    private static bool IsTerminalFailureCloseable(WorkItemState state) =>
        state is WorkItemState.Failed
            or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts;

    private static string? BuildCloseLastError(WorkItemState priorState, string? reason, string? resolutionSha)
    {
        if (reason is null && resolutionSha is null) return null;
        var prefix = $"closed by operator from {priorState}";
        if (resolutionSha is not null) prefix += $" (resolution-sha={resolutionSha})";
        return reason is null ? prefix : $"{prefix}: {reason}";
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

        var requeued = item.With(WorkItemState.Queued) with
        {
            RecoveryAttempts = 0,
            RecoveryAttemptSourceState = null,
        };
        var updated = await store.TryUpdateIfStateAsync(requeued, WorkItemState.Cancelled, ct);
        if (!updated)
            return Results.Conflict(new { error = "concurrent uncancel request already processed this item" });
        if (streamSummaries is not null)
            await streamSummaries.DeleteByWorkItemAsync(requeued.Id, ct);
        await queue.EnqueueAsync(requeued.Id, ct);
        AuditLog.WorkItemRetried(requeued.Id, "uncancel");

        return Results.Ok(new { id = requeued.Id.ToString(), state = requeued.State.ToString() });
    }

    private static async Task<IResult> AbandonAsync(
        string id,
        IWorkItemStore store,
        IAgentStreamSummaryStore? streamSummaries,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State == WorkItemState.Done)
            return Results.Conflict(new { error = $"cannot abandon item in state {item.State}" });

        if (item.State == WorkItemState.AbandonedAfterRecoveryAttempts)
            return Results.Ok(new { id = item.Id.ToString(), state = item.State.ToString() });

        if (item.State is WorkItemState.Working
            or WorkItemState.WorkComplete
            or WorkItemState.Auditing
            or WorkItemState.Reworking
            or WorkItemState.AuditPassed
            or WorkItemState.Merging
            or WorkItemState.Merged
            or WorkItemState.UpstreamPushing
            or WorkItemState.ReworkingForConflict)
        {
            return Results.Conflict(new
            {
                error = $"cannot abandon in-flight item in state {item.State}; cancel it first",
            });
        }

        var abandoned = item.With(
            WorkItemState.AbandonedAfterRecoveryAttempts,
            "abandoned via API");
        var updated = await store.TryUpdateIfStateAndUpdatedAtAsync(
            abandoned,
            item.State,
            item.UpdatedAt,
            ct);
        if (!updated)
            return Results.Conflict(new { error = "work item changed before it could be abandoned; retry the request" });

        if (streamSummaries is not null)
            await streamSummaries.DeleteByWorkItemAsync(abandoned.Id, ct);
        AuditLog.WorkItemTransitioned(abandoned.Id, abandoned.State.ToString());

        return Results.Ok(new { id = abandoned.Id.ToString(), state = abandoned.State.ToString() });
    }

    /// <summary>
    /// Resume an operator-cancelled work item against its existing bare repo
    /// and work-branch — preserving every agent commit already made — instead
    /// of re-doing the work via /replay. The operator's <c>DELETE</c> is
    /// undone, the audit-iteration counter continues from where it stopped,
    /// and the pipeline re-enters at the requested phase.
    ///
    /// Distinct from <c>/uncancel</c> (which refuses operator cancels) and
    /// <c>/retry</c> (which is scoped to terminal-failed states). Returns:
    ///   - 400 if 'from' is not one of work/audit/merge or the reason violates
    ///     the length/control-character guard shared with /queue/pause.
    ///   - 409 if the item is not in Cancelled state, or if from=audit/merge
    ///     was requested but no durable audit progress exists (work-branch never
    ///     reached an auditable state).
    ///   - 412 if the bare repo or the work-branch ref is no longer present
    ///     (resume cannot reconstruct the prior agent work; the operator must
    ///     fall back to /replay).
    ///
    /// Orchestration (validation, precondition checks, atomic update, audit
    /// log emit, queue kick) lives in <see cref="WorkItemRetrier.ResumeAsync"/>
    /// so the API does not depend on <see cref="IGitHost"/> or duplicate the
    /// retry-path logic.
    /// </summary>
    private static async Task<IResult> ResumeAsync(
        string id,
        ResumeWorkItemRequest? body,
        IWorkItemStore store,
        WorkItemRetrier retrier,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var reason = body?.Reason;
        if (reason is not null)
        {
            if (reason.Any(char.IsControl))
                return Results.BadRequest(new { error = "reason must not contain control characters" });
            if (reason.Length > 500)
                return Results.BadRequest(new { error = "reason must be <= 500 chars" });
        }

        var outcome = await retrier.ResumeAsync(item!, body?.From ?? "work", reason, ct);

        switch (outcome.Status)
        {
            case WorkItemRetrier.ResumeStatus.BadRequest:
                return Results.BadRequest(new { error = outcome.Error });
            case WorkItemRetrier.ResumeStatus.Conflict:
                return Results.Conflict(new { error = outcome.Error });
            case WorkItemRetrier.ResumeStatus.PreconditionFailed:
                return Results.Json(new { error = outcome.Error },
                    statusCode: StatusCodes.Status412PreconditionFailed);
        }

        var resumed = outcome.Resumed!;
        var requestedFrom = (body?.From ?? "work").Trim().ToLowerInvariant();

        var project = await projects.GetAsync(resumed.ProjectId, ct);
        if (project is not null)
        {
            await webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.resumed",
                WorkItem = resumed,
                Project = project,
                Details = new
                {
                    id = resumed.Id.ToString(),
                    externalId = resumed.ExternalId,
                    externalIds = resumed.ExternalIds,
                    from = requestedFrom,
                    reason = reason,
                },
            }, ct);
        }

        return Results.Ok(new
        {
            id = resumed.Id.ToString(),
            from = requestedFrom,
            state = resumed.State.ToString(),
        });
    }

    private static async Task<IResult> PromoteAsync(
        string id,
        IWorkItemStore store,
        IProjectRepository projects,
        ITaskQueue queue,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State != WorkItemState.Queued)
            return Results.Conflict(new { error = $"cannot promote item in state {item.State}; only Queued items can be promoted" });

        var project = await projects.GetAsync(item.ProjectId, ct);
        if (project is null)
            return Results.BadRequest(new { error = $"unknown project '{item.ProjectId}'" });

        var highestAllowedPriority = project.MaxPriority is { } cap
            ? Math.Min(cap, GlobalMaxPriority)
            : GlobalMaxPriority;
        var promotedPriority = Math.Max(item.Priority, highestAllowedPriority);
        if (item.Priority == promotedPriority)
            return Results.Ok(new { id = item.Id.ToString(), state = item.State.ToString() });

        var result = await store.UpdatePriorityIfStateAsync(
            item.Id,
            promotedPriority,
            DateTimeOffset.UtcNow,
            WorkItemState.Queued,
            ct);
        switch (result.Outcome)
        {
            case PriorityUpdateOutcome.NotFound:
                return Results.NotFound(new { error = $"work item '{id}' no longer exists" });
            case PriorityUpdateOutcome.TerminalState:
                return Results.Conflict(new
                {
                    error = $"work item transitioned to terminal state '{result.Item!.State}' before it could be promoted",
                });
            case PriorityUpdateOutcome.StateMismatch:
                return Results.Conflict(new
                {
                    error = $"work item transitioned to state '{result.Item!.State}' before it could be promoted",
                });
            case PriorityUpdateOutcome.Updated:
                break;
            default:
                throw new InvalidOperationException($"Unexpected priority update outcome '{result.Outcome}'.");
        }

        var promoted = result.Item!;
        AuditLog.WorkItemPriorityChanged(promoted.Id, result.OldPriority!.Value, promoted.Priority);
        await queue.EnqueueAsync(promoted.Id, ct);

        return Results.Ok(new { id = promoted.Id.ToString(), state = promoted.State.ToString() });
    }

    /// <summary>
    /// POST /workitems/{id}/recover — one-call operator recovery for a single
    /// stuck in-flight item. Same recovery path the
    /// <see cref="ItemStaleProgressWatchdog"/> uses: claim the bound worker
    /// row (if any), release its pool slot, requeue PRESERVING the work
    /// branch (so the next pickup re-rebases existing commits onto current
    /// upstream main rather than starting over), and increment
    /// <see cref="WorkItem.RecoveryAttempts"/>. Bounded by
    /// <c>CodeyBox:WorkerProgressWatchdog:ItemStaleMaxRecoveryAttempts</c>;
    /// once exceeded the item escalates to
    /// <see cref="WorkItemState.NeedsOperatorInput"/> instead of looping.
    ///
    /// <para>
    /// Refuses anything that is not in an active in-flight state (Working /
    /// Reworking / Auditing / Merging / ReworkingForConflict /
    /// UpstreamPushing). Use POST /workitems/{id}/retry for terminal-failed
    /// or operator-parked items; use POST /workitems/{id}/resume for the
    /// operator-cancel resume path.
    /// </para>
    /// </summary>
    private static async Task<IResult> RecoverAsync(
        string id,
        IWorkItemStore store,
        ItemStaleProgressWatchdog watchdog,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        // The watchdog refuses non-active states with a structured error; no
        // need to duplicate the state check here.
        var result = await watchdog.RecoverItemAsync(
            item!,
            reason: $"operator-triggered recovery via POST /workitems/{item!.Id}/recover",
            ct);

        if (!result.Recovered)
            return Results.Conflict(new { error = result.Error ?? "recovery did not transition the work item" });

        return Results.Accepted(
            $"/workitems/{item.Id}",
            new
            {
                id = item.Id.ToString(),
                fromState = result.FromState?.ToString(),
                state = result.NewState?.ToString(),
                recoveryAttempt = result.Attempt,
                branchPreserved = result.BranchPreserved,
            });
    }

    /// <summary>
    /// Partially update a work item's editable fields. Most fields (title,
    /// prompt, agent, work/merge timeouts, min model score, required
    /// capabilities) are Queued-only — they affect a running pipeline so the
    /// endpoint rejects 409 once dispatch starts. <see cref="PatchWorkItemRequest.DependsOn"/>
    /// and the audit-budget fields are the exceptions: they are allowed on any
    /// non-terminal state (Queued / Working / Auditing / …), persisted via
    /// partial UPDATEs that do not stomp <c>state</c> and friends.
    ///
    /// Timeout / score fields are clamped using the same bounds as creation —
    /// out-of-range values do not error, they pin to the boundary so an
    /// operator-led bulk-PATCH of a queue after a defaults bump never 400s.
    ///
    /// Priority is NOT modifiable via this endpoint: the store's TryUpdateIfStateAsync
    /// deliberately omits the priority column (see commit 31789f7 — UpdatePriorityAsync
    /// is the TOCTOU-safe partial-UPDATE path). Use PATCH /workitems/{id}/priority
    /// for priority changes.
    /// </summary>
    private static async Task<IResult> PatchWorkItemAsync(
        string id,
        PatchWorkItemRequest body,
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        IKnobRegistry knobs,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var depsPatch = body.DependsOn is not null;
        var queuedOnlyPatch =
            body.Title is not null
            || body.Prompt is not null
            || body.Agent is not null
            || body.WorkTimeoutMinutes is not null
            || body.MergeTimeoutMinutes is not null
            || body.MinModelScore is not null
            || body.RequiredCapabilities is not null
            || body.Knobs is not null;
        var queuedRowPatch =
            body.Title is not null
            || body.Prompt is not null
            || body.Agent is not null
            || body.WorkTimeoutMinutes is not null
            || body.MergeTimeoutMinutes is not null
            || body.MinModelScore is not null
            || body.RequiredCapabilities is not null;
        var auditBudgetPatch = body.AuditMaxIterations is not null
            || body.AuditComplexity is not null;

        // ── State pre-checks: surface 409 before any write ────────────────────
        // DependsOn is allowed on any non-terminal state — adding a dependency
        // post-hoc is the whole reason this field exists. Other fields stay
        // Queued-only because they affect a running pipeline.
        if (depsPatch && WorkItemDependencies.TerminalStates.Contains(item!.State))
            return Results.Conflict(new
            {
                error = $"cannot edit dependencies of work item in terminal state '{item.State}'",
            });
        if (auditBudgetPatch && WorkItemDependencies.TerminalStates.Contains(item!.State))
            return Results.Conflict(new
            {
                error = $"cannot edit audit budget of work item in terminal state '{item.State}'",
            });
        if (queuedOnlyPatch && item!.State != WorkItemState.Queued)
            return Results.Conflict(new
            {
                error = $"cannot edit item in state {item.State}; only Queued items are editable",
            });

        // ── DependsOn resolution + cycle check (no writes yet, may 400) ──────
        List<WorkItemId>? newDependsOn = null;
        if (depsPatch)
        {
            var (depErr, ids) = await ResolveAndValidateDependsOnAsync(
                body.DependsOn!, item!.Id, item.ProjectId, store, ct);
            if (depErr is not null) return depErr;
            newDependsOn = ids;
        }

        IReadOnlyDictionary<string, string>? normalisedPatchKnobs = null;
        if (body.Knobs is { } patchKnobs)
        {
            var (normalisedKnobs, knobErr) = WorkItemCreationService.NormaliseKnobs(patchKnobs, knobs);
            if (knobErr is not null) return knobErr;
            normalisedPatchKnobs = normalisedKnobs!;
        }

        var updated = item!;
        var now = DateTimeOffset.UtcNow;
        var queuedUpdateExpectedUpdatedAt = item!.UpdatedAt;

        if (body.Title is not null)
        {
            try { Validation.ValidateNoOptionLikeOrControl(body.Title, nameof(body.Title)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (body.Title.Length > 200) return Results.BadRequest(new { error = "title must be <= 200 chars" });
            updated = updated with { Title = body.Title, UpdatedAt = now };
        }

        if (body.Prompt is not null)
        {
            if (body.Prompt.Length > 64 * 1024) return Results.BadRequest(new { error = "prompt must be <= 64KB" });
            // Route through TryReplacePromptAsync — the only write path that
            // touches prompt + prompt_revision. The full-row UPDATE below
            // deliberately does NOT carry the prompt columns (they would
            // clobber a concurrent PUT /workitems/{id}/prompt). The state
            // guard inside TryReplacePromptAsync mirrors the Queued check
            // above; success refreshes our in-memory snapshot for the rest
            // of the PATCH so the response DTO reflects the new revision.
            var promptResult = await store.TryReplacePromptAsync(updated.Id, body.Prompt, now, ct);
            if (promptResult.Outcome == PromptReplaceOutcome.NotFound)
                return Results.NotFound(new { error = $"work item '{id}' no longer exists" });
            if (promptResult.Outcome == PromptReplaceOutcome.TerminalState)
                return Results.Conflict(new { error = $"cannot edit item in terminal state" });
            updated = updated with
            {
                Prompt = body.Prompt,
                PromptRevision = promptResult.NewRevision ?? updated.PromptRevision + 1,
                UpdatedAt = now,
            };
            queuedUpdateExpectedUpdatedAt = now;
        }

        if (body.Agent is not null)
        {
            var kind = new AgentKind(body.Agent);
            if (!agents.TryGet(kind, out _))
                return Results.BadRequest(new { error = $"unknown agent '{body.Agent}'", available = agents.Available.Select(a => a.Value) });
            updated = updated with { Agent = kind, UpdatedAt = now };
        }

        if (body.WorkTimeoutMinutes is { } w)
            updated = updated with { WorkTimeout = TimeSpan.FromMinutes(Math.Clamp(w, 1, 480)), UpdatedAt = now };

        if (body.MergeTimeoutMinutes is { } m)
            updated = updated with { MergeTimeout = TimeSpan.FromMinutes(Math.Clamp(m, 1, 240)), UpdatedAt = now };

        if (body.MinModelScore is { } minScore)
            updated = updated with { MinModelScore = Math.Clamp(minScore, 0, 200), UpdatedAt = now };

        if (body.RequiredCapabilities is { } patchCaps)
        {
            var (normalised, capErr) = NormaliseRequiredCapabilities(patchCaps);
            if (capErr is not null) return capErr;
            updated = updated with { RequiredCapabilities = normalised!, UpdatedAt = now };
        }

        if (normalisedPatchKnobs is not null)
        {
            updated = updated with { Knobs = normalisedPatchKnobs, UpdatedAt = now };
        }

        if (body.AuditMaxIterations is { } auditMaxIterations)
        {
            var auditMaxIterationsError = AuditBudgetRequestValidation.ValidateAuditMaxIterations(auditMaxIterations);
            if (auditMaxIterationsError is not null)
                return Results.BadRequest(new { error = auditMaxIterationsError });
            updated = updated with { AuditMaxIterations = auditMaxIterations, UpdatedAt = now };
        }

        if (body.AuditComplexity is not null)
        {
            var (normalised, complexityErr) = AuditBudgetRequestValidation.NormaliseAuditComplexity(body.AuditComplexity);
            if (complexityErr is not null) return Results.BadRequest(new { error = complexityErr });
            updated = updated with { AuditComplexity = normalised, UpdatedAt = now };
        }

        IReadOnlyList<WorkItemId> oldDependsOn = updated.DependsOn;
        if (depsPatch)
            updated = updated with { DependsOn = newDependsOn!, UpdatedAt = now };

        // ── Persist ──────────────────────────────────────────────────────────
        // Queued-only fields except knobs go through the guarded row UPDATE.
        // Knobs have their own partial write below so worker state transitions
        // from stale snapshots cannot erase an accepted queued edit. Audit-budget
        // fields are restored to their original values for the row write and then
        // persisted through UpdateAuditBudgetAsync below. That keeps the audit
        // budget path partial even when an operator sends it alongside a queued
        // title/timeout/etc edit.
        var needsQueuedRowUpdate = queuedRowPatch || (depsPatch && queuedOnlyPatch);
        if (needsQueuedRowUpdate)
        {
            var queuedUpdate = auditBudgetPatch
                ? updated with
                {
                    AuditMaxIterations = item!.AuditMaxIterations,
                    AuditComplexity = item!.AuditComplexity,
                }
                : updated;
            // Guard state and updated_at so queued edits cannot be written over
            // a concurrent pickup or another accepted queued-field patch.
            var written = await store.TryUpdateIfStateAndUpdatedAtAsync(
                queuedUpdate,
                WorkItemState.Queued,
                queuedUpdateExpectedUpdatedAt,
                ct);
            if (!written)
                return Results.Conflict(new { error = "item changed before the queued-field update could be written" });
            queuedUpdateExpectedUpdatedAt = now;
        }
        if (normalisedPatchKnobs is not null)
        {
            var written = await store.TryReplaceKnobsIfStateAndUpdatedAtAsync(
                updated.Id,
                normalisedPatchKnobs,
                now,
                WorkItemState.Queued,
                queuedUpdateExpectedUpdatedAt,
                ct);
            if (!written)
                return Results.Conflict(new { error = "item changed before the queued knob update could be written" });
        }
        if (auditBudgetPatch)
        {
            var budgetResult = await store.UpdateAuditBudgetAsync(
                updated.Id,
                updated.AuditMaxIterations,
                updated.AuditComplexity,
                now,
                ct);
            switch (budgetResult.Outcome)
            {
                case AuditBudgetUpdateOutcome.NotFound:
                    return Results.NotFound(new { error = $"work item '{id}' no longer exists" });
                case AuditBudgetUpdateOutcome.TerminalState:
                    return Results.Conflict(new
                    {
                        error = $"work item transitioned to terminal state '{budgetResult.Item!.State}' before audit budget could be updated",
                    });
                case AuditBudgetUpdateOutcome.Updated:
                    updated = budgetResult.Item ?? updated;
                    break;
            }
        }
        if (depsPatch && !queuedOnlyPatch)
        {
            var depResult = await store.UpdateDependsOnAsync(updated.Id, newDependsOn!, now, ct);
            switch (depResult.Outcome)
            {
                case DependsOnUpdateOutcome.NotFound:
                    return Results.NotFound(new { error = $"work item '{id}' no longer exists" });
                case DependsOnUpdateOutcome.TerminalState:
                    return Results.Conflict(new
                    {
                        error = $"work item transitioned to terminal state '{depResult.Item!.State}' before dependencies could be updated",
                    });
            }
            oldDependsOn = depResult.OldDependsOn ?? oldDependsOn;
            updated = depResult.Item ?? updated with { DependsOn = newDependsOn!, UpdatedAt = now };
        }

        if (queuedOnlyPatch || auditBudgetPatch)
        {
            AuditLog.WorkItemPatched(
                updated.Id,
                titleChanged: body.Title is not null,
                promptChanged: body.Prompt is not null,
                agentChanged: body.Agent is not null,
                workTimeoutChanged: body.WorkTimeoutMinutes is not null,
                mergeTimeoutChanged: body.MergeTimeoutMinutes is not null,
                minModelScoreChanged: body.MinModelScore is not null,
                requiredCapabilitiesChanged: body.RequiredCapabilities is not null,
                auditBudgetChanged: auditBudgetPatch,
                knobsChanged: body.Knobs is not null);
        }
        if (depsPatch)
            AuditLog.WorkItemDependenciesChanged(updated.Id, oldDependsOn, newDependsOn!);

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

        // If the dep edit on a Queued item left all deps satisfied (typical for
        // dependsOn=[]), kick the dispatcher so it picks the item up immediately
        // instead of waiting for the next scan tick. Mirrors the Create path.
        if (depsPatch
            && updated.State == WorkItemState.Queued
            && WorkItemDependencies.AreSatisfied(updated.DependsOn, statesById))
        {
            await queue.EnqueueAsync(updated.Id, ct);
        }

        var project = await projects.GetAsync(updated.ProjectId, ct);
        return Results.Ok(ToDto(updated, project, statesById, depExternalIds));
    }

    /// <summary>
    /// Resolves and validates a <c>dependsOn</c> string array for a PATCH-time
    /// dependency edit. Each entry is a GUID, a namespaced <c>'ns:value'</c>
    /// externalId, or a bare externalId (unambiguous within the project). Caps
    /// at 100 entries, rejects self-loops and missing deps, and runs full
    /// cycle detection over the proposed graph.
    ///
    /// Returns either the validated WorkItemId list, or an IResult ready to
    /// short-circuit the endpoint with a 400. Mirrors the inline validation
    /// block in CreateAsync — the two paths must keep the same shape so
    /// invariants do not drift.
    /// </summary>
    private static async Task<(IResult? Error, List<WorkItemId>? Ids)> ResolveAndValidateDependsOnAsync(
        string[] rawDeps,
        WorkItemId targetId,
        ProjectId projectId,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (rawDeps.Length > 100)
            return (Results.BadRequest(new { error = "dependsOn must contain at most 100 entries" }), null);

        var allItems = new List<WorkItem>();
        await foreach (var existing in store.ListAsync(ct)) allItems.Add(existing);

        var byNamespacedExternalId = new Dictionary<(string Namespace, string Value), WorkItem>();
        var byBareExternalId = new Dictionary<string, List<(string Namespace, WorkItem Item)>>(StringComparer.Ordinal);
        foreach (var existing in allItems.Where(i => i.ProjectId == projectId))
        {
            foreach (var (ns, value) in existing.ExternalIds)
            {
                byNamespacedExternalId[(ns, value)] = existing;
                if (!byBareExternalId.TryGetValue(value, out var list))
                    byBareExternalId[value] = list = new List<(string, WorkItem)>();
                list.Add((ns, existing));
            }
        }

        var dependsOnIds = new List<WorkItemId>(rawDeps.Length);
        foreach (var rawId in rawDeps)
        {
            if (rawId is null)
                return (Results.BadRequest(new { error = "dependency could not be resolved: null entry in dependsOn array" }), null);
            if (Guid.TryParse(rawId, out var g))
            {
                dependsOnIds.Add(new WorkItemId(g));
                continue;
            }
            if (Validation.TryParseNamespacedExternalId(rawId, out var depNs, out var depValue) && depNs is not null)
            {
                if (!byNamespacedExternalId.TryGetValue((depNs, depValue), out var depByNs))
                    return (Results.BadRequest(new
                    {
                        error = $"dependency '{rawId}' could not be resolved: no work item with externalId '{depValue}' in namespace '{depNs}' in project '{projectId}'",
                    }), null);
                dependsOnIds.Add(depByNs.Id);
                continue;
            }
            if (!byBareExternalId.TryGetValue(rawId, out var matches) || matches.Count == 0)
                return (Results.BadRequest(new
                {
                    error = $"dependency '{rawId}' could not be resolved: no work item with externalId '{rawId}' in project '{projectId}'",
                }), null);
            var distinctItems = matches.Select(m => m.Item.Id).Distinct().ToList();
            if (distinctItems.Count > 1)
                return (Results.BadRequest(new
                {
                    error = $"dependency '{rawId}' is ambiguous: matches multiple work items via namespaces {string.Join(", ", matches.Select(m => m.Namespace).Distinct())} — qualify as 'namespace:value'",
                }), null);
            dependsOnIds.Add(distinctItems[0]);
        }

        if (dependsOnIds.Contains(targetId))
            return (Results.BadRequest(new { error = "a work item cannot depend on itself" }), null);

        var missingDep = WorkItemDependencies.FindMissingDependency(dependsOnIds, allItems);
        if (missingDep is not null)
            return (Results.BadRequest(new { error = $"dependency {missingDep} not found" }), null);

        // Cycle detection: FindCycle overrides adj[targetId] = dependsOnIds in
        // the existing graph, so passing the existing item's own id correctly
        // models the edit case (its old deps are replaced before DFS).
        var cyclePath = WorkItemDependencies.FindCycle(targetId, dependsOnIds, allItems);
        if (cyclePath is not null)
            return (Results.BadRequest(new { error = $"circular dependency detected: {cyclePath}" }), null);

        return (null, dependsOnIds);
    }

    /// <summary>
    /// Patches the work item's namespaced external IDs.
    ///
    /// Default semantics: MERGE. The request body's entries are overlaid on the
    /// existing map; a value of <c>null</c> deletes the key. Set
    /// <c>replaceExternalIds: true</c> to overwrite the whole map instead.
    ///
    /// Conflict resolution: each resulting <c>(namespace, value)</c> pair must
    /// be unique within the project — colliding writes return 409. The legacy
    /// singular <c>external_id</c> column on the underlying row is kept in
    /// sync with the <c>legacy</c> namespace for the deprecation window.
    ///
    /// Allowed in any non-deleted state — namespaced external IDs are
    /// caller-facing identifiers and may be added at any point in the item's
    /// lifecycle (e.g. after a PR has been opened the GitHub ID is appended).
    /// </summary>
    private static async Task<IResult> PatchExternalIdsAsync(
        string id,
        PatchExternalIdsRequest body,
        IWorkItemStore store,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (body is null)
            return Results.BadRequest(new { error = "request body is required" });
        if (body.ExternalIds is null)
            return Results.BadRequest(new { error = "externalIds field is required" });

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        // Build the resulting map. Start from current (merge) or empty (replace),
        // then apply the patch — string values set/overwrite, null values delete.
        var resulting = body.ReplaceExternalIds == true
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(item!.ExternalIds, StringComparer.OrdinalIgnoreCase);

        foreach (var (ns, value) in body.ExternalIds)
        {
            try { Validation.ValidateExternalIdNamespace(ns, $"externalIds key '{ns}'"); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (value is null)
            {
                resulting.Remove(ns);
                continue;
            }
            try { Validation.ValidateExternalId(value, $"externalIds['{ns}']"); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            resulting[ns] = value;
        }

        if (resulting.Count > 16)
            return Results.BadRequest(new { error = "externalIds may contain at most 16 entries per work item" });

        // Pre-check for conflicts on namespaced IDs newly assigned to this item
        // (additions and changed values). We don't pre-check unchanged entries —
        // they already belong to this item.
        foreach (var (ns, value) in resulting)
        {
            if (item!.ExternalIds.TryGetValue(ns, out var existing) && existing == value)
                continue;
            var other = await store.GetByNamespacedExternalIdAsync(item.ProjectId, ns, value, ct);
            if (other is not null && other.Id != item.Id)
                return Results.Conflict(new
                {
                    error = $"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {other.Id} (state: {other.State})"
                });
        }

        WorkItem? updated;
        try
        {
            updated = await store.ReplaceExternalIdsAsync(item!.Id, resulting, DateTimeOffset.UtcNow, ct);
        }
        catch (WorkItemExternalIdConflictException)
        {
            // Re-probe to surface the colliding namespaced ID after a race.
            foreach (var (ns, value) in resulting)
            {
                var other = await store.GetByNamespacedExternalIdAsync(item!.ProjectId, ns, value, ct);
                if (other is not null && other.Id != item.Id)
                    return Results.Conflict(new
                    {
                        error = $"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {other.Id} (state: {other.State})"
                    });
            }
            return Results.Conflict(new { error = "external id conflict (concurrent duplicate)" });
        }
        if (updated is null)
            return Results.NotFound(new { error = $"work item '{id}' no longer exists" });

        var project = await projects.GetAsync(updated.ProjectId, ct);
        var depStates = new Dictionary<WorkItemId, WorkItemState>();
        var depExtIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in updated.DependsOn)
        {
            var dep = await store.GetAsync(depId, ct);
            if (dep is not null)
            {
                depStates[depId] = dep.State;
                depExtIds[depId] = dep.ExternalId;
            }
        }
        return Results.Ok(ToDto(updated, project, depStates, depExtIds));
    }

    /// <summary>
    /// Atomically replaces the prompt of a non-terminal work item and bumps
    /// <see cref="WorkItem.PromptRevision"/> by 1. The new revision is echoed in
    /// the response so the caller (JobTrack et al.) can correlate with the
    /// agent commit's <c>CodeyBox-Prompt-Revision</c> trailer. Mid-iteration
    /// edits do not affect the already-dispatched iteration — the snapshotted
    /// <c>prompt_revision_at_dispatch</c> wins for that iteration.
    /// </summary>
    private static async Task<IResult> PutPromptAsync(
        string id,
        PutPromptRequest body,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrEmpty(body.Prompt))
            return Results.BadRequest(new { error = "prompt is required" });
        if (body.Prompt.Length > 64 * 1024)
            return Results.BadRequest(new { error = "prompt must be <= 64KB" });

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var result = await store.TryReplacePromptAsync(item!.Id, body.Prompt, DateTimeOffset.UtcNow, ct);
        return result.Outcome switch
        {
            PromptReplaceOutcome.NotFound => Results.NotFound(new { error = $"work item '{id}' no longer exists" }),
            PromptReplaceOutcome.TerminalState => Results.Conflict(new
            {
                error = $"cannot replace prompt of work item in terminal state '{item.State}'",
            }),
            PromptReplaceOutcome.Updated => Results.Ok(new
            {
                id = item.Id.ToString(),
                promptRevision = result.NewRevision!.Value,
            }),
            _ => throw new InvalidOperationException($"Unexpected prompt replace outcome '{result.Outcome}'."),
        };
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

    private static async Task<IResult> GetQueueStatusAsync(
        IQueueController queueController,
        IRefactorProjectGateStatusProvider refactorProjectGates,
        CancellationToken ct)
    {
        var refactorGates = await refactorProjectGates.GetRefactorProjectGateStatusAsync(ct);
        return Results.Ok(new
        {
            state = queueController.State.ToString(),
            pausedAt = queueController.PausedAt,
            pausedReason = queueController.PausedReason,
            refactorGates = refactorGates.Select(g => new
            {
                projectId = g.ProjectId.Value,
                state = g.State,
                refactorWorkItemId = g.RefactorWorkItemId.ToString(),
                refactorInFlight = g.RefactorInFlight,
                otherInFlight = g.OtherInFlight,
                reason = g.Reason,
            }),
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
    /// Resolves a route path segment to a <see cref="WorkItem"/>. Accepts:
    ///   * a UUID
    ///   * a composite <c>projectId:externalId</c> where externalId is a bare
    ///     value (matches across every namespace; 400 if ambiguous)
    ///   * a composite <c>projectId:namespace:value</c> where the second
    ///     segment is a recognised external-id namespace key (unambiguous).
    /// Returns the item and a null error result on success, or a null item
    /// with an error result on failure.
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
                return (null, Results.BadRequest(new { error = "composite id format requires non-empty projectId and externalId: '<projectId>:<externalId>' or '<projectId>:<namespace>:<value>'" }));
            ProjectId pid;
            try { pid = new ProjectId(projectPart); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }

            // Detect the optional namespace qualifier inside externalPart. If
            // the leading token is a valid namespace key followed by a value,
            // route to the namespaced lookup; otherwise treat the whole thing
            // as a bare value (which scans every namespace).
            if (Validation.TryParseNamespacedExternalId(externalPart, out var ns, out var nsValue) && ns is not null)
            {
                try { Validation.ValidateExternalId(nsValue, "externalId"); }
                catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
                var byNs = await store.GetByNamespacedExternalIdAsync(pid, ns, nsValue, ct);
                return byNs is null ? (null, Results.NotFound()) : (byNs, null);
            }

            try { Validation.ValidateExternalId(externalPart, "externalId"); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            try
            {
                var byExtId = await store.GetByExternalIdAsync(pid, externalPart, ct);
                return byExtId is null ? (null, Results.NotFound()) : (byExtId, null);
            }
            catch (AmbiguousExternalIdException ex)
            {
                return (null, Results.BadRequest(new
                {
                    error = $"externalId '{externalPart}' is ambiguous in project '{pid}': matches namespaces {string.Join(", ", ex.Namespaces)}. Use '<projectId>:<namespace>:<value>' to disambiguate."
                }));
            }
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
        WorkItemUsageSummary? usage = null,
        IReadOnlyList<WorkItemIteration>? iterations = null)
    {
        var depsSatisfied = WorkItemDependencies.AreSatisfied(item.DependsOn, statesById);
        var depExtIds = item.DependsOn.ToDictionary(
            d => d.ToString(),
            d => depExternalIds is not null && depExternalIds.TryGetValue(d, out var eid) ? eid : null);
        return new WorkItemDto(
            item.Id.ToString(),
            item.ExternalId,
            item.ExternalIds.Count == 0
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : item.ExternalIds.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
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
            QuotaRetryFrom: item.QuotaRetryFrom,
            QuotaRetryPhase: item.QuotaRetryPhase,
            NextTransientRetryAt: item.NextTransientRetryAt,
            TransientRetryAttempts: item.TransientRetryAttempts,
            TransientRetryFirstFailedAt: item.TransientRetryFirstFailedAt,
            TransientRetryFrom: item.TransientRetryFrom,
            AgentPauseTarget: item.AgentPauseTarget?.Value,
            AgentPauseRetryFrom: item.AgentPauseRetryFrom,
            Usage: usage?.Iteration,
            UsageTotal: usage?.Total,
            Priority: item.Priority,
            AuditMaxIterations: item.AuditMaxIterations,
            AuditComplexity: item.AuditComplexity,
            CancellationSource: item.CancellationSource,
            TransientCancelRetries: item.TransientCancelRetries,
            PromptRevision: item.PromptRevision,
            Iterations: iterations?
                .Select(i => new WorkItemIterationDto(i.Iteration, i.PromptRevisionAtDispatch, i.DispatchedAt))
                .ToList(),
            RequiredCapabilities: item.RequiredCapabilities.Count == 0
                ? Array.Empty<string>()
                : item.RequiredCapabilities.ToList(),
            JobType: item.JobType.ToString(),
            Check: item.Check,
            AgentControl: ToAgentControlDto(item.AgentControl),
            Verdict: item.Verdict,
            OriginCheckWorkItemId: item.OriginCheckWorkItemId?.ToString(),
            ReCheckVerdicts: item.ReCheckVerdicts.Count == 0 ? null : item.ReCheckVerdicts,
            AgentInstanceId: item.AgentInstanceId,
            TemplateName: item.TemplateName,
            TemplateEntryIndex: item.TemplateEntryIndex,
            Knobs: item.Knobs.Count == 0
                ? null
                : item.Knobs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
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
    private const int MaxRequiredCapabilities = 16;
    private const int MaxCapabilityLength = 64;

    /// <summary>
    /// Normalises and validates a caller-supplied list of required-capability
    /// tags. Returns the normalised list, or null + error result on failure.
    /// Trims whitespace, drops empties, de-duplicates case-insensitively.
    /// </summary>
    private static (IReadOnlyList<string>? Tags, IResult? Error) NormaliseRequiredCapabilities(
        IReadOnlyList<string> raw)
    {
        if (raw.Count > MaxRequiredCapabilities)
            return (null, Results.BadRequest(new
            {
                error = $"requiredCapabilities may contain at most {MaxRequiredCapabilities} entries",
            }));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var entry in raw)
        {
            if (entry is null) continue;
            var tag = entry.Trim();
            if (tag.Length == 0) continue;
            if (tag.Length > MaxCapabilityLength)
                return (null, Results.BadRequest(new
                {
                    error = $"requiredCapabilities entry '{tag}' exceeds {MaxCapabilityLength} chars",
                }));
            if (tag.Any(char.IsControl))
                return (null, Results.BadRequest(new
                {
                    error = "requiredCapabilities entries must not contain control characters",
                }));
            if (seen.Add(tag)) result.Add(tag);
        }
        return (result, null);
    }

    private static AgentControlDto? ToAgentControlDto(AgentControlSpec? spec) =>
        spec is null
            ? null
            : new AgentControlDto(
                spec.Action switch
                {
                    AgentControlAction.Pause => "pause",
                    AgentControlAction.Resume => "resume",
                    _ => spec.Action.ToString(),
                },
                spec.Agent,
                spec.Reason,
                spec.DurationSeconds,
                spec.ExpiresAt);

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
    /// <summary>
    /// Optional agent preference. When <c>agentClassId</c> is omitted, selects the agent
    /// directly (overriding the project default). When <c>agentClassId</c> is set, this
    /// field is <b>not consulted</b> during class routing: members are chosen purely by
    /// quality score, quota availability, smoke gates, and related routing rules. At pickup
    /// the orchestrator <b>rewrites</b> the persisted work item's <c>agent</c> field to
    /// whichever class member the router actually chose. Per-agent concurrency caps
    /// participate in routing as an additional gate: when the top-ranked eligible member
    /// is at its cap, the router spills to the next eligible-and-free member. Only when
    /// every eligible member is at its cap does the item defer. There is no mechanism
    /// today to hard-pin a work item to a specific agent inside a class.
    /// </summary>
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
    int? Priority = null,
    int? AuditMaxIterations = null,
    string? AuditComplexity = null,
    // Namespaced external IDs. The legacy singular `ExternalId` field is
    // accepted as a write-shortcut stored under namespace 'legacy'. Sending
    // both is allowed only when they agree; conflicting values 400.
    IReadOnlyDictionary<string, string>? ExternalIds = null,
    // Clearance tags the agent member must declare. Empty (default) ⇒ any
    // member of the resolved AgentClass is eligible.
    IReadOnlyList<string>? RequiredCapabilities = null,
    // When present, creates a JobType.CheckAndAct item: the agent evaluates
    // the supplied yes/no question against the project repo and returns a
    // structured verdict. On a matching verdict, the orchestrator enqueues
    // the OnYes follow-up as a normal work item parented to the check.
    CheckAndActRequest? Check = null,
    // When present, creates a JobType.AgentControl item that performs a
    // control-plane pause/resume for one agent kind without launching an agent.
    AgentControlRequest? AgentControl = null,
    // When true, creates a JobType.Refactor item. Refactors run the same
    // work → audit → merge → upstream pipeline as Normal items, but the
    // dispatcher treats them as project-exclusive: a refactor only starts
    // once the project has zero other in-flight items, and while it runs no
    // other item for the same project may start. Mutually exclusive with
    // <c>Check</c> and <c>AgentControl</c>.
    bool? IsRefactor = null,
    // Per-item knob overrides. Keys must match a registered IKnob.Key; values
    // must satisfy the knob descriptor parser. Unknown keys and invalid values
    // are rejected at create time with 400.
    IReadOnlyDictionary<string, string>? Knobs = null);

/// <summary>
/// Request payload for the optional <c>check</c> block on
/// <c>POST /workitems</c>. When supplied, the resulting work item is created
/// with <see cref="JobType.CheckAndAct"/>; the orchestrator runs a single
/// agent invocation in a sandbox that answers <see cref="Question"/> against
/// the project repo and returns a structured verdict.
/// </summary>
public sealed record CheckAndActRequest(
    string Question,
    OnYesActionRequest OnYes,
    bool? ActionableAnswer = null,
    string? Mode = null);

/// <summary>
/// Request payload for the follow-up work item the orchestrator should
/// enqueue when a check verdict matches the actionable condition. Mirrors
/// the relevant subset of <see cref="CreateWorkItemRequest"/>.
/// </summary>
public sealed record OnYesActionRequest(
    string Title,
    string Prompt,
    int? MinModelScore = null,
    int? Priority = null,
    string? Agent = null,
    string? AgentClassId = null,
    string[]? DependsOn = null,
    IReadOnlyDictionary<string, string>? Knobs = null);

public sealed record AgentControlRequest(
    string Action,
    string Agent,
    string? Reason = null,
    int? DurationSeconds = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record AgentControlDto(
    string Action,
    string Agent,
    string? Reason = null,
    int? DurationSeconds = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record RetryWorkItemRequest(string? From);

public sealed record ResumeWorkItemRequest(string? From = null, string? Reason = null);

public sealed record PatchWorkItemRequest(
    string? Title = null,
    string? Prompt = null,
    string? Agent = null,
    int? WorkTimeoutMinutes = null,
    int? MergeTimeoutMinutes = null,
    int? MinModelScore = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    int? AuditMaxIterations = null,
    string? AuditComplexity = null,
    // Replace-set dependency edit. Same string-format and validation rules
    // as the create handler: each entry is a GUID, a namespaced
    // 'ns:value' externalId, or a bare externalId (unambiguous within the
    // project). Cap at 100 entries; cycle-checked; allowed on any non-terminal
    // item. Passing an empty array clears all dependencies.
    string[]? DependsOn = null,
    // Replace-set knob edit (queued-only, like Title/Agent). Sending a non-null
    // map replaces the entire stored map. Unknown keys and invalid values are
    // rejected with 400. Send an empty map to clear all per-item overrides.
    IReadOnlyDictionary<string, string>? Knobs = null);

public sealed record PatchPriorityRequest(int Priority);

/// <summary>
/// Body for PATCH /workitems/{id}/external-ids.
///
/// <see cref="ExternalIds"/> entries with a non-null string are added or
/// updated; entries with a null value delete that namespace. By default the
/// patch is MERGED with the existing map; set
/// <see cref="ReplaceExternalIds"/> to <c>true</c> to overwrite the whole map.
/// </summary>
public sealed record PatchExternalIdsRequest(
    IReadOnlyDictionary<string, string?>? ExternalIds = null,
    bool? ReplaceExternalIds = null);

public sealed record PutPromptRequest(string Prompt);

public sealed record WorkItemIterationDto(int Iteration, int PromptRevision, DateTimeOffset DispatchedAt);

public sealed record ReorderWorkItemsRequest(string[]? Ids = null);

public sealed record PauseQueueRequest(string Reason = "");

public sealed record WorkItemTimelineResponse(string WorkItemId, IReadOnlyList<TimelineEntry> Entries);

public sealed record WorkItemAgentHistoryResponse(
    string WorkItemId,
    // Both omitted (not []/null-valued) when the involvement store is unwired, so
    // a poller can distinguish "feature disabled" (field absent) from "wired but
    // no agent has run yet" ([]) — mirroring the full GET /workitems/{id} handler.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkAgent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AgentInvolvementDto>? AgentHistory);

public sealed record WorkItemDto(
    string Id,
    string? ExternalId,
    IReadOnlyDictionary<string, string> ExternalIds,
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
    int MinModelScore = 0,
    string? ReleaseId = null,
    string? FailureKind = null,
    DateTimeOffset? QuotaResetAt = null,
    DateTimeOffset? NextQuotaRetryAt = null,
    int QuotaRetryAttempts = 0,
    string? QuotaRetryFrom = null,
    string? QuotaRetryPhase = null,
    DateTimeOffset? NextTransientRetryAt = null,
    int TransientRetryAttempts = 0,
    DateTimeOffset? TransientRetryFirstFailedAt = null,
    string? TransientRetryFrom = null,
    string? AgentPauseTarget = null,
    string? AgentPauseRetryFrom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkItemIterationUsage? Usage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorkItemUsageTotal? UsageTotal = null,
    IReadOnlyList<AgentFallbackDto>? FallbackHistory = null,
    int Priority = 0,
    int? AuditMaxIterations = null,
    string? AuditComplexity = null,
    string? CancellationSource = null,
    int TransientCancelRetries = 0,
    int PromptRevision = 1,
    IReadOnlyList<WorkItemIterationDto>? Iterations = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    string JobType = "Normal",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CheckAndActSpec? Check = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AgentControlDto? AgentControl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CheckVerdict? Verdict = null,
    string? OriginCheckWorkItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CheckVerdict>? ReCheckVerdicts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AgentInvolvementDto>? AgentHistory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AgentInstanceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WorkAgent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TemplateName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TemplateEntryIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Knobs = null);

/// <summary>
/// One entry in a work item's per-phase agent involvement trail. Mirrors
/// <see cref="CodeyBox.Core.AgentInvolvement"/>; <see cref="EndedAt"/> /
/// <see cref="Outcome"/> are null while the agent is still running that phase.
/// </summary>
public sealed record AgentInvolvementDto(
    string Id,
    string AgentKind,
    string? ModelId,
    string Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? Iteration,
    string? Outcome,
    string? AgentInstanceId = null);

public sealed record AgentFallbackDto(
    string Id,
    string Phase,
    int? Iteration,
    string FromAgent,
    string? FromModel,
    string? ToAgent,
    string? ToModel,
    string Reason,
    DateTimeOffset OccurredAt,
    string? FromInstanceId = null,
    string? ToInstanceId = null);

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
