using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed metadata index for work-item attachments. Shares the same
/// database file as <see cref="SqliteWorkItemStore"/>; the
/// <c>work_item_attachments</c> table is created here via its own additive
/// migration so the two stores stay independently testable.
/// </summary>
/// <remarks>
/// Foreign-key enforcement is left OFF on this connection (matching
/// <see cref="SqliteSuggestionStore"/>): the REFERENCES declaration documents
/// the relationship for schema readers without coupling testability to FK
/// enforcement, and blob lifecycle is managed explicitly by the orchestrator
/// (terminal-state cleanup + orphan sweep) rather than by row cascades.
/// </remarks>
public sealed class SqliteWorkItemAttachmentStore : IWorkItemAttachmentStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    public SqliteWorkItemAttachmentStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGate.ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var walCmd = _conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys = OFF;";
                walCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS work_item_attachments (
                    id            TEXT PRIMARY KEY,
                    work_item_id  TEXT NOT NULL REFERENCES work_items(id),
                    file_name     TEXT NOT NULL,
                    content_type  TEXT NOT NULL,
                    size_bytes    INTEGER NOT NULL,
                    sha256        TEXT NOT NULL,
                    caption       TEXT NOT NULL DEFAULT '',
                    created_at    TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_work_item_attachments_wi ON work_item_attachments(work_item_id);
                CREATE INDEX IF NOT EXISTS idx_work_item_attachments_sha ON work_item_attachments(sha256);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CreateAsync(WorkItemAttachmentRecord record, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO work_item_attachments
                    (id, work_item_id, file_name, content_type, size_bytes, sha256, caption, created_at)
                VALUES
                    ($id, $wi, $fn, $ct, $sz, $sha, $cap, $ca);
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$wi", record.WorkItemId.ToString());
            cmd.Parameters.AddWithValue("$fn", record.FileName);
            cmd.Parameters.AddWithValue("$ct", record.ContentType);
            cmd.Parameters.AddWithValue("$sz", record.SizeBytes);
            cmd.Parameters.AddWithValue("$sha", record.Sha256);
            cmd.Parameters.AddWithValue("$cap", record.Caption);
            cmd.Parameters.AddWithValue("$ca", record.CreatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<WorkItemAttachmentRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_item_attachments WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<WorkItemAttachmentRecord>> ListForWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_item_attachments WHERE work_item_id = $wi ORDER BY created_at ASC, id ASC;";
        cmd.Parameters.AddWithValue("$wi", workItemId.ToString());
        var results = new List<WorkItemAttachmentRecord>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Read(reader));
        return results;
    }

    public async Task<(int Count, long TotalBytes)> AggregateForWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM work_item_attachments WHERE work_item_id = $wi;";
        cmd.Parameters.AddWithValue("$wi", workItemId.ToString());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0);
        var count = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
        var total = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return (count, total);
    }

    public async Task<WorkItemAttachmentRecord?> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var existing = await GetAsync(id, ct);
            if (existing is null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM work_item_attachments WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return existing;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> CountReferencesAsync(string sha256, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM work_item_attachments WHERE sha256 = $sha;";
        cmd.Parameters.AddWithValue("$sha", sha256);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<IReadOnlyCollection<string>> ListReferencedHashesAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT sha256 FROM work_item_attachments;";
        var results = new HashSet<string>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetString(0));
        return results;
    }

    public async IAsyncEnumerable<WorkItemId> ListTerminalWithAttachmentsAsync(
        DateTimeOffset olderThan,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Terminal states: Done(6), Failed(100), Cancelled(101), AuditFailed(102),
        // AbandonedAfterRecoveryAttempts(103), MergeConflictResolutionFailed(104).
        // These align with WorkItemState terminal classification — we list the
        // numeric values directly to keep this store decoupled from the orchestrator
        // helper that owns the in-memory classification.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT a.work_item_id
            FROM work_item_attachments a
            JOIN work_items w ON w.id = a.work_item_id
            WHERE w.state IN (6, 100, 101, 102, 103, 104)
              AND w.updated_at < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return WorkItemId.Parse(reader.GetString(0));
        }
    }

    public async Task<IReadOnlyList<WorkItemAttachmentRecord>> DeleteAllForWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var rows = await ListForWorkItemAsync(workItemId, ct);
            if (rows.Count == 0) return rows;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM work_item_attachments WHERE work_item_id = $wi;";
            cmd.Parameters.AddWithValue("$wi", workItemId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
            return rows;
        }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static WorkItemAttachmentRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        WorkItemId = WorkItemId.Parse(r.GetString(r.GetOrdinal("work_item_id"))),
        FileName = r.GetString(r.GetOrdinal("file_name")),
        ContentType = r.GetString(r.GetOrdinal("content_type")),
        SizeBytes = r.GetInt64(r.GetOrdinal("size_bytes")),
        Sha256 = r.GetString(r.GetOrdinal("sha256")),
        Caption = r.IsDBNull(r.GetOrdinal("caption")) ? string.Empty : r.GetString(r.GetOrdinal("caption")),
        CreatedAt = DateTimeOffset.Parse(
            r.GetString(r.GetOrdinal("created_at")),
            System.Globalization.CultureInfo.InvariantCulture),
    };
}
