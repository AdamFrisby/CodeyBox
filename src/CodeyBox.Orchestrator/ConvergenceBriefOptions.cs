namespace CodeyBox.Orchestrator;

/// <summary>
/// Operational tuning knobs for composing convergence briefs from work item history.
/// Bound from <c>CodeyBox:ConvergenceBrief</c>.
/// </summary>
public sealed class ConvergenceBriefOptions
{
    /// <summary>
    /// Upper bound on the total character length of the generated brief.
    /// Default 32 KiB (32 * 1024 chars).
    /// </summary>
    public int MaxBriefChars { get; set; } = 32 * 1024;

    /// <summary>
    /// Per-section upper bound on captured stream excerpts or final assistant messages.
    /// Default 2,000 chars.
    /// </summary>
    public int MaxExcerptChars { get; set; } = 2_000;

    /// <summary>
    /// Per-finding upper bound on audit finding descriptions.
    /// Default 1,000 chars.
    /// </summary>
    public int MaxFindingDescriptionChars { get; set; } = 1_000;

    /// <summary>
    /// Upper bound on error message texts.
    /// Default 1,000 chars.
    /// </summary>
    public int MaxErrorMessageChars { get; set; } = 1_000;
}
