using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Executes one dispatched phase against the bare repo at
/// <paramref name="repoPath"/> and returns its result. The in-process runner
/// calls this against the orchestrator's own repo path; a remote executor
/// runs the same logic against its staged copy, which is what makes a remote
/// dispatch return an outcome equivalent to running it in process.
/// Executor-side wiring of this seam (through the executor host process) is a
/// follow-up; the proxy and its tests run it directly through the transport
/// fake's staged copy.
/// </summary>
public interface IExecutorPhaseHandler
{
    Task<ExecutorPhaseResult> ExecuteAsync(
        ExecutorPhaseRequest request,
        string repoPath,
        CancellationToken ct);
}

/// <summary>
/// Phase-execution interface for one work-item phase. Implemented by
/// <see cref="InProcessExecutorPhaseRunner"/> (runs the phase against the
/// orchestrator's own bare repo) and <see cref="ExecutorPhaseProxy"/>
/// (dispatches to a registered executor when one is available, otherwise
/// falls back to the in-process runner with unchanged behaviour).
/// </summary>
public interface IExecutorPhaseRunner
{
    Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct);
}

/// <summary>
/// In-process <see cref="IExecutorPhaseRunner"/>: resolves the phase's bare
/// repo through <see cref="IGitHost"/> and runs the injected
/// <see cref="IExecutorPhaseHandler"/> against it. Used directly when no
/// executor is registered and as the proxy's fallback.
/// </summary>
public sealed class InProcessExecutorPhaseRunner : IExecutorPhaseRunner
{
    private readonly IGitHost _gitHost;
    private readonly IExecutorPhaseHandler _handler;
    private readonly Func<ExecutorPhaseDispatchOptions>? _optionsAccessor;

    public InProcessExecutorPhaseRunner(
        IGitHost gitHost,
        IExecutorPhaseHandler handler,
        Func<ExecutorPhaseDispatchOptions>? optionsAccessor = null)
    {
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _optionsAccessor = optionsAccessor;
    }

    public async Task<ExecutorPhaseResult> ExecutePhaseAsync(ExecutorPhaseRequest request, CancellationToken ct)
    {
        ExecutorPhaseProxy.ValidateRequest(request, ResolvedOptions());
        var repoPath = ResolveRepoPath(request.RepositoryId);
        var result = await _handler.ExecuteAsync(request, repoPath, ct).ConfigureAwait(false);
        return ExecutorPhaseProxy.ValidateResult(result, ResolvedOptions());
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
