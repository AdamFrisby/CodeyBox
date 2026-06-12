using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the dispose-time tolerance for the shared
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> teardown helper:
/// <c>SqliteConnection.Close()</c> has been observed to throw both
/// <see cref="NullReferenceException"/> and the
/// <see cref="InvalidOperationException"/> "Collection was modified" shape from
/// <c>SqliteCommand.DisposePreparedStatements</c> when a still-in-flight async
/// command races against connection disposal. Stores swallow those two
/// driver-internal cases so the rest of the dispose chain can still run.
/// Any other exception type, including <see cref="InvalidOperationException"/>
/// that does not originate inside the SQLite driver, must bubble so unrelated
/// bugs stay visible.
/// </summary>
public sealed class SqliteConnectionDisposalTests
{
    [Fact]
    public void DisposeTolerantOfTeardownRace_SwallowsNullReferenceException()
    {
        var thrower = new ThrowingDisposable(new NullReferenceException("Object reference not set"));
        SqliteConnectionDisposal.DisposeTolerantOfTeardownRace(thrower);
        Assert.True(thrower.DisposeCalled);
    }

    [Fact]
    public void DisposeTolerantOfTeardownRace_SwallowsSqliteCollectionModifiedRace()
    {
        // The driver's SqliteCommand.DisposePreparedStatements iterates an
        // internal List<> of prepared statements; an in-flight finalize that
        // mutates the list mid-iteration throws InvalidOperationException
        // "Collection was modified". Stack-trace inspection picks the driver
        // frame; same race shape as the NRE, same tolerance.
        var ioe = MakeInvalidOperationFromSqliteTeardown();
        var thrower = new ThrowingDisposable(ioe);
        SqliteConnectionDisposal.DisposeTolerantOfTeardownRace(thrower);
        Assert.True(thrower.DisposeCalled);
    }

    [Fact]
    public void DisposeTolerantOfTeardownRace_LetsOtherExceptionsBubble()
    {
        // Anything that isn't a SQLite-driver teardown race points at a real
        // fault (corruption, IO, disposed-twice). Surfacing them keeps the
        // bug visible — including InvalidOperationException raised from
        // outside Microsoft.Data.Sqlite.
        var thrower = new ThrowingDisposable(new InvalidOperationException("not the race"));
        Assert.Throws<InvalidOperationException>(() =>
            SqliteConnectionDisposal.DisposeTolerantOfTeardownRace(thrower));
    }

    /// <summary>
    /// Forges an <see cref="InvalidOperationException"/> whose
    /// <see cref="Exception.StackTrace"/> contains a
    /// <c>Microsoft.Data.Sqlite.SqliteCommand</c> frame so the tolerance
    /// branch's stack-trace check matches it. We can't construct a real
    /// driver-internal race deterministically from a test; the stack-trace
    /// gate is the seam the tolerance method exposes.
    /// </summary>
    private static InvalidOperationException MakeInvalidOperationFromSqliteTeardown()
    {
        try
        {
            // The reflection cast forces an IOE whose stack includes a frame
            // matching the driver namespace literal we filter on.
            throw new MicrosoftDataSqliteSyntheticFrame().ThrowCollectionModified();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private sealed class MicrosoftDataSqliteSyntheticFrame
    {
        // The full class name is irrelevant; only the runtime-formatted stack
        // string is inspected, and the driver-namespace literal must appear
        // somewhere in the trace. We synthesize that by setting an explicit
        // remote stack trace prefix that mimics the real driver frame.
        public InvalidOperationException ThrowCollectionModified()
        {
            var ioe = new InvalidOperationException(
                "Collection was modified; enumeration operation may not execute.");
            ExceptionDispatchInfoSetRemoteStackTrace(
                ioe,
                "   at System.Collections.Generic.List`1.Enumerator.MoveNext()\n" +
                "   at Microsoft.Data.Sqlite.SqliteCommand.DisposePreparedStatements(Boolean disposing)\n" +
                "   at Microsoft.Data.Sqlite.SqliteCommand.Dispose(Boolean disposing)\n");
            throw ioe;
        }

        private static void ExceptionDispatchInfoSetRemoteStackTrace(Exception ex, string trace)
        {
            // .NET 10 still exposes Exception.SetRemoteStackTrace; older
            // overloads expected (string trace) which is what we use here.
            ex.GetType().GetMethod(
                    "SetRemoteStackTrace",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                    [typeof(string)])!
                .Invoke(ex, [trace]);
        }
    }

    [Fact]
    public void DisposeTolerantOfTeardownRace_NormalDisposeRunsThrough()
    {
        var ok = new ThrowingDisposable(throwOnDispose: null);
        SqliteConnectionDisposal.DisposeTolerantOfTeardownRace(ok);
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
