// Run the large main suite strictly one test at a time. Two independent load
// pathologies drove a recurring "different unrelated fixture fails each run"
// flake on the small (2 logical core) audit hosts, and both are killed only by
// full serialization:
//
//   1. CPU oversubscription. Several fixtures fork git/dotnet/incus/multipass/
//      bridge child processes and arm real wall-clock deadlines (100-250 ms
//      timeouts, flock release-on-close, 15 s signal waits). Their CPU demand
//      stacks on top of the xUnit worker threads, so any worker pool wider than
//      one already oversubscribes a 2-core box the moment a fixture forks --
//      the stacked children starve each other into spurious timeouts and abort
//      forked bridges (SIGABRT / exit 134). Capping the pool at one worker
//      addressed that.
//
//   2. Async interleaving. Capping the pool is NOT enough on its own: while
//      parallelization stays enabled xUnit installs a MaxConcurrencySyncContext
//      that keeps *interleaving* the awaiting continuations of many collections
//      onto that single worker. So dozens of async fixtures are live at once,
//      and whenever one blocks on a real-time bound (a WaitAsync ceiling, a
//      background-timer deadline) the worker is off running another test's
//      continuation instead of the one whose clock is ticking -- turning a
//      scheduling latency into a spurious timeout, and multiplying the live-
//      object set so GC pauses land unpredictably. That interleaving also
//      corrupts global-static fixtures: Serilog tests (the `GlobalSerilog`
//      collection) set `Log.Logger` in their constructor and assert on their
//      own in-memory sink, but when such a test awaits (e.g. ResolveAsync) the
//      worker starts another collection's test whose constructor replaces
//      `Log.Logger`; the first test's audit event then lands in the wrong sink
//      and its `Assert.Single` sees an empty collection. That interleaving is
//      exactly what earlier de-flakes had to work around fixture by fixture.
//
// DisableTestParallelization removes the sync context entirely: each test (and
// all of its continuations) runs to completion -- through its awaits -- before
// the next one starts, so every fixture observes its own un-starved,
// un-interleaved timing deterministically (AGENTS.md §8). Individual test
// latency only improves under this (no contention); the cost is total
// wall-clock, which the suite already accepted as the price of a non-flaky run
// on a two-core host. Prefer injected clocks and per-test loggers over relaxing
// this. MaxParallelThreads is kept at 1 for defence in depth (it is moot once
// parallelization is off).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
