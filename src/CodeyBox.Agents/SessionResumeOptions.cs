using System.Threading;
using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Config-driven knob bounding how many times <see cref="CliAgentRunnerBase"/>
/// will rebuild a failed agent CLI invocation as a CLI-native session resume
/// (e.g. <c>claude --resume &lt;session-id&gt;</c>) inside the same sandbox
/// before giving up and surfacing the failure.
///
/// <para>
/// Session resume is a recovery for transient agent-process crashes — non-zero
/// exit, sandbox still alive, the agent CLI emitted a session id during the
/// crashed run. Without resume, the orchestrator currently fails the whole work
/// item and re-runs from scratch, throwing away the agent's accumulated
/// conversation/context (and burning more of the quota). Bounded retries keep
/// the cost capped while letting a one-off blip recover in place.
/// </para>
///
/// <para>
/// Hot-reloadable via <see cref="SetMaxResumeAttempts"/> (called from the
/// <c>CodeyBox:PipelineTuning</c> hot-reload coordinator). Defaults to 2 — one
/// retry covers the typical OOM / SIGPIPE / network-hiccup blip, the second
/// exists so a single mid-resume blip does not collapse the work item. Hard
/// quota/auth failures are filtered out by <see cref="IsResumeEligible"/>
/// so they defer via the quota path instead of consuming the resume budget.
/// Soft 429/rate-limit blips may spend the bounded resume budget unless the
/// output carries an explicit reset window. Deterministic work failures
/// (<see cref="AgentFailureKind.Normal"/>) are not resume-eligible.
/// </para>
/// </summary>
public static class SessionResumeOptions
{
    public const int DefaultMaxResumeAttempts = 2;
    public const int MaxAllowedResumeAttempts = 10;

    private static int _maxResumeAttempts = DefaultMaxResumeAttempts;

    /// <summary>
    /// Maximum CLI-native session-resume retries per agent run. A value of 0
    /// disables session resume entirely (the base runner falls back to the
    /// legacy single-shot re-invocation path). Values supplied via hot-reload
    /// are clamped to the inclusive range 0 to <see cref="MaxAllowedResumeAttempts"/>
    /// so operator typos cannot create unbounded relaunch loops.
    /// </summary>
    public static int MaxResumeAttempts => Volatile.Read(ref _maxResumeAttempts);

    /// <summary>
    /// Update the resume-attempt budget. Negative values are clamped to 0
    /// (disables resume), and very large values are capped at
    /// <see cref="MaxAllowedResumeAttempts"/> so an operator typo via the
    /// hot-reload config does not produce nonsensical or expensive behaviour.
    /// </summary>
    public static void SetMaxResumeAttempts(int value)
    {
        if (value < 0) value = 0;
        if (value > MaxAllowedResumeAttempts) value = MaxAllowedResumeAttempts;
        Volatile.Write(ref _maxResumeAttempts, value);
    }

    /// <summary>
    /// Returns true when a failed run's <paramref name="classification"/> is
    /// transient enough that re-running with <c>--resume</c> has a realistic
    /// chance of completing. Hard quota / auth failures would immediately
    /// re-fail on resume, so they defer / fall back per the normal quota path
    /// instead of resume-hammering. Soft rate-limit classifications can resume
    /// only when no reset window was parsed from the failure streams.
    /// </summary>
    public static bool IsResumeEligible(
        AgentFailureClassification classification,
        string? stderr = null,
        string? stdout = null) =>
        classification.Kind switch
        {
            AgentFailureKind.QuotaExhausted => IsSoftRateLimitWithoutReset(classification, stderr, stdout),
            AgentFailureKind.TransientNetwork => true,
            AgentFailureKind.Unknown => true,
            _ => false,
        };

    private static bool IsSoftRateLimitWithoutReset(
        AgentFailureClassification classification,
        string? stderr,
        string? stdout)
    {
        if (classification.QuotaFailure != AgentQuotaFailureKind.SoftRateLimit)
            return false;
        if (classification.QuotaResetAt is not null)
            return false;
        return QuotaResetParser.TryParseResetAt([stderr, stdout]) is null;
    }
}
