namespace CodeyBox.Agents.Crock;

/// <summary>
/// Terminal/non-terminal classification of a <c>crock status &lt;task-id&gt;</c>
/// observation. The crock CLI runs work asynchronously via Anthropic's Message
/// Batches API; the orchestrator's <see cref="CrockAgentRunner"/> submits the
/// task, then polls status until the state lands in one of the terminal kinds.
/// </summary>
public enum CrockTaskStateKind
{
    /// <summary>State could not be parsed from the CLI output — treated as
    /// in-progress for liveness, but counted toward the unknown-streak limit so
    /// the poll loop does not hang on a permanently mute CLI.</summary>
    Unknown,

    /// <summary>Task is queued, submitted, running, or otherwise still
    /// progressing toward a terminal state.</summary>
    InProgress,

    /// <summary>Task finished and produced an artifact / committed work.</summary>
    Succeeded,

    /// <summary>Task finished with an error, was cancelled, or otherwise will
    /// not produce a usable artifact.</summary>
    Failed,
}

/// <summary>
/// Parsed view of one <c>crock status &lt;task-id&gt;</c> observation. The
/// <see cref="StateKind"/> drives whether the poll loop continues; the raw
/// <see cref="StateToken"/> is preserved for diagnostics so a future contract
/// drift surfaces in operator logs without changing the loop's behaviour.
/// </summary>
/// <param name="StateKind">Terminal/non-terminal classification.</param>
/// <param name="StateToken">The literal state word the parser matched, or
/// <c>null</c> when none was found.</param>
/// <param name="Summary">Optional one-line summary suitable for the agent
/// stream — e.g. "state=succeeded" or "no state detected".</param>
public sealed record CrockTaskStatus(
    CrockTaskStateKind StateKind,
    string? StateToken,
    string Summary);
