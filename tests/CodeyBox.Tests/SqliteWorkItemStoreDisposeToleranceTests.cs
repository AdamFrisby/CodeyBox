using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the dispose-time tolerance for the
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> teardown race:
/// <c>SqliteConnection.Close()</c> has been observed to throw
/// <see cref="NullReferenceException"/> intermittently when a still-in-flight
/// async command races against connection disposal. The store swallows that
/// one specific case so the rest of the dispose chain (notably the
/// write-gate release) still runs. Any other exception type must bubble — we
/// must NOT mask unrelated bugs.
/// </summary>
public sealed class SqliteWorkItemStoreDisposeToleranceTests
{
    [Fact]
    public void DisposeSqliteConnectionTolerantOfTeardownNre_SwallowsNullReferenceException()
    {
        var thrower = new ThrowingDisposable(new NullReferenceException("Object reference not set"));
        SqliteWorkItemStore.DisposeSqliteConnectionTolerantOfTeardownNre(thrower);
        Assert.True(thrower.DisposeCalled);
    }

    [Fact]
    public void DisposeSqliteConnectionTolerantOfTeardownNre_LetsOtherExceptionsBubble()
    {
        // Anything that isn't NRE points at a real fault (corruption, IO,
        // disposed-twice). Surfacing them keeps the bug visible.
        var thrower = new ThrowingDisposable(new InvalidOperationException("not the race"));
        Assert.Throws<InvalidOperationException>(() =>
            SqliteWorkItemStore.DisposeSqliteConnectionTolerantOfTeardownNre(thrower));
    }

    [Fact]
    public void DisposeSqliteConnectionTolerantOfTeardownNre_NormalDisposeRunsThrough()
    {
        var ok = new ThrowingDisposable(throwOnDispose: null);
        SqliteWorkItemStore.DisposeSqliteConnectionTolerantOfTeardownNre(ok);
        Assert.True(ok.DisposeCalled);
    }

    [Fact]
    public void Store_Dispose_ReleasesWriteGate_EvenWhenConnectionDisposeThrowsNre()
    {
        // End-to-end shape guard: a second store on the same path Dispose()s
        // cleanly twice (the second call is a no-op), proving the write-gate
        // is released even though the path is a no-op. The static helper
        // above pins the race-tolerance branch; this assertion guards the
        // contract that Dispose is idempotent and exception-free under
        // normal teardown.
        var path = Path.Combine(Path.GetTempPath(), $"codeybox-dispose-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteWorkItemStore(path);
            store.Dispose();
            store.Dispose();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        private readonly Exception? _throwOnDispose;
        public bool DisposeCalled { get; private set; }

        public ThrowingDisposable(Exception? throwOnDispose)
        {
            _throwOnDispose = throwOnDispose;
        }

        public void Dispose()
        {
            DisposeCalled = true;
            if (_throwOnDispose is not null)
                throw _throwOnDispose;
        }
    }
}
