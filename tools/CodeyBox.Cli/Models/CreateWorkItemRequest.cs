namespace CodeyBox.Cli.Models;

internal sealed class CreateWorkItemRequest
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? Agent { get; set; }
    public string? BaseBranch { get; set; }
    public string? WorkBranch { get; set; }
    public bool? PushUpstream { get; set; }
    public List<string>? DependsOn { get; set; }
}
