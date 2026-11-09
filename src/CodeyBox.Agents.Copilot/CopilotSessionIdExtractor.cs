namespace CodeyBox.Agents.Copilot;

/// <summary>
/// Pulls the Copilot CLI's resumable native session id from captured stdout:
/// the trailing <c>result</c> event's <c>sessionId</c> in
/// <c>--output-format json</c> mode, or the trailing
/// <c>Resume copilot --resume=&lt;uuid&gt;</c> footer line in plaintext mode.
/// Both positions are CLI-emitted and end-anchored (see
/// <see cref="CopilotTurnOutcome"/>), so model-printed fake resume hints
/// cannot match. Returns null when no trustworthy id is present.
/// </summary>
internal static class CopilotSessionIdExtractor
{
    public static string? Extract(string? stdout)
        => CopilotTurnOutcome.Extract(stdout).SessionId;
}
