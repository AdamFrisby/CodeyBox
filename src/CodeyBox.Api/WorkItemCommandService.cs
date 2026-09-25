using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Options;

namespace CodeyBox.Api;

/// <summary>
/// The HTTP-neutral core of the mutating work-item endpoints: PATCH fields,
/// priority, external ids, DELETE (cancel), and POST retry. The bodies moved
/// here verbatim from <see cref="WorkItemEndpoints"/> so two callers — the
/// REST endpoints and the majordomo MCP tool surface — execute identical
/// validation, state-machine rules, guarded writes, audit entries, and
/// webhooks. Neither caller re-implements policy.
/// </summary>
/// <remarks>
/// Every command is split into a plan step and a commit step. The plan step
/// performs every check and projects the post-image without writing; the
/// commit step performs the writes exactly as the original endpoint did, in
/// the same order. Callers that only need validation (dry-run, proposal
/// review) run the plan and stop; the REST endpoints run both back to back.
/// One deliberate change versus the previous ordering: every field
/// validation now runs before any write, so a patch that fails late
/// validation can no longer leave a lone prompt write behind.
/// </remarks>
internal sealed class WorkItemCommandService
{
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IProjectRepository _projects;
    private readonly IAgentRegistry _agents;
    private readonly IKnobRegistry _knobs;
    private readonly IWorkerRegistry _registry;
    private readonly AgentClassRouter _router;
    private readonly CancellationRegistry _cancellations;
    private readonly IWebhookDispatcher _webhooks;
    private readonly WorkItemRetrier _retrier;
    private readonly ItemStaleProgressWatchdog _staleWatchdog;
    private readonly IOptionsMonitor<CodeyBoxOptions> _options;
    private readonly ITimingStore? _timings;
    private readonly WorkItemRepoReaper? _repoReaper;

    public WorkItemCommandService(
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        IKnobRegistry knobs,
        IWorkerRegistry registry,
        AgentClassRouter router,
        CancellationRegistry cancellations,
        IWebhookDispatcher webhooks,
        WorkItemRetrier retrier,
        ItemStaleProgressWatchdog staleWatchdog,
        IOptionsMonitor<CodeyBoxOptions> options,
        ITimingStore? timings = null,
        WorkItemRepoReaper? repoReaper = null)
    {
        _store = store;
        _queue = queue;
        _projects = projects;
        _agents = agents;
        _knobs = knobs;
        _registry = registry;
        _router = router;
        _cancellations = cancellations;
        _webhooks = webhooks;
        _retrier = retrier;
        _staleWatchdog = staleWatchdog;
        _options = options;
        _timings = timings;
        _repoReaper = repoReaper;
    }

    /// <summary>
    /// The outcome of a command expressed HTTP-neutrally: the same status code
    /// and payload shape the endpoint would have produced, plus the affected
    /// item when the command succeeded. <see cref="ToHttpResult"/> renders it
    /// for the REST surface; the majordomo executor maps it onto a tool result.
    /// </summary>
    internal sealed record WorkItemCommandOutcome(
        int StatusCode,
        object? Body = null,
        string? Location = null,
        WorkItem? Item = null,
        IReadOnlyList<WorkItemId>? AlsoAffected = null,
        string? Error = null)
    {
        public bool Succeeded => StatusCode is >= 200 and < 300;

        public IResult ToHttpResult() => StatusCode switch
        {
            StatusCodes.Status201Created => Results.Created(Location!, Body),
            StatusCodes.Status202Accepted => Body is null
                ? Results.Accepted(Location)
                : Results.Accepted(Location, Body),
            StatusCodes.Status200OK => Body is null ? Results.Ok() : Results.Ok(Body),
            _ => Body is null
                ? Results.StatusCode(StatusCode)
                : Results.Json(Body, statusCode: StatusCode),
        };

        internal static WorkItemCommandOutcome BadRequest(string error) =>
            new(StatusCodes.Status400BadRequest, new { error }, Error: error);

        internal static WorkItemCommandOutcome Conflict(string error) =>
            new(StatusCodes.Status409Conflict, new { error }, Error: error);

        internal static WorkItemCommandOutcome NotFound(string error) =>
            new(StatusCodes.Status404NotFound, new { error }, Error: error);
    }

    /// <summary>
    /// The result of a plan step: either the plan to commit or the refusal the
    /// endpoint would have returned. A plan is a pure value — it carries the
    /// projected post-image so dry-run callers can report what would change
    /// without committing anything.
    /// </summary>
    internal sealed record CommandPlan<T>(T? Plan, WorkItemCommandOutcome? Error)
    {
        public static CommandPlan<T> Ready(T plan) => new(plan, null);
        public static CommandPlan<T> Refused(WorkItemCommandOutcome error) => new(default, error);
    }

    // ── PATCH /workitems/{id} ────────────────────────────────────────────────

    internal sealed record WorkItemPatchPlan(
        WorkItem Item,
        WorkItem Updated,
        PatchWorkItemRequest Body,
        bool DepsPatch,
        bool QueuedOnlyPatch,
        bool QueuedRowPatch,
        bool AuditBudgetPatch,
        bool AgentClassPatch,
        IReadOnlyList<WorkItemId>? NewDependsOn,
        IReadOnlyList<WorkItemId> OldDependsOn,
        IReadOnlyDictionary<string, string>? NormalisedKnobs,
        string? NewAgentClassId,
        string? OldAgentClassId,
        bool PromptViaReplace,
        DateTimeOffset Now,
        DateTimeOffset QueuedUpdateExpectedUpdatedAt);

