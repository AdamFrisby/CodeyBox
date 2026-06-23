using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeyBox.Core;

/// <summary>
/// Durable store of <see cref="E2eRun"/> records. Implementations persist runs
/// alongside the other orchestrator state (a SQLite table sharing the work-item
/// database in production).
/// </summary>
public interface IE2eRunStore
{
    Task CreateAsync(E2eRun run, CancellationToken ct = default);

    Task<E2eRun?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Lists all runs, newest-first.</summary>
    IAsyncEnumerable<E2eRun> ListAsync(CancellationToken ct = default);

    IAsyncEnumerable<E2eRun> ListByTestCaseAsync(string testCaseId, CancellationToken ct = default);

    IAsyncEnumerable<E2eRun> ListByBatchAsync(string batchId, CancellationToken ct = default);

    /// <summary>Atomically claims the oldest queued run; returns null when the queue is empty.</summary>
    Task<E2eRun?> ClaimNextQueuedAsync(string sandboxId, CancellationToken ct = default);

    /// <summary>Updates the status and terminal fields of a run.</summary>
    Task<bool> UpdateStatusAsync(string id, E2eRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string? result, CancellationToken ct = default);

    /// <summary>Marks a queued or running run as Canceled; returns false when the run is already terminal.</summary>
    Task<bool> CancelAsync(string id, CancellationToken ct = default);
}
