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
    /// a transient retry hint (network), identify sandbox/provisioning defects
    /// (infrastructure), or fail the work item (normal / auth). The default
    /// implementation runs the shared
    /// <see cref="AgentFailureClassifier"/> heuristics; runners with
    /// CLI-specific failure shapes can override.
    /// </summary>
    AgentFailureClassification ClassifyFailure(AgentResult result)
    {
        if (AgentFailureClassifier.DetectAuthRequired(Kind, result.Stderr, result.Stdout) is { } authRequired)
            return authRequired.Classification;
        if (result.Success)
            return new AgentFailureClassification(AgentFailureKind.Normal);
        return AgentFailureClassifier.Classify(Kind, result.Stderr, result.Stdout, result.Summary);
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
    /// <summary>
    /// Runs a text-only model call. Host HTTP runners ignore
    /// <paramref name="sandbox"/> and <paramref name="workingDirectory"/>.
    /// Subscription CLIs that must execute inside the work-item sandbox supply
    /// both parameters; host-side invocation without them returns failure.
    /// </summary>
    Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null);

    /// <summary>
    /// Cheap, credential-only viability probe. Returns <c>null</c> when the
    /// supplied credential is sufficient for this runner's text-only path to
    /// reach the provider; otherwise returns a short human-readable reason
    /// (e.g. <c>"GEMINI_API_KEY is required"</c>). Subscription CLIs that can
    /// use image-baked auth return <c>null</c> when <paramref name="credential"/>
    /// is <c>null</c>. The default implementation is permissive — runners that
    /// need specific env vars override this so the rebase-resolver router can
    /// walk the class chain past a runner with no viable text-only credential
    /// rather than hard-failing the work item.
    /// </summary>
    string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) => null;

    /// <summary>
    /// <c>true</c> when this runner's text-only path can only execute inside a
    /// work-item sandbox (i.e. <see cref="RunTextOnlyAsync"/> fails when invoked
    /// with a <c>null</c> sandbox). Subscription CLIs such as Cursor and Opencode
    /// shell out inside the VM and set this; host-side HTTP runners (API-key
    /// Claude, Gemini, Codex) leave it <c>false</c>.
    ///
    /// <para>The default is <c>false</c>. Callers that have no sandbox to offer
    /// consult this to surface an explicit infrastructure failure rather than
    /// issue a call that is guaranteed to fail.
    /// This is distinct from <see cref="GetTextOnlyUnavailabilityReason"/>, which
    /// is a credential-only probe and returns <c>null</c> for these CLIs whenever
    /// the auth bundle is present.</para>
    /// </summary>
    bool TextOnlyRequiresSandbox => false;
}

public sealed record TextOnlyAgentResult(bool Success, string Summary, string? Output, string? Error);

public sealed record ConflictResolverFile(string Path, string Content);

/// <summary>
/// Optional runner capability for CLIs whose planning-phase stdout is a
/// structured envelope (e.g. NDJSON stream-json) rather than the raw plan
/// text. Implementations return the agent-visible plan text — typically the
/// concatenated assistant turn — so the orchestrator's plan-artifact parser
/// can normalise it without having to know any provider-specific envelope
/// shape. Returns <c>null</c> when no envelope was detected (the raw stdout
/// is fed straight to the parser).
///
/// <para>This is the runner-side seam that keeps the orchestrator agent-
/// agnostic: a non-Claude runner that needs similar treatment implements this
/// interface, instead of growing an <c>AgentKind</c> switch in core pipeline
/// code.</para>
/// </summary>
public interface IPlanArtifactExtractor
{
    string? ExtractPlanArtifactText(string rawStdout);
}

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

/// <summary>
/// Optional capability for runners that drive a CLI with a native in-process
/// session resume mode (for example <c>claude --resume &lt;id&gt;</c>).
/// Distinct from <see cref="IResumableAgentRunner"/>, which restores
/// checkpointed scratchpad state after host preemption.
///
/// <para>
/// Implementations must declare whether their session-id extractor depends on
/// id-bearing structured output, provide the extractor, and expose the quota
/// gate classifier that blocks hard quota/rate failures. The concrete CLI
/// invocation remains an agent-layer concern so Core consumers do not depend on
/// argv/environment/stdin process-launch details.
/// </para>
/// </summary>
public interface ICliSessionResumableAgentRunner : IAgentRunner
{
    /// <summary>
    /// True when <see cref="TryExtractSessionId"/> is trustworthy only when
    /// <see cref="IAgentRunner.RunAsync"/> was invoked with
    /// <c>captureStructuredStream: true</c>.
    /// </summary>
    bool RequiresStructuredStreamForSessionId { get; }

    /// <summary>
    /// Classifier used to decide whether a failed run is safe to resume. Must be
    /// non-null; missing quota classification must fail closed rather than
    /// resume-hammering hard quota/rate failures.
    /// </summary>
    IQuotaFailureClassifier SessionResumeQuotaClassifier { get; }

    /// <summary>
    /// Extracts the native CLI session id from the captured stdout for a failed
    /// run. Returns null when no usable id was emitted.
    /// </summary>
    string? TryExtractSessionId(string? stdout);
}

public sealed record AgentResult(bool Success, string Summary, string? Stdout, string? Stderr)
{
    /// <summary>
    /// The runner-extracted TERMINAL error region (e.g. a CLI's final
    /// <c>RESOURCE_EXHAUSTED</c> / quota line) when the agent surfaces its cause
    /// in a side-channel that its process exit code and stderr do not reflect.
    ///
    /// <para>Some CLIs (notably <c>agy</c>) exit <b>0</b> and make no file changes
    /// when they give up on a consumer-tier quota block, writing the 429 only to
    /// an internal log. Such a run reaches the pipeline as a "success" with an
    /// empty diff and would otherwise terminal-fail as "produced no changes",
    /// losing legitimate work that a short quota reset would have recovered. The
    /// runner lifts that terminal region into this field — separate from
    /// <see cref="Stderr"/> so the success-path auth classifier is unaffected —
    /// so the no-changes branch can classify it and park the item in
    /// <c>WaitingForQuotaReset</c> instead of dead-lettering it.</para>
    /// </summary>
    public string? TerminalDiagnostic { get; init; }
}

/// <summary>Maps agent kinds to runners. Loose coupling: register new runners without recompiling consumers.</summary>
public interface IAgentRegistry
{
    bool TryGet(AgentKind kind, out IAgentRunner runner);
    IReadOnlyCollection<AgentKind> Available { get; }
}
