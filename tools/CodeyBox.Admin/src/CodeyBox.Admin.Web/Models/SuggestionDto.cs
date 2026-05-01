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

    public string Age
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - CreatedAt;
            if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h";
            return $"{(int)elapsed.TotalDays}d";
        }
    }

    public string SeverityCss => Severity switch
    {
        "important" => "severity-important",
        "notable" => "severity-notable",
        _ => "severity-minor",
    };
}
