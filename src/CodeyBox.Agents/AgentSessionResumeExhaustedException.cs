using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// Raised when a runner used every configured CLI-native session-resume attempt
/// and the resumed agent process still failed.
/// </summary>
public sealed class AgentSessionResumeExhaustedException : Exception
{
    public AgentSessionResumeExhaustedException(
        AgentKind agent,
        int maxResumeAttempts,
        AgentResult lastResult)
        : base($"Agent {agent} exhausted {maxResumeAttempts} session resume attempt(s): {lastResult.Summary}")
    {
        Agent = agent;
        MaxResumeAttempts = maxResumeAttempts;
        LastResult = lastResult;
    }

    public AgentKind Agent { get; }

    public int MaxResumeAttempts { get; }

    public AgentResult LastResult { get; }
}
