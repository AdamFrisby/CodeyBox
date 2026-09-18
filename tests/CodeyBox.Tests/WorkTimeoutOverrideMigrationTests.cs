using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// Migration coverage for the <c>work_timeout_override_ticks</c> column that
/// split the persisted per-item timeout into "explicit override" (NULL means
/// inherit project/global). A pre-change database must open cleanly and keep
/// behaving exactly as before: every legacy row's baked value becomes an
/// explicit override, while rows created afterwards with no timeout store NULL
/// and follow configuration at dispatch.
/// </summary>
public sealed class WorkTimeoutOverrideMigrationTests : IDisposable
{
    // Fixed ids so the raw-SQL seed and the store reads address the same rows.
    private const string TunedId = "11111111-1111-1111-1111-111111111111";
    private const string BakedId = "22222222-2222-2222-2222-222222222222";
    private const string InheritId = "33333333-3333-3333-3333-333333333333";
    private const string ExplicitId = "44444444-4444-4444-4444-444444444444";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-wto-migrate-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task LegacyDatabase_OpensCleanly_AndPreservesExplicitBudgets()
    {
        SeedLegacyWorkItems();

        using var store = new SqliteWorkItemStore(_dbPath);

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        Assert.True(ColumnExists(raw, "work_items", "work_timeout_override_ticks"));

        // The explicit operator-set budget survives as an explicit override.
        var tuned = await store.GetAsync(WorkItemId.Parse(TunedId));
        Assert.NotNull(tuned);
        Assert.Equal(TimeSpan.FromMinutes(60), tuned!.WorkTimeout);

        // A row that baked the old shipped default stays pinned to it rather
        // than silently becoming "inherit" — pre-existing data behaves as before.
        var baked = await store.GetAsync(WorkItemId.Parse(BakedId));
        Assert.NotNull(baked);
        Assert.Equal(TimeSpan.FromMinutes(240), baked!.WorkTimeout);

        using var check = raw.CreateCommand();
        check.CommandText = "SELECT work_timeout_override_ticks FROM work_items WHERE id = $id;";
        check.Parameters.AddWithValue("$id", WorkItemId.Parse(TunedId).ToString());
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(TimeSpan.FromMinutes(60).Ticks, reader.GetInt64(0));
    }

    [Fact]
    public async Task NewRows_NullTimeout_RoundTripsAsInherit()
    {
        using var store = new SqliteWorkItemStore(_dbPath);

        var item = MinimalItem(InheritId) with { WorkTimeout = null };
        await store.CreateAsync(item);

        var readBack = await store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Null(readBack!.WorkTimeout);
    }

    [Fact]
    public async Task NewRows_ExplicitTimeout_RoundTrips()
    {
        using var store = new SqliteWorkItemStore(_dbPath);

        var item = MinimalItem(ExplicitId) with { WorkTimeout = TimeSpan.FromMinutes(123) };
        await store.CreateAsync(item);

        var readBack = await store.GetAsync(item.Id);
        Assert.NotNull(readBack);
        Assert.Equal(TimeSpan.FromMinutes(123), readBack!.WorkTimeout);
    }

    [Fact]
    public async Task Reopen_DoesNotClobberInheritRows()
    {
        // Guards the back-fill: it runs once when the column is added and must
        // never re-copy the legacy compat value over a NULL (inherit) override.
        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            await store.CreateAsync(MinimalItem(InheritId) with { WorkTimeout = null });
            await store.CreateAsync(MinimalItem(ExplicitId) with { WorkTimeout = TimeSpan.FromMinutes(60) });
        }

        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            Assert.Null((await store.GetAsync(WorkItemId.Parse(InheritId)))!.WorkTimeout);
            Assert.Equal(
                TimeSpan.FromMinutes(60),
                (await store.GetAsync(WorkItemId.Parse(ExplicitId)))!.WorkTimeout);
        }

        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            Assert.Null((await store.GetAsync(WorkItemId.Parse(InheritId)))!.WorkTimeout);
        }
    }

    [Fact]
    public async Task LegacyCompatColumn_KeepsSaneValueForInheritRows()
    {
        // Direct DB readers (and older binaries) see the shipped default for
        // inherit rows rather than a NULL they cannot interpret.
        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            await store.CreateAsync(MinimalItem(InheritId) with { WorkTimeout = null });
        }

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var check = raw.CreateCommand();
        check.CommandText = "SELECT work_timeout_ticks, work_timeout_override_ticks FROM work_items WHERE id = $id;";
        check.Parameters.AddWithValue("$id", WorkItemId.Parse(InheritId).ToString());
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(TimeSpan.FromMinutes(240).Ticks, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
    }

    private void SeedLegacyWorkItems()
    {
        // Exact pre-change shape: no override column. The store's own
        // migration chain fills in every other column it needs.
        // Tick literals: 60 min = 36,000,000,000; 240 min = 144,000,000,000;
        // 15 min merge = 9,000,000,000.
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
                ($tuned, 'proj', 't', 'p', 36000000000, 9000000000, 0, 1,
                 '2026-06-01T00:00:00Z', '2026-06-01T00:00:00Z'),
                ($baked, 'proj', 't', 'p', 144000000000, 9000000000, 0, 1,
                 '2026-06-01T00:00:00Z', '2026-06-01T00:00:00Z');
            """;
        cmd.Parameters.AddWithValue("$tuned", WorkItemId.Parse(TunedId).ToString());
        cmd.Parameters.AddWithValue("$baked", WorkItemId.Parse(BakedId).ToString());
        cmd.ExecuteNonQuery();
    }

    private static WorkItem MinimalItem(string id) => new()
    {
        Id = WorkItemId.Parse(id),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        PushUpstream = false,
    };

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
}
