namespace CodeyBox.Cli.Models;

internal sealed class QueueTemplateRequest
{
    public string Template { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? Agent { get; set; }
    public string? AgentClassId { get; set; }
    public int? Priority { get; set; }
    public int? MinModelScore { get; set; }
}
