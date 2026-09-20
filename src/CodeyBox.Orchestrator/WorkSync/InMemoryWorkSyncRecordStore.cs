using CodeyBox.Core;

namespace CodeyBox.Orchestrator.WorkSync;

/// <summary>
/// In-memory <see cref="IWorkSyncRecordStore"/> for hosts that persist the
/// cross-system audit trail elsewhere (or single-process deployments).
/// Thread-safe; newest writes win no races because records are append-only.
/// </summary>
public sealed class InMemoryWorkSyncRecordStore : IWorkSyncRecordStore
{
    private readonly List<WorkSyncRecord> _records = [];
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task RecordAsync(WorkSyncRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
            _records.Add(record);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkSyncRecord>> ListByWorkItemAsync(
        WorkItemId workItemId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<WorkSyncRecord>>(
                _records.Where(r => r.WorkItemId == workItemId).OrderBy(r => r.CreatedAt).ToList());
    }

    /// <inheritdoc />
    public Task<string?> LastPostedStatusAsync(
        WorkItemId workItemId, string @namespace, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_records
                .Where(r => r.WorkItemId == workItemId
                    && string.Equals(r.Namespace, @namespace, StringComparison.OrdinalIgnoreCase)
                    && r.Succeeded
                    && (r.Kind is WorkSyncRecordKind.Progress
                        or WorkSyncRecordKind.Completion
                        or WorkSyncRecordKind.Links)
                    && !string.IsNullOrEmpty(r.ExternalStatus))
                .OrderBy(r => r.CreatedAt)
                .LastOrDefault()?.ExternalStatus);
    }
}
