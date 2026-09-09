namespace CodeyBox.Core;

/// <summary>
/// Signals that an already-checkpointed agent turn could not reach its resumed
/// CLI dispatch because restoring its private state or prerequisites lost the
/// sandbox execution transport. The durable dispatch claim may be released
/// without consuming an attempt because no agent process was started.
/// </summary>
public sealed class AgentResumePreparationUnavailableException : Exception
{
    public AgentResumePreparationUnavailableException(int? exitCode, Exception? innerException = null)
        : base(
            exitCode is { } code
                ? $"Agent resume preparation was unavailable (sandbox exit {code})."
                : "Agent resume preparation was unavailable.",
            innerException)
    {
        ExitCode = exitCode;
    }

    public int? ExitCode { get; }
}
