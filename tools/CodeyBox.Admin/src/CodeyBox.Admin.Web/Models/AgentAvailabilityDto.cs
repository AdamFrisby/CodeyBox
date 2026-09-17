namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// One row of <c>GET /admin/agents/availability</c>: whether the agent can
/// currently take work and why not.
/// </summary>
public sealed class AgentAvailabilityDto
{
    public string Agent { get; set; } = "";
    public bool Excluded { get; set; }
    public string? Reason { get; set; }
}
