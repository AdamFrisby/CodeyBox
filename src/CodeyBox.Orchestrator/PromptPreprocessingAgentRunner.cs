using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Wraps an inner <see cref="IAgentRunner"/> so the
/// <see cref="AgentPromptPreprocessorChain"/> runs against every prompt
/// immediately before the agent is invoked. Use <see cref="Wrap"/> to
/// construct one — the factory returns a text-only-capable subclass when
/// the inner runner implements <see cref="ITextOnlyAgentRunner"/>, so an
/// <c>is ITextOnlyAgentRunner</c> check on the returned instance reflects
/// the inner runner's true capability instead of the wrapper's claim.
/// </summary>
internal class PromptPreprocessingAgentRunner : IAgentRunner, IAgentDefaultModelProvider
{
    protected readonly IAgentRunner Inner;
    protected readonly AgentPromptPreprocessorChain Chain;
    protected readonly WorkItemId ItemId;
    protected readonly AgentPromptPhase Phase;
    protected readonly int Iteration;
    protected readonly Project Project;

    protected PromptPreprocessingAgentRunner(
        IAgentRunner inner,
        AgentPromptPreprocessorChain chain,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project)
    {
        Inner = inner;
        Chain = chain;
        ItemId = itemId;
        Phase = phase;
        Iteration = iteration;
        Project = project;
    }

    public static PromptPreprocessingAgentRunner Wrap(
        IAgentRunner inner,
        AgentPromptPreprocessorChain chain,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project)
    {
        if (inner is ITextOnlyAgentRunner)
            return new TextOnlyPromptPreprocessingAgentRunner(inner, chain, itemId, phase, iteration, project);

        return new PromptPreprocessingAgentRunner(inner, chain, itemId, phase, iteration, project);
    }

    public AgentKind Kind => Inner.Kind;

    string? IAgentDefaultModelProvider.DefaultModelId =>
        (Inner as IAgentDefaultModelProvider)?.DefaultModelId;

    public AgentFailureClassification ClassifyFailure(AgentResult result) =>
        Inner.ClassifyFailure(result);

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
        var processed = await Chain.ProcessAsync(
            new PromptContext(ItemId, Inner.Kind, Phase, Iteration, Project, sandbox),
            prompt,
            ct).ConfigureAwait(false);

        return await Inner.RunAsync(
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

    private sealed class TextOnlyPromptPreprocessingAgentRunner
        : PromptPreprocessingAgentRunner, ITextOnlyAgentRunner
    {
        public TextOnlyPromptPreprocessingAgentRunner(
            IAgentRunner inner,
            AgentPromptPreprocessorChain chain,
            WorkItemId itemId,
            AgentPromptPhase phase,
            int iteration,
            Project project)
            : base(inner, chain, itemId, phase, iteration, project)
        {
        }

        public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) =>
            ((ITextOnlyAgentRunner)Inner).GetTextOnlyUnavailabilityReason(credential);

        public async Task<TextOnlyAgentResult> RunTextOnlyAsync(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
        {
            if (sandbox is not null)
            {
                prompt = await Chain.ProcessAsync(
                    new PromptContext(ItemId, Inner.Kind, Phase, Iteration, Project, sandbox),
                    prompt,
                    ct).ConfigureAwait(false);
            }

            return await ((ITextOnlyAgentRunner)Inner).RunTextOnlyAsync(
                prompt,
                credential,
                modelId,
                reasoningMode,
                ct,
                sandbox,
                workingDirectory).ConfigureAwait(false);
        }
    }
}
