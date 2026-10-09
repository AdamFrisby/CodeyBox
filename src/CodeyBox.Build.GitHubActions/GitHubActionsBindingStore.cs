using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Adapter-owned durable correlation truth for one framework build: the
/// expected checkout identity pinned at dispatch (head sha, optional
/// merge-result sha), the approved workflow identity, and the observed
/// provider run/attempt. Persisted beside — never inside — the
/// versioned framework record, so pre-existing framework state still loads.
/// </summary>
public sealed record GitHubActionsBinding(
    string BuildId,
    string RequestId,
    string TargetId,
    string ApprovedTargetName,
    string Owner,
    string Repository,
    string WorkflowPath,
    string CandidateRef,
    string ExpectedHeadSha,
    string? ExpectedMergeSha,
    string SourceDigest,
    string? RunId,
    int Attempt,
    GitHubActionsRunOutcome? TerminalOutcome,
    ExternalBuildEvidence? TerminalEvidence,
    DateTimeOffset UpdatedAt);

public interface IGitHubActionsBindingStore
{
    Task SaveAsync(GitHubActionsBinding binding, CancellationToken ct = default);
    Task<GitHubActionsBinding?> GetByBuildAsync(string buildId, CancellationToken ct = default);
    Task<GitHubActionsBinding?> GetByRequestAsync(string requestId, CancellationToken ct = default);
    Task<GitHubActionsBinding?> GetByRunAsync(string runId, CancellationToken ct = default);
}

/// <summary>
/// In-memory binding store with a hard entry cap and
/// oldest-updated-first eviction, so a misconfigured caller cannot grow
/// memory without bound. Lifetime is process-local only: bindings survive
/// provider re-creation in-process (registered singleton) but are wiped by a
/// process restart, so restart safety (no duplicate run after restart)
/// requires a durable backing store, which this implementation does not provide.
/// </summary>
public sealed class InMemoryGitHubActionsBindingStore : IGitHubActionsBindingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GitHubActionsBinding> _byBuild = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _buildByRequest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _buildByRun = new(StringComparer.Ordinal);
    private int _maxEntries = 10_000;

    public int MaxEntries
    {
        get { lock (_gate) return _maxEntries; }
        set
        {
            if (value < 100) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) _maxEntries = value;
        }
    }

    public Task SaveAsync(GitHubActionsBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (_byBuild.TryGetValue(binding.BuildId, out var prior))
            {
                if (!string.Equals(prior.RequestId, binding.RequestId, StringComparison.Ordinal))
                    _buildByRequest.Remove(prior.RequestId);
                if (prior.RunId is not null && !string.Equals(prior.RunId, binding.RunId, StringComparison.Ordinal))
                    _buildByRun.Remove(prior.RunId);
            }
            _byBuild[binding.BuildId] = binding;
            _buildByRequest[binding.RequestId] = binding.BuildId;
            if (binding.RunId is not null)
                _buildByRun[binding.RunId] = binding.BuildId;
            while (_byBuild.Count > _maxEntries)
            {
                var oldest = _byBuild.Values.OrderBy(static b => b.UpdatedAt).First();
                _byBuild.Remove(oldest.BuildId);
                _buildByRequest.Remove(oldest.RequestId);
                if (oldest.RunId is not null)
                    _buildByRun.Remove(oldest.RunId);
            }
        }
        return Task.CompletedTask;
    }

    public Task<GitHubActionsBinding?> GetByBuildAsync(string buildId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_byBuild.TryGetValue(buildId, out var b) ? b : null);
    }

    public Task<GitHubActionsBinding?> GetByRequestAsync(string requestId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(
            _buildByRequest.TryGetValue(requestId, out var buildId)
            && _byBuild.TryGetValue(buildId, out var b) ? b : null);
    }

    public Task<GitHubActionsBinding?> GetByRunAsync(string runId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(
            _buildByRun.TryGetValue(runId, out var buildId)
            && _byBuild.TryGetValue(buildId, out var b) ? b : null);
    }
}
