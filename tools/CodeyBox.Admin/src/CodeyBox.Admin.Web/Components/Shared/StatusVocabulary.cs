namespace CodeyBox.Admin.Web.Components.Shared;

/// <summary>
/// The single status vocabulary for the admin UI: every state renders through
/// this map so the same value looks identical on every page. Colour is never
/// the only carrier — each entry pairs a tone (colour) with a distinct glyph
/// and a text label, keeping states readable for colourblind operators and at
/// a glance across a room.
/// </summary>
public sealed record StatusInfo(string Text, string Glyph, string Tone, string Title);

/// <summary>
/// Pure lookup tables from backend state names to <see cref="StatusInfo"/>.
/// All methods are total functions: unknown or differently-cased input falls
/// back to a neutral entry rather than throwing, so a new backend state can
/// never blank a page.
/// </summary>
public static class StatusVocabulary
{
    /// <summary>Every known work-item state name (mirrors the orchestrator enum).</summary>
    public static readonly string[] AllWorkItemStates =
    [
        "Queued", "Working", "WorkComplete", "Auditing", "Reworking",
        "AuditPassed", "Merging", "Merged", "UpstreamPushing", "Done",
        "NeedsOperatorInput", "WaitingForQuotaReset", "ReworkingForConflict",
        "WaitingForAgentResume", "WaitingForTransientRetry", "Planning",
        "PlanReview", "PlanApproved", "Delegating", "Failed", "Cancelled",
        "AuditFailed", "MergeConflictResolutionFailed",
        "AbandonedAfterRecoveryAttempts", "NoActionRequired",
    ];

