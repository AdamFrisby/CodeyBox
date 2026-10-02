namespace CodeyBox.Majordomo;

/// <summary>How a majordomo wakeup concluded. Every wakeup records one of
/// these, including the ones that did nothing — the operator sees the full
/// ledger, not just the passes that found work.</summary>
public enum MajordomoWakeupOutcome
{
    /// <summary>A findings report was stored in the conversation (and any
    /// approved mutations ran under the live autonomy mode).</summary>
    Reported,

    /// <summary>The pass found nothing: no conversation row was appended.</summary>
    QuietNoFindings,

    /// <summary>Skipped because a turn was already in flight; never queued.</summary>
    SkippedTurnInFlight,

    /// <summary>Skipped because wakeups are disabled in configuration.</summary>
    SkippedDisabled,

    /// <summary>An event burst inside the trigger floor collapsed to the in-flight pass.</summary>
    CollapsedBurst,

    /// <summary>A scheduled tick arrived before the cadence came due.</summary>
    NotDue,
}

/// <summary>One mutation a wakeup pass proposed to make.</summary>
/// <param name="ToolName">Exact mutate-tool name.</param>
/// <param name="Arguments">Typed arguments for the call.</param>
public sealed record MajordomoWakeupMutation(string ToolName, MajordomoToolArgs Arguments)
{
    public string ToolName { get; } = string.IsNullOrWhiteSpace(ToolName)
        ? throw new ArgumentException("tool name must be non-empty", nameof(ToolName))
        : ToolName;
    public MajordomoToolArgs Arguments { get; } = Arguments ?? throw new ArgumentNullException(nameof(Arguments));
}

/// <summary>What one wakeup pass observed.</summary>
/// <param name="HasFindings">Whether anything is worth telling the operator.</param>
/// <param name="ReportText">The report to store when <paramref name="HasFindings"/> is true.</param>
/// <param name="Mutations">Mutations the pass wants; each is gated by <see cref="MajordomoAuthorization"/>.</param>
public sealed record MajordomoWakeupAssessment(
    bool HasFindings,
    string ReportText = "",
    IReadOnlyList<MajordomoWakeupMutation>? Mutations = null)
{
    public IReadOnlyList<MajordomoWakeupMutation> Mutations { get; } = Mutations ?? [];
}

/// <summary>What one wakeup did.</summary>
public sealed record MajordomoWakeupResult(
    MajordomoWakeupOutcome Outcome,
    MajordomoWakeupKind Kind,
    string Reason,
    int Executed,
    int Proposed,
    int Refused,
    DateTimeOffset At);

/// <summary>One visible ledger row per wakeup attempt, including no-ops.</summary>
public sealed record MajordomoWakeupRecord(
    DateTimeOffset At,
    MajordomoWakeupKind Kind,
    string Reason,
    MajordomoWakeupOutcome Outcome,
    int Executed,
    int Proposed,
    int Refused);
