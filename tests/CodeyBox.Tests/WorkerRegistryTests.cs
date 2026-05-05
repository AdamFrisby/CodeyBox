using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="SqliteWorkerRegistry"/>: register, heartbeat,
/// deregister, list, and ClaimDeadWorkers.
/// </summary>
public sealed class WorkerRegistryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-regtest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkerRegistry _registry;

    public WorkerRegistryTests() => _registry = new SqliteWorkerRegistry(_dbPath);

    public void Dispose()
    {
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkerRegistration MakeReg(string? workItemId = null) => new()
    {
        WorkerId = Guid.NewGuid().ToString(),
        HostName = "test-host",
        ProcessId = 1234,
        StartedAt = DateTimeOffset.UtcNow,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        CurrentWorkItemId = workItemId,
    };

    [Fact]
    public async Task Register_ThenList_ReturnsRow()
    {
        var reg = MakeReg();
        await _registry.RegisterAsync(reg);

        var list = await _registry.ListAsync();

        var found = Assert.Single(list);
        Assert.Equal(reg.WorkerId, found.WorkerId);
        Assert.Equal(reg.HostName, found.HostName);
        Assert.Equal(reg.ProcessId, found.ProcessId);
        Assert.Null(found.CurrentWorkItemId);
    }

    [Fact]
    public async Task Register_WithWorkItemId_Persisted()
    {
        var itemId = Guid.NewGuid().ToString();
        var reg = MakeReg(itemId);
        await _registry.RegisterAsync(reg);

        var list = await _registry.ListAsync();
        var found = Assert.Single(list);
        Assert.Equal(itemId, found.CurrentWorkItemId);
    }

    [Fact]
    public async Task Heartbeat_UpdatesTimestampAndWorkItemId()
    {
        var reg = MakeReg();
        await _registry.RegisterAsync(reg);

        var newItemId = Guid.NewGuid().ToString();
        var before = DateTimeOffset.UtcNow;
        await Task.Delay(5); // ensure measurable time gap
        await _registry.HeartbeatAsync(reg.WorkerId, newItemId);

        var found = Assert.Single(await _registry.ListAsync());
        Assert.True(found.LastHeartbeatAt >= before, "LastHeartbeatAt should have advanced");
        Assert.Equal(newItemId, found.CurrentWorkItemId);
    }

    [Fact]
    public async Task Heartbeat_ClearsWorkItemId_WhenNull()
    {
        var reg = MakeReg(Guid.NewGuid().ToString());
        await _registry.RegisterAsync(reg);

        await _registry.HeartbeatAsync(reg.WorkerId, null);

        var found = Assert.Single(await _registry.ListAsync());
        Assert.Null(found.CurrentWorkItemId);
    }

    [Fact]
    public async Task Deregister_RemovesRow()
    {
        var reg = MakeReg();
        await _registry.RegisterAsync(reg);

        await _registry.DeregisterAsync(reg.WorkerId);

        Assert.Empty(await _registry.ListAsync());
    }

    [Fact]
    public async Task Deregister_NonExistentWorker_IsNoOp()
    {
        // Should not throw.
        await _registry.DeregisterAsync(Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task ClaimDeadWorkers_ReturnsStaledRows_AndDeletesThem()
    {
        var staleReg = new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CurrentWorkItemId = Guid.NewGuid().ToString(),
        };
        var freshReg = MakeReg();

        await _registry.RegisterAsync(staleReg);
        await _registry.RegisterAsync(freshReg);

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var claimed = await _registry.ClaimDeadWorkersAsync(cutoff);

        var dead = Assert.Single(claimed);
        Assert.Equal(staleReg.WorkerId, dead.WorkerId);

        // Stale row should be gone; fresh row should remain.
        var remaining = await _registry.ListAsync();
        var survivor = Assert.Single(remaining);
        Assert.Equal(freshReg.WorkerId, survivor.WorkerId);
    }

    [Fact]
    public async Task ClaimDeadWorkers_Idempotent_SecondCallReturnsEmpty()
    {
        var staleReg = new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        await _registry.RegisterAsync(staleReg);

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = await _registry.ClaimDeadWorkersAsync(cutoff);
        var second = await _registry.ClaimDeadWorkersAsync(cutoff);

        Assert.Single(first);
        Assert.Empty(second); // already deleted
    }

    [Fact]
    public async Task ClaimDeadWorkers_NoStaleRows_ReturnsEmpty()
    {
        await _registry.RegisterAsync(MakeReg());

        var claimed = await _registry.ClaimDeadWorkersAsync(DateTimeOffset.UtcNow.AddMinutes(-99999));
        Assert.Empty(claimed);
    }
}
