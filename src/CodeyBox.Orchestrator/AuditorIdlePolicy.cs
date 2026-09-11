namespace CodeyBox.Orchestrator;

/// <summary>
/// Decision returned by <see cref="AuditorIdlePolicy.Decide"/> when a single
/// auditor run has gone quiet.
/// </summary>
public enum AuditorIdleDecision
{
    /// <summary>
    /// Keep waiting: the auditor is demonstrably progressing (its task already
    /// finished and the result is ready, or it still holds live sandbox work)
    /// or the quiet window has not elapsed yet.
    /// </summary>
    KeepWaiting,

    /// <summary>
    /// Declare the run idle: no output for at least the idle window AND no
    /// evidence of live progress.
    /// </summary>
    DeclareIdle,

    /// <summary>
    /// The run outlived the absolute per-auditor bound. Fires regardless of
    /// liveness so a never-quiet-but-never-finishing run cannot hang forever.
    /// </summary>
    AbsoluteExceeded,
}

/// <summary>
/// Pure idle-vs-progress decision for a single auditor run.
///
/// <para>
/// The legacy guard classified an auditor as idle from elapsed wall time
/// alone: no stdout chunk for longer than <c>AuditorIdleTimeout</c> meant
/// termination. A long <c>dotnet test</c> suite legitimately emits nothing
/// until the final result, so a working run was killed and the iteration was
/// recorded <c>incomplete</c> even though the auditor process was alive and
/// would have produced a verdict. The pipeline now consults this policy at
/// the moment the quiet window elapses: an auditor whose task already
/// completed, or whose sandbox still holds active execs, is progressing and
/// keeps its slot; only a quiet run with no live work is declared idle. The
/// absolute bound is the backstop that keeps a genuinely hung (but
/// exec-holding) run from living forever.
/// </para>
/// </summary>
public static class AuditorIdlePolicy
{
    /// <summary>
    /// Decides what to do when an auditor run is evaluated.
    /// </summary>
    /// <param name="quietElapsed">Time since the last observed output.</param>
    /// <param name="idleTimeout">Quiet window that marks a run idle. Zero or negative disables the idle leg.</param>
    /// <param name="totalElapsed">Time since the auditor run started.</param>
    /// <param name="absoluteTimeout">Absolute per-auditor bound. Zero or negative disables the absolute leg.</param>
    /// <param name="auditorTaskCompleted">True when the auditor task already finished (its result is ready).</param>
    /// <param name="hasActiveExecs">True when the sandbox still reports live execs for this run.</param>
    public static AuditorIdleDecision Decide(
        TimeSpan quietElapsed,
        TimeSpan idleTimeout,
        TimeSpan totalElapsed,
        TimeSpan absoluteTimeout,
        bool auditorTaskCompleted,
        bool hasActiveExecs)
    {
        if (auditorTaskCompleted)
            return AuditorIdleDecision.KeepWaiting;

        if (absoluteTimeout > TimeSpan.Zero && totalElapsed >= absoluteTimeout)
            return AuditorIdleDecision.AbsoluteExceeded;

        if (idleTimeout <= TimeSpan.Zero || quietElapsed < idleTimeout)
            return AuditorIdleDecision.KeepWaiting;

        return hasActiveExecs
            ? AuditorIdleDecision.KeepWaiting
            : AuditorIdleDecision.DeclareIdle;
    }
}
