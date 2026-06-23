using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class SqliteStoreDisposeTests
{
    [Fact]
    public void WorkItemStore_Dispose_CanBeCalledTwice()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-workitems-dispose-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteWorkItemStore(dbPath);

            store.Dispose();
            var ex = Record.Exception(store.Dispose);

            Assert.Null(ex);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void TestCaseStore_Dispose_CanBeCalledTwice()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-testcases-dispose-{Guid.NewGuid():N}.db");
        try
        {
            // The work-items table must exist before the test-case store FK declaration takes
            // effect at INSERT time. Constructing the work-item store first creates it.
            using var itemStore = new SqliteWorkItemStore(dbPath);
            var store = new SqliteTestCaseStore(dbPath);

            store.Dispose();
            var ex = Record.Exception(store.Dispose);

            Assert.Null(ex);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void WorkerRegistry_Dispose_CanBeCalledTwice()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-workers-dispose-{Guid.NewGuid():N}.db");
        try
        {
            var registry = new SqliteWorkerRegistry(dbPath);

            registry.Dispose();
            var ex = Record.Exception(registry.Dispose);

            Assert.Null(ex);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }
}
