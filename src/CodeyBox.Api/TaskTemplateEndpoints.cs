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
        WorkItemCreationService creation,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.Template) && !TemplateRefsMatch(req.Template, name))
            return Results.BadRequest(new { error = "body template must match route template name" });

        return await QueueCoreAsync(
            req with { Template = name },
            registry,
            creation,
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
        WorkItemCreationService creation,
        CancellationToken ct)
    {
        return await QueueCoreAsync(req, registry, creation, ct);
    }

    private static async Task<IResult> QueueCoreAsync(
        QueueTaskTemplateRequest req,
        ITaskTemplateRegistry registry,
        WorkItemCreationService creation,
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

        var preparedItems = new List<PreparedWorkItemCreation>(template.Checks.Count);
        for (var i = 0; i < template.Checks.Count; i++)
        {
            var prepared = await creation.PrepareAsync(
                BuildCreateRequest(req, i, template.Checks[i]),
                new WorkItemCreationProvenance(template.Name, i),
                ct);
            if (prepared.Error is not null) return prepared.Error;
            preparedItems.Add(prepared.Prepared!);
        }

        var queued = new List<QueuedTaskTemplateItem>(preparedItems.Count);
        foreach (var prepared in preparedItems)
        {
            var committed = await creation.CommitAsync(prepared, ct);
            if (committed.Error is not null) return committed.Error;
            queued.Add(new QueuedTaskTemplateItem(
                committed.Item.Id.ToString(),
                committed.Item.ProjectId.Value,
                committed.Item.Title,
                committed.Item.TemplateName!,
                committed.Item.TemplateEntryIndex!.Value));
        }

        return Results.Created("/workitems", new QueueTaskTemplateResponse(
            template.Name,
            queued.Count,
            queued));
    }

    private static CreateWorkItemRequest BuildCreateRequest(
        QueueTaskTemplateRequest req,
        int entryIndex,
        TaskTemplateCheck entry)
    {
        var onYes = entry.OnYes;
        return new CreateWorkItemRequest(
            ProjectId: req.ProjectId!,
            Title: entry.Title ?? BuildGeneratedTitle(entryIndex, entry.Question),
            Prompt: entry.Prompt ?? entry.Question,
            Agent: req.Agent,
            AuditorProfile: null,
            AgentClassId: req.AgentClassId,
            BaseBranch: null,
            WorkBranch: null,
            PushUpstream: null,
            WorkTimeoutMinutes: null,
            MergeTimeoutMinutes: null,
            ExternalId: null,
            DependsOn: null,
            MinModelScore: req.MinModelScore,
            ReleaseId: null,
            Priority: req.Priority,
            ExternalIds: null,
            RequiredCapabilities: req.RequiredCapabilities,
            Check: new CheckAndActRequest(
                Question: entry.Question,
                OnYes: new OnYesActionRequest(
                    Title: onYes.Title,
                    Prompt: onYes.Prompt,
                    MinModelScore: onYes.MinModelScore,
                    Priority: onYes.Priority,
                    Agent: onYes.Agent,
                    AgentClassId: onYes.AgentClassId,
                    DependsOn: onYes.DependsOn,
                    Knobs: onYes.Knobs),
                ActionableAnswer: entry.ActionableAnswer,
                Mode: entry.Mode));
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
    IReadOnlyList<QueuedTaskTemplateItem> WorkItems);

public sealed record QueuedTaskTemplateItem(
    string Id,
    string ProjectId,
    string Title,
    string TemplateName,
    int TemplateEntryIndex);
