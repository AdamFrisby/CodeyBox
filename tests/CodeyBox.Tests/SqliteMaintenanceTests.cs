using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the freelist bound: deleting rows leaves reclaimable pages, and
/// the maintenance service reclaims them with a VACUUM once the configured
/// threshold is reached — while leaving live rows untouched.
/// </summary>
[Collection("Background service timing")]
public sealed class SqliteMaintenanceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-maint-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static SqliteDatabaseMaintenanceService BuildService(string dbPath) =>
        new(
            dbPath,
            static () => new SqliteMaintenanceOptions(),
            new SqliteDatabaseWriteGateFactory(
                static () => new SqliteWriteGateOptions
                {
                    AcquisitionTimeout = TimeSpan.FromSeconds(5),
                    MaxHoldDuration = TimeSpan.FromSeconds(30),
                },
                NullLoggerFactory.Instance),
            NullLogger<SqliteDatabaseMaintenanceService>.Instance,
            TimeProvider.System);

    private static long FreelistPages(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA freelist_count;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public async Task Maintenance_VacuumsFreelistWhenThresholdExceeded_AndKeepsLiveRows()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var keeper = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "keeper",
            Prompt = "p",
            State = WorkItemState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.CreateAsync(keeper);

        // Audit-progress rows carry large JSON payloads; recording then
        // purging them leaves reclaimable freelist pages behind through the
        // public store API (work items themselves are never deleted).
        var attempt = DateTimeOffset.UtcNow;
        for (var i = 0; i < 50; i++)
        {
            await store.RecordAuditProgressAsync(
                keeper.Id,
                attempt,
                new AuditProgressRecord(
                    Iteration: i + 1,
                    MaxIterations: 50,
                    BlockingFindings: 1,
                    NonBlockingFindings: 0,
                    BlockingFindingIds: [$"finding-{i}"],
                    BlockingFindingsDetails:
                    [
                        new AuditProgressFinding(
                            "auditor",
                            AuditSeverity.Error,
                            $"finding {i}",
                            new string('x', 4096)),
                    ],
                    Findings: [],
                    WorkBranchTip: null,
                    Status: AuditProgressStatuses.InProgress,
                    ScheduledAuditors: ["auditor"],
                    CompletedAuditors: []),
                DateTimeOffset.UtcNow);
        }
        Assert.Equal(50, await store.PurgeAuditProgressAsync(keeper.Id, attempt));

        Assert.True(FreelistPages(_dbPath) > 0);

        var service = BuildService(_dbPath);
        var outcome = await service.RunMaintenanceOnceAsync(
            new SqliteMaintenanceOptions { FreelistPageThreshold = 1 },
            CancellationToken.None);

        Assert.Equal(
            SqliteDatabaseMaintenanceService.MaintenanceOutcome.Vacuumed,
            outcome);
        Assert.Equal(0, FreelistPages(_dbPath));
        Assert.NotNull(await store.GetAsync(keeper.Id));
    }

    [Fact]
    public async Task Maintenance_SkipsVacuumWhenBelowThreshold()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        await store.CreateAsync(new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var service = BuildService(_dbPath);
        var outcome = await service.RunMaintenanceOnceAsync(
            new SqliteMaintenanceOptions { FreelistPageThreshold = long.MaxValue },
            CancellationToken.None);

        Assert.Equal(
            SqliteDatabaseMaintenanceService.MaintenanceOutcome.NoAction,
            outcome);
    }
}