    // Glyphs are unique per state inside this map (not just the tone), so a
    // colourblind operator can tell any two states apart without reading text.
    private static readonly Dictionary<string, StatusInfo> WorkItems =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Queued"] = new("Queued", "○", "queued", "Queued — waiting for a pickup slot"),
            ["Working"] = new("Working", "▶", "active", "Working — an agent is executing"),
            ["WorkComplete"] = new("Work complete", "◔", "review", "Work complete — awaiting audit"),
            ["Auditing"] = new("Auditing", "◐", "active", "Auditing — an auditor is reviewing"),
            ["Reworking"] = new("Reworking", "↻", "rework", "Reworking — addressing audit findings"),
            ["AuditPassed"] = new("Audit passed", "☑", "review", "Audit passed — awaiting merge"),
            ["Merging"] = new("Merging", "⤺", "active", "Merging — integrating the work branch"),
            ["Merged"] = new("Merged", "⤳", "done", "Merged — awaiting upstream push"),
            ["UpstreamPushing"] = new("Pushing", "⬆", "active", "Pushing — opening or updating the upstream PR"),
            ["Done"] = new("Done", "✓", "done", "Done — merged and pushed"),
            ["NeedsOperatorInput"] = new("Needs you", "◉", "wait", "Parked — waiting for operator input"),
            ["WaitingForQuotaReset"] = new("Quota wait", "⏳", "wait", "Parked — all agents hit quota, auto-retries on reset"),
            ["ReworkingForConflict"] = new("Conflict fix", "⚔", "rework", "Resolving merge conflicts with the work agent"),
            ["WaitingForAgentResume"] = new("Agent paused", "⏸", "wait", "Parked — every eligible agent is paused by an operator"),
            ["WaitingForTransientRetry"] = new("Retrying", "⇄", "wait", "Parked — transient transport failure, backoff retry scheduled"),
            ["Planning"] = new("Planning", "✎", "active", "Planning — drafting a reviewable plan"),
            ["PlanReview"] = new("Plan review", "◎", "review", "Plan review — an auditor is checking the plan"),
            ["PlanApproved"] = new("Plan approved", "✔", "review", "Plan approved — ready to enter the work lifecycle"),
            ["Delegating"] = new("Delegating", "➔", "active", "Delegating — one repair turn by a delegate agent"),
            ["Failed"] = new("Failed", "✗", "fail", "Failed — terminal, retry available"),
            ["Cancelled"] = new("Cancelled", "⊘", "muted", "Cancelled by an operator"),
            ["AuditFailed"] = new("Audit failed", "‼", "fail", "Audit failed — terminal, retry available"),
            ["MergeConflictResolutionFailed"] = new("Merge failed", "⚠", "fail", "Merge conflict resolution failed — terminal"),
            ["AbandonedAfterRecoveryAttempts"] = new("Abandoned", "⊗", "fail", "Abandoned — host recovery retries exhausted"),
            ["NoActionRequired"] = new("No action", "➖", "muted", "No action required — agent reported the precondition does not hold"),
        };

    private static readonly Dictionary<string, StatusInfo> ReleaseStates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Open"] = new("Open", "○", "queued", "Release open — collecting work items"),
            ["InReview"] = new("In review", "◐", "review", "Release in review"),
            ["In-Review"] = new("In review", "◐", "review", "Release in review"),
            ["Released"] = new("Released", "✓", "done", "Release shipped"),
            ["Closed"] = new("Closed", "⊘", "muted", "Release closed without shipping"),
            ["Failed"] = new("Failed", "✗", "fail", "Release failed"),
        };

    private static readonly Dictionary<string, StatusInfo> SuggestionStates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["open"] = new("Open", "○", "queued", "Suggestion awaiting triage"),
            ["dismissed"] = new("Dismissed", "⊘", "muted", "Suggestion dismissed by an operator"),
            ["promoted"] = new("Promoted", "➔", "done", "Suggestion promoted to a work item"),
        };

    private static readonly Dictionary<string, StatusInfo> AgentAvailability =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Available"] = new("Available", "✓", "done", "Agent is accepting work"),
            ["Active"] = new("Active", "▶", "active", "Agent is actively working"),
            ["Paused"] = new("Paused", "⏸", "wait", "Agent paused by an operator"),
            ["QuotaExhausted"] = new("Quota out", "⏳", "wait", "Agent exhausted its quota"),
            ["Backoff"] = new("Backing off", "⇄", "wait", "Agent in transient-error backoff"),
            ["Unknown"] = new("Unknown", "?", "muted", "Agent availability unknown"),
        };

    /// <summary>One-line fleet/project rollup derived from aggregate counts.</summary>
    public static StatusInfo ForProject(bool isPaused, bool hasRecentFailures, int inFlight, int queued) =>
        isPaused ? new("Paused", "⏸", "wait", "Project paused")
        : hasRecentFailures ? new("Failing", "⚠", "fail", "Recent failures — needs attention")
        : inFlight > 0 ? new("Active", "▶", "active", "Agents working on this project")
        : queued > 0 ? new("Queued", "○", "queued", "Items waiting for a slot")
        : new("Idle", "·", "muted", "No queued or in-flight work");

    /// <summary>Quota band from a 0–100 remaining percentage.</summary>
    public static StatusInfo ForQuotaBand(int? pct) => pct switch
    {
        null => new("Quota ?", "?", "muted", "Quota unknown"),
        <= 0 => new("Quota out", "⏳", "fail", "Quota exhausted"),
        < 25 => new("Quota low", "⚠", "fail", "Quota critically low"),
        < 50 => new("Quota tight", "◐", "review", "Quota below half"),
        _ => new("Quota ok", "●", "done", "Quota healthy"),
    };

    /// <summary>Legacy budget-bar CSS class for the same band (single source of truth).</summary>
    public static string BudgetBarCss(int pct) =>
        pct >= 100 ? "budget-full" : pct >= 80 ? "budget-warn" : "";

    /// <summary>Legacy quota-bar CSS class for a remaining percentage.</summary>
    public static string QuotaBarCss(int remainingPct) =>
        remainingPct <= 10 ? "budget-full" : remainingPct <= 25 ? "budget-warn" : "";

    /// <summary>Circuit-breaker state for an agent class.</summary>
    public static StatusInfo ForBreaker(string? state) => state?.ToLowerInvariant() switch
    {
        "open" => new("Breaker open", "⚠", "fail", "Circuit breaker open — dispatch halted"),
        "halfopen" or "half-open" => new("Breaker probing", "◐", "wait", "Circuit breaker half-open — trial dispatches only"),
        "closed" => new("Breaker closed", "✓", "done", "Circuit breaker closed — normal dispatch"),
        _ => new("Breaker ?", "?", "muted", "Circuit breaker state unknown"),
    };

    public static StatusInfo ForWorkItem(string? state)
    {
        if (state is not null && WorkItems.TryGetValue(state.Trim(), out var info))
        {
            return info;
        }

        return new(string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim(), "?", "muted", "Unrecognised work-item state");
    }

    public static StatusInfo ForRelease(string? state)
    {
        if (state is not null && ReleaseStates.TryGetValue(state.Trim(), out var info))
        {
            return info;
        }

        return new(string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim(), "?", "muted", "Unrecognised release state");
    }

    public static StatusInfo ForSuggestion(string? state)
    {
        if (state is not null && SuggestionStates.TryGetValue(state.Trim(), out var info))
        {
            return info;
        }

        return new(string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim(), "?", "muted", "Unrecognised suggestion state");
    }

    public static StatusInfo ForSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "error" or "blocking" or "important" or "critical" => new(Cap(severity), "‼", "fail", "Blocking severity"),
        "warning" or "notable" or "high" => new(Cap(severity), "⚠", "review", "Notable severity"),
        "info" or "minor" or "low" => new(Cap(severity), "ⓘ", "queued", "Minor severity"),
        "none" => new("None", "✓", "done", "No findings"),
        _ => new(string.IsNullOrWhiteSpace(severity) ? "Unknown" : Cap(severity.Trim()), "?", "muted", "Unrecognised severity"),
    };

    public static StatusInfo ForAvailability(string? availability)
    {
        if (availability is not null && AgentAvailability.TryGetValue(availability.Trim(), out var info))
        {
            return info;
        }

        return AgentAvailability["Unknown"];
    }

    private static readonly Dictionary<string, StatusInfo> SessionStates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Running"] = new("Running", "▶", "active", "Session is running"),
            ["Active"] = new("Active", "▶", "active", "Session is active"),
            ["Idle"] = new("Idle", "·", "muted", "Session is idle"),
            ["Waiting"] = new("Waiting", "⏳", "wait", "Session is waiting"),
            ["Completed"] = new("Done", "✓", "done", "Session completed"),
            ["Done"] = new("Done", "✓", "done", "Session completed"),
            ["Failed"] = new("Failed", "✗", "fail", "Session failed"),
            ["Error"] = new("Error", "✗", "fail", "Session errored"),
            ["Open"] = new("Open", "○", "queued", "Accepting injections"),
            ["Closed"] = new("Closed", "⊘", "muted", "Not accepting injections"),
        };

    /// <summary>Live supervision session state (free-form backend string with a safe fallback).</summary>
    public static StatusInfo ForSessionState(string? state)
    {
        if (state is not null && SessionStates.TryGetValue(state.Trim(), out var info))
        {
            return info;
        }

        return new(string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim(), "?", "muted", "Unrecognised session state");
    }

    private static string Cap(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
}
