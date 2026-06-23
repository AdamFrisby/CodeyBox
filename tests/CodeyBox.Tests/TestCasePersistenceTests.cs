using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class TestCasePersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _itemStore;
    private readonly SqliteTestCaseStore _store;

    public TestCasePersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-tc-test-{Guid.NewGuid():N}.db");
        // Create the item store first so the work_items table exists for FK references.
        _itemStore = new SqliteWorkItemStore(_dbPath);
        _store = new SqliteTestCaseStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _itemStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<string> SeedWorkItemAsync()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "Persistence test item",
            Prompt = "test",
        };
        await _itemStore.CreateAsync(item);
        return item.Id.ToString();
    }

    [Fact]
    public async Task RoundTrip_AllFieldsPreserved()
    {
        var wid = await SeedWorkItemAsync();
        var id = Guid.NewGuid().ToString("N");
        var tc = new TestCase
        {
            Id = id,
            Name = "E2E Replay Case",
            Description = "Verifies basic replay path works",
            SourceWorkItemId = wid,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            IsArchived = true,
            AutomationKind = AutomationKind.E2eReplay,
            ExecutableArtifactJson = "{\"steps\":[{\"action\":\"click\",\"selector\":\"#btn\"}]}",
            ConformanceJson = "{\"brokenBranch\":\"fix-1\",\"expectedOutcome\":\"Fail\"}",
            Label = "e2e-auth",
            LastRunPassed = true,
            LastRunAt = DateTimeOffset.UtcNow,
            LastRunResult = "Replay execution completed successfully in 1.2s."
        };

        await _store.CreateAsync(tc);

        var loaded = await _store.GetAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal(tc.Id, loaded.Id);
        Assert.Equal(tc.Name, loaded.Name);
        Assert.Equal(tc.Description, loaded.Description);
        Assert.Equal(tc.SourceWorkItemId, loaded.SourceWorkItemId);
        Assert.Equal(tc.IsArchived, loaded.IsArchived);
        Assert.Equal(tc.AutomationKind, loaded.AutomationKind);
        Assert.Equal(tc.ExecutableArtifactJson, loaded.ExecutableArtifactJson);
        Assert.Equal(tc.ConformanceJson, loaded.ConformanceJson);
        Assert.Equal(tc.Label, loaded.Label);
        Assert.Equal(tc.LastRunPassed, loaded.LastRunPassed);
        Assert.Equal(tc.LastRunResult, loaded.LastRunResult);
        // Round-trip via ISO 8601 ("O") is lossless; assert exact equality so a future format
        // change that drops precision regresses noisily.
        Assert.Equal(tc.CreatedAt, loaded.CreatedAt);
        Assert.Equal(tc.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal(tc.LastRunAt, loaded.LastRunAt);
    }

    [Fact]
    public async Task OptionalFields_Null_RoundTrip()
    {
        var wid = await SeedWorkItemAsync();
        var id = Guid.NewGuid().ToString("N");
        var tc = new TestCase
        {
            Id = id,
            Name = "Manual Case",
            Description = "Simple manual check",
            SourceWorkItemId = wid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsArchived = false,
            AutomationKind = null,
            ExecutableArtifactJson = null,
            ConformanceJson = null,
            Label = null,
            LastRunPassed = null,
            LastRunAt = null,
            LastRunResult = null
        };

        await _store.CreateAsync(tc);

        var loaded = await _store.GetAsync(id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.AutomationKind);
        Assert.Null(loaded.ExecutableArtifactJson);
        Assert.Null(loaded.ConformanceJson);
        Assert.Null(loaded.Label);
        Assert.Null(loaded.LastRunPassed);
        Assert.Null(loaded.LastRunAt);
        Assert.Null(loaded.LastRunResult);
    }

    [Fact]
    public async Task Update_MissingRow_ReturnsFalse()
    {
        var wid = await SeedWorkItemAsync();
        var ghost = new TestCase
        {
            Id = "never-existed",
            Name = "Ghost",
            Description = "",
            SourceWorkItemId = wid,
        };
        var ok = await _store.UpdateAsync(ghost);
        Assert.False(ok);
    }

    [Fact]
    public async Task Update_ExistingRow_ReturnsTrue()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-up-rc-1",
            Name = "n",
            Description = "",
            SourceWorkItemId = wid,
        };
        await _store.CreateAsync(tc);
        var ok = await _store.UpdateAsync(tc with { Name = "n2" });
        Assert.True(ok);
    }

    [Fact]
    public async Task Delete_MissingRow_ReturnsFalse()
    {
        var ok = await _store.DeleteAsync("never-existed");
        Assert.False(ok);
    }

    [Fact]
    public async Task Delete_ExistingRow_ReturnsTrue()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-del-rc-1",
            Name = "n",
            Description = "",
            SourceWorkItemId = wid,
        };
        await _store.CreateAsync(tc);
        var ok = await _store.DeleteAsync("tc-del-rc-1");
        Assert.True(ok);
    }

    [Fact]
    public async Task RoundTrip_AutomationKind_Integration()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-int-1",
            Name = "Integration Case",
            Description = "",
            SourceWorkItemId = wid,
            AutomationKind = AutomationKind.Integration,
        };
        await _store.CreateAsync(tc);
        var loaded = await _store.GetAsync("tc-int-1");
        Assert.NotNull(loaded);
        Assert.Equal(AutomationKind.Integration, loaded.AutomationKind);
    }

    [Fact]
    public async Task Read_UnknownAutomationKind_FallsBackToNullInsteadOfThrowing()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase
        {
            Id = "tc-unknown-kind",
            Name = "n",
            Description = "",
            SourceWorkItemId = wid,
        };
        await _store.CreateAsync(tc);

        // Forge a value the running enum doesn't know about — simulating either a forward-compat
        // row written by a newer version or a corrupted row. Read must not throw.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE test_cases SET automation_kind='SomeFutureKind' WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", "tc-unknown-kind");
            await cmd.ExecuteNonQueryAsync();
        }

        var loaded = await _store.GetAsync("tc-unknown-kind");
        Assert.NotNull(loaded);
        Assert.Null(loaded.AutomationKind);
    }

    [Fact]
    public async Task BulkCreate_DuplicateIdMidBatch_RollsBackEntireBatch()
    {
        var wid = await SeedWorkItemAsync();
        // Seed an existing row whose Id one of the bulk items will collide with.
        await _store.CreateAsync(new TestCase
        {
            Id = "tc-collide",
            Name = "Pre-existing",
            Description = "",
            SourceWorkItemId = wid,
        });

        var batch = new List<TestCase>
        {
            new() { Id = "tc-bulk-rollback-1", Name = "A", Description = "", SourceWorkItemId = wid },
            new() { Id = "tc-bulk-rollback-2", Name = "B", Description = "", SourceWorkItemId = wid },
            new() { Id = "tc-collide", Name = "Conflict", Description = "", SourceWorkItemId = wid },
        };

        await Assert.ThrowsAsync<SqliteException>(() => _store.BulkCreateAsync(batch));

        // Items 1 and 2 must NOT have landed — the whole batch is rolled back.
        Assert.Null(await _store.GetAsync("tc-bulk-rollback-1"));
        Assert.Null(await _store.GetAsync("tc-bulk-rollback-2"));
    }

    [Fact]
    public async Task Update_ModifiesPersistedValues()
    {
        var wid = await SeedWorkItemAsync();
        var id = Guid.NewGuid().ToString("N");
        var tc = new TestCase
        {
            Id = id,
            Name = "Initial Name",
            Description = "Initial Description",
            SourceWorkItemId = wid,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsArchived = false,
            AutomationKind = AutomationKind.Manual
        };

        await _store.CreateAsync(tc);

        var updated = tc with
        {
            Name = "Updated Name",
            Description = "Updated Description",
            IsArchived = true,
            AutomationKind = AutomationKind.Unit,
            Label = "unit-test",
            LastRunPassed = false,
            LastRunAt = DateTimeOffset.UtcNow,
            LastRunResult = "Failed due to division by zero."
        };

        await _store.UpdateAsync(updated);

        var loaded = await _store.GetAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal("Updated Name", loaded.Name);
        Assert.Equal("Updated Description", loaded.Description);
        Assert.True(loaded.IsArchived);
        Assert.Equal(AutomationKind.Unit, loaded.AutomationKind);
        Assert.Equal("unit-test", loaded.Label);
        Assert.False(loaded.LastRunPassed);
        Assert.Equal("Failed due to division by zero.", loaded.LastRunResult);
    }

    [Fact]
    public async Task BulkCreate_SavesAllAtomically()
    {
        var wid = await SeedWorkItemAsync();
        var list = new List<TestCase>
        {
            new() { Id = "tc-1", Name = "Case 1", Description = "Desc 1", SourceWorkItemId = wid },
            new() { Id = "tc-2", Name = "Case 2", Description = "Desc 2", SourceWorkItemId = wid },
            new() { Id = "tc-3", Name = "Case 3", Description = "Desc 3", SourceWorkItemId = wid }
        };

        await _store.BulkCreateAsync(list);

        var loaded1 = await _store.GetAsync("tc-1");
        var loaded2 = await _store.GetAsync("tc-2");
        var loaded3 = await _store.GetAsync("tc-3");

        Assert.NotNull(loaded1);
        Assert.NotNull(loaded2);
        Assert.NotNull(loaded3);
        Assert.Equal("Case 1", loaded1.Name);
        Assert.Equal("Case 2", loaded2.Name);
        Assert.Equal("Case 3", loaded3.Name);
    }

    [Fact]
    public async Task ListByWorkItem_ReturnsCorrectCases()
    {
        var widA = await SeedWorkItemAsync();
        var widB = await SeedWorkItemAsync();

        var tc1 = new TestCase { Id = "tc-101", Name = "Case 1", Description = "", SourceWorkItemId = widA };
        var tc2 = new TestCase { Id = "tc-102", Name = "Case 2", Description = "", SourceWorkItemId = widA };
        var tc3 = new TestCase { Id = "tc-103", Name = "Case 3", Description = "", SourceWorkItemId = widB };

        await _store.CreateAsync(tc1);
        await _store.CreateAsync(tc2);
        await _store.CreateAsync(tc3);

        var listA = new List<TestCase>();
        await foreach (var tc in _store.ListByWorkItemAsync(widA))
            listA.Add(tc);

        var listB = new List<TestCase>();
        await foreach (var tc in _store.ListByWorkItemAsync(widB))
            listB.Add(tc);

        Assert.Equal(2, listA.Count);
        Assert.Contains(listA, x => x.Id == "tc-101");
        Assert.Contains(listA, x => x.Id == "tc-102");

        Assert.Single(listB);
        Assert.Equal("tc-103", listB[0].Id);
    }

    [Fact]
    public async Task Delete_RemovesFromStore()
    {
        var wid = await SeedWorkItemAsync();
        var tc = new TestCase { Id = "tc-to-del", Name = "To Delete", Description = "", SourceWorkItemId = wid };
        await _store.CreateAsync(tc);

        var loadedBefore = await _store.GetAsync("tc-to-del");
        Assert.NotNull(loadedBefore);

        await _store.DeleteAsync("tc-to-del");

        var loadedAfter = await _store.GetAsync("tc-to-del");
        Assert.Null(loadedAfter);
    }

    [Fact]
    public async Task CascadeDelete_WorkItemDeleted_TestCasesCascade()
    {
        var wid = await SeedWorkItemAsync();
        var tc1 = new TestCase { Id = "tc-cas-1", Name = "Cascade 1", Description = "", SourceWorkItemId = wid };
        var tc2 = new TestCase { Id = "tc-cas-2", Name = "Cascade 2", Description = "", SourceWorkItemId = wid };

        await _store.CreateAsync(tc1);
        await _store.CreateAsync(tc2);

        // Delete the work item from the work_items store
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM work_items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", wid);
            var rows = await cmd.ExecuteNonQueryAsync();
            Assert.Equal(1, rows);
        }

        // Check if the linked test cases were cascade deleted automatically.
        var loaded1 = await _store.GetAsync("tc-cas-1");
        var loaded2 = await _store.GetAsync("tc-cas-2");

        Assert.Null(loaded1);
        Assert.Null(loaded2);
    }

    [Fact]
    public async Task Migration_AppliesToExistingDb()
    {
        var migrationDbPath = Path.Combine(Path.GetTempPath(), $"codeybox-tc-mig-test-{Guid.NewGuid():N}.db");
        try
        {
            // 1. Setup an existing database that has only the work_items table, but NO test_cases table.
            using (var conn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE work_items (
                        id TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        prompt TEXT NOT NULL,
                        base_branch TEXT,
                        work_branch TEXT,
                        agent TEXT,
                        agent_instance_id TEXT,
                        work_timeout_ticks INTEGER NOT NULL,
                        merge_timeout_ticks INTEGER NOT NULL,
                        push_upstream INTEGER NOT NULL,
                        state INTEGER NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        last_error TEXT
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // 2. Seed a work item row so the FK on test_cases.source_work_item_id is satisfied.
            using (var conn = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO work_items (id, project_id, title, prompt, work_timeout_ticks, merge_timeout_ticks, push_upstream, state, created_at, updated_at)
                    VALUES ('some-wid', 'test-proj', 'Mig Item', 'test', 0, 0, 0, 0, '2026-06-10T12:00:00Z', '2026-06-10T12:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            // 3. Instantiating SqliteTestCaseStore on the pre-existing DB must run the additive
            // migration (CREATE TABLE IF NOT EXISTS test_cases + indexes) without error, and the
            // table must be writable + readable immediately afterwards.
            using (var store = new SqliteTestCaseStore(migrationDbPath))
            {
                var tc = new TestCase
                {
                    Id = "tc-mig-1",
                    Name = "Migration Case",
                    Description = "Verify migration works",
                    SourceWorkItemId = "some-wid",
                };

                await store.CreateAsync(tc);
                var loaded = await store.GetAsync("tc-mig-1");
                Assert.NotNull(loaded);
                Assert.Equal("Migration Case", loaded.Name);

                // The three documented indexes must have been created by the migration.
                using var conn = new SqliteConnection($"Data Source={migrationDbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='test_cases';";
                using var reader = cmd.ExecuteReader();
                var indexes = new List<string>();
                while (reader.Read()) indexes.Add(reader.GetString(0));
                Assert.Contains("idx_test_cases_work_item", indexes);
                Assert.Contains("idx_test_cases_label", indexes);
                Assert.Contains("idx_test_cases_archived", indexes);
            }
        }
        finally
        {
            try { File.Delete(migrationDbPath); } catch { }
        }
    }
}
