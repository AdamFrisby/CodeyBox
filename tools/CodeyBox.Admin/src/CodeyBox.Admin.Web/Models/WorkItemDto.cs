namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's work item response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class WorkItemDto
{
    public string Id { get; set; } = "";
    public string? ExternalId { get; set; }
    public Dictionary<string, string> ExternalIds { get; set; } = [];
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Agent { get; set; } = "";
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
    public int Priority { get; set; }
    public string? ReplayOfWorkItemId { get; set; }
    public string? AgentClassId { get; set; }
    public int? AuditIterations { get; set; }
    public int? FinalAuditBlockingFindings { get; set; }
    /// <summary>
    /// GitHub-side authoritative merge commit sha — resolves on
    /// <c>GET /repos/{owner}/{repo}/commits/{sha}</c>. Null until the auto-merge
    /// API call lands. Historical rows (pre-2026-06) may carry a stale local
    /// sha here that does NOT resolve on the GitHub API; cross-reference by
    /// <see cref="MergedPrNumber"/> / <see cref="MergedPrUrl"/> for those.
    /// </summary>
    public string? MergeSha { get; set; }

    /// <summary>Local bare-repo merge sha; not resolvable on GitHub.</summary>
    public string? LocalSquashSha { get; set; }

    /// <summary>Forge-assigned PR number once the upstream push opens one.</summary>
    public int? MergedPrNumber { get; set; }

    /// <summary>URL of the upstream PR opened for this work item.</summary>
    public string? MergedPrUrl { get; set; }

    public bool IsTerminal => State is "Done" or "Failed" or "Cancelled" or "AuditFailed";
    public bool IsQueued => State == "Queued";
    public bool IsInFlight => !IsTerminal && !IsQueued;

    public string ShortId => Id.Length >= 8 ? Id[..8] : Id;

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
}
