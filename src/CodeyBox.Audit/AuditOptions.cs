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
    /// capability group). Defaults to 30 minutes. The original 10-minute
    /// default was too tight in practice: LLM auditors at high reasoning
    /// effort routinely take 5–10 minutes apiece, and an iteration may
    /// run several in sequence, plus the local toolchain auditors and
    /// the rework agent itself. 30 min gives enough headroom for typical
    /// repos; large codebases (e.g. CodeyBox auditing itself) may want
    /// to override this further via project config.
    /// </summary>
    public TimeSpan PerIterationTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// If true, stop running auditors as soon as one returns a failing
    /// finding. Useful when you have an expensive LLM auditor after cheap
    /// linters — no point paying for the LLM if a linter already failed.
    /// </summary>
    public bool StopOnFirstFailure { get; init; }
}
