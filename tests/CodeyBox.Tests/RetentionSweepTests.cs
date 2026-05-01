using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the retention sweep logic via <see cref="IAuditReportStore.DeleteOlderThanAsync"/>.
/// The <see cref="AuditReportRetentionService"/> wiring is not tested here — it is a thin
/// wrapper over this method with a daily timer.
/// </summary>
public sealed class RetentionSweepTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-retention-{Guid.NewGuid():N}.db");
    private readonly SqliteAuditReportStore _store;

    public RetentionSweepTests() => _store = new SqliteAuditReportStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static AuditReport MakeAt(DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = "wi-retain",
        Iteration = 1,
        AuditorName = "Lint",
        AuditorKind = "diff-pattern",
        WorstSeverity = "none",
        StartedAt = startedAt,
        EndedAt = startedAt.AddSeconds(1),
        DurationMs = 1000,
        Findings = [],
        RawOutput = null,
    };

    [Fact]
    public async Task DeleteOlderThan_RemovesRows_OlderThanCutoff()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await _store.CreateAsync(MakeAt(cutoff.AddDays(-1)));
        await _store.CreateAsync(MakeAt(cutoff.AddDays(-10)));

        var deleted = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(2, deleted);
        var remaining = await _store.GetByWorkItemAsync("wi-retain");
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DeleteOlderThan_KeepsRows_AtOrAfterCutoff()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await _store.CreateAsync(MakeAt(cutoff));             // exactly at cutoff — NOT deleted
        await _store.CreateAsync(MakeAt(cutoff.AddHours(1))); // after cutoff — NOT deleted

        var deleted = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(0, deleted);
        var remaining = await _store.GetByWorkItemAsync("wi-retain");
        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public async Task DeleteOlderThan_MixedAges_OnlyDeletesOld()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var oldReport = MakeAt(cutoff.AddDays(-5));
        var freshReport = MakeAt(DateTimeOffset.UtcNow.AddDays(-3));
        await _store.CreateAsync(oldReport);
        await _store.CreateAsync(freshReport);

        var deleted = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, deleted);
        var remaining = await _store.GetByWorkItemAsync("wi-retain");
        Assert.Single(remaining);
        Assert.Equal(freshReport.Id, remaining[0].Id);
    }

    [Fact]
    public async Task DeleteOlderThan_EmptyStore_ReturnsZero()
    {
        var deleted = await _store.DeleteOlderThanAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task DeleteOlderThan_ReturnsDeletionCount()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        for (var i = 0; i < 5; i++)
            await _store.CreateAsync(MakeAt(cutoff.AddDays(-i - 1)));

        var deleted = await _store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(5, deleted);
    }
}
