using System.Text.Json;
using CodeyBox.Composition;
using CodeyBox.Core;
using CodeyBox.Majordomo;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Http;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// The MUTATE half of the majordomo surface. Every method validates the full
/// call before any write and — unless <c>commit</c> is set — stops at the
/// plan, so dry-run and Proposed-mode calls share exactly the machinery that
/// would mutate. The store is never written until every check has passed.
/// </summary>
/// <remarks>
/// Majordomo-specific strictness lives here rather than in the shared command
/// layer: dependencies on terminal items are refused outright (the REST
/// surface allows them and lets the gate idle; for a model caller the
/// correctable refusal is worth more than the silent park), and multi-part
/// updates plan all of their sub-edits before any write runs.
/// </remarks>
internal sealed class MajordomoMutateBackend
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemCreationService _creation;
    private readonly WorkItemCommandService _commands;

    public MajordomoMutateBackend(
        IWorkItemStore store,
        WorkItemCreationService creation,
        WorkItemCommandService commands)
    {
        _store = store;
        _creation = creation;
        _commands = commands;
    }

    /// <summary>One work item → <see cref="CreateWorkItemRequest"/>.</summary>
    private static CreateWorkItemRequest ToRequest(
        NewWorkItemSpec spec,
        WorkInitiator initiator,
        IEnumerable<string>? extraDependsOn = null) =>
        new(
            spec.ProjectId.Value,
            spec.Title,
            spec.Prompt,
            spec.Agent?.Value,
            spec.AuditorProfile,
            spec.AgentClassId,
            spec.BaseBranch,
            spec.WorkBranch,
            spec.PushUpstream,
            spec.WorkTimeout is { } wt ? ToWholeMinutes(wt, "work_timeout") : null,
            spec.MergeTimeout is { } mt ? ToWholeMinutes(mt, "merge_timeout") : null,
            ExternalId: null,
            DependsOn: spec.DependsOn.Select(d => d.ToString())
                .Concat(extraDependsOn ?? [])
                .ToArray(),
            MinModelScore: spec.MinModelScore,
            ReleaseId: spec.ReleaseId?.ToString(),
            Priority: spec.Priority,
            AuditMaxIterations: spec.AuditMaxIterations,
            AuditComplexity: spec.AuditComplexity,
            ExternalIds: spec.ExternalIds.Count == 0 ? null : spec.ExternalIds,
            Initiator: initiator,
            RequiredCapabilities: spec.RequiredCapabilities.Count == 0 ? null : spec.RequiredCapabilities,
            Check: null,
            AgentControl: null,
            IsRefactor: spec.IsRefactor ? true : null,
            Knobs: spec.Knobs.Count == 0 ? null : spec.Knobs);

    private static int ToWholeMinutes(TimeSpan value, string field)
    {
        // Contract-level durations are already validated; the command surface
        // speaks whole minutes. A sub-minute value is a refusal, never a
        // silent truncation — the caller sees the precise reason.
        if (value.Ticks % TimeSpan.TicksPerMinute != 0 || value.TotalMinutes > int.MaxValue)
            throw new MajordomoRefusalException(field,
                $"duration '{value}' is not a whole number of minutes — this surface expresses timeouts in minutes");
        return (int)value.TotalMinutes;
    }

    /// <summary>Raised by argument→request mapping when a value is unrepresentable downstream.</summary>
    private sealed class MajordomoRefusalException(string field, string message) : Exception(message)
    {
        public string Field { get; } = field;
    }

    /// <summary>
    /// Refuses when a dependency target is missing or already terminal. The
    /// create path itself re-checks existence; the terminal rule is
    /// majordomo-only: a dep that can never satisfy is a model mistake worth
    /// refusing, not a parked item.
    /// </summary>
    private async Task<MajordomoRefusal?> CheckDependencyTargetsAsync(
        IReadOnlyList<WorkItemId> dependsOn, string itemLabel, string field, CancellationToken ct)
    {
        foreach (var depId in dependsOn)
        {
            var dep = await _store.GetAsync(depId, ct).ConfigureAwait(false);
            if (dep is null)
                return new MajordomoRefusal(
                    "dependency_not_found",
                    $"{itemLabel}: dependency '{depId}' does not exist — pass the id of an existing work item",
                    Item: itemLabel,
                    Field: field);
            if (WorkItemDependencies.TerminalStates.Contains(dep.State))
                return new MajordomoRefusal(
                    "dependency_terminal",
                    $"{itemLabel}: dependency '{depId}' is already in terminal state {dep.State} — " +
                    "it can never satisfy the gate; drop the edge or retry the item first",
                    Item: itemLabel,
                    Field: field);
        }

        return null;
    }

    private static async Task<MajordomoMutationResult> RefusalFromResultAsync(IResult error)
    {
        // The existing creation/commit contracts report errors as HTTP results;
        // read the payload back so the refusal can carry the same detail text.
        var detail = await ResultText.ReadErrorTextAsync(error).ConfigureAwait(false);
        return MajordomoMutationResult.Refused(
            new MajordomoRefusal("validation_failed", detail));
    }

    // ── create_work_item ────────────────────────────────────────────────────

    public async Task<MajordomoMutationResult> CreateAsync(
        CreateWorkItemArgs args, WorkInitiator initiator, bool commit, CancellationToken ct)
    {
        var spec = args.Item;

        if (await CheckDependencyTargetsAsync(spec.DependsOn, spec.Title, "item.depends_on", ct).ConfigureAwait(false)
                is { } depRefusal)
            return MajordomoMutationResult.Refused(depRefusal);

        CreateWorkItemRequest req;
        try { req = ToRequest(spec, initiator); }
        catch (MajordomoRefusalException ex)
        {
            return MajordomoMutationResult.Refused(
                new MajordomoRefusal("invalid_arguments", ex.Message, Item: spec.Title, Field: ex.Field));
        }

        var prepared = await _creation.PrepareAsync(req, ct).ConfigureAwait(false);
        if (prepared.Error is not null)
            return await RefusalFromResultAsync(prepared.Error).ConfigureAwait(false);

        if (!commit)
            return MajordomoMutationResult.Planned(
                new MajordomoChangeSet(true, [new MajordomoPlannedChange.CreateItem(spec)], []));

        var committed = await _creation.CommitAsync(prepared.Prepared!, ct).ConfigureAwait(false);
        if (committed.Error is not null)
            return await RefusalFromResultAsync(committed.Error).ConfigureAwait(false);

        return MajordomoMutationResult.Committed(
            new MajordomoChangeSet(false, [new MajordomoPlannedChange.CreateItem(spec)], [committed.Item.Id]));
    }

    // ── create_work_item_chain ──────────────────────────────────────────────

    public async Task<MajordomoMutationResult> CreateChainAsync(
        CreateWorkItemChainArgs args, WorkInitiator initiator, bool commit, CancellationToken ct)
    {
        var nodes = args.Items;

        // Whole-set review: dangling/forward/self edges, cycles, refactor
        // exclusivity, and external-id collisions between nodes — before any
        // store round-trip, so a partially valid chain cannot partially file.
        var review = CompositionReview.ValidateStructured(nodes
            .Select(n => new StructuredCompositionItem(
                n.DependsOnIndexes,
                n.Item.ExternalIds.Count == 0 ? null : n.Item.ExternalIds,
                n.Item.IsRefactor))
            .ToList());
        if (review.Count > 0)
        {
            var p = review[0];
            return MajordomoMutationResult.Refused(new MajordomoRefusal(
                "invalid_chain",
                p.Message,
                Item: p.ItemIndex is { } ix ? $"items[{ix}]" : null,
                Field: p.Field));
        }

        // Live checks against the store: every pre-existing dependency target
        // must exist and be non-terminal (in-chain edges point at pending
        // nodes — covered by the review and the prepare pass below).
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (await CheckDependencyTargetsAsync(
                    node.Item.DependsOn, $"items[{i}]", "depends_on", ct).ConfigureAwait(false)
                    is { } depRefusal)
                return MajordomoMutationResult.Refused(depRefusal);
        }

        // Prepare every node before anything commits. Prepared predecessors
        // pass their freshly minted ids to later nodes as concrete dependency
        // ids and join the dependency/cycle/external-id graph via
        // pendingSiblings, so the last node is validated against the whole
        // chain exactly as the store will see it.
        var prepared = new List<PreparedWorkItemCreation>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var edgeIds = node.DependsOnIndexes.Select(ix => prepared[ix].Item.Id.ToString());
            CreateWorkItemRequest req;
            try { req = ToRequest(node.Item, initiator, edgeIds); }
            catch (MajordomoRefusalException ex)
            {
                return MajordomoMutationResult.Refused(
                    new MajordomoRefusal("invalid_arguments", ex.Message, Item: $"items[{i}]", Field: ex.Field));
            }

            var result = await _creation.PrepareAsync(
                req, provenance: null, pendingSiblings: prepared.Select(p => p.Item).ToList(), ct)
                .ConfigureAwait(false);
            if (result.Error is not null)
            {
                var detail = await ResultText.ReadErrorTextAsync(result.Error).ConfigureAwait(false);
                return MajordomoMutationResult.Refused(
                    new MajordomoRefusal("validation_failed", $"items[{i}]: {detail}", Item: $"items[{i}]"));
            }

            prepared.Add(result.Prepared!);
        }

        if (!commit)
            return MajordomoMutationResult.Planned(
                new MajordomoChangeSet(true, [new MajordomoPlannedChange.CreateChain(nodes)], []));

        var committed = await _creation.CommitAllAsync(prepared, ct).ConfigureAwait(false);
        if (committed.Error is not null)
            return await RefusalFromResultAsync(committed.Error).ConfigureAwait(false);

        return MajordomoMutationResult.Committed(
            new MajordomoChangeSet(
                false,
                [new MajordomoPlannedChange.CreateChain(nodes)],
                committed.Items.Select(c => c.Item.Id).ToList()));
    }

    // ── update_work_item ────────────────────────────────────────────────────

    public async Task<MajordomoMutationResult> UpdateAsync(
        UpdateWorkItemArgs args, bool commit, CancellationToken ct)
    {
        var item = await _store.GetAsync(args.Id, ct).ConfigureAwait(false);
        if (item is null)
            return MajordomoMutationResult.Refused(new MajordomoRefusal(
                "not_found",
                $"work item '{args.Id}' does not exist",
                Item: args.Id.ToString(),
                Field: "id"));

        var patch = args.Patch;
        if (patch.DependsOn is { } deps
            && await CheckDependencyTargetsAsync(deps, args.Id.ToString(), "patch.depends_on", ct).ConfigureAwait(false)
                is { } depRefusal)
            return MajordomoMutationResult.Refused(depRefusal);

        // Plan every requested sub-edit before any write. The three command
        // paths are the exact PATCH /workitems/{id}, /priority, and
        // /external-ids semantics the REST surface exposes.
        PatchWorkItemRequest fieldPatch;
        try
        {
            fieldPatch = new PatchWorkItemRequest(
            Title: patch.Title,
            Prompt: patch.Prompt,
            Agent: patch.Agent?.Value,
            WorkTimeoutMinutes: patch.WorkTimeout is { } wt ? ToWholeMinutes(wt, "patch.work_timeout") : null,
            MergeTimeoutMinutes: patch.MergeTimeout is { } mt ? ToWholeMinutes(mt, "patch.merge_timeout") : null,
            MinModelScore: patch.MinModelScore,
            RequiredCapabilities: patch.RequiredCapabilities,
            AuditMaxIterations: patch.AuditMaxIterations,
            AuditComplexity: patch.AuditComplexity,
            DependsOn: patch.DependsOn?.Select(d => d.ToString()).ToArray(),
            Knobs: patch.Knobs,
            AgentClassId: patch.AgentClassId);
        }
        catch (MajordomoRefusalException ex)
        {
            return MajordomoMutationResult.Refused(
                new MajordomoRefusal("invalid_arguments", ex.Message, Item: args.Id.ToString(), Field: ex.Field));
        }

        WorkItemCommandService.CommandPlan<WorkItemCommandService.WorkItemPatchPlan>? patchPlan = null;
        var hasFieldPatch =
            patch.Title is not null || patch.Prompt is not null || patch.Agent is not null
            || patch.WorkTimeout is not null || patch.MergeTimeout is not null
            || patch.MinModelScore is not null || patch.AuditMaxIterations is not null
            || patch.AuditComplexity is not null || patch.RequiredCapabilities is not null
            || patch.DependsOn is not null || patch.Knobs is not null || patch.AgentClassId is not null;
        if (hasFieldPatch)
        {
            patchPlan = await _commands.PlanPatchAsync(item, fieldPatch, ct).ConfigureAwait(false);
            if (patchPlan.Error is { } patchError)
                return MajordomoMutationResult.Refused(
                    new MajordomoRefusal("validation_failed", patchError.Error ?? "patch rejected", Item: args.Id.ToString()));
        }

        WorkItemCommandService.CommandPlan<WorkItemCommandService.WorkItemPriorityPlan>? priorityPlan = null;
        if (patch.Priority is { } priority)
        {
            priorityPlan = await _commands.PlanPriorityAsync(item, priority, ct).ConfigureAwait(false);
            if (priorityPlan.Error is { } priorityError)
                return MajordomoMutationResult.Refused(new MajordomoRefusal(
                    "validation_failed",
                    priorityError.Error ?? "priority rejected",
                    Item: args.Id.ToString(),
                    Field: "patch.priority"));
        }

        WorkItemCommandService.CommandPlan<WorkItemCommandService.WorkItemExternalIdsPlan>? extIdsPlan = null;
        if (patch.ExternalIds is { } extIds)
        {
            extIdsPlan = await _commands.PlanExternalIdsAsync(
                item,
                new PatchExternalIdsRequest(
                    extIds.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                    ReplaceExternalIds: true),
                ct).ConfigureAwait(false);
            if (extIdsPlan.Error is { } extIdsError)
                return MajordomoMutationResult.Refused(new MajordomoRefusal(
                    "validation_failed",
                    extIdsError.Error ?? "externalIds rejected",
                    Item: args.Id.ToString(),
                    Field: "patch.external_ids"));
        }

        if (!commit)
            return MajordomoMutationResult.Planned(
                new MajordomoChangeSet(true, [new MajordomoPlannedChange.UpdateItem(args.Id, patch)], []));

        // Commit in field-group order: field patch, then priority, then
        // external ids. Each write is individually guarded; a mid-sequence
        // failure reports exactly what was applied.
        var writesApplied = false;
        if (patchPlan is not null)
        {
            var outcome = await _commands.CommitPatchAsync(patchPlan.Plan!, ct).ConfigureAwait(false);
            writesApplied = true;
            if (!outcome.Succeeded)
                return MajordomoMutationResult.Refused(
                    new MajordomoRefusal("conflict", outcome.Error ?? "patch failed", Item: args.Id.ToString()),
                    writesApplied: outcome.StatusCode != 404);
        }

        if (priorityPlan is not null)
        {
            var outcome = await _commands.CommitPriorityAsync(priorityPlan.Plan!, ct).ConfigureAwait(false);
            if (!outcome.Succeeded)
                return MajordomoMutationResult.Refused(
                    new MajordomoRefusal(
                        "conflict",
                        (outcome.Error ?? "priority update failed") + (writesApplied ? " (earlier field edits were applied)" : string.Empty),
                        Item: args.Id.ToString(),
                        Field: "patch.priority"),
                    writesApplied: true);
            writesApplied = true;
        }

        if (extIdsPlan is not null)
        {
            var outcome = await _commands.CommitExternalIdsAsync(extIdsPlan.Plan!, ct).ConfigureAwait(false);
            if (!outcome.Succeeded)
                return MajordomoMutationResult.Refused(
                    new MajordomoRefusal(
                        "conflict",
                        (outcome.Error ?? "external ids update failed") + (writesApplied ? " (earlier edits were applied)" : string.Empty),
                        Item: args.Id.ToString(),
                        Field: "patch.external_ids"),
                    writesApplied: true);
        }

        return MajordomoMutationResult.Committed(
            new MajordomoChangeSet(
                false,
                [new MajordomoPlannedChange.UpdateItem(args.Id, patch)],
                [args.Id]));
    }

    // ── cancel_work_item ────────────────────────────────────────────────────

    public async Task<MajordomoMutationResult> CancelAsync(
        CancelWorkItemArgs args, bool commit, CancellationToken ct)
    {
        var item = await _store.GetAsync(args.Id, ct).ConfigureAwait(false);
        if (item is null)
            return MajordomoMutationResult.Refused(new MajordomoRefusal(
                "not_found",
                $"work item '{args.Id}' does not exist",
                Item: args.Id.ToString(),
                Field: "id"));

        var (plan, planError) = _commands.PlanCancel(item, args.Reason, resolutionSha: null);
        if (planError is not null)
            return MajordomoMutationResult.Refused(new MajordomoRefusal(
                planError.StatusCode == StatusCodes.Status404NotFound ? "not_found" : "conflict",
                planError.Error ?? "cancel refused",
                Item: args.Id.ToString()));

        if (!commit)
            return MajordomoMutationResult.Planned(
                new MajordomoChangeSet(true, [new MajordomoPlannedChange.CancelItem(args.Id, args.Reason)], []));

        var outcome = await _commands.CommitCancelAsync(plan!, ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
            return MajordomoMutationResult.Refused(
                new MajordomoRefusal("conflict", outcome.Error ?? "cancel failed", Item: args.Id.ToString()),
                writesApplied: true);

        var affected = new List<WorkItemId> { args.Id };
        if (outcome.AlsoAffected is { } cascaded)
            affected.AddRange(cascaded);

        return MajordomoMutationResult.Committed(
            new MajordomoChangeSet(
                false,
                [new MajordomoPlannedChange.CancelItem(args.Id, args.Reason)],
                affected));
    }

    // ── retry_work_item ─────────────────────────────────────────────────────

    public async Task<MajordomoMutationResult> RetryAsync(
        RetryWorkItemArgs args, bool commit, CancellationToken ct)
    {
        var item = await _store.GetAsync(args.Id, ct).ConfigureAwait(false);
        if (item is null)
            return MajordomoMutationResult.Refused(new MajordomoRefusal(
                "not_found",
                $"work item '{args.Id}' does not exist",
                Item: args.Id.ToString(),
                Field: "id"));

        var (plan, planError) = await _commands.PlanRetryAsync(item, args.From.ToPolicyValue(), ct).ConfigureAwait(false);
        if (planError is not null)
            return MajordomoMutationResult.Refused(new MajordomoRefusal(
                "conflict",
                planError.Error ?? "retry refused",
                Item: args.Id.ToString(),
                Field: "id"));

        if (!commit)
            return MajordomoMutationResult.Planned(
                new MajordomoChangeSet(
                    true,
                    [new MajordomoPlannedChange.RetryItem(args.Id, args.From, args.WorkTimeout)],
                    []));

        var outcome = await _commands.CommitRetryAsync(
            plan!,
            args.WorkTimeout is { } wt ? ToWholeMinutes(wt, "work_timeout") : null,
            ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
            return MajordomoMutationResult.Refused(
                new MajordomoRefusal("conflict", outcome.Error ?? "retry failed", Item: args.Id.ToString()),
                writesApplied: plan is { NeedsStaleFence: true });

        return MajordomoMutationResult.Committed(
            new MajordomoChangeSet(
                false,
                [new MajordomoPlannedChange.RetryItem(args.Id, args.From, args.WorkTimeout)],
                [args.Id]));
    }
}
