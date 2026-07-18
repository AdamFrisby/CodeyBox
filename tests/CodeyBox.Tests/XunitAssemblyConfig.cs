// Run the large main suite strictly sequentially — no cross-collection
// interleaving at all. `MaxParallelThreads = 1` was NOT enough: it caps the
// worker pool at one thread but leaves parallelization ENABLED, so xUnit still
// schedules multiple collections onto that single worker and hops between them
// at every `await`. That interleaving corrupts two kinds of fixtures:
//   * Global-static Serilog tests (the `GlobalSerilog` collection) set
//     `Log.Logger` in their constructor and assert on their own in-memory sink.
//     When such a test awaits (e.g. ResolveAsync), the worker starts another
//     collection's test whose constructor replaces `Log.Logger`; the first
//     test's audit event then lands in the wrong sink and its `Assert.Single`
//     sees an empty collection.
//   * Subprocess lifecycle tests fork git/dotnet/incus/multipass/bridge children
//     and arm real wall-clock deadlines (100-250 ms timeouts, flock
//     release-on-close, 15 s signal waits). Interleaved onto one worker on a
//     small 2-core audit host, the stacked child processes starve each other
//     into spurious timeouts and abort forked bridges (SIGABRT / exit 134).
// `DisableTestParallelization = true` removes the interleaving entirely: each
// test runs to completion — through its awaits — before the next begins, so
// every test observes its real outcome deterministically (AGENTS.md §8). The
// suite trades wall-clock throughput for that determinism; prefer injected
// clocks and per-test loggers over relaxing this.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
