namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// A task template the orchestrator can queue as check-and-act work items
/// (<c>GET /templates</c>). Locally defined — REST + JSON only.
/// </summary>
public sealed class TaskTemplateDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int? CheckCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>Body for <c>POST /templates/queue</c>.</summary>
public sealed class QueueTaskTemplateRequest
{
    public string? Template { get; set; }
    public string? ProjectId { get; set; }
    public string? Agent { get; set; }
    public string? AgentClassId { get; set; }
    public int? Priority { get; set; }
    public int? MinModelScore { get; set; }
    public List<string>? RequiredCapabilities { get; set; }
}

/// <summary>Queued items accepted from a template request.</summary>
public sealed class QueuedTaskTemplateResponse
{
    public string Template { get; set; } = "";
    public int Enqueued { get; set; }
    public List<QueuedTaskTemplateItem> WorkItems { get; set; } = [];
}

/// <summary>One work item queued from a template entry.</summary>
public sealed class QueuedTaskTemplateItem
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public int TemplateEntryIndex { get; set; }
}
