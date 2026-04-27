using System.Collections.Concurrent;
using CodeyBox.Core;

namespace CodeyBox.Git;

/// <summary>
/// Default IPullRequestService backed by an in-memory map. Suitable for
/// single-process orchestrators; replace with a SQLite or external-forge
/// implementation for production. Pure metadata — does not perform any git
/// operations or talk to remotes.
/// </summary>
public sealed class InMemoryPullRequestService : IPullRequestService
{
    private readonly ConcurrentDictionary<string, PullRequest> _store = new();

    public Task<PullRequest> OpenAsync(OpenPullRequest request, CancellationToken ct = default)
    {
        var id = new PullRequestId(Guid.NewGuid().ToString("N"));
        var pr = new PullRequest(
            id,
            request.RepositoryId,
            request.SourceBranch,
            request.TargetBranch,
            request.Title,
            request.Description,
            PullRequestStatus.Open,
            MergeCommitSha: null);
        _store[id.Value] = pr;
        return Task.FromResult(pr);
    }

    public Task MarkMergedAsync(PullRequestId id, string mergeCommitSha, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            id.Value,
            _ => throw new KeyNotFoundException(id.Value),
            (_, existing) => existing with { Status = PullRequestStatus.Merged, MergeCommitSha = mergeCommitSha });
        return Task.CompletedTask;
    }

    public Task MarkClosedAsync(PullRequestId id, string? reason, CancellationToken ct = default)
    {
        _store.AddOrUpdate(
            id.Value,
            _ => throw new KeyNotFoundException(id.Value),
            (_, existing) => existing with { Status = PullRequestStatus.Closed });
        _ = reason;
        return Task.CompletedTask;
    }

    public Task<PullRequest?> GetAsync(PullRequestId id, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(id.Value, out var pr) ? pr : null);
}
