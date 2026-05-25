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

    /// <summary>
    /// Classifies a failed <see cref="AgentResult"/> into a structured
    /// <see cref="AgentFailureKind"/> so the pipeline can decide whether to
    /// retry the iteration against the next-best class member (quota), surface
    /// a transient retry hint (network), or fail the work item (normal /
    /// auth). The default implementation runs the shared
    /// <see cref="AgentFailureClassifier"/> heuristics; runners with
    /// CLI-specific failure shapes can override.
    /// </summary>
    AgentFailureClassification ClassifyFailure(AgentResult result)
    {
        if (result.Success)
            return new AgentFailureClassification(AgentFailureKind.Normal);
        return AgentFailureClassifier.Classify(result.Stderr, result.Stdout);
    }
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
/// Optional runner capability for prompts that must be handled as pure
/// text-in/text-out model calls. Implementations must not expose a shell,
/// filesystem, repository checkout, agent tool runtime, or model-controlled
/// network to the prompt; the only output channel is returned text.
/// Credentials are transport authentication for the provider call only and
/// must never be included in the prompt, model-visible context, or returned
/// output.
/// </summary>
public interface ITextOnlyAgentRunner : IAgentRunner
{
    Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default);

    /// <summary>
    /// Cheap, credential-only viability probe. Returns <c>null</c> when the
    /// supplied credential is sufficient for <see cref="RunTextOnlyAsync"/> to
    /// reach the provider; otherwise returns a short human-readable reason
    /// (e.g. <c>"GEMINI_API_KEY is required"</c>). The default implementation
    /// is permissive — runners that need specific env vars override this so
    /// the rebase-resolver router can walk the class chain past a runner with
    /// no viable text-only credential rather than hard-failing the work item.
    /// </summary>
    string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) => null;
}

public sealed record TextOnlyAgentResult(bool Success, string Summary, string? Output, string? Error);

public sealed record ConflictResolverFile(string Path, string Content);

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
