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
    private readonly SqliteDatabaseWriteGate _writeLock;
    private readonly TimeProvider _time;

    public SqliteSuggestionStore(
        string path,
        SqliteDatabaseWriteGateFactory? writeGateFactory = null,
        TimeProvider? timeProvider = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={path}");
        _time = timeProvider ?? TimeProvider.System;
        _writeLock = SqliteDatabaseWriteGateFactory.Resolve(writeGateFactory).ForPath(path);
        _writeLock.Wait();
        try
        {
            _conn.Open();

            using (var walCmd = _conn.CreateCommand())
            {
                // Disable FK enforcement: the REFERENCES declaration is for schema clarity only;
                // enforcement is not required and would break standalone-store usage in tests.
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys = OFF;";
                walCmd.ExecuteNonQuery();
            }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS suggestions (
                    id                        TEXT PRIMARY KEY,
                    source_work_item_id       TEXT NOT NULL REFERENCES work_items(id),
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
                    promoted_to_work_item_id  TEXT,
                    occurrence_count          INTEGER NOT NULL DEFAULT 1,
                    source_work_item_ids_json TEXT NOT NULL DEFAULT '[]',
                    dismissed_at              TEXT,
                    dedupe_key                TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_suggestions_state_project ON suggestions(state, project_id);
                """;
            cmd.ExecuteNonQuery();

            // Additive migrations for databases created before dedupe existed.
            RunMigration("ALTER TABLE suggestions ADD COLUMN occurrence_count INTEGER NOT NULL DEFAULT 1;");
            RunMigration("ALTER TABLE suggestions ADD COLUMN source_work_item_ids_json TEXT NOT NULL DEFAULT '[]';");
            RunMigration("ALTER TABLE suggestions ADD COLUMN dismissed_at TEXT;");
            RunMigration("ALTER TABLE suggestions ADD COLUMN dedupe_key TEXT;");
            BackfillDedupeColumns();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Runs a migration statement, tolerating "duplicate column name" so
    /// re-opened databases skip already-applied columns. All callers pass
    /// hardcoded DDL literals.
    /// </summary>
    private void RunMigration(string sql)
    {
        try
        {
            using var m = _conn.CreateCommand();
            // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all callers pass hardcoded DDL literals; no user-supplied input reaches this method
            m.CommandText = sql;
            m.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists from a previous startup — nothing to do.
        }
    }

    /// <summary>
    /// Back-fills rows written before dedupe existed: the source-id list
    /// starts at the single recorded source, and dedupe_key is derived so
    /// old rows participate in exact-key matching without a rescan.
    /// </summary>
    private void BackfillDedupeColumns()
    {
        using var fill = _conn.CreateCommand();
        fill.CommandText = """
            UPDATE suggestions
            SET source_work_item_ids_json = json_array(source_work_item_id)
            WHERE source_work_item_ids_json = '[]';
            """;
        fill.ExecuteNonQuery();

        var stale = new List<(string Id, string Category, string Title, IReadOnlyList<string> Files)>();
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT id, category, title, files_referenced_json FROM suggestions WHERE dedupe_key IS NULL;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var filesJson = reader.GetString(3);
                IReadOnlyList<string> files;
                try { files = JsonSerializer.Deserialize<string[]>(filesJson) ?? []; }
                catch (JsonException) { files = []; }
                stale.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), files));
            }
        }

        foreach (var row in stale)
        {
            using var upd = _conn.CreateCommand();
            upd.CommandText = "UPDATE suggestions SET dedupe_key = $key WHERE id = $id;";
            upd.Parameters.AddWithValue("$key",
                SuggestionDedupe.ComputeDedupeKey(row.Category, row.Files, row.Title));
            upd.Parameters.AddWithValue("$id", row.Id);
            upd.ExecuteNonQuery();
        }
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
                    dismiss_reason, promoted_to_work_item_id, occurrence_count,
                    source_work_item_ids_json, dismissed_at, dedupe_key)
                VALUES ($id, $wi, $pid, $title, $rationale, $category, $severity, $effort, $files,
                    $ca, $state, $dismiss, $promoted, $occurrences, $sources, $dismissedAt, $dedupeKey);
                """;
            Bind(cmd, NormalizeForInsert(suggestion));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<SuggestionCreateOutcome> CreateOrMergeAsync(
        Suggestion suggestion,
        SuggestionDedupePolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        await _writeLock.WaitAsync(ct);
        try
        {
            var incoming = NormalizeForInsert(suggestion);
            var match = await FindMatchAsync(incoming, policy, ct);
            if (match is null)
            {
                using var insert = _conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO suggestions (id, source_work_item_id, project_id, title, rationale, category,
                        severity, estimated_effort, files_referenced_json, created_at, state,
                        dismiss_reason, promoted_to_work_item_id, occurrence_count,
                        source_work_item_ids_json, dismissed_at, dedupe_key)
                    VALUES ($id, $wi, $pid, $title, $rationale, $category, $severity, $effort, $files,
                        $ca, $state, $dismiss, $promoted, $occurrences, $sources, $dismissedAt, $dedupeKey);
                    """;
                Bind(insert, incoming);
                await insert.ExecuteNonQueryAsync(ct);
                return new SuggestionCreateOutcome(incoming, Merged: false);
            }

            var sources = new List<string>(match.SourceWorkItemIds);
            if (!sources.Contains(match.SourceWorkItemId, StringComparer.Ordinal))
                sources.Insert(0, match.SourceWorkItemId);
            if (!sources.Contains(suggestion.SourceWorkItemId, StringComparer.Ordinal)
                && sources.Count < policy.MaxRecordedSourceIds)
                sources.Add(suggestion.SourceWorkItemId);

            using var merge = _conn.CreateCommand();
            merge.CommandText = """
                UPDATE suggestions
                SET occurrence_count = occurrence_count + 1,
                    source_work_item_ids_json = $sources
                WHERE id = $id;
                """;
            merge.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(sources));
            merge.Parameters.AddWithValue("$id", match.Id);
            await merge.ExecuteNonQueryAsync(ct);

            var merged = await GetAsync(match.Id, ct);
            return new SuggestionCreateOutcome(merged!, Merged: true);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Finds the canonical row an incoming suggestion repeats: candidates are
    /// open suggestions plus dismissed ones inside <paramref name="policy"/>'s
    /// window — same project and category, both part of the dedupe identity.
    /// An exact dedupe-key hit wins outright (score 1.0); otherwise the
    /// best-scoring candidate at or above the similarity threshold wins.
    /// Legacy dismissed rows (null dismissed_at) always qualify.
    /// </summary>
    private async Task<Suggestion?> FindMatchAsync(
        Suggestion incoming, SuggestionDedupePolicy policy, CancellationToken ct)
    {
        var candidates = new List<Suggestion>();
        using (var cmd = _conn.CreateCommand())
        {
            var cutoff = (_time.GetUtcNow() - policy.DismissedMatchWindow).ToString("O");
            cmd.CommandText = """
                SELECT * FROM suggestions
                WHERE project_id = $pid AND category = $cat COLLATE NOCASE
                  AND (state = 'open'
                       OR (state = 'dismissed'
                           AND (dismissed_at IS NULL OR dismissed_at >= $cutoff)));
                """;
            cmd.Parameters.AddWithValue("$pid", incoming.ProjectId);
            cmd.Parameters.AddWithValue("$cat", incoming.Category);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add(Read(reader));
        }

        var incomingSig = SuggestionDedupe.SignatureFor(incoming);
        Suggestion? best = null;
        var bestScore = policy.SimilarityThreshold;
        foreach (var candidate in candidates)
        {
            var candidateSig = SuggestionDedupe.SignatureFor(candidate);
            var score = string.Equals(candidateSig.DedupeKey, incomingSig.DedupeKey, StringComparison.Ordinal)
                ? 1.0
                : SuggestionDedupe.Similarity(incomingSig, candidateSig);
            if (score >= bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// Fills derivable fields so every inserted row carries a consistent
    /// dedupe identity: the source list always starts with
    /// <see cref="Suggestion.SourceWorkItemId"/> and the key is computed when
    /// the caller did not supply one.
    /// </summary>
    private static Suggestion NormalizeForInsert(Suggestion s)
    {
        var sources = s.SourceWorkItemIds.Count > 0
            ? s.SourceWorkItemIds
            : [s.SourceWorkItemId];
        return s with
        {
            OccurrenceCount = s.OccurrenceCount < 1 ? 1 : s.OccurrenceCount,
            SourceWorkItemIds = sources,
            DedupeKey = s.DedupeKey ?? SuggestionDedupe.ComputeDedupeKey(
                s.Category, s.FilesReferenced, s.Title),
        };
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
                    promoted_to_work_item_id = $promoted,
                    dismissed_at = COALESCE($dismissedAt, dismissed_at)
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$state", suggestion.State);
            cmd.Parameters.AddWithValue("$dismiss", (object?)suggestion.DismissReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$promoted", (object?)suggestion.PromotedToWorkItemId ?? DBNull.Value);
            // Stamping the dismissal time keeps the dedupe window honest for
            // rows transitioned through UpdateAsync rather than TryDismissAsync.
            var dismissedAt = suggestion.DismissedAt
                ?? (suggestion.State == "dismissed" ? (DateTimeOffset?)_time.GetUtcNow() : null);
            cmd.Parameters.AddWithValue("$dismissedAt",
                dismissedAt is { } at ? at.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", suggestion.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> TryAcceptAsync(string id, string promotedToWorkItemId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE suggestions
                SET state = 'accepted', promoted_to_work_item_id = $pid
                WHERE id = $id AND state = 'open';
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$pid", promotedToWorkItemId);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> TryDismissAsync(string id, string? dismissReason, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE suggestions SET state = 'dismissed', dismiss_reason = $reason, dismissed_at = $at WHERE id = $id AND state = 'open';";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$reason", (object?)dismissReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", _time.GetUtcNow().ToString("O"));
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
        finally { _writeLock.Release(); }
    }

    public async IAsyncEnumerable<Suggestion> ListAsync(
        string? projectId = null,
        string? category = null,
        string? severity = null,
        string? state = "open",
        int limit = 200,
        int offset = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        if (state is not null) { where.Add("state = $state"); cmd.Parameters.AddWithValue("$state", state); }
        if (projectId is not null) { where.Add("project_id = $pid"); cmd.Parameters.AddWithValue("$pid", projectId); }
        if (category is not null) { where.Add("category = $cat"); cmd.Parameters.AddWithValue("$cat", category); }
        if (severity is not null) { where.Add("severity = $sev"); cmd.Parameters.AddWithValue("$sev", severity); }

        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all conditions use named parameterized placeholders; no user input reaches the SQL string
        cmd.CommandText = $"SELECT * FROM suggestions {whereClause} ORDER BY created_at DESC LIMIT $limit OFFSET $offset;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return Read(reader);
    }

    public async Task<int> CountAsync(
        string? projectId = null,
        string? category = null,
        string? severity = null,
        string? state = "open",
        CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        if (state is not null) { where.Add("state = $state"); cmd.Parameters.AddWithValue("$state", state); }
        if (projectId is not null) { where.Add("project_id = $pid"); cmd.Parameters.AddWithValue("$pid", projectId); }
        if (category is not null) { where.Add("category = $cat"); cmd.Parameters.AddWithValue("$cat", category); }
        if (severity is not null) { where.Add("severity = $sev"); cmd.Parameters.AddWithValue("$sev", severity); }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- all conditions use named parameterized placeholders; no user input reaches the SQL string
        cmd.CommandText = $"SELECT COUNT(*) FROM suggestions {whereClause};";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
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
        cmd.Parameters.AddWithValue("$occurrences", s.OccurrenceCount);
        cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(s.SourceWorkItemIds.ToList()));
        cmd.Parameters.AddWithValue("$dismissedAt",
            s.DismissedAt is { } dismissedAt ? dismissedAt.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$dedupeKey", (object?)s.DedupeKey ?? DBNull.Value);
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
        OccurrenceCount = r.GetInt32(r.GetOrdinal("occurrence_count")),
        SourceWorkItemIds = ReadSources(r),
        DismissedAt = r.IsDBNull(r.GetOrdinal("dismissed_at"))
            ? null
            : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("dismissed_at")),
                System.Globalization.CultureInfo.InvariantCulture),
        DedupeKey = r.IsDBNull(r.GetOrdinal("dedupe_key"))
            ? null : r.GetString(r.GetOrdinal("dedupe_key")),
    };

    private static IReadOnlyList<string> ReadFiles(SqliteDataReader r) =>
        ReadStringList(r, "files_referenced_json");

    private static IReadOnlyList<string> ReadSources(SqliteDataReader r)
    {
        var sources = ReadStringList(r, "source_work_item_ids_json");
        // Rows predating the column (or written by a non-normalizing path)
        // still report the single recorded source.
        return sources.Count > 0
            ? sources
            : [r.GetString(r.GetOrdinal("source_work_item_id"))];
    }

    private static IReadOnlyList<string> ReadStringList(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        if (r.IsDBNull(ord)) return [];
        var json = r.GetString(ord);
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
