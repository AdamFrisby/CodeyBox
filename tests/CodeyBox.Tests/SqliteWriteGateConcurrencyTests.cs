using System.Net;
using System.Net.Http.Json;
using CodeyBox.Agents;
using Microsoft.Data.Sqlite;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteWriteGateConcurrencyTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-write-gate-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task ConcurrentStateWritersAndPostWorkitems_WaitAndCompleteWhileUsagePruneRuns()
    {
        using var factory = new WorkItemApiFactory(_dbPath);
        using var client = factory.CreateClient();
        using var registry = new SqliteWorkerRegistry(_dbPath);
        using var timing = new SqliteTimingStore(_dbPath);
        using var usage = new SqliteAgentUsageStore(_dbPath);
        using var costs = new SqliteWorkItemCostStore(_dbPath);
        using var streamSummaries = new SqliteAgentStreamSummaryStore(_dbPath);

        var timedItem = MakeWorkItem("timed");
        await factory.Store.CreateAsync(timedItem);
        var streamRow = MakeStreamSummary(timedItem.Id);

        const int workerCount = 4;
        var workerIds = new List<string>();
        for (var i = 0; i < workerCount; i++)
        {
            var workerId = $"worker-{i}";
            workerIds.Add(workerId);
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = workerId,
                HostName = "test-host",
                ProcessId = 1000 + i,
                StartedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            });
        }

        const int usageRows = 650;
        var oldUsageTime = DateTimeOffset.UtcNow.AddDays(-120);
        for (var i = 0; i < usageRows; i++)
        {
            await usage.RecordAsync(new AgentUsageEvent
            {
                Id = $"usage-{i}",
                TimeUtc = oldUsageTime.AddMilliseconds(i),
                AgentKind = "codex",
                InputTokens = 10,
                OutputTokens = 5,
                CostMicroCents = 1,
                WorkItemId = timedItem.Id.ToString(),
            });
        }

        var maintenance = await LongMaintenanceWriter.OpenAsync(_dbPath);

        var heartbeatBefore = DateTimeOffset.UtcNow;
        var postTasks = Enumerable.Range(0, 8)
            .Select(i => client.PostAsJsonAsync("/workitems", new
            {
                projectId = "test-project",
                title = $"concurrent-{i}",
                prompt = "p",
            }))
            .ToArray();

        var heartbeatTasks = workerIds
            .Select((workerId, i) => registry.HeartbeatAsync(workerId, $"work-{i}"))
            .ToArray();

        var timingTasks = Enumerable.Range(0, 8)
            .Select(async i =>
            {
                var id = $"timing-{i}";
                await timing.BeginAsync(new TimingRecord
                {
                    Id = id,
                    WorkItemId = timedItem.Id,
                    Phase = "work",
                    Iteration = i,
                    Step = "step",
                    StartedAt = DateTimeOffset.UtcNow,
                });
                await timing.EndAsync(id, DateTimeOffset.UtcNow, durationMs: 1);
            })
            .ToArray();

        var pruneTask = usage.PruneAsync(DateTimeOffset.UtcNow.AddDays(-90));
        var streamSummaryTask = streamSummaries.UpsertAsync(streamRow);
        var reconcileTask = costs.ReconcileFromAgentStreamSummaryAsync(streamRow);
        var allWrites = postTasks
            .Select(t => (Task)t)
            .Concat(heartbeatTasks)
            .Concat(timingTasks)
            .Append(pruneTask)
            .Append(streamSummaryTask)
            .Append(reconcileTask)
            .ToArray();

        try
        {
            await AssertUngatedWriterBlockedBySqliteLockAsync(_dbPath);

            await Task.Delay(100);
            Assert.All(
                allWrites,
                t => Assert.False(t.IsCompleted, "Writers should queue behind the shared write gate while maintenance owns it."));
            Assert.DoesNotContain(allWrites, t => t.IsFaulted);

            await maintenance.ReleaseSqliteWriteLockAsync();
            await AssertUngatedWriterCanWriteWhileGateHeldAsync(_dbPath);
            await Task.Delay(250);
            Assert.All(
                allWrites,
                t => Assert.False(
                    t.IsCompleted,
                    "A writer completed after the raw SQLite lock was released while the shared write gate was still held, which indicates it bypassed the gate."));
        }
        finally
        {
            await maintenance.DisposeAsync();
        }

        var responses = await Task.WhenAll(postTasks).WaitAsync(TimeSpan.FromSeconds(15));
        await Task.WhenAll(heartbeatTasks.Concat(timingTasks).Append(streamSummaryTask).Append(reconcileTask)).WaitAsync(TimeSpan.FromSeconds(15));
        var pruned = await pruneTask.WaitAsync(TimeSpan.FromSeconds(15));

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }

        var workers = await registry.ListAsync();
        Assert.All(workers, worker => Assert.True(worker.LastHeartbeatAt >= heartbeatBefore));
        Assert.All(workers, worker => Assert.StartsWith("work-", worker.CurrentWorkItemId));
        Assert.Equal(8, (await timing.GetByWorkItemAsync(timedItem.Id)).Count);
        Assert.Equal(usageRows, pruned);
        Assert.Single(await streamSummaries.GetByWorkItemAsync(timedItem.Id));
        var costRow = Assert.Single(await costs.GetByWorkItemAsync(timedItem.Id.ToString()));
        Assert.Equal("work", costRow.Phase);
        Assert.Equal(100, costRow.InputTokens);
        Assert.Equal(20, costRow.CachedInputTokens);
        Assert.Equal(50, costRow.OutputTokens);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static WorkItem MakeWorkItem(string title) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = title,
        Prompt = "p",
    };

    private static AgentStreamSummaryRow MakeStreamSummary(WorkItemId id) => new(
        id,
        "work-codex.jsonl",
        "work",
        null,
        AgentKind.Codex,
        new AgentStreamSummary(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(250),
            100,
            50,
            20,
            0.42m,
            [],
            [],
            "done"),
        DateTimeOffset.UtcNow);

    private static async Task AssertUngatedWriterBlockedBySqliteLockAsync(string dbPath)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=50;";
            await pragma.ExecuteNonQueryAsync();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 1;
        cmd.CommandText = """
            INSERT INTO write_gate_maintenance_lock (id, touched_at)
            VALUES (1, $touched_at)
            ON CONFLICT(id) DO UPDATE SET touched_at = excluded.touched_at;
            """;
        cmd.Parameters.AddWithValue("$touched_at", DateTimeOffset.UtcNow.ToString("O"));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        Assert.True(
            ex.SqliteErrorCode is 5 or 6,
            $"expected SQLITE_BUSY/SQLITE_LOCKED from an ungated writer, got {ex.SqliteErrorCode}");
    }

    private static async Task AssertUngatedWriterCanWriteWhileGateHeldAsync(string dbPath)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=50;";
            await pragma.ExecuteNonQueryAsync();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO write_gate_maintenance_lock (id, touched_at)
            VALUES (2, $touched_at)
            ON CONFLICT(id) DO UPDATE SET touched_at = excluded.touched_at;
            """;
        cmd.Parameters.AddWithValue("$touched_at", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    private sealed class LongMaintenanceWriter : IAsyncDisposable
    {
        private readonly SqliteDatabaseWriteGate _gate;
        private readonly SqliteConnection _conn;
        private int _sqliteLockReleased;
        private int _released;

        private LongMaintenanceWriter(SqliteDatabaseWriteGate gate, SqliteConnection conn)
        {
            _gate = gate;
            _conn = conn;
        }

        public static async Task<LongMaintenanceWriter> OpenAsync(string dbPath)
        {
            var gate = SqliteDatabaseWriteGate.ForPath(dbPath);
            gate.Wait();
            var conn = new SqliteConnection($"Data Source={dbPath}");

            try
            {
                await conn.OpenAsync();

                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                    await pragma.ExecuteNonQueryAsync();
                }

                using (var create = conn.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE IF NOT EXISTS write_gate_maintenance_lock (
                            id INTEGER PRIMARY KEY,
                            touched_at TEXT NOT NULL
                        );
                        """;
                    await create.ExecuteNonQueryAsync();
                }

                using (var begin = conn.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE;";
                    await begin.ExecuteNonQueryAsync();
                }

                using var touch = conn.CreateCommand();
                touch.CommandText = """
                    INSERT INTO write_gate_maintenance_lock (id, touched_at)
                    VALUES (1, $touched_at)
                    ON CONFLICT(id) DO UPDATE SET touched_at = excluded.touched_at;
                    """;
                touch.Parameters.AddWithValue("$touched_at", DateTimeOffset.UtcNow.ToString("O"));
                await touch.ExecuteNonQueryAsync();
                return new LongMaintenanceWriter(gate, conn);
            }
            catch
            {
                await conn.DisposeAsync();
                gate.Release();
                gate.Dispose();
                throw;
            }
        }

        public async Task ReleaseSqliteWriteLockAsync()
        {
            if (Interlocked.Exchange(ref _sqliteLockReleased, 1) != 0)
                return;

            using var rollback = _conn.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            try
            {
                if (Interlocked.Exchange(ref _sqliteLockReleased, 1) == 0)
                {
                    using var rollback = _conn.CreateCommand();
                    rollback.CommandText = "ROLLBACK;";
                    await rollback.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await _conn.DisposeAsync();
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
