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
        Assert.True(Math.Abs((tc.CreatedAt - loaded.CreatedAt).TotalSeconds) < 1);
        Assert.True(Math.Abs((tc.UpdatedAt - loaded.UpdatedAt).TotalSeconds) < 1);
        Assert.True(tc.LastRunAt.HasValue && loaded.LastRunAt.HasValue && Math.Abs((tc.LastRunAt.Value - loaded.LastRunAt.Value).TotalSeconds) < 1);
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

            // 2. Instantiate SqliteTestCaseStore on the existing DB file.
            // This should run the Migration logic (CREATE TABLE IF NOT EXISTS test_cases, indexes, etc.).
            using (var store = new SqliteTestCaseStore(migrationDbPath))
            {
                // 3. Verify that we can write to and read from the test_cases table without error.
                var tc = new TestCase
                {
                    Id = "tc-mig-1",
                    Name = "Migration Case",
                    Description = "Verify migration works",
                    SourceWorkItemId = "some-wid", // FK check is ON but since it's not referenced by work_items in this isolated test, wait:
                    // Wait, we turned foreign keys ON, so referencing a non-existent work item might fail?
                    // Let's seed a work item in this database to be safe.
                };

                // Let's seed a work item in the migration DB first.
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

                await store.CreateAsync(tc);
                var loaded = await store.GetAsync("tc-mig-1");
                Assert.NotNull(loaded);
                Assert.Equal("Migration Case", loaded.Name);
            }
        }
        finally
        {
            try { File.Delete(migrationDbPath); } catch { }
        }
    }
}
