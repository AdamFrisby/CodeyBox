namespace CodeyBox.Core;

/// <summary>
/// Drives a coding agent inside a prepared sandbox. The orchestrator selects
/// the runner whose <see cref="Kind"/> matches the work item.
/// </summary>
public interface IAgentRunner
{
    AgentKind Kind { get; }

    /// <summary>
    /// Runs the agent against the given prompt inside the sandbox's working
    /// directory. The agent is expected to leave changes staged or committed
    /// in the working tree at <paramref name="workingDirectory"/> on success.
    /// </summary>
    Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        CancellationToken ct = default);
}

public sealed record AgentResult(bool Success, string Summary, string? Stdout, string? Stderr);

/// <summary>Maps agent kinds to runners. Loose coupling: register new runners without recompiling consumers.</summary>
public interface IAgentRegistry
{
    bool TryGet(AgentKind kind, out IAgentRunner runner);
    IReadOnlyCollection<AgentKind> Available { get; }
}
