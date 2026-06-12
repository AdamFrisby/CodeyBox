using System.Reflection;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteReleaseStoreDisposeTests
{
    [Fact]
    public async Task Dispose_IsIdempotentAndReleasesWriteGate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codeybox-release-dispose-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteReleaseStore(path);
            Assert.True(WriteGateEntryExists(path));

            store.Dispose();
            store.Dispose();

            Assert.False(WriteGateEntryExists(path));

            using var reopened = await Task.Run(() => new SqliteReleaseStore(path))
                .WaitAsync(TimeSpan.FromSeconds(10));

            var release = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
            await reopened.CreateAsync(release);

            var fetched = await reopened.GetAsync(release.Id);
            Assert.NotNull(fetched);
            Assert.Equal(release.Id, fetched!.Id);
        }
        finally
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                try { File.Delete(candidate); } catch { }
            }
        }
    }

    private static bool WriteGateEntryExists(string path)
    {
        var field = typeof(SqliteDatabaseWriteGate).GetField(
            "Entries",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var entries = field.GetValue(null)!;
        var containsKey = entries.GetType().GetMethod("ContainsKey", [typeof(string)]);
        Assert.NotNull(containsKey);
        return (bool)containsKey.Invoke(entries, [Path.GetFullPath(path)])!;
    }
}
