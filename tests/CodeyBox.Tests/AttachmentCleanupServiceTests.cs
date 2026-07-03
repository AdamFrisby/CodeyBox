using System.Text;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class AttachmentCleanupServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _rootDir;
    private readonly SqliteConnection _rawConn;
    private readonly SqliteWorkItemAttachmentStore _store;
    private readonly HostWorkItemAttachmentBlobStore _blobs;
    private readonly ManualTimeProvider _time;
    private readonly AttachmentsOptions _options;

    public AttachmentCleanupServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-cleanup-db-test-{Guid.NewGuid():N}.db");
        _rootDir = Path.Combine(Path.GetTempPath(), $"codeybox-cleanup-blobs-test-{Guid.NewGuid():N}");

        _rawConn = new SqliteConnection($"Data Source={_dbPath}");
        _rawConn.Open();
        using var setupCmd = _rawConn.CreateCommand();
        setupCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS work_items (
                id         TEXT PRIMARY KEY,
                state      INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT ''
            );
            """;
        setupCmd.ExecuteNonQuery();

        _store = new SqliteWorkItemAttachmentStore(_dbPath);
        _blobs = new HostWorkItemAttachmentBlobStore(() => _rootDir);
        _time = new ManualTimeProvider();
        _time.Advance(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch); // Set to current system time approx

        _options = new AttachmentsOptions
        {
            RootDirectory = _rootDir,
            TerminalCleanupTtl = TimeSpan.FromDays(7),
            OrphanGracePeriod = TimeSpan.FromMinutes(10)
        };
    }

    public void Dispose()
    {
        _store.Dispose();
        _rawConn.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true); } catch { /* best-effort */ }
    }

    private static WorkItemId NewId() => new(Guid.NewGuid());

    private void SeedWorkItem(WorkItemId id, WorkItemState state, DateTimeOffset updatedAt)
    {
        using var cmd = _rawConn.CreateCommand();
        cmd.CommandText = "INSERT INTO work_items (id, state, updated_at) VALUES ($id, $state, $now)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$now", updatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task RunTerminalCleanupAsync_DeletesMetadataAndBlobs_WhenOlderThanTtl()
    {
        var service = new AttachmentCleanupService(
            _store,
            _blobs,
            () => _options,
            NullLogger<AttachmentCleanupService>.Instance,
            _time);

        var now = _time.GetUtcNow();
        var olderThanTtl = now.AddDays(-10);
        var newerThanTtl = now.AddDays(-3);

        // Work Item 1: Terminal (Done), older than 7 days TTL -> should be cleaned up
        var wi1 = NewId();
        SeedWorkItem(wi1, WorkItemState.Done, olderThanTtl);
        using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes("content1"));
        var blob1 = await _blobs.StageAsync(stream1, 100);
        var rec1 = new WorkItemAttachmentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = wi1,
            FileName = "file1.txt",
            ContentType = "text/plain",
            SizeBytes = blob1.SizeBytes,
            Sha256 = blob1.Sha256,
            CreatedAt = olderThanTtl
        };
        await _store.CreateAsync(rec1);

        // Work Item 2: Terminal (Failed), but newer than TTL -> should NOT be cleaned up
        var wi2 = NewId();
        SeedWorkItem(wi2, WorkItemState.Failed, newerThanTtl);
        using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes("content2"));
        var blob2 = await _blobs.StageAsync(stream2, 100);
        var rec2 = new WorkItemAttachmentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = wi2,
            FileName = "file2.txt",
            ContentType = "text/plain",
            SizeBytes = blob2.SizeBytes,
            Sha256 = blob2.Sha256,
            CreatedAt = newerThanTtl
        };
        await _store.CreateAsync(rec2);

        // Work Item 3: Non-terminal (Queued), older than TTL -> should NOT be cleaned up
        var wi3 = NewId();
        SeedWorkItem(wi3, WorkItemState.Queued, olderThanTtl);
        using var stream3 = new MemoryStream(Encoding.UTF8.GetBytes("content3"));
        var blob3 = await _blobs.StageAsync(stream3, 100);
        var rec3 = new WorkItemAttachmentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = wi3,
            FileName = "file3.txt",
            ContentType = "text/plain",
            SizeBytes = blob3.SizeBytes,
            Sha256 = blob3.Sha256,
            CreatedAt = olderThanTtl
        };
        await _store.CreateAsync(rec3);

        // Run the sweep
        var deletedCount = await service.RunTerminalCleanupAsync(_options, now, CancellationToken.None);

        Assert.Equal(1, deletedCount);

        // Metadata for WI 1 should be gone
        Assert.Null(await _store.GetAsync(rec1.Id));
        // Blob 1 should be deleted from disk
        Assert.False(_blobs.Exists(blob1.Sha256));

        // Metadata and blobs for WI 2 and WI 3 should be preserved
        Assert.NotNull(await _store.GetAsync(rec2.Id));
        Assert.True(_blobs.Exists(blob2.Sha256));
        Assert.NotNull(await _store.GetAsync(rec3.Id));
        Assert.True(_blobs.Exists(blob3.Sha256));
    }

    [Fact]
    public async Task RunOrphanSweepAsync_DeletesOrphans_OlderThanGracePeriod()
    {
        var service = new AttachmentCleanupService(
            _store,
            _blobs,
            () => _options,
            NullLogger<AttachmentCleanupService>.Instance,
            _time);

        var now = _time.GetUtcNow();

        // 1. Referenced blob -> should NOT be deleted
        var wi = NewId();
        SeedWorkItem(wi, WorkItemState.Queued, now);
        using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("referenced"));
        var blob1 = await _blobs.StageAsync(s1, 100);
        var rec = new WorkItemAttachmentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = wi,
            FileName = "file1.txt",
            ContentType = "text/plain",
            SizeBytes = blob1.SizeBytes,
            Sha256 = blob1.Sha256,
            CreatedAt = now
        };
        await _store.CreateAsync(rec);

        // 2. Orphan blob 1: young (created just now) -> should NOT be deleted
        using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("young-orphan"));
        var blob2 = await _blobs.StageAsync(s2, 100);
        var path2 = Path.Combine(_rootDir, blob2.Sha256[..2], blob2.Sha256);
        File.SetCreationTimeUtc(path2, now.UtcDateTime);

        // 3. Orphan blob 2: old (created 1 hour ago) -> should be deleted
        using var s3 = new MemoryStream(Encoding.UTF8.GetBytes("old-orphan"));
        var blob3 = await _blobs.StageAsync(s3, 100);
        var path3 = Path.Combine(_rootDir, blob3.Sha256[..2], blob3.Sha256);
        File.SetCreationTimeUtc(path3, now.AddHours(-1).UtcDateTime);

        // Run the sweep
        var deletedCount = await service.RunOrphanSweepAsync(_options, CancellationToken.None);

        Assert.Equal(1, deletedCount);

        // Referenced blob still exists
        Assert.True(_blobs.Exists(blob1.Sha256));

        // Young orphan still exists
        Assert.True(_blobs.Exists(blob2.Sha256));

        // Old orphan is deleted
        Assert.False(_blobs.Exists(blob3.Sha256));
    }
}
