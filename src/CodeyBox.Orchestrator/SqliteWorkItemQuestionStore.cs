using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed store for agent-emitted work item questions.
/// Uses the same connection-level WAL mode as <see cref="SqliteWorkItemStore"/>;
/// shares the same database file so foreign keys can cascade on work item deletion.
/// </summary>
public sealed class SqliteWorkItemQuestionStore : IWorkItemQuestionStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteDatabaseWriteGate _writeLock;

    public SqliteWorkItemQuestionStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _writeLock = SqliteDatabaseWriteGate.ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var pragmaCmd = _conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS work_item_questions (
                    id            TEXT PRIMARY KEY,
                    work_item_id  TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                    question_id   TEXT NOT NULL,
                    question_text TEXT NOT NULL,
                    asked_at      TEXT NOT NULL,
                    answered_at   TEXT,
                    answer_text   TEXT,
                    answered_by   TEXT,
                    dismissed_at  TEXT,
                    dismiss_reason TEXT,
                    state         TEXT NOT NULL DEFAULT 'open'
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_questions_unique
                    ON work_item_questions(work_item_id, question_id);
                CREATE INDEX IF NOT EXISTS idx_questions_work_item
                    ON work_item_questions(work_item_id);
                """;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CreateIfNotExistsAsync(WorkItemQuestion question, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            // INSERT OR IGNORE skips on UNIQUE constraint violation (same work_item_id + question_id).
            cmd.CommandText = """
                INSERT OR IGNORE INTO work_item_questions
                    (id, work_item_id, question_id, question_text, asked_at, state)
                VALUES ($id, $work_item_id, $question_id, $question_text, $asked_at, 'open');
                """;
            cmd.Parameters.AddWithValue("$id", question.Id);
            cmd.Parameters.AddWithValue("$work_item_id", question.WorkItemId);
            cmd.Parameters.AddWithValue("$question_id", question.QuestionId);
            cmd.Parameters.AddWithValue("$question_text", question.QuestionText);
            cmd.Parameters.AddWithValue("$asked_at", question.AskedAt.ToString("O"));
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<WorkItemQuestion?> GetAsync(string workItemId, string questionId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM work_item_questions
            WHERE work_item_id = $wid AND question_id = $qid;
            """;
        cmd.Parameters.AddWithValue("$wid", workItemId);
        cmd.Parameters.AddWithValue("$qid", questionId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<WorkItemQuestion>> ListByWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM work_item_questions
            WHERE work_item_id = $wid
            ORDER BY asked_at ASC;
            """;
        cmd.Parameters.AddWithValue("$wid", workItemId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<WorkItemQuestion>();
        while (await reader.ReadAsync(ct))
            results.Add(Read(reader));
        return results;
    }

    public async Task AnswerAsync(string workItemId, string questionId, string answer, string? answeredBy, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            // Only update open rows; answering an already-answered/dismissed question is a no-op.
            cmd.CommandText = """
                UPDATE work_item_questions
                SET state = 'answered',
                    answer_text = $answer,
                    answered_by = $by,
                    answered_at = $at
                WHERE work_item_id = $wid AND question_id = $qid AND state = 'open';
                """;
            cmd.Parameters.AddWithValue("$wid", workItemId);
            cmd.Parameters.AddWithValue("$qid", questionId);
            cmd.Parameters.AddWithValue("$answer", answer);
            cmd.Parameters.AddWithValue("$by", (object?)answeredBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DismissAsync(string workItemId, string questionId, string reason, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE work_item_questions
                SET state = 'dismissed',
                    dismiss_reason = $reason,
                    dismissed_at = $at
                WHERE work_item_id = $wid AND question_id = $qid AND state = 'open';
                """;
            cmd.Parameters.AddWithValue("$wid", workItemId);
            cmd.Parameters.AddWithValue("$qid", questionId);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static WorkItemQuestion Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        WorkItemId = r.GetString(r.GetOrdinal("work_item_id")),
        QuestionId = r.GetString(r.GetOrdinal("question_id")),
        QuestionText = r.GetString(r.GetOrdinal("question_text")),
        AskedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("asked_at")), System.Globalization.CultureInfo.InvariantCulture),
        AnsweredAt = ReadNullable(r, "answered_at"),
        AnswerText = r.IsDBNull(r.GetOrdinal("answer_text")) ? null : r.GetString(r.GetOrdinal("answer_text")),
        AnsweredBy = r.IsDBNull(r.GetOrdinal("answered_by")) ? null : r.GetString(r.GetOrdinal("answered_by")),
        DismissedAt = ReadNullable(r, "dismissed_at"),
        DismissReason = r.IsDBNull(r.GetOrdinal("dismiss_reason")) ? null : r.GetString(r.GetOrdinal("dismiss_reason")),
        State = r.GetString(r.GetOrdinal("state")),
    };

    private static DateTimeOffset? ReadNullable(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord)
            ? null
            : DateTimeOffset.Parse(r.GetString(ord), System.Globalization.CultureInfo.InvariantCulture);
    }
}
