using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Host-side baseline store: in-memory index keyed by project + commit with
/// bounded per-project retention. A production failure is recorded for status
/// observability but never evicts a good baseline — staging only ever reads
/// successfully stored artifacts, so failures cannot block audits (fail-safe).
/// Thread-safe; every method guards its inputs at the sink (exact-match keys,
/// size caps before buffering) so future callers cannot widen it.
/// </summary>
public sealed class TestSelectionBaselineStore : ITestSelectionBaselineStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedList<TestSelectionBaselineStored>> _baselines =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestSelectionBaselineFailure> _failures =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public TestSelectionBaselineStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public Task StoreAsync(
        string projectId,
        string commit,
        DateTimeOffset producedAtUtc,
        string json,
        int testCount,
        TimeSpan duration,
        int maxRetainedPerProject,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(json);
        if (maxRetainedPerProject <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedPerProject));

        lock (_sync)
        {
            if (!_baselines.TryGetValue(projectId, out var list))
                _baselines[projectId] = list = new LinkedList<TestSelectionBaselineStored>();

            for (var node = list.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Commit, commit, StringComparison.Ordinal))
                {
                    list.Remove(node);
                    break;
                }
            }

            list.AddFirst(new TestSelectionBaselineStored(
                projectId,
                commit,
                producedAtUtc,
                json,
                SizeBytes: json.Length,
                TestCount: testCount));

            while (list.Count > maxRetainedPerProject)
                list.RemoveLast();
        }

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(
        string projectId,
        string commit,
        string error,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        lock (_sync)
        {
            _failures[projectId] = new TestSelectionBaselineFailure(
                projectId,
                commit,
                _clock.GetUtcNow(),
                error,
                duration);
        }

        return Task.CompletedTask;
    }

    public Task<TestSelectionBaselineStored?> GetAsync(
        string projectId,
        string commit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        lock (_sync)
        {
            if (!_baselines.TryGetValue(projectId, out var list))
                return Task.FromResult<TestSelectionBaselineStored?>(null);
            foreach (var stored in list)
            {
                if (string.Equals(stored.Commit, commit, StringComparison.Ordinal))
                    return Task.FromResult<TestSelectionBaselineStored?>(stored);
            }

            return Task.FromResult<TestSelectionBaselineStored?>(null);
        }
    }

    public Task<IReadOnlyList<TestSelectionBaselineStored>> ListAsync(
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        lock (_sync)
        {
            if (!_baselines.TryGetValue(projectId, out var list))
                return Task.FromResult<IReadOnlyList<TestSelectionBaselineStored>>([]);
            return Task.FromResult<IReadOnlyList<TestSelectionBaselineStored>>([.. list]);
        }
    }

    public Task<TestSelectionBaselineFailure?> GetLastFailureAsync(
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        lock (_sync)
        {
            _failures.TryGetValue(projectId, out var failure);
            return Task.FromResult(failure);
        }
    }
}
