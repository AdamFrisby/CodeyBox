using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class StoreWorkItemAttachmentSourceTests
{
    private static WorkItemId NewId() => new(Guid.NewGuid());

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenNoAttachments()
    {
        var store = new InMemoryAttachmentStore();
        var source = new StoreWorkItemAttachmentSource(store);
        var result = await source.ListAsync(NewId());
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAsync_PathsAreUnderStagingDirectory()
    {
        var id = NewId();
        var store = new InMemoryAttachmentStore();
        await store.CreateAsync(MakeRecord(id, "spec.txt"));
        var source = new StoreWorkItemAttachmentSource(store);

        var result = await source.ListAsync(id);

        var att = Assert.Single(result);
        Assert.Equal("spec.txt", att.FileName);
        Assert.Equal("/work/.codeybox/attachments/spec.txt", att.InVmPath);
        Assert.StartsWith(StoreWorkItemAttachmentSource.SandboxStagingDirectory, att.InVmPath);
    }

    [Fact]
    public async Task ListAsync_DisambiguatesDuplicateFileNames_WithConsistentNameAndPath()
    {
        var id = NewId();
        var store = new InMemoryAttachmentStore();
        await store.CreateAsync(MakeRecord(id, "spec.txt", idSuffix: "aaa"));
        await store.CreateAsync(MakeRecord(id, "spec.txt", idSuffix: "bbb"));
        var source = new StoreWorkItemAttachmentSource(store);

        var result = await source.ListAsync(id);

        Assert.Equal(2, result.Count);
        // Both InVmPaths must be distinct and under the staging dir.
        Assert.Equal("/work/.codeybox/attachments/spec.txt", result[0].InVmPath);
        Assert.Equal("/work/.codeybox/attachments/bbb-spec.txt", result[1].InVmPath);
        // The disambiguated FileName must match the InVmPath basename so the
        // manifest does not lie about where the file lives.
        Assert.Equal("bbb-spec.txt", result[1].FileName);
        Assert.NotEqual(result[0].InVmPath, result[1].InVmPath);
    }

    private static WorkItemAttachmentRecord MakeRecord(WorkItemId workItemId, string fileName, string idSuffix = "") => new()
    {
        Id = string.IsNullOrEmpty(idSuffix) ? Guid.NewGuid().ToString("N") : idSuffix,
        WorkItemId = workItemId,
        FileName = fileName,
        ContentType = "text/plain",
        SizeBytes = 100,
        Sha256 = new string('a', 64),
        Caption = "",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class InMemoryAttachmentStore : IWorkItemAttachmentStore
    {
        private readonly List<WorkItemAttachmentRecord> _rows = new();

        public Task CreateAsync(WorkItemAttachmentRecord record, CancellationToken ct = default)
        { _rows.Add(record); return Task.CompletedTask; }

        public Task<WorkItemAttachmentRecord?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_rows.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<WorkItemAttachmentRecord>> ListForWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkItemAttachmentRecord>>(_rows.Where(r => r.WorkItemId == workItemId).ToList());

        public Task<(int Count, long TotalBytes)> AggregateForWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        {
            var rows = _rows.Where(r => r.WorkItemId == workItemId).ToList();
            return Task.FromResult((rows.Count, rows.Sum(r => r.SizeBytes)));
        }

        public Task<WorkItemAttachmentRecord?> DeleteAsync(string id, WorkItemId? scopeByWorkItemId = null, CancellationToken ct = default)
        {
            var row = _rows.FirstOrDefault(r => r.Id == id);
            if (row is not null) _rows.Remove(row);
            return Task.FromResult(row);
        }

        public Task<int> CountReferencesAsync(string sha256, CancellationToken ct = default) =>
            Task.FromResult(_rows.Count(r => r.Sha256 == sha256));

        public Task<IReadOnlyCollection<string>> ListReferencedHashesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(_rows.Select(r => r.Sha256).Distinct().ToList());

        public async IAsyncEnumerable<WorkItemId> ListTerminalWithAttachmentsAsync(DateTimeOffset olderThan, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public Task<IReadOnlyList<WorkItemAttachmentRecord>> DeleteAllForWorkItemAsync(WorkItemId workItemId, CancellationToken ct = default)
        {
            var rows = _rows.Where(r => r.WorkItemId == workItemId).ToList();
            _rows.RemoveAll(r => r.WorkItemId == workItemId);
            return Task.FromResult<IReadOnlyList<WorkItemAttachmentRecord>>(rows);
        }

        public Task<bool> CreateBatchIfUnderCapAsync(IReadOnlyList<WorkItemAttachmentRecord> records, int maxCount, long maxTotalBytes, CancellationToken ct = default)
        {
            foreach (var r in records) _rows.Add(r);
            return Task.FromResult(true);
        }

        public Task<AttachmentBatchCreateResult> CreateBatchForQueuedWorkItemIfUnderCapAsync(
            IReadOnlyList<WorkItemAttachmentRecord> records,
            int maxCount,
            long maxTotalBytes,
            CancellationToken ct = default)
        {
            foreach (var r in records) _rows.Add(r);
            return Task.FromResult(new AttachmentBatchCreateResult(AttachmentMutationOutcome.Applied));
        }

        public Task<AttachmentDeleteResult> DeleteIfWorkItemQueuedAsync(
            string id,
            WorkItemId workItemId,
            CancellationToken ct = default)
        {
            var row = _rows.FirstOrDefault(r => r.Id == id && r.WorkItemId == workItemId);
            if (row is null)
                return Task.FromResult(new AttachmentDeleteResult(AttachmentMutationOutcome.NotFound));
            _rows.Remove(row);
            return Task.FromResult(new AttachmentDeleteResult(AttachmentMutationOutcome.Applied, row, WorkItemState.Queued));
        }

        public async IAsyncEnumerable<WorkItemId> ListCleanupCandidatesWithAttachmentsAsync(DateTimeOffset updatedBefore, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
    }
}
