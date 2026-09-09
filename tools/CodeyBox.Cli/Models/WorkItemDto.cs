namespace CodeyBox.Cli.Models;

/// <summary>
/// Local copy of the orchestrator's work item response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST+JSON only.
/// </summary>
internal sealed class WorkItemDto
{
    public string Id { get; set; } = "";
    public string? ExternalId { get; set; }
    public Dictionary<string, string> ExternalIds { get; set; } = [];
    public WorkInitiatorDto? Initiator { get; set; }
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
    public string? TemplateName { get; set; }
    public int? TemplateEntryIndex { get; set; }

    /// <summary>
    /// Matches orchestrator <c>WorkItemDependencies.TerminalStates</c> (Merged is not terminal).
    /// </summary>
    internal static bool IsTerminalState(string state) =>
        state is "Done" or "Failed" or "Cancelled" or "AuditFailed"
            or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts";

    internal bool IsTerminal => IsTerminalState(State);

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

internal sealed class WorkInitiatorDto
{
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<WorkInitiatorProviderIdentityDto> ProviderIdentities { get; set; } = [];
}

internal sealed class WorkInitiatorProviderIdentityDto
{
    public string Provider { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Login { get; set; } = "";
}
