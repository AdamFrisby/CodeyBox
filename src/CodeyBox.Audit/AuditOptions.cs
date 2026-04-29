using CodeyBox.Core;

namespace CodeyBox.Audit;

public sealed record AuditOptions
{
    /// <summary>Maximum audit + rework cycles before giving up.</summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>
    /// Findings at or above this severity cause the audit to fail. Lower-
    /// severity findings are still surfaced to the agent on rework but do
    /// not on their own block the merge.
    /// </summary>
    public AuditSeverity FailingSeverity { get; init; } = AuditSeverity.Error;

    /// <summary>
    /// Wall-clock budget for a single audit iteration's sandbox (per
    /// capability group). Defaults to 10 minutes.
    /// </summary>
    public TimeSpan PerIterationTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// If true, stop running auditors as soon as one returns a failing
    /// finding. Useful when you have an expensive LLM auditor after cheap
    /// linters — no point paying for the LLM if a linter already failed.
    /// </summary>
    public bool StopOnFirstFailure { get; init; }
}
