namespace CodeyBox.Core;

/// <summary>
/// Produces a human-readable PR body from the diff and agent context.
/// Called by the upstream remote after pushing the work branch and before
/// opening the pull request. Implementations may throw; callers must enforce
/// a timeout and fall back to the static template on failure.
/// </summary>
public interface IPullRequestDescriptionGenerator
{
    Task<string> GenerateAsync(PullRequestDescriptionRequest request, CancellationToken ct);
}

/// <summary>
/// Selects which <see cref="IPullRequestDescriptionGenerator"/> implementation
/// produces pull request descriptions for a project. Bound from configuration
/// (e.g. <c>Upstream.PrDescription.Strategy</c>) so operators can switch
/// strategies without a restart; project configuration is reloaded through
/// <c>IOptionsMonitor</c>. Default: <see cref="Completion"/>.
/// </summary>
public enum PrDescriptionStrategy
{
    /// <summary>
    /// Tool-less completion call via <see cref="ICompletionClient"/> against a
    /// configured completion endpoint. Creates no sandbox and runs no agent.
    /// </summary>
    Completion = 0,

    /// <summary>
    /// Runs the configured agent CLI inside a provisioned sandbox over the
    /// diff. Requires <c>SandboxImageReference</c>.
    /// </summary>
    Agentic = 1,
}

/// <summary>Input to <see cref="IPullRequestDescriptionGenerator.GenerateAsync"/>.</summary>
public sealed record PullRequestDescriptionRequest
{
    /// <summary>git diff --stat output between base and work branches (compact summary).</summary>
    public required string DiffSummary { get; init; }

    /// <summary>
    /// Full git diff output between base and work branches.
    /// Callers need not pre-truncate; <see cref="IPullRequestDescriptionGenerator.GenerateAsync"/>
    /// applies <see cref="PrDescriptionOptions.MaxDiffBytes"/> truncation internally.
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

    /// <summary>
    /// Full commit messages the agent produced on the work branch (subjects
    /// plus bodies, oldest first). Each entry is pre-truncated to at most
    /// 2 KB and the list holds at most 20 entries; strategies include them
    /// in the generation prompt so the model sees the agent's own summary
    /// of each change alongside the raw diff.
    /// </summary>
    public IReadOnlyList<string> CommitMessages { get; init; } = [];
}
