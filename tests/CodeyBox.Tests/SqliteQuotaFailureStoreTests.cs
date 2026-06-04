using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteQuotaFailureStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-quota-failures-{Guid.NewGuid():N}.db");
    private readonly SqliteQuotaFailureStore _store;

    public SqliteQuotaFailureStoreTests() => _store = new SqliteQuotaFailureStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task PruneOlderThanAsync_ContinuesPastFirstBatch()
    {
        const int oldCount = 501;
        var now = DateTimeOffset.UtcNow;
        var oldObservedAt = now.AddDays(-120);

        for (var i = 0; i < oldCount; i++)
        {
            await _store.RecordAsync(
                AgentKind.Gemini,
                "old-model",
                QuotaFailureKind.LimitReached,
                oldObservedAt.AddMilliseconds(i));
        }

        await _store.RecordAsync(
            AgentKind.Gemini,
            "fresh-model",
            QuotaFailureKind.LimitReached,
            now);

        await _store.PruneOlderThanAsync(now.AddDays(-90));

        var remaining = await _store.ListRecentAsync(TimeSpan.FromDays(365), now.AddDays(1));
        var survivor = Assert.Single(remaining);
        Assert.Equal("fresh-model", survivor.ModelId);
        Assert.Equal(AgentKind.Gemini, survivor.Agent);
    }
}
