using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        group.MapPost("/{id}/delegate", DelegateAsync);
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
        group.MapGet("/{id}/delegations", GetDelegationsAsync);
        group.MapPost("/{id}/answer", AnswerQuestionAsync);
        group.MapPost("/{id}/dismiss-question", DismissQuestionAsync);
        group.MapGet("/{id}/deployment-review", GetDeploymentReviewAsync);
        group.MapPost("/{id}/deployment-review/approve", ApproveDeploymentReviewAsync);
        group.MapPost("/{id}/deployment-review/reject", RejectDeploymentReviewAsync);
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
        app.MapPost("/queue/drain", DrainQueueAsync);
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
            occupiedSlots = (status.OccupiedSlots ?? []).Select(s => new
            {
                workerIndex = s.WorkerIndex,
                workItemId = s.WorkItemId,
                registryWorkerId = s.RegistryWorkerId,
                acquiredAt = s.AcquiredAt,
            }).ToArray(),
        });
    }

    private static async Task<IResult> CreateAsync(
        CreateWorkItemRequest req,
        WorkItemCreationService creation,
        HttpContext context,
        CancellationToken ct)
    {
        var initiator = ApiKeyAuth.ResolveInitiator(context, req.Initiator);
        if (initiator.Error is not null) return initiator.Error;
        var prepared = await creation.PrepareAsync(req with { Initiator = initiator.Value }, ct);
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
        IUpstreamRemoteFactory upstreams,
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
        if (project is not null && item.MergedPrNumber is > 0)
        {
            try
            {
                var pullRequest = await upstreams.Create(project).GetPullRequestAsync(item.MergedPrNumber.Value, ct);
                if (pullRequest is not null)
                {
                    dto = dto with
                    {
                        PullRequestState = pullRequest.Status.ToString().ToLowerInvariant(),
                        PullRequestMergeSha = pullRequest.MergeCommitSha,
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("CodeyBox.Api.WorkItemEndpoints").LogWarning(
                    ex,
                    "Failed to read upstream PR {PullRequestNumber} for work item {WorkItemId}",
                    item.MergedPrNumber,
                    item.Id);
            }
        }
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
    ///
    /// <para>
    /// A worker-occupied item (any <see cref="WorkItemRecoveryPolicy.WorkerOccupiedStates"/>
    /// state, including the phase-boundary states <c>WorkComplete</c> /
    /// <c>AuditPassed</c> / <c>Merged</c> / <c>PlanApproved</c>) whose
    /// <c>UpdatedAt</c> has not advanced inside the item-stale window while a
    /// worker row still binds it is also retryable: the retry first fences the
    /// wedged worker through the same recovery the
    /// <see cref="ItemStaleProgressWatchdog"/> sweep uses (registry-row claim,
    /// pool-slot release, pipeline cancellation so phase finally blocks tear
    /// down the sandbox), then resumes from the requested phase. Without this
    /// the operator has no route back for a heartbeating-but-frozen worker
    /// short of deleting the sandbox or restarting the orchestrator.
    /// </para>
    /// </summary>
    private static async Task<IResult> RetryAsync(
        string id,
        RetryWorkItemRequest? body,
        IWorkItemStore store,
        WorkItemCommandService commands,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var outcome = await commands.RetryAsync(item!, body?.From, body?.WorkTimeoutMinutes, ct);
        return outcome.ToHttpResult();
    }


    /// <summary>
    /// Delegate a work item to the unconstrained delegation phase: one repair
    /// turn with latitude the normal work/audit/rework cycle does not grant,
    /// verified by the same audit and merge gates afterwards.
    ///
    /// Works from any non-terminal state and from the terminal failure states
    /// (Failed, AuditFailed, MergeConflictResolutionFailed,
    /// AbandonedAfterRecoveryAttempts) — exactly the items that need it.
    /// Done (nothing to repair), Cancelled (operator-stopped), and
    /// NoActionRequired (resolved) return 409.
    ///
    /// The optional <c>note</c> is stored on the item and rendered into the
    /// convergence brief so the operator can direct the attempt. A
    /// worker-held in-flight item is fenced through worker recovery first
    /// (operator intent substitutes for a staleness verdict); when fencing
    /// fails closed the command returns 409 rather than racing the pipeline.
    ///
    /// The delegated turn competes for the same worker and sandbox capacity
    /// as normal work through the shared dispatcher: priority is preserved,
    /// an explicit end-of-queue position is stamped, and no lane or boost is
    /// granted — so delegation cannot starve normal dispatch.
    ///
    /// Returns:
    ///   - 202 with the Delegating item when the trigger is armed.
    ///   - 400 when the note violates its length/control-character guard.
    ///   - 404 when the item does not exist.
    ///   - 409 when the state is not delegable, operator questions are still
    ///     open, the worker fence fails closed, or the row advanced
    ///     concurrently.
    /// </summary>
    private static async Task<IResult> DelegateAsync(
        string id,
        DelegateWorkItemRequest? body,
        IWorkItemStore store,
        DelegationEscalationService delegationEscalation,
        IWorkerRegistry registry,
        ItemStaleProgressWatchdog staleWatchdog,
        IWorkItemQuestionStore? questions,
        IOptionsMonitor<CodeyBoxOptions> options,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var maxNoteChars = Math.Max(1, options.CurrentValue.DelegationEscalation.MaxNoteChars);
        var note = body?.Note;
        if (string.IsNullOrWhiteSpace(note))
        {
            note = null;
        }
        else if (note.Length > maxNoteChars)
        {
            return Results.BadRequest(new { error = $"note must be <= {maxNoteChars} chars" });
        }
        else if (note.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
        {
            return Results.BadRequest(new { error = "note must not contain control characters" });
        }

        if (!DelegationEscalationPolicy.IsDelegableState(item!.State))
            return Results.Conflict(new { error = $"cannot delegate item in state {item.State}; only non-terminal states and terminal failure states can be delegated" });

        if (item.State == WorkItemState.NeedsOperatorInput && questions is not null)
        {
            var openQuestions = (await questions.ListByWorkItemAsync(item.Id.ToString(), ct))
                .Where(q => string.Equals(q.State, "open", StringComparison.Ordinal))
                .Select(q => q.QuestionId)
                .Take(5)
                .ToArray();
            if (openQuestions.Length > 0)
            {
                return Results.Conflict(new
                {
                    error = "cannot delegate item while operator questions are open; answer or dismiss them first",
                    openQuestions,
                });
            }
        }

        // A worker-held in-flight item cannot simply be flipped to Delegating
        // under a live pipeline. Fence it through worker recovery first — the
        // explicit operator command authorizes interrupting the current turn,
        // so no staleness verdict is required; recovery still fails closed on
        // unfenceable dispatch claims and concurrent advances.
        if (WorkItemRecoveryPolicy.IsItemStaleWatchedState(item.State))
        {
            var fenceError = await TryFenceLiveWorkerItemForDelegateAsync(
                item, registry, staleWatchdog, ct);
            if (fenceError is not null)
                return fenceError;
            var fenced = await store.GetAsync(item.Id, ct);
            if (fenced is null)
                return Results.Conflict(new { error = "work item no longer exists" });
            if (!DelegationEscalationPolicy.IsDelegableState(fenced.State))
                return Results.Conflict(new { error = $"cannot delegate item in state {fenced.State} after fencing the previous worker; only non-terminal states and terminal failure states can be delegated" });
            item = fenced;
        }

        var result = await delegationEscalation.DelegateAsync(
            item,
            DelegationTriggers.Operator,
            note,
            markAutoEscalated: false,
            failureContext: null,
            ct);
        if (!result.Delegated)
            return Results.Conflict(new { error = result.Error });

        return Results.Accepted(
            $"/workitems/{item.Id}",
            new
            {
                id = item.Id.ToString(),
                trigger = DelegationTriggers.Operator,
                priorState = item.State.ToString(),
                state = WorkItemState.Delegating.ToString(),
            });
    }

    /// <summary>
    /// Fences a worker-bound item so an operator delegate cannot race the live
    /// pipeline. Returns null when delegation may proceed (no worker binds the
    /// item, or recovery fenced it); otherwise the 409 result to return.
    /// Unlike the retry fence, staleness is not required: the explicit
    /// operator command itself authorizes interrupting the current turn.
    /// Recovery fails closed on unfenceable dispatch claims, concurrent
    /// advances, and exhausted attempt budgets that cannot park.
    /// </summary>
    private static async Task<IResult?> TryFenceLiveWorkerItemForDelegateAsync(
        WorkItem item,
        IWorkerRegistry registry,
        ItemStaleProgressWatchdog staleWatchdog,
        CancellationToken ct)
    {
        var idStr = item.Id.ToString();
        var bound = false;
        try
        {
            var workers = await registry.ListAsync(ct);
            foreach (var worker in workers)
            {
                if (string.Equals(worker.CurrentWorkItemId, idStr, StringComparison.OrdinalIgnoreCase))
                {
                    bound = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Results.Conflict(new { error = $"cannot delegate worker-held item {item.Id}: failed to inspect worker bindings: {ex.Message}" });
        }

        if (!bound)
            return null;

        var recovery = await staleWatchdog.RecoverItemAsync(
            item,
            $"operator delegate fenced worker-held item in {item.State}",
            ct);
        if (!recovery.Recovered)
        {
            return Results.Conflict(new { error = $"cannot delegate worker-held item {item.Id}: {recovery.Error ?? "recovery did not transition the work item"}" });
        }

        return null;
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
        HttpContext context,
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
                return Results.BadRequest(new { error = $"unknown agent '{Validation.DescribeUntrustedValue(body.Agent)}'", available = agents.Available.Select(a => a.Value) });
            agentOverride = kind;
            agentClassOverride = null; // agent-specific override clears class routing
        }

        if (!string.IsNullOrWhiteSpace(body?.AgentClassId))
        {
            var (normalizedClassId, classIdError) = WorkItemFieldRules.NormalizeAgentClassId(body.AgentClassId);
            if (classIdError is not null)
                return Results.BadRequest(new { error = classIdError });
            agentClassOverride = normalizedClassId;
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

            if (WorkItemFieldRules.CheckDistinctBranches(source.BaseBranch, body.WorkBranch) is { } branchError)
                return Results.BadRequest(new { error = branchError });

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
        var replayInitiator = ApiKeyAuth.ResolveInitiator(context, delegated: null);
        if (replayInitiator.Error is not null) return replayInitiator.Error;

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
            Initiator = replayInitiator.Value,
        };

        await store.CreateAsync(replay, ct);
        AuditLog.WorkItemCreated(replay.Id, replay.ProjectId, replay.Title, replay.Initiator);

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

            var reports = await reportStore.GetByWorkItemAsync(
                item.Id.ToString(), AuditTarget.Code, ct);
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
        WorkItemCommandService commands,
        string? reason,
        string? resolutionSha,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var outcome = await commands.CancelAsync(item!, reason, resolutionSha, ct);
        return outcome.ToHttpResult();
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
            ConsecutiveInfrastructureRecoveries = 0,
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
        [FromServices] WorkItemRepoReaper? repoReaper,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State == WorkItemState.Done)
            return Results.Conflict(new { error = $"cannot abandon item in state {item.State}" });

        if (item.State == WorkItemState.AbandonedAfterRecoveryAttempts)
            return Results.Ok(new { id = item.Id.ToString(), state = item.State.ToString() });

        if (item.State is WorkItemState.Planning
            or WorkItemState.PlanReview
            or WorkItemState.PlanApproved
            or WorkItemState.Working
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
        if (repoReaper is not null)
            await repoReaper.TryReapWorkItemAsync(abandoned.Id, CancellationToken.None);

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
        if (AgentPauseValidation.ValidateOptionalReason(reason, "reason") is { } reasonError)
            return Results.BadRequest(new { error = reasonError });

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
            ? Math.Min(cap, WorkItemLimits.MaxPriority)
            : WorkItemLimits.MaxPriority;
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
    /// Refuses anything that is not in an active in-flight state (any
    /// <see cref="WorkItemRecoveryPolicy.WorkerOccupiedStates"/> state,
    /// including the phase-boundary states <c>WorkComplete</c> /
    /// <c>AuditPassed</c> / <c>Merged</c> / <c>PlanApproved</c>). Use POST
    /// /workitems/{id}/retry for terminal-failed, operator-parked, or
    /// stale-but-worker-held items (the retry endpoint fences the wedged
    /// worker first); use POST /workitems/{id}/resume for the
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
    /// endpoint rejects 409 once dispatch starts. <see cref="PatchWorkItemRequest.DependsOn"/>,
    /// the audit-budget fields, and <see cref="PatchWorkItemRequest.AgentClassId"/>
    /// are the exceptions: they are allowed on any non-terminal state
    /// (Queued / Working / Auditing / WorkComplete / …), persisted via
    /// partial UPDATEs that do not stomp <c>state</c> and friends.
    ///
    /// AgentClassId is refused with 409 while a worker holds the item (the
    /// dispatch path may be resolving the class concurrently) and with 409 on
    /// terminal items; unknown class ids are rejected with 400 against the
    /// live router catalog.
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
        WorkItemCommandService commands,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var outcome = await commands.PatchAsync(item!, body, ct);
        return outcome.ToHttpResult();
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
        WorkItemCommandService commands,
        CancellationToken ct)
    {
        if (body is null)
            return Results.BadRequest(new { error = "request body is required" });

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var outcome = await commands.ExternalIdsAsync(item!, body, ct);
        return outcome.ToHttpResult();
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
        if (body is null)
            return Results.BadRequest(new { error = "prompt is required" });
        var (newPrompt, promptError) = WorkItemFieldRules.NormalizePrompt(body.Prompt);
        if (promptError is not null)
            return Results.BadRequest(new { error = promptError });

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var result = await store.TryReplacePromptAsync(item!.Id, newPrompt!, DateTimeOffset.UtcNow, ct);
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
        WorkItemCommandService commands,
        CancellationToken ct)
    {
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var outcome = await commands.PriorityAsync(item!, body.Priority, ct);
        return outcome.ToHttpResult();
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
                return Results.BadRequest(new { error = $"'{Validation.DescribeUntrustedValue(raw)}' is not a valid work item id" });
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

    /// <summary>Minimum drain wait (seconds) accepted by the drain endpoint.</summary>
    public const int MinDrainTimeoutSeconds = 1;

    /// <summary>Maximum drain wait (seconds) accepted by the drain endpoint.</summary>
    public const int MaxDrainTimeoutSeconds = 3600;

    /// <summary>
    /// Shared required-reason guard for the queue pause/drain endpoints: the
    /// reason must be present, contain no control characters, and fit within
    /// <see cref="AgentPauseValidation.MaxReasonLength"/> characters. Returns a
    /// BadRequest result when invalid, null when the reason is acceptable.
    /// </summary>
    private static IResult? ValidateQueueReason(string? reason)
    {
        var error = AgentPauseValidation.ValidateRequiredReason(reason, "reason");
        return error is null ? null : Results.BadRequest(new { error });
    }

    private static async Task<IResult> PauseQueueAsync(
        PauseQueueRequest body,
        IQueueController queueController,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        if (ValidateQueueReason(body.Reason) is { } reasonError)
            return reasonError;

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

    /// <summary>
    /// Pause-and-wait drain for graceful restarts. Pauses the queue when it is
    /// still running, then blocks until no workers are running or
    /// <c>timeoutSeconds</c> elapses. Unlike <c>POST /queue/pause</c> — which
    /// returns immediately and leaves in-flight work running — drain lets an
    /// operator restart without interrupting running work: when
    /// <c>drained</c> is true every worker has reached a safe boundary. The
    /// queue stays paused afterwards; resume it (or restart, then resume)
    /// when ready.
    /// </summary>
    private static async Task<IResult> DrainQueueAsync(
        DrainQueueRequest body,
        IQueueController queueController,
        OrchestratorService orchestrator,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        if (ValidateQueueReason(body.Reason) is { } drainReasonError)
            return drainReasonError;
        if (body.TimeoutSeconds is not { } timeoutSeconds
            || timeoutSeconds < MinDrainTimeoutSeconds
            || timeoutSeconds > MaxDrainTimeoutSeconds)
            return Results.BadRequest(new { error = $"timeoutSeconds is required and must be between {MinDrainTimeoutSeconds} and {MaxDrainTimeoutSeconds}" });

        if (queueController.State == QueueState.Running)
        {
            await queueController.PauseAsync(body.Reason, ct);
            _ = webhooks.PublishAsync(new WebhookEvent
            {
                Event = "queue.paused",
                Details = new { pausedAt = queueController.PausedAt, reason = queueController.PausedReason, pausedBy = "api" },
            }, CancellationToken.None);
        }

        var drained = await QueueDrain.WaitForQuiescenceAsync(
            async innerCt => (await orchestrator.GetStatusAsync(innerCt)).CurrentlyRunning,
            TimeSpan.FromSeconds(timeoutSeconds),
            ct);
        var status = await orchestrator.GetStatusAsync(ct);
        return Results.Ok(new
        {
            state = queueController.State.ToString(),
            drained,
            currentlyRunning = status.CurrentlyRunning,
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

    private static async Task<IResult> GetDelegationsAsync(
        string id,
        IWorkItemStore store,
        IDelegationEventStore? delegationEvents,
        CancellationToken ct)
    {
        if (delegationEvents is null) return Results.Json(new { error = "delegation event store not configured" }, statusCode: 503);
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;
        var events = await delegationEvents.ListByWorkItemAsync(item!.Id, ct);
        return Results.Ok(events.Select(e => new DelegationEventDto(
            e.Id, e.WorkItemId.ToString(), e.Attempt, e.Brief,
            e.Agent.Value, e.Model, e.Outcome, e.Reason,
            e.DiffStat, e.ResultDiff, e.OccurredAt)));
    }

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
        IHumanDeploymentReviewStore? reviews,
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
        await QuestionAnswerPipeline.AnswerAndResumeAsync(
            item, req.QuestionId, redactedAnswer,
            answeredBy: null, decidedBy: null,
            questionStore, store, queue, webhooks, projects, reviews, ct);

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
        if (AgentPauseValidation.ValidateRequiredReason(req.Reason, "reason") is { } reasonError)
            return Results.BadRequest(new { error = reasonError });

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
        await QuestionAnswerPipeline.MaybeResumeFromNeedsOperatorInputAsync(item, store, questionStore, queue, webhooks, project, ct);

        return Results.Ok(new { status = "dismissed" });
    }

    // ── Human deployment-review verdict endpoints ────────────────────────────
    //
    // The operator acts as a reviewer through the standard auditor seam:
    // approve records a pass, reject-with-notes records blocking findings
    // that feed the normal rework loop. A verdict past the review deadline
    // is refused with 410 and fails closed through the shared expiry path.
    // The generic POST /answer endpoint verdicts identically (answering the
    // backing question with "approve" approves; any other text rejects).

    private static async Task<IResult> GetDeploymentReviewAsync(
        string id,
        IWorkItemStore store,
        IHumanDeploymentReviewStore? reviews,
        CancellationToken ct)
    {
        if (reviews is null) return Results.Json(new { error = "human review store not configured" }, statusCode: 503);
        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        var review = await reviews.GetActiveForWorkItemAsync(item!.Id.ToString(), ct);
        if (review is null)
            return Results.NotFound(new { error = "no pending human deployment review for this work item" });

        return Results.Ok(new DeploymentReviewDto(
            review.WorkItemId,
            review.Iteration,
            review.DeploymentId,
            review.Deadline,
            review.RequestedAt,
            review.QuestionId,
            review.Status.ToString(),
            review.Brief));
    }

    private static async Task<IResult> ApproveDeploymentReviewAsync(
        string id,
        ApproveDeploymentReviewRequest? req,
        IWorkItemStore store,
        IHumanDeploymentReviewStore? reviews,
        IWorkItemQuestionStore? questionStore,
        IDeploymentManager? deployments,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (req?.Note is { Length: > 4000 })
            return Results.BadRequest(new { error = "note must be <= 4000 chars" });
        return await RecordDeploymentReviewVerdictAsync(
            id, approved: true, notes: req?.Note, store, reviews, questionStore,
            deployments, queue, webhooks, projects, ct);
    }

    private static async Task<IResult> RejectDeploymentReviewAsync(
        string id,
        RejectDeploymentReviewRequest? req,
        IWorkItemStore store,
        IHumanDeploymentReviewStore? reviews,
        IWorkItemQuestionStore? questionStore,
        IDeploymentManager? deployments,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Notes))
            return Results.BadRequest(new { error = "notes describing what fails are required" });
        if (req!.Notes.Length > 4000)
            return Results.BadRequest(new { error = "notes must be <= 4000 chars" });
        return await RecordDeploymentReviewVerdictAsync(
            id, approved: false, notes: req.Notes, store, reviews, questionStore,
            deployments, queue, webhooks, projects, ct);
    }

    private static async Task<IResult> RecordDeploymentReviewVerdictAsync(
        string id,
        bool approved,
        string? notes,
        IWorkItemStore store,
        IHumanDeploymentReviewStore? reviews,
        IWorkItemQuestionStore? questionStore,
        IDeploymentManager? deployments,
        ITaskQueue queue,
        IWebhookDispatcher webhooks,
        IProjectRepository projects,
        CancellationToken ct)
    {
        if (reviews is null) return Results.Json(new { error = "human review store not configured" }, statusCode: 503);
        if (questionStore is null) return Results.Json(new { error = "question store not configured" }, statusCode: 503);

        var (item, err) = await ResolveWorkItemAsync(id, store, ct);
        if (err is not null) return err;

        if (item!.State != WorkItemState.NeedsOperatorInput)
            return Results.Conflict(new { error = "work item is not waiting for operator input" });

        var review = await reviews.GetActiveForWorkItemAsync(item.Id.ToString(), ct);
        if (review is null)
            return Results.NotFound(new { error = "no pending human deployment review for this work item" });
        if (review.Status != HumanDeploymentReviewStatus.Pending)
            return Results.Conflict(new { error = $"review is already {review.Status.ToString().ToLowerInvariant()}; resume is pending" });

        var now = DateTimeOffset.UtcNow;
        if (now >= review.Deadline)
        {
            // Late verdict: refuse and fail closed through the shared expiry
            // path (teardown + dismiss + re-queue) so silence past the
            // deadline never becomes an implicit pass.
            await HumanReviewExpiry.ExpireAsync(
                reviews, deployments, store, questionStore, queue, webhooks,
                review, now, ct: ct);
            return Results.Json(new { error = "review expired unreviewed" }, statusCode: 410);
        }

        var recorded = await reviews.RecordVerdictAsync(
            review.WorkItemId,
            review.Iteration,
            approved,
            approved ? notes : HumanDeploymentReviewPolicy.TruncateNotes(notes ?? string.Empty),
            decidedBy: null,
            now,
            ct);
        if (!recorded)
            return Results.Conflict(new { error = "review was decided concurrently" });

        // Answer the backing question so the Q&A trail shows the verdict;
        // the idempotent no-op branch below covers a concurrent answer.
        var question = await questionStore.GetAsync(item.Id.ToString(), review.QuestionId, ct);
        if (question is { State: "open" })
        {
            var answerText = approved ? "approve" : HumanDeploymentReviewPolicy.TruncateNotes(notes ?? string.Empty);
            await questionStore.AnswerAsync(item.Id.ToString(), review.QuestionId, answerText, answeredBy: null, ct);
            var project = await projects.GetAsync(item.ProjectId, ct);
            await webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.question_answered",
                WorkItem = item,
                Project = project,
                Details = new QuestionAnsweredDetails(
                    item.Id.ToString(), item.ProjectId.Value, review.QuestionId, answerText, AnsweredBy: null),
            }, ct);
            await QuestionAnswerPipeline.MaybeResumeFromNeedsOperatorInputAsync(item, store, questionStore, queue, webhooks, project, ct);
        }

        return Results.Ok(new
        {
            status = approved ? "approved" : "rejected",
            iteration = review.Iteration,
        });
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

    internal static WorkItemDto ToDto(
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
            item.Initiator,
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
            LocalSquashSha: item.LocalSquashSha,
            MergedPrNumber: item.MergedPrNumber,
            MergedPrUrl: item.MergedPrUrl,
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
            PlanArtifact: item.PlanArtifact,
            PlanGeneratedAt: item.PlanGeneratedAt,
            PlanReviewedAt: item.PlanReviewedAt,
            PlanReviewSummary: item.PlanReviewSummary,
            DelegationAttempts: item.DelegationAttempts,
            DelegationRequested: item.DelegationRequested,
            DelegationReason: item.DelegationReason,
            DelegationNote: item.DelegationNote,
            AutoDelegationEscalated: item.AutoDelegationEscalated,
            DelegationFailed: item.DelegationFailed,
            TerminalFailureCount: item.TerminalFailureCount,
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
            audit.MaxIterations,
            p.SandboxSecrets.Count > 0,
            p.SandboxSecrets.Select(secret => new ProjectSandboxSecretDto(
                secret.HostEnvVar,
                secret.SandboxEnvVar,
                secret.Group,
                secret.Scopes)).ToList(),
            p.SandboxSecretGrants.Select(grant => new ProjectSandboxSecretGrantDto(
                grant.Group,
                grant.WorkItemId?.ToString("N"),
                grant.IsProjectWide,
                grant.IsImplicit)).ToList());
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
            WorkItemState.NoActionRequired or
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
    // Accepted only from API clients configured with CanDelegateInitiator.
    // All other callers receive their server-configured principal.
    WorkInitiator? Initiator = null,
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

public sealed record RetryWorkItemRequest(string? From, int? WorkTimeoutMinutes = null);

public sealed record DelegateWorkItemRequest(string? Note = null);

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
    IReadOnlyDictionary<string, string>? Knobs = null,
    // Agent-class reassignment. Allowed on any non-terminal item with no
    // worker bound to it (409 while a worker holds the item or the item is
    // terminal). The id must name a class in the live router catalog —
    // unknown ids are rejected with 400, mirroring the create-time bounds.
    // A work_item.agent_class_changed audit entry records the old/new values.
    string? AgentClassId = null);

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

/// <summary>
/// Pause-and-wait drain request. <c>Reason</c> follows the shared queue-reason
/// guard (required, no control characters, at most
/// <c>AgentPauseValidation.MaxReasonLength</c> characters).
/// <c>TimeoutSeconds</c> bounds how long the endpoint waits for in-flight work
/// to reach a safe boundary (<c>WorkItemEndpoints.MinDrainTimeoutSeconds</c> to
/// <c>WorkItemEndpoints.MaxDrainTimeoutSeconds</c>); on expiry the endpoint
/// reports <c>drained: false</c> and the queue stays paused.
/// </summary>
public sealed record DrainQueueRequest(string Reason = "", int? TimeoutSeconds = null);

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
    WorkInitiator? Initiator,
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
    string? LocalSquashSha = null,
    int? MergedPrNumber = null,
    string? MergedPrUrl = null,
    string? PullRequestState = null,
    string? PullRequestMergeSha = null,
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
    string? PlanArtifact = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? PlanGeneratedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? PlanReviewedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlanReviewSummary = null,
    int DelegationAttempts = 0,
    bool DelegationRequested = false,
    string? DelegationReason = null,
    string? DelegationNote = null,
    bool AutoDelegationEscalated = false,
    bool DelegationFailed = false,
    int TerminalFailureCount = 0,
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
    int AuditMaxIterations,
    bool HasSandboxSecrets = false,
    IReadOnlyList<ProjectSandboxSecretDto>? SandboxSecrets = null,
    IReadOnlyList<ProjectSandboxSecretGrantDto>? SecretGrants = null);

/// <summary>
/// Names-only view of one project sandbox secret. Never carries a value.
/// </summary>
public sealed record ProjectSandboxSecretDto(
    string HostEnvVar,
    string SandboxEnvVar,
    string Group,
    IReadOnlyList<string> Scopes);

/// <summary>
/// Operator audit view of one secret-group grant. Names the group and,
/// for work-item grants, the authorised work-item id. Never carries a value.
/// </summary>
public sealed record ProjectSandboxSecretGrantDto(
    string Group,
    string? WorkItemId,
    bool IsProjectWide,
    bool IsImplicit);

public sealed record AnswerQuestionRequest(string QuestionId, string Answer);

public sealed record DismissQuestionRequest(string QuestionId, string Reason);

public sealed record ApproveDeploymentReviewRequest(string? Note);

public sealed record RejectDeploymentReviewRequest(string? Notes);

public sealed record DeploymentReviewDto(
    string WorkItemId,
    int Iteration,
    string DeploymentId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RequestedAt,
    string QuestionId,
    string Status,
    string Brief);

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

/// <summary>Wire DTO for one delegation turn: what the delegate was told
/// (brief), who ran it (agent/model), and what it did (diff).</summary>
public sealed record DelegationEventDto(
    string Id,
    string WorkItemId,
    int Attempt,
    string Brief,
    string Agent,
    string? Model,
    string Outcome,
    string? Reason,
    string DiffStat,
    string ResultDiff,
    DateTimeOffset OccurredAt);
public sealed record ReplayWorkItemRequest(
    string? Agent = null,
    string? ModelId = null,
    string? AgentClassId = null,
    string? WorkBranch = null);

public sealed record WorkItemReplaysResponse(
    WorkItemDto Source,
    IReadOnlyList<WorkItemDto> Replays);