    /// <summary>
    /// Validates <paramref name="body"/> against <paramref name="item"/> and
    /// projects the post-patch row. Pure: resolves dependencies, normalises
    /// knobs, and checks the agent-class catalog and worker bindings, but
    /// writes nothing.
    /// </summary>
    public async Task<CommandPlan<WorkItemPatchPlan>> PlanPatchAsync(
        WorkItem item,
        PatchWorkItemRequest body,
        CancellationToken ct)
    {
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
        var agentClassPatch = body.AgentClassId is not null;

        // ── State pre-checks: surface 409 before any write ────────────────────
        // DependsOn is allowed on any non-terminal state — adding a dependency
        // post-hoc is the whole reason this field exists. Other fields stay
        // Queued-only because they affect a running pipeline.
        if (depsPatch && WorkItemDependencies.TerminalStates.Contains(item.State))
            return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot edit dependencies of work item in terminal state '{item.State}'"));
        if (auditBudgetPatch && WorkItemDependencies.TerminalStates.Contains(item.State))
            return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot edit audit budget of work item in terminal state '{item.State}'"));
        // AgentClassId is allowed on any non-terminal state — the motivating
        // case is a WorkComplete item parked behind an auditor class whose
        // members are all unavailable (a Queued-only restriction would not
        // solve it). Terminal items are closed; worker-held items are refused
        // below rather than racing the dispatch path.
        if (agentClassPatch && WorkItemDependencies.TerminalStates.Contains(item.State))
            return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot edit agent class of work item in terminal state '{item.State}'"));
        if (queuedOnlyPatch && item.State != WorkItemState.Queued)
            return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot edit item in state {item.State}; only Queued items are editable"));

        // ── DependsOn resolution + cycle check (no writes yet, may 400) ──────
        List<WorkItemId>? newDependsOn = null;
        if (depsPatch)
        {
            var (depErr, ids) = await ResolveAndValidateDependsOnAsync(
                body.DependsOn!, item.Id, item.ProjectId, _store, ct);
            if (depErr is not null) return CommandPlan<WorkItemPatchPlan>.Refused(depErr);
            newDependsOn = ids;
        }

        IReadOnlyDictionary<string, string>? normalisedPatchKnobs = null;
        if (body.Knobs is { } patchKnobs)
        {
            var (normalisedKnobs, knobErr) = WorkItemCreationService.NormaliseKnobs(patchKnobs, _knobs);
            if (knobErr is not null) return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(knobErr));
            normalisedPatchKnobs = normalisedKnobs!;
        }

