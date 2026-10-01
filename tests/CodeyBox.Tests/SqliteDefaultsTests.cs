using Microsoft.Data.Sqlite;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteDefaultsTests : IDisposable
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-sqlite-defaults-");
    private readonly string _dbPath;

    public SqliteDefaultsTests()
    {
        _dbPath = _scratch.DbPath("defaults.db");
    }

    public void Dispose()
    {
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
        _scratch.Dispose();
    }

    [Fact]
    public void IsLockContention_RecognizesBusyAndLockedIncludingInnerExceptions()
    {
        Assert.True(SqliteDefaults.IsLockContention(
            new SqliteException("busy", errorCode: SqliteDefaults.SqliteBusy)));
        Assert.True(SqliteDefaults.IsLockContention(
            new SqliteException("locked", errorCode: SqliteDefaults.SqliteLocked)));
        Assert.True(SqliteDefaults.IsLockContention(
            new InvalidOperationException(
                "wrapped",
                new SqliteException("busy", errorCode: SqliteDefaults.SqliteBusy))));
        Assert.False(SqliteDefaults.IsLockContention(
            new SqliteException("corrupt", errorCode: 11)));
        Assert.False(SqliteDefaults.IsLockContention(new InvalidOperationException("no sqlite")));
    }

    [Fact]
    public async Task BeginDeferredTransaction_DoesNotTakeMainDatabaseReservedLock()
    {
        await using var writer = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await writer.OpenAsync();
        using (var wal = writer.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=0;";
            await wal.ExecuteNonQueryAsync();
        }

        using (var create = writer.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS lock_probe (id INTEGER PRIMARY KEY);";
            await create.ExecuteNonQueryAsync();
        }

        using var reserved = writer.BeginTransaction();

        await using var reader = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await reader.OpenAsync();
        using (var busy = reader.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=0;";
            await busy.ExecuteNonQueryAsync();
        }

        using var deferred = SqliteDefaults.BeginDeferredTransaction(reader);
        using (var createTemp = reader.CreateCommand())
        {
            createTemp.Transaction = deferred;
            createTemp.CommandText = """
                CREATE TEMP TABLE codeybox_dispatch_skip_ids (
                    id TEXT PRIMARY KEY
                ) WITHOUT ROWID;
                """;
            await createTemp.ExecuteNonQueryAsync();
        }

        using (var insert = reader.CreateCommand())
        {
            insert.Transaction = deferred;
            insert.CommandText = "INSERT INTO temp.codeybox_dispatch_skip_ids (id) VALUES ($id);";
            insert.Parameters.AddWithValue("$id", "skip-1");
            await insert.ExecuteNonQueryAsync();
        }

        using (var countCmd = reader.CreateCommand())
        {
            countCmd.Transaction = deferred;
            countCmd.CommandText = "SELECT COUNT(*) FROM temp.codeybox_dispatch_skip_ids;";
            Assert.Equal(1, Convert.ToInt32(await countCmd.ExecuteScalarAsync()));
        }

        using (var idCmd = reader.CreateCommand())
        {
            idCmd.Transaction = deferred;
            idCmd.CommandText = "SELECT id FROM temp.codeybox_dispatch_skip_ids;";
            Assert.Equal("skip-1", await idCmd.ExecuteScalarAsync());
        }

        deferred.Commit();
    }

    [Fact]
    public async Task ParameterlessBeginTransaction_TakesReservedLockAndConflictsWithImmediateWriter()
    {
        await using var writer = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await writer.OpenAsync();
        using (var wal = writer.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=0;";
            await wal.ExecuteNonQueryAsync();
        }

        using var reserved = writer.BeginTransaction();

        await using var other = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await other.OpenAsync();
        using (var busy = other.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=0;";
            await busy.ExecuteNonQueryAsync();
        }

        var ex = Assert.Throws<SqliteException>(() => other.BeginTransaction());
        Assert.Equal(SqliteDefaults.SqliteBusy, ex.SqliteErrorCode);
    }
}
