using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the one-shot back-fill that copies <c>work_items.external_id</c>
/// into <c>work_item_external_ids</c> under namespace <c>legacy</c> during the
/// migration to namespaced external IDs (acceptance criterion #4: tests for
/// the migration with pre-migration row count == post-migration
/// <c>(namespace='legacy')</c> row count).
/// </summary>
public sealed class SqliteWorkItemStoreMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-migrate-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best-effort */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Backfill_CopiesLegacyExternalIdColumn_IntoSideTable_UnderLegacyNamespace()
    {
        // Phase 1: open the store, create work items with the legacy singular
        // external_id populated (via the 'legacy' namespace, which mirrors into
        // the work_items.external_id column).
        var ids = new List<string>();
        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            for (var i = 0; i < 5; i++)
            {
                var item = new WorkItem
                {
                    Id = WorkItemId.New(),
                    ProjectId = new ProjectId("proj"),
                    Title = $"t{i}",
                    Prompt = "p",
                    ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["legacy"] = $"LEGACY-{i}",
                    },
                };
                await store.CreateAsync(item);
                ids.Add(item.Id.ToString());
            }
        }

        // Phase 2: simulate the pre-migration state by deleting all rows from
        // the side table while leaving work_items.external_id intact. Pre- and
        // post-conditions of the migration we're testing:
        //   pre:  work_items.external_id has N non-null rows;
        //         work_item_external_ids has 0 rows.
        //   post: work_item_external_ids has N rows where namespace='legacy'.
        int preLegacyCount;
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var del = raw.CreateCommand();
            del.CommandText = "DELETE FROM work_item_external_ids;";
            del.ExecuteNonQuery();

            using var pre = raw.CreateCommand();
            pre.CommandText = "SELECT COUNT(*) FROM work_items WHERE external_id IS NOT NULL;";
            preLegacyCount = Convert.ToInt32(pre.ExecuteScalar());
        }
        Assert.Equal(5, preLegacyCount);

        // Phase 3: re-open the store. The constructor runs the back-fill
        // INSERT OR IGNORE which copies every non-null work_items.external_id
        // into the side table under namespace 'legacy'.
        using (var _ = new SqliteWorkItemStore(_dbPath)) { /* triggers migration */ }

        // Verify: side table row count under 'legacy' equals the pre-migration
        // count, and each (work_item_id, external_id) pair round-trips.
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var count = raw.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM work_item_external_ids WHERE namespace = 'legacy';";
            var postLegacyCount = Convert.ToInt32(count.ExecuteScalar());
            Assert.Equal(preLegacyCount, postLegacyCount);

            using var rows = raw.CreateCommand();
            rows.CommandText = "SELECT work_item_id, project_id, external_id FROM work_item_external_ids WHERE namespace = 'legacy' ORDER BY external_id;";
            using var reader = rows.ExecuteReader();
            var seen = new HashSet<string>();
            while (reader.Read())
            {
                var wid = reader.GetString(0);
                var pid = reader.GetString(1);
                var eid = reader.GetString(2);
                Assert.Equal("proj", pid);
                Assert.StartsWith("LEGACY-", eid);
                Assert.Contains(wid, ids);
                seen.Add(wid);
            }
            Assert.Equal(ids.Count, seen.Count);
        }
    }

    [Fact]
    public async Task Backfill_IsIdempotent_OnRepeatedOpen()
    {
        // The migration uses INSERT OR IGNORE so re-running it after the side
        // table has already been populated must be a no-op (no duplicate rows
        // and no UNIQUE-constraint failures).
        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("proj"),
                Title = "t",
                Prompt = "p",
                ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["legacy"] = "X-1",
                },
            };
            await store.CreateAsync(item);
        }

        // Re-open the store three times; back-fill should not duplicate rows.
        using (var _ = new SqliteWorkItemStore(_dbPath)) { }
        using (var _ = new SqliteWorkItemStore(_dbPath)) { }
        using (var _ = new SqliteWorkItemStore(_dbPath)) { }

        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using var count = raw.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM work_item_external_ids WHERE namespace = 'legacy';";
        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()));
    }

    [Fact]
    public async Task Backfill_SkipsItemsWithNullExternalIdColumn()
    {
        // Items that never had a legacy external_id must NOT produce a side-table row.
        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            var withExt = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("proj"),
                Title = "t1",
                Prompt = "p",
                ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["legacy"] = "Y-1" },
            };
            var withoutExt = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("proj"),
                Title = "t2",
                Prompt = "p",
            };
            await store.CreateAsync(withExt);
            await store.CreateAsync(withoutExt);
        }

        // Wipe the side table and re-open; only the legacy item should be backfilled.
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var del = raw.CreateCommand();
            del.CommandText = "DELETE FROM work_item_external_ids;";
            del.ExecuteNonQuery();
        }
        using (var _ = new SqliteWorkItemStore(_dbPath)) { }

        using var raw2 = new SqliteConnection($"Data Source={_dbPath}");
        raw2.Open();
        using var count = raw2.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM work_item_external_ids;";
        Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()));
    }

    [Fact]
    public async Task OriginCheckUniqueIndexMigration_ClearsDuplicateBacklinksBeforeCreatingIndex()
    {
        var originCheckId = WorkItemId.New();
        var first = Sample() with
        {
            Title = "first follow-up",
            OriginCheckWorkItemId = originCheckId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        var second = Sample() with
        {
            Title = "second follow-up",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };

        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            await store.CreateAsync(first);
            await store.CreateAsync(second);
        }

        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                DROP INDEX idx_work_items_origin_check_unique;
                UPDATE work_items
                SET origin_check_work_item_id = $origin
                WHERE id = $second;
                """;
            cmd.Parameters.AddWithValue("$origin", originCheckId.ToString());
            cmd.Parameters.AddWithValue("$second", second.Id.ToString());
            cmd.ExecuteNonQuery();
        }

        using (var reopened = new SqliteWorkItemStore(_dbPath))
        {
            var firstRead = await reopened.GetAsync(first.Id);
            var secondRead = await reopened.GetAsync(second.Id);

            Assert.NotNull(firstRead);
            Assert.NotNull(secondRead);
            Assert.Null(firstRead!.OriginCheckWorkItemId);
            Assert.Equal(originCheckId, secondRead!.OriginCheckWorkItemId);

            await Assert.ThrowsAsync<WorkItemOriginCheckConflictException>(() =>
                reopened.CreateAsync(Sample() with { OriginCheckWorkItemId = originCheckId }));
        }
    }

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
    };
}