        // ── AgentClassId validation (no writes yet, may 400) ─────────────────
        // Same bounds as the create path (≤200 chars), plus an existence check
        // against the live router catalog: an unknown class would otherwise
        // fall through to direct agent pick at dispatch and silently strand
        // the item outside the class the operator intended.
        string? newAgentClassId = null;
        string? oldAgentClassId = item.AgentClassId;
        if (agentClassPatch)
        {
            var (normalizedClassId, classIdError) = WorkItemFieldRules.NormalizeAgentClassId(body.AgentClassId);
            if (classIdError is not null)
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(classIdError));
            var trimmed = normalizedClassId!;
            var knownClasses = _router.ClassIds;
            if (!knownClasses.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                return CommandPlan<WorkItemPatchPlan>.Refused(new WorkItemCommandOutcome(
                    StatusCodes.Status400BadRequest,
                    new
                    {
                        error = $"unknown agent class '{trimmed}'",
                        available = knownClasses.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
                    },
                    Error: $"unknown agent class '{trimmed}'"));
            newAgentClassId = trimmed;

            // Refuse while a worker holds the item rather than racing the
            // dispatch path, which may be resolving the class concurrently.
            // Checked last (closest to the write) to minimise the check/write
            // gap; the store's terminal guard below still fails closed on a
            // concurrent transition.
            try
            {
                var idStr = item.Id.ToString();
                var workers = await _registry.ListAsync(ct);
                foreach (var worker in workers)
                {
                    if (string.Equals(worker.CurrentWorkItemId, idStr, StringComparison.OrdinalIgnoreCase))
                        return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.Conflict(
                            $"cannot change agent class while worker '{worker.WorkerId}' holds work item '{item.Id}'"));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.Conflict(
                    $"cannot change agent class of work item '{item.Id}': failed to inspect worker bindings: {ex.Message}"));
            }
        }

        var updated = item;
        var now = DateTimeOffset.UtcNow;
        var queuedUpdateExpectedUpdatedAt = item.UpdatedAt;
        // When the knob map also rides this patch, the prompt joins the
        // combined guarded row+knob write (one transaction covers both, and
        // the plan-clearing below persists with them); otherwise the prompt
        // takes the dedicated prompt write inside commit. The in-memory
        // projection is identical either way — the commit's replace call
        // reports the authoritative revision back over the +1 estimate.
        var promptViaReplace = body.Prompt is not null && normalisedPatchKnobs is null;

        if (body.Title is not null)
        {
            var (newTitle, titleError) = WorkItemFieldRules.NormalizeTitle(body.Title);
            if (titleError is not null)
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(titleError));
            updated = updated with { Title = newTitle!, UpdatedAt = now };
        }

        if (body.Prompt is not null)
        {
            var (newPrompt, promptError) = WorkItemFieldRules.NormalizePrompt(body.Prompt);
            if (promptError is not null)
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(promptError));
            updated = updated with
            {
                Prompt = newPrompt!,
                PromptRevision = updated.PromptRevision + 1,
                UpdatedAt = now,
            };
        }

        if (body.Agent is not null)
        {
            var kind = new AgentKind(body.Agent);
            if (!_agents.TryGet(kind, out _))
                return CommandPlan<WorkItemPatchPlan>.Refused(new WorkItemCommandOutcome(
                    StatusCodes.Status400BadRequest,
                    new
                    {
                        error = $"unknown agent '{Validation.DescribeUntrustedValue(body.Agent)}'",
                        available = _agents.Available.Select(a => a.Value),
                    },
                    Error: $"unknown agent '{Validation.DescribeUntrustedValue(body.Agent)}'"));
            updated = updated with { Agent = kind, UpdatedAt = now };
        }

        if (body.WorkTimeoutMinutes is { } w)
            updated = updated with { WorkTimeout = WorkItemFieldRules.ClampWorkTimeoutMinutes(w), UpdatedAt = now };

        if (body.MergeTimeoutMinutes is { } m)
            updated = updated with { MergeTimeout = WorkItemFieldRules.ClampMergeTimeoutMinutes(m), UpdatedAt = now };

        if (body.MinModelScore is { } minScore)
            updated = updated with { MinModelScore = WorkItemFieldRules.ClampMinModelScore(minScore), UpdatedAt = now };

        if (body.RequiredCapabilities is { } patchCaps)
        {
            var (normalised, capErr) = WorkItemFieldRules.NormalizeRequiredCapabilities(patchCaps);
            if (capErr is not null)
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(capErr));
            updated = updated with { RequiredCapabilities = normalised!, UpdatedAt = now };
        }

        if (normalisedPatchKnobs is not null)
        {
            updated = updated with { Knobs = normalisedPatchKnobs, UpdatedAt = now };
        }

        if (queuedOnlyPatch)
            updated = ClearPlanReview(updated) with { UpdatedAt = now };

        if (body.AuditMaxIterations is { } auditMaxIterations)
        {
            var auditMaxIterationsError = WorkItemFieldRules.CheckAuditMaxIterations(auditMaxIterations);
            if (auditMaxIterationsError is not null)
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(auditMaxIterationsError));
            updated = updated with { AuditMaxIterations = auditMaxIterations, UpdatedAt = now };
        }

        if (body.AuditComplexity is not null)
        {
            var (normalised, complexityErr) = WorkItemFieldRules.NormalizeAuditComplexity(body.AuditComplexity);
            if (complexityErr is not null)
                return CommandPlan<WorkItemPatchPlan>.Refused(WorkItemCommandOutcome.BadRequest(complexityErr));
            updated = updated with { AuditComplexity = normalised, UpdatedAt = now };
        }

        if (newAgentClassId is not null)
            updated = updated with { AgentClassId = newAgentClassId, UpdatedAt = now };

        var oldDependsOn = updated.DependsOn;
        if (depsPatch)
            updated = updated with { DependsOn = newDependsOn!, UpdatedAt = now };

        return CommandPlan<WorkItemPatchPlan>.Ready(new WorkItemPatchPlan(
            item, updated, body,
            depsPatch, queuedOnlyPatch, queuedRowPatch, auditBudgetPatch, agentClassPatch,
            newDependsOn, oldDependsOn, normalisedPatchKnobs,
            newAgentClassId, oldAgentClassId, promptViaReplace,
            now, queuedUpdateExpectedUpdatedAt));
    }

    /// <summary>
    /// Performs the writes a validated <see cref="WorkItemPatchPlan"/>
    /// describes, in the same order the endpoint did: prompt replace (when the
    /// prompt travels alone), the guarded queued-row update, the audit-budget
    /// write, the agent-class write, the dependency write, then audit entries
    /// and the dispatcher kick.
    /// </summary>
    public async Task<WorkItemCommandOutcome> CommitPatchAsync(WorkItemPatchPlan plan, CancellationToken ct)
    {
        var item = plan.Item;
        var body = plan.Body;
        var updated = plan.Updated;
        var now = plan.Now;
        var queuedUpdateExpectedUpdatedAt = plan.QueuedUpdateExpectedUpdatedAt;
        var newDependsOn = plan.NewDependsOn;
        var oldDependsOn = plan.OldDependsOn;
        var newAgentClassId = plan.NewAgentClassId;
        var oldAgentClassId = plan.OldAgentClassId;
        var normalisedPatchKnobs = plan.NormalisedKnobs;

        if (plan.PromptViaReplace)
        {
            // Route through TryReplacePromptAsync — the only write path that
            // touches prompt + prompt_revision. The full-row UPDATE below
            // deliberately does NOT carry the prompt columns (they would
            // clobber a concurrent PUT /workitems/{id}/prompt). The state
            // guard inside TryReplacePromptAsync mirrors the Queued check
            // above; success refreshes our in-memory snapshot for the rest
            // of the PATCH so the response DTO reflects the new revision.
            var promptResult = await _store.TryReplacePromptAsync(updated.Id, updated.Prompt, now, ct);
            if (promptResult.Outcome == PromptReplaceOutcome.NotFound)
                return WorkItemCommandOutcome.NotFound($"work item '{item.Id}' no longer exists");
            if (promptResult.Outcome == PromptReplaceOutcome.TerminalState)
                return WorkItemCommandOutcome.Conflict("cannot edit item in terminal state");
            updated = updated with
            {
                PromptRevision = promptResult.NewRevision ?? updated.PromptRevision,
            };
            queuedUpdateExpectedUpdatedAt = now;
        }

        // ── Persist ──────────────────────────────────────────────────────────
        // Queued-only fields go through a guarded row UPDATE. Any PATCH that
        // touches the knob map routes through the combined guarded row+knob
        // write so the plan-clearing performed by ClearPlanReview above (which
        // runs for every queuedOnlyPatch, knobs included) is persisted in the
        // same transaction as the knob replacement — a partial knob write would
        // leave stale plan_* columns behind. When knobs and audit budget fields
        // are sent together, include the audit budget in that same guarded
        // write; otherwise the later audit-budget write could conflict after
        // the knob map had already been replaced.
        var auditBudgetWrittenWithQueuedUpdate = normalisedPatchKnobs is not null && plan.AuditBudgetPatch;
        var needsQueuedRowUpdate =
            plan.QueuedRowPatch
            || normalisedPatchKnobs is not null
            || (plan.DepsPatch && plan.QueuedOnlyPatch)
            || auditBudgetWrittenWithQueuedUpdate;
        if (needsQueuedRowUpdate)
        {
            var queuedUpdate = plan.AuditBudgetPatch && !auditBudgetWrittenWithQueuedUpdate
                ? updated with
                {
                    AuditMaxIterations = item.AuditMaxIterations,
                    AuditComplexity = item.AuditComplexity,
                }
                : updated;
            // Guard state and updated_at so queued edits cannot be written over
            // a concurrent pickup or another accepted queued-field patch.
            var written = normalisedPatchKnobs is not null
                ? await _store.TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
                    queuedUpdate,
                    WorkItemState.Queued,
                    queuedUpdateExpectedUpdatedAt,
                    ct)
                : await _store.TryUpdateIfStateAndUpdatedAtAsync(
                    queuedUpdate,
                    WorkItemState.Queued,
                    queuedUpdateExpectedUpdatedAt,
                    ct);
            if (!written)
                return WorkItemCommandOutcome.Conflict("item changed before the queued-field update could be written");
            queuedUpdateExpectedUpdatedAt = now;
        }
        if (plan.AuditBudgetPatch && !auditBudgetWrittenWithQueuedUpdate)
        {
            var budgetResult = await _store.UpdateAuditBudgetAsync(
                updated.Id,
                updated.AuditMaxIterations,
                updated.AuditComplexity,
                now,
                ct);
            switch (budgetResult.Outcome)
            {
                case AuditBudgetUpdateOutcome.NotFound:
                    return WorkItemCommandOutcome.NotFound($"work item '{item.Id}' no longer exists");
                case AuditBudgetUpdateOutcome.TerminalState:
                    return WorkItemCommandOutcome.Conflict(
                        $"work item transitioned to terminal state '{budgetResult.Item!.State}' before audit budget could be updated");
                case AuditBudgetUpdateOutcome.Updated:
                    updated = budgetResult.Item ?? updated;
                    break;
            }
        }
        // AgentClassId on a Queued item with other queued edits rides the
        // guarded row UPDATE above (its SQL carries agent_class_id); every
        // other case — notably non-Queued items, where the guarded write is
        // unavailable — goes through the terminal-guarded partial UPDATE so
        // pipeline-owned columns are never stomped.
        if (plan.AgentClassPatch && !needsQueuedRowUpdate)
        {
            var classResult = await _store.UpdateAgentClassAsync(
                updated.Id,
                newAgentClassId,
                now,
                ct);
            switch (classResult.Outcome)
            {
                case AgentClassUpdateOutcome.NotFound:
                    return WorkItemCommandOutcome.NotFound($"work item '{item.Id}' no longer exists");
                case AgentClassUpdateOutcome.TerminalState:
                    return WorkItemCommandOutcome.Conflict(
                        $"work item transitioned to terminal state '{classResult.Item!.State}' before agent class could be updated");
                case AgentClassUpdateOutcome.Updated:
                    oldAgentClassId = classResult.OldAgentClassId ?? oldAgentClassId;
                    updated = classResult.Item ?? updated with { AgentClassId = newAgentClassId, UpdatedAt = now };
                    break;
            }
        }
        if (plan.DepsPatch && !plan.QueuedOnlyPatch)
        {
            var depResult = await _store.UpdateDependsOnAsync(updated.Id, newDependsOn!, now, ct);
            switch (depResult.Outcome)
            {
                case DependsOnUpdateOutcome.NotFound:
                    return WorkItemCommandOutcome.NotFound($"work item '{item.Id}' no longer exists");
                case DependsOnUpdateOutcome.TerminalState:
                    return WorkItemCommandOutcome.Conflict(
                        $"work item transitioned to terminal state '{depResult.Item!.State}' before dependencies could be updated");
            }
            oldDependsOn = depResult.OldDependsOn ?? oldDependsOn;
            updated = depResult.Item ?? updated with { DependsOn = newDependsOn!, UpdatedAt = now };
        }

        if (plan.QueuedOnlyPatch || plan.AuditBudgetPatch)
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
                auditBudgetChanged: plan.AuditBudgetPatch,
                knobsChanged: body.Knobs is not null);
        }
        if (plan.DepsPatch)
            AuditLog.WorkItemDependenciesChanged(updated.Id, oldDependsOn, newDependsOn!);
        if (plan.AgentClassPatch)
            AuditLog.WorkItemAgentClassChanged(updated.Id, oldAgentClassId, newAgentClassId);

        var statesById = new Dictionary<WorkItemId, WorkItemState>();
        var depExternalIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in updated.DependsOn)
        {
            var dep = await _store.GetAsync(depId, ct);
            if (dep is not null)
            {
                statesById[depId] = dep.State;
                depExternalIds[depId] = dep.ExternalId;
            }
        }

        // If the dep edit on a Queued item left all deps satisfied (typical for
        // dependsOn=[]), kick the dispatcher so it picks the item up immediately
        // instead of waiting for the next scan tick. Mirrors the Create path.
        if (plan.DepsPatch
            && updated.State == WorkItemState.Queued
            && WorkItemDependencies.AreSatisfied(updated.DependsOn, statesById))
        {
            await _queue.EnqueueAsync(updated.Id, ct);
        }

        var project = await _projects.GetAsync(updated.ProjectId, ct);
        return new WorkItemCommandOutcome(
            StatusCodes.Status200OK,
            WorkItemEndpoints.ToDto(updated, project, statesById, depExternalIds),
            Item: updated);
    }

    public async Task<WorkItemCommandOutcome> PatchAsync(
        WorkItem item, PatchWorkItemRequest body, CancellationToken ct)
    {
        var (plan, error) = await PlanPatchAsync(item, body, ct);
        return error ?? await CommitPatchAsync(plan!, ct);
    }

    private static WorkItem ClearPlanReview(WorkItem item) => item with
    {
        PlanArtifact = null,
        PlanGeneratedAt = null,
        PlanReviewedAt = null,
        PlanReviewSummary = null,
        PlanReviewAttempts = 0,
    };

    /// <summary>
    /// Shared resolver for the <c>dependsOn</c> string array used by create and
    /// PATCH. Entries may be GUIDs, namespaced externalIds ('ns:value'), or bare
    /// externalIds. Returns an error (bad entry, self-dependency, missing
    /// target, ambiguity, cycle) or the resolved IDs in request order.
    /// </summary>
    private async Task<(WorkItemCommandOutcome? Error, List<WorkItemId>? Ids)> ResolveAndValidateDependsOnAsync(
        string[] rawDeps,
        WorkItemId targetId,
        ProjectId projectId,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (WorkItemFieldRules.CheckDependsOnCount(rawDeps.Length) is { } depsCountError)
            return (WorkItemCommandOutcome.BadRequest(depsCountError), null);

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
                return (WorkItemCommandOutcome.BadRequest("dependency could not be resolved: null entry in dependsOn array"), null);
            if (Guid.TryParse(rawId, out var g))
            {
                dependsOnIds.Add(new WorkItemId(g));
                continue;
            }
            if (Validation.TryParseNamespacedExternalId(rawId, out var depNs, out var depValue) && depNs is not null)
            {
                if (!byNamespacedExternalId.TryGetValue((depNs, depValue), out var depByNs))
                    return (new WorkItemCommandOutcome(
                        StatusCodes.Status400BadRequest,
                        new
                        {
                            error = $"dependency '{Validation.DescribeUntrustedValue(rawId)}' could not be resolved: no work item with externalId '{Validation.DescribeUntrustedValue(depValue)}' in namespace '{depNs}' in project '{projectId}'",
                        },
                        Error: $"dependency '{Validation.DescribeUntrustedValue(rawId)}' could not be resolved: no work item with externalId '{Validation.DescribeUntrustedValue(depValue)}' in namespace '{depNs}' in project '{projectId}'"), null);
                dependsOnIds.Add(depByNs.Id);
                continue;
            }
            if (!byBareExternalId.TryGetValue(rawId, out var matches) || matches.Count == 0)
                return (new WorkItemCommandOutcome(
                    StatusCodes.Status400BadRequest,
                    new
                    {
                        error = $"dependency '{Validation.DescribeUntrustedValue(rawId)}' could not be resolved: no work item with externalId '{Validation.DescribeUntrustedValue(rawId)}' in project '{projectId}'",
                    },
                    Error: $"dependency '{Validation.DescribeUntrustedValue(rawId)}' could not be resolved: no work item with externalId '{Validation.DescribeUntrustedValue(rawId)}' in project '{projectId}'"), null);
            var distinctItems = matches.Select(m => m.Item.Id).Distinct().ToList();
            if (distinctItems.Count > 1)
                return (new WorkItemCommandOutcome(
                    StatusCodes.Status400BadRequest,
                    new
                    {
                        error = $"dependency '{Validation.DescribeUntrustedValue(rawId)}' is ambiguous: matches multiple work items via namespaces {string.Join(", ", matches.Select(m => m.Namespace).Distinct())} — qualify as 'namespace:value'",
                    },
                    Error: $"dependency '{Validation.DescribeUntrustedValue(rawId)}' is ambiguous"), null);
            dependsOnIds.Add(distinctItems[0]);
        }

        if (dependsOnIds.Contains(targetId))
            return (WorkItemCommandOutcome.BadRequest("a work item cannot depend on itself"), null);

        var missingDep = WorkItemDependencies.FindMissingDependency(dependsOnIds, allItems);
        if (missingDep is not null)
            return (WorkItemCommandOutcome.BadRequest($"dependency {missingDep} not found"), null);

        // Cycle detection: FindCycle overrides adj[targetId] = dependsOnIds in
        // the existing graph, so passing the existing item's own id correctly
        // models the edit case (its old deps are replaced before DFS).
        var cyclePath = WorkItemDependencies.FindCycle(targetId, dependsOnIds, allItems);
        if (cyclePath is not null)
            return (WorkItemCommandOutcome.BadRequest($"circular dependency detected: {cyclePath}"), null);

        return (null, dependsOnIds);
    }

    // ── PATCH /workitems/{id}/priority ───────────────────────────────────────

    internal sealed record WorkItemPriorityPlan(
        WorkItem Item,
        int Priority,
        Project Project,
        bool AlreadyAtPriority);

    /// <summary>
    /// Validates a priority change: project lookup, per-project ceiling, and
    /// the terminal-state gate. Pure — no writes.
    /// </summary>
    public async Task<CommandPlan<WorkItemPriorityPlan>> PlanPriorityAsync(
        WorkItem item, int priority, CancellationToken ct)
    {
        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null)
            return CommandPlan<WorkItemPriorityPlan>.Refused(
                WorkItemCommandOutcome.BadRequest($"unknown project '{item.ProjectId}'"));

        var priorityError = ValidatePriority(priority, project);
        if (priorityError is not null)
            return CommandPlan<WorkItemPriorityPlan>.Refused(priorityError);

        if (WorkItemDependencies.TerminalStates.Contains(item.State))
            return CommandPlan<WorkItemPriorityPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot change priority of work item in terminal state '{item.State}'"));

        return CommandPlan<WorkItemPriorityPlan>.Ready(
            new WorkItemPriorityPlan(item, priority, project, item.Priority == priority));
    }

    public async Task<WorkItemCommandOutcome> CommitPriorityAsync(WorkItemPriorityPlan plan, CancellationToken ct)
    {
        var item = plan.Item;
        if (plan.AlreadyAtPriority)
            return new WorkItemCommandOutcome(
                StatusCodes.Status200OK,
                new { id = item.Id.ToString(), priority = plan.Priority, status = "no-op" },
                Item: item);

        var result = await _store.UpdatePriorityAsync(item.Id, plan.Priority, DateTimeOffset.UtcNow, ct);
        switch (result.Outcome)
        {
            case PriorityUpdateOutcome.NotFound:
                return WorkItemCommandOutcome.NotFound($"work item '{item.Id}' no longer exists");
            case PriorityUpdateOutcome.TerminalState:
                // The item raced into a terminal state between the read above and
                // the partial UPDATE; surface 409 like the pre-check would have.
                return WorkItemCommandOutcome.Conflict(
                    $"work item transitioned to terminal state '{result.Item!.State}' before priority could be updated");
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
            await _queue.EnqueueAsync(updated.Id, ct);

        return new WorkItemCommandOutcome(
            StatusCodes.Status200OK,
            new { id = updated.Id.ToString(), priority = updated.Priority },
            Item: updated);
    }

    public async Task<WorkItemCommandOutcome> PriorityAsync(
        WorkItem item, int priority, CancellationToken ct)
    {
        var (plan, error) = await PlanPriorityAsync(item, priority, ct);
        return error ?? await CommitPriorityAsync(plan!, ct);
    }

    /// <summary>
    /// Validates a requested priority against the project's ceiling. Returns
    /// the refusal outcome when the value is outside the allowed range.
    /// </summary>
    private static WorkItemCommandOutcome? ValidatePriority(int priority, Project project)
    {
        var error = WorkItemFieldRules.CheckPriorityBounds(priority)
            ?? WorkItemFieldRules.CheckProjectPriorityCeiling(priority, project);
        return error is null ? null : WorkItemCommandOutcome.BadRequest(error);
    }

    // ── PATCH /workitems/{id}/external-ids ───────────────────────────────────

    internal sealed record WorkItemExternalIdsPlan(
        WorkItem Item,
        IReadOnlyDictionary<string, string> Resulting);

    /// <summary>
    /// Builds the resulting external-id map and pre-checks conflicts on newly
    /// assigned values. Pure — no writes.
    /// </summary>
    public async Task<CommandPlan<WorkItemExternalIdsPlan>> PlanExternalIdsAsync(
        WorkItem item, PatchExternalIdsRequest body, CancellationToken ct)
    {
        if (body.ExternalIds is null)
            return CommandPlan<WorkItemExternalIdsPlan>.Refused(
                WorkItemCommandOutcome.BadRequest("externalIds field is required"));

        // Build the resulting map. Start from current (merge) or empty (replace),
        // then apply the patch — string values set/overwrite, null values delete.
        var resulting = body.ReplaceExternalIds == true
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(item.ExternalIds, StringComparer.OrdinalIgnoreCase);

        foreach (var (ns, value) in body.ExternalIds)
        {
            // ns is untrusted input echoed into the error field label — strip
            // control characters and bound it before interpolating.
            var nsLabel = Validation.DescribeUntrustedValue(ns);
            try { Validation.ValidateExternalIdNamespace(ns, $"externalIds key '{nsLabel}'"); }
            catch (ArgumentException ex) { return CommandPlan<WorkItemExternalIdsPlan>.Refused(WorkItemCommandOutcome.BadRequest(ex.Message)); }
            if (value is null)
            {
                resulting.Remove(ns);
                continue;
            }
            try { Validation.ValidateExternalId(value, $"externalIds['{nsLabel}']"); }
            catch (ArgumentException ex) { return CommandPlan<WorkItemExternalIdsPlan>.Refused(WorkItemCommandOutcome.BadRequest(ex.Message)); }
            resulting[ns] = value;
        }

        if (resulting.Count > WorkItemLimits.MaxExternalIds)
            return CommandPlan<WorkItemExternalIdsPlan>.Refused(WorkItemCommandOutcome.BadRequest(
                $"externalIds may contain at most {WorkItemLimits.MaxExternalIds} entries per work item"));

        // Pre-check for conflicts on namespaced IDs newly assigned to this item
        // (additions and changed values). We don't pre-check unchanged entries —
        // they already belong to this item.
        foreach (var (ns, value) in resulting)
        {
            if (item.ExternalIds.TryGetValue(ns, out var existing) && existing == value)
                continue;
            var other = await _store.GetByNamespacedExternalIdAsync(item.ProjectId, ns, value, ct);
            if (other is not null && other.Id != item.Id)
                return CommandPlan<WorkItemExternalIdsPlan>.Refused(new WorkItemCommandOutcome(
                    StatusCodes.Status409Conflict,
                    new
                    {
                        error = $"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {other.Id} (state: {other.State})"
                    },
                    Error: $"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {other.Id} (state: {other.State})"));
        }

        return CommandPlan<WorkItemExternalIdsPlan>.Ready(new WorkItemExternalIdsPlan(item, resulting));
    }

    public async Task<WorkItemCommandOutcome> CommitExternalIdsAsync(WorkItemExternalIdsPlan plan, CancellationToken ct)
    {
        var item = plan.Item;
        WorkItem? updated;
        try
        {
            updated = await _store.ReplaceExternalIdsAsync(item.Id, plan.Resulting, DateTimeOffset.UtcNow, ct);
        }
        catch (WorkItemExternalIdConflictException)
        {
            // Re-probe to surface the colliding namespaced ID after a race.
            foreach (var (ns, value) in plan.Resulting)
            {
                var other = await _store.GetByNamespacedExternalIdAsync(item.ProjectId, ns, value, ct);
                if (other is not null && other.Id != item.Id)
                    return new WorkItemCommandOutcome(
                        StatusCodes.Status409Conflict,
                        new
                        {
                            error = $"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {other.Id} (state: {other.State})"
                        },
                        Error: $"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {other.Id} (state: {other.State})");
            }
            return WorkItemCommandOutcome.Conflict("external id conflict (concurrent duplicate)");
        }
        if (updated is null)
            return WorkItemCommandOutcome.NotFound($"work item '{item.Id}' no longer exists");

        var project = await _projects.GetAsync(updated.ProjectId, ct);
        var depStates = new Dictionary<WorkItemId, WorkItemState>();
        var depExtIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in updated.DependsOn)
        {
            var dep = await _store.GetAsync(depId, ct);
            if (dep is not null)
            {
                depStates[depId] = dep.State;
                depExtIds[depId] = dep.ExternalId;
            }
        }
        return new WorkItemCommandOutcome(
            StatusCodes.Status200OK,
            WorkItemEndpoints.ToDto(updated, project, depStates, depExtIds),
            Item: updated);
    }

    public async Task<WorkItemCommandOutcome> ExternalIdsAsync(
        WorkItem item, PatchExternalIdsRequest body, CancellationToken ct)
    {
        var (plan, error) = await PlanExternalIdsAsync(item, body, ct);
        return error ?? await CommitExternalIdsAsync(plan!, ct);
    }

    // ── DELETE /workitems/{id} (cancel) ──────────────────────────────────────

    internal enum WorkItemCancelKind
    {
        /// <summary>Terminal-failure bookkeeping close-out: write Cancelled directly.</summary>
        CloseTerminalFailure,

        /// <summary>Already cancelled — a no-op success, not a conflict.</summary>
        AlreadyCancelled,

        /// <summary>A live item: signal the worker (if any) and write Cancelled.</summary>
        Cancel,
    }

    internal sealed record WorkItemCancelPlan(
        WorkItem Item,
        WorkItemCancelKind Kind,
        string? Reason,
        string? ResolutionSha);

    /// <summary>
    /// Validates the close-out metadata and classifies the cancel by item
    /// state. Pure — no writes; the worker-binding check happens inside the
    /// commit because the cancel signal is itself the mutation.
    /// </summary>
    public CommandPlan<WorkItemCancelPlan> PlanCancel(
        WorkItem item, string? reason, string? resolutionSha)
    {
        // Validate optional close-out metadata. Same shape as /resume's reason
        // guard (no control chars, ≤500 chars); resolutionSha is a Git-shaped
        // hex SHA so triage tooling can link the manual-resolution commit.
        if (AgentPauseValidation.ValidateOptionalReason(reason, "reason") is { } reasonError)
            return CommandPlan<WorkItemCancelPlan>.Refused(WorkItemCommandOutcome.BadRequest(reasonError));
        if (resolutionSha is not null)
        {
            if (resolutionSha.Length is < 7 or > 40
                || !resolutionSha.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return CommandPlan<WorkItemCancelPlan>.Refused(
                    WorkItemCommandOutcome.BadRequest("resolutionSha must be a 7-40 character hex string"));
        }

        // Bookkeeping close-out path: when an operator resolves a terminal-failure
        // item out-of-band (e.g. manually merges after MergeConflictResolutionFailed),
        // DELETE used to 409 — leaving the item stranded forever. Transition it to
        // Cancelled with the same OperatorRequested reason as the in-flight path so
        // there is a single terminal-closed shape regardless of how it got there.
        if (IsTerminalFailureCloseable(item.State))
            return CommandPlan<WorkItemCancelPlan>.Ready(
                new WorkItemCancelPlan(item, WorkItemCancelKind.CloseTerminalFailure, reason, resolutionSha));

        // Idempotent close: an already-cancelled item is a no-op rather than 409.
        // Lets operator scripts and the audit UI retry DELETE safely.
        if (item.State == WorkItemState.Cancelled)
            return CommandPlan<WorkItemCancelPlan>.Ready(
                new WorkItemCancelPlan(item, WorkItemCancelKind.AlreadyCancelled, reason, resolutionSha));

        if (item.State == WorkItemState.Done)
            return CommandPlan<WorkItemCancelPlan>.Refused(
                WorkItemCommandOutcome.Conflict($"cannot cancel item in state {item.State}"));

        // A no-action-required resolution is a recorded determination, not a
        // live run: cancelling it would overwrite the preserved reasoning
        // with "cancelled via API". Like Done, it has nothing to cancel —
        // retry it instead if the precondition now holds.
        if (item.State == WorkItemState.NoActionRequired)
            return CommandPlan<WorkItemCancelPlan>.Refused(
                WorkItemCommandOutcome.Conflict($"cannot cancel item in state {item.State}"));

        return CommandPlan<WorkItemCancelPlan>.Ready(
            new WorkItemCancelPlan(item, WorkItemCancelKind.Cancel, reason, resolutionSha));
    }

    public async Task<WorkItemCommandOutcome> CommitCancelAsync(WorkItemCancelPlan plan, CancellationToken ct)
    {
        var item = plan.Item;
        var workItemId = item.Id;
        var reason = plan.Reason;
        var resolutionSha = plan.ResolutionSha;
        WorkItem? affected = null;

        if (plan.Kind == WorkItemCancelKind.CloseTerminalFailure)
        {
            var priorState = item.State;
            var lastError = BuildCloseLastError(priorState, reason, resolutionSha);
            var closed = item.With(WorkItemState.Cancelled, lastError,
                WorkItemCancellationReason.OperatorRequested);
            await _store.UpdateAsync(closed, ct);
            affected = closed;
            AuditLog.WorkItemCancelled(workItemId);
            if (_repoReaper is not null)
                await _repoReaper.TryReapWorkItemAsync(workItemId, CancellationToken.None);
            var project = await _projects.GetAsync(item.ProjectId, ct);
            if (project is not null)
                await _webhooks.PublishAsync(new WebhookEvent
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
            return new WorkItemCommandOutcome(
                StatusCodes.Status202Accepted, Location: $"/workitems/{workItemId}", Item: affected);
        }

        if (plan.Kind == WorkItemCancelKind.AlreadyCancelled)
            return new WorkItemCommandOutcome(
                StatusCodes.Status202Accepted, Location: $"/workitems/{workItemId}", Item: item);

        var wasActive = _cancellations.Cancel(workItemId);
        if (!wasActive)
        {
            var lastError = BuildCloseLastError(item.State, reason, resolutionSha)
                ?? "cancelled via API";
            var cancelled = item.With(WorkItemState.Cancelled, lastError,
                WorkItemCancellationReason.OperatorRequested);
            await _store.UpdateAsync(cancelled, ct);
            affected = cancelled;
            AuditLog.WorkItemCancelled(workItemId);
            if (_repoReaper is not null)
                await _repoReaper.TryReapWorkItemAsync(workItemId, CancellationToken.None);
            var project = await _projects.GetAsync(item.ProjectId, ct);
            if (project is not null)
                await _webhooks.PublishAsync(new WebhookEvent
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
            if (_timings is not null)
                await _timings.DeleteByWorkItemAsync(workItemId, ct);
        }

        // Cascade: cancel all Queued items that (transitively) depend on this
        // one. In-flight items (non-Queued) are left to run their course.
        var cascaded = await CascadeCancelDependentsAsync(workItemId, _store, ct);

        // Orphan any replays: clear their replay_of link so they keep running
        // but are no longer linked to the (now-cancelled) source.
        await _store.OrphanReplaysAsync(workItemId, ct);

        return new WorkItemCommandOutcome(
            StatusCodes.Status202Accepted,
            Location: $"/workitems/{workItemId}",
            Item: affected,
            AlsoAffected: cascaded);
    }

    public async Task<WorkItemCommandOutcome> CancelAsync(
        WorkItem item, string? reason, string? resolutionSha, CancellationToken ct)
    {
        var (plan, error) = PlanCancel(item, reason, resolutionSha);
        return error ?? await CommitCancelAsync(plan!, ct);
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

    /// <summary>
    /// Cancels every Queued item that transitively depends on
    /// <paramref name="cancelledId"/>; returns the ids actually transitioned.
    /// </summary>
    private async Task<IReadOnlyList<WorkItemId>> CascadeCancelDependentsAsync(
        WorkItemId cancelledId,
        IWorkItemStore store,
        CancellationToken ct)
    {
        var allItems = new List<WorkItem>();
        await foreach (var i in store.ListAsync(ct)) allItems.Add(i);

        var targets = WorkItemDependencies.FindCascadeCancelTargets(cancelledId, allItems);
        var cascaded = new List<WorkItemId>(targets.Count);
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
            {
                cascaded.Add(target.Id);
                AuditLog.WorkItemDependentCancelled(target.Id, cancelledId);
            }
        }

        return cascaded;
    }

    // ── POST /workitems/{id}/retry ───────────────────────────────────────────

    internal sealed record WorkItemRetryPlan(
        WorkItem Item,
        string? RequestedFrom,
        /// <summary>
        /// True when the item sits in a worker-occupiable stale state and a
        /// worker still holds it — the commit must fence it through the
        /// stale-progress watchdog before retrying.
        /// </summary>
        bool NeedsStaleFence);

    /// <summary>
    /// Applies the retryable-state gate and — for worker-occupiable states —
    /// the stale-worker eligibility check and the worker-binding probe. Pure:
    /// the fence itself is a mutation and lives in the commit.
    /// </summary>
    public async Task<CommandPlan<WorkItemRetryPlan>> PlanRetryAsync(
        WorkItem item, string? requestedFrom, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(requestedFrom)
            ? null
            : requestedFrom.Trim().ToLowerInvariant();

        // Only resume from terminal-failed states or parked states
        // (NeedsOperatorInput for operator triage, WaitingForQuotaReset /
        // WaitingForTransientRetry for operator override of the schedulers,
        // WaitingForAgentResume for operator override of per-agent runtime
        // pause controls).
        // NoActionRequired items are retryable too: the precondition may hold
        // on a later run, so the operator can re-run the item from scratch.
        // Done items have nothing to retry; other non-terminal states would
        // race the pipeline — except a stale worker-held item, which is
        // fenced first (see below).
        if (item.State is WorkItemState.Failed or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed or WorkItemState.Cancelled
            or WorkItemState.AbandonedAfterRecoveryAttempts
            or WorkItemState.NoActionRequired
            or WorkItemState.NeedsOperatorInput
            or WorkItemState.WaitingForQuotaReset
            or WorkItemState.WaitingForAgentResume
            or WorkItemState.WaitingForTransientRetry)
        {
            return CommandPlan<WorkItemRetryPlan>.Ready(
                new WorkItemRetryPlan(item, normalized, NeedsStaleFence: false));
        }

        var staleTimeout = _options.CurrentValue.WorkerProgressWatchdog.ResolveItemStaleTimeout(item.Agent);
        if (!IsStaleWorkerRetryEligible(item, DateTimeOffset.UtcNow, staleTimeout))
        {
            return CommandPlan<WorkItemRetryPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot retry item in state {item.State}; only terminal-failed or operator-parked items can be retried"));
        }

        var idStr = item.Id.ToString();
        var bound = false;
        try
        {
            var workers = await _registry.ListAsync(ct);
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
            return CommandPlan<WorkItemRetryPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot retry stale worker-held item {item.Id}: failed to inspect worker bindings: {ex.Message}"));
        }

        if (!bound)
        {
            return CommandPlan<WorkItemRetryPlan>.Refused(WorkItemCommandOutcome.Conflict(
                $"cannot retry item in state {item.State}; only terminal-failed or operator-parked items can be retried"));
        }

        return CommandPlan<WorkItemRetryPlan>.Ready(
            new WorkItemRetryPlan(item, normalized, NeedsStaleFence: true));
    }

    public async Task<WorkItemCommandOutcome> CommitRetryAsync(
        WorkItemRetryPlan plan, int? workTimeoutMinutes, CancellationToken ct)
    {
        var item = plan.Item;
        if (plan.NeedsStaleFence)
        {
            var sinceProgressSeconds = (long)(DateTimeOffset.UtcNow - item.UpdatedAt).TotalSeconds;
            var recovery = await _staleWatchdog.RecoverItemAsync(
                item,
                $"operator retry from '{plan.RequestedFrom ?? "auto"}' fenced stale worker-held item in {item.State} with no progress for {sinceProgressSeconds}s",
                ct);
            if (!recovery.Recovered)
            {
                return WorkItemCommandOutcome.Conflict(
                    $"cannot retry stale worker-held item {item.Id}: {recovery.Error ?? "recovery did not transition the work item"}");
            }

            var fenced = await _store.GetAsync(item.Id, ct);
            if (fenced is null)
                return WorkItemCommandOutcome.Conflict("work item no longer exists");
            item = fenced;
        }

        var (success, error, resumeState, actualFrom, openQuestions) = await _retrier.RetryAsync(
            item,
            plan.RequestedFrom,
            trigger: "manual",
            ct: ct,
            workTimeoutMinutes: workTimeoutMinutes);

        if (!success)
        {
            if (openQuestions is { Count: > 0 })
                return new WorkItemCommandOutcome(
                    StatusCodes.Status409Conflict, new { error, openQuestions }, Error: error);

            if (error!.Contains("no longer exists"))
                return new WorkItemCommandOutcome(
                    StatusCodes.Status409Conflict,
                    new { error, hint = "retry with from=\"work\" to start over from a fresh clone" },
                    Error: error);

            return WorkItemCommandOutcome.Conflict(error!);
        }

        return new WorkItemCommandOutcome(
            StatusCodes.Status202Accepted,
            new { id = item.Id.ToString(), from = plan.RequestedFrom ?? "auto", actualFrom = actualFrom!, state = resumeState!.Value.ToString() },
            Location: $"/workitems/{item.Id}",
            Item: item);
    }

    public async Task<WorkItemCommandOutcome> RetryAsync(
        WorkItem item, string? requestedFrom, int? workTimeoutMinutes, CancellationToken ct)
    {
        var (plan, error) = await PlanRetryAsync(item, requestedFrom, ct);
        return error ?? await CommitRetryAsync(plan!, workTimeoutMinutes, ct);
    }

    /// <summary>
    /// Pure eligibility gate for operator retry of a worker-occupied item:
    /// the item must sit in a worker-occupiable state and its
    /// <c>UpdatedAt</c> must be frozen past the item-stale window. A zero or
    /// negative timeout disables the gate (matches the watchdog sweep's
    /// per-agent opt-out semantics: no window, no staleness verdict).
    /// </summary>
    internal static bool IsStaleWorkerRetryEligible(WorkItem item, DateTimeOffset now, TimeSpan staleTimeout)
        => WorkItemRecoveryPolicy.IsItemStaleWatchedState(item.State)
            && staleTimeout > TimeSpan.Zero
            && item.UpdatedAt <= now - staleTimeout;
}
