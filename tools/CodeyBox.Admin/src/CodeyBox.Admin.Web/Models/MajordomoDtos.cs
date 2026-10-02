using System.Text.Json;

namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// One row of the majordomo conversation as served by
/// <c>GET /majordomo/conversation</c>. Roles arrive lowercase
/// (<c>operator</c>, <c>majordomo</c>, <c>toolcall</c>, <c>toolresult</c>);
/// tool rows carry the exact vocabulary name they belong to.
/// </summary>
public sealed class MajordomoConversationEntryDto
{
    public long Sequence { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string? ToolName { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>One page of the majordomo conversation plus its rollup summary.</summary>
public sealed class MajordomoConversationPageDto
{
    public string? Summary { get; set; }
    public List<MajordomoConversationEntryDto> Entries { get; set; } = [];
}

/// <summary>Effective autonomy mode and where it came from.</summary>
public sealed class MajordomoModeDto
{
    /// <summary><c>autonomous</c> or <c>proposed</c>.</summary>
    public string Mode { get; set; } = "proposed";
    /// <summary><c>config</c> or <c>override</c>.</summary>
    public string Source { get; set; } = "config";
}

/// <summary>
/// One proposal as served by <c>GET /majordomo/proposals</c>: the queued
/// tool call, the reasoning behind it, and the reviewed change set the
/// operator approves or rejects.
/// </summary>
public sealed class MajordomoProposalDto
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonElement? Arguments { get; set; }
    public string? Reasoning { get; set; }
    public JsonElement? ReviewedChangeSet { get; set; }
    public string ProposedBy { get; set; } = "";
    public DateTimeOffset ProposedAt { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
    public List<string> ResultAffectedItems { get; set; } = [];
}

/// <summary>
/// Outcome of approving or rejecting a proposal. A refusal (stale, drifted,
/// already decided) is data, not an exception: <see cref="Ok"/> is false
/// and <see cref="Reason"/> names the refusal so the panel can say so
/// instead of failing silently.
/// </summary>
public sealed class MajordomoProposalDecisionDto
{
    public bool Ok { get; set; }
    /// <summary><c>approved</c>, <c>already-approved</c>, <c>rejected</c>, or <c>refused</c>.</summary>
    public string Status { get; set; } = "";
    /// <summary>Machine refusal code from the server (for example <c>proposal_drifted</c>).</summary>
    public string? Reason { get; set; }
    /// <summary>Human-readable detail accompanying a refusal.</summary>
    public string? Detail { get; set; }
    public List<string> AffectedItems { get; set; } = [];
}
