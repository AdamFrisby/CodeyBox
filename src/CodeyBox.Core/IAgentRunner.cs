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
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false);
}

/// <summary>
/// Optional runner capability for CLIs that can emit structured stdout
/// streams suitable for persistent capture.
/// </summary>
public interface IStructuredStreamAgentRunner : IAgentRunner
{
    Task<bool> SupportsStructuredStreamAsync(ISandbox sandbox, CancellationToken ct = default);
}

/// <summary>
/// Optional runner capability for merge conflict resolution. Unlike
/// <see cref="IAgentRunner.RunAsync"/>, this path receives only conflict file
/// text and returns file contents; it is not given a sandbox, shell, repository
/// mount, or network profile.
/// </summary>
public interface IConflictResolverAgentRunner : IAgentRunner
{
    Task<ConflictResolverResult> ResolveConflictsAsync(
        string prompt,
        IReadOnlyList<ConflictResolverFile> files,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default);
}

public sealed record ConflictResolverFile(string Path, string Content);

public sealed record ConflictResolverResult(
    bool Success,
    string Summary,
    IReadOnlyDictionary<string, string> ResolvedFiles,
    string? Stdout,
    string? Stderr);

/// <summary>
/// Optional runner capability for CLIs where CodeyBox pins a default model
/// even when the work item does not carry an explicit ModelId.
/// </summary>
public interface IAgentDefaultModelProvider
{
    string? DefaultModelId { get; }
}

/// <summary>
/// Optional capability for runners that can receive a graceful preempt signal
/// before the orchestrator cancels the sandbox exec process during host shutdown.
/// Implementations must not trust repository contents while collecting state.
/// </summary>
public interface IPreemptibleAgentRunner : IAgentRunner
{
    Task RequestPreemptAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct = default);
}

/// <summary>
/// Optional capability for runners that can restore CLI session state captured
/// during graceful preemption and invoke the agent in that resumed context.
/// Implementations may still fall back to a normal one-shot run after restoring
/// the scratchpad archive when the underlying CLI has no true resume mode.
/// </summary>
public interface IResumableAgentRunner : IAgentRunner
{
    Task<AgentResult> RunResumedAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        AgentResumeContext resume,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null);
}

public sealed record AgentResumeContext(
    string CheckpointRef,
    string ScratchpadArchivePath = ".codeybox/preempt-scratchpad.tgz");

public sealed record AgentResult(bool Success, string Summary, string? Stdout, string? Stderr);

/// <summary>Maps agent kinds to runners. Loose coupling: register new runners without recompiling consumers.</summary>
public interface IAgentRegistry
{
    bool TryGet(AgentKind kind, out IAgentRunner runner);
    IReadOnlyCollection<AgentKind> Available { get; }
}
