using CodeyBox.Admin.Web.Services;

namespace CodeyBox.Admin.Web.Models;

public sealed class SuggestionDto
{
    public string Id { get; set; } = "";
    public string SourceWorkItemId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string EstimatedEffort { get; set; } = "";
    public List<string> FilesReferenced { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = "open";
    public string? DismissReason { get; set; }
    public string? PromotedToWorkItemId { get; set; }

    public string ShortWorkItemId => SourceWorkItemId.Length >= 8 ? SourceWorkItemId[..8] : SourceWorkItemId;

    public string Age => AdminFormat.FormatShortAge(DateTimeOffset.UtcNow - CreatedAt);

}
