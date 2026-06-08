using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Round-trip migration coverage for the <c>agent_instance_id</c> column that
/// the multi-subscription pooling change (#226/#227) added across the five
/// SQLite stores. A pre-pooling database must be openable by the new binary
/// without a "no such column" crash; the column is back-filled NULL for
/// existing rows so reads continue to round-trip.
///
/// The first pooling rollout (#226/#227) shipped a regression in
/// <see cref="SqliteAgentUsageStore"/>: the partial CREATE INDEX referencing
/// <c>agent_instance_id</c> sat inside the initial CREATE TABLE bundle, so it
/// ran BEFORE the ALTER TABLE that adds the column on an old DB. SQLite raised
/// "no such column: agent_instance_id" and aborted host startup before DI
/// could even resolve the work-item store. These tests pin the fix in place.
/// </summary>
public sealed class SqliteAgentInstanceColumnMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-instanceid-migrate-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best-effort */ }
    }

    [Fact]
    public void AgentUsageStore_OpensCleanly_AgainstPrePoolingSchema()
    {
        // Pre-pooling schema: agent_usage_events without agent_instance_id and
        // without the partial idx_usage_instance_model_time index. This is the
        // exact shape an operator's pre-#226 DB had on disk when they tried to
        // start the new binary.
        SeedPrePoolingAgentUsageEvents();

        // Must not throw. Pre-fix this raised
        //   SqliteException: SQLite Error 1: 'no such column: agent_instance_id'
        // from the CREATE INDEX statement bundled in with CREATE TABLE.
        using var store = new SqliteAgentUsageStore(_dbPath);

        // Column added, existing row preserved with NULL agent_instance_id.
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();

        Assert.True(ColumnExists(raw, "agent_usage_events", "agent_instance_id"));
        Assert.True(IndexExists(raw, "idx_usage_instance_model_time"));

        using var check = raw.CreateCommand();
        check.CommandText = "SELECT agent_instance_id FROM agent_usage_events WHERE id = 'legacy-row';";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public void WorkItemCostStore_OpensCleanly_AgainstPrePoolingSchema()
    {
        SeedPrePoolingWorkItemsAndCosts();

        using var store = new SqliteWorkItemCostStore(_dbPath);

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        Assert.True(ColumnExists(raw, "work_item_costs", "agent_instance_id"));

        using var check = raw.CreateCommand();
        check.CommandText = "SELECT agent_instance_id FROM work_item_costs WHERE id = 'cost-legacy';";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public void AgentInvolvementStore_OpensCleanly_AgainstPrePoolingSchema()
    {
        SeedPrePoolingAgentInvolvement();

        using var store = new SqliteAgentInvolvementStore(_dbPath);

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        Assert.True(ColumnExists(raw, "agent_involvement", "agent_instance_id"));

        using var check = raw.CreateCommand();
        check.CommandText = "SELECT agent_instance_id FROM agent_involvement WHERE id = 'inv-legacy';";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public void WorkItemStore_OpensCleanly_AgainstPrePoolingSchema()
    {
        SeedPrePoolingWorkItems();

        using var store = new SqliteWorkItemStore(_dbPath);

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        Assert.True(ColumnExists(raw, "work_items", "agent_instance_id"));

        using var check = raw.CreateCommand();
        check.CommandText = "SELECT agent_instance_id FROM work_items WHERE id = 'wi-legacy';";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public void AgentPauseController_OpensCleanly_AgainstPrePoolingSchema()
    {
        SeedPrePoolingAgentPauseState();

        using var controller = new SqliteAgentPauseController(
            _dbPath,
            NullLogger<SqliteAgentPauseController>.Instance);

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        // The pause controller's legacy migration tears down the old PK and
        // rebuilds the table with pause_key + agent_instance_id; the existing
        // pause row is carried over with a NULL instance and the agent_kind
        // promoted into pause_key.
        Assert.True(ColumnExists(raw, "agent_pause_state", "pause_key"));
        Assert.True(ColumnExists(raw, "agent_pause_state", "agent_instance_id"));

        using var check = raw.CreateCommand();
        check.CommandText = "SELECT pause_key, agent_kind, agent_instance_id, paused FROM agent_pause_state;";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("claude", reader.GetString(0));
        Assert.Equal("claude", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(1, reader.GetInt32(3));
    }

    [Fact]
    public void AllStores_OpenSequentially_AgainstSinglePrePoolingDb()
    {
        // The orchestrator opens each of these stores during DI bring-up
        // against the same shared SQLite file. Verify the combined startup
        // sequence migrates every table without crashing — this is what the
        // 2026-06-08 redeploy hit before the rollback.
        SeedPrePoolingAgentUsageEvents();
        SeedPrePoolingWorkItems();
        SeedPrePoolingWorkItemCostsForExistingWorkItems();
        SeedPrePoolingAgentInvolvement();
        SeedPrePoolingAgentPauseState();

        using (var w = new SqliteWorkItemStore(_dbPath)) { }
        using (var c = new SqliteWorkItemCostStore(_dbPath)) { }
        using (var u = new SqliteAgentUsageStore(_dbPath)) { }
        using (var i = new SqliteAgentInvolvementStore(_dbPath)) { }
        using (var p = new SqliteAgentPauseController(
            _dbPath, NullLogger<SqliteAgentPauseController>.Instance)) { }

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        Assert.True(ColumnExists(raw, "agent_usage_events", "agent_instance_id"));
        Assert.True(ColumnExists(raw, "work_item_costs", "agent_instance_id"));
        Assert.True(ColumnExists(raw, "agent_involvement", "agent_instance_id"));
        Assert.True(ColumnExists(raw, "work_items", "agent_instance_id"));
        Assert.True(ColumnExists(raw, "agent_pause_state", "agent_instance_id"));
    }

    private void SeedPrePoolingWorkItemCostsForExistingWorkItems()
    {
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE work_item_costs (
                id                  TEXT PRIMARY KEY,
                work_item_id        TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                phase               TEXT NOT NULL,
                iteration           INTEGER,
                agent_kind          TEXT NOT NULL,
                model_id            TEXT,
                input_tokens        INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens       INTEGER NOT NULL,
                estimated_usd       REAL NOT NULL DEFAULT 0,
                started_at          TEXT NOT NULL,
                ended_at            TEXT NOT NULL,
                raw_metadata_json   TEXT NOT NULL DEFAULT '{}'
            );
            INSERT INTO work_item_costs
                (id, work_item_id, phase, iteration, agent_kind, model_id,
                 input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                 started_at, ended_at, raw_metadata_json)
            VALUES
                ('cost-shared', 'wi-legacy', 'work', 1, 'claude', 'm1',
                 1, 0, 1, 0.1, '2026-06-01T00:00:00Z', '2026-06-01T00:01:00Z', '{}');
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedPrePoolingAgentUsageEvents()
    {
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE agent_usage_events (
                id                  TEXT PRIMARY KEY,
                time_utc            TEXT NOT NULL,
                agent_kind          TEXT NOT NULL,
                model_id            TEXT,
                input_tokens        INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens       INTEGER NOT NULL,
                cost_microcents     INTEGER NOT NULL DEFAULT 0,
                work_item_id        TEXT
            );
            CREATE INDEX idx_usage_agent_model_time
                ON agent_usage_events(agent_kind, model_id, time_utc);
            CREATE INDEX idx_usage_time
                ON agent_usage_events(time_utc);
            INSERT INTO agent_usage_events
                (id, time_utc, agent_kind, model_id, input_tokens, cached_input_tokens, output_tokens, cost_microcents, work_item_id)
            VALUES
                ('legacy-row', '2026-06-01T00:00:00Z', 'claude', 'm1', 1, 0, 1, 100, 'wi-1');
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedPrePoolingWorkItemsAndCosts()
    {
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE work_items (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL DEFAULT '',
                state INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO work_items (id, project_id, state, updated_at)
            VALUES ('wi-1', 'proj', 0, '2026-06-01T00:00:00Z');
            CREATE TABLE work_item_costs (
                id                  TEXT PRIMARY KEY,
                work_item_id        TEXT NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                phase               TEXT NOT NULL,
                iteration           INTEGER,
                agent_kind          TEXT NOT NULL,
                model_id            TEXT,
                input_tokens        INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens       INTEGER NOT NULL,
                estimated_usd       REAL NOT NULL DEFAULT 0,
                started_at          TEXT NOT NULL,
                ended_at            TEXT NOT NULL,
                raw_metadata_json   TEXT NOT NULL DEFAULT '{}'
            );
            INSERT INTO work_item_costs
                (id, work_item_id, phase, iteration, agent_kind, model_id,
                 input_tokens, cached_input_tokens, output_tokens, estimated_usd,
                 started_at, ended_at, raw_metadata_json)
            VALUES
                ('cost-legacy', 'wi-1', 'work', 1, 'claude', 'm1',
                 1, 0, 1, 0.1, '2026-06-01T00:00:00Z', '2026-06-01T00:01:00Z', '{}');
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedPrePoolingAgentInvolvement()
    {
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE agent_involvement (
                id TEXT PRIMARY KEY,
                work_item_id TEXT NOT NULL,
                agent_kind TEXT NOT NULL,
                model_id TEXT,
                phase TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                iteration INTEGER,
                outcome TEXT
            );
            INSERT INTO agent_involvement
                (id, work_item_id, agent_kind, model_id, phase, started_at, ended_at, iteration, outcome)
            VALUES
                ('inv-legacy', 'wi-1', 'claude', 'm1', 'work', '2026-06-01T00:00:00Z', NULL, 1, NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedPrePoolingWorkItems()
    {
        // SqliteWorkItemStore manages many additive migrations beyond
        // agent_instance_id. The minimal table here only needs to be missing
        // the column under test; the store's own ALTER chain fills in any
        // other columns the rest of the constructor needs.
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE work_items (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                title TEXT NOT NULL,
                prompt TEXT NOT NULL,
                base_branch TEXT,
                work_branch TEXT,
                agent TEXT,
                work_timeout_ticks INTEGER NOT NULL,
                merge_timeout_ticks INTEGER NOT NULL,
                push_upstream INTEGER NOT NULL,
                state INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                upstream_push_attempts INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO work_items
                (id, project_id, title, prompt, work_timeout_ticks, merge_timeout_ticks,
                 push_upstream, state, created_at, updated_at)
            VALUES
                ('wi-legacy', 'proj', 't', 'p', 0, 0, 0, 0,
                 '2026-06-01T00:00:00Z', '2026-06-01T00:00:00Z');
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedPrePoolingAgentPauseState()
    {
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        // Original (#223) schema: agent_kind PK, no pause_key / agent_instance_id.
        cmd.CommandText = """
            CREATE TABLE agent_pause_state (
                agent_kind    TEXT PRIMARY KEY,
                paused        INTEGER NOT NULL DEFAULT 0,
                paused_at     TEXT,
                paused_reason TEXT,
                paused_by     TEXT,
                expires_at    TEXT,
                updated_at    TEXT NOT NULL
            );
            INSERT INTO agent_pause_state
                (agent_kind, paused, paused_at, paused_reason, paused_by, expires_at, updated_at)
            VALUES
                ('claude', 1, '2026-06-01T00:00:00Z', 'rate', 'operator', NULL, '2026-06-01T00:00:00Z');
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IndexExists(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", indexName);
        return cmd.ExecuteScalar() is not null;
    }
}
