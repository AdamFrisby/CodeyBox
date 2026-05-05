using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests that SqliteQueueController correctly persists and loads per-project
/// queue state, and that pause/resume are idempotent.
/// </summary>
public sealed class IQueueControllerProjectScopeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-pqs-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private SqliteQueueController Make() =>
        new(_dbPath, NullLogger<SqliteQueueController>.Instance);

    private static readonly ProjectId ProjX = new("proj-x");
    private static readonly ProjectId ProjY = new("proj-y");

    // ── Initial state is null (running) ───────────────────────────────────────

    [Fact]
    public async Task GetProjectState_ReturnsNull_WhenNeverPaused()
    {
        using var ctrl = Make();
        var state = await ctrl.GetProjectStateAsync(ProjX);
        Assert.Null(state);
    }

    // ── Pause persists ────────────────────────────────────────────────────────

    [Fact]
    public async Task PauseProject_SetsPausedState()
    {
        using var ctrl = Make();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await ctrl.PauseProjectAsync(ProjX, "budget exceeded");

        var state = await ctrl.GetProjectStateAsync(ProjX);
        Assert.NotNull(state);
        Assert.True(state!.Paused);
        Assert.Equal("budget exceeded", state.PausedReason);
        Assert.NotNull(state.PausedAt);
        Assert.True(state.PausedAt >= before);
    }

    [Fact]
    public async Task PauseProject_PersistedAcrossRestart()
    {
        using (var ctrl = Make())
            await ctrl.PauseProjectAsync(ProjX, "budget exceeded");

        using var ctrl2 = Make();
        var state = await ctrl2.GetProjectStateAsync(ProjX);
        Assert.NotNull(state);
        Assert.True(state!.Paused);
        Assert.Equal("budget exceeded", state.PausedReason);
    }

    // ── Idempotent pause ──────────────────────────────────────────────────────

    [Fact]
    public async Task PauseProject_Idempotent_KeepsOriginalPausedAt()
    {
        using var ctrl = Make();
        await ctrl.PauseProjectAsync(ProjX, "first reason");
        var state1 = await ctrl.GetProjectStateAsync(ProjX);

        await ctrl.PauseProjectAsync(ProjX, "second reason"); // re-pause
        var state2 = await ctrl.GetProjectStateAsync(ProjX);

        // paused_at stays from the first pause (COALESCE)
        Assert.Equal(state1!.PausedAt, state2!.PausedAt);
        // reason updates
        Assert.Equal("second reason", state2.PausedReason);
    }

    // ── Resume clears state ───────────────────────────────────────────────────

    [Fact]
    public async Task ResumeProject_ClearsPausedState()
    {
        using var ctrl = Make();
        await ctrl.PauseProjectAsync(ProjX, "budget");
        await ctrl.ResumeProjectAsync(ProjX);

        var state = await ctrl.GetProjectStateAsync(ProjX);
        // Row exists but paused = false
        Assert.NotNull(state);
        Assert.False(state!.Paused);
        Assert.Null(state.PausedAt);
        Assert.Null(state.PausedReason);
    }

    [Fact]
    public async Task ResumeProject_PersistedAcrossRestart()
    {
        using (var ctrl = Make())
        {
            await ctrl.PauseProjectAsync(ProjX, "budget");
            await ctrl.ResumeProjectAsync(ProjX);
        }

        using var ctrl2 = Make();
        var state = await ctrl2.GetProjectStateAsync(ProjX);
        Assert.NotNull(state);
        Assert.False(state!.Paused);
    }

    // ── Projects are isolated ─────────────────────────────────────────────────

    [Fact]
    public async Task PauseProject_DoesNotAffectOtherProjects()
    {
        using var ctrl = Make();
        await ctrl.PauseProjectAsync(ProjX, "budget");

        var stateY = await ctrl.GetProjectStateAsync(ProjY);
        Assert.Null(stateY);
    }

    // ── Global pause is independent ───────────────────────────────────────────

    [Fact]
    public async Task GlobalPause_DoesNotAffectProjectState()
    {
        using var ctrl = Make();
        await ctrl.PauseAsync("global maintenance");

        var projState = await ctrl.GetProjectStateAsync(ProjX);
        Assert.Null(projState);
        Assert.Equal(QueueState.Paused, ctrl.State);
    }
}
