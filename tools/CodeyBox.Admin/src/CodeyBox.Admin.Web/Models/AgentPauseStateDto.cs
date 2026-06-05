namespace CodeyBox.Admin.Web.Models;

public sealed class AgentPauseStateDto
{
    public string Agent { get; set; } = "";
    public bool Paused { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public string? PausedReason { get; set; }
    public string? PausedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
