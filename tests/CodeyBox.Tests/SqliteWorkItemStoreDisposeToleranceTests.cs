using CodeyBox.Core;
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
    public async Task Store_Dispose_IsIdempotentAndReleasesWriteGate_AcrossReopen()
    {
        // End-to-end shape guard for the load-bearing Dispose() contract:
        //   1. Disposing twice is exception-free (idempotency).
        //   2. After Dispose the write gate is released so a SECOND store on
        //      the same path can be constructed, take the write gate, and
        //      complete a write. If Dispose left the gate held, the second
        //      store's constructor (which acquires the gate before running
        //      migrations) would hang forever — guarded here by a hard
        //      timeout that fails fast.
        var path = Path.Combine(Path.GetTempPath(), $"codeybox-dispose-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteWorkItemStore(path);
            store.Dispose();
            store.Dispose(); // idempotent — no throw, no double-release.

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var reopened = await Task.Run(() => new SqliteWorkItemStore(path), cts.Token);
            Assert.NotNull(reopened);

            // Exercise the write gate the reopened store now owns: a CREATE
            // through CreateAsync takes the gate via WaitAsync/Release, so a
            // leaked gate from the first store would have stranded the
            // semaphore at zero and this call would hang.
            var item = new WorkItem
            {
                Id = WorkItemId.New(),
                ProjectId = new ProjectId("dispose-tolerance-test"),
                Title = "after-reopen",
                Prompt = "p",
                State = WorkItemState.Queued,
            };
            await reopened.CreateAsync(item, cts.Token);
            var fetched = await reopened.GetAsync(item.Id, cts.Token);
            Assert.NotNull(fetched);
            Assert.Equal(item.Id, fetched!.Id);
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
