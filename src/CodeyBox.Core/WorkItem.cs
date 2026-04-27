namespace CodeyBox.Core;

/// <summary>
/// A unit of work to be performed by an agent inside a sandbox. Immutable;
/// state transitions produce new instances via <see cref="With"/>.
/// </summary>
public sealed record WorkItem
{
    public required WorkItemId Id { get; init; }

    /// <summary>Human-readable label for logs and the API.</summary>
    public required string Title { get; init; }

    /// <summary>The natural-language task to give to the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>Origin git URL the work-phase sandbox clones from. Resolved by the host (typically a local bare repo it manages).</summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>Branch to base the agent's work on. Defaults to the host repo's default branch.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Branch the agent pushes its work to. Generated if null.</summary>
    public string? WorkBranch { get; init; }

    /// <summary>Which agent runner to use.</summary>
    public required AgentKind Agent { get; init; }

    /// <summary>Wall-clock budget for the work phase.</summary>
    public TimeSpan WorkTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Wall-clock budget for the merge phase.</summary>
    public TimeSpan MergeTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>If true and an upstream is configured, push main to it after merge.</summary>
    public bool PushUpstream { get; init; } = true;

    public WorkItemState State { get; init; } = WorkItemState.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message if state is Failed.</summary>
    public string? LastError { get; init; }

    /// <summary>Number of attempts that have been made on the upstream-push phase.</summary>
    public int UpstreamPushAttempts { get; init; }

    public WorkItem With(WorkItemState state, string? error = null) => this with
    {
        State = state,
        LastError = error,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
