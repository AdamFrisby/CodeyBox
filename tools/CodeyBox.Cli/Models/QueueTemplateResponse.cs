namespace CodeyBox.Cli.Models;

internal sealed class QueueTemplateResponse
{
    public string Template { get; set; } = "";
    public int Enqueued { get; set; }
    public List<WorkItemDto> WorkItems { get; set; } = [];
}
