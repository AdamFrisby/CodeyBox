namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// Shared, process-wide cache of conditional-request state for GitHub REST
/// reads: ETags for list/branch endpoints and per-pull-request detail
/// snapshots keyed by the head/base revisions they were fetched at.
///
/// <para><see cref="GitHubUpstreamRemote"/> instances are created per call by
/// <c>IUpstreamRemoteFactory</c>, so this state must live outside them to
/// survive across sweeps. It lets the stale-PR sweep answer "did anything
/// change?" with 304s (which do not count against the rate limit) and, for
/// detail reads, with no request at all when the head sha and base branch tip
/// are unchanged — steady-state cost stays O(1) per sweep instead of growing
/// per open PR.</para>
///
/// <para>All access is locked; entries are bounded per repository and evicted
/// oldest-first. Cached bodies are never trusted across revision changes: a
/// pull-request entry is only reused when both the head sha and the resolved
/// base tip still match, and list/branch entries are only reused on a 304
/// revalidation.</para>
/// </summary>
public sealed class GitHubUpstreamReadCache
{
    private const int MaxRepositories = 64;
    private const int MaxPagesPerRepository = 32;
    private const int MaxPullRequestsPerRepository = 4096;
    private const int MaxCachedBodyChars = 1024 * 1024;

    /// <summary>A cached GET response body plus the ETag that revalidates it.</summary>
    public sealed record CachedResponse(string? ETag, string Body);

    /// <summary>
    /// A cached pull-request detail body. <see cref="HeadSha"/> and
    /// <see cref="BaseSha"/> are the revisions the body was fetched against —
    /// callers reuse the body only while both still match the live summary.
    /// </summary>
    public sealed record CachedPullRequest(string HeadSha, string BaseSha, string? ETag, string Body);

    private sealed class Slot<T>
    {
        public required T Value { get; init; }
        public DateTimeOffset LastUsedUtc { get; set; }
    }

    private sealed class RepositoryReads
    {
        public readonly Dictionary<string, Slot<CachedResponse>> Pages = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Slot<CachedResponse>> BranchTips = new(StringComparer.Ordinal);
        public readonly Dictionary<int, Slot<CachedPullRequest>> PullRequests = new();
        public DateTimeOffset LastUsedUtc { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, RepositoryReads> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public GitHubUpstreamReadCache(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public CachedResponse? GetPage(string repositoryKey, string url)
    {
        lock (_gate)
        {
            if (!_repos.TryGetValue(repositoryKey, out var repo))
                return null;
            if (!repo.Pages.TryGetValue(url, out var slot))
                return null;
            Touch(repo, slot);
            return slot.Value;
        }
    }

    public void SetPage(string repositoryKey, string url, CachedResponse response)
    {
        if (response.Body.Length > MaxCachedBodyChars)
            return;
        lock (_gate)
        {
            var repo = GetOrCreateRepo(repositoryKey);
            repo.Pages[url] = new Slot<CachedResponse> { Value = response, LastUsedUtc = Now() };
            EvictOldest(repo.Pages, MaxPagesPerRepository);
        }
    }

    public CachedResponse? GetBranchTip(string repositoryKey, string branch)
    {
        lock (_gate)
        {
            if (!_repos.TryGetValue(repositoryKey, out var repo))
                return null;
            if (!repo.BranchTips.TryGetValue(branch, out var slot))
                return null;
            Touch(repo, slot);
            return slot.Value;
        }
    }

    public void SetBranchTip(string repositoryKey, string branch, CachedResponse response)
    {
        if (response.Body.Length > MaxCachedBodyChars)
            return;
        lock (_gate)
        {
            var repo = GetOrCreateRepo(repositoryKey);
            repo.BranchTips[branch] = new Slot<CachedResponse> { Value = response, LastUsedUtc = Now() };
            EvictOldest(repo.BranchTips, MaxPagesPerRepository);
        }
    }

    public CachedPullRequest? GetPullRequest(string repositoryKey, int number)
    {
        lock (_gate)
        {
            if (!_repos.TryGetValue(repositoryKey, out var repo))
                return null;
            if (!repo.PullRequests.TryGetValue(number, out var slot))
                return null;
            Touch(repo, slot);
            return slot.Value;
        }
    }

    public void SetPullRequest(string repositoryKey, int number, CachedPullRequest entry)
    {
        if (entry.Body.Length > MaxCachedBodyChars)
            return;
        lock (_gate)
        {
            var repo = GetOrCreateRepo(repositoryKey);
            repo.PullRequests[number] = new Slot<CachedPullRequest> { Value = entry, LastUsedUtc = Now() };
            EvictOldest(repo.PullRequests, MaxPullRequestsPerRepository);
        }
    }

    private DateTimeOffset Now() => _time.GetUtcNow();

    private void Touch(RepositoryReads repo, Slot<CachedResponse> slot)
    {
        slot.LastUsedUtc = Now();
        repo.LastUsedUtc = slot.LastUsedUtc;
    }

    private void Touch(RepositoryReads repo, Slot<CachedPullRequest> slot)
    {
        slot.LastUsedUtc = Now();
        repo.LastUsedUtc = slot.LastUsedUtc;
    }

    private RepositoryReads GetOrCreateRepo(string repositoryKey)
    {
        if (_repos.TryGetValue(repositoryKey, out var existing))
            return existing;
        if (_repos.Count >= MaxRepositories)
        {
            var oldestKey = _repos.MinBy(kv => kv.Value.LastUsedUtc).Key;
            _repos.Remove(oldestKey);
        }
        var repo = new RepositoryReads { LastUsedUtc = Now() };
        _repos[repositoryKey] = repo;
        return repo;
    }

    private static void EvictOldest<TKey, TValue>(Dictionary<TKey, Slot<TValue>> dict, int max)
        where TKey : notnull
    {
        while (dict.Count > max)
        {
            var oldestKey = dict.MinBy(kv => kv.Value.LastUsedUtc).Key;
            dict.Remove(oldestKey);
        }
    }
}
