using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// The store's short-lived reader connections register per-connection scalar
/// functions, so disposing one while an in-flight statement still references
/// those functions intermittently throws <c>SQLITE_BUSY</c> out of
/// <c>SqliteConnection.Deactivate</c> — observed as
/// <c>WorkItemPriorityTests.Dispatch_PicksHigherPriorityFirst</c> failing
/// inside a polling <c>GetAsync</c> racing a live orchestrator. The race
/// itself is a driver-level timing window with no deterministic trigger, so
/// this guards its fix instead: every connection from the read seam must be
/// a teardown-tolerant one, and a revert to a bare
/// <c>SqliteConnection</c> fails this test.
/// </summary>
public sealed class SqliteReadConnectionResilienceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-readconn-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SqliteReadConnectionResilienceTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task OpenReadConnectionAsync_ReturnsTeardownTolerantConnection()
    {
        using var conn = await _store.OpenReadConnectionAsync(CancellationToken.None);
        Assert.IsType<SqliteWorkItemStore.TolerantReadConnection>(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1;";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }
}
