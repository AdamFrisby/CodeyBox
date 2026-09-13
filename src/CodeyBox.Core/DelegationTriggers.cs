namespace CodeyBox.Core;

/// <summary>
/// Exact-match trigger labels identifying what asked for a delegation turn.
/// Used as the metric tag on delegation counts, as the attribution prefix in
/// <see cref="WorkItem.DelegationReason"/>, and in escalation webhook details —
/// never substring-compared.
/// </summary>
public static class DelegationTriggers
{
    /// <summary>Explicit operator command (<c>POST /workitems/{id}/delegate</c>).</summary>
    public const string Operator = "operator";

    /// <summary>Automatic escalation: audit iterations reached the configured maximum without passing.</summary>
    public const string AuditMaxIterations = "audit-max-iterations";

    /// <summary>Automatic escalation: the item terminally failed repeatedly after retry.</summary>
    public const string RepeatedTerminalFailure = "repeated-terminal-failure";

    /// <summary>Whether the trigger is an automatic escalation (as opposed to an operator command).</summary>
    public static bool IsAutomatic(string? trigger) =>
        string.Equals(trigger, AuditMaxIterations, StringComparison.Ordinal)
        || string.Equals(trigger, RepeatedTerminalFailure, StringComparison.Ordinal);
}
