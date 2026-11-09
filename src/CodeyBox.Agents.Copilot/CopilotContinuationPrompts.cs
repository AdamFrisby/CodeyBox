namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Continuation wording for Copilot same-session resumes after a turn ends
/// with no final assistant message and no recorded edits. Single source of
/// truth so the dispatch and any documentation of the nudge can never drift.
/// </summary>
internal static class CopilotContinuationPrompts
{
    /// <summary>
    /// Builds the "continue and finish" instruction sent via
    /// <c>copilot --resume &lt;session-id&gt;</c> after an empty finish. The
    /// restored session already carries the original task prompt and the
    /// turn's exploratory context, so this states only what was missing (a
    /// concluding message plus the task's file changes) rather than
    /// repeating the assignment.
    /// </summary>
    public static string BuildEmptyFinishContinuePrompt()
        => "Your previous turn ended without a final assistant message and without recording any edits. "
            + "Continue and finish the assigned task in this session: complete the remaining work, "
            + "then reply with a concise summary of what you changed.";
}
