using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests that SqliteQueueController correctly persists and loads queue state.
/// </summary>
public sealed class QueueControllerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-qc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private SqliteQueueController Make() => new(_dbPath, NullLogger<SqliteQueueController>.Instance);

    [Fact]
    public void InitialState_IsRunning()
    {
        using var ctrl = Make();
        Assert.Equal(QueueState.Running, ctrl.State);
        Assert.Null(ctrl.PausedAt);
        Assert.Null(ctrl.PausedReason);
    }

    [Fact]
    public async Task PauseAsync_SetsPausedState()
    {
        using var ctrl = Make();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await ctrl.PauseAsync("incident-1234");

        Assert.Equal(QueueState.Paused, ctrl.State);
        Assert.Equal("incident-1234", ctrl.PausedReason);
        Assert.NotNull(ctrl.PausedAt);
        Assert.True(ctrl.PausedAt >= before);
    }

    [Fact]
    public async Task ResumeAsync_ClearsState()
    {
        using var ctrl = Make();
        await ctrl.PauseAsync("testing");

        await ctrl.ResumeAsync();

        Assert.Equal(QueueState.Running, ctrl.State);
        Assert.Null(ctrl.PausedAt);
        Assert.Null(ctrl.PausedReason);
    }

    [Fact]
    public async Task PausedState_PersistedAcrossRestart()
    {
        using (var ctrl = Make())
            await ctrl.PauseAsync("maintenance window");

        // Simulate restart by constructing a new controller on the same DB.
        using var ctrl2 = Make();
        Assert.Equal(QueueState.Paused, ctrl2.State);
        Assert.Equal("maintenance window", ctrl2.PausedReason);
        Assert.NotNull(ctrl2.PausedAt);
    }

    [Fact]
    public async Task ResumedState_PersistedAcrossRestart()
    {
        using (var ctrl = Make())
        {
            await ctrl.PauseAsync("paused");
            await ctrl.ResumeAsync();
        }

        using var ctrl2 = Make();
        Assert.Equal(QueueState.Running, ctrl2.State);
        Assert.Null(ctrl2.PausedAt);
    }

    [Fact]
    public async Task MultipleControllers_SameDb_SeeConsistentState()
    {
        // A controller that pauses the queue writes to SQLite. A NEW controller
        // opened on the same DB path afterwards reads the persisted state, so
        // cross-instance consistency is guaranteed via the database row.
        using var ctrl1 = Make();
        await ctrl1.PauseAsync("from-ctrl1");
        Assert.Equal(QueueState.Paused, ctrl1.State);

        // New controller on the same path loads from SQLite on construction.
        using var ctrl2 = Make();
        Assert.Equal(QueueState.Paused, ctrl2.State);
        Assert.Equal("from-ctrl1", ctrl2.PausedReason);
    }
}
