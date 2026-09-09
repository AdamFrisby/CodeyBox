using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Data.Sqlite;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the additive surrogate-key migration on <c>work_item_audit_progress</c>: rows written
/// before the <c>id</c> column existed are backfilled with the deterministic id on the next open,
/// and become addressable by it.
/// </summary>
public sealed class AuditProgressBackfillMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-apmig-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
    };

    [Fact]
    public async Task Reopen_BackfillsSurrogateIdForRowsWrittenBeforeTheColumnExisted()
    {
        var item = Sample();
        var attempt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expectedId = SqliteWorkItemStore.ComputeAuditProgressId(
            item.Id.ToString(), attempt.ToString("O"), 4);

        // Write a progress row through the normal path.
        using (var store = new SqliteWorkItemStore(_dbPath))
        {
            await store.CreateAsync(item);
            await store.RecordAuditProgressAsync(
                item.Id,
                attempt,
                new AuditProgressRecord(
                    Iteration: 4,
                    MaxIterations: 6,
                    BlockingFindings: 0,
                    NonBlockingFindings: 0,
                    BlockingFindingIds: [],
                    BlockingFindingsDetails: [],
                    Findings: [],
                    WorkBranchTip: null),
                DateTimeOffset.UtcNow);
        }

        // Simulate a legacy row (written before the surrogate key existed) by clearing its id.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            using var clear = conn.CreateCommand();
            clear.CommandText = "UPDATE work_item_audit_progress SET id = NULL;";
            Assert.Equal(1, await clear.ExecuteNonQueryAsync());
        }

        // Reopening the store runs the idempotent backfill migration.
        using (var reopened = new SqliteWorkItemStore(_dbPath))
        {
            var row = Assert.Single(await reopened.GetAllAuditProgressForWorkItemAsync(item.Id));
            Assert.Equal(expectedId, row.Id);

            // The backfilled row is addressable by its surrogate id.
            var byId = await reopened.GetAuditProgressByIdAsync(item.Id, expectedId);
            Assert.NotNull(byId);
            Assert.Equal(4, byId!.Progress.Iteration);
        }
    }
}
