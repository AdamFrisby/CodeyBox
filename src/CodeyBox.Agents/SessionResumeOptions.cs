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
/// retry covers the typical 429 / OOM / SIGPIPE blip, the second exists so a
/// single mid-resume blip does not collapse the work item.
/// </para>
/// </summary>
public static class SessionResumeOptions
{
    public const int DefaultMaxResumeAttempts = 2;

    private static int _maxResumeAttempts = DefaultMaxResumeAttempts;

    /// <summary>
    /// Maximum CLI-native session-resume retries per agent run. A value of 0
    /// disables session resume entirely (the base runner falls back to the
    /// legacy single-shot re-invocation path).
    /// </summary>
    public static int MaxResumeAttempts => Volatile.Read(ref _maxResumeAttempts);

    public static void SetMaxResumeAttempts(int value)
    {
        if (value < 0) value = 0;
        Volatile.Write(ref _maxResumeAttempts, value);
    }

    /// <summary>
    /// Returns true when a failed run's <paramref name="classification"/> is
    /// transient enough that re-running with <c>--resume</c> has a realistic
    /// chance of completing. Quota / auth failures would immediately re-fail
    /// on resume, so they defer / fall back per the normal quota path instead
    /// of resume-hammering.
    /// </summary>
    public static bool IsResumeEligible(AgentFailureClassification classification) =>
        classification.Kind switch
        {
            AgentFailureKind.QuotaExhausted => false,
            AgentFailureKind.AuthError => false,
            _ => true,
        };
}
