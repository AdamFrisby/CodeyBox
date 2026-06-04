using System.Net;
using System.Net.Http.Json;
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

        var timedItem = MakeWorkItem("timed");
        await factory.Store.CreateAsync(timedItem);

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

        using var gate = SqliteDatabaseWriteGate.ForPath(_dbPath);
        gate.Wait();

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

        try
        {
            await Task.Delay(100);
        }
        finally
        {
            gate.Release();
        }

        var responses = await Task.WhenAll(postTasks).WaitAsync(TimeSpan.FromSeconds(15));
        await Task.WhenAll(heartbeatTasks.Concat(timingTasks)).WaitAsync(TimeSpan.FromSeconds(15));
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
}
