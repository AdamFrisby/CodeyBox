using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Majordomo;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed <see cref="IMajordomoConversationStore"/>. Shares the state
/// database file with the work-item and proposal stores via additive tables,
/// so the majordomo conversation survives orchestrator restarts and sandbox
/// recreations — the sandbox is disposable, the conversation is not.
/// </summary>
/// <remarks>
/// Appends join <see cref="SqliteDatabaseWriteGate"/>, the shared per-file
/// write gate every Sqlite* store on this database coordinates through;
/// <see cref="_gate"/> additionally serializes use of this store's single
/// connection. Lock order is always <see cref="_gate"/> then
/// <see cref="_writeLock"/> — reads take only <see cref="_gate"/>, so a
/// reader can never deadlock a writer. Each append runs its insert plus any
/// summary compaction inside one SQLite transaction, so a crash can never
/// leave compacted rows deleted without their summary, or vice versa.
/// </remarks>
public sealed class SqliteMajordomoConversationStore : IMajordomoConversationStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly ILogger<SqliteMajordomoConversationStore>? _log;
    private int _disposed;

    public SqliteMajordomoConversationStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null,
        ILogger<SqliteMajordomoConversationStore>? log = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _log = log;
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragmaCmd = _conn.CreateCommand())
            {
                pragmaCmd.CommandText =
                    $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={SqliteDefaults.BusyTimeoutMilliseconds}; PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS majordomo_conversation (
                    seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                    role        TEXT NOT NULL,
                    tool        TEXT,
                    text        TEXT NOT NULL,
                    recorded_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_majordomo_conversation_seq
                    ON majordomo_conversation(seq);
                CREATE TABLE IF NOT EXISTS majordomo_conversation_summary (
                    id           INTEGER PRIMARY KEY CHECK (id = 1),
                    up_to_seq    INTEGER NOT NULL DEFAULT 0,
                    summary_text TEXT NOT NULL DEFAULT '',
                    updated_at   TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MajordomoConversationEntry> AppendAsync(
        MajordomoConversationRole role,
        string text,
        string? toolName,
        DateTimeOffset recordedAt,
        MajordomoHistoryOptions policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(policy);
        var invalid = MajordomoHistoryOptions.Validate(policy);
        if (invalid is not null)
            throw new ArgumentException($"Invalid conversation policy: {invalid}", nameof(policy));

        // Validates role/tool/text pairing at the sink: unknown tool names
        // and prose turns carrying a tool are refused before anything is
        // persisted, exactly as the pure entry contract requires.
        _ = new MajordomoConversationEntry(role, text, recordedAt, 0, toolName);
        var stored = MajordomoConversationLimits.TruncateForStorage(text, policy.MaxEntryChars);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var tx = _conn.BeginTransaction();
                long seq;
                using (var insert = _conn.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO majordomo_conversation (role, tool, text, recorded_at)
                        VALUES ($role, $tool, $text, $at);
                        SELECT last_insert_rowid();
                        """;
                    insert.Parameters.AddWithValue("$role", role.ToString());
                    insert.Parameters.AddWithValue("$tool", toolName ?? (object)DBNull.Value);
                    insert.Parameters.AddWithValue("$text", stored);
                    insert.Parameters.AddWithValue("$at", recordedAt.ToString("O"));
                    seq = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                }

                long count;
                using (var counter = _conn.CreateCommand())
                {
                    counter.Transaction = tx;
                    counter.CommandText = "SELECT COUNT(*) FROM majordomo_conversation;";
                    count = (long)(await counter.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                }

                if (count > policy.MaxEntries)
                {
                    var excess = (int)Math.Min(count - policy.MaxEntries, int.MaxValue);
                    var covered = await ReadOldestAsync(tx, excess, ct).ConfigureAwait(false);
                    if (covered.Count > 0)
                    {
                        var existing = await ReadSummaryAsync(tx, ct).ConfigureAwait(false);
                        var combined = MajordomoHistorySummarizer.Combine(existing, covered, recordedAt);
                        await WriteSummaryAsync(tx, combined, ct).ConfigureAwait(false);
                        using var del = _conn.CreateCommand();
                        del.Transaction = tx;
                        del.CommandText = "DELETE FROM majordomo_conversation WHERE seq <= $through;";
                        del.Parameters.AddWithValue("$through", combined.UpToSequence);
                        await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                tx.Commit();
                return new MajordomoConversationEntry(role, stored, recordedAt, seq, toolName);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MajordomoConversationEntry>> ListAsync(
        long afterSequence = 0,
        int limit = IMajordomoConversationStore.MaxListLimit,
        CancellationToken ct = default)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence), afterSequence, "afterSequence must be >= 0");
        if (limit is < 1 or > IMajordomoConversationStore.MaxListLimit)
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit,
                $"limit must be within [1, {IMajordomoConversationStore.MaxListLimit}]");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT seq, role, tool, text, recorded_at FROM majordomo_conversation
                WHERE seq > $after ORDER BY seq LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$after", afterSequence);
            cmd.Parameters.AddWithValue("$limit", limit);
            var rows = new List<MajordomoConversationEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var entry = TryRead(reader);
                if (entry is not null)
                    rows.Add(entry);
            }

            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM majordomo_conversation;";
            return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MajordomoConversationSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT up_to_seq, summary_text, updated_at FROM majordomo_conversation_summary WHERE id = 1;";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return MajordomoConversationSummary.None;
            var upTo = reader.GetInt64(0);
            var text = reader.GetString(1);
            if (upTo <= 0 || string.IsNullOrWhiteSpace(text))
                return MajordomoConversationSummary.None;
            return new MajordomoConversationSummary(
                upTo, text, DateTimeOffset.Parse(reader.GetString(2), null));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<MajordomoConversationEntry>> ReadOldestAsync(
        SqliteTransaction tx, int count, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT seq, role, tool, text, recorded_at FROM majordomo_conversation
            ORDER BY seq LIMIT $count;
            """;
        cmd.Parameters.AddWithValue("$count", count);
        var rows = new List<MajordomoConversationEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = TryRead(reader);
            if (entry is not null)
                rows.Add(entry);
        }

        return rows;
    }

    private async Task<MajordomoConversationSummary> ReadSummaryAsync(
        SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT up_to_seq, summary_text, updated_at FROM majordomo_conversation_summary WHERE id = 1;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return MajordomoConversationSummary.None;
        var upTo = reader.GetInt64(0);
        var text = reader.GetString(1);
        if (upTo <= 0 || string.IsNullOrWhiteSpace(text))
            return MajordomoConversationSummary.None;
        return new MajordomoConversationSummary(
            upTo, text, DateTimeOffset.Parse(reader.GetString(2), null));
    }

    private async Task WriteSummaryAsync(
        SqliteTransaction tx, MajordomoConversationSummary summary, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO majordomo_conversation_summary (id, up_to_seq, summary_text, updated_at)
            VALUES (1, $through, $text, $at)
            ON CONFLICT (id) DO UPDATE SET up_to_seq = $through, summary_text = $text, updated_at = $at;
            """;
        cmd.Parameters.AddWithValue("$through", summary.UpToSequence);
        cmd.Parameters.AddWithValue("$text", summary.Text);
        cmd.Parameters.AddWithValue("$at", summary.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one row; corrupt rows are skipped (logged) so one unreadable
    /// row cannot take the whole history down — the same posture as the
    /// proposal store's list path.
    /// </summary>
    private MajordomoConversationEntry? TryRead(SqliteDataReader reader)
    {
        try
        {
            var seq = reader.GetInt64(0);
            var roleText = reader.GetString(1);
            if (!Enum.TryParse<MajordomoConversationRole>(roleText, out var role)
                || !Enum.IsDefined(role))
                throw new FormatException($"unknown role '{roleText}'");
            var tool = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (tool is not null && !MajordomoTools.TryGet(tool, out _))
                throw new FormatException($"unknown tool '{tool}'");
            return new MajordomoConversationEntry(
                role,
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), null),
                seq,
                tool);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or IndexOutOfRangeException)
        {
            _log?.LogWarning(
                "Skipping corrupt majordomo conversation row: {Detail}",
                Validation.DescribeUntrustedValue(ex.Message));
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _conn.Dispose();
            _gate.Dispose();
            _writeLock.Dispose();
        }
    }
}
