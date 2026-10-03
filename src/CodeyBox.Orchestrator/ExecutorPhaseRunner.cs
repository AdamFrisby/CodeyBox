using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Executes one dispatched phase against a staged bare repo inside an
/// already-provisioned sandbox. The in-process runner calls this against the
/// orchestrator's own repo path; a remote executor runs the same logic
/// against its staged copy, which is what makes a remote dispatch return an
/// outcome equivalent to running it in process. The caller owns the sandbox
/// lifecycle (provision, track, tear down); the handler owns the phase's
/// agent work: it clones or reads the staged repo at
/// <paramref name="repoPath"/>, works inside <paramref name="sandbox"/>, and
/// returns the phase result. The handler must not dispose
/// <paramref name="sandbox"/>.
/// </summary>
public interface IExecutorPhaseHandler
{
    Task<ExecutorPhaseResult> ExecuteAsync(
        ExecutorPhaseRequest request,
        string repoPath,
        ISandbox sandbox,
        CancellationToken ct);
}

/// <summary>
/// Phase-execution interface for one work-item phase. Implemented by
/// <see cref="InProcessExecutorPhaseRunner"/> (runs the phase against the
/// orchestrator's own bare repo), <see cref="ExecutorHostPhaseRunner"/>
/// (runs the phase on an executor host against its staged copy) and
/// <see cref="ExecutorPhaseProxy"/> (dispatches to a registered executor
/// when one is available, otherwise falls back to the in-process runner with
/// unchanged behaviour).
/// </summary>
public interface IExecutorPhaseRunner
{
    Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct);
}

/// <summary>
/// In-process <see cref="IExecutorPhaseRunner"/>: resolves the phase's bare
/// repo through <see cref="IGitHost"/>, provisions a sandbox through the
/// injected provider, and runs the injected
/// <see cref="IExecutorPhaseHandler"/> against both. Used directly when no
/// executor is registered and as the proxy's fallback.
/// </summary>
public sealed class InProcessExecutorPhaseRunner : IExecutorPhaseRunner
{
    private readonly IGitHost _gitHost;
    private readonly IExecutorPhaseHandler _handler;
    private readonly ISandboxProvider _sandboxes;
    private readonly Func<ExecutorPhaseDispatchOptions>? _optionsAccessor;
    private readonly Func<ExecutorPhaseRequest, SandboxSpec>? _specFactory;

    public InProcessExecutorPhaseRunner(
        IGitHost gitHost,
        IExecutorPhaseHandler handler,
        ISandboxProvider sandboxes,
        Func<ExecutorPhaseDispatchOptions>? optionsAccessor = null,
        Func<ExecutorPhaseRequest, SandboxSpec>? sandboxSpecFactory = null)
    {
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _optionsAccessor = optionsAccessor;
        _specFactory = sandboxSpecFactory;
    }

    public async Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
    {
        var options = ResolvedOptions();
        ExecutorPhaseProxy.ValidateRequest(request, options);
        var repoPath = ResolveRepoPath(request.RepositoryId);
        return await ExecutorPhaseExecution.RunInSandboxAsync(
            _handler,
            request,
            repoPath,
            _sandboxes,
            _specFactory ?? (static req => ExecutorPhaseExecution.DefaultSandboxSpec(req, imageReference: string.Empty)),
            options,
            tracker: null,
            trackerPhaseId: null,
            log: null,
            ct).ConfigureAwait(false);
    }

    private string ResolveRepoPath(string repositoryId)
    {
        string repoPath;
        try
        {
            repoPath = _gitHost.GetRepoPath(repositoryId);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(
                $"Git host exposes no local repository path for '{repositoryId}'; in-process phase execution needs a filesystem-backed repo.", ex);
        }

        return ExecutorPhaseProxy.CanonicalizeRepoPath(repoPath, _gitHost.RepositoriesRootDirectory, repositoryId);
    }

    private ExecutorPhaseDispatchOptions ResolvedOptions()
    {
        var options = _optionsAccessor?.Invoke() ?? new ExecutorPhaseDispatchOptions();
        options.Validate();
        return options;
    }
}
