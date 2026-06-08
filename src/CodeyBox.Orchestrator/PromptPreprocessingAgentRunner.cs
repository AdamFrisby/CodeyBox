using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class PromptPreprocessingAgentRunner : ITextOnlyAgentRunner, IAgentDefaultModelProvider
{
    private readonly IAgentRunner _inner;
    private readonly AgentPromptPreprocessorChain _chain;
    private readonly WorkItemId _itemId;
    private readonly AgentPromptPhase _phase;
    private readonly int _iteration;
    private readonly Project _project;

    public PromptPreprocessingAgentRunner(
        IAgentRunner inner,
        AgentPromptPreprocessorChain chain,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project)
    {
        _inner = inner;
        _chain = chain;
        _itemId = itemId;
        _phase = phase;
        _iteration = iteration;
        _project = project;
    }

    public AgentKind Kind => _inner.Kind;

    internal bool SupportsTextOnly => _inner is ITextOnlyAgentRunner;

    string? IAgentDefaultModelProvider.DefaultModelId =>
        (_inner as IAgentDefaultModelProvider)?.DefaultModelId;

    public AgentFailureClassification ClassifyFailure(AgentResult result) =>
        _inner.ClassifyFailure(result);

    public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) =>
        _inner is ITextOnlyAgentRunner textOnly
            ? textOnly.GetTextOnlyUnavailabilityReason(credential)
            : $"{_inner.Kind.Value} runner is not text-only capable";

    public async Task<AgentResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        Action<string>? stdoutChunkCallback = null,
        bool captureStructuredStream = false)
    {
        var processed = await _chain.ProcessAsync(
            new PromptContext(_itemId, _inner.Kind, _phase, _iteration, _project, sandbox),
            prompt,
            ct).ConfigureAwait(false);

        return await _inner.RunAsync(
            sandbox,
            workingDirectory,
            processed,
            credential,
            modelId,
            reasoningMode,
            ct,
            stdoutChunkCallback,
            captureStructuredStream).ConfigureAwait(false);
    }

    public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
        string prompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        if (_inner is not ITextOnlyAgentRunner textOnly)
            return new TextOnlyAgentResult(
                false,
                $"{_inner.Kind.Value} runner is not text-only capable",
                null,
                null);

        if (sandbox is not null)
        {
            prompt = await _chain.ProcessAsync(
                new PromptContext(_itemId, _inner.Kind, _phase, _iteration, _project, sandbox),
                prompt,
                ct).ConfigureAwait(false);
        }

        return await textOnly.RunTextOnlyAsync(
            prompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            sandbox,
            workingDirectory).ConfigureAwait(false);
    }
}
