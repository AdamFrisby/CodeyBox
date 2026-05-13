namespace CodeyBox.Cli.Models;

/// <summary>
/// Local copy of the orchestrator's work item response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST+JSON only.
/// </summary>
internal sealed class WorkItemDto
{
    public string Id { get; set; } = "";
    public string? ExternalId { get; set; }
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Agent { get; set; } = "";
    public string? AuditorProfile { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? BaseBranch { get; set; }
    public string? WorkBranch { get; set; }
    public string State { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastError { get; set; }
    public int UpstreamPushAttempts { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public bool DependsOnSatisfied { get; set; }
    public Dictionary<string, string?> DependsOnExternalIds { get; set; } = [];
    public long QueuePosition { get; set; }

    internal bool IsTerminal => State is "Done" or "Failed" or "Cancelled" or "AuditFailed";

    internal string ShortId => Id.Length >= 8 ? Id[..8] : Id;

    internal string RelativeAge
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - UpdatedAt;
            if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }
}
