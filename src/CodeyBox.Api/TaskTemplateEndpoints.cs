using CodeyBox.Core;

namespace CodeyBox.Api;

internal static class TaskTemplateEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/templates");
        group.MapGet("/", ListAsync);
        group.MapPost("/queue", QueueAsync);
        group.MapPost("/{name}/queue", QueueByNameAsync);
    }

    private static async Task<IResult> ListAsync(
        ITaskTemplateRegistry registry,
        CancellationToken ct)
    {
        var templates = await registry.ListAsync(ct);
        return Results.Ok(templates);
    }

    private static async Task<IResult> QueueByNameAsync(
        string name,
        QueueTaskTemplateRequest req,
        ITaskTemplateRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.Template) && !TemplateRefsMatch(req.Template, name))
            return Results.BadRequest(new { error = "body template must match route template name" });

        return await QueueCoreAsync(
            req with { Template = name },
            registry,
            store,
            queue,
            projects,
            agents,
            ct);
    }

    private static bool TemplateRefsMatch(string left, string right) =>
        string.Equals(NormaliseTemplateRef(left), NormaliseTemplateRef(right), StringComparison.OrdinalIgnoreCase);

    private static string NormaliseTemplateRef(string templateRef)
    {
        var normalised = templateRef.Trim().Replace('\\', '/');
        if (normalised.StartsWith("templates/", StringComparison.OrdinalIgnoreCase))
            normalised = normalised["templates/".Length..];
        if (normalised.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            normalised = normalised[..^".json".Length];
        return normalised.Trim('/');
    }

    private static async Task<IResult> QueueAsync(
        QueueTaskTemplateRequest req,
        ITaskTemplateRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        return await QueueCoreAsync(req, registry, store, queue, projects, agents, ct);
    }

    private static async Task<IResult> QueueCoreAsync(
        QueueTaskTemplateRequest req,
        ITaskTemplateRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        IProjectRepository projects,
        IAgentRegistry agents,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Template))
            return Results.BadRequest(new { error = "template is required" });
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            return Results.BadRequest(new { error = "projectId is required" });

        TaskTemplateDefinition template;
        try
        {
            template = await registry.LoadAsync(req.Template, ct);
        }
        catch (TaskTemplateNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (TaskTemplateLoadException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        ProjectId pid;
        try { pid = new ProjectId(req.ProjectId); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

        var project = await projects.GetAsync(pid, ct);
        if (project is null)
        {
            var known = (await projects.ListAsync(ct)).Select(p => p.Id.Value).ToList();
            return Results.BadRequest(new { error = $"unknown project '{req.ProjectId}'", available = known });
        }

        AgentKind? agentOverride = null;
        if (!string.IsNullOrWhiteSpace(req.Agent))
        {
            var kind = new AgentKind(req.Agent);
            if (!agents.TryGet(kind, out _))
                return Results.BadRequest(new { error = $"unknown agent '{req.Agent}'", available = agents.Available.Select(a => a.Value) });
            agentOverride = kind;
        }

        string? agentClassId = null;
        if (!string.IsNullOrWhiteSpace(req.AgentClassId))
        {
            if (req.AgentClassId.Length > 200)
                return Results.BadRequest(new { error = "agentClassId must be <= 200 chars" });
            agentClassId = req.AgentClassId.Trim();
        }

        var priority = 0;
        if (req.Priority is { } p)
        {
            var priorityError = ValidatePriority(p, project);
            if (priorityError is not null) return priorityError;
            priority = p;
        }

        var minModelScore = req.MinModelScore is { } s ? Math.Clamp(s, 0, 200) : 0;

        IReadOnlyList<string> requiredCapabilities = [];
        if (req.RequiredCapabilities is { Count: > 0 } reqCaps)
        {
            var (normalised, capErr) = NormaliseRequiredCapabilities(reqCaps);
            if (capErr is not null) return capErr;
            requiredCapabilities = normalised!;
        }

        for (var i = 0; i < template.Checks.Count; i++)
        {
            var entry = template.Checks[i];
            if (!string.IsNullOrWhiteSpace(entry.OnYes.Agent))
            {
                var kind = new AgentKind(entry.OnYes.Agent);
                if (!agents.TryGet(kind, out _))
                    return Results.BadRequest(new
                    {
                        error = $"unknown agent '{entry.OnYes.Agent}' on template '{template.Name}' checks[{i}].onYes",
                        available = agents.Available.Select(a => a.Value),
                    });
            }
        }

        var items = new List<WorkItem>(template.Checks.Count);
        for (var i = 0; i < template.Checks.Count; i++)
        {
            var entry = template.Checks[i];
            var item = BuildWorkItem(
                template.Name,
                i,
                entry,
                pid,
                agentOverride,
                agentClassId,
                priority,
                minModelScore,
                requiredCapabilities);
            items.Add(item);
        }

        var dtos = new List<WorkItemDto>(items.Count);
        var emptyDepStates = new Dictionary<WorkItemId, WorkItemState>();
        var emptyDepExternalIds = new Dictionary<WorkItemId, string?>();
        foreach (var item in items)
        {
            await store.CreateAsync(item, ct);
            AuditLog.WorkItemCreated(item.Id, item.ProjectId, item.Title);
            await queue.EnqueueAsync(item.Id, ct);
            dtos.Add(WorkItemEndpoints.ToDto(item, project, emptyDepStates, emptyDepExternalIds));
        }

        return Results.Created("/workitems", new QueueTaskTemplateResponse(
            template.Name,
            template.Checks.Count,
            dtos));
    }

    private static WorkItem BuildWorkItem(
        string templateName,
        int entryIndex,
        TaskTemplateCheck entry,
        ProjectId projectId,
        AgentKind? agentOverride,
        string? agentClassId,
        int priority,
        int minModelScore,
        IReadOnlyList<string> requiredCapabilities)
    {
        var onYes = entry.OnYes;
        return new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = projectId,
            Title = entry.Title ?? BuildGeneratedTitle(entryIndex, entry.Question),
            Prompt = entry.Prompt ?? entry.Question,
            Agent = agentOverride,
            AgentClassId = agentClassId,
            QueuePosition = DateTimeOffset.UtcNow.Ticks,
            Priority = priority,
            MinModelScore = minModelScore,
            RequiredCapabilities = requiredCapabilities,
            JobType = JobType.CheckAndAct,
            TemplateName = templateName,
            TemplateEntryIndex = entryIndex,
            Check = new CheckAndActSpec
            {
                Question = entry.Question,
                ActionableAnswer = entry.ActionableAnswer ?? true,
                OnYes = new OnYesActionSpec
                {
                    Title = onYes.Title,
                    Prompt = onYes.Prompt,
                    MinModelScore = onYes.MinModelScore,
                    Priority = onYes.Priority,
                    Agent = onYes.Agent,
                    AgentClassId = onYes.AgentClassId,
                    DependsOn = onYes.DependsOn,
                },
            },
        };
    }

    private static string BuildGeneratedTitle(int entryIndex, string question)
    {
        var compact = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const string prefix = "Check template entry ";
        var fullPrefix = $"{prefix}{entryIndex + 1}: ";
        var maxQuestionLength = 200 - fullPrefix.Length;
        if (compact.Length > maxQuestionLength)
            compact = compact[..Math.Max(0, maxQuestionLength - 3)] + "...";
        return fullPrefix + compact;
    }

    private const int GlobalMinPriority = -1000;
    private const int GlobalMaxPriority = 1000;
    private const int MaxRequiredCapabilities = 16;
    private const int MaxCapabilityLength = 64;

    private static IResult? ValidatePriority(int priority, Project project)
    {
        if (priority is < GlobalMinPriority or > GlobalMaxPriority)
            return Results.BadRequest(new
            {
                error = $"priority must be between {GlobalMinPriority} and {GlobalMaxPriority}"
            });
        if (project.MaxPriority is { } max && priority > max)
            return Results.BadRequest(new
            {
                error = $"priority {priority} exceeds project maxPriority {max}"
            });
        return null;
    }

    private static (IReadOnlyList<string>? Tags, IResult? Error) NormaliseRequiredCapabilities(
        IReadOnlyList<string> raw)
    {
        if (raw.Count > MaxRequiredCapabilities)
            return (null, Results.BadRequest(new
            {
                error = $"requiredCapabilities must contain at most {MaxRequiredCapabilities} entries"
            }));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var tag = value.Trim();
            if (tag.Length > MaxCapabilityLength)
                return (null, Results.BadRequest(new
                {
                    error = $"requiredCapabilities entries must be <= {MaxCapabilityLength} chars"
                }));
            try { Validation.ValidateNoOptionLikeOrControl(tag, "requiredCapabilities"); }
            catch (ArgumentException ex) { return (null, Results.BadRequest(new { error = ex.Message })); }
            if (seen.Add(tag)) result.Add(tag);
        }

        return (result, null);
    }
}

public sealed record QueueTaskTemplateRequest(
    string? Template = null,
    string? ProjectId = null,
    string? Agent = null,
    string? AgentClassId = null,
    int? Priority = null,
    int? MinModelScore = null,
    IReadOnlyList<string>? RequiredCapabilities = null);

public sealed record QueueTaskTemplateResponse(
    string Template,
    int Enqueued,
    IReadOnlyList<WorkItemDto> WorkItems);
