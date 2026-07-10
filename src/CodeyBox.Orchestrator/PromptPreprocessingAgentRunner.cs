using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Wraps an inner <see cref="IAgentRunner"/> so the
/// <see cref="AgentPromptPreprocessorChain"/> runs against every prompt
/// immediately before the agent is invoked. Use <see cref="Wrap"/> to
/// construct one — the factory returns subclasses that mirror optional
/// runner capabilities, so <c>is</c> checks on the returned instance reflect
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
    protected readonly AuditTarget? AuditTarget;

    protected PromptPreprocessingAgentRunner(
        IAgentRunner inner,
        AgentPromptPreprocessorChain chain,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project,
        AuditTarget? auditTarget = null)
    {
        Inner = inner;
        Chain = chain;
        ItemId = itemId;
        Phase = phase;
        Iteration = iteration;
        Project = project;
        AuditTarget = auditTarget;
    }

    public static PromptPreprocessingAgentRunner Wrap(
        IAgentRunner inner,
        AgentPromptPreprocessorChain chain,
        WorkItemId itemId,
        AgentPromptPhase phase,
        int iteration,
        Project project,
        AuditTarget? auditTarget = null)
    {
        var textOnly = inner is ITextOnlyAgentRunner;
        var cliSessionResumable = inner is ICliSessionResumableAgentRunner;

        if (textOnly && cliSessionResumable)
            return new TextOnlyCliSessionResumablePromptPreprocessingAgentRunner(inner, chain, itemId, phase, iteration, project, auditTarget);
        if (textOnly)
            return new TextOnlyPromptPreprocessingAgentRunner(inner, chain, itemId, phase, iteration, project, auditTarget);
        if (cliSessionResumable)
            return new CliSessionResumablePromptPreprocessingAgentRunner(inner, chain, itemId, phase, iteration, project, auditTarget);

        return new PromptPreprocessingAgentRunner(inner, chain, itemId, phase, iteration, project, auditTarget);
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
            new PromptContext(ItemId, Inner.Kind, Phase, Iteration, Project, sandbox, workingDirectory, AuditTarget),
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

    protected string? GetInnerTextOnlyUnavailabilityReason(AgentCredential? credential) =>
        ((ITextOnlyAgentRunner)Inner).GetTextOnlyUnavailabilityReason(credential);

    protected async Task<TextOnlyAgentResult> RunTextOnlyInnerAsync(
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
            // RunTextOnlyAsync's workingDirectory is nullable on the interface;
            // preprocessors that read repo files need a real path, so fall back
            // to the sandbox convention when callers pass null. Real text-only
            // callers in the orchestrator (e.g. RunMergeSecurityReviewAsync)
            // already pass /work.
            var resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? SandboxConventions.WorkDir
                : workingDirectory;
            prompt = await Chain.ProcessAsync(
                new PromptContext(ItemId, Inner.Kind, Phase, Iteration, Project, sandbox, resolvedWorkingDirectory, AuditTarget),
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

    protected async Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptInnerAsync(
        string systemPrompt,
        string userPrompt,
        AgentCredential? credential,
        string? modelId = null,
        string? reasoningMode = null,
        CancellationToken ct = default,
        ISandbox? sandbox = null,
        string? workingDirectory = null)
    {
        if (sandbox is not null)
        {
            var resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? SandboxConventions.WorkDir
                : workingDirectory;
            userPrompt = await Chain.ProcessAsync(
                new PromptContext(ItemId, Inner.Kind, Phase, Iteration, Project, sandbox, resolvedWorkingDirectory, AuditTarget),
                userPrompt,
                ct).ConfigureAwait(false);
        }

        return await ((ITextOnlyAgentRunner)Inner).RunTextOnlyWithSystemPromptAsync(
            systemPrompt,
            userPrompt,
            credential,
            modelId,
            reasoningMode,
            ct,
            sandbox,
            workingDirectory).ConfigureAwait(false);
    }

    private class CliSessionResumablePromptPreprocessingAgentRunner
        : PromptPreprocessingAgentRunner, ICliSessionResumableAgentRunner
    {
        public CliSessionResumablePromptPreprocessingAgentRunner(
            IAgentRunner inner,
            AgentPromptPreprocessorChain chain,
            WorkItemId itemId,
            AgentPromptPhase phase,
            int iteration,
            Project project,
            AuditTarget? auditTarget = null)
            : base(inner, chain, itemId, phase, iteration, project, auditTarget)
        {
        }

        private ICliSessionResumableAgentRunner InnerCliSessionResumable =>
            (ICliSessionResumableAgentRunner)Inner;

        public bool RequiresStructuredStreamForSessionId =>
            InnerCliSessionResumable.RequiresStructuredStreamForSessionId;

        public IQuotaFailureClassifier SessionResumeQuotaClassifier =>
            InnerCliSessionResumable.SessionResumeQuotaClassifier;

        public string? TryExtractSessionId(string? stdout) =>
            InnerCliSessionResumable.TryExtractSessionId(stdout);
    }

    private class TextOnlyPromptPreprocessingAgentRunner
        : PromptPreprocessingAgentRunner, ITextOnlyAgentRunner
    {
        public TextOnlyPromptPreprocessingAgentRunner(
            IAgentRunner inner,
            AgentPromptPreprocessorChain chain,
            WorkItemId itemId,
            AgentPromptPhase phase,
            int iteration,
            Project project,
            AuditTarget? auditTarget = null)
            : base(inner, chain, itemId, phase, iteration, project, auditTarget)
        {
        }

        public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) =>
            GetInnerTextOnlyUnavailabilityReason(credential);

        public bool TextOnlyRequiresSandbox =>
            ((ITextOnlyAgentRunner)Inner).TextOnlyRequiresSandbox;

        public bool SupportsSeparateSystemPrompt =>
            ((ITextOnlyAgentRunner)Inner).SupportsSeparateSystemPrompt;

        public Task<TextOnlyAgentResult> RunTextOnlyAsync(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
            => RunTextOnlyInnerAsync(prompt, credential, modelId, reasoningMode, ct, sandbox, workingDirectory);

        public Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptAsync(
            string systemPrompt,
            string userPrompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
            => RunTextOnlyWithSystemPromptInnerAsync(
                systemPrompt,
                userPrompt,
                credential,
                modelId,
                reasoningMode,
                ct,
                sandbox,
                workingDirectory);
    }

    private sealed class TextOnlyCliSessionResumablePromptPreprocessingAgentRunner
        : CliSessionResumablePromptPreprocessingAgentRunner, ITextOnlyAgentRunner
    {
        public TextOnlyCliSessionResumablePromptPreprocessingAgentRunner(
            IAgentRunner inner,
            AgentPromptPreprocessorChain chain,
            WorkItemId itemId,
            AgentPromptPhase phase,
            int iteration,
            Project project,
            AuditTarget? auditTarget = null)
            : base(inner, chain, itemId, phase, iteration, project, auditTarget)
        {
        }

        public string? GetTextOnlyUnavailabilityReason(AgentCredential? credential) =>
            GetInnerTextOnlyUnavailabilityReason(credential);

        public bool TextOnlyRequiresSandbox =>
            ((ITextOnlyAgentRunner)Inner).TextOnlyRequiresSandbox;

        public bool SupportsSeparateSystemPrompt =>
            ((ITextOnlyAgentRunner)Inner).SupportsSeparateSystemPrompt;

        public Task<TextOnlyAgentResult> RunTextOnlyAsync(
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
            => RunTextOnlyInnerAsync(prompt, credential, modelId, reasoningMode, ct, sandbox, workingDirectory);

        public Task<TextOnlyAgentResult> RunTextOnlyWithSystemPromptAsync(
            string systemPrompt,
            string userPrompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            ISandbox? sandbox = null,
            string? workingDirectory = null)
            => RunTextOnlyWithSystemPromptInnerAsync(
                systemPrompt,
                userPrompt,
                credential,
                modelId,
                reasoningMode,
                ct,
                sandbox,
                workingDirectory);
    }
}
