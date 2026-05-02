namespace CodeyBox.Core;

/// <summary>
/// Produces a human-readable PR body from the diff and agent context.
/// Called by the upstream remote after pushing the work branch and before
/// opening the pull request. Implementations must be non-throwing on their
/// own failures — the caller is responsible for enforcing a timeout and
/// falling back to the static template when the generator fails.
/// </summary>
public interface IPullRequestDescriptionGenerator
{
    Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct);
}

/// <summary>Input to <see cref="IPullRequestDescriptionGenerator.GenerateAsync"/>.</summary>
public sealed record PullRequestDescriptionRequest
{
    /// <summary>git diff --stat output between base and work branches (compact summary).</summary>
    public required string DiffSummary { get; init; }

    /// <summary>
    /// Full git diff output between base and work branches, capped at
    /// <see cref="PrDescriptionOptions.MaxDiffBytes"/> and truncated from the
    /// middle so both the first and last diff hunks are preserved.
    /// </summary>
    public required string FullDiff { get; init; }

    /// <summary>Work item title passed as the PR title.</summary>
    public required string Title { get; init; }

    /// <summary>Original work item prompt, truncated to 2 KB.</summary>
    public required string Prompt { get; init; }

    /// <summary>Titles of audit findings the agent addressed across rework iterations.</summary>
    public IReadOnlyList<string> AddressedFindings { get; init; } = [];

    /// <summary>Last 2 KB of agent stdout — the agent's concluding reasoning.</summary>
    public string? AgentReasoningTail { get; init; }
}
