// Run the large main suite single-threaded. Several fixtures fork
// git/dotnet/incus/multipass child processes whose own CPU demand stacks on top
// of the xUnit worker threads, and many scripted lifecycle tests arm real
// wall-clock deadlines (100-250 ms operation timeouts, flock release-on-close,
// background timers). Audit hosts are small (2 logical cores), so even a
// two-worker pool oversubscribed the box once forked children ran: the
// wall-clock-deadline tests were starved past their deadlines (spurious
// TimeoutExceptions and off-by-one command counts) and the testhost aborted
// mid-run under full-suite load. One worker removes that CPU contention so every
// test observes its real outcome deterministically; the suite trades wall-clock
// throughput for the determinism the timing- and subprocess-sensitive fixtures
// require (AGENTS.md §8). Prefer injected clocks over raising this back.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 1)]
