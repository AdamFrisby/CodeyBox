using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// SQLite-backed suggestion store. Shares the same database file as
/// <see cref="SqliteWorkItemStore"/>; the suggestions table is created here
/// via its own additive migration so the two stores stay independently testable.
/// </summary>
public sealed class SqliteSuggestionStore : ISuggestionStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteSuggestionStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();

        using (var walCmd = _conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            walCmd.ExecuteNonQuery();
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS suggestions (
                id                        TEXT PRIMARY KEY,
                source_work_item_id       TEXT NOT NULL,
                project_id                TEXT NOT NULL,
                title                     TEXT NOT NULL,
                rationale                 TEXT NOT NULL,
                category                  TEXT NOT NULL,
                severity                  TEXT NOT NULL,
                estimated_effort          TEXT NOT NULL,
                files_referenced_json     TEXT NOT NULL DEFAULT '[]',
                created_at                TEXT NOT NULL,
                state                     TEXT NOT NULL DEFAULT 'open',
                dismiss_reason            TEXT,
                promoted_to_work_item_id  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_suggestions_state_project ON suggestions(state, project_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task CreateAsync(Suggestion suggestion, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO suggestions (id, source_work_item_id, project_id, title, rationale, category,
                    severity, estimated_effort, files_referenced_json, created_at, state,
                    dismiss_reason, promoted_to_work_item_id)
                VALUES ($id, $wi, $pid, $title, $rationale, $category, $severity, $effort, $files,
                    $ca, $state, $dismiss, $promoted);
                """;
            Bind(cmd, suggestion);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<Suggestion?> GetAsync(string id, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM suggestions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task UpdateAsync(Suggestion suggestion, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE suggestions SET
                    state = $state,
                    dismiss_reason = $dismiss,
                    promoted_to_work_item_id = $promoted
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$state", suggestion.State);
            cmd.Parameters.AddWithValue("$dismiss", (object?)suggestion.DismissReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$promoted", (object?)suggestion.PromotedToWorkItemId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", suggestion.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async IAsyncEnumerable<Suggestion> ListAsync(
        string? projectId = null,
        string? category = null,
        string? severity = null,
        string? state = "open",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        if (state is not null) { where.Add("state = $state"); cmd.Parameters.AddWithValue("$state", state); }
        if (projectId is not null) { where.Add("project_id = $pid"); cmd.Parameters.AddWithValue("$pid", projectId); }
        if (category is not null) { where.Add("category = $cat"); cmd.Parameters.AddWithValue("$cat", category); }
        if (severity is not null) { where.Add("severity = $sev"); cmd.Parameters.AddWithValue("$sev", severity); }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all conditions use named parameterized placeholders; no user input reaches the SQL string
        cmd.CommandText = $"SELECT * FROM suggestions {whereClause} ORDER BY created_at DESC;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return Read(reader);
    }

    public async Task<int> CountOpenAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM suggestions WHERE state = 'open';";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public void Dispose()
    {
        _conn.Dispose();
        _writeLock.Dispose();
    }

    private static void Bind(SqliteCommand cmd, Suggestion s)
    {
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$wi", s.SourceWorkItemId);
        cmd.Parameters.AddWithValue("$pid", s.ProjectId);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$rationale", s.Rationale);
        cmd.Parameters.AddWithValue("$category", s.Category);
        cmd.Parameters.AddWithValue("$severity", s.Severity);
        cmd.Parameters.AddWithValue("$effort", s.EstimatedEffort);
        cmd.Parameters.AddWithValue("$files", JsonSerializer.Serialize(s.FilesReferenced.ToList()));
        cmd.Parameters.AddWithValue("$ca", s.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$state", s.State);
        cmd.Parameters.AddWithValue("$dismiss", (object?)s.DismissReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$promoted", (object?)s.PromotedToWorkItemId ?? DBNull.Value);
    }

    private static Suggestion Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SourceWorkItemId = r.GetString(r.GetOrdinal("source_work_item_id")),
        ProjectId = r.GetString(r.GetOrdinal("project_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        Rationale = r.GetString(r.GetOrdinal("rationale")),
        Category = r.GetString(r.GetOrdinal("category")),
        Severity = r.GetString(r.GetOrdinal("severity")),
        EstimatedEffort = r.GetString(r.GetOrdinal("estimated_effort")),
        FilesReferenced = ReadFiles(r),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")),
            System.Globalization.CultureInfo.InvariantCulture),
        State = r.GetString(r.GetOrdinal("state")),
        DismissReason = r.IsDBNull(r.GetOrdinal("dismiss_reason"))
            ? null : r.GetString(r.GetOrdinal("dismiss_reason")),
        PromotedToWorkItemId = r.IsDBNull(r.GetOrdinal("promoted_to_work_item_id"))
            ? null : r.GetString(r.GetOrdinal("promoted_to_work_item_id")),
    };

    private static IReadOnlyList<string> ReadFiles(SqliteDataReader r)
    {
        var ord = r.GetOrdinal("files_referenced_json");
        if (r.IsDBNull(ord)) return [];
        var json = r.GetString(ord);
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
}
