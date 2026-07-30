using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal sealed class WorkItemCreationService
{
    private const int GlobalMinPriority = -1000;
    private const int GlobalMaxPriority = 1000;
    private const int MaxRequiredCapabilities = 16;
    private const int MaxCapabilityLength = 64;

    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IProjectRepository _projects;
    private readonly IAgentRegistry _agents;
    private readonly IKnobRegistry _knobs;
    private readonly Func<IReleaseStore?> _releaseStoreFactory;
    private readonly IWebhookDispatcher _webhooks;

    public WorkItemCreationService(
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        IKnobRegistry knobs,
        IWebhookDispatcher webhooks,
        Func<IReleaseStore?>? releaseStoreFactory = null)
    {
        _store = store;
        _queue = queue;
        _projects = projects;
        _agents = agents;
        _knobs = knobs;
        _webhooks = webhooks;
        _releaseStoreFactory = releaseStoreFactory ?? (() => null);
    }

    public Task<PreparedWorkItemCreationResult> PrepareAsync(
        CreateWorkItemRequest req,
        CancellationToken ct = default) =>
        PrepareAsync(req, provenance: null, ct);

    public async Task<PreparedWorkItemCreationResult> PrepareAsync(
        CreateWorkItemRequest req,
        WorkItemCreationProvenance? provenance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return Error("title is required");
        if (string.IsNullOrWhiteSpace(req.Prompt)) return Error("prompt is required");
        if (string.IsNullOrWhiteSpace(req.ProjectId)) return Error("projectId is required");

        ProjectId pid;
        try { pid = new ProjectId(req.ProjectId); }
        catch (ArgumentException ex) { return Error(ex.Message); }

        var project = await _projects.GetAsync(pid, ct);
        if (project is null)
        {
            var known = (await _projects.ListAsync(ct)).Select(p => p.Id.Value).ToList();
            return new PreparedWorkItemCreationResult(
                null,
                Results.BadRequest(new { error = $"unknown project '{req.ProjectId}'", available = known }));
        }

        try
        {
            if (req.BaseBranch is not null) Validation.ValidateBranchName(req.BaseBranch, nameof(req.BaseBranch));
            if (req.WorkBranch is not null) Validation.ValidateBranchName(req.WorkBranch, nameof(req.WorkBranch));
            Validation.ValidateNoOptionLikeOrControl(req.Title, nameof(req.Title));

            if (req.WorkBranch is not null && req.BaseBranch is not null
                && string.Equals(req.WorkBranch, req.BaseBranch, StringComparison.Ordinal))
            {
                return Error("workBranch must differ from baseBranch");
            }

            if (req.Title.Length > 200)
                return Error("title must be <= 200 chars");
            if (req.Prompt.Length > 64 * 1024)
                return Error("prompt must be <= 64KB");
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }

        var canonicalExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (req.ExternalIds is { Count: > 0 })
        {
            if (req.ExternalIds.Count > 16)
                return Error("externalIds may contain at most 16 entries per work item");
            foreach (var (ns, value) in req.ExternalIds)
            {
                if (value is null)
                    return Error($"externalIds['{ns}'] must not be null on create - use PATCH /external-ids to delete");
                try
                {
                    Validation.ValidateExternalIdNamespace(ns, $"externalIds key '{ns}'");
                    Validation.ValidateExternalId(value, $"externalIds['{ns}']");
                }
                catch (ArgumentException ex) { return Error(ex.Message); }
                canonicalExternalIds[ns] = value;
            }
        }
        if (!string.IsNullOrEmpty(req.ExternalId))
        {
            try { Validation.ValidateExternalId(req.ExternalId, nameof(req.ExternalId)); }
            catch (ArgumentException ex) { return Error(ex.Message); }
            if (canonicalExternalIds.TryGetValue("legacy", out var dictLegacy) && dictLegacy != req.ExternalId)
                return Error("externalId and externalIds['legacy'] disagree; send one or matching values");
            canonicalExternalIds["legacy"] = req.ExternalId;
        }

        foreach (var (ns, value) in canonicalExternalIds)
        {
            var conflict = await _store.GetByNamespacedExternalIdAsync(pid, ns, value, ct);
            if (conflict is not null)
                return Error($"externalId '{value}' in namespace '{ns}' already exists in project '{pid}' for work item {conflict.Id} (state: {conflict.State})");
        }

        AgentKind? agentOverride = null;
        if (!string.IsNullOrWhiteSpace(req.Agent))
        {
            var kind = new AgentKind(req.Agent);
            if (!_agents.TryGet(kind, out _))
                return new PreparedWorkItemCreationResult(
                    null,
                    Results.BadRequest(new { error = $"unknown agent '{req.Agent}'", available = _agents.Available.Select(a => a.Value) }));
            agentOverride = kind;
        }

        string? auditorProfile = null;
        if (!string.IsNullOrWhiteSpace(req.AuditorProfile))
        {
            auditorProfile = req.AuditorProfile.Trim();
            if (!AuditProfileExists(project.Audit, auditorProfile))
                return new PreparedWorkItemCreationResult(
                    null,
                    Results.BadRequest(new
                    {
                        error = $"unknown auditorProfile '{auditorProfile}' for project '{pid}'",
                        availableProfiles = AvailableAuditProfiles(project.Audit),
                    }));
        }

        if ((req.DependsOn?.Length ?? 0) > 100)
            return Error("dependsOn must contain at most 100 entries");

        var allItems = new List<WorkItem>();
        var byNamespacedExternalId = new Dictionary<(string Namespace, string Value), WorkItem>();
        var byBareExternalId = new Dictionary<string, List<(string Namespace, WorkItem Item)>>(StringComparer.Ordinal);
        if (req.DependsOn?.Length > 0)
        {
            await foreach (var existing in _store.ListAsync(ct)) allItems.Add(existing);
            foreach (var existing in allItems.Where(i => i.ProjectId == pid))
            {
                foreach (var (ns, value) in existing.ExternalIds)
                {
                    byNamespacedExternalId[(ns, value)] = existing;
                    if (!byBareExternalId.TryGetValue(value, out var list))
                        byBareExternalId[value] = list = new List<(string, WorkItem)>();
                    list.Add((ns, existing));
                }
            }
        }

        var newId = WorkItemId.New();
        var dependsOnIds = new List<WorkItemId>();
        foreach (var rawId in req.DependsOn ?? [])
        {
            if (rawId is null)
                return Error("dependency could not be resolved: null entry in dependsOn array");
            if (Guid.TryParse(rawId, out var g))
            {
                dependsOnIds.Add(new WorkItemId(g));
                continue;
            }
            if (Validation.TryParseNamespacedExternalId(rawId, out var depNs, out var depValue) && depNs is not null)
            {
                if (!byNamespacedExternalId.TryGetValue((depNs, depValue), out var depByNs))
                    return Error($"dependency '{rawId}' could not be resolved: no work item with externalId '{depValue}' in namespace '{depNs}' in project '{pid}'");
                dependsOnIds.Add(depByNs.Id);
                continue;
            }
            if (!byBareExternalId.TryGetValue(rawId, out var matches) || matches.Count == 0)
                return Error($"dependency '{rawId}' could not be resolved: no work item with externalId '{rawId}' in project '{pid}'");
            var distinctItems = matches.Select(m => m.Item.Id).Distinct().ToList();
            if (distinctItems.Count > 1)
                return Error($"dependency '{rawId}' is ambiguous: matches multiple work items via namespaces {string.Join(", ", matches.Select(m => m.Namespace).Distinct())} - qualify as 'namespace:value'");
            dependsOnIds.Add(distinctItems[0]);
        }

        if (dependsOnIds.Contains(newId))
            return Error("a work item cannot depend on itself");

        var missingDep = WorkItemDependencies.FindMissingDependency(dependsOnIds, allItems);
        if (missingDep is not null)
            return Error($"dependency {missingDep} not found");

        var cyclePath = WorkItemDependencies.FindCycle(newId, dependsOnIds, allItems);
        if (cyclePath is not null)
            return Error($"circular dependency detected: {cyclePath}");

        Release? boundRelease = null;
        ReleaseId? releaseId = null;
        if (!string.IsNullOrWhiteSpace(req.ReleaseId))
        {
            var releaseStore = _releaseStoreFactory();
            if (releaseStore is null)
                return Error("release management is not available");

            if (!ReleaseId.TryParse(req.ReleaseId, out var rid))
                return Error("invalid releaseId");

            var rel = await releaseStore.GetAsync(rid, ct);
            if (rel is null)
                return new PreparedWorkItemCreationResult(
                    null,
                    Results.NotFound(new { error = $"release '{req.ReleaseId}' not found" }));
            if (rel.ProjectId != pid)
                return Error("release belongs to a different project");
            if (rel.State != ReleaseState.Open)
                return Error($"release is {rel.State}; only Open releases accept new work items");

            if (!project.ReleaseConfig.Enabled)
                return Error($"release management is not enabled for project '{req.ProjectId}'");

            boundRelease = rel;
            releaseId = rid;
        }

        string? agentClassId = null;
        if (!string.IsNullOrWhiteSpace(req.AgentClassId))
        {
            if (req.AgentClassId.Length > 200)
                return Error("agentClassId must be <= 200 chars");
            agentClassId = req.AgentClassId.Trim();
        }

        var priority = 0;
        if (req.Priority is { } p)
        {
            var priorityError = ValidatePriority(p, project);
            if (priorityError is not null)
                return new PreparedWorkItemCreationResult(null, priorityError);
            priority = p;
        }

        int? auditMaxIterations = null;
        if (req.AuditMaxIterations is { } auditBudget)
        {
            var auditBudgetError = AuditBudgetRequestValidation.ValidateAuditMaxIterations(auditBudget);
            if (auditBudgetError is not null)
                return Error(auditBudgetError);
            auditMaxIterations = auditBudget;
        }

        string? auditComplexity = null;
        if (req.AuditComplexity is not null)
        {
            var (normalised, complexityError) = AuditBudgetRequestValidation.NormaliseAuditComplexity(req.AuditComplexity);
            if (complexityError is not null)
                return Error(complexityError);
            auditComplexity = normalised;
        }

        IReadOnlyList<string> requiredCapabilities = [];
        if (req.RequiredCapabilities is { } reqCaps)
        {
            var (normalised, capErr) = NormaliseRequiredCapabilities(reqCaps);
            if (capErr is not null)
                return new PreparedWorkItemCreationResult(null, capErr);
            requiredCapabilities = normalised!;
        }

        IReadOnlyDictionary<string, string> knobs = EmptyKnobs;
        if (req.Knobs is { Count: > 0 })
        {
            var (normalisedKnobs, knobErr) = NormaliseKnobs(req.Knobs, _knobs);
            if (knobErr is not null)
                return new PreparedWorkItemCreationResult(null, knobErr);
            knobs = normalisedKnobs!;
        }

        var jobType = JobType.Normal;
        CheckAndActSpec? checkSpec = null;
        AgentControlSpec? agentControlSpec = null;
        var isRefactor = req.IsRefactor == true;
        if (req.Check is not null)
        {
            if (req.AgentControl is not null)
                return Error("check and agentControl cannot both be provided");
            if (isRefactor)
                return Error("check and isRefactor cannot both be provided");

            var check = req.Check;
            if (string.IsNullOrWhiteSpace(check.Question))
                return Error("check.question is required");
            if (check.Question.Length > 64 * 1024)
                return Error("check.question must be <= 64KB");
            if (!CheckAndActModes.TryNormalise(check.Mode, out var checkMode))
                return Error("check.mode must be 'agentic' or 'completion'");
            if (check.OnYes is null)
                return Error("check.onYes is required when check is provided");
            var onYes = check.OnYes;
            if (string.IsNullOrWhiteSpace(onYes.Title))
                return Error("check.onYes.title is required");
            try { Validation.ValidateNoOptionLikeOrControl(onYes.Title, "check.onYes.title"); }
            catch (ArgumentException ex) { return Error(ex.Message); }
            if (onYes.Title.Length > 200)
                return Error("check.onYes.title must be <= 200 chars");
            if (string.IsNullOrWhiteSpace(onYes.Prompt))
                return Error("check.onYes.prompt is required");
            if (onYes.Prompt.Length > 64 * 1024)
                return Error("check.onYes.prompt must be <= 64KB");
            if (!string.IsNullOrWhiteSpace(onYes.Agent))
            {
                var kind = new AgentKind(onYes.Agent);
                if (!_agents.TryGet(kind, out _))
                {
                    var onYesLocation = provenance is null
                        ? "check.onYes"
                        : $"template '{provenance.TemplateName}' checks[{provenance.TemplateEntryIndex}].onYes";
                    return new PreparedWorkItemCreationResult(
                        null,
                        Results.BadRequest(new
                        {
                            error = $"unknown agent '{onYes.Agent}' on {onYesLocation}",
                            available = _agents.Available.Select(a => a.Value),
                        }));
                }
            }
            if (onYes.AgentClassId is { Length: > 200 })
                return Error("check.onYes.agentClassId must be <= 200 chars");
            if (onYes.DependsOn is { Length: > 100 })
                return Error("check.onYes.dependsOn must contain at most 100 entries");
            IReadOnlyDictionary<string, string> onYesKnobs = EmptyKnobs;
            if (onYes.Knobs is { Count: > 0 })
            {
                var (normalisedOnYesKnobs, onYesKnobErr) = NormaliseKnobs(onYes.Knobs, _knobs);
                if (onYesKnobErr is not null)
                    return new PreparedWorkItemCreationResult(null, onYesKnobErr);
                onYesKnobs = normalisedOnYesKnobs!;
            }

            checkSpec = new CheckAndActSpec
            {
                Question = check.Question,
                Mode = checkMode,
                ActionableAnswer = check.ActionableAnswer ?? true,
                OnYes = new OnYesActionSpec
                {
                    Title = onYes.Title,
                    Prompt = onYes.Prompt,
                    MinModelScore = onYes.MinModelScore,
                    Priority = onYes.Priority,
                    Agent = string.IsNullOrWhiteSpace(onYes.Agent) ? null : onYes.Agent.Trim(),
                    AgentClassId = string.IsNullOrWhiteSpace(onYes.AgentClassId) ? null : onYes.AgentClassId.Trim(),
                    DependsOn = onYes.DependsOn is null
                        ? null
                        : onYes.DependsOn.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList(),
                    Knobs = onYesKnobs,
                },
            };
            jobType = JobType.CheckAndAct;
        }
        else if (req.AgentControl is not null)
        {
            if (isRefactor)
                return Error("agentControl and isRefactor cannot both be provided");
            var control = req.AgentControl;
            if (string.IsNullOrWhiteSpace(control.Agent))
                return Error("agentControl.agent is required");
            var controlAgent = new AgentKind(control.Agent.Trim().ToLowerInvariant());
            if (!_agents.TryGet(controlAgent, out _))
                return new PreparedWorkItemCreationResult(
                    null,
                    Results.BadRequest(new { error = $"unknown agent '{control.Agent}'", available = _agents.Available.Select(a => a.Value) }));

            var actionText = control.Action?.Trim();
            AgentControlAction action;
            if (string.Equals(actionText, "pause", StringComparison.OrdinalIgnoreCase))
                action = AgentControlAction.Pause;
            else if (string.Equals(actionText, "resume", StringComparison.OrdinalIgnoreCase))
                action = AgentControlAction.Resume;
            else
                return Error("agentControl.action must be 'pause' or 'resume'");

            var reason = string.IsNullOrWhiteSpace(control.Reason) ? null : control.Reason.Trim();
            if (action == AgentControlAction.Pause && reason is null)
                return Error("agentControl.reason is required for pause");

            var reasonValidation = AgentPauseValidation.ValidateOptionalReason(reason, "agentControl.reason");
            if (reasonValidation is not null)
                return Error(reasonValidation);

            if (control.DurationSeconds is { } seconds && seconds <= 0)
                return Error("agentControl.durationSeconds must be positive");
            if (control.DurationSeconds is not null && control.ExpiresAt is not null)
                return Error("agentControl: provide either durationSeconds or expiresAt, not both");
            if (control.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
                return Error("agentControl.expiresAt must be in the future");

            agentControlSpec = new AgentControlSpec
            {
                Action = action,
                Agent = controlAgent.Value,
                Reason = reason,
                DurationSeconds = control.DurationSeconds,
                ExpiresAt = control.ExpiresAt,
            };
            jobType = JobType.AgentControl;
        }
        else if (isRefactor)
        {
            jobType = JobType.Refactor;
        }

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
            AuditMaxIterations = auditMaxIterations,
            AuditComplexity = auditComplexity,
            ExternalIds = canonicalExternalIds,
            Initiator = req.Initiator,
            ReleaseId = releaseId,
            RequiredCapabilities = requiredCapabilities,
            Knobs = knobs,
            JobType = jobType,
            Check = checkSpec,
            AgentControl = agentControlSpec,
            TemplateName = provenance?.TemplateName,
            TemplateEntryIndex = provenance?.TemplateEntryIndex,
        };
        if (req.WorkTimeoutMinutes is { } w)
            item = item with { WorkTimeout = TimeSpan.FromMinutes(Math.Clamp(w, 1, 480)) };
        if (req.MergeTimeoutMinutes is { } m)
            item = item with { MergeTimeout = TimeSpan.FromMinutes(Math.Clamp(m, 1, 240)) };
        if (req.MinModelScore is { } minScore)
            item = item with { MinModelScore = Math.Clamp(minScore, 0, 200) };

        return new PreparedWorkItemCreationResult(
            new PreparedWorkItemCreation(item, project, boundRelease, canonicalExternalIds),
            null);

        static PreparedWorkItemCreationResult Error(string message) =>
            new(null, Results.BadRequest(new { error = message }));
    }

    public async Task<CommittedWorkItemCreationResult> CommitAsync(
        PreparedWorkItemCreation prepared,
        CancellationToken ct = default)
    {
        var item = prepared.Item;
        try { await _store.CreateAsync(item, ct); }
        catch (WorkItemExternalIdConflictException)
        {
            foreach (var (ns, value) in prepared.CanonicalExternalIds)
            {
                var conflict = await _store.GetByNamespacedExternalIdAsync(item.ProjectId, ns, value, ct);
                if (conflict is not null)
                    return Error($"externalId '{value}' in namespace '{ns}' already exists in project '{item.ProjectId}' for work item {conflict.Id} (state: {conflict.State})");
            }
            return Error("an external id already exists in this project (concurrent duplicate)");
        }
        AuditLog.WorkItemCreated(item.Id, item.ProjectId, item.Title, item.Initiator);

        var freshDepStates = new Dictionary<WorkItemId, WorkItemState>();
        var freshDepExternalIds = new Dictionary<WorkItemId, string?>();
        foreach (var depId in item.DependsOn)
        {
            var dep = await _store.GetAsync(depId, ct);
            if (dep is not null)
            {
                freshDepStates[depId] = dep.State;
                freshDepExternalIds[depId] = dep.ExternalId;
            }
        }
        if (WorkItemDependencies.AreSatisfied(item.DependsOn, freshDepStates))
            await _queue.EnqueueAsync(item.Id, ct);

        if (prepared.BoundRelease is not null)
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "release.work_item_added",
                WorkItem = item,
                Project = prepared.Project,
                Release = prepared.BoundRelease,
            }, ct);

        return new CommittedWorkItemCreationResult(
            item,
            prepared.Project,
            freshDepStates,
            freshDepExternalIds,
            null);

        CommittedWorkItemCreationResult Error(string message) =>
            new(
                item,
                prepared.Project,
                new Dictionary<WorkItemId, WorkItemState>(),
                new Dictionary<WorkItemId, string?>(),
                Results.BadRequest(new { error = message }));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyKnobs
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static (IReadOnlyDictionary<string, string>? Knobs, IResult? Error) NormaliseKnobs(
        IReadOnlyDictionary<string, string> raw,
        IKnobRegistry registry)
    {
        var normalised = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in raw)
        {
            var verdict = registry.Normalize(rawKey, rawValue);
            if (!verdict.Ok)
                return (null, Results.BadRequest(new { error = verdict.Error }));
            normalised[verdict.Key!] = verdict.Value!;
        }

        return (normalised, null);
    }

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
                error = $"priority {priority} exceeds project '{project.Id}' maxPriority {maxPriority}",
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
}

internal sealed record WorkItemCreationProvenance(string TemplateName, int TemplateEntryIndex);

internal sealed record PreparedWorkItemCreation(
    WorkItem Item,
    Project Project,
    Release? BoundRelease,
    IReadOnlyDictionary<string, string> CanonicalExternalIds);

internal sealed record PreparedWorkItemCreationResult(
    PreparedWorkItemCreation? Prepared,
    IResult? Error);

internal sealed record CommittedWorkItemCreationResult(
    WorkItem Item,
    Project Project,
    IReadOnlyDictionary<WorkItemId, WorkItemState> DependencyStates,
    IReadOnlyDictionary<WorkItemId, string?> DependencyExternalIds,
    IResult? Error);
