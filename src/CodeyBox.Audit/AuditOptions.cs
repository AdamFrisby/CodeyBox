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
    /// capability group). Defaults to 120 minutes. Earlier defaults (10,
    /// then 30) routinely clipped legitimate work: LLM auditors at high
    /// reasoning effort routinely take 5–10 minutes apiece, an iteration
    /// may run several in sequence, plus local toolchain auditors and
    /// the rework agent itself; on a large self-audit run the cumulative
    /// time can exceed an hour without anything actually being stuck.
    /// Operational note: in the system's history to date no auditor has
    /// genuinely hung, so a generous ceiling is preferred over losing
    /// hours of work to a too-tight timer.
    /// </summary>
    public TimeSpan PerIterationTimeout { get; init; } = TimeSpan.FromMinutes(120);

    /// <summary>
    /// If true, stop running auditors as soon as one returns a failing
    /// finding. Useful when you have an expensive LLM auditor after cheap
    /// linters — no point paying for the LLM if a linter already failed.
    /// </summary>
    public bool StopOnFirstFailure { get; init; }
}
